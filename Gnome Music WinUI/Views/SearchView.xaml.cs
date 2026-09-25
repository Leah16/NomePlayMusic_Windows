// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Linq;
using Gnome_Music_WinUI.Controls;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Views;

/// <summary>Search.State.</summary>
public enum SearchState
{
    None = 0,
    Result = 1,
    NoResult = 2,
}

/// <summary>
/// Search results: artists, albums and songs (views/searchview.py). One row of
/// artists and two rows of albums are shown, with "View All" pages for the rest.
/// </summary>
public sealed partial class SearchView : UserControl
{
    /// <summary>A tile: 192 px image + 32 px (search.py items_per_row).</summary>
    private const double TileWidth = 192 + 32;

    private readonly CoreModel _model = App.Services.Model;
    private readonly DispatcherQueueTimer _delay;
    private SearchResults _results = SearchResults.Empty;
    private string _text = "";
    private int _artistsShown = 6;
    private int _albumsShown = 8;
    private SearchState _state = SearchState.None;

    public SearchView()
    {
        InitializeComponent();
        _delay = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _delay.Interval = TimeSpan.FromMilliseconds(250);
        _delay.IsRepeating = false;
        _delay.Tick += (_, _) => RunSearch();
        _model.LibraryChanged += (_, _) =>
        {
            if (_text.Length > 0)
                RunSearch();
        };
        ResultsScroller.SizeChanged += (_, e) => OnWidthChanged(e.NewSize.Width);
        ShowResults();
    }

    /// <summary>Raised when the search state changes (the entry turns red on NO_RESULT).</summary>
    public event EventHandler<SearchState>? StateChanged;

    public SearchState State => _state;

    public SearchResults Results => _results;

    /// <summary>Updates the search after the 250 ms search delay of GtkSearchEntry.</summary>
    public void Search(string text)
    {
        _text = text;
        _delay.Stop();
        if (text.Trim().Length == 0)
            RunSearch();
        else
            _delay.Start();
    }

    private void RunSearch()
    {
        _results = _model.Search(_text);
        ShowResults();
    }

    private void OnWidthChanged(double width)
    {
        // Narrow windows get smaller side margins than GNOME's fixed 120 px.
        double margin = width >= 900 ? 120 : Math.Max(12, width * 0.06);
        ResultsPanel.Margin = new Thickness(margin, 20, margin, 20);

        int perRow = Math.Max(1, (int)Math.Floor((width - 2 * margin) / TileWidth));
        if (perRow != _artistsShown || perRow * 2 != _albumsShown)
        {
            _artistsShown = perRow;
            _albumsShown = perRow * 2;
            ShowResults();
        }
    }

    private void ShowResults()
    {
        var results = _results;
        ArtistsGrid.ItemsSource = results.Artists.Take(_artistsShown).ToList();
        AlbumsGrid.ItemsSource = results.Albums.Take(_albumsShown).ToList();
        SongsList.ItemsSource = results.Songs;

        bool hasArtists = results.Artists.Count > 0;
        bool hasAlbums = results.Albums.Count > 0;
        bool hasSongs = results.Songs.Count > 0;
        ArtistsHeader.Visibility = ArtistsGrid.Visibility = Show(hasArtists);
        AlbumsHeader.Visibility = AlbumsGrid.Visibility = Show(hasAlbums);
        SongsHeader.Visibility = SongsCard.Visibility = Show(hasSongs);
        AllArtistsButton.Visibility = Show(results.Artists.Count > _artistsShown);
        AllAlbumsButton.Visibility = Show(results.Albums.Count > _albumsShown);

        var state = !results.IsEmpty ? SearchState.Result
            : _text.Trim().Length > 0 ? SearchState.NoResult
            : SearchState.None;
        ResultsScroller.Visibility = Show(state == SearchState.Result);
        StatusPanel.Visibility = Show(state != SearchState.Result);
        StatusTitle.Text = state == SearchState.NoResult ? Strings.NoResultsFound : Strings.NoSearchStarted;
        StatusDescription.Text = state == SearchState.NoResult ? Strings.TryDifferentSearch : Strings.NoSearchStartedDescription;

        if (_state != state)
        {
            _state = state;
            StateChanged?.Invoke(this, state);
        }
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnArtistClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CoreArtist artist)
            App.MainWindow?.ShowArtist(artist);
    }

    private void OnAlbumClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CoreAlbum album)
            App.MainWindow?.ShowAlbum(album);
    }

    private void OnAllArtistsClick(object sender, RoutedEventArgs e) => App.MainWindow?.ShowAllArtists(_results);

    private void OnAllAlbumsClick(object sender, RoutedEventArgs e) => App.MainWindow?.ShowAllAlbums(_results);

    private void OnSongClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CoreSong song)
            SongActions.PlaySearchResults(_results, song);
    }

    private void OnSongPlayRequested(object? sender, CoreSong song) => SongActions.PlaySearchResults(_results, song);
}
