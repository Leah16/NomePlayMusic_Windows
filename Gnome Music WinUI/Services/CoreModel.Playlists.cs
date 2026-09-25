// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;

namespace Gnome_Music_WinUI.Services;

public sealed partial class CoreModel
{
    /// <summary>Recently Played keeps this many songs.</summary>
    private const int HistoryLimit = 100;

    private readonly Dictionary<string, List<string>> _playlistPaths = new();
    private FavoritesPlaylist _favorites = null!;
    private HistoryPlaylist _history = null!;

    /// <summary>The order of the favorite songs, those missing from the library included.</summary>
    private List<string> _favoritePaths = new();

    /// <summary>Recently Played, the latest first, songs missing from the library included.</summary>
    private List<string> _historyPaths = new();

    /// <summary>
    /// The sidebar's playlists: Favorite Songs and Recently Played (the port keeps these
    /// two of GNOME Music's smart playlists), then the user's playlists, newest first.
    /// Playlists staged for deletion are left out.
    /// </summary>
    public ObservableCollection<Playlist> Playlists { get; } = new();

    public FavoritesPlaylist Favorites => _favorites;

    public HistoryPlaylist History => _history;

    /// <summary>user_playlists_sort.</summary>
    public IEnumerable<UserPlaylist> UserPlaylists => Playlists.OfType<UserPlaylist>();

    /// <summary>What songs can be added to (the Add to Playlist dialog): Favorite Songs and the user's playlists.</summary>
    public IEnumerable<Playlist> EditablePlaylists => Playlists.Where(p => p.IsEditable);

    // ------------------------------------------------------------------
    // Favorite Songs and Recently Played
    // ------------------------------------------------------------------

    private void CreateSystemPlaylists()
    {
        _favorites = new FavoritesPlaylist(Strings.StarredSongs);
        _history = new HistoryPlaylist(Strings.RecentlyPlayed);
        Playlists.Add(_favorites);
        Playlists.Add(_history);
    }

    /// <summary>
    /// Their saved order and history. The first time, what GNOME Music's smart playlists
    /// showed: the starred songs, the latest added first, and the songs played by their
    /// last play; both are saved then.
    /// </summary>
    private void LoadSystemPlaylists()
    {
        var store = _services.PlaylistStore;
        if (store.Favorites is { } favorites)
            _favoritePaths = favorites;
        else
            store.SetFavorites(_favoritePaths = _services.UserData.FavoritePaths());

        if (store.History is { } history)
            _historyPaths = history;
        else
            store.SetHistory(_historyPaths = _services.UserData.PlayedPaths(HistoryLimit));

        ResolveSystemPlaylists();
    }

    /// <summary>Maps their paths to library songs again; Favorite Songs also takes the starred songs its order misses.</summary>
    private void ResolveSystemPlaylists()
    {
        var favorites = new List<CoreSong>();
        var seen = new HashSet<CoreSong>();
        foreach (var path in _favoritePaths)
        {
            if (FindSong(path) is { Favorite: true } song && seen.Add(song))
                favorites.Add(song);
        }

        favorites.AddRange(_songs.Where(s => s.Favorite && !seen.Contains(s)));
        _favorites.SetSongs(favorites);
        _history.SetSongs(Resolve(_historyPaths).Distinct().ToList());
    }

    /// <summary>A song was starred or unstarred: it goes to the top of Favorite Songs, or out of it.</summary>
    internal void OnSongFavoriteChanged(CoreSong song)
    {
        _favoritePaths.RemoveAll(p => SamePath(p, song.FilePath));
        if (song.Favorite)
            _favoritePaths.Insert(0, song.FilePath);
        _services.PlaylistStore.SetFavorites(_favoritePaths);

        int index = _favorites.Songs.IndexOf(song);
        if (song.Favorite && index < 0)
            _favorites.Songs.Insert(0, song);
        else if (!song.Favorite && index >= 0)
            _favorites.Songs.RemoveAt(index);
    }

    /// <summary>A song started playing (Player): it goes to the top of Recently Played.</summary>
    internal void OnSongStarted(CoreSong song)
    {
        _historyPaths.RemoveAll(p => SamePath(p, song.FilePath));
        _historyPaths.Insert(0, song.FilePath);
        if (_historyPaths.Count > HistoryLimit)
            _historyPaths.RemoveRange(HistoryLimit, _historyPaths.Count - HistoryLimit);
        _services.PlaylistStore.SetHistory(_historyPaths);

        int index = _history.Songs.IndexOf(song);
        if (index > 0)
        {
            _history.Songs.Move(index, 0);
        }
        else if (index < 0)
        {
            _history.Songs.Insert(0, song);
            while (_history.Songs.Count > HistoryLimit)
                _history.Songs.RemoveAt(_history.Songs.Count - 1);
        }
    }

