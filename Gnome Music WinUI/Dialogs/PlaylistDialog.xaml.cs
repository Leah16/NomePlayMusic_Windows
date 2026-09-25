// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;
using System.Linq;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Gnome_Music_WinUI.Dialogs;

/// <summary>
/// "Add to Playlist" (widgets/playlistdialog.py): pick a user playlist, or create
/// one, and append the songs to it. The port also offers Favorite Songs (it stars them).
/// </summary>
public sealed partial class PlaylistDialog : ContentDialog
{
    private readonly IReadOnlyList<CoreSong> _songs;
    private readonly CoreModel _model = App.Services.Model;

    public PlaylistDialog(IReadOnlyList<CoreSong> songs)
    {
        _songs = songs;
        InitializeComponent();

        var playlists = _model.EditablePlaylists.ToList();
        bool empty = playlists.Count == 0;
        EmptyBox.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NormalBox.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        PlaylistsList.ItemsSource = playlists;
        Opened += (_, _) => (empty ? FirstPlaylistEntry : (Control)PlaylistsList).Focus(FocusState.Programmatic);
    }

    // State A: first playlist

    private void OnFirstEntryTextChanged(object sender, TextChangedEventArgs e) =>
        CreateButton.IsEnabled = FirstPlaylistEntry.Text.Length > 0;

    private void OnFirstEntryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && FirstPlaylistEntry.Text.Length > 0)
        {
            e.Handled = true;
            CreateAndClose(FirstPlaylistEntry.Text);
        }
    }

    private void OnCreateClick(object sender, RoutedEventArgs e) => CreateAndClose(FirstPlaylistEntry.Text);

    // State B: existing playlists

    private void OnPlaylistSelected(object sender, SelectionChangedEventArgs e)
    {
        bool selected = PlaylistsList.SelectedItem is Playlist;
        IsPrimaryButtonEnabled = selected;
        if (selected)
        {
            // Selecting a row clears the new playlist entry.
            NewPlaylistEntry.Text = "";
            NewPlaylistButton.IsEnabled = false;
        }
    }

    /// <summary>Focusing the entry deselects the rows.</summary>
    private void OnNewEntryFocused(object sender, RoutedEventArgs e) => PlaylistsList.SelectedItem = null;

    private void OnNewEntryTextChanged(object sender, TextChangedEventArgs e) =>
        NewPlaylistButton.IsEnabled = NewPlaylistEntry.Text.Length > 0;

    private void OnNewEntryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && NewPlaylistEntry.Text.Length > 0)
        {
            e.Handled = true;
            CreateAndClose(NewPlaylistEntry.Text);
        }
    }

    private void OnNewPlaylistClick(object sender, RoutedEventArgs e) => CreateAndClose(NewPlaylistEntry.Text);

    private void OnAddClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (PlaylistsList.SelectedItem is Playlist playlist)
            _model.AddToPlaylist(playlist, _songs);
    }

    /// <summary>Creates a playlist with that exact title, adds the songs and closes.</summary>
    private void CreateAndClose(string title)
    {
        if (title.Length == 0)
            return;

        _model.CreatePlaylist(title, _songs);
        Hide();
    }
}
