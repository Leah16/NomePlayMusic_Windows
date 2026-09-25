// SPDX-License-Identifier: GPL-2.0-or-later
using System.ComponentModel;
using Gnome_Music_WinUI.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// A song in the play queue overlay (not in GNOME Music). It shows the playback
/// state like <see cref="SongRow"/>: bold with a ▶ while playing, dimmed once played,
/// dimmed with an error icon when it failed to play.
/// </summary>
public sealed partial class QueueRow : UserControl
{
    public static readonly DependencyProperty SongProperty = DependencyProperty.Register(
        nameof(Song), typeof(CoreSong), typeof(QueueRow), new PropertyMetadata(null, OnSongChanged));

    private CoreSong? _subscribed;

    public QueueRow()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe(Song);
        Unloaded += (_, _) => Subscribe(null);
    }

    public CoreSong? Song
    {
        get => (CoreSong?)GetValue(SongProperty);
        set => SetValue(SongProperty, value);
    }

    private static void OnSongChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (QueueRow)d;
        row.Cover.Song = e.NewValue as CoreSong;
        if (row.IsLoaded)
            row.Subscribe(e.NewValue as CoreSong);
        else
            row.Update();
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
        if (e.PropertyName is nameof(CoreSong.State) or nameof(CoreSong.Validation)
            or nameof(CoreSong.Title) or nameof(CoreSong.Artist) or nameof(CoreSong.DurationText))
        {
            Update();
        }
    }

    private void Update()
    {
        var song = Song;
        TitleText.Text = song?.Title ?? "";
        ArtistText.Text = song?.Artist ?? "";
        DurationText.Text = song?.DurationText ?? "";
        ToolTipService.SetToolTip(TitleText, song?.Title);

        bool failed = song?.IsFailed == true;
        bool playing = song?.State == SongState.Playing;
        PlayIcon.Glyph = failed ? "" : "";
        PlayIcon.Visibility = failed || playing ? Visibility.Visible : Visibility.Collapsed;
        TitleText.FontWeight = playing && !failed ? FontWeights.SemiBold : FontWeights.Normal;
        Opacity = failed || song?.State == SongState.Played ? 0.55 : 1.0;
        VisualStateManager.GoToState(this, failed ? "Failed" : playing ? "Playing" : "Idle", false);
    }
}
