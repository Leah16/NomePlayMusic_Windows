// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Services;

namespace Gnome_Music_WinUI.Models;

/// <summary>Playback state of a song in the queue (SongWidget.State).</summary>
public enum SongState
{
    Played = 0,
    Playing = 1,
    Unplayed = 2,
}

/// <summary>Whether the song could be loaded by the player (CoreSong.Validation).</summary>
public enum SongValidation
{
    Pending = 0,
    InProgress = 1,
    Failed = 2,
    Succeeded = 3,
}

/// <summary>
/// A song of the library: the port of gnomemusic/coresong.py. Wraps the file
/// metadata and the user data (favorite, play count, last played).
/// </summary>
public sealed class CoreSong : ObservableObject
{
    private readonly SongUserData _userData;
    private SongState _state = SongState.Unplayed;
    private SongValidation _validation = SongValidation.Pending;
    private NaturalKey? _titleKey;
    private NaturalKey? _artistKey;
    private NaturalKey? _albumKey;

    public CoreSong(SongRecord record, SongUserData userData)
    {
        _userData = userData;
        Record = record;
    }

    internal SongRecord Record { get; private set; }

    /// <summary>Unique id: the file path (the song URN in GNOME Music).</summary>
    public string Id => Record.Path;

    public string FilePath => Record.Path;

    public long ModifiedTicks => Record.Modified;

    /// <summary>The title tag, or the file name with "_" replaced by spaces.</summary>
    public string Title => Record.Title ?? Utils.TitleFromPath(Record.Path);

    /// <summary>The track artist, or "Unknown Artist".</summary>
    public string Artist => RawArtist ?? Strings.UnknownArtist;

    /// <summary>The track artist tag (several artists are joined).</summary>
    public string? RawArtist => Record.Artists is { Length: > 0 } a ? string.Join(", ", a) : null;

    public bool HasArtist => Record.Artists is { Length: > 0 };

    /// <summary>The album title; empty when the song has no album.</summary>
    public string AlbumTitle => Record.Album ?? "";

    public bool HasAlbum => Record.Album is not null;

    public string? AlbumArtist => Record.AlbumArtist;

    public string? Composer => Record.Composer;

    public int TrackNumber => Record.Track;

    /// <summary>Track number as shown in album lists; blank when 0.</summary>
    public string TrackNumberText => Record.Track > 0 ? Record.Track.ToString() : "";

    /// <summary>setNumber: the disc tag if &gt; 0, else 1.</summary>
    public int DiscNumber => Record.Disc > 0 ? Record.Disc : 1;

    public int Year => Record.Year;

    /// <summary>Duration in whole seconds (nfo:duration is an integer).</summary>
    public double Duration => Math.Floor(Record.Duration);

    public string DurationText => Utils.SecondsToString(Duration);

    public DateTime Added => new(_userData.Added, DateTimeKind.Utc);

    /// <summary>The album this song belongs to, if it has an album tag.</summary>
    public CoreAlbum? Album { get; internal set; }

    internal NaturalKey TitleKey => _titleKey ??= NaturalKey.Create(Title);

    internal NaturalKey ArtistKey => _artistKey ??= NaturalKey.Create(Artist);

    internal NaturalKey AlbumKey => _albumKey ??= NaturalKey.Create(AlbumTitle);

    /// <summary>The favorite ("starred") flag, persisted immediately.</summary>
    public bool Favorite
    {
        get => _userData.Favorite;
        set
        {
            if (_userData.Favorite == value)
                return;

            App.Services.UserData.Update(FilePath, d => d.Favorite = value);
            OnPropertyChanged();
            App.Services.Model.OnSongFavoriteChanged(this);
        }
    }

    /// <summary>
    /// Set as instrumental (not in GNOME Music): no lyrics are looked up, the lyrics
    /// page shows the song's title, album and artist. Persisted immediately.
    /// </summary>
    public bool Instrumental
    {
        get => _userData.Instrumental;
        set
        {
            if (_userData.Instrumental == value)
                return;

            App.Services.UserData.Update(FilePath, d => d.Instrumental = value);
            OnPropertyChanged();
            App.Services.Lyrics.OnInstrumentalChanged(this);
        }
    }

    public int PlayCount => _userData.PlayCount;

    public DateTime? LastPlayed => _userData.LastPlayed > 0 ? new DateTime(_userData.LastPlayed, DateTimeKind.Utc) : null;

    public SongState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(IsPlaying));
        }
    }

    public bool IsPlaying => _state == SongState.Playing;

    public SongValidation Validation
    {
        get => _validation;
        set
        {
            if (SetProperty(ref _validation, value))
                OnPropertyChanged(nameof(IsFailed));
        }
    }

    public bool IsFailed => _validation == SongValidation.Failed;

    /// <summary>
    /// Counts a play (player.py _on_clock_tick): sets the last played date and
    /// bumps the play count; both are persisted immediately.
    /// </summary>
    internal void CountPlay()
    {
        App.Services.UserData.Update(FilePath, d =>
        {
            d.PlayCount++;
            d.LastPlayed = DateTime.UtcNow.Ticks;
        });
        OnPropertyChanged(nameof(PlayCount));
        OnPropertyChanged(nameof(LastPlayed));
    }

    /// <summary>Updates the metadata after a rescan (CoreSong.update()).</summary>
    internal bool UpdateRecord(SongRecord record)
    {
        var old = Record;
        Record = record;
        bool changed = old.Title != record.Title
            || old.Album != record.Album
            || old.AlbumArtist != record.AlbumArtist
            || old.Composer != record.Composer
            || !SameArtists(old.Artists, record.Artists)
            || old.Track != record.Track
            || old.Disc != record.Disc
            || old.Year != record.Year
            || Math.Floor(old.Duration) != Math.Floor(record.Duration);
        if (changed)
        {
            _titleKey = _artistKey = _albumKey = null;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Artist));
            OnPropertyChanged(nameof(AlbumTitle));
            OnPropertyChanged(nameof(TrackNumberText));
            OnPropertyChanged(nameof(DurationText));
        }

        return changed;
    }

    private static bool SameArtists(string[]? a, string[]? b)
    {
        if (a is null || b is null)
            return a is null && b is null;

        return a.AsSpan().SequenceEqual(b);
    }

    public override string ToString() => $"{Title} — {Artist}";
}
