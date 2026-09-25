// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;

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
