// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services.Audio;

namespace Gnome_Music_WinUI.Services;

/// <summary>ReplayGain mode (gschema "replaygain": disabled, album, track).</summary>
public enum ReplayGainMode
{
    Disabled,
    Album,
    Track,
}

/// <summary>Where downloaded lyrics are kept (Windows port).</summary>
public enum LyricsLocation
{
    /// <summary>The .lrc file named like the song, in its folder.</summary>
    SongFolder,

    /// <summary>The app's lyrics cache, in its data folder.</summary>
    Cache,
}

/// <summary>What the mini player shows (Windows port).</summary>
public enum MiniPlayerMode
{
    /// <summary>The cover; the controls come over its blurred copy while pointed at.</summary>
    Cover,

    /// <summary>The controls, over the blurred cover, all the time.</summary>
    Controls,

    /// <summary>The lyrics, as the lyrics page shows them; the cover where there are none.</summary>
    Lyrics,
}

public sealed class SettingsData
{
    public int[]? WindowSize { get; set; }

    /// <summary>Null until the window was closed once (gschema default: true).</summary>
    public bool? WindowMaximized { get; set; }
    public RepeatMode Repeat { get; set; }
    public ReplayGainMode ReplayGain { get; set; }
    public bool InhibitSuspend { get; set; }
    public List<string>? LibraryFolders { get; set; }

    // Windows port: the output and the processing
    public OutputApi OutputApi { get; set; }
    public string? WasapiDevice { get; set; }
    public string? DirectSoundDevice { get; set; }
    public string? AsioDevice { get; set; }
    public bool WasapiExclusive { get; set; }
    public DsdMode DsdMode { get; set; }
    public double DsdGainDb { get; set; }
    public bool PhaseInvert { get; set; }
    public bool MonoOutput { get; set; }
    public bool SwapChannels { get; set; }

    // Windows port: what the player shows (null: the default, on)
    public bool ShowAudioFormat { get; set; }
    public bool? MiniPlayerEnabled { get; set; }
    public MiniPlayerMode MiniPlayerMode { get; set; }
    public bool? OutputButtonEnabled { get; set; }
    public bool? VolumeControlEnabled { get; set; }
    public bool? LyricsEnabled { get; set; }
    public bool LoadLocalLyrics { get; set; }
    public bool DownloadLyrics { get; set; }
    public LyricsLocation LyricsLocation { get; set; }
    public bool RememberMissingLyrics { get; set; }
}

/// <summary>
/// Application settings: the keys of org.gnome.Music.gschema.xml plus the
/// Windows-only list of music folders (Tracker decides the indexed folders on Linux).
/// </summary>
public sealed class Settings : ObservableObject
{
    /// <summary>gschema default of "window-size".</summary>
    public static readonly int[] DefaultWindowSize = { 768, 600 };

    private readonly SettingsData _data;
    private readonly DebouncedSaver _saver;

