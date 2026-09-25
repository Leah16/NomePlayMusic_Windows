// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Specialized;
using System.ComponentModel;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// A playlist with its controls (widgets/playlistswidget.py + playlistcontrols.py).
/// User playlists can be renamed inline, deleted, and reordered by drag and drop.
/// </summary>
public sealed partial class PlaylistsWidget : UserControl
{
    public static readonly DependencyProperty PlaylistProperty = DependencyProperty.Register(
        nameof(Playlist), typeof(Playlist), typeof(PlaylistsWidget), new PropertyMetadata(null, OnPlaylistChanged));

    public PlaylistsWidget()
    {
        InitializeComponent();
        Unloaded += (_, _) => Watch(null);
        Loaded += (_, _) => Watch(Playlist);
    }

    /// <summary>True while a playlist is being renamed (suppresses type-to-search and Ctrl+F).</summary>
    public static bool RenameActive { get; private set; }

    public Playlist? Playlist
    {
        get => (Playlist?)GetValue(PlaylistProperty);
        set => SetValue(PlaylistProperty, value);
    }

    private Playlist? _watched;

    private static void OnPlaylistChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var widget = (PlaylistsWidget)d;
        widget.FinishRename(commit: false);
        widget.Watch(e.NewValue as Playlist);

        var playlist = e.NewValue as Playlist;
        bool user = playlist is UserPlaylist;
        widget.SongsList.CanReorderItems = user;
        widget.SongsList.CanDragItems = user;
        widget.SongsList.AllowDrop = user;
        widget.SongsList.ItemsSource = playlist?.Songs;
        widget.Update();
    }

    private void Watch(Playlist? playlist)
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnPlaylistPropertyChanged;
            _watched.Songs.CollectionChanged -= OnSongsChanged;
        }

        _watched = playlist;
        if (playlist is not null)
        {
            playlist.PropertyChanged += OnPlaylistPropertyChanged;
            playlist.Songs.CollectionChanged += OnSongsChanged;
        }
    }

    private void OnPlaylistPropertyChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void OnSongsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Update();

    private void Update()
    {
        var playlist = Playlist;
        TitleText.Text = playlist?.Title ?? "";
        ToolTipService.SetToolTip(TitleText, playlist?.Title);
        CountText.Text = playlist?.CountText ?? "";
        PlayButton.IsEnabled = playlist?.Count > 0;
        MenuButton.IsEnabled = playlist is not null;

        // No empty card for an empty playlist.
        SongsList.Visibility = playlist?.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is SongRow row)
        {
            row.Playlist = Playlist;
            row.IsDraggable = Playlist is UserPlaylist;
        }
    }

    // Playing

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Playlist is { } playlist)
            SongActions.PlayPlaylist(playlist);
    }

    private void OnSongClick(object sender, ItemClickEventArgs e)
    {
        if (Playlist is { } playlist && e.ClickedItem is CoreSong song)
            SongActions.PlayPlaylist(playlist, song);
    }

    private void OnSongPlayRequested(object? sender, CoreSong song)
    {
        if (Playlist is { } playlist)
            SongActions.PlayPlaylist(playlist, song);
    }

    // Menu: Play / Delete / Rename… (Delete and Rename are disabled for smart playlists)

    private void OnMenuOpening(object? sender, object e)
    {
        bool user = Playlist is UserPlaylist;
        PlayItem.IsEnabled = Playlist?.Count > 0;
        DeleteItem.IsEnabled = user;
        RenameItem.IsEnabled = user;
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Playlist is UserPlaylist playlist)
            SongActions.DeletePlaylist(playlist);
    }

    // Editing

    private void OnSongRemoveRequested(object? sender, CoreSong song)
    {
        if (Playlist is not UserPlaylist playlist || sender is not DependencyObject row)
            return;

        int index = IndexOfRow(row);
        SongActions.RemoveFromPlaylist(playlist, index >= 0 ? index : playlist.Songs.IndexOf(song));
    }

    /// <summary>The list already moved the song; store the new order.</summary>
    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (Playlist is UserPlaylist playlist)
            App.Services.Model.SyncPaths(playlist);
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (Playlist is not UserPlaylist playlist)
            return;

        RenameActive = true;
        TitleText.Visibility = Visibility.Collapsed;
        RenamePanel.Visibility = Visibility.Visible;
        RenameEntry.Text = playlist.Title;
        RenameEntry.SelectAll();
        RenameEntry.Focus(FocusState.Programmatic);
    }

    private void OnRenameTextChanged(object sender, TextChangedEventArgs e) =>
        RenameDoneButton.IsEnabled = RenameEntry.Text.Length > 0;

    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            FinishRename(commit: true);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            FinishRename(commit: false);
            e.Handled = true;
        }
    }

    private void OnRenameDoneClick(object sender, RoutedEventArgs e) => FinishRename(commit: true);

    private void FinishRename(bool commit)
    {
        if (commit && Playlist is UserPlaylist playlist && RenameEntry.Text.Trim().Length > 0)
            App.Services.Model.RenamePlaylist(playlist, RenameEntry.Text);

        RenameActive = false;
        RenamePanel.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        Update();
    }

    private int IndexOfRow(DependencyObject element)
    {
        var current = element;
        while (current is not null and not ListViewItem)
            current = VisualTreeHelper.GetParent(current);

        return current is ListViewItem item ? SongsList.IndexFromContainer(item) : -1;
    }
}
