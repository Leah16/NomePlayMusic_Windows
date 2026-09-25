// SPDX-License-Identifier: GPL-2.0-or-later
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Gnome_Music_WinUI.Views;

/// <summary>The albums of an artist opened from search (widgets/artistnavigationpage.py).</summary>
public sealed partial class ArtistPage : Page
{
    public ArtistPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is CoreArtist artist)
            AlbumsPane.Artist = artist;
    }
}
