// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Controls;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services;
using Gnome_Music_WinUI.Services.Audio;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Gnome_Music_WinUI.Views;

/// <summary>
/// Preferences (widgets/preferencesdialog.py) as a view of the main page, like the
/// albums, artists and playlists, rather than a dialog; the title bar's ☰ shows it
/// where GNOME Music has its primary menu. The categories are in a sidebar, as Windows
/// Settings has them, and changes apply immediately. Besides GNOME Music's player and
/// power settings: the channel processing, the music output (interface, device, what the
/// device takes, DSD), the player's features, the music folders and a reset page (the
/// lyrics cache, the play counts, the settings), all of the Windows port. At the
/// sidebar's foot, what the primary menu held besides: the keyboard shortcuts
/// (shortcuts-dialog.ui) and About (about.py) with the help.
/// </summary>
public sealed partial class PreferencesView : UserControl
{
    private readonly Settings _settings = App.Services.Settings;
    private readonly DispatcherQueueTimer _gainTimer;
    private string _category = "playback";
    private bool _loading;
    private bool _applying;
    private bool _loadingDevices;
    private int _devicesLookup;
    private int _probe;
    private DeviceCapabilities? _caps;

    public PreferencesView()
    {
        InitializeComponent();

        // The gain takes effect once the slider rests: a DSD song playing is reopened for it.
        _gainTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _gainTimer.Interval = TimeSpan.FromMilliseconds(300);
        _gainTimer.IsRepeating = false;
        _gainTimer.Tick += (_, _) => Apply(() => _settings.DsdGainDb = GainSlider.Value);

        // The view stays: what changes elsewhere shows here too (the repeat mode from the
        // player bar and its shortcut, the output from the player bar's output button, the
        // folders from the welcome page).
        _settings.PropertyChanged += OnSettingsChanged;

        LoadValues();
        BuildAbout();
        Categories.SelectedIndex = 0;
    }

    /// <summary>The view came up: the settings and the page are read again.</summary>
    public void OnShown()
    {
        LoadValues();
        RefreshPage();
    }

