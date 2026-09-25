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
    private const int SmartPlaylistLimit = 50;

    private readonly List<SmartPlaylist> _smartPlaylists = new();
    private readonly Dictionary<string, List<string>> _playlistPaths = new();
    private bool _smartRefreshQueued;

    /// <summary>
    /// playlists_sort: smart playlists first (natural order of their titles), then
    /// user playlists, newest first. Playlists staged for deletion are left out.
    /// </summary>
    public ObservableCollection<Playlist> Playlists { get; } = new();

    public IReadOnlyList<SmartPlaylist> SmartPlaylists => _smartPlaylists;

    /// <summary>user_playlists_sort: what the Add to Playlist dialog lists.</summary>
    public IEnumerable<UserPlaylist> UserPlaylists => Playlists.OfType<UserPlaylist>();

    // ------------------------------------------------------------------
    // Smart playlists (grilowrappers/smartplaylist.py)
    // ------------------------------------------------------------------

    private void CreateSmartPlaylists()
    {
        // "Recently" = 7 days before UTC midnight, computed once at start-up.
        var now = DateTime.UtcNow;
        var compareDate = now.AddDays(-7).Date;

        _smartPlaylists.Add(new SmartPlaylist("MOST_PLAYED", Strings.MostPlayed, SmartPlaylist.Glyphs.MostPlayed, songs => songs
            .Where(s => s.PlayCount > 0)
            .OrderByDescending(s => s.PlayCount)
            .Take(SmartPlaylistLimit)));

        _smartPlaylists.Add(new SmartPlaylist("NEVER_PLAYED", Strings.NeverPlayed, SmartPlaylist.Glyphs.NeverPlayed, songs => songs
            .Where(s => s.PlayCount == 0)
            .Take(SmartPlaylistLimit)));

        _smartPlaylists.Add(new SmartPlaylist("RECENTLY_PLAYED", Strings.RecentlyPlayed, SmartPlaylist.Glyphs.RecentlyPlayed, songs => songs
            .Where(s => s.PlayCount > 0 && s.LastPlayed is not null)
            .OrderByDescending(s => s.LastPlayed)
            .Take(SmartPlaylistLimit)
            .Where(s => s.LastPlayed > compareDate)));

        _smartPlaylists.Add(new SmartPlaylist("RECENTLY_ADDED", Strings.RecentlyAdded, SmartPlaylist.Glyphs.RecentlyAdded, songs => songs
            .Where(s => s.Added > compareDate)
            .OrderByDescending(s => s.Added)
            .Take(SmartPlaylistLimit)));

        _smartPlaylists.Add(new SmartPlaylist("FAVORITES", Strings.StarredSongs, SmartPlaylist.Glyphs.Favorites, songs => songs
            .Where(s => s.Favorite)
            .OrderByDescending(s => s.Added)));

        _smartPlaylists.Add(new SmartPlaylist("INSUFFICIENT_TAGGED", Strings.InsufficientlyTagged, SmartPlaylist.Glyphs.InsufficientTagged, songs => songs
            .Where(s => !s.HasAlbum || !s.HasArtist)));

        foreach (var playlist in _smartPlaylists.OrderBy(p => NaturalKey.Create(p.Title)))
            Playlists.Add(playlist);
    }

    /// <summary>Re-runs the query of every smart playlist.</summary>
    public void RefreshSmartPlaylists()
    {
        _smartRefreshQueued = false;
        foreach (var playlist in _smartPlaylists)
            playlist.Refresh(_songs);
    }

    /// <summary>Re-runs one smart playlist (done whenever it is opened in the playlists view).</summary>
    public void RefreshSmartPlaylist(SmartPlaylist playlist) => playlist.Refresh(_songs);

    /// <summary>
    /// [PORT] GNOME Music only refreshes a smart playlist when it is opened; the
    /// port also refreshes them after favorites and play counts change.
    /// </summary>
    private void QueueSmartPlaylistRefresh()
    {
        if (_smartRefreshQueued)
            return;

        _smartRefreshQueued = true;
        _services.Dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RefreshSmartPlaylists);
    }

    internal void OnSongFavoriteChanged(CoreSong song) => QueueSmartPlaylistRefresh();

    internal void OnSongPlayed(CoreSong song) => QueueSmartPlaylistRefresh();

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

    /// <summary>Inserts a user playlist after the smart ones, newest first.</summary>
    private void InsertSorted(UserPlaylist playlist)
    {
        int index = _smartPlaylists.Count(p => Playlists.Contains(p));
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
        var data = _services.PlaylistStore.Create(title, list.Select(s => s.FilePath));
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

    /// <summary>Appends songs (duplicates allowed), like Playlist.add_songs().</summary>
    public void AddToPlaylist(UserPlaylist playlist, IEnumerable<CoreSong> songs)
    {
        var paths = GetPaths(playlist);
        foreach (var song in songs)
        {
            paths.Add(song.FilePath);
            playlist.Songs.Add(song);
        }

        _services.PlaylistStore.SetSongs(playlist.Id, paths);
    }

    public void RemoveFromPlaylist(UserPlaylist playlist, int index)
    {
        if (index < 0 || index >= playlist.Songs.Count)
            return;

        playlist.Songs.RemoveAt(index);
        SyncPaths(playlist);
    }

    public void InsertIntoPlaylist(UserPlaylist playlist, int index, CoreSong song)
    {
        playlist.Songs.Insert(Math.Clamp(index, 0, playlist.Songs.Count), song);
        SyncPaths(playlist);
    }

    /// <summary>Stores the current song order of a playlist (renumbers the entries).</summary>
    public void SyncPaths(UserPlaylist playlist)
    {
        var paths = playlist.Songs.Select(s => s.FilePath).ToList();
        _playlistPaths[playlist.Id] = paths;
        _services.PlaylistStore.SetSongs(playlist.Id, paths);
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
