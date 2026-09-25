// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Controls;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Gnome_Music_WinUI.Services.Audio;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Gnome_Music_WinUI.Dialogs;

/// <summary>
/// A song's properties (not in GNOME Music), from its menu: the file, the audio
/// stream, the play statistics and the ReplayGain on one page, all its tags with the
/// cover on the next, read only. A third page shows the song's lyrics as the lyrics
/// view would, sets the song as instrumental, opens its local lyrics (its lyrics file,
/// else its cached lyrics) in another app to edit them, and searches LRCLIB for lyrics
/// to download; it works only with the lyrics, local lyrics and downloads all on. The
/// lyrics view's menu opens the dialog at that page.
/// </summary>
public sealed partial class SongPropertiesDialog : ContentDialog
{
    private const int LyricsIndex = 2;

    /// <summary>The page shown last (for this session).</summary>
    private static int _lastCategory;

    private readonly CoreSong _song;
    private readonly string _songFile;
    private readonly string _cacheFile;
    private readonly TypedEventHandler<XamlRoot, XamlRootChangedEventArgs> _rootChanged;

    /// <summary>The lyrics as the page shows them; null until they are looked up.</summary>
    private LyricsResult? _lyrics;

    /// <summary>The song's local lyrics files when the page last looked.</summary>
    private LyricsFiles _files;

    private int _lyricsLoad;

    /// <summary>The lyrics changed while the search was open: they load again after it.</summary>
    private bool _lyricsStale;

    private bool _settingSwitch;
    private int _search;
    private bool _searched;
    private LrclibTrack? _selected;

    /// <param name="showLyrics">Opens at the lyrics page (while the lyrics are on) rather than the page shown last.</param>
    public SongPropertiesDialog(CoreSong song, bool showLyrics = false)
    {
        InitializeComponent();
        _song = song;
        _songFile = LyricsFile.PathFor(song.FilePath);
        _cacheFile = LyricsService.CachePathFor(song.FilePath);
        Title = song.Title;

        _rootChanged = (_, _) => FitToWindow();
        Loaded += (_, _) =>
        {
            FitToWindow();
            XamlRoot.Changed += _rootChanged;
        };

        // The lyrics follow changes made here, by the lyrics view and in an editor.
        App.Services.Lyrics.Changed += OnLyricsChanged;
        if (App.MainWindow is { } window)
            window.Activated += OnWindowActivated;
        Closed += (_, _) =>
        {
            XamlRoot.Changed -= _rootChanged;
            App.Services.Lyrics.Changed -= OnLyricsChanged;
            if (App.MainWindow is { } window)
                window.Activated -= OnWindowActivated;
        };

        Categories.SelectedIndex = showLyrics ? LyricsIndex : _lastCategory;
        _ = LoadAsync();
    }

    /// <summary>
    /// The lyrics page works only with the lyrics, local lyrics and downloads all on
    /// (the user's rule): else it says so, and nothing on it can be used.
    /// </summary>
    private static bool LyricsReady
    {
        get
        {
            var settings = App.Services.Settings;
            return settings.LyricsEnabled && settings.LoadLocalLyrics && settings.DownloadLyrics;
        }
    }