    public Settings()
    {
        _data = JsonStorage.Load(AppPaths.SettingsFile, AppJsonContext.Default.SettingsData) ?? new SettingsData();
        _saver = new DebouncedSaver(Save, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>Window size in DIPs (gschema "window-size").</summary>
    public int[] WindowSize
    {
        get => _data.WindowSize is { Length: 2 } size && size[0] > 0 && size[1] > 0 ? size : DefaultWindowSize;
        set => Set(() => _data.WindowSize = value);
    }

    /// <summary>gschema "window-maximized".</summary>
    public bool WindowMaximized
    {
        get => _data.WindowMaximized ?? true;
        set => Set(() => _data.WindowMaximized = value, _data.WindowMaximized != value);
    }

    /// <summary>gschema "repeat".</summary>
    public RepeatMode Repeat
    {
        get => _data.Repeat;
        set => Set(() => _data.Repeat = value, _data.Repeat != value);
    }

    /// <summary>gschema "replaygain".</summary>
    public ReplayGainMode ReplayGain
    {
        get => _data.ReplayGain;
        set => Set(() => _data.ReplayGain = value, _data.ReplayGain != value);
    }

    /// <summary>gschema "inhibit-suspend".</summary>
    public bool InhibitSuspend
    {
        get => _data.InhibitSuspend;
        set => Set(() => _data.InhibitSuspend = value, _data.InhibitSuspend != value);
    }

    /// <summary>Folders scanned for music. Defaults to the user's Music folder.</summary>
    public IReadOnlyList<string> LibraryFolders
    {
        get => _data.LibraryFolders ?? DefaultLibraryFolders();
        set => Set(() => _data.LibraryFolders = value.ToList());
    }

    // ------------------------------------------------------------------
    // Windows port: music output
    // ------------------------------------------------------------------

    /// <summary>The audio interface the music goes out through.</summary>
    public OutputApi OutputApi
    {
        get => _data.OutputApi;
        set => Set(() => _data.OutputApi = value, _data.OutputApi != value);
    }

    /// <summary>The chosen device of an audio interface; null for the default one.</summary>
    public string? GetOutputDevice(OutputApi api) => api switch
    {
        OutputApi.DirectSound => _data.DirectSoundDevice,
        OutputApi.Asio => _data.AsioDevice,
        _ => _data.WasapiDevice,
    };

    public void SetOutputDevice(OutputApi api, string? device)
    {
        if (GetOutputDevice(api) == device)
            return;

        Set(() =>
        {
            if (api == OutputApi.DirectSound)
                _data.DirectSoundDevice = device;
            else if (api == OutputApi.Asio)
                _data.AsioDevice = device;
            else
                _data.WasapiDevice = device;
        }, name: nameof(OutputSettings));
    }

    /// <summary>WASAPI exclusive mode: the device alone, at the file's own format.</summary>
    public bool WasapiExclusive
    {
        get => _data.WasapiExclusive;
        set => Set(() => _data.WasapiExclusive = value, _data.WasapiExclusive != value);
    }

    public DsdMode DsdMode
    {
        get => _data.DsdMode;
        set => Set(() => _data.DsdMode = value, _data.DsdMode != value);
    }

    /// <summary>The gain of DSD converted to PCM, 0 to +6 dB.</summary>
    public double DsdGainDb
    {
        get => Math.Clamp(_data.DsdGainDb, 0, 6);
        set => Set(() => _data.DsdGainDb = Math.Clamp(value, 0, 6), _data.DsdGainDb != value);
    }

    /// <summary>The output as the settings above choose it.</summary>
    public OutputSettings OutputSettings => new(OutputApi, GetOutputDevice(OutputApi), WasapiExclusive, DsdMode, DsdGainDb);

    // ------------------------------------------------------------------
    // Windows port: playback processing
    // ------------------------------------------------------------------

    /// <summary>Inverts the polarity of every channel.</summary>
    public bool PhaseInvert
    {
        get => _data.PhaseInvert;
        set => Set(() => _data.PhaseInvert = value, _data.PhaseInvert != value);
    }

    /// <summary>Left and right mixed, out of both.</summary>
    public bool MonoOutput
    {
        get => _data.MonoOutput;
        set => Set(() => _data.MonoOutput = value, _data.MonoOutput != value);
    }

    public bool SwapChannels
    {
        get => _data.SwapChannels;
        set => Set(() => _data.SwapChannels = value, _data.SwapChannels != value);
    }

    // ------------------------------------------------------------------
    // Windows port: the player's features (all on by default)
    // ------------------------------------------------------------------

    /// <summary>The bit depth and sample rate under the song in the player bar (off by default).</summary>
    public bool ShowAudioFormat
    {
        get => _data.ShowAudioFormat;
        set => Set(() => _data.ShowAudioFormat = value, _data.ShowAudioFormat != value);
    }

    public bool MiniPlayerEnabled
    {
        get => _data.MiniPlayerEnabled ?? true;
        set => Set(() => _data.MiniPlayerEnabled = value, MiniPlayerEnabled != value);
    }

    /// <summary>What the mini player shows: the cover (the default), the controls or the lyrics.</summary>
    public MiniPlayerMode MiniPlayerMode
    {
        get => _data.MiniPlayerMode;
        set => Set(() => _data.MiniPlayerMode = value, _data.MiniPlayerMode != value);
    }

    /// <summary>The player bar's button for the audio interface, the device and exclusive mode.</summary>
    public bool OutputButtonEnabled
    {
        get => _data.OutputButtonEnabled ?? true;
        set => Set(() => _data.OutputButtonEnabled = value, OutputButtonEnabled != value);
    }

    /// <summary>The volume button and shortcuts; off, the music plays at full volume.</summary>
    public bool VolumeControlEnabled
    {
        get => _data.VolumeControlEnabled ?? true;
        set => Set(() => _data.VolumeControlEnabled = value, VolumeControlEnabled != value);
    }

    /// <summary>Clicking the song in the player bar shows its lyrics.</summary>
    public bool LyricsEnabled
    {
        get => _data.LyricsEnabled ?? true;
        set => Set(() => _data.LyricsEnabled = value, LyricsEnabled != value);
    }

    /// <summary>
    /// Lyrics come from the song's lyrics file first (the LRC file with its name in its
    /// folder), then from the lyrics cache, from LRCLIB only without either (off by
    /// default: always LRCLIB).
    /// </summary>
    public bool LoadLocalLyrics
    {
        get => _data.LoadLocalLyrics;
        set => Set(() => _data.LoadLocalLyrics = value, _data.LoadLocalLyrics != value);
    }

    /// <summary>Lyrics found on LRCLIB are kept where <see cref="LyricsLocation"/> says, when there are none (off by default).</summary>
    public bool DownloadLyrics
    {
        get => _data.DownloadLyrics;
        set => Set(() => _data.DownloadLyrics = value, _data.DownloadLyrics != value);
    }

    /// <summary>Where downloaded lyrics go: the song's folder (the default) or the app's lyrics cache.</summary>
    public LyricsLocation LyricsLocation
    {
        get => _data.LyricsLocation;
        set => Set(() => _data.LyricsLocation = value, _data.LyricsLocation != value);
    }

    /// <summary>
    /// Songs LRCLIB had no lyrics for are remembered (in the lyrics cache) and not looked
    /// up again: without local lyrics they show none at once (off by default).
    /// </summary>
    public bool RememberMissingLyrics
    {
        get => _data.RememberMissingLyrics;
        set => Set(() => _data.RememberMissingLyrics = value, _data.RememberMissingLyrics != value);
    }

    /// <summary>
    /// Every preference back to its default (Preferences → Reset). The music folders and
    /// the window's size stay. Only what changes is announced, so a song playing keeps
    /// its output unless that changed.
    /// </summary>
    public void ResetToDefaults()
    {
        var output = OutputSettings;
        var changed = new List<string>();
        void Reset(string name, bool isDefault, Action reset)
        {
            if (isDefault)
                return;
            reset();
            changed.Add(name);
        }

        Reset(nameof(Repeat), _data.Repeat == default, () => _data.Repeat = default);
        Reset(nameof(ReplayGain), _data.ReplayGain == default, () => _data.ReplayGain = default);
        Reset(nameof(InhibitSuspend), !_data.InhibitSuspend, () => _data.InhibitSuspend = false);
        Reset(nameof(OutputApi), _data.OutputApi == default, () => _data.OutputApi = default);
        bool devices = _data.WasapiDevice is not null || _data.DirectSoundDevice is not null || _data.AsioDevice is not null;
        _data.WasapiDevice = _data.DirectSoundDevice = _data.AsioDevice = null;
        Reset(nameof(WasapiExclusive), !_data.WasapiExclusive, () => _data.WasapiExclusive = false);
        Reset(nameof(DsdMode), _data.DsdMode == default, () => _data.DsdMode = default);
        Reset(nameof(DsdGainDb), _data.DsdGainDb == 0, () => _data.DsdGainDb = 0);
        Reset(nameof(PhaseInvert), !_data.PhaseInvert, () => _data.PhaseInvert = false);
        Reset(nameof(MonoOutput), !_data.MonoOutput, () => _data.MonoOutput = false);
        Reset(nameof(SwapChannels), !_data.SwapChannels, () => _data.SwapChannels = false);
        Reset(nameof(ShowAudioFormat), !_data.ShowAudioFormat, () => _data.ShowAudioFormat = false);
        Reset(nameof(MiniPlayerEnabled), _data.MiniPlayerEnabled is null or true, () => _data.MiniPlayerEnabled = null);
        Reset(nameof(MiniPlayerMode), _data.MiniPlayerMode == default, () => _data.MiniPlayerMode = default);
        Reset(nameof(OutputButtonEnabled), _data.OutputButtonEnabled is null or true, () => _data.OutputButtonEnabled = null);
        Reset(nameof(VolumeControlEnabled), _data.VolumeControlEnabled is null or true, () => _data.VolumeControlEnabled = null);
        Reset(nameof(LyricsEnabled), _data.LyricsEnabled is null or true, () => _data.LyricsEnabled = null);
        Reset(nameof(LoadLocalLyrics), !_data.LoadLocalLyrics, () => _data.LoadLocalLyrics = false);
        Reset(nameof(DownloadLyrics), !_data.DownloadLyrics, () => _data.DownloadLyrics = false);
        Reset(nameof(LyricsLocation), _data.LyricsLocation == default, () => _data.LyricsLocation = default);
        Reset(nameof(RememberMissingLyrics), !_data.RememberMissingLyrics, () => _data.RememberMissingLyrics = false);

        // The output reopens only if what it plays through changed.
        if (OutputSettings != output)
            changed.Add(nameof(OutputSettings));
        foreach (var name in changed)
            OnPropertyChanged(name);
        if (changed.Count > 0 || devices)
            _saver.Schedule();
    }

    public static IReadOnlyList<string> DefaultLibraryFolders()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        return string.IsNullOrEmpty(music) ? Array.Empty<string>() : new[] { music };
    }

    public void Flush() => _saver.Flush();

    private void Set(Action apply, bool changed = true, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!changed)
            return;

        apply();
        OnPropertyChanged(name);
        if (name is nameof(OutputApi) or nameof(WasapiExclusive) or nameof(DsdMode) or nameof(DsdGainDb))
            OnPropertyChanged(nameof(OutputSettings));
        _saver.Schedule();
    }

    private void Save() => JsonStorage.Save(AppPaths.SettingsFile, _data, AppJsonContext.Default.SettingsData);
}
