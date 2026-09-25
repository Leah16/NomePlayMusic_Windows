// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.ComponentModel;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// Volume popover with a mute toggle and a slider (widgets/volumebutton.py). The
/// slider works on the cubic scale; the player volume is linear = cubic³.
/// </summary>
public sealed partial class VolumeButton : Button
{
    private readonly Player _player = App.Services.Player;
    private readonly FontIcon _icon = new() { FontSize = 16 };
    private readonly Slider _slider;
    private readonly ToggleButton _mute;
    private bool _updating;

    public VolumeButton()
    {
        Style = (Style)Application.Current.Resources["SubtleIconButtonStyle"];
        Content = _icon;
        AutomationProperties.SetName(this, Strings.AdjustVolume);
        ToolTipService.SetToolTip(this, Strings.AdjustVolume);

        // A standard Fluent toggle button: accent filled while muted.
        _mute = new ToggleButton
        {
            Content = new FontIcon { FontSize = 16, Glyph = "" },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_mute, Strings.MuteUnmute);
        ToolTipService.SetToolTip(_mute, Strings.MuteUnmute);
        _mute.Click += (_, _) => SetMuted(_mute.IsChecked == true);

        _slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            StepFrequency = 0.01,
            SmallChange = 0.01,
            LargeChange = 0.1,
            IsThumbToolTipEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_slider, Strings.AdjustVolume);
        _slider.ValueChanged += OnSliderValueChanged;

        var panel = new Grid { Width = 250, ColumnSpacing = 8 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_slider, 1);
        panel.Children.Add(_mute);
        panel.Children.Add(_slider);
        Flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top };

        _player.PropertyChanged += OnPlayerPropertyChanged;
        PointerWheelChanged += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0)
                _player.IncreaseVolume();
            else
                _player.DecreaseVolume();
            e.Handled = true;
        };
        Update();
    }

    private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating)
            return;

        // Moving the slider to 0 mutes; raising it from 0 unmutes.
        _player.CubicVolume = e.NewValue;
        if (e.NewValue <= 0)
            _player.Muted = true;
        else if (_player.Muted)
            _player.Muted = false;
    }

    private void SetMuted(bool muted)
    {
        // Unmuting at volume 0 restores a cubic volume of 0.25.
        if (!muted && _player.Volume <= 0)
            _player.CubicVolume = 0.25;
        _player.Muted = muted;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Player.Volume) or nameof(Player.Muted))
            Update();
    }

    private void Update()
    {
        double cubic = _player.CubicVolume;
        _updating = true;
        _slider.Value = _player.Muted ? 0 : cubic;
        _mute.IsChecked = _player.Muted;
        _updating = false;

        // audio-volume-{muted,low,medium,high}-symbolic
        _icon.Glyph = _player.Muted ? ""
            : cubic < 0.3 ? ""
            : cubic < 0.7 ? ""
            : "";
    }
}