    /// <summary>
    /// Every song's play count back to 0 (Preferences → Reset, not in GNOME Music).
    /// Favorites, last played dates and Recently Played stay. Returns how many songs had
    /// plays.
    /// </summary>
    public int ClearPlayCounts()
    {
        int cleared = _services.UserData.ClearPlayCounts();
        foreach (var song in _songs)
            song.OnPlayCountCleared();
        Log.Info($"Cleared the play counts of {cleared} songs");
        return cleared;
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    // User playlists
    // ------------------------------------------------------------------

    private void LoadUserPlaylists()
    {
        foreach (var data in _services.PlaylistStore.Snapshot())
        {
            var playlist = new UserPlaylist(data.Id, data.Title, new DateTime(data.Created, DateTimeKind.Utc));
            _playlistPaths[data.Id] = data.Songs;
            playlist.SetSongs(Resolve(data.Songs));
            InsertSorted(playlist);
        }
    }

    /// <summary>Inserts a user playlist after the app's, newest first.</summary>
    private void InsertSorted(UserPlaylist playlist)
    {
        int index = Playlists.Count(p => p.IsSystem);
        while (index < Playlists.Count
            && Playlists[index] is UserPlaylist other
            && other.CreationDate >= playlist.CreationDate)
        {
            index++;
        }

        Playlists.Insert(index, playlist);
    }

    /// <summary>Maps the stored paths of every user playlist to library songs again.</summary>
    private void ResolveUserPlaylists()
    {
        foreach (var playlist in UserPlaylists)
        {
            if (_playlistPaths.TryGetValue(playlist.Id, out var paths))
                playlist.SetSongs(Resolve(paths));
        }
    }

    /// <summary>Entries whose file is not in the library are skipped.</summary>
    private List<CoreSong> Resolve(IEnumerable<string> paths) =>
        paths.Select(FindSong).Where(s => s is not null).Select(s => s!).ToList();

    public UserPlaylist CreatePlaylist(string title, IEnumerable<CoreSong>? songs = null)
    {
        var list = songs?.ToList() ?? new List<CoreSong>();
        var data = _services.PlaylistStore.Create(title.Trim(), list.Select(s => s.FilePath));
        var playlist = new UserPlaylist(data.Id, data.Title, new DateTime(data.Created, DateTimeKind.Utc));
        _playlistPaths[data.Id] = data.Songs;
        playlist.SetSongs(list);
        InsertSorted(playlist);
        return playlist;
    }

    public void RenamePlaylist(UserPlaylist playlist, string title)
    {
        title = title.Trim();
        if (title.Length == 0 || title == playlist.Title)
            return;

        playlist.Title = title;
        _services.PlaylistStore.Rename(playlist.Id, title);
    }

    /// <summary>
    /// Hides a playlist at once (stage_playlist_deletion). It is deleted for good by
    /// <see cref="FinishPlaylistDeletion"/> or brought back by <see cref="UndoPlaylistDeletion"/>.
    /// </summary>
    public void StagePlaylistDeletion(UserPlaylist playlist) => Playlists.Remove(playlist);

    public void UndoPlaylistDeletion(UserPlaylist playlist)
    {
        if (!Playlists.Contains(playlist))
            InsertSorted(playlist);
    }

    public void FinishPlaylistDeletion(UserPlaylist playlist)
    {
        if (Playlists.Contains(playlist))
            return;

        _services.PlaylistStore.Delete(playlist.Id);
        _playlistPaths.Remove(playlist.Id);
    }

    // ------------------------------------------------------------------
    // Songs of a playlist: user playlists and Favorite Songs
    // ------------------------------------------------------------------

    /// <summary>
    /// Appends songs to a user playlist (duplicates allowed, like Playlist.add_songs());
    /// Favorite Songs stars them. Recently Played takes none.
    /// </summary>
    public void AddToPlaylist(Playlist playlist, IEnumerable<CoreSong> songs)
    {
        if (playlist is FavoritesPlaylist)
        {
            foreach (var song in songs)
                song.Favorite = true;
            return;
        }

        if (playlist is not UserPlaylist user)
            return;

        var paths = GetPaths(user);
        foreach (var song in songs)
        {
            paths.Add(song.FilePath);
            user.Songs.Add(song);
        }

        _services.PlaylistStore.SetSongs(user.Id, paths);
    }

    /// <summary>Takes a song out of a playlist; out of Favorite Songs, it is unstarred.</summary>
    public void RemoveFromPlaylist(Playlist playlist, int index)
    {
        if (!playlist.IsEditable || index < 0 || index >= playlist.Songs.Count)
            return;

        if (playlist is FavoritesPlaylist)
        {
            playlist.Songs[index].Favorite = false;   // OnSongFavoriteChanged takes it out
            return;
        }

        playlist.Songs.RemoveAt(index);
        SyncOrder(playlist);
    }

    /// <summary>Puts a song back where it was (undo); into Favorite Songs, it is starred again.</summary>
    public void InsertIntoPlaylist(Playlist playlist, int index, CoreSong song)
    {
        if (!playlist.IsEditable)
            return;

        if (playlist is FavoritesPlaylist)
        {
            song.Favorite = true;   // at the top, then back in its place
            int at = playlist.Songs.IndexOf(song);
            int target = Math.Clamp(index, 0, playlist.Songs.Count - 1);
            if (at >= 0 && at != target)
                playlist.Songs.Move(at, target);
        }
        else
        {
            playlist.Songs.Insert(Math.Clamp(index, 0, playlist.Songs.Count), song);
        }

        SyncOrder(playlist);
    }

    /// <summary>Stores the order of a playlist's songs as it shows (after a drag, say; renumbers the entries).</summary>
    public void SyncOrder(Playlist playlist)
    {
        switch (playlist)
        {
            case UserPlaylist user:
                var paths = user.Songs.Select(s => s.FilePath).ToList();
                _playlistPaths[user.Id] = paths;
                _services.PlaylistStore.SetSongs(user.Id, paths);
                break;

            case FavoritesPlaylist favorites:
                // Favorites missing from the library keep their places after the others.
                var order = favorites.Songs.Select(s => s.FilePath).ToList();
                var shown = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
                order.AddRange(_favoritePaths.Where(p => !shown.Contains(p)));
                _favoritePaths = order;
                _services.PlaylistStore.SetFavorites(order);
                break;
        }
    }

    private List<string> GetPaths(UserPlaylist playlist)
    {
        if (!_playlistPaths.TryGetValue(playlist.Id, out var paths))
        {
            paths = playlist.Songs.Select(s => s.FilePath).ToList();
            _playlistPaths[playlist.Id] = paths;
        }

        return paths;
    }
}
