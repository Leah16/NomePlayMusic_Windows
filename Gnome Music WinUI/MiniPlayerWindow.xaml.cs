// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
/// progress. Dragging it anywhere moves it. <see cref="MainWindow"/> hides itself
/// while the mini player is open and comes back when it closes.
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    private const int Size = 300;
    private const int MinimumSize = 200;
    private const int ScreenMargin = 24;

    /// <summary>Where the mini player was when it last closed, for this session.</summary>
    private static (PointInt32 Position, SizeInt32 ClientSize)? _lastPlacement;

    private readonly Player _player = App.Services.Player;
    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _hoverTimer;
    private CoreSong? _song;
    private int _artId;
    private bool _pointerOver;
    private bool _keyboardFocus;
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

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.PreferredMinimumWidth = (int)(MinimumSize * scale);
            presenter.PreferredMinimumHeight = (int)(MinimumSize * scale);
            presenter.SetBorderAndTitleBar(true, false);
        }

        // AppWindow.ResizeClient counts a title bar this window does not have, so the
        // size comes from the frame the window actually has.
        int frameWidth = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        int frameHeight = AppWindow.Size.Height - AppWindow.ClientSize.Height;
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
        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(250);
        _hoverTimer.Tick += (_, _) => SetPointerOver(IsPointerOverWindow());

        _player.PropertyChanged += OnPlayerPropertyChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _hoverTimer.Stop();
            _player.PropertyChanged -= OnPlayerPropertyChanged;
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

    private void UpdateOverlay()
    {
        if (!_closed)
            Overlay.Opacity = _pointerOver || _keyboardFocus ? 1 : 0;
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
