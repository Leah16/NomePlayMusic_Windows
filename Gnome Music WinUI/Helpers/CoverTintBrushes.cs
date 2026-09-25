// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Gnome_Music_WinUI.Helpers;

/// <summary>
/// Paints brushes in the color of a cover (<see cref="CoverColors"/>), or in the
/// app's accent color when there is no cover (accent dark 1 in the light theme,
/// accent light 2 in the dark theme; see App.xaml). The brushes are looked up by key in the Light
/// and Default (dark) theme dictionaries of some resources; their high contrast
/// dictionaries keep the system colors.
/// </summary>
public sealed class CoverTintBrushes
{
    /// <summary>The fills of an accent button.</summary>
    public static readonly string[] AccentButtonKeys =
    {
        "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
    };

    /// <summary>The fills of a slider's value and thumb.</summary>
    public static readonly string[] SliderKeys =
    {
        "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
        "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
    };

    /// <summary>The pill under the selected item of a SelectorBar.</summary>
    public static readonly string[] SelectorBarKeys = { "SelectorBarItemPillFill" };

    /// <summary>The pill of a selected list item.</summary>
    public static readonly string[] ListViewItemKeys =
    {
        "ListViewItemSelectionIndicatorBrush", "ListViewItemSelectionIndicatorPointerOverBrush",
        "ListViewItemSelectionIndicatorPressedBrush",
    };

    /// <summary>The fill and edge of a toggle switch that is on.</summary>
    public static readonly string[] ToggleSwitchKeys =
    {
        "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
        "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnPointerOver", "ToggleSwitchStrokeOnPressed",
    };

    /// <summary>The ring of a progress ring.</summary>
    public static readonly string[] ProgressRingKeys = { "ProgressRingForegroundThemeBrush" };

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(400);

    private readonly List<SolidColorBrush> _light = new();
    private readonly List<SolidColorBrush> _dark = new();
    private readonly Stopwatch _clock = new();
    private CoverTint _from;
    private CoverTint _to;
    private CoverTint _shown;
    private int _loadId;

    public CoverTintBrushes()
    {
        _from = _to = _shown = AccentTint();
    }

    /// <summary>Adds the brushes with these keys in the theme dictionaries of <paramref name="resources"/>.</summary>
    public void Add(ResourceDictionary resources, params string[] keys)
    {
        foreach (var key in keys)
        {
            _light.Add((SolidColorBrush)((ResourceDictionary)resources.ThemeDictionaries["Light"])[key]);
            _dark.Add((SolidColorBrush)((ResourceDictionary)resources.ThemeDictionaries["Default"])[key]);
        }

        Paint(_shown);
    }

    /// <summary>Shows the color of the song's cover.</summary>
    public void Show(CoreSong? song, bool animate) =>
        Show(song is null ? null : App.Services.Art.GetSongArtAsync(song), animate);

    /// <summary>Shows the color of the album's cover.</summary>
    public void Show(CoreAlbum? album, bool animate) =>
        Show(album is null ? null : App.Services.Art.GetAlbumArtAsync(album), animate);

    /// <summary>
    /// Shows the color of the cover at the path <paramref name="art"/> gives. Unless
    /// the color is known at once, the old one shows in the meantime and changes smoothly.
    /// </summary>
    private async void Show(Task<string?>? art, bool animate)
    {
        int id = ++_loadId;
        var tint = LoadAsync(art);
        if (!tint.IsCompleted)
            animate = true;

        var result = await tint;
        if (id != _loadId)
            return;

        _from = _shown;
        _to = result ?? AccentTint();
        CompositionTarget.Rendering -= OnFrame;
        _clock.Restart();
        if (animate && _from != _to)
            CompositionTarget.Rendering += OnFrame;
        else
            Paint(_to);
    }

    private static async Task<CoverTint?> LoadAsync(Task<string?>? art)
    {
        try
        {
            if (art is not null && await art is { } path)
                return await CoverColors.GetTintAsync(path);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot read the color of a cover: {ex.Message}");
        }

        return null;
    }

    private void OnFrame(object? sender, object e)
    {
        double t = Math.Min(1, _clock.Elapsed / Duration);
        double k = 1 - Math.Pow(1 - t, 3);   // ease out
        Paint(new CoverTint(Mix(_from.Light, _to.Light, k), Mix(_from.Dark, _to.Dark, k)));
        if (t >= 1)
            CompositionTarget.Rendering -= OnFrame;
    }

    private void Paint(CoverTint tint)
    {
        _shown = tint;
        foreach (var brush in _light)
            brush.Color = tint.Light;
        foreach (var brush in _dark)
            brush.Color = tint.Dark;
    }

    // The app's accent (App.xaml), not the system's: UISettings would give the latter.
    private static CoverTint AccentTint() => new(
        (Color)Application.Current.Resources["SystemAccentColorDark1"],
        (Color)Application.Current.Resources["SystemAccentColorLight2"]);

    private static Color Mix(Color from, Color to, double t) => Color.FromArgb(
        255,
        (byte)Math.Round(from.R + (to.R - from.R) * t),
        (byte)Math.Round(from.G + (to.G - from.G) * t),
        (byte)Math.Round(from.B + (to.B - from.B) * t));
}
