// SPDX-License-Identifier: GPL-2.0-or-later
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>A playlist row of the playlists sidebar (widgets/playlisttile.py).</summary>
public sealed partial class PlaylistTile : UserControl
{
    public static readonly DependencyProperty PlaylistProperty = DependencyProperty.Register(
        nameof(Playlist), typeof(Playlist), typeof(PlaylistTile), new PropertyMetadata(null));

    public PlaylistTile()
    {
        InitializeComponent();
    }

    public Playlist? Playlist
    {
        get => (Playlist?)GetValue(PlaylistProperty);
        set => SetValue(PlaylistProperty, value);
    }
}
