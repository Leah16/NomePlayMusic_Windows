// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Controls;
using Gnome_Music_WinUI.Dialogs;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml;

namespace Gnome_Music_WinUI.Helpers;

/// <summary>
/// Actions shared by the album, artist, playlist, search and song widgets
/// (album.*, songwidget.* and win.playlist_* actions of GNOME Music).
/// </summary>
public static class SongActions
{
    private static Player Player => App.Services.Player;

    /// <summary>Plays an album from <paramref name="start"/> or from its first song.</summary>
    public static void PlayAlbum(CoreAlbum album, CoreSong? start = null) =>
        Player.PlaySongs(album.Songs, start, album, QueueType.Album);

    /// <summary>Plays all songs of an artist (albums by year, discs, tracks).</summary>
    public static void PlayArtist(CoreArtist artist, CoreSong? start = null) =>
        Player.PlaySongs(artist.Songs, start, artist, QueueType.Artist);

    public static void PlayPlaylist(Playlist playlist, CoreSong? start = null)
    {
        if (playlist.Songs.Count > 0)
            Player.PlaySongs(playlist.Songs.ToList(), start, playlist, QueueType.Playlist);
    }

    /// <summary>Plays the song results of a search (queue type SEARCH_RESULT).</summary>
    public static void PlaySearchResults(SearchResults results, CoreSong start) =>
        Player.PlaySongs(results.Songs, start, results, QueueType.SearchResult);

    /// <summary>"Add to Favorite Songs": stars every song that is not starred yet.</summary>
    public static void AddToFavorites(IEnumerable<CoreSong> songs)
    {
        foreach (var song in songs)
            song.Favorite = true;
    }

    /// <summary>Opens the "Add to Playlist" dialog for <paramref name="songs"/>.</summary>
    public static async Task AddToPlaylistAsync(XamlRoot? root, IEnumerable<CoreSong> songs)
    {
        if (root is null)
            return;

        var list = songs.ToList();
        if (list.Count == 0)
            return;

        if (App.MainWindow is { } window)
            await window.ShowDialogAsync(new PlaylistDialog(list));
    }

    /// <summary>Shows the file in File Explorer.</summary>
    public static void OpenLocation(CoreSong song)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{song.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot open the location of {song.FilePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a song from a user playlist, or from Favorite Songs (unstarring it), with
    /// an undo toast (songtoast.py). Undo puts it back at the same position.
    /// </summary>
    public static void RemoveFromPlaylist(Playlist playlist, int index)
    {
        if (!playlist.IsEditable || index < 0 || index >= playlist.Songs.Count)
            return;

        var model = App.Services.Model;
        var song = playlist.Songs[index];
        model.RemoveFromPlaylist(playlist, index);

        var toast = new Toast(Strings.SongRemovedFrom(song.Title, playlist.Title)) { ButtonLabel = Strings.Undo };
        toast.ButtonClicked += () => model.InsertIntoPlaylist(playlist, index, song);
        App.MainWindow?.ShowToast(toast);
    }

    /// <summary>
    /// Deletes a user playlist with an undo toast (playlisttoast.py). The playlist is
    /// hidden at once and deleted for good once the toast goes away. If it is being
    /// played, playback stops first.
    /// </summary>
    public static void DeletePlaylist(UserPlaylist playlist)
    {
        var model = App.Services.Model;
        if (ReferenceEquals(Player.Queue.Source, playlist) && Player.CurrentSong is not null)
            Player.Stop();

        model.StagePlaylistDeletion(playlist);
        bool undone = false;

        var toast = new Toast(Strings.PlaylistRemoved(playlist.Title)) { ButtonLabel = Strings.Undo };
        toast.ButtonClicked += () =>
        {
            undone = true;
            model.UndoPlaylistDeletion(playlist);
        };
        toast.Dismissed += () =>
        {
            if (!undone)
                model.FinishPlaylistDeletion(playlist);
        };
        App.MainWindow?.ShowToast(toast);
    }
}
