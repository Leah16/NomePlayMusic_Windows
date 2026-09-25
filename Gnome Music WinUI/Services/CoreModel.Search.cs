// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;

namespace Gnome_Music_WinUI.Services;

/// <summary>Result of a library search (localsearchwrapper.py search()).</summary>
public sealed class SearchResults
{
    public static readonly SearchResults Empty = new("", Array.Empty<CoreArtist>(), Array.Empty<CoreAlbum>(), Array.Empty<CoreSong>());

    public SearchResults(string text, IReadOnlyList<CoreArtist> artists, IReadOnlyList<CoreAlbum> albums, IReadOnlyList<CoreSong> songs)
    {
        Text = text;
        Artists = artists;
        Albums = albums;
        Songs = songs;
    }

    public string Text { get; }

    public IReadOnlyList<CoreArtist> Artists { get; }

    public IReadOnlyList<CoreAlbum> Albums { get; }

    public IReadOnlyList<CoreSong> Songs { get; }

    public bool IsEmpty => Artists.Count == 0 && Albums.Count == 0 && Songs.Count == 0;
}

public sealed partial class CoreModel
{
    /// <summary>LIMIT of each of the three search queries.</summary>
    public const int SearchLimit = 50;

    /// <summary>Normalized match fields per song; replaced whenever the library changes.</summary>
    private ConditionalWeakTable<CoreSong, string[]> _searchFields = new();

    /// <summary>
    /// Searches artists, albums and songs (search_*.rq). The term is compared as a
    /// case-folded NFKD substring of the album title, the track artist, the song
    /// title and the composer; accents in the library are ignored when the term has none.
    /// Songs match on their own fields; an album matches if one of its songs does;
    /// an artist matches on the track artist, title or composer of its songs.
    /// </summary>
    public SearchResults Search(string? text)
    {
        var term = Utils.NormalizeCaseless(text?.Trim());
        if (term.Length == 0)
            return SearchResults.Empty;

        bool SongMatches(CoreSong song, bool includeAlbum)
        {
            var fields = _searchFields.GetValue(song, BuildFields);
            for (int i = includeAlbum ? 0 : 2; i < fields.Length; i++)
            {
                if (fields[i].Contains(term, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        var songs = _songs.Where(s => SongMatches(s, true)).Take(SearchLimit).ToList();
        var albums = _masterAlbums.Where(a => a.Songs.Any(s => SongMatches(s, true))).Take(SearchLimit).ToList();
        var artists = Artists
            .Where(a => _artistSongs.TryGetValue(a, out var contributing) && contributing.Any(s => SongMatches(s, false)))
            .Take(SearchLimit)
            .ToList();

        return new SearchResults(text!.Trim(), artists, albums, songs);
    }

    /// <summary>
    /// The match fields of a song: album title and album-less forms first
    /// (indexes 0-1), then track artist, title and composer, each as NFKD and unaccented.
    /// </summary>
    private static string[] BuildFields(CoreSong song)
    {
        var raw = new[] { song.AlbumTitle, song.RawArtist ?? "", song.Title, song.Composer ?? "" };
        var fields = new List<string>(8);

        // Album title first so artist searches can skip it.
        var album = Utils.NormalizeCaseless(raw[0]);
        fields.Add(album);
        fields.Add(Utils.Unaccent(album));
        for (int i = 1; i < raw.Length; i++)
        {
            var normalized = Utils.NormalizeCaseless(raw[i]);
            fields.Add(normalized);
            fields.Add(Utils.Unaccent(normalized));
        }

        return fields.ToArray();
    }
}
