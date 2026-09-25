// SPDX-License-Identifier: GPL-2.0-or-later
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Gnome_Music_WinUI.Views;

/// <summary>An album with its songs (widgets/albumnavigationpage.py).</summary>
public sealed partial class AlbumPage : Page
{
    public AlbumPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is CoreAlbum album)
            AlbumView.Album = album;
    }
}
