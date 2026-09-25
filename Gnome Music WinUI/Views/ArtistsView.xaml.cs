// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.ComponentModel;
using System.Linq;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Views;

/// <summary>
/// Artists sidebar and the albums of the selected artist (views/artistsview.py).
/// The first artist is selected automatically.
/// </summary>
public sealed partial class ArtistsView : UserControl
{
    private string? _selectedName;

    public ArtistsView()
    {
        InitializeComponent();
        Model.PropertyChanged += OnModelPropertyChanged;
        Loaded += (_, _) => RestoreSelection();
    }

    public CoreModel Model { get; } = App.Services.Model;

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CoreModel.Artists))
            DispatcherQueue.TryEnqueue(RestoreSelection);
    }

    /// <summary>Keeps the selected artist across library updates, else selects the first.</summary>
    private void RestoreSelection()
    {
        var artists = Model.Artists;
        if (artists.Count == 0)
        {
            AlbumsPane.Artist = null;
            return;
        }

        var artist = artists.FirstOrDefault(a => a.Name == _selectedName) ?? artists[0];
        if (!ReferenceEquals(ArtistsList.SelectedItem, artist))
            ArtistsList.SelectedItem = artist;
        else
            AlbumsPane.Artist = artist;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ArtistsList.SelectedItem is CoreArtist artist)
        {
            _selectedName = artist.Name;
            AlbumsPane.Artist = artist;
        }
    }

    /// <summary>AdwOverlaySplitView: sidebar at 25 % of the width, 180–280 px.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        SidebarColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width * 0.25, 180, 280));
}
