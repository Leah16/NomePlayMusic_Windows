// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;
using System.Linq;
using Gnome_Music_WinUI.Helpers;

namespace Gnome_Music_WinUI.Models;

/// <summary>A disc of an album (coredisc.py).</summary>
public sealed class CoreDisc
{
    public CoreDisc(CoreAlbum album, int discNumber, IReadOnlyList<CoreSong> songs)
    {
        Album = album;
        DiscNumber = discNumber;
        Songs = songs;
    }

    public CoreAlbum Album { get; }

    public int DiscNumber { get; }

    /// <summary>Songs sorted by track number (stable: ties keep the library order).</summary>
    public IReadOnlyList<CoreSong> Songs { get; }

    public double Duration => Songs.Sum(s => s.Duration);

    /// <summary>"Disc N" (DiscBox.ui heading).</summary>
    public string Title => Strings.Disc(DiscNumber);

    /// <summary>The heading is hidden when the album has exactly one disc.</summary>
    public bool ShowTitle => Album.HasMultipleDiscs;
}

/// <summary>
/// An album (corealbum.py). As in LocalSearch, an album is identified by its
/// title, its album artist tag (if any) and its date (if any).
/// </summary>
public sealed class CoreAlbum : ObservableObject
{
    public CoreAlbum(string id, string title, string? albumArtist, IReadOnlyList<CoreSong> songsInLibraryOrder)
    {
        Id = id;
        Title = title;
        AlbumArtist = albumArtist;

        var withArtist = songsInLibraryOrder.FirstOrDefault(s => s.HasArtist);
        Artist = albumArtist ?? withArtist?.RawArtist ?? Strings.UnknownArtist;
        TrackArtist = withArtist?.RawArtist;
        Composer = songsInLibraryOrder.Select(s => s.Composer).FirstOrDefault(c => !string.IsNullOrEmpty(c));

        // albums.rq: YEAR(MAX(nie:contentCreated)), the latest year of the songs.
        Year = songsInLibraryOrder.Select(s => s.Year).DefaultIfEmpty(0).Max();

        // Discs by number; songs of a disc by track number (stable sort).
        Discs = songsInLibraryOrder
            .GroupBy(s => s.DiscNumber)
            .OrderBy(g => g.Key)
            .Select(g => new CoreDisc(this, g.Key, g.OrderBy(s => s.TrackNumber).ToList()))
            .ToList();
        Songs = Discs.SelectMany(d => d.Songs).ToList();
        Duration = Songs.Sum(s => s.Duration);
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>The album artist tag, if any.</summary>
    public string? AlbumArtist { get; }

    /// <summary>A track artist of the album (albums.rq "artist" column).</summary>
    public string? TrackArtist { get; }

    /// <summary>Album artist, else a track artist, else "Unknown Artist".</summary>
    public string Artist { get; }

    public string? Composer { get; }

    public int Year { get; }

    public string YearText => Year > 0 ? Year.ToString() : "";

    public double Duration { get; }

    public IReadOnlyList<CoreDisc> Discs { get; }

    /// <summary>All songs: discs in order, tracks in order.</summary>
    public IReadOnlyList<CoreSong> Songs { get; }

    public bool HasMultipleDiscs => Discs.Count > 1;

    public override string ToString() => $"{Title} — {Artist}";
}

/// <summary>
/// An artist (coreartist.py): an album artist, or a track artist of albums
/// without an album artist tag.
/// </summary>
public sealed class CoreArtist : ObservableObject
{
    public CoreArtist(string name, IReadOnlyList<CoreAlbum> albums)
    {
        Name = name;
        Albums = albums;
        Songs = albums.SelectMany(a => a.Songs).ToList();
    }

    public string Id => Name;

    public string Name { get; }

    /// <summary>
    /// Albums with a song by the artist or with the artist as album artist, sorted
    /// by year (albums without a year last).
    /// </summary>
    public IReadOnlyList<CoreAlbum> Albums { get; }

    /// <summary>The queue of the artist: albums, then discs, then tracks.</summary>
    public IReadOnlyList<CoreSong> Songs { get; }

    /// <summary>
    /// Album whose cover stands in for the artist image (GNOME Music looks up
    /// artist art online; the port uses the artist's latest album cover).
    /// </summary>
    public CoreAlbum? CoverAlbum =>
        Albums.LastOrDefault(a => (a.AlbumArtist ?? a.TrackArtist) == Name)
        ?? (Albums.Count > 0 ? Albums[^1] : null);

    public override string ToString() => Name;
}
