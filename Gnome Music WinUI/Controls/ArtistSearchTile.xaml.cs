// SPDX-License-Identifier: GPL-2.0-or-later
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>An artist in search results (widgets/artistsearchtile.py).</summary>
public sealed partial class ArtistSearchTile : UserControl
{
    public static readonly DependencyProperty ArtistProperty = DependencyProperty.Register(
        nameof(Artist), typeof(CoreArtist), typeof(ArtistSearchTile), new PropertyMetadata(null));

    public ArtistSearchTile()
    {
        InitializeComponent();
    }

    public CoreArtist? Artist
    {
        get => (CoreArtist?)GetValue(ArtistProperty);
        set => SetValue(ArtistProperty, value);
    }
}
