// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Gnome_Music_WinUI.Controls;
using Gnome_Music_WinUI.Dialogs;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Gnome_Music_WinUI.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace Gnome_Music_WinUI;

/// <summary>The main window (window.py, HeaderBar.ui, SearchHeaderBar.ui).</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// The smallest window (DIPs) in which nothing gives way: the player bar keeps the
    /// cover (the way to the lyrics), the song's title and all its buttons, the title
    /// bar the view switcher or the search entry, and the content a row of albums or a
    /// few lines of lyrics. GNOME Music goes down to 360 × 294.
    /// </summary>
    private const int MinWidth = 640;
    private const int MinHeight = 480;

    /// <summary>How many views Back can return through.</summary>
    private const int MaxViewHistory = 50;

    /// <summary>Over the lyrics, the controls step aside once the pointer has rested this long in the window…</summary>
    private static readonly TimeSpan ControlsRestTime = TimeSpan.FromSeconds(3);

    /// <summary>…or this long after it left the window.</summary>
    private static readonly TimeSpan ControlsLeaveTime = TimeSpan.FromMilliseconds(500);

    /// <summary>The lyrics' back button fades as the player bar does (PlayerToolbar.SetAway).</summary>
    private static readonly TimeSpan ControlsAwayFade = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ControlsBackFade = TimeSpan.FromMilliseconds(150);

    private readonly AppServices _services = App.Services;
    private readonly IntPtr _hwnd;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _pointerTimer;
    private MainPage? _mainPage;

    /// <summary>The views shown before the current one, the last last (Back returns through them).</summary>
    private readonly List<string> _viewHistory = new();

    /// <summary>Back is showing a view of the history: that is not a new step in it.</summary>
    private bool _restoringView;

    private bool _searchMode;
    private bool _lyricsOpen;
    private ContentDialog? _openDialog;
    private MiniPlayerWindow? _miniPlayer;

    // Where the pointer was at the last look, and when it last moved in the window (or left
    // it, or the song played again); whether the song played then
    private POINT _pointer;
    private bool _pointerOver;
    private long _pointerActive;
    private bool _playing;
    private bool _controlsAway;

    /// <summary>The width of the caption buttons (DIPs): AppWindow reports none while they are away.</summary>
    private double _captionWidth;

    public MainWindow()
    {
        InitializeComponent();
        Title = Strings.AppName;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarLayout();
        AppTitleBar.Loaded += (_, _) => UpdateTitleBarLayout();
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();
        UpdateCaptionButtonColors();

        RestoreWindowPlacement();
        AppWindow.Closing += OnAppWindowClosing;

        // The minimum size follows the window to screens of other scales.
        RootGrid.Loaded += (_, _) =>
        {
            double shown = RootGrid.XamlRoot.RasterizationScale;
            RootGrid.XamlRoot.Changed += (root, _) =>
            {
                if (root.RasterizationScale != shown)
                {
                    shown = root.RasterizationScale;
                    ApplyMinimumSize(shown);
                }
            };
        };

        RegisterAccelerators();

        // The player's features the settings turn off (not in GNOME Music), and the
        // output falling back to the default device.
        _services.Settings.PropertyChanged += (_, e) =>
        {
            var settings = _services.Settings;
            if (e.PropertyName == nameof(Settings.LyricsEnabled) && !settings.LyricsEnabled)
                SetLyricsOpen(false);
            else if (e.PropertyName == nameof(Settings.MiniPlayerEnabled) && !settings.MiniPlayerEnabled)
                _miniPlayer?.Close();
        };
        _services.Player.OutputFallback += (_, _) => ShowToast(new Toast(Strings.OutputFallback));
        _services.Player.NoPlaybackDevice += (_, _) => ShowToast(new Toast(Strings.NoPlaybackDevice));
        Services.Audio.AsioThread.WindowHandle = _hwnd;

        // Previous, play/pause and next under the window's thumbnail in the taskbar (they
        // live as long as the window).
        _ = new TaskbarButtons(_hwnd, _services.Player);

        // The lyrics go when the player stops; over them, pausing brings the controls back.
        // Back in the window, maybe from editing the lyrics file, they are checked again.
        Lyrics.Opened += OnLyricsOpened;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                Lyrics.CheckLyricsFile();
        };
        _services.Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Player.CurrentSong) && _services.Player.CurrentSong is null)
                SetLyricsOpen(false);
            else if (e.PropertyName == nameof(Player.State) && _lyricsOpen)
                UpdateLyricsControls();
        };

        // Without music there is nothing to search, and a pushed page shows songs that are
        // gone: the status page comes back.
        _services.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CoreModel.SongsAvailable) && !_services.Model.SongsAvailable)
            {
                SetSearchMode(false);
                PopToRoot();
            }
        };

        // Over the lyrics, the player bar covers their bottom, and it and the caption
        // buttons step aside while the pointer rests (UpdateLyricsControls).
        PlayerBar.SizeChanged += (_, _) => Lyrics.BottomInset = PlayerBar.ActualHeight;
        _pointerTimer = DispatcherQueue.CreateTimer();
        _pointerTimer.Interval = TimeSpan.FromMilliseconds(250);
        _pointerTimer.Tick += (_, _) => UpdateLyricsControls();
        RootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, _) =>
        {
            if (_lyricsOpen)
                UpdateLyricsControls();
        }), true);

        // The keyboard coming into the bar, or to the lyrics' back button, brings them back
        // too, like a move of the pointer.
        TypedEventHandler<UIElement, GettingFocusEventArgs> keyboardBack = (_, e) =>
        {
            if (_lyricsOpen && e.FocusState == FocusState.Keyboard)
            {
                _pointerActive = Environment.TickCount64;
                SetControlsAway(false);
            }
        };
        PlayerBar.GettingFocus += keyboardBack;
        LyricsBackButton.GettingFocus += keyboardBack;

        // Lists handle typed characters for their own type-ahead; search wins.
        RootGrid.AddHandler(UIElement.CharacterReceivedEvent,
            new TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>(OnRootCharacterReceived), true);
        NavFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
        ViewSwitcher.SelectedItem = AlbumsItem;
    }

    public MainPage? RootPage => _mainPage;

    public bool IsSearchActive => _searchMode;

    public void ShowToast(Toast toast) => Toasts.Show(toast);

    /// <summary>
    /// Replaces the window with the mini player (not in GNOME Music) until "Back to
    /// Full View", or until the mini player is closed some other way.
    /// </summary>
    public void ShowMiniPlayer()
    {
        if (_miniPlayer is null)
        {
            _miniPlayer = new MiniPlayerWindow(AppWindow, Scale);
            _miniPlayer.Closed += (_, _) =>
            {
                _miniPlayer = null;
                AppWindow.Show();
                Activate();
            };
        }

        _miniPlayer.Activate();
        AppWindow.Hide();
    }

    // ------------------------------------------------------------------
    // Lyrics (not in GNOME Music)
    // ------------------------------------------------------------------

    public bool IsLyricsOpen => _lyricsOpen;

    public void ToggleLyrics() => SetLyricsOpen(!_lyricsOpen);

    /// <summary>
    /// Shows or hides the lyrics of the playing song. Their white page covers the
    /// content and the title bar in one piece, leaving the caption buttons and the player
    /// bar, until the song is clicked again (or Esc).
    /// </summary>
    public void SetLyricsOpen(bool open)
    {
        if (open == _lyricsOpen || open && _services.Player.CurrentSong is null)
            return;

        _lyricsOpen = open;
        PlayerBar.SetLyricsOpen(open);
        if (open)
        {
            if (IsFocusWithin(AppTitleBar))
                PlayerBar.FocusSongInfo();
            Lyrics.Open();
            LyricsBackButton.Visibility = Visibility.Visible;
            WatchPointer(true);
        }
        else
        {
            // The controls come back first: the title bar lays out around the caption buttons.
            WatchPointer(false);

            // The content shows again under the lyrics as they go; the title bar's own back
            // button takes the place of theirs.
            NavFrame.Visibility = Visibility.Visible;
            if (IsFocusWithin(Lyrics) || IsFocusWithin(LyricsBackButton))
                PlayerBar.FocusSongInfo();
            LyricsBackButton.Visibility = Visibility.Collapsed;
            Lyrics.Close();
        }

        UpdateHeader();
    }

    private void OnLyricsBackClick(object sender, RoutedEventArgs e) => SetLyricsOpen(false);

    /// <summary>Once the lyrics cover it, the content is hidden: nothing under them takes the focus.</summary>
    private void OnLyricsOpened(object? sender, EventArgs e)
    {
        if (!_lyricsOpen)
            return;

        if (IsFocusWithin(NavFrame))
            PlayerBar.FocusSongInfo();
        NavFrame.Visibility = Visibility.Collapsed;
    }

    private bool IsFocusWithin(DependencyObject container)
    {
        for (var element = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject; element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element == container)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Over the lyrics of a playing song, the player bar and the caption buttons step
    /// aside while the pointer rests in the window or is out of it, and come back as soon
    /// as it moves in the window or comes into it.
    /// </summary>
    private void WatchPointer(bool watch)
    {
        if (watch)
        {
            _pointerOver = IsPointerOverWindow(out _pointer);
            _pointerActive = Environment.TickCount64;
            _playing = _services.Player.State == PlayerState.Playing;
            _pointerTimer.Start();
        }
        else
        {
            _pointerTimer.Stop();
            SetControlsAway(false);
        }
    }

    /// <summary>
    /// Goes by where the pointer really is, every quarter of a second and on each XAML
    /// move: XAML also reports moves and exits of a resting pointer, and nothing over the
    /// title bar. The controls stay while nothing plays, while the pointer rests on them
    /// (the player bar, the caption buttons) and while a flyout of theirs is open. Playing
    /// again counts as a move.
    /// </summary>
    private void UpdateLyricsControls()
    {
        long now = Environment.TickCount64;
        bool over = IsPointerOverWindow(out var pointer);

        // Loading (between songs, or on the way to playing) keeps the last word.
        var state = _services.Player.State;
        bool playing = state == PlayerState.Playing || state == PlayerState.Loading && _playing;
        if (over != _pointerOver || over && (pointer.X != _pointer.X || pointer.Y != _pointer.Y) || playing && !_playing)
            _pointerActive = now;
        _pointer = pointer;
        _pointerOver = over;
        _playing = playing;

        var wait = over ? ControlsRestTime : ControlsLeaveTime;
        SetControlsAway(now - _pointerActive >= wait.TotalMilliseconds
            && playing && !(over && IsOverControls(pointer)) && !IsFlyoutOpen());
    }

    /// <summary>Whether the pointer is on the player bar, the caption buttons or the lyrics' back button.</summary>
    private bool IsOverControls(POINT pointer)
    {
        if (!ScreenToClient(_hwnd, ref pointer) || Content.XamlRoot is not { } root)
            return false;

        double scale = root.RasterizationScale;
        var at = new Windows.Foundation.Point(pointer.X / scale, pointer.Y / scale);
        var bar = PlayerBar.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, PlayerBar.ActualWidth, PlayerBar.ActualHeight));
        var back = LyricsBackButton.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, LyricsBackButton.ActualWidth, LyricsBackButton.ActualHeight));
        if (AppWindow.TitleBar.RightInset > 0)
            _captionWidth = AppWindow.TitleBar.RightInset / scale;
        var caption = new Windows.Foundation.Rect(
            RootGrid.ActualWidth - _captionWidth, 0, _captionWidth, AppTitleBar.ActualHeight);
        return bar.Contains(at) || caption.Contains(at) || back.Contains(at);
    }

    /// <summary>Whether a flyout is open: the play queue, the volume, the repeat modes (tooltips aside).</summary>
    private bool IsFlyoutOpen()
    {
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot))
        {
            if (popup.Child is not ToolTip)
                return true;
        }

        return false;
    }

    private void SetControlsAway(bool away)
    {
        if (away == _controlsAway)
            return;

        _controlsAway = away;
        if (away)
        {
            // A tooltip of the bar would stay behind.
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Content.XamlRoot))
            {
                if (popup.Child is ToolTip toolTip)
                    toolTip.IsOpen = false;
            }
        }

        PlayerBar.SetAway(away);
        AppWindow.TitleBar.PreferredHeightOption = away ? TitleBarHeightOption.Collapsed : TitleBarHeightOption.Tall;

        // The lyrics' back button fades with the bar; away, it takes no clicks, and its
        // place drags the window again.
        LyricsBackButton.OpacityTransition.Duration = away ? ControlsAwayFade : ControlsBackFade;
        LyricsBackButton.Opacity = away ? 0 : 1;
        LyricsBackButton.IsHitTestVisible = !away;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTitleBarLayout);
    }

    /// <summary>Whether the pointer is over this window or a popup of it, rather than over another window in front.</summary>
    private bool IsPointerOverWindow(out POINT pointer) =>
        GetCursorPos(out pointer) && GetAncestor(WindowFromPoint(pointer), GA_ROOTOWNER) == _hwnd;

    // ------------------------------------------------------------------
    // Navigation (AdwNavigationView)
    // ------------------------------------------------------------------

    public void ShowAlbum(CoreAlbum album) => Push(typeof(AlbumPage), album);

    public void ShowArtist(CoreArtist artist) => Push(typeof(ArtistPage), artist);

    public void ShowAllAlbums(SearchResults results) => Push(typeof(AlbumsSearchPage), results);

    public void ShowAllArtists(SearchResults results) => Push(typeof(ArtistsSearchPage), results);

    /// <summary>
    /// win.view_albums / view_artists / view_playlists, also from a pushed page (it
    /// closes; its own view's shortcut goes back to the view).
    /// </summary>
    public void ShowView(string view)
    {
        if (_searchMode || _mainPage?.HeaderState != HeaderState.Main)
            return;

        var item = ViewItem(view);
        if (ViewSwitcher.SelectedItem == item)
            PopToRoot();
        else
            ViewSwitcher.SelectedItem = item;
    }

    private SelectorBarItem ViewItem(string view) => view switch
    {
        "artists" => ArtistsItem,
        "playlists" => PlaylistsItem,
        _ => AlbumsItem,
    };

    /// <summary>
    /// win.navigate_back, made global (the title bar's back button, Alt+← and the mouse's
    /// back button): a pushed page goes; else search closes; else the view shown before
    /// comes back (the albums, artists, playlists and preferences, in the order they were
    /// shown), but not from the status page: without music those views show it too.
    /// False when there is nowhere to go back to.
    /// </summary>
    public bool GoBack()
    {
        if (NavFrame.CanGoBack)
        {
            // Going back plays the push transition in reverse: the page slides out to the
            // right. (FromLeft would reverse a push from the left, sliding it out to the left.)
            NavFrame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
            return true;
        }

        if (_searchMode)
        {
            SetSearchMode(false);
            return true;
        }

        if (_mainPage?.ShowsStatus == true)
            return false;

        while (_viewHistory.Count > 0)
        {
            string view = _viewHistory[^1];
            _viewHistory.RemoveAt(_viewHistory.Count - 1);
            if (view == _mainPage?.CurrentView)
                continue;

            _restoringView = true;
            try
            {
                if (view == "preferences")
                    PreferencesSwitcher.SelectedItem = PreferencesItem;
                else
                    ViewSwitcher.SelectedItem = ViewItem(view);
            }
            finally
            {
                _restoringView = false;
            }

            return true;
        }

        return false;
    }

    /// <summary>Whether <see cref="GoBack"/> goes anywhere.</summary>
    private bool CanGoBack =>
        NavFrame.CanGoBack || _searchMode
        || _mainPage?.ShowsStatus != true && _viewHistory.Any(view => view != _mainPage?.CurrentView);

    /// <summary>
    /// Shows a view of the switcher, or the preferences; search and pushed pages close.
    /// The view left goes into the history that Back returns through.
    /// </summary>
    private void SwitchView(string view)
    {
        if (_mainPage is not { } main)
            return;

        if (_searchMode)
            SetSearchMode(false);
        PopToRoot();
        string current = main.CurrentView;
        if (current != view && !_restoringView)
        {
            _viewHistory.Add(current);
            if (_viewHistory.Count > MaxViewHistory)
                _viewHistory.RemoveAt(0);
        }

        main.ShowView(view);
        UpdateHeader();
    }

    private void Push(Type page, object parameter) =>
        NavFrame.Navigate(page, parameter, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    /// <summary>replace_with_tags(["mainview"]): drops every pushed page.</summary>
    private void PopToRoot()
    {
        while (NavFrame.CanGoBack)
            NavFrame.GoBack(new SuppressNavigationTransitionInfo());
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is MainPage main && _mainPage != main)
        {
            _mainPage = main;
            main.HeaderStateChanged += (_, _) => UpdateHeader();
            main.SearchStateChanged += (_, state) => SetSearchError(state == SearchState.NoResult);
        }

        UpdateHeader();

        // Frame.Navigated comes before Page.OnNavigatedTo, which sets the page title.
        DispatcherQueue.TryEnqueue(UpdateHeader);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    /// <summary>
    /// The system caption buttons follow the app theme, with the Fluent colors of
    /// subtle buttons (TextFillColor* and SubtleFillColor* tokens).
    /// </summary>
    private void UpdateCaptionButtonColors()
    {
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        byte ink = dark ? (byte)255 : (byte)0;
        Windows.UI.Color Ink(byte alpha) => Windows.UI.Color.FromArgb(alpha, ink, ink, ink);
        bar.ButtonForegroundColor = Ink(dark ? (byte)0xFF : (byte)0xE4);
        bar.ButtonHoverForegroundColor = Ink(dark ? (byte)0xFF : (byte)0xE4);
        bar.ButtonPressedForegroundColor = Ink(dark ? (byte)0xC5 : (byte)0x9E);
        bar.ButtonInactiveForegroundColor = Ink(dark ? (byte)0x5D : (byte)0x5C);
        bar.ButtonBackgroundColor = Ink(0);
        bar.ButtonInactiveBackgroundColor = Ink(0);
        bar.ButtonHoverBackgroundColor = Ink(dark ? (byte)0x0F : (byte)0x09);
        bar.ButtonPressedBackgroundColor = Ink(dark ? (byte)0x0A : (byte)0x06);
    }

    /// <summary>
    /// Reserves room for the caption buttons, keeps the title widget centred without
    /// overlapping the side buttons, and lets clicks reach the interactive elements
    /// (everything else in the bar drags the window).
    /// </summary>
    private void UpdateTitleBarLayout()
    {
        if (Content.XamlRoot is not { } root)
            return;

        double scale = root.RasterizationScale;
        CaptionColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        if (AppWindow.TitleBar.RightInset > 0)
            _captionWidth = CaptionColumn.Width.Value;

        static double Occupied(FrameworkElement e) =>
            e.Visibility == Visibility.Visible ? e.ActualWidth + e.Margin.Left + e.Margin.Right : 0;
        double left = Occupied(BackButton) + 12;
        double right = Occupied(SearchButton) + Occupied(PreferencesSwitcher) + 12 + CaptionColumn.Width.Value;
        TitleContent.MaxWidth = Math.Max(0, AppTitleBar.ActualWidth - 2 * Math.Max(left, right));

        // The search entry is 500 px wide, or as wide as the room between the buttons.
        SearchEntryBorder.Width = Math.Min(500, TitleContent.MaxWidth);

        var rects = new System.Collections.Generic.List<RectInt32>();
        foreach (FrameworkElement element in new FrameworkElement[] { BackButton, SearchButton, ViewSwitcher, SearchEntryBorder, PreferencesSwitcher, LyricsBackButton })
        {
            if (element.Visibility != Visibility.Visible || !element.IsHitTestVisible || element.ActualWidth <= 0)
                continue;

            var bounds = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rects.Add(new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)));
        }

        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
    }

    private void OnViewSwitcherSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not SelectorBarItem item)
            return;

        PreferencesSwitcher.SelectedItem = null;
        SwitchView((string)item.Tag);
    }

    /// <summary>
    /// Pushed pages keep the switcher, with their view selected: tapping that view again
    /// goes back to it.
    /// </summary>
    private void OnViewItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ViewSwitcher.SelectedItem) && NavFrame.CanGoBack)
            PopToRoot();
    }

    /// <summary>The preferences view, where the primary menu was; no view of the switcher is selected then.</summary>
    private void OnPreferencesSwitcherSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null)
            return;

        ViewSwitcher.SelectedItem = null;
        SwitchView("preferences");
    }

    /// <summary>Search takes the switcher's place; the preferences item, still selected under it, closes it.</summary>
    private void OnPreferencesItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_searchMode)
            SetSearchMode(false);
    }

    /// <summary>
    /// Shows the preferences from a shortcut or a song's properties, from anywhere (the
    /// lyrics, search and pushed pages close), at a category if given ("shortcuts" for
    /// Ctrl+?), with the keyboard focus on the category.
    /// </summary>
    public void ShowPreferences(string? category = null)
    {
        if (_lyricsOpen)
            SetLyricsOpen(false);
        if (PreferencesSwitcher.SelectedItem != PreferencesItem)
            PreferencesSwitcher.SelectedItem = PreferencesItem;   // SwitchView: search and pushed pages close
        else if (_searchMode)
            SetSearchMode(false);
        if (_mainPage is not { } main)
            return;

        if (category is not null)
            main.Preferences.ShowCategory(category);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, main.Preferences.FocusCategory);
    }

    /// <summary>
    /// Header states: MAIN (view switcher), EMPTY (no music: the status page, or the
    /// preferences over it) and SEARCH (entry), on pushed pages too: a page keeps the
    /// header of the view it was pushed in, with that view selected (GNOME's pushed pages
    /// show a back button only). Without music only the preferences button is there. The
    /// back button shows while it leads somewhere, never on the status page. Unlike
    /// GNOME's search header bar, search keeps the preferences (the primary menu's
    /// place), so the search button stays in place. Under the lyrics the title bar has
    /// no buttons: nothing there can be clicked.
    /// </summary>
    private void UpdateHeader()
    {
        var state = _mainPage?.HeaderState ?? HeaderState.Main;
        bool header = !_lyricsOpen;

        BackButton.Visibility = Show(header && CanGoBack);
        SearchButton.Visibility = Show(header && state != HeaderState.Empty);
        SearchButton.IsChecked = _searchMode;
        PreferencesSwitcher.Visibility = Show(header);

        ViewSwitcher.Visibility = Show(header && state == HeaderState.Main);
        SearchEntryBorder.Visibility = Show(header && state == HeaderState.Search);

        // The interactive regions move with the header content; refresh after layout.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTitleBarLayout);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------
    // Search (searchheaderbar.py)
    // ------------------------------------------------------------------

    /// <summary>Opens or closes search. Closing it returns to the main page.</summary>
    public void SetSearchMode(bool active, string? initialText = null)
    {
        if (active && (PlaylistsWidget.RenameActive || _mainPage?.HeaderState == HeaderState.Empty))
        {
            SearchButton.IsChecked = false;
            return;
        }

        if (_searchMode == active && initialText is null)
            return;

        PopToRoot();
        _searchMode = active;
        _mainPage?.SetSearchMode(active);

        if (active)
        {
            if (initialText is not null)
            {
                SearchEntry.Text = initialText;
                SearchEntry.SelectionStart = SearchEntry.Text.Length;
            }

            UpdateHeader();
            SearchEntry.Focus(FocusState.Programmatic);
        }
        else
        {
            SearchEntry.Text = "";
            SetSearchError(false);
            UpdateHeader();
        }
    }

    private void OnSearchButtonClick(object sender, RoutedEventArgs e) => SetSearchMode(SearchButton.IsChecked == true);

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Typing on a page pushed from the results goes back to them.
        if (NavFrame.CanGoBack && SearchEntry.FocusState != FocusState.Unfocused)
            PopToRoot();
        _mainPage?.Search.Search(SearchEntry.Text);
    }

    private void OnSearchEntryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            SetSearchMode(false);
            e.Handled = true;
        }
    }

    /// <summary>The entry gets the "error" style when nothing was found.</summary>
    private void SetSearchError(bool error)
    {
        var brush = error ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : null;
        SearchEntryBorder.BorderThickness = new Thickness(error ? 2 : 0);
        SearchEntryBorder.BorderBrush = brush;
        if (brush is not null)
        {
            SearchEntry.Resources["TextControlForegroundFocused"] = brush;
            SearchEntry.Resources["TextControlForegroundPointerOver"] = brush;
            SearchEntry.Foreground = brush;
        }
        else
        {
            SearchEntry.Resources.Remove("TextControlForegroundFocused");
            SearchEntry.Resources.Remove("TextControlForegroundPointerOver");
            SearchEntry.ClearValue(Control.ForegroundProperty);
        }

        // Re-apply the visual state so the focused text picks up the new brush.
        VisualStateManager.GoToState(SearchEntry, SearchEntry.FocusState == FocusState.Unfocused ? "Normal" : "Focused", false);
    }

    /// <summary>Type-to-search: a printable character in the main view opens search with it (not in the preferences).</summary>
    private void OnRootCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_searchMode || _lyricsOpen || NavFrame.Content is not MainPage || _mainPage?.HeaderState != HeaderState.Main
            || _mainPage.CurrentView == "preferences"
            || PlaylistsWidget.RenameActive || _openDialog is not null || !_services.Model.IsLoaded)
        {
            return;
        }

        char c = args.Character;
        if (char.IsControl(c) || char.IsWhiteSpace(c) || char.IsSurrogate(c))
            return;

        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
            return;

        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        if (ctrl.HasFlag(CoreVirtualKeyStates.Down) || alt.HasFlag(CoreVirtualKeyStates.Down))
            return;

        args.Handled = true;
        SetSearchMode(true, c.ToString());
    }

    /// <summary>
    /// win.search_bar_close: Escape closes search, also from pages pushed from it. Over
    /// the lyrics it closes them first.
    /// </summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
            return;

        if (_lyricsOpen)
        {
            SetLyricsOpen(false);
            e.Handled = true;
        }
        else if (_searchMode)
        {
            SetSearchMode(false);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------
    // Dialogs
    // ------------------------------------------------------------------

    /// <summary>Shows a dialog unless one is already open (only one ContentDialog can be shown).</summary>
    public async System.Threading.Tasks.Task ShowDialogAsync(ContentDialog dialog)
    {
        if (_openDialog is not null)
            return;

        _openDialog = dialog;
        dialog.XamlRoot = Content.XamlRoot;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _openDialog = null;
        }
    }

    /// <summary>A song's properties (not in GNOME Music): from its menu, or from the lyrics' menu at their lyrics page.</summary>
    public async void ShowSongProperties(CoreSong song, bool lyrics = false) =>
        await ShowDialogAsync(new Dialogs.SongPropertiesDialog(song, lyrics));

    // ------------------------------------------------------------------
    // Keyboard shortcuts (application.py / window.py actions)
    // ------------------------------------------------------------------

    private void RegisterAccelerators()
    {
        var player = _services.Player;

        // General. The preferences are a view; the keyboard shortcuts are one of its pages.
        Add((VirtualKey)188, VirtualKeyModifiers.Control, () => ShowPreferences());
        Add(VirtualKey.F, VirtualKeyModifiers.Control, () =>
        {
            if (!_searchMode)
                SetSearchMode(true);
        });
        Add(VirtualKey.F1, VirtualKeyModifiers.None, PreferencesView.OpenHelp);
        Add((VirtualKey)191, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => ShowPreferences("shortcuts"));
        Add(VirtualKey.Q, VirtualKeyModifiers.Control, Close, withLyrics: true);

        // Playback. Ctrl+Space is caught on its way to the focused element: a focused
        // button takes Space as a click even with Ctrl down (the album's play button
        // would restart the album instead of pausing).
        RootGrid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Space || _openDialog is not null || !IsDown(VirtualKey.Control)
                || IsDown(VirtualKey.Shift) || IsDown(VirtualKey.Menu))
            {
                return;
            }

            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown)   // not again while the keys are held
                player.PlayPause();
        };
        Add(VirtualKey.N, VirtualKeyModifiers.Control, player.Next, withLyrics: true);
        Add(VirtualKey.B, VirtualKeyModifiers.Control, player.Previous, withLyrics: true);
        Add(VirtualKey.R, VirtualKeyModifiers.Control, player.ToggleRepeat, withLyrics: true);
        Add(VirtualKey.S, VirtualKeyModifiers.Control, player.ToggleShuffle, withLyrics: true);
        Add((VirtualKey)187, VirtualKeyModifiers.Control, player.IncreaseVolume, withLyrics: true);                              // Ctrl+=
        Add((VirtualKey)187, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, player.IncreaseVolume, withLyrics: true);  // Ctrl++
        Add(VirtualKey.Add, VirtualKeyModifiers.Control, player.IncreaseVolume, withLyrics: true);
        Add((VirtualKey)189, VirtualKeyModifiers.Control, player.DecreaseVolume, withLyrics: true);                              // Ctrl+-
        Add(VirtualKey.Subtract, VirtualKeyModifiers.Control, player.DecreaseVolume, withLyrics: true);
        Add(VirtualKey.M, VirtualKeyModifiers.Control, player.ToggleMute, withLyrics: true);

        // Navigation. Going back from the lyrics closes them.
        Add(VirtualKey.Number1, VirtualKeyModifiers.Menu, () => ShowView("albums"));
        Add(VirtualKey.Number2, VirtualKeyModifiers.Menu, () => ShowView("artists"));
        Add(VirtualKey.Number3, VirtualKeyModifiers.Menu, () => ShowView("playlists"));
        Add(VirtualKey.NumberPad1, VirtualKeyModifiers.Menu, () => ShowView("albums"));
        Add(VirtualKey.NumberPad2, VirtualKeyModifiers.Menu, () => ShowView("artists"));
        Add(VirtualKey.NumberPad3, VirtualKeyModifiers.Menu, () => ShowView("playlists"));
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, () => Back(), withLyrics: true);
        Add(VirtualKey.GoBack, VirtualKeyModifiers.None, () => Back(), withLyrics: true);

        // Under the lyrics the title bar, with search and the menu, takes no shortcuts.
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action, bool withLyrics = false)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) =>
            {
                if (_openDialog is not null || _lyricsOpen && !withLyrics)
                    return;

                e.Handled = true;
                action();
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }

        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // Mouse "back" button (button 8).
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(RootGrid).Properties.IsXButton1Pressed && Back())
                e.Handled = true;
        }), true);

        bool Back()
        {
            if (!_lyricsOpen)
                return GoBack();

            SetLyricsOpen(false);
            return true;
        }

        static bool IsDown(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }

    // ------------------------------------------------------------------
    // Window size (windowplacement.py)
    // ------------------------------------------------------------------

    private double Scale => GetDpiForWindow(_hwnd) / 96.0;

    private void RestoreWindowPlacement()
    {
        double scale = Scale;
        var size = _services.Settings.WindowSize;
        ApplyMinimumSize(scale);

        // A size saved while the minimum was smaller grows to the minimum.
        AppWindow.Resize(new SizeInt32(
            (int)(Math.Max(size[0], MinWidth) * scale),
            (int)(Math.Max(size[1], MinHeight) * scale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter && _services.Settings.WindowMaximized)
            presenter.Maximize();

        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange && !args.DidPresenterChange)
                return;

            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                bool maximized = p.State == OverlappedPresenterState.Maximized;
                _services.Settings.WindowMaximized = maximized;
                if (!maximized && p.State != OverlappedPresenterState.Minimized)
                {
                    double s = Scale;
                    _services.Settings.WindowSize = new[] { (int)(AppWindow.Size.Width / s), (int)(AppWindow.Size.Height / s) };
                }
            }
        };
    }

    /// <summary>The minimum size in physical pixels, at the scale of the window's screen.</summary>
    private void ApplyMinimumSize(double scale)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(MinWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinHeight * scale);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Pending deletions (playlists) are committed when their toast goes away.
        Toasts.DismissAll();
        _services.Flush();
    }

    private const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }
}
