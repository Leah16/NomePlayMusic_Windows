// SPDX-License-Identifier: GPL-2.0-or-later
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>An album in the albums grid and in search results (widgets/albumtile.py).</summary>
public sealed partial class AlbumTile : UserControl
{
    public static readonly DependencyProperty AlbumProperty = DependencyProperty.Register(
        nameof(Album), typeof(CoreAlbum), typeof(AlbumTile), new PropertyMetadata(null));

    /// <summary>ArtSize.MEDIUM.</summary>
    public static readonly DependencyProperty CoverSizeProperty = DependencyProperty.Register(
        nameof(CoverSize), typeof(double), typeof(AlbumTile), new PropertyMetadata(192.0));

    public AlbumTile()
    {
        InitializeComponent();
    }

    public CoreAlbum? Album
    {
        get => (CoreAlbum?)GetValue(AlbumProperty);
        set => SetValue(AlbumProperty, value);
    }

    public double CoverSize
    {
        get => (double)GetValue(CoverSizeProperty);
        set => SetValue(CoverSizeProperty, value);
    }

    public double TileWidth(double coverSize) => coverSize + 12;

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
