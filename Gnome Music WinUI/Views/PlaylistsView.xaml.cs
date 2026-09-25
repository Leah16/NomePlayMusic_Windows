// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Gnome_Music_WinUI.Views;

/// <summary>The line between the app's playlists and the user's in the sidebar.</summary>
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
/// Playlists sidebar and the selected playlist (views/playlistsview.py). The app's
/// playlists come first (Favorite Songs, Recently Played), then a separator and the
/// user playlists, newest first; the first playlist is selected automatically. Not in
/// GNOME Music: a playlist is created, named, from the sidebar's foot, and a
/// right-click on one plays it, or renames or deletes one of the user's.
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
        foreach (var playlist in _model.Playlists.Where(p => p.IsSystem))
            _items.Add(playlist);

        _items.Add(SidebarSeparator.Instance);
        foreach (var playlist in _model.Playlists.OfType<UserPlaylist>())
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
        PlaylistPane.Playlist = PlaylistsList.SelectedItem as Playlist;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        bool separator = args.Item is SidebarSeparator;
        args.ItemContainer.IsEnabled = !separator;
        args.ItemContainer.MinHeight = separator ? 0 : 36;
        args.ItemContainer.IsTabStop = !separator;
        AutomationProperties.SetAccessibilityView(args.ItemContainer, separator ? AccessibilityView.Raw : AccessibilityView.Content);   // no "SidebarSeparator" for screen readers
        args.ItemContainer.ContextFlyout = args.Item is Playlist playlist ? Menu(playlist, args.ItemContainer) : null;
    }

    /// <summary>A playlist's right-click menu: Play, and for the user's Rename… and Delete.</summary>
    private static MenuFlyout Menu(Playlist playlist, FrameworkElement row)
    {
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = Strings.MenuPlay };
        play.Click += (_, _) => SongActions.PlayPlaylist(playlist);
        menu.Items.Add(play);
        menu.Opening += (_, _) => play.IsEnabled = playlist.Count > 0;

        if (playlist is UserPlaylist user)
        {
            var rename = new MenuFlyoutItem { Text = Strings.MenuRename };
            rename.Click += (_, _) => AskName(row, user.Title, Strings.Rename, name => App.Services.Model.RenamePlaylist(user, name));
            menu.Items.Add(rename);

            var delete = new MenuFlyoutItem { Text = Strings.MenuDelete };
            delete.Click += (_, _) => SongActions.DeletePlaylist(user);
            menu.Items.Add(delete);
        }

        return menu;
    }

    /// <summary>A new playlist, named first; it shows once created.</summary>
    private void OnNewPlaylistClick(object sender, RoutedEventArgs e) =>
        AskName(NewPlaylistButton, "", Strings.Create, name => Select(_model.CreatePlaylist(name)));

    /// <summary>
    /// A playlist's name, asked in a flyout at <paramref name="anchor"/>: the entry and
    /// the action, joined like the rename entry of the playlist's page. Enter confirms;
    /// an empty name cannot be.
    /// </summary>
    private static void AskName(FrameworkElement anchor, string name, string action, Action<string> done)
    {
        var entry = new TextBox
        {
            Text = name,
            Width = 220,
            PlaceholderText = Strings.PlaylistName,
            CornerRadius = new CornerRadius(4, 0, 0, 4),
        };
        AutomationProperties.SetName(entry, Strings.PlaylistName);
        var button = new Button
        {
            Content = action,
            IsEnabled = name.Trim().Length > 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(0, 4, 4, 0),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(entry);
        panel.Children.Add(button);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom };

        void Confirm()
        {
            if (entry.Text.Trim().Length == 0)
                return;
            flyout.Hide();
            done(entry.Text.Trim());
        }

        entry.TextChanged += (_, _) => button.IsEnabled = entry.Text.Trim().Length > 0;
        entry.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Confirm();
            }
        };
        button.Click += (_, _) => Confirm();
        flyout.Opened += (_, _) =>
        {
            entry.SelectAll();
            entry.Focus(FocusState.Programmatic);
        };
        flyout.ShowAt(anchor);
    }

    /// <summary>AdwOverlaySplitView: 25 % of the width, 220–280 px for playlists.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        SidebarColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width * 0.25, 220, 280));
}
