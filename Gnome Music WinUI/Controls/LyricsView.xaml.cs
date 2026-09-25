// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// The lyrics of the playing song (not in GNOME Music), from its lyrics file or LRCLIB
/// (<see cref="LyricsService"/>): a white page over the whole window with large bold
/// lines in the color of the cover. The lines start below its Padding (the title bar)
/// and scroll up to the top edge; the player bar covers the bottom while it shows
/// (<see cref="BottomInset"/>). Synced lyrics follow the song: the current line is in
/// full color and kept a little above the middle, played lines are faint and the next
/// ones in between; clicking a line plays the song from there. Plain lyrics are all in
/// full color, under a note that they are not synced. An instrumental shows its title,
/// album and artist. <see cref="MainWindow"/> opens and closes the view.
/// </summary>
public sealed partial class LyricsView : UserControl
{
    private const double PlayedOpacity = 0.3;
    private const double UnplayedOpacity = 0.6;

    /// <summary>The current line's centre sits this far down the view.</summary>
    private const double FollowPosition = 0.4;

    /// <summary>A line turns current this many seconds early, as its color fades in.</summary>
    private const double Lead = 0.1;

    private const double LineSpacing = 6;
    private const double StanzaSpacing = 32;

    /// <summary>An instrumental's album and artist, next to its title in the lines' size.</summary>
    private const double DetailScale = 0.6;

    /// <summary>The widest the lines get (the column is centred in the view).</summary>
    private const double MaxLineWidth = 784;

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>After the user scrolls, the view follows the song again after this long.</summary>
    private static readonly TimeSpan UserScrollPause = TimeSpan.FromSeconds(4);

    private readonly Player _player = App.Services.Player;
    private readonly Storyboard _openStoryboard;
    private readonly Storyboard _closeStoryboard;
    private readonly DispatcherQueueTimer _lineTimer;
    private readonly DispatcherQueueTimer _followTimer;
    private readonly DispatcherQueueTimer _loadingTimer;
    private readonly List<Line> _lines = new();

    /// <summary>The distinct times of the synced lines, pauses included, in order.</summary>
    private double[] _times = Array.Empty<double>();

    private CoreSong? _song;
    private LyricsResult? _result;
    private int _loadId;
    private bool _failed;

    /// <summary>The lyrics may have changed while the view was closed: they load again when it opens.</summary>
    private bool _stale;

    /// <summary>Index in <see cref="_times"/> of the current time; -1 before the first line.</summary>
    private int _current = -1;

    private int _nextIndex;
    private double _fontSize = 32;
    private double _bottomInset;
    private bool _open;
    private bool _following = true;

    /// <summary>Where the view is scrolling to by itself; other scrolling is the user's.</summary>
    private double? _scrollTarget;