    /// <summary>Selects a category by its tag ("playback", …, "shortcuts", "about").</summary>
    public void ShowCategory(string tag)
    {
        foreach (var list in new[] { Categories, FooterCategories })
        {
            var item = list.Items.OfType<ListViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
            if (item is not null)
            {
                list.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>Keyboard focus on the selected category, as the view comes up from a shortcut.</summary>
    public void FocusCategory()
    {
        var list = Categories.SelectedItem is not null ? Categories : FooterCategories;
        if (list.SelectedItem is ListViewItem item)
            item.Focus(FocusState.Keyboard);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        SidebarColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width * 0.25, 220, 280));

    /// <summary>One selection across the two lists of the sidebar.</summary>
    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = (ListView)sender;
        if (list.SelectedItem is not ListViewItem { Tag: string tag })
            return;

        (list == Categories ? FooterCategories : Categories).SelectedItem = null;
        _category = tag;
        var pages = new (string Tag, FrameworkElement Page, string Title)[]
        {
            ("playback", PlaybackPage, Strings.PrefsPlayback),
            ("output", OutputPage, Strings.PrefsOutput),
            ("controls", ControlsPage, Strings.PrefsControls),
            ("folders", FoldersPage, Strings.MusicFolders),
            ("reset", ResetPage, Strings.PrefsReset),
            ("shortcuts", ShortcutsPage, Strings.KeyboardShortcuts),
            ("about", AboutPage, Strings.About),
        };
        foreach (var (pageTag, page, title) in pages)
        {
            page.Visibility = pageTag == tag ? Visibility.Visible : Visibility.Collapsed;
            if (pageTag == tag)
                PageTitle.Text = title;
        }

        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
        RefreshPage();
    }

    /// <summary>What may have changed since the page last showed: the devices, the features, the folders, the lyrics cache.</summary>
    private void RefreshPage()
    {
        switch (_category)
        {
            case "output":
                _ = LoadDevicesAsync();
                break;
            case "shortcuts":
                BuildShortcuts();
                break;
            case "folders":
                UpdateFolders();
                break;
            case "reset":
                _ = UpdateLyricsCacheAsync();
                break;
        }
    }

    /// <summary>The settings into the controls, without applying them back.</summary>
    private void LoadValues()
    {
        _loading = true;
        RepeatCombo.SelectedIndex = (int)_settings.Repeat;
        ReplayGainCombo.SelectedIndex = (int)_settings.ReplayGain;
        InhibitSwitch.IsOn = _settings.InhibitSuspend;
        InvertSwitch.IsOn = _settings.PhaseInvert;
        MonoSwitch.IsOn = _settings.MonoOutput;
        SwapSwitch.IsOn = _settings.SwapChannels;

        ApiCombo.SelectedIndex = (int)_settings.OutputApi;
        ExclusiveSwitch.IsOn = _settings.WasapiExclusive;
        DsdCombo.SelectedIndex = (int)_settings.DsdMode;
        if (!_gainTimer.IsRunning)
            GainSlider.Value = _settings.DsdGainDb;
        UpdateGainText();
        UpdateOutputRows();

        MiniPlayerSwitch.IsOn = _settings.MiniPlayerEnabled;
        MiniPlayerModeCombo.SelectedIndex = (int)_settings.MiniPlayerMode;
        UpdateMiniPlayerOptions();
        OutputButtonSwitch.IsOn = _settings.OutputButtonEnabled;
        VolumeSwitch.IsOn = _settings.VolumeControlEnabled;
        LyricsSwitch.IsOn = _settings.LyricsEnabled;
        LocalLyricsSwitch.IsOn = _settings.LoadLocalLyrics;
        DownloadLyricsSwitch.IsOn = _settings.DownloadLyrics;
        LyricsLocationCombo.SelectedIndex = (int)_settings.LyricsLocation;
        RememberMissingLyricsSwitch.IsOn = _settings.RememberMissingLyrics;
        UpdateLyricsOptions();
        FormatSwitch.IsOn = _settings.ShowAudioFormat;
        UpdateFolders();
        _loading = false;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || _applying)
            return;

        if (e.PropertyName == nameof(Settings.Repeat))
        {
            _loading = true;
            RepeatCombo.SelectedIndex = (int)_settings.Repeat;
            _loading = false;
        }
        else if (e.PropertyName == nameof(Settings.OutputSettings))
        {
            ShowOutput();
        }
        else if (e.PropertyName == nameof(Settings.LibraryFolders))
        {
            UpdateFolders();
        }
    }

    /// <summary>A change made here: <see cref="OnSettingsChanged"/> leaves the controls as they are.</summary>
    private void Apply(Action change)
    {
        _applying = true;
        try
        {
            change();
        }
        finally
        {
            _applying = false;
        }
    }

    private OutputApi Api => (OutputApi)Math.Max(0, ApiCombo.SelectedIndex);

    private DsdMode Dsd => (DsdMode)Math.Max(0, DsdCombo.SelectedIndex);

    // ------------------------------------------------------------------
    // Playback
    // ------------------------------------------------------------------

