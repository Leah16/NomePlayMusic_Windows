// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gnome_Music_WinUI.Models;

namespace Gnome_Music_WinUI.Services;

/// <summary>What a queue was built from (Queue.Type).</summary>
public enum QueueType
{
    Songs = 0,
    Album = 1,
    Artist = 2,
    Playlist = 3,
    SearchResult = 4,
}

/// <summary>
/// The play queue: queue.py together with shufflelistmodel.py. Positions are
/// indexes in the (possibly shuffled) play order. The song states it maintains
/// (playing / played / unplayed) drive the rows of the song lists.
/// </summary>
public sealed class PlayQueue
{
    private readonly Random _random = new();
    private List<CoreSong> _items = new();
    private List<int> _values = new();
    private int _position;

    public object? Source { get; private set; }

    public QueueType Type { get; private set; }

    public RepeatMode RepeatMode { get; private set; }

    public int Count => _items.Count;

    public int Position => _position;

    public bool IsShuffled { get; private set; }

    /// <summary>The song at a position of the play order.</summary>
    public CoreSong this[int position] => _items[_values[position]];

    /// <summary>The songs in play order.</summary>
    public IEnumerable<CoreSong> Songs => _values.Select(i => _items[i]);

    /// <summary>
    /// Replaces the queue (coremodel.py _set_player_model). Loading the same source
    /// again keeps the list and only resets the song states.
    /// </summary>
    public void Load(IReadOnlyList<CoreSong> songs, object? source, QueueType type)
    {
        foreach (var song in _items)
            song.State = SongState.Unplayed;

        if (source is null || !ReferenceEquals(source, Source) || !_items.SequenceEqual(songs))
        {
            _items = songs.ToList();
            _values = Enumerable.Range(0, _items.Count).ToList();
            IsShuffled = false;
        }

        Source = source;
        Type = type;
        _position = 0;
    }

    /// <summary>
    /// Selects the song to start with (queue.py set_song). Without a song the queue
    /// starts at its first song, or at a random one in shuffle mode. In shuffle mode
    /// the chosen song plays first and all the others follow in random order.
    /// </summary>
    public CoreSong? SetSong(CoreSong? song)
    {
        if (_items.Count == 0)
            return null;

        Deshuffle();
        int index = song is null ? -1 : _items.IndexOf(song);
        if (song is not null && index < 0)
            return null;

        if (RepeatMode == RepeatMode.Shuffle)
        {
            int first = index >= 0 ? index : _random.Next(_items.Count);
            var rest = Enumerable.Range(0, _items.Count).Where(i => i != first).ToList();
            ShuffleInPlace(rest);
            rest.Insert(0, first);
            _values = rest;
            IsShuffled = true;
            _position = 0;
        }
        else
        {
            _position = Math.Max(0, index);
        }

        var current = this[_position];
        current.State = SongState.Playing;
        Validate(current);
        return current;
    }

    /// <summary>The PLAYING song (queue.py current_song), or null.</summary>
    public CoreSong? CurrentSong
    {
        get
        {
            if (_position >= 0 && _position < _values.Count && this[_position].State == SongState.Playing)
                return this[_position];

            for (int i = 0; i < _values.Count; i++)
            {
                if (this[i].State == SongState.Playing)
                {
                    _position = i;
                    return this[i];
                }
            }

            return null;
        }
    }

    public bool HasNext =>
        _items.Count > 0 && (RepeatMode is RepeatMode.Song or RepeatMode.All || _position < _items.Count - 1);

    public bool HasPrevious =>
        _items.Count > 0 && (RepeatMode is RepeatMode.Song or RepeatMode.All || (_position > 0 && _position <= _items.Count - 1));

    /// <summary>The song that <see cref="Next"/> would play, or null.</summary>
    public CoreSong? PeekNext() => HasNext ? this[NextIndex()] : null;

    /// <summary>The song <see cref="Next"/> would end up on, skipping failed songs (queue.py get_next).</summary>
    public CoreSong? PeekNextPlayable()
    {
        if (_items.Count == 0 || CurrentSong is null)
            return null;

        int position = _position;
        for (int attempts = 0; attempts < _items.Count; attempts++)
        {
            bool hasNext = RepeatMode is RepeatMode.Song or RepeatMode.All || position < _items.Count - 1;
            if (!hasNext)
                return null;

            position = RepeatMode switch
            {
                RepeatMode.Song => position,
                RepeatMode.All when position == _items.Count - 1 => 0,
                _ => position + 1,
            };
            var song = this[position];
            if (song.Validation != SongValidation.Failed)
                return song;
        }

        return null;
    }

