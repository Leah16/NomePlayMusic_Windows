// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// The player controls at the bottom of the window (widgets/playertoolbar.py and
/// widgets/smoothscale.py). Hidden while nothing is loaded. The play button, the
/// progress bar and the star take the color of the song's cover, and so does the
/// playing song in lists. Clicking the song shows its lyrics (not in GNOME Music).
/// </summary>
public sealed partial class PlayerToolbar : UserControl
{
    /// <summary>The lyrics page fades in and out this fast (LyricsView's storyboards).</summary>
    private static readonly TimeSpan PageInDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PageOutDuration = TimeSpan.FromMilliseconds(167);

    private static readonly TimeSpan AwayDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BackDuration = TimeSpan.FromMilliseconds(150);

    private readonly Player _player = App.Services.Player;
    private readonly Settings _settings = App.Services.Settings;
    private readonly DispatcherQueueTimer _seekTimer;
    private readonly CoverTintBrushes _nowPlaying = new();
    private CoreSong? _song;
    private bool _updatingSlider;
    private bool _dragging;

    public PlayerToolbar()
    {
        InitializeComponent();
        _player.PropertyChanged += OnPlayerPropertyChanged;

        // The color of the song's cover: the fills of the play button and the progress
        // bar (see the XAML), the accent of the audio output (OutputPicker.xaml), and the
        // playing song in lists (NowPlayingBrush in App.xaml).
        _nowPlaying.Add(PlayButton.Resources, CoverTintBrushes.AccentButtonKeys);
        _nowPlaying.Add(ProgressSlider.Resources, CoverTintBrushes.SliderKeys);
        _nowPlaying.Add(Output.Resources, CoverTintBrushes.SelectorBarKeys);
        _nowPlaying.Add(Output.Resources, CoverTintBrushes.ListViewItemKeys);
        _nowPlaying.Add(Output.Resources, CoverTintBrushes.ToggleSwitchKeys);
        _nowPlaying.Add(Output.Resources, CoverTintBrushes.ProgressRingKeys);
        _nowPlaying.Add(Application.Current.Resources, "NowPlayingBrush");

        // SmoothScale: a drag seeks on release, or once the value rested for 100 ms.
        _seekTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _seekTimer.Interval = TimeSpan.FromMilliseconds(100);
        _seekTimer.IsRepeating = false;
        _seekTimer.Tick += (_, _) => _player.Seek(ProgressSlider.Value);
        ProgressSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _dragging = true), true);
        ProgressSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndDrag()), true);

        Bar.SizeChanged += (_, _) => LayOutBar();
        SongInfo.SizeChanged += (_, _) => LayOutBar();
        EndBox.SizeChanged += (_, _) => LayOutBar();

        // The settings turn the mini player, the output button, the volume and the lyrics
        // off, and the format line on (not in GNOME Music).
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Settings.MiniPlayerEnabled) or nameof(Settings.OutputButtonEnabled)
                or nameof(Settings.VolumeControlEnabled) or nameof(Settings.LyricsEnabled) or nameof(Settings.ShowAudioFormat))
                ApplyFeatures();
        };

        UpdateSong();
        UpdateState();
        ApplyFeatures();
    }

    private void ApplyFeatures()
    {
        MiniPlayerButton.Visibility = _settings.MiniPlayerEnabled ? Visibility.Visible : Visibility.Collapsed;
        OutputButton.Visibility = _settings.OutputButtonEnabled ? Visibility.Visible : Visibility.Collapsed;
        Volume.Visibility = _settings.VolumeControlEnabled ? Visibility.Visible : Visibility.Collapsed;

        // Without lyrics the song info is not a button.
        bool lyrics = _settings.LyricsEnabled;
        SongInfo.Style = (Style)(lyrics ? Application.Current.Resources["SubtleButtonStyle"] : Resources["PlainButtonStyle"]);
        SongInfo.IsTabStop = lyrics;
        UpdateSongInfoName();
        UpdateFormat();
    }

    private void UpdateSongInfoName()
    {
        string name = !_settings.LyricsEnabled ? $"{TitleText.Text}, {ArtistText.Text}"
            : App.MainWindow?.IsLyricsOpen == true ? Strings.HideLyrics
            : Strings.ShowLyrics;
        AutomationProperties.SetName(SongInfo, name);
    }

    /// <summary>"24 bit · 96 kHz" under the artist, when the settings show it.</summary>
    private void UpdateFormat()
    {
        var format = _player.Format;
        FormatText.Text = format?.Describe() ?? "";
        FormatText.Visibility = _settings.ShowAudioFormat && format is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// GtkCenterBox: the centre part stays centred against the whole bar. When the
    /// window is too narrow for that, the song's labels get shorter, then only its cover
    /// shows, then nothing; at the narrowest the centre part sits between the left edge
    /// and the end part, with its buttons closer together. (The minimum width of the
    /// window keeps the cover and the labels; the rest is for larger text.)
    /// </summary>
    private void LayOutBar()
    {
        const double Gap = 6, LabelsMinWidth = 48, LabelsMaxWidth = 220, Spacing = 12, TightSpacing = 4;
        double width = Bar.ActualWidth - Bar.Padding.Left - Bar.Padding.Right;
        double end = EndBox.ActualWidth + EndBox.Margin.Left + EndBox.Margin.Right;
        double buttons = ButtonsRow.Children.Sum(c => c.DesiredSize.Width) + (ButtonsRow.Children.Count - 1) * Spacing;

        // The widest each side may be with the buttons centred
        double side = (width - buttons) / 2 - Gap;
        double cover = SongInfo.Margin.Left + SongInfo.Padding.Left + Cover.Width + SongInfo.Padding.Right + SongInfo.Margin.Right;
        double labels = side - cover - SongInfoContent.Spacing;
        bool centred = side >= end;
        SongInfo.Visibility = centred && side >= cover ? Visibility.Visible : Visibility.Collapsed;
        Labels.Visibility = centred && labels >= LabelsMinWidth ? Visibility.Visible : Visibility.Collapsed;
        Labels.MaxWidth = Math.Clamp(labels, LabelsMinWidth, LabelsMaxWidth);
        ButtonsRow.Spacing = centred || width - end - 2 * Gap >= buttons ? Spacing : TightSpacing;

        if (centred)
        {
            double start = SongInfo.Visibility == Visibility.Visible
                ? SongInfo.ActualWidth + SongInfo.Margin.Left + SongInfo.Margin.Right
                : 0;
            double margin = Math.Max(start, end) + Gap;
            CenterBox.Margin = new Thickness(margin, 6, margin, 6);
        }
        else
        {
            CenterBox.Margin = new Thickness(Gap, 6, end + Gap, 6);
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.CurrentSong):
            case nameof(Player.HasNext):
            case nameof(Player.HasPrevious):
                UpdateSong();
                break;
            case nameof(Player.State):
                UpdateState();
                break;
            case nameof(Player.Position):
            case nameof(Player.Duration):
                UpdateProgress();
                break;
            case nameof(Player.Format):
                UpdateFormat();
                break;
        }
    }

    /// <summary>
    /// The play queue fits its songs, and a long one scrolls: at most up to the title
    /// bar (48 px), leaving a gap, and 720 px.
    /// </summary>
    private void OnQueueFlyoutOpening(object? sender, object e)
    {
        double available = (XamlRoot?.Size.Height ?? 0) - ActualHeight - 48 - 12;
        QueueView.MaxHeight = Math.Clamp(available, 160, 720);
    }

    /// <summary>The output as the settings have it now: the preferences may have changed it.</summary>
    private void OnOutputFlyoutOpening(object? sender, object e) => Output.Load();

    private void UpdateSong()
    {
        var song = _player.CurrentSong;
        Visibility = song is null ? Visibility.Collapsed : Visibility.Visible;
        if (song is null)
        {
            QueueFlyout.Hide();
            OutputFlyout.Hide();
        }
        PreviousButton.IsEnabled = _player.HasPrevious;
        NextButton.IsEnabled = _player.HasNext;
        PlayButton.IsEnabled = Star.IsEnabled = song is not null;
        if (song == _song)
            return;

        // The colors change smoothly from song to song, but not when the bar appears.
        _nowPlaying.Show(song, animate: _song is not null);
        _song = song;
        TitleText.Text = TipTitle.Text = song?.Title ?? "";
        ArtistText.Text = TipArtist.Text = song?.Artist ?? "";
        Cover.Song = song;
        Star.Song = song;
        UpdateSongInfoName();
        UpdateFormat();

        // On song change the elapsed label resets and the total shows the song length.
        ElapsedText.Text = Utils.SecondsToString(0);
        DurationText.Text = Utils.SecondsToString(song?.Duration ?? 0);
        UpdateProgress();
    }

    private void UpdateState()
    {
        bool playing = _player.State == PlayerState.Playing;
        PlayIcon.Glyph = playing ? "" : "";
        string label = playing ? Strings.Pause : Strings.Play;
        ToolTipService.SetToolTip(PlayButton, label);
        AutomationProperties.SetName(PlayButton, label);

        // The scale is insensitive and reset to 0 while stopped or loading.
        bool active = _player.State is PlayerState.Playing or PlayerState.Paused;
        ProgressSlider.IsEnabled = active;
        if (!active)
            SetSlider(0, Math.Max(1, _player.Duration));
    }

    private void UpdateProgress()
    {
        double duration = _player.Duration > 0 ? _player.Duration : _song?.Duration ?? 0;
        DurationText.Text = Utils.SecondsToString(duration);
        if (_dragging || _player.State is not (PlayerState.Playing or PlayerState.Paused))
            return;

        SetSlider(Math.Min(_player.Position, duration), Math.Max(1, duration));
        ElapsedText.Text = Utils.SecondsToString(_player.Position);
    }

    private void SetSlider(double value, double maximum)
    {
        _updatingSlider = true;
        ProgressSlider.Maximum = maximum;
        ProgressSlider.Value = value;
        _updatingSlider = false;
    }

    private void OnProgressValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider)
            return;

        ElapsedText.Text = Utils.SecondsToString(e.NewValue);
        _seekTimer.Stop();
        if (_dragging)
            _seekTimer.Start();
        else
            _player.Seek(e.NewValue);   // keyboard steps seek at once
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;

        _dragging = false;
        _seekTimer.Stop();
        _player.Seek(ProgressSlider.Value);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => _player.PlayPause();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _player.Previous();

    private void OnNextClick(object sender, RoutedEventArgs e) => _player.Next();

    private void OnMiniPlayerClick(object sender, RoutedEventArgs e) => App.MainWindow?.ShowMiniPlayer();

    /// <summary>The cover, title and artist show the lyrics, and hide them again (unless turned off).</summary>
    private void OnSongInfoClick(object sender, RoutedEventArgs e)
    {
        if (_settings.LyricsEnabled)
            App.MainWindow?.ToggleLyrics();
    }

    /// <summary>Over the lyrics the bar has an edge, which fades with their page.</summary>
    public void SetLyricsOpen(bool open)
    {
        if (_settings.LyricsEnabled)
            AutomationProperties.SetName(SongInfo, open ? Strings.HideLyrics : Strings.ShowLyrics);
        LyricsEdge.OpacityTransition.Duration = open ? PageInDuration : PageOutDuration;
        LyricsEdge.Opacity = open ? 1 : 0;
    }

    /// <summary>
    /// Over the lyrics, the bar steps aside while the pointer rests or is out of the
    /// window (<see cref="MainWindow"/> decides): it fades out a little way down and lets
    /// clicks through to the lyrics. It comes back quicker than it went.
    /// </summary>
    public void SetAway(bool away)
    {
        var duration = away ? AwayDuration : BackDuration;
        Bar.OpacityTransition.Duration = duration;
        Bar.TranslationTransition.Duration = duration;
        Bar.Opacity = away ? 0 : 1;
        Bar.Translation = away ? new Vector3(0, 12, 0) : Vector3.Zero;
        Bar.IsHitTestVisible = !away;
    }

    public void FocusSongInfo() => SongInfo.Focus(FocusState.Programmatic);
}
