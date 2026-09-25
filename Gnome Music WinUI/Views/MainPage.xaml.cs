// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.ComponentModel;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Views;

/// <summary>Which header the title bar shows (HeaderBar.State).</summary>
public enum HeaderState
{
    /// <summary>View switcher, search toggle and preferences.</summary>
    Main,

    /// <summary>No music, the status page or the preferences over it: only the preferences button.</summary>
    Empty,

    /// <summary>Search entry instead of the view switcher.</summary>
    Search,
}

/// <summary>
/// The root page (the "mainview" of window.py): the albums, artists and playlists
/// views, or the status page while there is no music, and the search view. The
/// preferences are a view here too (not in GNOME Music, where they are a dialog); they
/// show with or without music.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly CoreModel _model = App.Services.Model;
    private readonly DispatcherQueueTimer _startupTimer;
    private string _view = "albums";
    private bool _searchMode;
    private bool _decided;

    public MainPage()
    {
        InitializeComponent();
        _model.PropertyChanged += OnModelPropertyChanged;
        SearchView.StateChanged += (_, state) => SearchStateChanged?.Invoke(this, state);

        // window.py: decide between the views and the status page after 1 s, or
        // as soon as the library reports songs; here also once the first scan found none.
        _decided = _model.SongsAvailable || _model.IsLoaded;
        _startupTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _startupTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _startupTimer.IsRepeating = false;
        _startupTimer.Tick += (_, _) =>
        {
            _decided = true;
            Update();
        };
        _startupTimer.Start();
        Update();
    }

    public event EventHandler<HeaderState>? HeaderStateChanged;

    public event EventHandler<SearchState>? SearchStateChanged;

    public HeaderState HeaderState { get; private set; } = HeaderState.Main;

    /// <summary>The status page (welcome) shows: there is no music and no other view over it.</summary>
    public bool ShowsStatus { get; private set; }

    public string CurrentView => _view;

    public SearchView Search => SearchView;

    public PreferencesView Preferences => PreferencesView;

    public void ShowView(string view)
    {
        _view = view;
        Update();
    }

    public void SetSearchMode(bool active)
    {
        _searchMode = active;
        if (!active)
            SearchView.Search("");
        Update();
    }

    /// <summary>Selects a playlist in the playlists view.</summary>
    public void SelectPlaylist(Playlist playlist) => PlaylistsView.Select(playlist);

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CoreModel.SongsAvailable) or nameof(CoreModel.IsLoaded))
        {
            if (_model.SongsAvailable || _model.IsLoaded)
                _decided = true;
            Update();
        }
        else if (e.PropertyName == nameof(CoreModel.IsScanning))
        {
            UpdateLoading();
        }
    }

    private void Update()
    {
        UpdateLoading();
        bool songs = _model.SongsAvailable;
        bool preferences = !_searchMode && _view == "preferences";
        bool empty = _decided && !songs;
        bool status = empty && !_searchMode && !preferences;

        AlbumsView.Visibility = Show(!_searchMode && songs && _view == "albums");
        ArtistsView.Visibility = Show(!_searchMode && songs && _view == "artists");
        PlaylistsView.Visibility = Show(!_searchMode && songs && _view == "playlists");
        SearchView.Visibility = Show(_searchMode);
        StatusView.Visibility = Show(status);
        if (status)
            StatusView.Update();

        bool shown = PreferencesView.Visibility == Visibility.Visible;
        PreferencesView.Visibility = Show(preferences);
        if (preferences && !shown)
            PreferencesView.OnShown();

        // Without music the preferences keep the status page's header: no view switcher and
        // no search, whose views would be empty (not in GNOME Music, which has no such view).
        var header = _searchMode ? HeaderState.Search : empty ? HeaderState.Empty : HeaderState.Main;
        if (header != HeaderState || status != ShowsStatus)
        {
            HeaderState = header;
            ShowsStatus = status;
            HeaderStateChanged?.Invoke(this, header);
        }
    }

    private void UpdateLoading() => LoadingBar.Visibility = Show(_model.IsScanning);

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