    /// <summary>To the settings that turn the lyrics page on: the dialog closes for the preferences.</summary>
    private void OnOpenLyricsSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        App.MainWindow?.ShowPreferences("controls");
    }

    private void FitToWindow()
    {
        var size = XamlRoot.Size;
        Root.Width = Math.Clamp(size.Width - 120, 480, 860);
        Root.Height = Math.Clamp(size.Height - 240, 300, 600);
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = Categories.SelectedIndex;
        if (index < 0)
            return;

        _lastCategory = index;
        PropertiesPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        TagsPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.Visibility = index == LyricsIndex ? Visibility.Collapsed : Visibility.Visible;
        LyricsPage.Visibility = index == LyricsIndex ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
        if (index != LyricsIndex)
            return;

        bool ready = LyricsReady;
        LyricsUnavailable.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        LyricsMain.Visibility = ready && SearchPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

        // The lyrics are looked up once the page shows.
        if (ready && _lyricsLoad == 0)
            LoadLyrics();
    }

    private async Task LoadAsync()
    {
        SongDetails details;
        try
        {
            details = await SongDetails.ReadAsync(_song);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot read the details of {_song.FilePath}: {ex.Message}");
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            return;
        }

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        FillProperties(details);
        FillTags(details);
    }

    // ------------------------------------------------------------------
    // Properties: file, audio, statistics, ReplayGain
    // ------------------------------------------------------------------

    private void FillProperties(SongDetails d)
    {
        var format = d.Format;
        var location = new StackPanel { Spacing = 2 };
        location.Children.Add(Value(d.Path));
        var open = new HyperlinkButton { Content = Strings.OpenLocation, Padding = new Thickness(0, 2, 0, 2) };
        open.Click += (_, _) => SongActions.OpenLocation(_song);
        location.Children.Add(open);

        AddGroup(PropertiesPage, Strings.PropFile, first: true, new[]
        {
            Row(Strings.PropType, d.TypeName),
            Row(Strings.PropSize, d.Size > 0 ? $"{FileSize(d.Size)} ({Strings.PropBytes(d.Size.ToString("N0", CultureInfo.CurrentCulture))})" : null),
            Row(Strings.PropModified, d.Modified > DateTime.MinValue ? Date(d.Modified) : null),
            Row(Strings.PropLocation, location),
        });

        string? rate = format is null ? null
            : format.IsDsd ? $"{AudioFormats.RateText(format.DsdRate)} ({AudioFormats.DsdName(format.DsdRate)})"
            : AudioFormats.RateText(format.SampleRate);
        string? depth = format is null ? null
            : format.IsDsd ? Strings.IntBits(1)
            : format.Lossless && format.BitsPerSample > 0 ? (format.Float ? Strings.FloatBits(format.BitsPerSample) : Strings.IntBits(format.BitsPerSample))
            : null;
        AddGroup(PropertiesPage, Strings.PropAudio, first: false, new[]
        {
            Row(Strings.PropDuration, d.Duration > 0 ? Utils.SecondsToString(d.Duration) : null),
            Row(Strings.PropFormat, d.Codec),
            Row(Strings.PropSampleRate, rate),
            Row(Strings.PropBitDepth, depth),
            Row(Strings.ChannelCounts, format is null ? null : ChannelName(format.Channels)),
            Row(Strings.PropBitrate, d.Bitrate > 0 ? $"{(d.Bitrate + 500) / 1000} kbps" : null),
            Row(Strings.PropEncoder, d.Encoder),
            Row(Strings.PropTagFormat, d.TagFormat),
        });

        AddGroup(PropertiesPage, Strings.PropStatistics, first: false, new[]
        {
            Row(Strings.PropAdded, Date(_song.Added.ToLocalTime())),
            Row(Strings.PropLastPlayed, _song.LastPlayed is { } last ? Value(Date(last.ToLocalTime())) : Missing(Strings.PropNever)),
            Row(Strings.PropPlayCount, _song.PlayCount.ToString(CultureInfo.CurrentCulture)),
            Row(Strings.PropFavorite, _song.Favorite ? Strings.PropYes : Strings.PropNo),
        });

        var gain = d.ReplayGain;
        AddGroup(PropertiesPage, Strings.ReplayGain, first: false, gain.IsEmpty
            ? new[] { Row(Strings.PropTrackGain, Missing(Strings.PropNotCalculated)), Row(Strings.PropAlbumGain, Missing(Strings.PropNotCalculated)) }
            : new[]
            {
                Row(Strings.PropTrackGain, Decibels(gain.TrackGain)),
                Row(Strings.PropTrackPeak, Peak(gain.TrackPeak)),
                Row(Strings.PropAlbumGain, Decibels(gain.AlbumGain)),
                Row(Strings.PropAlbumPeak, Peak(gain.AlbumPeak)),
            });
    }

    // ------------------------------------------------------------------
    // Tags: the cover and the song, then every tag
    // ------------------------------------------------------------------

    private void FillTags(SongDetails d)
    {
        var header = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new CoverArt { Size = 128, Radius = 8, Song = _song, VerticalAlignment = VerticalAlignment.Top });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        names.Children.Add(new TextBlock
        {
            Text = d.Title ?? _song.Title,
            Style = Resource<Style>("SubtitleTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        names.Children.Add(new TextBlock
        {
            Text = d.Artist ?? _song.Artist,
            Style = Resource<Style>("SecondaryLabelStyle"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        if (d.Album is { } album)
        {
            names.Children.Add(new TextBlock
            {
                Text = album,
                Style = Resource<Style>("SecondaryLabelStyle"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
        }

        Grid.SetColumn(names, 1);
        header.Children.Add(names);
        TagsPage.Children.Add(header);

        AddGroup(TagsPage, Strings.Tags, first: true, new[]
        {
            Row(Strings.PropTitle, d.Title),
            Row(Strings.PropArtist, d.Artist),
            Row(Strings.PropAlbumArtist, d.AlbumArtist),
            Row(Strings.PropAlbum, d.Album),
            Row(Strings.PropYear, d.Year),
            Row(Strings.PropTrack, NumberOf(d.Track, d.TrackTotal)),
            Row(Strings.PropDisc, NumberOf(d.Disc, d.DiscTotal)),
            Row(Strings.PropGenre, d.Genre),
            Row(Strings.PropComposer, d.Composer),
            Row(Strings.PropConductor, d.Conductor),
            Row(Strings.PropPublisher, d.Publisher),
            Row(Strings.PropGrouping, d.Grouping),
            Row(Strings.PropComment, d.Comment),
        });
    }

    // ------------------------------------------------------------------
    // Lyrics: the instrumental switch, the lyrics and their source
    // ------------------------------------------------------------------

    /// <summary>Looks the lyrics up as the lyrics view does, and the song's local lyrics files.</summary>
    private async void LoadLyrics()
    {
        int id = ++_lyricsLoad;
        _lyricsStale = false;
        _settingSwitch = true;
        InstrumentalSwitch.IsOn = _song.Instrumental;
        _settingSwitch = false;
        EditButton.IsEnabled = SearchButton.IsEnabled = false;
        EditButtonHint.Visibility = Visibility.Collapsed;
        LyricsText.Inlines.Clear();
        LyricsMessage.Visibility = Visibility.Collapsed;
        LyricsRing.Visibility = Visibility.Visible;
        LyricsRing.IsActive = true;

        LyricsResult result;
        try
        {
            result = await App.Services.Lyrics.GetAsync(_song);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot show the lyrics: {ex.Message}");
            result = new LyricsResult(LyricsStatus.Failed, LyricsSource.Lrclib);
        }

        // Looked at after the lookup, which may have just downloaded them.
        var files = await Task.Run(() => LyricsService.Stamp(_song));
        if (id != _lyricsLoad)
            return;

        _lyrics = result;
        _files = files;
        ShowLyrics(result);
    }

    private void ShowLyrics(LyricsResult result)
    {
        bool instrumental = _song.Instrumental;
        string fileName = Path.GetFileName(_songFile);
        LyricsRing.IsActive = false;
        LyricsRing.Visibility = Visibility.Collapsed;

        // Editing needs the lyrics saved locally; neither applies to an instrumental.
        EditButton.IsEnabled = _files.Any && !instrumental;
        SearchButton.IsEnabled = !instrumental;
        bool needsFile = !_files.Any && !instrumental;
        EditButtonHint.Visibility = needsFile ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(EditButton, needsFile ? Strings.EditLyricsUnavailable : "");

        LyricsInfo.Children.Clear();
        string? source = result switch
        {
            { Source: LyricsSource.File } => Strings.LyricsFromFile(fileName),
            { Source: LyricsSource.Cache } => Strings.LyricsFromCache,
            { Source: LyricsSource.Lrclib, Status: LyricsStatus.Found, Track: { } track } => Strings.LyricsFromLrclib(TrackName(track)),
            { Source: LyricsSource.Lrclib, Status: LyricsStatus.Instrumental } => Strings.LyricsLrclibInstrumental,
            _ => null,
        };
        if (source is not null && result.Lyrics is { IsSynced: false })
            source += " · " + Strings.LyricsNotSynced;
        AddInfo(source);
        if (!instrumental && !App.Services.Settings.LoadLocalLyrics)
        {
            if (_files.Song.Exists)
                AddInfo(Strings.LyricsFileNotLoaded(fileName));
            else if (_files.Cache.Exists)
                AddInfo(Strings.LyricsCacheNotLoaded);
        }

        LyricsText.Inlines.Clear();
        LyricsMessage.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        if (result.Status == LyricsStatus.Found && result.Lyrics is { } lyrics)
        {
            FillLines(LyricsText, lyrics);
        }
        else if (result.Status == LyricsStatus.Instrumental)
        {
            // Like the lyrics view: the title, album and artist in place of lyrics
            LyricsText.Inlines.Add(new Run { Text = _song.Title, FontWeight = FontWeights.SemiBold });
            if (_song.HasAlbum)
                AddSecondaryLine(LyricsText, _song.AlbumTitle);
            AddSecondaryLine(LyricsText, _song.Artist);
        }
        else
        {
            LyricsMessageText.Text = result.Status == LyricsStatus.Failed ? Strings.LyricsFailed : Strings.LyricsNotFound;
            RetryButton.Visibility = result.Status == LyricsStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
            LyricsMessage.Visibility = Visibility.Visible;
        }

        LyricsScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void AddInfo(string? text)
    {
        if (text is null)
            return;

        LyricsInfo.Children.Add(new TextBlock
        {
            Text = text,
            Style = Resource<Style>("RowDescriptionStyle"),
            IsTextSelectionEnabled = true,
        });
    }

    private void OnInstrumentalToggled(object sender, RoutedEventArgs e)
    {
        // The service tells everyone, this page included (OnLyricsChanged).
        if (!_settingSwitch)
            _song.Instrumental = InstrumentalSwitch.IsOn;
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => LoadLyrics();

    /// <summary>The song set as instrumental or not, its lyrics saved, local lyrics turned on or off.</summary>
    private void OnLyricsChanged(object? sender, CoreSong? song)
    {
        if (_lyricsLoad == 0 || song is not null && song != _song)
            return;

        if (SearchPanel.Visibility == Visibility.Visible)
            _lyricsStale = true;
        else
            LoadLyrics();
    }

    /// <summary>Back from another app, maybe the editor: a lyrics file (or cached lyrics) that changed, came or went shows.</summary>
    private async void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated || _lyricsLoad == 0)
            return;

        var now = await Task.Run(() => LyricsService.Stamp(_song));
        if (now == _files)
            return;

        if (SearchPanel.Visibility == Visibility.Visible)
        {
            _files = now;
            _lyricsStale = true;
            UpdateDownloadButton();
        }
        else
        {
            LoadLyrics();
        }
    }

    private void OnEditClick(SplitButton sender, SplitButtonClickEventArgs args) => OpenLyricsFile(chooseApp: false);

    private void OnEditWithClick(object sender, RoutedEventArgs e) => OpenLyricsFile(chooseApp: true);

    /// <summary>
    /// Opens the local lyrics in another app to edit them (the lyrics file, else the
    /// cached one: the one they are read from): the app Windows opens .lrc files with
    /// (Windows asks which when there is none), or one the user picks. The page shows the
    /// changes once the user comes back to the window.
    /// </summary>
    private void OpenLyricsFile(bool chooseApp)
    {
        string file = _files.Song.Exists ? _songFile : _cacheFile;
        try
        {
            var start = chooseApp
                ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "OpenWith.exe"), $"\"{file}\"")
                : new ProcessStartInfo(file) { UseShellExecute = true };
            if (!chooseApp && start.Verbs.Contains("edit", StringComparer.OrdinalIgnoreCase))
                start.Verb = "edit";
            Process.Start(start);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot open {file}: {ex.Message}");
            LyricsInfo.Children.Clear();
            AddInfo(Strings.LyricsOpenFailed(ex.Message));
        }
    }

    // ------------------------------------------------------------------
    // Lyrics: searching LRCLIB
    // ------------------------------------------------------------------

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        LyricsMain.Visibility = Visibility.Collapsed;
        SearchPanel.Visibility = Visibility.Visible;
        DownloadStatus.Text = "";
        UpdateDownloadButton();
        if (!_searched)
        {
            SearchTitleBox.Text = _song.Title;
            SearchArtistBox.Text = _song.RawArtist ?? "";
            RunSearch();
        }

        SearchTitleBox.Focus(FocusState.Programmatic);
    }

    private void OnSearchBackClick(object sender, RoutedEventArgs e) => CloseSearch();

    private void CloseSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        LyricsMain.Visibility = Visibility.Visible;
        if (_lyricsStale)
            LoadLyrics();
        SearchButton.Focus(FocusState.Programmatic);
    }

    private void OnRunSearchClick(object sender, RoutedEventArgs e) => RunSearch();

    /// <summary>Enter searches; it does not close the dialog.</summary>
    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        RunSearch();
    }

    /// <summary>
    /// Searches LRCLIB for the title and artist in the boxes. The results come with
    /// their lyrics; the one the lyrics come from, if any, is selected, else the first.
    /// </summary>
    private async void RunSearch()
    {
        string title = SearchTitleBox.Text.Trim();
        string artist = SearchArtistBox.Text.Trim();
        if (title.Length == 0 && artist.Length == 0)
            return;

        int id = ++_search;
        _searched = true;
        ResultsList.Items.Clear();
        ShowPreview(null);
        SearchMessage.Visibility = Visibility.Collapsed;
        SearchRing.Visibility = Visibility.Visible;
        SearchRing.IsActive = true;

        IReadOnlyList<LrclibTrack> results;
        string? error = null;
        try
        {
            results = LrclibClient.Rank(await App.Services.Lyrics.SearchAsync(title, artist), _song.Duration);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot search LRCLIB: {ex.Message}");
            results = Array.Empty<LrclibTrack>();
            error = ex.Message;
        }

        if (id != _search)
            return;

        SearchRing.IsActive = false;
        SearchRing.Visibility = Visibility.Collapsed;
        if (results.Count == 0)
        {
            SearchMessage.Text = error is null ? Strings.SearchNoResults : Strings.SearchFailed(error);
            SearchMessage.Visibility = Visibility.Visible;
            return;
        }

        long inUse = _lyrics is { Source: LyricsSource.Lrclib, Track: { } used } ? used.Id : -1;
        foreach (var track in results)
            ResultsList.Items.Add(ResultItem(track, track.Id == inUse));
        ResultsList.SelectedIndex = Math.Max(0, results.ToList().FindIndex(t => t.Id == inUse));
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    /// <summary>
    /// A result like a song row: the title and its duration, the artist and album and
    /// whether the lyrics are synced; a check before the one the lyrics come from.
    /// </summary>
    private static ListViewItem ResultItem(LrclibTrack track, bool inUse)
    {
        var grid = new Grid { Padding = new Thickness(0, 6, 0, 6), ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (inUse)
        {
            var check = new FontIcon { Glyph = "", FontSize = 12, Style = Resource<Style>("AccentIconStyle") };
            ToolTipService.SetToolTip(check, Strings.ResultInUse);
            grid.Children.Add(check);
        }

        Place(grid, new TextBlock { Text = track.TrackName ?? "", Style = Resource<Style>("BodyLabelStyle") }, 0, 1);
        Place(grid, new TextBlock { Text = Duration(track), Style = Resource<Style>("SecondaryCaptionLabelStyle"), VerticalAlignment = VerticalAlignment.Center }, 0, 2);
        Place(grid, new TextBlock { Text = Source(track), Style = Resource<Style>("SecondaryCaptionLabelStyle") }, 1, 1);
        Place(grid, new TextBlock { Text = Kind(track), Style = Resource<Style>("SecondaryCaptionLabelStyle") }, 1, 2);

        string details = Details(track, withArtist: true) + (inUse ? " · " + Strings.ResultInUse : "");
        var item = new ListViewItem { Content = grid, Tag = track, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, $"{track.TrackName}, {details}");
        ToolTipService.SetToolTip(item, $"{track.TrackName}\n{details}");
        return item;

        static void Place(Grid grid, FrameworkElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
    }

    /// <summary>"Artist · Album · 4:13 · Synced".</summary>
    private static string Details(LrclibTrack track, bool withArtist) =>
        string.Join(" · ", new[] { withArtist ? Source(track) : track.AlbumName, Duration(track), Kind(track) }.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>"Artist · Album".</summary>
    private static string Source(LrclibTrack track) =>
        string.Join(" · ", new[] { track.ArtistName, track.AlbumName }.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Duration(LrclibTrack track) =>
        track.Duration is double duration && duration > 0 ? Utils.SecondsToString(duration) : "";

    private static string Kind(LrclibTrack track) =>
        track.HasSynced ? Strings.ResultSynced
        : track.HasLyrics ? Strings.ResultNotSynced
        : track.Instrumental ? Strings.LyricsInstrumental
        : "";

    private static string TrackName(LrclibTrack track) =>
        string.IsNullOrWhiteSpace(track.ArtistName) ? track.TrackName ?? "" : $"{track.TrackName} — {track.ArtistName}";

    private void OnResultSelected(object sender, SelectionChangedEventArgs e) =>
        ShowPreview((ResultsList.SelectedItem as ListViewItem)?.Tag as LrclibTrack);

    private void ShowPreview(LrclibTrack? track)
    {
        _selected = track;
        PreviewText.Inlines.Clear();
        PreviewHeader.Visibility = track is null ? Visibility.Collapsed : Visibility.Visible;
        if (track is null)
        {
            AddSecondaryText(PreviewText, Strings.SelectResult);
        }
        else
        {
            PreviewTitle.Text = TrackName(track);
            PreviewDetails.Text = Details(track, withArtist: false);
            if (App.Services.Lyrics.ToLyrics(track) is { } lyrics)
                FillLines(PreviewText, lyrics);
            else
                AddSecondaryText(PreviewText, track.Instrumental ? Strings.LyricsInstrumental : Strings.LyricsNotFound);
        }

        PreviewScroller.ChangeView(null, 0, null, disableAnimation: true);
        UpdateDownloadButton();
    }

    /// <summary>
    /// Only lyrics can be downloaded. Over local lyrics the button says it replaces them,
    /// else where they go (Preferences: the song's folder or the lyrics cache).
    /// </summary>
    private void UpdateDownloadButton()
    {
        var (file, exists) = App.Services.Lyrics.SaveTarget(_song, _files);
        DownloadButton.IsEnabled = _selected is { HasLyrics: true };
        DownloadText.Text = exists ? Strings.ReplaceLocalLyrics
            : file == _cacheFile ? Strings.DownloadToCache
            : Strings.DownloadToLocal;
    }

    private void OnResultsAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= 520;
        ResultsColumn.Width = wide ? new GridLength(240) : new GridLength(1, GridUnitType.Star);
        PreviewColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ResultsRow.Height = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(150);
        PreviewRow.Height = wide ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ResultsArea.ColumnSpacing = wide ? 12 : 0;
        ResultsArea.RowSpacing = wide ? 0 : 12;
        Grid.SetColumn(PreviewCard, wide ? 1 : 0);
        Grid.SetRow(PreviewCard, wide ? 0 : 1);
    }

    /// <summary>
    /// Saves the selected lyrics as the song's local lyrics (see <see cref="LyricsService.SaveTarget"/>).
    /// A file that is there, maybe the user's own, is only replaced once confirmed.
    /// </summary>
    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not { HasLyrics: true } track)
            return;

        var (file, exists) = App.Services.Lyrics.SaveTarget(_song, _files);
        if (!exists)
        {
            _ = SaveLyricsAsync(track, file);
            return;
        }

        var replace = new Button { Content = Strings.Replace, Style = Resource<Style>("AccentButtonStyle") };
        var confirm = new StackPanel { Spacing = 12, MaxWidth = 300 };
        confirm.Children.Add(new TextBlock
        {
            Text = file == _cacheFile ? Strings.ReplaceCachedLyricsConfirm : Strings.ReplaceLyricsConfirm(Path.GetFileName(file)),
            TextWrapping = TextWrapping.Wrap,
        });
        confirm.Children.Add(replace);
        var flyout = new Flyout { Content = confirm };
        replace.Click += (_, _) =>
        {
            flyout.Hide();
            _ = SaveLyricsAsync(track, file);
        };
        flyout.ShowAt(DownloadButton);
    }

    /// <summary>Back at the lyrics once saved: they show the file, or say why they do not.</summary>
    private async Task SaveLyricsAsync(LrclibTrack track, string file)
    {
        DownloadButton.IsEnabled = false;
        DownloadStatus.Text = "";
        try
        {
            await App.Services.Lyrics.SaveAsync(_song, track, file);
            _lyricsStale = true;
            CloseSearch();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Cannot save the lyrics as {file}: {ex.Message}");
            DownloadStatus.Text = Strings.LyricsSaveFailed(ex.Message);
            UpdateDownloadButton();
        }
    }

    // ------------------------------------------------------------------
    // Lines of lyrics
    // ------------------------------------------------------------------

    /// <summary>The lines, synced ones after their times (m:ss) in the secondary color; pauses are left out.</summary>
    private static void FillLines(TextBlock target, Lyrics lyrics)
    {
        var times = Resource<Brush>("TextFillColorSecondaryBrush");
        bool first = true;
        foreach (var line in lyrics.Lines)
        {
            if (line.Text.Length == 0 && lyrics.IsSynced)
                continue;

            if (!first)
                target.Inlines.Add(new LineBreak());
            first = false;
            if (line.Time is { } time)
                target.Inlines.Add(new Run { Text = $"{(int)time.TotalMinutes}:{time.Seconds:00} ", Foreground = times });
            target.Inlines.Add(new Run { Text = line.Text });
        }
    }

    private static void AddSecondaryLine(TextBlock target, string text)
    {
        target.Inlines.Add(new LineBreak());
        AddSecondaryText(target, text);
    }

    private static void AddSecondaryText(TextBlock target, string text) =>
        target.Inlines.Add(new Run { Text = text, Foreground = Resource<Brush>("TextFillColorSecondaryBrush") });

    // ------------------------------------------------------------------
    // Rows
    // ------------------------------------------------------------------

    /// <summary>A heading and a card of rows, as in the preferences.</summary>
    private static void AddGroup(Panel page, string title, bool first, IReadOnlyList<FrameworkElement> rows)
    {
        page.Children.Add(new TextBlock
        {
            Text = title,
            Style = Resource<Style>("StrongLabelStyle"),
            Margin = new Thickness(0, first ? 0 : 18, 0, 8),
        });

        var stack = new StackPanel();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                stack.Children.Add(new Border { Height = 1, Background = Resource<Microsoft.UI.Xaml.Media.Brush>("DividerStrokeColorDefaultBrush") });
            stack.Children.Add(rows[i]);
        }

        page.Children.Add(new Border { Style = Resource<Style>("CardStyle"), Child = stack });
    }

    private static Grid Row(string label, string? value) => Row(label, Value(value));

    /// <summary>The label on the left, the value (selectable) on the right.</summary>
    private static Grid Row(string label, FrameworkElement value)
    {
        var row = new Grid { Padding = new Thickness(14, 9, 14, 9), MinHeight = 40, ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            Style = Resource<Style>("SecondaryLabelStyle"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    /// <summary>A value; one that is missing shows as a dash.</summary>
    private static TextBlock Value(string? text) => string.IsNullOrWhiteSpace(text) ? Missing("—") : new()
    {
        Text = text,
        Style = Resource<Style>("BodyLabelStyle"),
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    /// <summary>What stands in for a missing value ("—", "Never", …), dimmed.</summary>
    private static TextBlock Missing(string text) => new()
    {
        Text = text,
        Style = Resource<Style>("SecondaryLabelStyle"),
        TextWrapping = TextWrapping.Wrap,
    };

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    private static string? NumberOf(string? number, string? total) =>
        number is null ? null : total is null ? number : Strings.PropNumberOf(number, total);

    private static string Date(DateTime date) => date.ToString("g", CultureInfo.CurrentCulture);

    private static string FileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };

    private static string? Decibels(double? gain) =>
        gain is { } g ? g.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " dB" : null;

    private static string? Peak(double? peak) => peak?.ToString("0.000000", CultureInfo.InvariantCulture);

    private static string ChannelName(int channels) => channels switch
    {
        1 => Strings.MonoChannel,
        2 => Strings.StereoChannels,
        6 => "5.1",
        8 => "7.1",
        _ => channels.ToString(CultureInfo.CurrentCulture),
    };
}
