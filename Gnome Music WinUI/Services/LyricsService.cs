// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Models;

namespace Gnome_Music_WinUI.Services;

public enum LyricsStatus
{
    Found,

    /// <summary>The song is set as instrumental, or LRCLIB knows it as one.</summary>
    Instrumental,

    NotFound,

    /// <summary>LRCLIB could not be reached; the next lookup tries again.</summary>
    Failed,
}

/// <summary>Where lyrics come from.</summary>
public enum LyricsSource
{
    /// <summary>Nothing was looked up: the song is set as instrumental.</summary>
    Song,

    /// <summary>The song's lyrics file (<see cref="LyricsFile"/>).</summary>
    File,

    /// <summary>The song's file in the app's lyrics cache.</summary>
    Cache,

    Lrclib,
}

/// <summary>A song's local lyrics files as they were when looked at: the one in its folder and the one in the lyrics cache.</summary>
public readonly record struct LyricsFiles(FileStamp Song, FileStamp Cache)
{
    public bool Any => Song.Exists || Cache.Exists;
}

public sealed record LyricsResult(LyricsStatus Status, LyricsSource Source, Lyrics? Lyrics = null)
{
    /// <summary>The LRCLIB record the lyrics (or the instrumental) come from.</summary>
    public LrclibTrack? Track { get; init; }

    /// <summary>
    /// The song's local lyrics files as they were when the lyrics were looked up, while
    /// local lyrics are loaded: when they change, so may the lyrics.
    /// </summary>
    public LyricsFiles? LocalFiles { get; init; }
}

/// <summary>
/// The lyrics of songs (not in GNOME Music), looked up only while the lyrics show: the
/// playing song and the next one, and the song whose properties are open. A song set as
/// instrumental has none. Else, with "Load local lyrics" on, its lyrics file comes first
/// (<see cref="LyricsFile"/>), then its file in the app's lyrics cache; without either,
/// or with the setting off, LRCLIB is asked, once per session, and with "Download
/// lyrics" on its lyrics are kept where the settings say: as the song's lyrics file or
/// in the cache. Both settings are off by default.
/// </summary>
public sealed class LyricsService
{
    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;

    private readonly Settings _settings;
    private readonly LrclibClient _lrclib = new(AppVersion());
    private readonly ConcurrentDictionary<string, Task<LyricsResult>> _lookups = new();
    private readonly SemaphoreSlim _gate = new(2);
    private readonly bool _simplifiedChinese = PrefersSimplifiedChinese();
    private readonly object _missingLock = new();

    /// <summary>The songs LRCLIB had no lyrics for (their paths); loaded when first needed.</summary>
    private HashSet<string>? _missing;

    public LyricsService(Settings settings)
    {
        _settings = settings;
        settings.PropertyChanged += (_, e) =>
        {
            // Looked up again, the lyrics showing are also saved once downloads are on
            // (or go to the other place).
            if (e.PropertyName is nameof(Settings.LoadLocalLyrics) or nameof(Settings.DownloadLyrics)
                or nameof(Settings.LyricsLocation) or nameof(Settings.RememberMissingLyrics))
                Changed?.Invoke(this, null);
        };
    }

    /// <summary>
    /// Raised on the UI thread when the lyrics of a song, or of every song (null), may
    /// have changed: it was set as instrumental or not, its lyrics were saved, local
    /// lyrics or downloads were turned on or off, or the cache was cleared.
    /// </summary>
    public event EventHandler<CoreSong?>? Changed;

    /// <summary>The app's lyrics cache: lyrics kept there rather than next to the songs.</summary>
    public static string CacheDirectory => Path.Combine(AppPaths.DataDirectory, "lyrics");

