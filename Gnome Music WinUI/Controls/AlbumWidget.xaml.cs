// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// An album with its discs (widgets/albumwidget.py). Used by the album page and,
/// without the artist line, for every album of the artists pane. Clicking a song
/// plays the <see cref="QueueSource"/> (the album, or the artist in the artists pane).
/// </summary>
public sealed partial class AlbumWidget : UserControl
{
    public static readonly DependencyProperty AlbumProperty = DependencyProperty.Register(
        nameof(Album), typeof(CoreAlbum), typeof(AlbumWidget), new PropertyMetadata(null, OnAlbumChanged));

    public static readonly DependencyProperty ShowArtistLabelProperty = DependencyProperty.Register(
        nameof(ShowArtistLabel), typeof(bool), typeof(AlbumWidget), new PropertyMetadata(true, OnAlbumChanged));

    public static readonly DependencyProperty ArtistProperty = DependencyProperty.Register(
        nameof(Artist), typeof(CoreArtist), typeof(AlbumWidget), new PropertyMetadata(null));

    private readonly CoverTintBrushes _playTint = new();

    public AlbumWidget()
    {
        InitializeComponent();
        _playTint.Add(PlayButton.Resources, CoverTintBrushes.AccentButtonKeys);
    }

    public CoreAlbum? Album
    {
        get => (CoreAlbum?)GetValue(AlbumProperty);
        set => SetValue(AlbumProperty, value);
    }

    /// <summary>show_artist_label: false inside the artists pane.</summary>
    public bool ShowArtistLabel
    {
        get => (bool)GetValue(ShowArtistLabelProperty);
        set => SetValue(ShowArtistLabelProperty, value);
    }

    /// <summary>active_coreobject in the artists pane: songs are played from the whole artist.</summary>
    public CoreArtist? Artist
    {
        get => (CoreArtist?)GetValue(ArtistProperty);
        set => SetValue(ArtistProperty, value);
    }

    private static void OnAlbumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AlbumWidget)d).Update();

    private void Update()
    {
        var album = Album;
        Cover.Album = album;
        DiscList.ItemsSource = album?.Discs;

        // On the album page play is the primary action: an accent button in the color
        // of the cover. In the artists pane, where every album has one, a standard button.
        PlayButton.Style = (Style)Application.Current.Resources[
            ShowArtistLabel ? "LargeAccentIconButtonStyle" : "LargeIconButtonStyle"];
        if (album is null)
            return;

        if (ShowArtistLabel)
            _playTint.Show(album, animate: false);

        TitleText.Text = album.Title;
        ToolTipService.SetToolTip(TitleText, album.Title);
        ArtistText.Text = album.Artist;
        ToolTipService.SetToolTip(ArtistText, album.Artist);
        ArtistText.Visibility = ShowArtistLabel ? Visibility.Visible : Visibility.Collapsed;
        ReleasedText.Text = ReleasedLabel(album);
        ComposerText.Text = album.Composer ?? "";
        ToolTipService.SetToolTip(ComposerText, album.Composer);
        ComposerText.Visibility = string.IsNullOrEmpty(album.Composer) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// "2005, 71 minutes" (albumwidget.py): the minutes are floor(seconds / 60) + 1.
    /// </summary>
    public static string ReleasedLabel(CoreAlbum album)
    {
        long mins = (long)Math.Floor(album.Duration / 60) + 1;
        string minsText = Strings.Minutes(mins);
        return album.Year > 0 ? $"{album.Year}, {minsText}" : minsText;
    }

    private void OnSongActivated(object? sender, CoreSong song)
    {
        if (Artist is { } artist)
            SongActions.PlayArtist(artist, song);
        else if (Album is { } album)
            SongActions.PlayAlbum(album, song);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Album is { } album)
            SongActions.PlayAlbum(album);
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (Album is { } album)
            SongActions.AddToFavorites(album.Songs);
    }

    private async void OnAddToPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (Album is { } album)
            await SongActions.AddToPlaylistAsync(XamlRoot, album.Songs);
    }
}
