// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Dispatching;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// The library: songs, albums, artists and playlists (coremodel.py together with
/// the LocalSearch wrappers). Songs come from <see cref="LibraryScanner"/>, which
/// is fed from a JSON cache first so the library shows up instantly.
/// </summary>
public sealed partial class CoreModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly LibraryScanner _scanner = new();
    private readonly Dictionary<string, CoreSong> _songsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly DispatcherQueueTimer _rescanTimer;
    private List<CoreSong> _songs = new();
    private List<CoreAlbum> _masterAlbums = new();
    private Dictionary<CoreArtist, List<CoreSong>> _artistSongs = new();
    private ObservableCollection<CoreAlbum> _albums = new();
    private ObservableCollection<CoreArtist> _artists = new();
    private CancellationTokenSource? _scanCancellation;
    private bool _isScanning;
    private bool _isLoaded;
    private bool _cacheDirty;
    private bool _rescanPending;

    public CoreModel(AppServices services)
    {
        _services = services;
        _rescanTimer = services.Dispatcher.CreateTimer();
        _rescanTimer.Interval = TimeSpan.FromSeconds(2);
        _rescanTimer.IsRepeating = false;
        _rescanTimer.Tick += (_, _) => Rescan();

        CreateSystemPlaylists();
    }

    /// <summary>Raised on the UI thread after the song/album/artist lists changed.</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>All songs, sorted naturally by title, then artist, then album.</summary>
    public IReadOnlyList<CoreSong> Songs => _songs;

    /// <summary>Albums as shown by the albums view (sorted by title).</summary>
    public ObservableCollection<CoreAlbum> Albums
    {
        get => _albums;
        private set => SetProperty(ref _albums, value);
    }

    /// <summary>Artists as shown by the artists view (sorted by name).</summary>
    public ObservableCollection<CoreArtist> Artists
    {
        get => _artists;
        private set => SetProperty(ref _artists, value);
    }

    /// <summary>True while the music folders are being scanned (the loading indicator).</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    /// <summary>True once songs were loaded from the cache or a first scan finished.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set => SetProperty(ref _isLoaded, value);
    }

    /// <summary>coremodel.py "songs-available".</summary>
    public bool SongsAvailable => _songs.Count > 0;

    /// <summary>Loads the cached library, then scans the music folders.</summary>
    public async void Start()
    {
        var cache = await Task.Run(() => JsonStorage.Load(AppPaths.LibraryCacheFile, AppJsonContext.Default.LibraryCache));
        if (cache is { Version: LibraryScanner.CacheVersion } && cache.Songs.Count > 0)
        {
            ApplyRecords(cache.Songs);
            IsLoaded = true;
        }

        LoadUserPlaylists();
        LoadSystemPlaylists();
        WatchFolders();
        Rescan();
    }

    /// <summary>Scans the music folders again.</summary>
    public async void Rescan()
    {
        if (IsScanning)
        {
            _rescanPending = true;
            return;
        }

        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        IsScanning = true;
        _rescanPending = false;

        try
        {
            var folders = _services.Settings.LibraryFolders.ToList();
            var cache = _songs.ToDictionary(s => s.FilePath, s => s.Record, StringComparer.OrdinalIgnoreCase);
            var result = await _scanner.ScanAsync(folders, cache, snapshot => _services.Dispatcher.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested)
                    ApplyRecords(snapshot);
            }), token);
            if (token.IsCancellationRequested)
                return;

            if (result.Changed || !IsLoaded)
            {
                ApplyRecords(result.Songs);
                _cacheDirty = true;
                SaveCache(background: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Scan failed", ex);
        }
        finally
        {
            IsScanning = false;
            IsLoaded = true;
        }

        if (_rescanPending)
            Rescan();
    }

    /// <summary>Restarts folder watching and scanning after the folder list changed.</summary>
    public void OnLibraryFoldersChanged()
    {
        _scanCancellation?.Cancel();
        WatchFolders();
        _rescanPending = true;
        if (!IsScanning)
            Rescan();
    }

    public void FlushCache()
    {
        if (_cacheDirty)
            SaveCache(background: false);
    }

    public void Dispose()
    {
        _scanCancellation?.Cancel();
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }

    public CoreSong? FindSong(string path) => _songsById.TryGetValue(path, out var song) ? song : null;

    // ------------------------------------------------------------------
    // Building the model (LocalSearch semantics, see §2 of the backend spec)
    // ------------------------------------------------------------------

    private void ApplyRecords(IReadOnlyCollection<SongRecord> records)
    {
        var stopwatch = Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var songs = new List<CoreSong>(records.Count);
        bool changed = !IsLoaded;

        foreach (var record in records)
        {
            if (!seen.Add(record.Path))
                continue;

            if (_songsById.TryGetValue(record.Path, out var existing))
            {
                if (!ReferenceEquals(existing.Record, record))
                    changed |= existing.UpdateRecord(record);
                songs.Add(existing);
            }
            else
            {
                var userData = _services.UserData.GetOrCreate(record.Path, new DateTime(record.Created, DateTimeKind.Utc));
                var song = new CoreSong(record, userData);
                _songsById[record.Path] = song;
                songs.Add(song);
                changed = true;
            }
        }

        foreach (var removed in _songsById.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _songsById.Remove(removed);
            changed = true;
        }

        if (!changed && songs.Count == _songs.Count)
            return;

        bool wasAvailable = SongsAvailable;

        // songs: natural sort by title, then artist, then album (coremodel.py _songs_sort).
        songs.Sort((a, b) =>
        {
            int c = a.TitleKey.CompareTo(b.TitleKey);
            if (c == 0)
                c = a.ArtistKey.CompareTo(b.ArtistKey);
            if (c == 0)
                c = a.AlbumKey.CompareTo(b.AlbumKey);
            return c;
        });
        _songs = songs;
        _searchFields = new();

        BuildAlbumsAndArtists();
        ResolveUserPlaylists();
        ResolveSystemPlaylists();
        OnPropertyChanged(nameof(Songs));
        if (wasAvailable != SongsAvailable)
            OnPropertyChanged(nameof(SongsAvailable));
        LibraryChanged?.Invoke(this, EventArgs.Empty);
        Log.Info($"Library: {songs.Count} songs, {Albums.Count} albums, {Artists.Count} artists (built in {stopwatch.ElapsedMilliseconds} ms)");
    }

    private void BuildAlbumsAndArtists()
    {
        // Albums: songs with an album title, keyed by title + album artist tag +
        // date. An album is listed if one of its songs also has a track artist.
        var groups = new Dictionary<string, List<CoreSong>>(StringComparer.Ordinal);
        foreach (var song in _songs)
        {
            song.Album = null;
            if (!song.HasAlbum)
                continue;

            var key = AlbumKey(song);
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new List<CoreSong>();
            list.Add(song);
        }

        var albums = new List<CoreAlbum>();
        foreach (var (key, list) in groups)
        {
            if (!list.Any(s => s.HasArtist))
                continue;

            var first = list[0];
            var album = new CoreAlbum(key, first.AlbumTitle, first.AlbumArtist, list);
            foreach (var song in list)
                song.Album = album;
            albums.Add(album);
        }

        // Master order (albums.rq): ORDER BY title, album artist, artist, date.
        albums = albums
            .OrderBy(a => a.Title, Utils.CollationComparer)
            .ThenBy(a => a.AlbumArtist ?? "", Utils.CollationComparer)
            .ThenBy(a => a.TrackArtist ?? "", Utils.CollationComparer)
            .ThenBy(a => a.Year)
            .ToList();
        _masterAlbums = albums;

        // Artists (artists.rq): COALESCE(album artist, track artist) of songs that
        // have an album and a track artist; one artist per distinct name.
        var artistNames = new Dictionary<string, List<CoreSong>>(StringComparer.Ordinal);
        foreach (var song in _songs)
        {
            if (song.Album is null || !song.HasArtist)
                continue;

            var name = song.Album.AlbumArtist ?? song.RawArtist!;
            if (!artistNames.TryGetValue(name, out var list))
                artistNames[name] = list = new List<CoreSong>();
            list.Add(song);
        }

        // artist_albums.rq: albums with a song by the artist, or with the artist
        // as album artist.
        var albumsByName = new Dictionary<string, HashSet<CoreAlbum>>(StringComparer.Ordinal);
        void Link(string name, CoreAlbum album)
        {
            if (!albumsByName.TryGetValue(name, out var set))
                albumsByName[name] = set = new HashSet<CoreAlbum>();
            set.Add(album);
        }

        foreach (var album in albums)
        {
            if (album.AlbumArtist is { } albumArtist)
                Link(albumArtist, album);
            foreach (var song in album.Songs)
            {
                if (song.RawArtist is { } trackArtist)
                    Link(trackArtist, album);
            }
        }

        var masterIndex = new Dictionary<CoreAlbum, int>();
        for (int i = 0; i < albums.Count; i++)
            masterIndex[albums[i]] = i;

        var artistSongs = new Dictionary<CoreArtist, List<CoreSong>>();
        var artists = new List<CoreArtist>();
        foreach (var (name, contributing) in artistNames)
        {
            // Sorted by year, undated albums last; ties keep the master order.
            var artistAlbums = albumsByName[name]
                .OrderBy(a => a.Year == 0 ? 1 : 0)
                .ThenBy(a => a.Year)
                .ThenBy(a => masterIndex[a])
                .ToList();
            var artist = new CoreArtist(name, artistAlbums);
            artists.Add(artist);
            artistSongs[artist] = contributing;
        }

        artists = artists.OrderBy(a => a.Name, Utils.CollationComparer).ToList();
        _artistSongs = artistSongs;

        Albums = new ObservableCollection<CoreAlbum>(albums.OrderBy(a => a.Title, Utils.CollationComparer));
        Artists = new ObservableCollection<CoreArtist>(artists);
    }

    private static string AlbumKey(CoreSong song)
    {
        var key = "urn:album:" + song.AlbumTitle;
        if (song.AlbumArtist is { } albumArtist)
            key += ":" + albumArtist;
        if (song.Year > 0)
            key += ":" + song.Year;
        return key;
    }

    private void SaveCache(bool background)
    {
        var snapshot = new LibraryCache { Songs = _songs.Select(s => s.Record).ToList() };
        _cacheDirty = false;
        if (background)
            _ = Task.Run(() => JsonStorage.Save(AppPaths.LibraryCacheFile, snapshot, AppJsonContext.Default.LibraryCache));
        else
            JsonStorage.Save(AppPaths.LibraryCacheFile, snapshot, AppJsonContext.Default.LibraryCache);
    }

    // ------------------------------------------------------------------
    // Folder monitoring (LocalSearch's miner does this on Linux)
    // ------------------------------------------------------------------

    private void WatchFolders()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();

        foreach (var folder in LibraryScanner.NormalizeFolders(_services.Settings.LibraryFolders))
        {
            if (!Directory.Exists(folder))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += OnFolderChanged;
                watcher.Deleted += OnFolderChanged;
                watcher.Changed += OnFolderChanged;
                watcher.Renamed += OnFolderChanged;
                watcher.Error += (_, _) => ScheduleRescan();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Log.Warning($"Cannot watch {folder}: {ex.Message}");
            }
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        bool relevant = LibraryScanner.IsAudioFile(e.FullPath)
            || (e is RenamedEventArgs r && LibraryScanner.IsAudioFile(r.OldFullPath))
            || string.IsNullOrEmpty(Path.GetExtension(e.FullPath));
        if (relevant)
            ScheduleRescan();
    }

    private void ScheduleRescan()
    {
        _services.Dispatcher.TryEnqueue(() =>
        {
            _rescanTimer.Stop();
            _rescanTimer.Start();
        });
    }
}