    /// <summary>A song's file in the lyrics cache: its name and a hash of its path ("Song 1a2b3c4d.lrc").</summary>
    public static string CachePathFor(string songPath)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(songPath.ToUpperInvariant()));
        return Path.Combine(CacheDirectory, $"{Path.GetFileNameWithoutExtension(songPath)} {Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}.lrc");
    }

    /// <summary>The song's local lyrics files as they are now (it reads the disk).</summary>
    public static LyricsFiles Stamp(CoreSong song) =>
        new(LyricsFile.Stamp(LyricsFile.PathFor(song.FilePath)), LyricsFile.Stamp(CachePathFor(song.FilePath)));

    /// <summary>The song's lyrics as the settings find them; a lookup that failed is tried again on the next call.</summary>
    public Task<LyricsResult> GetAsync(CoreSong song)
    {
        if (song.Instrumental)
            return Task.FromResult(new LyricsResult(LyricsStatus.Instrumental, LyricsSource.Song));

        bool local = _settings.LoadLocalLyrics;
        bool download = _settings.DownloadLyrics;
        bool rememberMissing = _settings.RememberMissingLyrics;
        string songFile = LyricsFile.PathFor(song.FilePath);
        string cacheFile = CachePathFor(song.FilePath);
        string saveAs = _settings.LyricsLocation == LyricsLocation.Cache ? cacheFile : songFile;
        var query = new LrclibQuery(song.Title, song.Record.Artists ?? Array.Empty<string>(), song.HasAlbum ? song.AlbumTitle : null, song.Duration);
        return Task.Run(async () =>
        {
            LyricsFiles? files = null;
            if (local)
            {
                files = Stamp(song);
                if (files.Value.Song.Exists && ReadFile(songFile) is { } lyrics)
                    return new LyricsResult(LyricsStatus.Found, LyricsSource.File, lyrics) { LocalFiles = files };
                if (files.Value.Cache.Exists && ReadFile(cacheFile) is { } cached)
                    return new LyricsResult(LyricsStatus.Found, LyricsSource.Cache, cached) { LocalFiles = files };
            }

            // Known to LRCLIB as without lyrics: not asked again.
            if (rememberMissing && IsMissing(song.FilePath))
                return new LyricsResult(LyricsStatus.NotFound, LyricsSource.Lrclib) { LocalFiles = files };

            var lookup = _lookups.GetOrAdd(query.Key, _ => LookupAsync(query));
            var result = await lookup.ConfigureAwait(false);
            if (result.Status == LyricsStatus.Failed)
                _lookups.TryRemove(new KeyValuePair<string, Task<LyricsResult>>(query.Key, lookup));
            else if (rememberMissing && result.Status == LyricsStatus.NotFound)
                SetMissing(song.FilePath, true);

            if (download && result.Track is { HasLyrics: true } track && SaveNew(saveAs, track) && local)
                files = Stamp(song);
            return result with { LocalFiles = files };
        });
    }

    /// <summary>What LRCLIB finds for a title and an artist, for the user to choose from.</summary>
    public Task<LrclibTrack[]> SearchAsync(string title, string? artist) => _lrclib.SearchAsync(title, artist);

    /// <summary>The lyrics of an LRCLIB record as they show (null when it has none).</summary>
    public Lyrics? ToLyrics(LrclibTrack track) => Lyrics.Create(ToScript(track.SyncedLyrics), ToScript(track.PlainLyrics));

    /// <summary>
    /// Where lyrics chosen for the song go: in place of the local file they are read
    /// from (the one in its folder, else the cached one), else where the settings keep
    /// downloads. Exists tells whether it replaces a file.
    /// </summary>
    public (string File, bool Exists) SaveTarget(CoreSong song, LyricsFiles files) =>
        files.Song.Exists ? (LyricsFile.PathFor(song.FilePath), true)
        : files.Cache.Exists ? (CachePathFor(song.FilePath), true)
        : (_settings.LyricsLocation == LyricsLocation.Cache ? CachePathFor(song.FilePath) : LyricsFile.PathFor(song.FilePath), false);

    /// <summary>Saves the lyrics of an LRCLIB record as the song's lyrics file or cached lyrics, in place of the file there.</summary>
    public async Task SaveAsync(CoreSong song, LrclibTrack track, string file)
    {
        string text = FileText(track) ?? throw new ArgumentException("The track has no lyrics.", nameof(track));
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            LyricsFile.Write(file, text, replace: true);
            SetMissing(song.FilePath, false);
        });
        Log.Info($"Saved the lyrics of {song.FilePath} as {file}");
        Changed?.Invoke(this, song);
    }

    /// <summary>The list of songs LRCLIB had no lyrics for, a line each, in the lyrics cache (cleared with it).</summary>
    private static string MissingFile => Path.Combine(CacheDirectory, "not-found.txt");

    private bool IsMissing(string songPath)
    {
        lock (_missingLock)
        {
            _missing ??= LoadMissing();
            return _missing.Contains(songPath);
        }
    }

    /// <summary>Remembers that LRCLIB has no lyrics for a song, or forgets it (it has lyrics now).</summary>
    private void SetMissing(string songPath, bool missing)
    {
        lock (_missingLock)
        {
            _missing ??= LoadMissing();
            if (!(missing ? _missing.Add(songPath) : _missing.Remove(songPath)))
                return;

            try
            {
                Directory.CreateDirectory(CacheDirectory);
                File.WriteAllLines(MissingFile, _missing.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"Cannot save the songs without lyrics: {ex.Message}");
            }
        }
    }

    private static HashSet<string> LoadMissing()
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(MissingFile))
                missing.UnionWith(File.ReadAllLines(MissingFile).Where(line => line.Length > 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Cannot read the songs without lyrics: {ex.Message}");
        }

        return missing;
    }

    /// <summary>How many files the lyrics cache holds, and their size.</summary>
    public static (int Files, long Bytes) CacheSize()
    {
        try
        {
            var directory = new DirectoryInfo(CacheDirectory);
            if (!directory.Exists)
                return (0, 0);

            var files = directory.GetFiles("*", SearchOption.AllDirectories);
            return (files.Length, files.Sum(f => f.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Cannot measure the lyrics cache: {ex.Message}");
            return (0, 0);
        }
    }

    /// <summary>
    /// Deletes everything in the lyrics cache (Preferences → Reset), and what this
    /// session found on LRCLIB; the lyrics files next to the songs stay. Returns how
    /// many files went.
    /// </summary>
    public async Task<int> ClearCacheAsync()
    {
        int deleted = await Task.Run(() =>
        {
            var directory = new DirectoryInfo(CacheDirectory);
            if (!directory.Exists)
                return 0;

            int count = 0;
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    file.Delete();
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning($"Cannot delete {file.FullName}: {ex.Message}");
                }
            }

            return count;
        });

        _lookups.Clear();
        lock (_missingLock)
            _missing = null;
        Log.Info($"Cleared the lyrics cache ({deleted} files)");
        Changed?.Invoke(this, null);
        return deleted;
    }

    /// <summary>The song was set as instrumental, or not any more.</summary>
    internal void OnInstrumentalChanged(CoreSong song) => Changed?.Invoke(this, song);

    private async Task<LyricsResult> LookupAsync(LrclibQuery query)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var track = await _lrclib.FindAsync(query).ConfigureAwait(false);
            return track is null ? new LyricsResult(LyricsStatus.NotFound, LyricsSource.Lrclib) : ToResult(track);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot get the lyrics of \"{query.Title}\": {ex.Message}");
            return new LyricsResult(LyricsStatus.Failed, LyricsSource.Lrclib);
        }
        finally
        {
            _gate.Release();
        }
    }

    private LyricsResult ToResult(LrclibTrack track)
    {
        if (ToLyrics(track) is { } lyrics)
            return new LyricsResult(LyricsStatus.Found, LyricsSource.Lrclib, lyrics) { Track = track };
        return new LyricsResult(track.Instrumental ? LyricsStatus.Instrumental : LyricsStatus.NotFound, LyricsSource.Lrclib) { Track = track };
    }

    /// <summary>The lyrics of the song's lyrics file; null when it has none or cannot be read.</summary>
    private Lyrics? ReadFile(string file)
    {
        try
        {
            return Lyrics.Parse(ToScript(LyricsFile.Read(file))!);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Cannot read {file}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Keeps downloaded lyrics as the song's lyrics file or cached lyrics, unless there is one (which may be the user's own).</summary>
    private bool SaveNew(string file, LrclibTrack track)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!LyricsFile.Write(file, FileText(track)!, replace: false))
                return false;

            Log.Info($"Saved the lyrics as {file}");
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Cannot save the lyrics as {file}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The text a lyrics file gets: the synced lyrics, else the plain ones, as they show.</summary>
    private string? FileText(LrclibTrack track) =>
        track.HasSynced ? ToScript(track.SyncedLyrics) : track.HasLyrics ? ToScript(track.PlainLyrics) : null;

    /// <summary>
    /// With the Simplified Chinese UI, lyrics in traditional characters show in
    /// simplified ones, like the rest of the app. Japanese lyrics (with kana) keep
    /// their kanji.
    /// </summary>
    private string? ToScript(string? text)
    {
        if (!_simplifiedChinese || string.IsNullOrEmpty(text) || text.Any(c => c is >= '぀' and <= 'ヿ'))
            return text;

        int length = LCMapStringEx("zh-CN", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (length <= 0)
            return text;

        var buffer = new char[length];
        length = LCMapStringEx("zh-CN", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return length > 0 ? new string(buffer, 0, length) : text;
    }

    private static bool PrefersSimplifiedChinese()
    {
        string language;
        try
        {
            language = Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault() ?? "";
        }
        catch (Exception)
        {
            language = CultureInfo.CurrentUICulture.Name;
        }

        return language.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-SG", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (InvalidOperationException)
        {
            return typeof(LyricsService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(
        string lpLocaleName, uint dwMapFlags, string lpSrcStr, int cchSrc, [Out] char[]? lpDestStr, int cchDest,
        IntPtr lpVersionInformation, IntPtr lpReserved, IntPtr sortHandle);
}
