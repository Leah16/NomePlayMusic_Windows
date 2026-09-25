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

    public abstract bool IsSmart { get; }

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

    public override bool IsSmart => false;

    public override string Glyph => "";
}

/// <summary>
/// An automatically generated playlist (grilowrappers/smartplaylist.py); its
/// content is computed from the library by <see cref="Query"/>.
/// </summary>
public sealed class SmartPlaylist : Playlist
{
    /// <summary>Segoe Fluent Icons stand-ins for the smart playlist icons.</summary>
    public static class Glyphs
    {
        public const string MostPlayed = "";          // audio-speakers-symbolic
        public const string NeverPlayed = "";         // deaf-symbolic
        public const string RecentlyPlayed = "";      // document-open-recent-symbolic
        public const string RecentlyAdded = "";       // list-add-symbolic
        public const string Favorites = "";           // starred-symbolic
        public const string InsufficientTagged = "";  // question-round-symbolic
    }

    private readonly string _glyph;

    public SmartPlaylist(string tag, string title, string glyph, Func<IEnumerable<CoreSong>, IEnumerable<CoreSong>> query)
        : base(tag, title, DateTime.MinValue)
    {
        Tag = tag;
        _glyph = glyph;
        Query = query;
    }

    /// <summary>tag_text, e.g. "MOST_PLAYED".</summary>
    public string Tag { get; }

    public Func<IEnumerable<CoreSong>, IEnumerable<CoreSong>> Query { get; }

    public override bool IsSmart => true;

    public override string Glyph => _glyph;

    public void Refresh(IEnumerable<CoreSong> library) => SetSongs(Query(library).ToList());
}
