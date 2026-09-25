// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Gnome_Music_WinUI.Helpers;

namespace Gnome_Music_WinUI.Models;

/// <summary>A playlist (grilowrappers/playlist.py).</summary>
public abstract class Playlist : ObservableObject
{
    private string _title;

    protected Playlist(string id, string title, DateTime creationDate)
    {
        Id = id;
        _title = title;
        CreationDate = creationDate;
        Songs.CollectionChanged += OnSongsChanged;
    }

    public string Id { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public DateTime CreationDate { get; }

    public ObservableCollection<CoreSong> Songs { get; } = new();

    public int Count => Songs.Count;

    /// <summary>ngettext("{} Song", "{} Songs", count) (playlistcontrols.py).</summary>
    public string CountText => Strings.SongCount(Songs.Count);

    /// <summary>One of the app's playlists (Favorite Songs, Recently Played): not renamed or deleted.</summary>
    public abstract bool IsSystem { get; }

    /// <summary>Songs can be added, removed and put in another order (all but Recently Played).</summary>
    public virtual bool IsEditable => true;

    /// <summary>The playlist icon (icon_name).</summary>
    public abstract string Glyph { get; }

    /// <summary>Replaces the songs, keeping the collection instance views are bound to.</summary>
    internal void SetSongs(IReadOnlyList<CoreSong> songs)
    {
        if (Songs.Count == songs.Count && Songs.SequenceEqual(songs))
            return;

        int common = 0;
        while (common < Songs.Count && common < songs.Count && ReferenceEquals(Songs[common], songs[common]))
            common++;

        while (Songs.Count > common)
            Songs.RemoveAt(Songs.Count - 1);

        for (int i = common; i < songs.Count; i++)
            Songs.Add(songs[i]);
    }

    private void OnSongsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
    }

    public override string ToString() => Title;
}

/// <summary>A playlist created by the user (icon music-playlist-symbolic).</summary>
public sealed class UserPlaylist : Playlist
{
    public UserPlaylist(string id, string title, DateTime creationDate)
        : base(id, title, creationDate)
    {
    }

    public override bool IsSystem => false;

    public override string Glyph => "";
}

/// <summary>
/// Favorite Songs (GNOME Music's "Starred Songs" smart playlist, a list of its own in
/// the port): the starred songs, in the order the user gives them. Starring a song puts
/// it at the top, unstarring takes it out; its songs can be dragged into another order.
/// </summary>
public sealed class FavoritesPlaylist : Playlist
{
    public FavoritesPlaylist(string title)
        : base("FAVORITES", title, DateTime.MinValue)
    {
    }

    public override bool IsSystem => true;

    public override string Glyph => "";   // starred-symbolic
}

/// <summary>
/// Recently Played (GNOME Music's smart playlist, a history in the port): the songs
/// played last, the latest at the top; each song that starts playing moves there. It
/// cannot be edited.
/// </summary>
public sealed class HistoryPlaylist : Playlist
{
    public HistoryPlaylist(string title)
        : base("RECENTLY_PLAYED", title, DateTime.MinValue)
    {
    }

    public override bool IsSystem => true;

    public override bool IsEditable => false;

    public override string Glyph => "";   // document-open-recent-symbolic
}
