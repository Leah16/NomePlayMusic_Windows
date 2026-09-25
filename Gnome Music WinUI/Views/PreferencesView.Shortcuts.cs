// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;
using Gnome_Music_WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gnome_Music_WinUI.Views;

/// <summary>The keyboard shortcuts (data/ui/shortcuts-dialog.ui), in cards like the settings.</summary>
public sealed partial class PreferencesView
{
    /// <summary>Built each time the page shows: the shortcuts of features turned off are left out.</summary>
    private void BuildShortcuts()
    {
        ShortcutsPage.Children.Clear();
        AddShortcuts(Strings.ShortcutsGeneral, first: true, new[]
        {
            (Strings.ShortcutsPreferences, new[] { "Ctrl", "," }),
            (Strings.ShortcutsSearch, new[] { "Ctrl", "F" }),
            (Strings.ShortcutsHelp, new[] { "F1" }),
            (Strings.ShortcutsKeyboardShortcuts, new[] { "Ctrl", "?" }),
            (Strings.ShortcutsQuit, new[] { "Ctrl", "Q" }),
        });

        var playback = new List<(string, string[])>
        {
            (Strings.ShortcutsPlayPause, new[] { "Ctrl", "Space" }),
            (Strings.ShortcutsNextSong, new[] { "Ctrl", "N" }),
            (Strings.ShortcutsPreviousSong, new[] { "Ctrl", "B" }),
            (Strings.ShortcutsToggleRepeat, new[] { "Ctrl", "R" }),
            (Strings.ShortcutsToggleShuffle, new[] { "Ctrl", "S" }),
        };
        if (_settings.VolumeControlEnabled)
        {
            playback.Add((Strings.ShortcutsIncreaseVolume, new[] { "Ctrl", "+" }));
            playback.Add((Strings.ShortcutsDecreaseVolume, new[] { "Ctrl", "−" }));
            playback.Add((Strings.ShortcutsToggleMute, new[] { "Ctrl", "M" }));
        }

        AddShortcuts(Strings.ShortcutsPlayback, first: false, playback);

        var navigation = new List<(string, string[])>
        {
            (Strings.ShortcutsGoToAlbums, new[] { "Alt", "1" }),
            (Strings.ShortcutsGoToArtists, new[] { "Alt", "2" }),
            (Strings.ShortcutsGoToPlaylists, new[] { "Alt", "3" }),
            (Strings.ShortcutsGoBack, new[] { "Alt", "←" }),
        };
        AddShortcuts(Strings.ShortcutsNavigation, first: false, navigation);
    }

    /// <summary>A heading and a card of rows: what the shortcut does, and its keys.</summary>
    private void AddShortcuts(string title, bool first, IReadOnlyList<(string Label, string[] Keys)> items)
    {
        ShortcutsPage.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Resources[first ? "FirstGroupTitleStyle" : "GroupTitleStyle"],
        });

        var rows = new StackPanel();
        for (int i = 0; i < items.Count; i++)
        {
            var (label, keys) = items[i];
            if (i > 0)
                rows.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });

            var row = new Grid { Style = (Style)Resources["InfoRowStyle"] };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });

            var accelerator = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            foreach (var key in keys)
                accelerator.Children.Add(KeyCap(key));
            Grid.SetColumn(accelerator, 1);
            row.Children.Add(accelerator);
            rows.Children.Add(row);
        }

        ShortcutsPage.Children.Add(new Border { Style = (Style)Resources["GroupCardStyle"], Child = rows });
    }

    private static Border KeyCap(string key) => new()
    {
        Style = (Style)Application.Current.Resources["KeyCapStyle"],
        Child = new TextBlock
        {
            Text = key,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        },
    };
}
