// SPDX-License-Identifier: GPL-2.0-or-later
using System.ComponentModel;
using System.Numerics;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// The favorite star of a song (widgets/startoggle.py): an outline star in the
/// secondary text color, filled in the accent color (or <see cref="StarredForeground"/>)
/// when starred. After a click it spins in from ±72° like the CSS animation.
/// </summary>
public sealed partial class StarToggle : Button
{
    public static readonly DependencyProperty SongProperty = DependencyProperty.Register(
        nameof(Song), typeof(CoreSong), typeof(StarToggle), new PropertyMetadata(null, OnSongChanged));

    public static readonly DependencyProperty StarredForegroundProperty = DependencyProperty.Register(
        nameof(StarredForeground), typeof(Brush), typeof(StarToggle),
        new PropertyMetadata(null, (d, _) => ((StarToggle)d).Update()));

    private static readonly Style StarredStyle = (Style)Application.Current.Resources["AccentIconStyle"];
    private static readonly Style UnstarredStyle = (Style)Application.Current.Resources["SecondaryIconStyle"];

    private readonly FontIcon _icon = new() { FontSize = 16 };
    private CoreSong? _subscribed;

    public StarToggle()
    {
        Style = (Style)Application.Current.Resources["SubtleIconButtonStyle"];
        Content = _icon;
        Click += OnClick;

        // Subscribe only while loaded so rows of closed pages can be collected.
        Loaded += (_, _) => Subscribe(Song);
        Unloaded += (_, _) => Subscribe(null);
        Update();
    }

    public CoreSong? Song
    {
        get => (CoreSong?)GetValue(SongProperty);
        set => SetValue(SongProperty, value);
    }

    /// <summary>The color of the filled star instead of the accent color.</summary>
    public Brush? StarredForeground
    {
        get => (Brush?)GetValue(StarredForegroundProperty);
        set => SetValue(StarredForegroundProperty, value);
    }

    private static void OnSongChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var toggle = (StarToggle)d;
        if (toggle.IsLoaded)
            toggle.Subscribe(e.NewValue as CoreSong);
        else
            toggle.Update();
    }

    private void Subscribe(CoreSong? song)
    {
        if (_subscribed is not null)
            _subscribed.PropertyChanged -= OnSongPropertyChanged;
        _subscribed = song;
        if (song is not null)
            song.PropertyChanged += OnSongPropertyChanged;
        Update();
    }

    private void OnSongPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CoreSong.Favorite))
            Update();
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (Song is not { } song)
            return;

        song.Favorite = !song.Favorite;
        Spin(song.Favorite ? -72 : 72);
    }

    private void Update()
    {
        bool starred = Song?.Favorite == true;
        _icon.Style = starred ? StarredStyle : UnstarredStyle;
        if (starred && StarredForeground is { } brush)
            _icon.Foreground = brush;
        else
            _icon.ClearValue(IconElement.ForegroundProperty);
        _icon.Glyph = starred ? "" : "";

        // TRANSLATORS (startoggle.py): "Star" and "Unstar" are verbs.
        string label = starred ? Strings.Unstar : Strings.Star;
        ToolTipService.SetToolTip(this, label);
        AutomationProperties.SetName(this, label);
    }

    /// <summary>@keyframes rotate_star / rotate_unstar: from ±72deg to 0 in 0.4 s, ease.</summary>
    private void Spin(float fromDegrees)
    {
        var visual = ElementCompositionPreview.GetElementVisual(_icon);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)_icon.ActualWidth / 2, (float)_icon.ActualHeight / 2, 0);

        var animation = compositor.CreateScalarKeyFrameAnimation();
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f));
        animation.InsertKeyFrame(0f, fromDegrees);
        animation.InsertKeyFrame(1f, 0f, ease);
        animation.Duration = System.TimeSpan.FromMilliseconds(400);
        visual.StartAnimation("RotationAngleInDegrees", animation);
    }
}
