// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.ComponentModel;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// A song row (widgets/songwidget.py). Variant A (album discs) shows the track
/// number; variant B (playlists, search) shows artist and album columns instead.
/// The row reflects the song's playback state: bold with a ▶ while playing,
/// dimmed once played, dimmed with an error icon when it failed to play.
/// Activating the row is handled by the list hosting it.
/// </summary>
public sealed partial class SongRow : UserControl
{
    public static readonly DependencyProperty SongProperty = DependencyProperty.Register(
        nameof(Song), typeof(CoreSong), typeof(SongRow), new PropertyMetadata(null, OnSongChanged));

    public static readonly DependencyProperty ShowArtistAndAlbumProperty = DependencyProperty.Register(
        nameof(ShowArtistAndAlbum), typeof(bool), typeof(SongRow), new PropertyMetadata(false, OnLayoutChanged));

    public static readonly DependencyProperty IsDraggableProperty = DependencyProperty.Register(
        nameof(IsDraggable), typeof(bool), typeof(SongRow), new PropertyMetadata(false, OnLayoutChanged));

    private CoreSong? _subscribed;

    public SongRow()
    {
        InitializeComponent();
        ContextFlyout = RowMenu;
        Loaded += (_, _) => Subscribe(Song);
        Unloaded += (_, _) => Subscribe(null);
        ApplyLayout();
    }

    /// <summary>Raised by "Play" in the menu; the host plays the song in its context.</summary>
    public event EventHandler<CoreSong>? PlayRequested;

    /// <summary>Raised by "Remove from Playlist".</summary>
    public event EventHandler<CoreSong>? RemoveRequested;

    public CoreSong? Song
    {
        get => (CoreSong?)GetValue(SongProperty);
        set => SetValue(SongProperty, value);
    }

    /// <summary>Variant B: artist and album columns, no track number.</summary>
    public bool ShowArtistAndAlbum
    {
        get => (bool)GetValue(ShowArtistAndAlbumProperty);
        set => SetValue(ShowArtistAndAlbumProperty, value);
    }

    /// <summary>Shows the drag handle (rows of user playlists).</summary>
    public bool IsDraggable
    {
        get => (bool)GetValue(IsDraggableProperty);
        set => SetValue(IsDraggableProperty, value);
    }

    /// <summary>
    /// The playlist the row belongs to. "Remove from Playlist" is shown for playlist
    /// rows and only enabled for user playlists.
    /// </summary>
    public Playlist? Playlist { get; set; }

    private static void OnSongChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (SongRow)d;
        if (row.IsLoaded)
            row.Subscribe(e.NewValue as CoreSong);
        else
            row.Update();
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SongRow)d).ApplyLayout();

    private void Subscribe(CoreSong? song)
    {
        if (_subscribed is not null)
            _subscribed.PropertyChanged -= OnSongPropertyChanged;
        _subscribed = song;
        if (song is not null)
            song.PropertyChanged += OnSongPropertyChanged;
        Star.Song = song;
        Update();
    }

    private void OnSongPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CoreSong.State) or nameof(CoreSong.Validation)
            or nameof(CoreSong.Title) or nameof(CoreSong.Artist) or nameof(CoreSong.AlbumTitle)
            or nameof(CoreSong.DurationText) or nameof(CoreSong.TrackNumberText))
        {
            Update();
        }
    }

    private void ApplyLayout()
    {
        bool b = ShowArtistAndAlbum;
        ArtistColumn.Width = b ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AlbumColumn.Width = b ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ArtistText.Visibility = b ? Visibility.Visible : Visibility.Collapsed;
        AlbumText.Visibility = b ? Visibility.Visible : Visibility.Collapsed;
        HandleColumn.Width = IsDraggable ? GridLength.Auto : new GridLength(0);
        DragHandle.Visibility = IsDraggable ? Visibility.Visible : Visibility.Collapsed;
        Update();
    }

    private void Update()
    {
        var song = Song;
        if (song is null)
        {
            TitleText.Text = ArtistText.Text = AlbumText.Text = DurationText.Text = NumberText.Text = "";
            PlayIcon.Visibility = Visibility.Collapsed;
            VisualStateManager.GoToState(this, "Idle", false);
            return;
        }

        TitleText.Text = song.Title;
        ToolTipService.SetToolTip(TitleText, song.Title);
        DurationText.Text = song.DurationText;
        NumberText.Text = ShowArtistAndAlbum ? "" : song.TrackNumberText;
        if (ShowArtistAndAlbum)
        {
            ArtistText.Text = song.Artist;
            AlbumText.Text = song.HasAlbum ? song.AlbumTitle : "";
        }

        bool failed = song.IsFailed;
        bool playing = song.State == SongState.Playing;
        PlayIcon.Glyph = failed ? "" : "";
        PlayIcon.Visibility = failed || playing ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(PlayIcon, NumberText.Text.Length == 0 ? 2 : 1);

        TitleText.FontWeight = playing && !failed ? FontWeights.SemiBold : FontWeights.Normal;
        TitleText.Opacity = failed || song.State == SongState.Played ? 0.55 : 1.0;
        VisualStateManager.GoToState(this, failed ? "Failed" : playing ? "Playing" : "Idle", false);
    }

    // Menu

    private void OnMenuOpening(object? sender, object e)
    {
        RemoveItem.Visibility = Playlist is { IsEditable: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Song is { } song)
            PlayRequested?.Invoke(this, song);
    }

    private async void OnAddToPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (Song is { } song)
            await SongActions.AddToPlaylistAsync(XamlRoot, new[] { song });
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Song is { } song)
            RemoveRequested?.Invoke(this, song);
    }

    private void OnOpenLocationClick(object sender, RoutedEventArgs e)
    {
        if (Song is { } song)
            SongActions.OpenLocation(song);
    }

    /// <summary>The song's file, audio stream and tags, read only (not in GNOME Music).</summary>
    private void OnPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (Song is { } song)
            App.MainWindow?.ShowSongProperties(song);
    }
}