    public LyricsView()
    {
        InitializeComponent();
        _openStoryboard = (Storyboard)Resources["OpenStoryboard"];
        _closeStoryboard = (Storyboard)Resources["CloseStoryboard"];
        _openStoryboard.Completed += (_, _) =>
        {
            if (_open)
                Opened?.Invoke(this, EventArgs.Empty);
        };
        _closeStoryboard.Completed += (_, _) =>
        {
            if (!_open)
                Visibility = Visibility.Collapsed;
        };

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _lineTimer = dispatcher.CreateTimer();
        _lineTimer.IsRepeating = false;
        _lineTimer.Tick += (_, _) =>
        {
            if (_player.State == PlayerState.Playing)
                SetCurrent(_nextIndex, animate: true);
        };
        _followTimer = dispatcher.CreateTimer();
        _followTimer.IsRepeating = false;
        _followTimer.Interval = UserScrollPause;
        _followTimer.Tick += (_, _) =>
        {
            _following = true;
            FollowCurrent(animate: true);
        };
        _loadingTimer = dispatcher.CreateTimer();
        _loadingTimer.IsRepeating = false;
        _loadingTimer.Interval = TimeSpan.FromMilliseconds(300);
        _loadingTimer.Tick += (_, _) =>
        {
            LoadingRing.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
        };

        SizeChanged += OnSizeChanged;

        // The song set as instrumental or not, its lyrics saved, local lyrics turned on or off
        App.Services.Lyrics.Changed += OnLyricsChanged;

        // When the user scrolls the lyrics (wheel, touch, scroll bar, keys), the view
        // stops following the song for a while. The wheel is caught at once, also while
        // the view scrolls by itself.
        Scroller.ViewChanged += OnScrollerViewChanged;
        Scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => OnUserScroll()), true);

        // Tab and Shift+Tab into the lines land on the current one.
        LinesPanel.GettingFocus += (_, e) =>
        {
            if (e.FocusState == FocusState.Keyboard && !IsLine(e.OldFocusedElement)
                && CurrentLine()?.Element is Button current && current != e.NewFocusedElement)
            {
                e.TrySetNewFocusedElement(current);
            }
        };
    }

    /// <summary>Raised when the view has come up and covers the content.</summary>
    public event EventHandler? Opened;

    /// <summary>
    /// The height at the bottom of the view that the player bar covers while it shows.
    /// The lines keep their places above it, so when the bar steps aside nothing moves:
    /// more lines show below.
    /// </summary>
    public double BottomInset
    {
        get => _bottomInset;
        set
        {
            if (value == _bottomInset)
                return;

            _bottomInset = value;
            LayOutColumn(ActualWidth, ActualHeight);
        }
    }

    public void Open()
    {
        if (_open)
            return;

        _open = true;
        _player.PropertyChanged += OnPlayerPropertyChanged;
        _following = true;
        _closeStoryboard.Stop();
        Visibility = Visibility.Visible;
        if (_player.CurrentSong != _song || _failed || _stale)
        {
            // Another song, lyrics that changed meanwhile, or LRCLIB could not be reached
            // last time: try again.
            Load(_player.CurrentSong);
        }
        else
        {
            // Back at the current line at once
            _current = -2;
            UpdateLayout();
            UpdatePosition(animate: false);
        }

        _openStoryboard.Begin();
    }

    public void Close()
    {
        if (!_open)
            return;

        _open = false;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _lineTimer.Stop();
        _followTimer.Stop();
        _closeStoryboard.Begin();
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.CurrentSong):
                if (_player.CurrentSong != _song)
                    Load(_player.CurrentSong);
                break;
            case nameof(Player.Position):
            case nameof(Player.State):
                UpdatePosition(animate: true);
                break;
        }
    }

    /// <summary>The lyrics of a song changed (null: of every song): they load again, now or when the view opens.</summary>
    private void OnLyricsChanged(object? sender, CoreSong? song)
    {
        if (_song is null || song is not null && song != _song)
            return;

        if (_open)
            Load(_song);
        else
            _stale = true;
    }

    /// <summary>
    /// Back in the window (from an editor, say): the lyrics load again when the song's
    /// lyrics file they were looked up with has changed, come or gone.
    /// </summary>
    public async void CheckLyricsFile()
    {
        if (_song is not { } song || _result?.LocalFile is not { } stamp)
            return;

        var now = await System.Threading.Tasks.Task.Run(() => LyricsFile.Stamp(LyricsFile.PathFor(song.FilePath)));
        if (song != _song || now == stamp)
            return;

        if (_open)
            Load(song);
        else
            _stale = true;
    }

    private async void Load(CoreSong? song)
    {
        int id = ++_loadId;
        _song = song;
        _result = null;
        _failed = false;
        _stale = false;
        ShowLines(null);
        if (song is null)
            return;

        var lookup = App.Services.Lyrics.GetAsync(song);
        if (!lookup.IsCompleted)
            _loadingTimer.Start();

        LyricsResult result;
        try
        {
            result = await lookup;
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot show the lyrics: {ex.Message}");
            result = new LyricsResult(LyricsStatus.Failed, LyricsSource.Lrclib);
        }

        if (id != _loadId)
            return;

        _loadingTimer.Stop();
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        _result = result;
        _failed = result.Status == LyricsStatus.Failed;
        ShowLines(result);

        // LRCLIB can take seconds: the next song's lyrics are ready when it starts.
        if (_open && _player.Queue.PeekNextPlayable() is { } next && next != song)
            _ = App.Services.Lyrics.GetAsync(next);
    }

    /// <summary>
    /// Shows the lyrics, an instrumental's title, album and artist, or "No lyrics found"
    /// in their place; nothing while loading.
    /// </summary>
    private void ShowLines(LyricsResult? result)
    {
        _lineTimer.Stop();
        _followTimer.Stop();
        _following = true;
        _lines.Clear();
        LinesPanel.Children.Clear();
        _times = Array.Empty<double>();
        _current = -2;
        Hint.Visibility = Visibility.Collapsed;
        ScrollTo(0, animate: false);
        if (result is null)
            return;

        if (result.Status == LyricsStatus.Instrumental && _song is { } song)
        {
            ShowHint(Strings.LyricsInstrumental);
            AddLine(song.Title, null, 0);
            if (song.HasAlbum)
                AddLine(song.AlbumTitle, null, 0, DetailScale, UnplayedOpacity);
            AddLine(song.Artist, null, 0, DetailScale, UnplayedOpacity);
            return;
        }

        if (result.Lyrics is not { } lyrics)
        {
            AddLine(Strings.LyricsNotFound, null, 0);
            return;
        }

        if (!lyrics.IsSynced)
            ShowHint(Strings.LyricsNotSynced);
        bool stanza = false;
        foreach (var line in lyrics.Lines)
        {
            if (line.Text.Length == 0)
            {
                // Plain lyrics keep their stanzas apart; synced ones just pause.
                stanza = !lyrics.IsSynced && _lines.Count > 0;
                continue;
            }

            AddLine(line.Text, line.Time?.TotalSeconds, stanza ? StanzaSpacing : 0);
            stanza = false;
        }

        if (lyrics.IsSynced)
        {
            _times = lyrics.Lines.Select(l => l.Time!.Value.TotalSeconds).Distinct().ToArray();
            UpdateLayout();
            UpdatePosition(animate: false);
        }
    }

    private void ShowHint(string text)
    {
        Hint.Text = text;
        Hint.Visibility = Visibility.Visible;
    }

    /// <summary>A line of the lyrics; <paramref name="scale"/> and <paramref name="opacity"/> set an instrumental's details apart.</summary>
    private void AddLine(string text, double? time, double spacingBefore, double scale = 1, double opacity = 1)
    {
        var label = new TextBlock
        {
            Style = (Style)Resources["LyricTextStyle"],
            Text = text,
            FontSize = _fontSize * scale,
            Opacity = opacity,
            OpacityTransition = new ScalarTransition { Duration = FadeDuration },
        };
        if (scale != 1)
            label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;

        FrameworkElement element;
        if (time is { } t)
        {
            label.Opacity = UnplayedOpacity;
            var button = new Button { Style = (Style)Resources["LyricButtonStyle"], Content = label, Tag = t };
            AutomationProperties.SetName(button, text);
            button.Click += OnLineClick;
            element = button;
        }
        else
        {
            element = new Border { Child = label, Padding = new Thickness(12, 8, 12, 8) };
        }

        // The text lines up with the note above; the hover plate reaches out of the column.
        element.Margin = new Thickness(-12, spacingBefore, -12, LineSpacing);
        _lines.Add(new Line(time, element, label, scale));
        LinesPanel.Children.Add(element);
    }

    /// <summary>
    /// Marks the lines played, current and next at the player's position, and turns the
    /// next line current on time rather than on the player's next tick (200 ms).
    /// </summary>
    private void UpdatePosition(bool animate)
    {
        _lineTimer.Stop();
        if (_times.Length == 0)
            return;

        bool playing = _player.State == PlayerState.Playing;
        double position = (playing || _player.State == PlayerState.Paused ? _player.Position : 0) + Lead;
        int index = UpperBound(position) - 1;

        // The player's clock may lag the line timer by a little: no step back for that.
        if (_current >= 0 && index == _current - 1 && _times[_current] - position < 0.25)
            index = _current;

        SetCurrent(index, animate);

        if (playing && index + 1 < _times.Length)
        {
            double wait = _times[index + 1] - position;
            if (wait < 0.25)
            {
                _nextIndex = index + 1;
                _lineTimer.Interval = TimeSpan.FromSeconds(Math.Max(0, wait));
                _lineTimer.Start();
            }
        }
    }

    /// <summary>The number of times at or before <paramref name="position"/>.</summary>
    private int UpperBound(double position)
    {
        int low = 0, high = _times.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (_times[middle] <= position)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private void SetCurrent(int index, bool animate)
    {
        if (index == _current)
            return;

        _current = index;
        double now = index >= 0 ? _times[index] : double.NegativeInfinity;
        foreach (var line in _lines)
        {
            if (line.Time is double time)
                line.Label.Opacity = time == now ? 1 : time < now ? PlayedOpacity : UnplayedOpacity;
        }

        FollowCurrent(animate);
    }

    /// <summary>The current line (the first of the lines sung together); none before the first line or in a pause.</summary>
    private Line? CurrentLine()
    {
        if (_current < 0 || _current >= _times.Length)
            return null;

        double now = _times[_current];
        return _lines.FirstOrDefault(l => l.Time == now);
    }

    private bool IsLine(DependencyObject? element) =>
        element is FrameworkElement { Parent: StackPanel panel } && panel == LinesPanel;

    /// <summary>
    /// Scrolls the current line to its place, unless the user is scrolling. During a
    /// pause the view stays.
    /// </summary>
    private void FollowCurrent(bool animate)
    {
        if (!_following || CurrentLine() is not { } line || line.Element.ActualHeight <= 0 || Scroller.ViewportHeight <= 0)
            return;

        double top;
        try
        {
            top = line.Element.TransformToVisual(Column).TransformPoint(default).Y;
        }
        catch (ArgumentException)
        {
            return;   // not laid out in the view (yet)
        }

        double visible = Math.Max(0, Scroller.ViewportHeight - BottomInset);
        double target = top + line.Element.ActualHeight / 2 - visible * FollowPosition;
        target = Math.Clamp(target, 0, Math.Max(0, Scroller.ScrollableHeight));
        if (Math.Abs(target - Scroller.VerticalOffset) >= 1)
            ScrollTo(target, animate);
    }

    private void ScrollTo(double offset, bool animate)
    {
        _scrollTarget = offset;
        if (!Scroller.ChangeView(null, offset, null, disableAnimation: !animate))
            _scrollTarget = null;
    }

    /// <summary>
    /// The view moved without scrolling to a line, or stopped short of it: the user
    /// scrolled.
    /// </summary>
    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scrollTarget is not double target)
        {
            OnUserScroll();
        }
        else if (!e.IsIntermediate)
        {
            _scrollTarget = null;
            if (Math.Abs(Scroller.VerticalOffset - target) > 2 && target <= Scroller.ScrollableHeight)
                OnUserScroll();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        LayOutColumn(e.NewSize.Width, e.NewSize.Height);

    /// <summary>
    /// Large text on large windows, in a column centred in the view. The top padding
    /// scrolls with the lines; the bottom padding lets the last line reach its place
    /// above the player bar. The current line keeps its place.
    /// </summary>
    private void LayOutColumn(double width, double height)
    {
        if (width <= 0)
            return;

        double margin = width < 600 ? 24 : 48;
        double side = Math.Max(margin, (width - MaxLineWidth) / 2);
        double visible = Math.Max(0, height - BottomInset);
        Column.Width = width;
        Column.Padding = new Thickness(side, Padding.Top + 40, side, Math.Max(40, height - visible * FollowPosition));

        double fontSize = Math.Clamp(Math.Round(width / 28), 24, 40);
        if (fontSize != _fontSize)
        {
            _fontSize = fontSize;
            foreach (var line in _lines)
                line.Label.FontSize = fontSize * line.Scale;
        }

        if (_open)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FollowCurrent(animate: false));
    }

    private void OnUserScroll()
    {
        _scrollTarget = null;
        _following = false;
        _followTimer.Stop();
        _followTimer.Start();
    }

    /// <summary>Plays the song from the line; the view follows the song again.</summary>
    private void OnLineClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: double time } || _player.State is not (PlayerState.Playing or PlayerState.Paused))
            return;

        _followTimer.Stop();
        _following = true;
        _player.Seek(time);
    }

    private sealed record Line(double? Time, FrameworkElement Element, TextBlock Label, double Scale);
}
