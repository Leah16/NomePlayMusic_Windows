// SPDX-License-Identifier: GPL-2.0-or-later
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>An artist row of the artists sidebar (widgets/artisttile.py).</summary>
public sealed partial class ArtistTile : UserControl
{
    public static readonly DependencyProperty ArtistProperty = DependencyProperty.Register(
        nameof(Artist), typeof(CoreArtist), typeof(ArtistTile), new PropertyMetadata(null));

    public ArtistTile()
    {
        InitializeComponent();
    }

    public CoreArtist? Artist
    {
        get => (CoreArtist?)GetValue(ArtistProperty);
        set => SetValue(ArtistProperty, value);
    }
}
