// SPDX-License-Identifier: GPL-2.0-or-later
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// All albums of an artist (widgets/artistalbumswidget.py). Clicking a song plays
/// every song of the artist from that song; an album's own play button plays the album.
/// </summary>
public sealed partial class ArtistAlbumsWidget : UserControl
{
    public static readonly DependencyProperty ArtistProperty = DependencyProperty.Register(
        nameof(Artist), typeof(CoreArtist), typeof(ArtistAlbumsWidget), new PropertyMetadata(null, OnArtistChanged));

    public ArtistAlbumsWidget()
    {
        InitializeComponent();
        AlbumsRepeater.ElementPrepared += (_, args) =>
        {
            if (args.Element is AlbumWidget widget)
                widget.Artist = Artist;
        };
    }

    public CoreArtist? Artist
    {
        get => (CoreArtist?)GetValue(ArtistProperty);
        set => SetValue(ArtistProperty, value);
    }

    private static void OnArtistChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var widget = (ArtistAlbumsWidget)d;
        widget.AlbumsRepeater.ItemsSource = (e.NewValue as CoreArtist)?.Albums;
        widget.Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }
}
