// SPDX-License-Identifier: GPL-2.0-or-later
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Gnome_Music_WinUI.Views;

/// <summary>All albums of a search (widgets/albumssearchnavigationpage.py).</summary>
public sealed partial class AlbumsSearchPage : Page
{
    public AlbumsSearchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SearchResults results)
            AlbumsGrid.ItemsSource = results.Albums;
    }

    private void OnAlbumClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CoreAlbum album)
            App.MainWindow?.ShowAlbum(album);
    }
}
