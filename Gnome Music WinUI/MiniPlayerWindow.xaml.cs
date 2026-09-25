// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Controls;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace Gnome_Music_WinUI;

/// <summary>
/// The mini player (not in GNOME Music), after the Windows media player's: a small
/// window on top of the others, without a title bar, filled with the cover. Pointing
/// at it shows previous, play/pause and next, the song and "Back to Full View" over a
/// blurred, dimmed copy of the cover; a thin bar along the bottom edge shows the
/// progress. Dragging it anywhere moves it; its edges and corners resize it, always
/// as a square, and below 240 px the song's title and artist hide. What it shows is a
/// setting (<see cref="MiniPlayerMode"/>): the cover as above, the controls over the
/// blurred cover all the time, or the song's lyrics on white as the lyrics page shows
/// them, without the progress bar; pointed at, the lyrics themselves blur and darken
/// under the controls (the cover takes their place for songs without lyrics).
/// <see cref="MainWindow"/> hides itself while the mini player is open and comes back
/// when it closes.
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    private const int Size = 300;
    private const int MinimumSize = 200;
    private const int ScreenMargin = 24;

    /// <summary>Below this size the song's title and artist would run into the buttons: they hide.</summary>
    private const double SongLabelsMinSize = 240;

    /// <summary>The picture of the lyrics that is blurred under the controls, in pixels each way.</summary>
    private const int LyricsBlurSize = 64;

    /// <summary>Where the mini player was when it last closed, for this session.</summary>
    private static (PointInt32 Position, SizeInt32 ClientSize)? _lastPlacement;

    private readonly Player _player = App.Services.Player;
    private readonly Settings _settings = App.Services.Settings;
    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _hoverTimer;
    private readonly DispatcherQueueTimer _lyricsBlurTimer;
    private CoreSong? _song;
    private int _artId;
    private bool _pointerOver;
    private bool _keyboardFocus;
    private bool _showsLyrics;
    private bool _blurringLyrics;
    private bool _lyricsBlurFailed;
    private byte[]? _lyricsPixels;
    private bool _closed;
    private (PointInt32 Cursor, PointInt32 Window)? _dragStart;

    /// <param name="mainWindow">The full view: the mini player first shows on its screen.</param>
    /// <param name="scale">The scale of that screen.</param>
    public MiniPlayerWindow(AppWindow mainWindow, double scale)
    {
        InitializeComponent();
        Title = Strings.AppName;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Placeholder.Glyph = CoverArt.AlbumGlyph;

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }

        // AppWindow.ResizeClient counts a title bar this window does not have, so the
        // size comes from the frame the window actually has. The frame is wider than it
        // is high: the smallest window has a square client inside it.
        int frameWidth = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        int frameHeight = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        if (presenter is not null)
        {
            presenter.PreferredMinimumWidth = (int)(MinimumSize * scale) + frameWidth;
            presenter.PreferredMinimumHeight = (int)(MinimumSize * scale) + frameHeight;
        }

        int size = (int)(Size * scale);
        var client = _lastPlacement?.ClientSize ?? new SizeInt32(size, size);
        AppWindow.Resize(new SizeInt32(client.Width + frameWidth, client.Height + frameHeight));
        if (_lastPlacement is { } last)
        {
            AppWindow.Move(last.Position);
        }
        else
        {
            // The bottom right corner of the screen
            int margin = (int)(ScreenMargin * scale);
            var area = DisplayArea.GetFromWindowId(mainWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Move(new PointInt32(
                area.X + area.Width - AppWindow.Size.Width - margin,
                area.Y + area.Height - AppWindow.Size.Height - margin));
        }

        // Remembered as it changes: closing it from code raises no AppWindow.Closing.
        AppWindow.Changed += (_, args) =>
        {
            if (!_closed && (args.DidPositionChange || args.DidSizeChange))
                _lastPlacement = (AppWindow.Position, AppWindow.ClientSize);
        };

        // It resizes as a square, and a small one shows only the buttons over the cover.
        SquareWindow.Attach(_hwnd);
        RootGrid.SizeChanged += (_, e) =>
            SongLabels.Visibility = Math.Min(e.NewSize.Width, e.NewSize.Height) >= SongLabelsMinSize ? Visibility.Visible : Visibility.Collapsed;
        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(250);
        _hoverTimer.Tick += (_, _) => SetPointerOver(IsPointerOverWindow());
        _lyricsBlurTimer = DispatcherQueue.CreateTimer();
        _lyricsBlurTimer.Interval = TimeSpan.FromMilliseconds(200);
        _lyricsBlurTimer.Tick += (_, _) => BlurLyrics();

        _player.PropertyChanged += OnPlayerPropertyChanged;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        MiniLyrics.HasLyricsChanged += (_, _) => UpdateMode();

        // The mini player has the taskbar button while the full view is hidden: the same
        // buttons under its thumbnail (they go with the window).
        _ = new TaskbarButtons(_hwnd, _player);
        Closed += (_, _) =>
        {
            _closed = true;
            _hoverTimer.Stop();
            _lyricsBlurTimer.Stop();
            _player.PropertyChanged -= OnPlayerPropertyChanged;
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
            MiniLyrics.Close();
        };

        // The controls also show while the keyboard is in them.
        RootGrid.GettingFocus += (_, e) =>
        {
            _keyboardFocus = e.FocusState == FocusState.Keyboard;
            UpdateOverlay();
        };
        RootGrid.LosingFocus += (_, e) =>
        {
            if (e.NewFocusedElement is null)
            {
                _keyboardFocus = false;
                UpdateOverlay();
            }
        };

        UpdateSong();
        UpdateState();
        UpdateMode();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Settings.MiniPlayerMode) or nameof(Settings.LyricsEnabled))
            UpdateMode();
    }

    /// <summary>
    /// Shows what the mode asks for. In the lyrics mode the lyrics layer shows once the
    /// song's lyrics are there; while they load, and for instrumentals, songs without
    /// lyrics or with the lyrics turned off, it is the cover mode.
    /// </summary>
    private void UpdateMode()
    {
        if (_closed)
            return;

        bool lyricsMode = _settings.MiniPlayerMode == MiniPlayerMode.Lyrics && _settings.LyricsEnabled;
        if (lyricsMode)
            MiniLyrics.Open();
        else
            MiniLyrics.Close();

        // Over the lyrics the controls lie on the lyrics blurred, not on the cover, and the
        // white page takes a darker veil: white on it keeps a contrast of 5.7:1.
        _showsLyrics = lyricsMode && MiniLyrics.HasLyrics;
        LyricsLayer.Opacity = _showsLyrics ? 1 : 0;
        BlurImage.Visibility = _showsLyrics ? Visibility.Collapsed : Visibility.Visible;
        LyricsBlurImage.Visibility = _showsLyrics ? Visibility.Visible : Visibility.Collapsed;
        Veil.Opacity = _showsLyrics ? 0.6 : 0.4;
        ProgressLine.Visibility = _showsLyrics ? Visibility.Collapsed : Visibility.Visible;
        UpdateOverlay();
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
        }
    }

    private void UpdateSong()
    {
        var song = _player.CurrentSong;
        PreviousButton.IsEnabled = _player.HasPrevious;
        NextButton.IsEnabled = _player.HasNext;
        PlayButton.IsEnabled = song is not null || _player.Queue.Count > 0;
        if (song == _song)
            return;

        _song = song;
        TitleText.Text = song?.Title ?? "";
        ArtistText.Text = song?.Artist ?? "";
        ToolTipService.SetToolTip(TitleText, song?.Title);
        LoadArt(song);
        UpdateProgress();
    }

    private void UpdateState()
    {
        bool playing = _player.State == PlayerState.Playing;
        PlayIcon.Glyph = playing ? "" : "";
        string label = playing ? Strings.Pause : Strings.Play;
        ToolTipService.SetToolTip(PlayButton, label);
        AutomationProperties.SetName(PlayButton, label);
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        double duration = _player.Duration > 0 ? _player.Duration : _song?.Duration ?? 0;
        bool active = _player.State is PlayerState.Playing or PlayerState.Paused;
        ProgressScale.ScaleX = active && duration > 0 ? Math.Clamp(_player.Position / duration, 0, 1) : 0;
    }

    /// <summary>The cover, and its blurred copy behind the controls.</summary>
    private async void LoadArt(CoreSong? song)
    {
        int id = ++_artId;
        string? path = null;
        Windows.Graphics.Imaging.SoftwareBitmap? blurred = null;
        try
        {
            if (song is not null)
                path = await App.Services.Art.GetSongArtAsync(song);
            if (path is not null)
                blurred = await CoverBlur.CreateAsync(path);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot show the cover in the mini player: {ex.Message}");
        }

        // The window may have closed in the meantime.
        if (id != _artId || _closed)
            return;

        ArtImage.Source = path is null ? null : new BitmapImage { DecodePixelWidth = 800, UriSource = new Uri(path) };
        Placeholder.Visibility = path is null ? Visibility.Visible : Visibility.Collapsed;
        if (blurred is null)
        {
            BlurImage.Source = null;
            return;
        }

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(blurred);
        if (id == _artId && !_closed)
            BlurImage.Source = source;
    }

    /// <summary>The controls: while pointed at or in the keyboard's hands, all the time in the controls mode.</summary>
    private void UpdateOverlay()
    {
        if (_closed)
            return;

        bool always = _settings.MiniPlayerMode == MiniPlayerMode.Controls;
        bool shown = always || _pointerOver || _keyboardFocus;
        Overlay.Opacity = shown ? 1 : 0;
        if (!shown || !_showsLyrics)
        {
            _lyricsBlurTimer.Stop();
            _lyricsBlurFailed = false;
        }
        else if (!_lyricsBlurTimer.IsRunning && !_lyricsBlurFailed)
        {
            BlurLyrics();
            _lyricsBlurTimer.Start();
        }
    }

    /// <summary>
    /// The lyrics blurred under the controls: a small picture of the lyrics layer,
    /// box-blurred like the cover, taken again five times a second while the controls
    /// show, so that it follows the lines. (In-app acrylic would blur them live, but it
    /// draws nothing in a Remote Desktop session: the white glyphs were left on white.)
    /// </summary>
    private async void BlurLyrics()
    {
        if (_blurringLyrics)
            return;

        _blurringLyrics = true;
        try
        {
            var picture = new RenderTargetBitmap();
            await picture.RenderAsync(LyricsLayer, LyricsBlurSize, LyricsBlurSize);
            byte[] pixels = (await picture.GetPixelsAsync()).ToArray();
            int width = picture.PixelWidth, height = picture.PixelHeight;
            if (_closed || !_showsLyrics || width == 0 || pixels.Length != width * height * 4
                || _lyricsPixels is { } last && pixels.AsSpan().SequenceEqual(last))
            {
                return;   // nothing new
            }

            _lyricsPixels = pixels;
            byte[] copy = (byte[])pixels.Clone();
            int radius = Math.Max(1, width / 20);
            var blurred = await Task.Run(() => CoverBlur.Blur(copy, width, height, radius));
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(blurred);
            if (!_closed)
                LyricsBlurImage.Source = source;
        }
        catch (Exception ex)
        {
            // Not again until the controls show the next time. (A picture under way when
            // the window closes fails too: nothing to say then.)
            _lyricsBlurFailed = true;
            _lyricsBlurTimer.Stop();
            if (!_closed)
                Log.Warning($"Cannot blur the lyrics in the mini player: {ex.Message}");
        }
        finally
        {
            _blurringLyrics = false;
        }
    }

    private void OnRootPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetPointerOver(true);
    }

    /// <summary>
    /// A resting pointer gets "exited" and "entered" again every half second, so
    /// whether it left is decided from where it really is: now, and every quarter of a
    /// second while the controls show.
    /// </summary>
    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetPointerOver(IsPointerOverWindow());
    }

    private void SetPointerOver(bool over)
    {
        _pointerOver = over;
        if (over)
            _hoverTimer.Start();
        else
            _hoverTimer.Stop();
        UpdateOverlay();
    }

    private bool IsPointerOverWindow() =>
        GetCursorPos(out var point) && GetWindowRect(_hwnd, out var rect)
        && point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    /// <summary>
    /// The window has no title bar: dragging it anywhere but on a button moves it by
    /// as much as the pointer moved on the screen.
    /// </summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed || !GetCursorPos(out var cursor))
            return;

        _dragStart = (new PointInt32(cursor.X, cursor.Y), AppWindow.Position);
        RootGrid.CapturePointer(e.Pointer);
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is { } start && GetCursorPos(out var cursor))
        {
            AppWindow.Move(new PointInt32(
                start.Window.X + cursor.X - start.Cursor.X,
                start.Window.Y + cursor.Y - start.Cursor.Y));
        }
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = null;
        RootGrid.ReleasePointerCapture(e.Pointer);
    }

    private void OnRootPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragStart = null;

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _player.Previous();

    private void OnPlayClick(object sender, RoutedEventArgs e) => _player.PlayPause();

    private void OnNextClick(object sender, RoutedEventArgs e) => _player.Next();

    private void OnFullViewClick(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

}
