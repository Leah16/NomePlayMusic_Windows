// SPDX-License-Identifier: GPL-2.0-or-later
using System.ComponentModel;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// Menu button choosing the repeat mode (widgets/repeatmodebutton.py). The icon
/// shows the current mode.
/// </summary>
public sealed partial class RepeatModeButton : Button
{
    private readonly Player _player = App.Services.Player;
    private readonly FontIcon _icon = new() { FontSize = 16 };
    private readonly MenuFlyout _menu = new() { Placement = FlyoutPlacementMode.Top };

    public RepeatModeButton()
    {
        Style = (Style)Application.Current.Resources["SubtleIconButtonStyle"];
        Content = _icon;
        ToolTipService.SetToolTip(this, Strings.SetRepeatMode);
        AutomationProperties.SetName(this, Strings.SetRepeatMode);

        foreach (var mode in new[] { RepeatMode.None, RepeatMode.Song, RepeatMode.All, RepeatMode.Shuffle })
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = Label(mode),
                GroupName = "repeat",
                Tag = mode,
                Icon = new FontIcon { Glyph = Glyph(mode) },
            };
            item.Click += (_, _) => _player.RepeatMode = mode;
            _menu.Items.Add(item);
        }

        Flyout = _menu;
        _menu.Opening += (_, _) => SyncMenu();
        _player.PropertyChanged += OnPlayerPropertyChanged;
        Update();
    }

    /// <summary>Label of a mode (utils.py: RepeatMode labels).</summary>
    public static string Label(RepeatMode mode) => mode switch
    {
        RepeatMode.Song => Strings.RepeatSong,
        RepeatMode.All => Strings.RepeatAll,
        RepeatMode.Shuffle => Strings.RepeatShuffle,
        _ => Strings.RepeatNone,
    };

    /// <summary>
    /// Icon of a mode: media-playlist-{consecutive,repeat-song,repeat,shuffle}-symbolic.
    /// </summary>
    public static string Glyph(RepeatMode mode) => mode switch
    {
        RepeatMode.Song => "",
        RepeatMode.All => "",
        RepeatMode.Shuffle => "",
        _ => "",
    };

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Player.RepeatMode))
            Update();
    }

    private void Update()
    {
        _icon.Glyph = Glyph(_player.RepeatMode);
        SyncMenu();
    }

    private void SyncMenu()
    {
        foreach (var item in _menu.Items)
        {
            if (item is RadioMenuFlyoutItem radio)
                radio.IsChecked = (RepeatMode)radio.Tag == _player.RepeatMode;
        }
    }
}
