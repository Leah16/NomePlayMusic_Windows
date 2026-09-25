// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Views;

/// <summary>The line between smart and user playlists in the sidebar.</summary>
public sealed class SidebarSeparator
{
    public static readonly SidebarSeparator Instance = new();
}

public sealed partial class SidebarTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PlaylistTemplate { get; set; }

    public DataTemplate? SeparatorTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is SidebarSeparator ? SeparatorTemplate : PlaylistTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// Playlists sidebar and the selected playlist (views/playlistsview.py). Smart
/// playlists come first, then a separator and the user playlists, newest first.
/// The first playlist is selected automatically; smart playlists are refreshed
/// whenever they are opened.
/// </summary>
public sealed partial class PlaylistsView : UserControl
{
    private readonly CoreModel _model = App.Services.Model;
    private readonly ObservableCollection<object> _items = new();
    private int _selectedIndex = -1;
    private bool _rebuilding;

    public PlaylistsView()
    {
        InitializeComponent();
        PlaylistsList.ItemsSource = _items;
        _model.Playlists.CollectionChanged += OnPlaylistsChanged;
        App.Services.Player.PropertyChanged += (_, e) =>
        {
            // Follow the playlist being played (e.g. started from the media controls).
            if (e.PropertyName == nameof(Player.CurrentSong) && App.Services.Player.Queue.Source is Playlist playing
                && PlaylistsList.SelectedItem != playing && _items.Contains(playing))
            {
                PlaylistsList.SelectedItem = playing;
            }
        };
        Rebuild();
    }

    /// <summary>Selects and shows a playlist.</summary>
    public void Select(Playlist playlist)
    {
        if (_items.Contains(playlist))
            PlaylistsList.SelectedItem = playlist;
    }

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var selected = PlaylistsList.SelectedItem as Playlist;
        int previousIndex = _selectedIndex;

        _rebuilding = true;
        _items.Clear();
        foreach (var playlist in _model.Playlists.OfType<SmartPlaylist>())
            _items.Add(playlist);

        var user = _model.Playlists.OfType<UserPlaylist>().ToList();
        if (user.Count > 0)
            _items.Add(SidebarSeparator.Instance);
        foreach (var playlist in user)
            _items.Add(playlist);
        _rebuilding = false;

        // Keep the selection; after a deletion select the row now at the same
        // position, or the previous one.
        object? target = selected is not null && _items.Contains(selected)
            ? selected
            : _items.Count == 0 ? null : _items[Math.Clamp(previousIndex < 0 ? 0 : previousIndex, 0, _items.Count - 1)];
        if (target is SidebarSeparator)
            target = _items.ElementAtOrDefault(_items.IndexOf(target) - 1) ?? _items.ElementAtOrDefault(_items.IndexOf(target) + 1);

        PlaylistsList.SelectedItem = target;
        if (target is Playlist same && ReferenceEquals(same, selected))
            PlaylistPane.Playlist = same;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding)
            return;

        if (PlaylistsList.SelectedItem is SidebarSeparator)
        {
            PlaylistsList.SelectedIndex = _selectedIndex;
            return;
        }

        _selectedIndex = PlaylistsList.SelectedIndex;
        if (PlaylistsList.SelectedItem is Playlist playlist)
        {
            if (playlist is SmartPlaylist smart)
                _model.RefreshSmartPlaylist(smart);
            PlaylistPane.Playlist = playlist;
        }
        else
        {
            PlaylistPane.Playlist = null;
        }
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        bool separator = args.Item is SidebarSeparator;
        args.ItemContainer.IsEnabled = !separator;
        args.ItemContainer.MinHeight = separator ? 0 : 36;
        args.ItemContainer.IsTabStop = !separator;
    }

    /// <summary>AdwOverlaySplitView: 25 % of the width, 220–280 px for playlists.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        SidebarColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width * 0.25, 220, 280));
}