    private void OnRepeatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && RepeatCombo.SelectedIndex >= 0)
            App.Services.Player.RepeatMode = (RepeatMode)RepeatCombo.SelectedIndex;
    }

    private void OnReplayGainChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ReplayGainCombo.SelectedIndex >= 0)
            _settings.ReplayGain = (ReplayGainMode)ReplayGainCombo.SelectedIndex;
    }

    private void OnProcessingToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _settings.PhaseInvert = InvertSwitch.IsOn;
        _settings.MonoOutput = MonoSwitch.IsOn;
        _settings.SwapChannels = SwapSwitch.IsOn;
    }

    private void OnInhibitToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            _settings.InhibitSuspend = InhibitSwitch.IsOn;
    }

    // ------------------------------------------------------------------
    // Music output
    // ------------------------------------------------------------------

    private void OnApiChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ApiCombo.SelectedIndex < 0)
            return;
        Apply(() => _settings.OutputApi = Api);
        UpdateOutputRows();
        _ = LoadDevicesAsync();
    }

    private void OnExclusiveToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        Apply(() => _settings.WasapiExclusive = ExclusiveSwitch.IsOn);
        UpdateOutputRows();
        ShowCapabilities();
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _loadingDevices || DeviceCombo.SelectedItem is not AudioDevice device)
            return;
        Apply(() => _settings.SetOutputDevice(Api, device.Id));
        _ = ProbeAsync();
    }

    /// <summary>
    /// The interface, exclusive mode or device chosen with the player bar's output button:
    /// the controls follow, and on the page the devices and what they take show again
    /// (else when it shows).
    /// </summary>
    private void ShowOutput()
    {
        var api = _settings.OutputApi;
        bool otherDevice = api != Api || (DeviceCombo.SelectedItem as AudioDevice)?.Id != _settings.GetOutputDevice(api);
        _loading = true;
        ApiCombo.SelectedIndex = (int)api;
        ExclusiveSwitch.IsOn = _settings.WasapiExclusive;
        _loading = false;
        UpdateOutputRows();

        if (_category != "output" || Visibility != Visibility.Visible)
            return;
        if (otherDevice)
            _ = LoadDevicesAsync();
        else
            ShowCapabilities();
    }

    private void OnDsdModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DsdCombo.SelectedIndex < 0)
            return;
        Apply(() => _settings.DsdMode = Dsd);
        UpdateDsdRows();
    }

    private void OnGainChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateGainText();
        if (_loading)
            return;
        _gainTimer.Stop();
        _gainTimer.Start();
    }

    private void UpdateGainText() =>
        GainText.Text = GainSlider.Value > 0 ? $"+{GainSlider.Value:0.0} dB" : "0.0 dB";

    private void UpdateOutputRows()
    {
        ExclusiveRow.Visibility = Api == OutputApi.Wasapi ? Visibility.Visible : Visibility.Collapsed;
        UpdateDsdRows();
    }

    /// <summary>
    /// Whether the output allows the chosen DSD mode: only then is there something to
    /// say (the settings have no explanations), and the gain shows for PCM.
    /// </summary>
    private void UpdateDsdRows()
    {
        var mode = Dsd;
        bool possible = mode switch
        {
            DsdMode.Dop => Api == OutputApi.Asio || Api == OutputApi.Wasapi && ExclusiveSwitch.IsOn,
            DsdMode.Native => Api == OutputApi.Asio,
            _ => true,
        };
        DsdModeDescription.Text = possible ? "" : Strings.DsdNotPossible;
        DsdModeDescription.Visibility = possible ? Visibility.Collapsed : Visibility.Visible;
        GainRow.Visibility = mode == DsdMode.ConvertToPcm || !possible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The devices of the chosen interface (the default one first), then what the chosen one takes.</summary>
    private async Task LoadDevicesAsync()
    {
        var api = Api;
        int lookup = ++_devicesLookup;
        var items = await OutputDevices.ListAsync(api);
        if (lookup != _devicesLookup)
            return;

        _loadingDevices = true;
        DeviceCombo.ItemsSource = items;
        DeviceCombo.IsEnabled = items.Count > 0;
        DeviceCombo.PlaceholderText = items.Count == 0 ? Strings.NoAsioDriver : "";
        DeviceCombo.SelectedItem = OutputDevices.Chosen(items, _settings.GetOutputDevice(api));
        _loadingDevices = false;
        await ProbeAsync();
    }

    private async Task ProbeAsync()
    {
        int probe = ++_probe;
        var api = Api;
        string? id = (DeviceCombo.SelectedItem as AudioDevice)?.Id;
        RatesText.Text = DepthsText.Text = DsdText.Text = ChannelsText.Text = Strings.Checking;
        FormatsNoteRow.Visibility = Visibility.Collapsed;

        DeviceCapabilities caps;
        if (api == OutputApi.Asio && DeviceCombo.Items.Count == 0)
        {
            caps = new DeviceCapabilities { Error = Strings.NoAsioDriver };
        }
        else
        {
            caps = await Task.Run(() => api switch
            {
                OutputApi.DirectSound => Wasapi.Probe(Wasapi.EndpointOfDirectSoundDevice(Guid.TryParse(id, out var guid) ? guid : null)),
                OutputApi.Asio => AsioOutput.Probe(id),
                _ => Wasapi.Probe(id),
            });
        }

        if (probe != _probe)
            return;
        _caps = caps;
        ShowCapabilities();
    }

    private void ShowCapabilities()
    {
        if (_caps is not { } caps)
            return;

        const string Separator = ", ";
        string none = Strings.NotSupported;

        // A device without exclusive formats (remote, virtual) plays what the mixer takes.
        bool mixerOnly = caps.PcmRates.Count == 0 && caps.MixRate > 0 && caps.Error is null && Api != OutputApi.Asio;
        var rates = mixerOnly ? new List<int> { caps.MixRate } : caps.PcmRates;
        var depths = mixerOnly ? new List<int> { caps.MixBitDepth } : caps.BitDepths;
        var channels = mixerOnly ? new List<int> { caps.MixChannels } : caps.Channels;
        RatesText.Text = rates.Count > 0 ? string.Join(Separator, rates.Select(AudioFormats.RateText)) : none;
        DepthsText.Text = depths.Count(d => d != 0) > 0
            ? string.Join(Separator, depths.Where(d => d != 0).OrderBy(d => Math.Abs(d)).Select(BitsName))
            : none;

        var dsd = new List<string>();
        if (caps.NativeDsdRates.Count > 0)
            dsd.Add($"{string.Join(Separator, caps.NativeDsdRates.Select(AudioFormats.DsdName))} ({Strings.NativeSuffix})");
        if (caps.DopRates.Count > 0)
            dsd.Add($"{string.Join(Separator, caps.DopRates.Select(AudioFormats.DsdName))} ({Strings.DopSuffix})");
        DsdText.Text = dsd.Count > 0 ? string.Join("\n", dsd) : none;
        ChannelsText.Text = channels.Count > 0 ? string.Join(Separator, channels.Select(ChannelName)) : none;

        // Only what went wrong: the settings have no explanations.
        var notes = new List<string>();
        if (caps.Error is { } error)
            notes.Add(error);
        FormatsNote.Text = string.Join("\n", notes);
        FormatsNoteRow.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BitsName(int bits) => bits > 0 ? Strings.IntBits(bits) : Strings.FloatBits(-bits);

    private static string ChannelName(int channels) => channels switch
    {
        1 => Strings.MonoChannel,
        2 => Strings.StereoChannels,
        6 => "5.1",
        8 => "7.1",
        _ => channels.ToString(System.Globalization.CultureInfo.CurrentCulture),
    };

    // ------------------------------------------------------------------
    // Player controls
    // ------------------------------------------------------------------

    private void OnFeatureToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _settings.MiniPlayerEnabled = MiniPlayerSwitch.IsOn;
        _settings.OutputButtonEnabled = OutputButtonSwitch.IsOn;
        _settings.VolumeControlEnabled = VolumeSwitch.IsOn;
        _settings.LyricsEnabled = LyricsSwitch.IsOn;
        _settings.ShowAudioFormat = FormatSwitch.IsOn;
        UpdateMiniPlayerOptions();
        UpdateLyricsOptions();
    }

    private void OnMiniPlayerModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && MiniPlayerModeCombo.SelectedIndex >= 0)
            _settings.MiniPlayerMode = (MiniPlayerMode)MiniPlayerModeCombo.SelectedIndex;
        UpdateMiniPlayerOptions();
    }

    /// <summary>The mode needs the mini player; the note is for the lyrics mode.</summary>
    private void UpdateMiniPlayerOptions()
    {
        MiniPlayerModeCombo.IsEnabled = MiniPlayerSwitch.IsOn;
        Dim(MiniPlayerModeLabel, MiniPlayerSwitch.IsOn);
        MiniPlayerLyricsNote.Visibility = MiniPlayerModeCombo.SelectedIndex == (int)MiniPlayerMode.Lyrics
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnLyricsOptionToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _settings.LoadLocalLyrics = LocalLyricsSwitch.IsOn;
        _settings.DownloadLyrics = DownloadLyricsSwitch.IsOn;
        _settings.RememberMissingLyrics = RememberMissingLyricsSwitch.IsOn;
        UpdateLyricsOptions();
    }

    private void OnLyricsLocationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && LyricsLocationCombo.SelectedIndex >= 0)
            _settings.LyricsLocation = (LyricsLocation)LyricsLocationCombo.SelectedIndex;
    }

    /// <summary>
    /// The lyrics options need the lyrics; where downloads go needs the downloads too
    /// (LRCLIB is asked either way, so not asking it again does not).
    /// </summary>
    private void UpdateLyricsOptions()
    {
        bool enabled = LyricsSwitch.IsOn;
        bool downloads = enabled && DownloadLyricsSwitch.IsOn;
        LocalLyricsSwitch.IsEnabled = DownloadLyricsSwitch.IsEnabled = RememberMissingLyricsSwitch.IsEnabled = enabled;
        LyricsLocationCombo.IsEnabled = downloads;
        Dim(LocalLyricsLabel, enabled);
        Dim(DownloadLyricsLabel, enabled);
        Dim(LyricsLocationLabel, downloads);
        Dim(RememberMissingLyricsLabel, enabled);
    }

    /// <summary>A disabled setting's name in the disabled text color.</summary>
    private static void Dim(TextBlock text, bool enabled)
    {
        if (enabled)
            text.ClearValue(TextBlock.ForegroundProperty);
        else
            text.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorDisabledBrush"];
    }

    // ------------------------------------------------------------------
    // Music folders
    // ------------------------------------------------------------------

    private void UpdateFolders() => FoldersList.ItemsSource = _settings.LibraryFolders.ToList();

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (await LibraryFolders.AddFolderAsync())
            UpdateFolders();
    }

    private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string folder })
        {
            LibraryFolders.Remove(folder);
            UpdateFolders();
        }
    }

    // ------------------------------------------------------------------
    // Reset
    // ------------------------------------------------------------------

    /// <summary>What the lyrics cache holds now; the button is only there for a cache with files.</summary>
    private async Task UpdateLyricsCacheAsync()
    {
        var (files, bytes) = await Task.Run(LyricsService.CacheSize);
        LyricsCacheSize.Text = files == 0 ? Strings.LyricsCacheEmpty : Strings.LyricsCacheSize(files, FileSize(bytes));
        ClearLyricsCacheButton.IsEnabled = files > 0;
    }

    private void OnClearLyricsCacheClick(object sender, RoutedEventArgs e) =>
        Confirm(ClearLyricsCacheButton, Strings.ClearLyricsCacheConfirm, Strings.Clear, async () =>
        {
            int deleted = await App.Services.Lyrics.ClearCacheAsync();
            App.MainWindow?.ShowToast(new Toast(Strings.LyricsCacheCleared(deleted)));
            await UpdateLyricsCacheAsync();
        });

    private void OnClearPlayCountsClick(object sender, RoutedEventArgs e) =>
        Confirm(ClearPlayCountsButton, Strings.ClearPlayCountsConfirm, Strings.Clear, () =>
        {
            App.Services.Model.ClearPlayCounts();
            App.MainWindow?.ShowToast(new Toast(Strings.PlayCountsCleared));
        });

    /// <summary>Every preference back to its default: the pages show them at once.</summary>
    private void OnResetSettingsClick(object sender, RoutedEventArgs e) =>
        Confirm(ResetSettingsButton, Strings.ResetSettingsConfirm, Strings.ResetAction, () =>
        {
            Apply(_settings.ResetToDefaults);
            LoadValues();
            App.MainWindow?.ShowToast(new Toast(Strings.SettingsReset));
        });

    /// <summary>Asks first, in a flyout at the button: what happens, and the action as an accent button.</summary>
    private static void Confirm(Button anchor, string message, string action, Action confirmed)
    {
        var button = new Button { Content = action, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var panel = new StackPanel { Spacing = 12, MaxWidth = 300 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(button);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        button.Click += (_, _) =>
        {
            flyout.Hide();
            confirmed();
        };
        flyout.ShowAt(anchor);
    }

    private static string FileSize(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };
}