    /// <summary>Advances to the next playable song. Failed songs are skipped.</summary>
    public bool Next()
    {
        for (int attempts = 0; attempts < Math.Max(1, _items.Count); attempts++)
        {
            if (!HasNext)
                return false;

            this[_position].State = SongState.Played;
            _position = NextIndex();
            var song = this[_position];
            Validate(song);
            if (song.Validation == SongValidation.Failed)
                continue;

            song.State = SongState.Playing;
            if (RepeatMode != RepeatMode.Song && PeekNext() is { } next)
                Validate(next);
            return true;
        }

        return false;
    }

    /// <summary>Goes back to the previous playable song.</summary>
    public bool Previous()
    {
        for (int attempts = 0; attempts < Math.Max(1, _items.Count); attempts++)
        {
            if (!HasPrevious)
                return false;

            this[_position].State = SongState.Played;
            _position = PreviousIndex();
            var song = this[_position];
            Validate(song);
            if (song.Validation == SongValidation.Failed)
                continue;

            song.State = SongState.Playing;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Makes the song at a position of the play order the current one, keeping the
    /// order even in shuffle mode (the play queue overlay; not in GNOME Music). Like
    /// <see cref="Next"/>, the song that was playing counts as played.
    /// </summary>
    public CoreSong? JumpTo(int position)
    {
        if (position < 0 || position >= _values.Count)
            return null;

        if (CurrentSong is { } current)
            current.State = SongState.Played;

        _position = position;
        var song = this[position];
        Validate(song);
        song.State = SongState.Playing;
        return song;
    }

    /// <summary>Ends the queue: every song unplayed, no current song (queue.py end()).</summary>
    public void End()
    {
        foreach (var song in _items)
            song.State = SongState.Unplayed;
    }

    /// <summary>Ends the queue and drops its songs: nothing is left to play again.</summary>
    public void Clear()
    {
        End();
        _items = new();
        _values = new();
        _position = 0;
        Source = null;
        IsShuffled = false;
    }

    /// <summary>
    /// Applies a repeat mode change during playback. Shuffle randomizes the songs
    /// after the current one; leaving shuffle puts them back in their original order.
    /// </summary>
    public void SetRepeatMode(RepeatMode mode)
    {
        var old = RepeatMode;
        RepeatMode = mode;
        if (old == mode || _items.Count == 0)
            return;

        if (mode == RepeatMode.Shuffle)
            ShuffleAfter(_position);
        else if (mode is RepeatMode.None or RepeatMode.All)
            DeshuffleAfter(_position);
    }

    /// <summary>Marks a song failed without playing it when its file is gone (validate()).</summary>
    public static void Validate(CoreSong song)
    {
        if (song.Validation == SongValidation.Pending && !File.Exists(song.FilePath))
            song.Validation = SongValidation.Failed;
    }

    private int NextIndex() => RepeatMode switch
    {
        RepeatMode.Song => _position,
        RepeatMode.All when _position == _items.Count - 1 => 0,
        _ => _position + 1,
    };

    private int PreviousIndex() => RepeatMode switch
    {
        RepeatMode.Song => _position,
        RepeatMode.All when _position == 0 => _items.Count - 1,
        _ => _position - 1,
    };

    /// <summary>shufflelistmodel.py shuffle(position): keeps 0..position, randomizes the rest.</summary>
    private void ShuffleAfter(int position)
    {
        position = Math.Clamp(position, 0, _values.Count - 1);
        var after = _values.Skip(position + 1).ToList();
        ShuffleInPlace(after);
        _values = _values.Take(position + 1).Concat(after).ToList();
        IsShuffled = true;
    }

    /// <summary>shufflelistmodel.py deshuffle(position): sorts the songs after position.</summary>
    private void DeshuffleAfter(int position)
    {
        position = Math.Clamp(position, 0, _values.Count - 1);
        var after = _values.Skip(position + 1).OrderBy(v => v).ToList();
        _values = _values.Take(position + 1).Concat(after).ToList();
        IsShuffled = false;
    }

    private void Deshuffle()
    {
        _values = Enumerable.Range(0, _items.Count).ToList();
        IsShuffled = false;
    }

    private void ShuffleInPlace(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
