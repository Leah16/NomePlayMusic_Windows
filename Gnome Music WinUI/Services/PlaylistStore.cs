// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Gnome_Music_WinUI.Services;

public sealed class PlaylistData
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public long Created { get; set; }
    public long Modified { get; set; }
    public List<string> Songs { get; set; } = new();
}

public sealed class PlaylistsFile
{
    public List<PlaylistData> Playlists { get; set; } = new();

    /// <summary>The favorite songs in the order the user gave them (Windows port); null until first saved.</summary>
    public List<string>? Favorites { get; set; }

    /// <summary>The songs played last, the latest first (Windows port); null until first saved.</summary>
    public List<string>? History { get; set; }
}

/// <summary>
/// Persistence of user playlists. GNOME Music stores them in Tracker as
/// nmm:Playlist resources with ordered nfo:MediaFileListEntry items; here they
/// are an ordered list of file paths in a JSON file, with the order of the favorite
/// songs and the history of Recently Played.
/// </summary>
public sealed class PlaylistStore
{
    private readonly object _lock = new();
    private readonly List<PlaylistData> _playlists;
    private readonly DebouncedSaver _saver;
    private List<string>? _favorites;
    private List<string>? _history;

    public PlaylistStore()
    {
        var file = JsonStorage.Load(AppPaths.PlaylistsFile, AppJsonContext.Default.PlaylistsFile);
        _playlists = file?.Playlists ?? new List<PlaylistData>();
        _favorites = file?.Favorites;
        _history = file?.History;
        _saver = new DebouncedSaver(Save, TimeSpan.FromSeconds(1));
    }

    /// <summary>The order of the favorite songs (paths); null when none was saved yet.</summary>
    public List<string>? Favorites
    {
        get
        {
            lock (_lock)
            {
                return _favorites is null ? null : new List<string>(_favorites);
            }
        }
    }

    /// <summary>Recently Played (paths, the latest first); null when none was saved yet.</summary>
    public List<string>? History
    {
        get
        {
            lock (_lock)
            {
                return _history is null ? null : new List<string>(_history);
            }
        }
    }

    public void SetFavorites(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        lock (_lock)
        {
            _favorites = list;
        }

        _saver.Schedule();
    }

    public void SetHistory(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        lock (_lock)
        {
            _history = list;
        }

        _saver.Schedule();
    }

    public IReadOnlyList<PlaylistData> Snapshot()
    {
        lock (_lock)
        {
            return _playlists.Select(Clone).ToList();
        }
    }

    public PlaylistData Create(string title, IEnumerable<string> songPaths)
    {
        var now = DateTime.UtcNow.Ticks;
        var data = new PlaylistData
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Created = now,
            Modified = now,
            Songs = songPaths.ToList(),
        };

        lock (_lock)
        {
            _playlists.Add(data);
        }

        _saver.Schedule();
        return Clone(data);
    }

    /// <summary>Re-inserts a previously deleted playlist (undo of a deletion).</summary>
    public void Restore(PlaylistData data)
    {
        lock (_lock)
        {
            if (_playlists.All(p => p.Id != data.Id))
                _playlists.Add(Clone(data));
        }

        _saver.Schedule();
    }

    public void Rename(string id, string title) => Mutate(id, p => p.Title = title);

    public void Delete(string id)
    {
        lock (_lock)
        {
            _playlists.RemoveAll(p => p.Id == id);
        }

        _saver.Schedule();
    }

    public void SetSongs(string id, IEnumerable<string> songPaths)
    {
        var list = songPaths.ToList();
        Mutate(id, p => p.Songs = list);
    }

    public void Flush() => _saver.Flush();

    private void Mutate(string id, Action<PlaylistData> action)
    {
        lock (_lock)
        {
            var playlist = _playlists.FirstOrDefault(p => p.Id == id);
            if (playlist is null)
                return;

            action(playlist);
            playlist.Modified = DateTime.UtcNow.Ticks;
        }

        _saver.Schedule();
    }

    private void Save()
    {
        PlaylistsFile snapshot;
        lock (_lock)
        {
            snapshot = new PlaylistsFile
            {
                Playlists = _playlists.Select(Clone).ToList(),
                Favorites = _favorites is null ? null : new List<string>(_favorites),
                History = _history is null ? null : new List<string>(_history),
            };
        }

        JsonStorage.Save(AppPaths.PlaylistsFile, snapshot, AppJsonContext.Default.PlaylistsFile);
    }

    private static PlaylistData Clone(PlaylistData p) => new()
    {
        Id = p.Id,
        Title = p.Title,
        Created = p.Created,
        Modified = p.Modified,
        Songs = new List<string>(p.Songs),
    };
}
