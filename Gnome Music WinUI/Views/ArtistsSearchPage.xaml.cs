// SPDX-License-Identifier: GPL-2.0-or-later
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Gnome_Music_WinUI.Views;

/// <summary>All artists of a search (widgets/artistssearchnavigationpage.py).</summary>
public sealed partial class ArtistsSearchPage : Page
{
    public ArtistsSearchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SearchResults results)
            ArtistsGrid.ItemsSource = results.Artists;
    }

    private void OnArtistClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CoreArtist artist)
            App.MainWindow?.ShowArtist(artist);
    }
}
