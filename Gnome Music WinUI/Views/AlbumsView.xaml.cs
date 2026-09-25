// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Views;

/// <summary>Grid of all albums, sorted by title (views/albumsview.py).</summary>
public sealed partial class AlbumsView : UserControl
{
    /// <summary>192 (cover) + 12 (tile-image margins) + 12 (child padding) + 18 (child margins).</summary>
    private const double MinCellWidth = 234;
    private const int MaxColumns = 10;

    public AlbumsView()
    {
        InitializeComponent();

        // The items panel only exists once there are items; size it when it appears.
        AlbumsGrid.ContainerContentChanging += (_, _) =>
        {
            if (AlbumsGrid.ItemsPanelRoot is ItemsWrapGrid { ItemWidth: double.NaN })
                UpdateColumns(AlbumsGrid.ActualWidth);
        };
    }

    public CoreModel Model { get; } = App.Services.Model;

    /// <summary>
    /// Like GtkGridView: as many columns of at least 234 px as fit (1–10), with
    /// the cells stretched to fill the row.
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns(e.NewSize.Width);

    private void UpdateColumns(double width)
    {
        if (AlbumsGrid.ItemsPanelRoot is not ItemsWrapGrid panel || width <= 0)
            return;

        double available = width - AlbumsGrid.Padding.Left - AlbumsGrid.Padding.Right - 1;
        int columns = Math.Clamp((int)Math.Floor(available / MinCellWidth), 1, MaxColumns);
        panel.ItemWidth = Math.Floor(available / columns);
    }

    private void OnAlbumClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CoreAlbum album)
            App.MainWindow?.ShowAlbum(album);
    }
}
