// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Gnome_Music_WinUI.Services;

public sealed class SongUserData
{
    public bool Favorite { get; set; }
    public int PlayCount { get; set; }
    public long LastPlayed { get; set; }
    public long Added { get; set; }

    /// <summary>Set as instrumental: no lyrics are looked up (Windows port).</summary>
    public bool Instrumental { get; set; }
}

public sealed class UserDataFile
{
    public Dictionary<string, SongUserData> Songs { get; set; } = new();
}

/// <summary>
/// Per-song data that Tracker stores for GNOME Music: the favorite tag
/// (nao:predefined-tag-favorite), the play count (nie:usageCounter), the last
/// played date (nie:contentAccessed) and the date the song was added (nrl:added);
/// and the port's own "instrumental" mark. Keyed by file path.
/// </summary>
public sealed class UserDataStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, SongUserData> _songs;
    private readonly DebouncedSaver _saver;

    public UserDataStore()
    {
        var file = JsonStorage.Load(AppPaths.UserDataFile, AppJsonContext.Default.UserDataFile);
        _songs = new Dictionary<string, SongUserData>(
            file?.Songs ?? new Dictionary<string, SongUserData>(),
            StringComparer.OrdinalIgnoreCase);
        _saver = new DebouncedSaver(Save, TimeSpan.FromSeconds(2));
    }

    /// <summary>Returns the data of a song, creating it (with its added date) when first seen.</summary>
    public SongUserData GetOrCreate(string path, DateTime addedFallbackUtc)
    {
        lock (_lock)
        {
            if (!_songs.TryGetValue(path, out var data))
            {
                var added = addedFallbackUtc > DateTime.UtcNow || addedFallbackUtc.Year < 1990
                    ? DateTime.UtcNow
                    : addedFallbackUtc;
                data = new SongUserData { Added = added.Ticks };
                _songs[path] = data;
                _saver.Schedule();
            }

            return data;
        }
    }

    public void Update(string path, Action<SongUserData> update)
    {
        lock (_lock)
        {
            if (!_songs.TryGetValue(path, out var data))
            {
                data = new SongUserData { Added = DateTime.UtcNow.Ticks };
                _songs[path] = data;
            }

            update(data);
        }

        _saver.Schedule();
    }

    /// <summary>The starred songs, the latest added first: Favorite Songs before the user orders it.</summary>
    public List<string> FavoritePaths()
    {
        lock (_lock)
        {
            return _songs.Where(s => s.Value.Favorite).OrderByDescending(s => s.Value.Added).Select(s => s.Key).ToList();
        }
    }

    /// <summary>The songs played, the latest first: Recently Played before it keeps its own history.</summary>
    public List<string> PlayedPaths(int limit)
    {
        lock (_lock)
        {
            return _songs.Where(s => s.Value.LastPlayed > 0).OrderByDescending(s => s.Value.LastPlayed).Take(limit).Select(s => s.Key).ToList();
        }
    }

    /// <summary>Every song's play count back to 0 (Preferences → Reset); the rest stays. Returns how many songs had plays.</summary>
    public int ClearPlayCounts()
    {
        int cleared = 0;
        lock (_lock)
        {
            foreach (var data in _songs.Values)
            {
                if (data.PlayCount == 0)
                    continue;
                data.PlayCount = 0;
                cleared++;
            }
        }

        if (cleared > 0)
            _saver.Schedule();
        return cleared;
    }

    public void Flush() => _saver.Flush();

    private void Save()
    {
        UserDataFile snapshot;
        lock (_lock)
        {
            var copy = new Dictionary<string, SongUserData>(_songs.Count);
            foreach (var (key, value) in _songs)
            {
                copy[key] = new SongUserData
                {
                    Favorite = value.Favorite,
                    PlayCount = value.PlayCount,
                    LastPlayed = value.LastPlayed,
                    Added = value.Added,
                    Instrumental = value.Instrumental,
                };
            }

            snapshot = new UserDataFile { Songs = copy };
        }

        JsonStorage.Save(AppPaths.UserDataFile, snapshot, AppJsonContext.Default.UserDataFile);
    }
}
