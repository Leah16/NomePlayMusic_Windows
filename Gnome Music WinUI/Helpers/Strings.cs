// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Gnome_Music_WinUI.Helpers;

/// <summary>
/// Localized strings (Strings/*/Resources.resw). The English texts and the
/// Simplified Chinese translation come from GNOME Music's po files. Exposed as
/// static properties so XAML can use {x:Bind h:Strings.Name}.
/// </summary>
public static class Strings
{
    private static readonly ResourceLoader? Loader = CreateLoader();

    private static ResourceLoader? CreateLoader()
    {
        try
        {
            return new ResourceLoader();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[strings] no resources: {ex.Message}");
            return null;
        }
    }

    public static string Get(string key)
    {
        try
        {
            var value = Loader?.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>ngettext() with the English plural rule (zh has a single form).</summary>
    public static string Plural(string key, long n) =>
        string.Format(CultureInfo.CurrentCulture, Get(n == 1 ? key + "_One" : key + "_Other"), n);

    // Application
    public static string AppName => Get("AppName");
    public static string AppDescription => Get("AppDescription");
    public static string AppLongDescription => Get("AppLongDescription");

    // Views
    public static string Albums => Get("Albums");
    public static string Artists => Get("Artists");
    public static string Playlists => Get("Playlists");
    public static string Songs => Get("Songs");

    // Header bar (the primary menu became the preferences view)
    public static string Menu => Get("Menu");
    public static string Search => Get("Search");
    public static string Back => Get("Back");
    public static string Preferences => Get("Preferences");
    public static string KeyboardShortcuts => Get("KeyboardShortcuts");
    public static string Help => Get("Help");
    public static string About => Get("About");
    public static string SearchPlaceholder => Get("SearchPlaceholder");

    // Player toolbar
    public static string Play => Get("Play");
    public static string Pause => Get("Pause");
    public static string Previous => Get("Previous");
    public static string Next => Get("Next");
    public static string SetRepeatMode => Get("SetRepeatMode");
    public static string AdjustVolume => Get("AdjustVolume");
    public static string MuteUnmute => Get("MuteUnmute");
    public static string PlayQueue => Get("PlayQueue");
    public static string MiniPlayer => Get("MiniPlayer");
    public static string AudioOutput => Get("AudioOutput");
    public static string BackToFullView => Get("BackToFullView");
    public static string Lyrics => Get("Lyrics");
    public static string ShowLyrics => Get("ShowLyrics");
    public static string HideLyrics => Get("HideLyrics");
    public static string LyricsLoading => Get("LyricsLoading");
    public static string LyricsNotFound => Get("LyricsNotFound");
    public static string LyricsNotSynced => Get("LyricsNotSynced");
    public static string LyricsInstrumental => Get("LyricsInstrumental");
    public static string RepeatNone => Get("RepeatNone");
    public static string RepeatSong => Get("RepeatSong");
    public static string RepeatAll => Get("RepeatAll");
    public static string RepeatShuffle => Get("RepeatShuffle");

    // Albums, artists and songs
    public static string UnknownArtist => Get("UnknownArtist");
    public static string UnknownAlbum => Get("UnknownAlbum");
    public static string VariousArtists => Get("VariousArtists");
    public static string MenuPlay => Get("MenuPlay");
    public static string AddToFavoriteSongs => Get("AddToFavoriteSongs");
    public static string AddToPlaylistEllipsis => Get("AddToPlaylistEllipsis");
    public static string RemoveFromPlaylist => Get("RemoveFromPlaylist");
    public static string OpenLocation => Get("OpenLocation");
    public static string Star => Get("Star");
    public static string Unstar => Get("Unstar");
    public static string ViewAll => Get("ViewAll");
    public static string Disc(int number) => Format("DiscFormat", number);
    public static string Minutes(long n) => Plural("Minutes", n);
    public static string SongCount(long n) => Plural("SongCount", n);

    // Playlists
    public static string MenuDelete => Get("MenuDelete");
    public static string MenuRename => Get("MenuRename");
    public static string PlaylistName => Get("PlaylistName");
    public static string Done => Get("Done");
    public static string AddToPlaylist => Get("AddToPlaylist");
    public static string Cancel => Get("Cancel");
    public static string Add => Get("Add");
    public static string Create => Get("Create");
    public static string FirstPlaylistPrompt => Get("FirstPlaylistPrompt");
    public static string NewPlaylistEllipsis => Get("NewPlaylistEllipsis");
    public static string PlaylistRemoved(string title) => Format("PlaylistRemoved", title);
    public static string SongRemovedFrom(string song, string playlist) => Format("SongRemovedFrom", song, playlist);
    public static string Undo => Get("Undo");
    public static string MostPlayed => Get("MostPlayed");
    public static string NeverPlayed => Get("NeverPlayed");
    public static string RecentlyPlayed => Get("RecentlyPlayed");
    public static string RecentlyAdded => Get("RecentlyAdded");
    public static string StarredSongs => Get("StarredSongs");
    public static string InsufficientlyTagged => Get("InsufficientlyTagged");
    public static string AllSongs => Get("AllSongs");

    // Status pages
    public static string WelcomeToMusic => Get("WelcomeToMusic");
    public static string MusicFolder => Get("MusicFolder");
    public static string ContentsWillAppear => Get("ContentsWillAppear");
    public static string NoMusicFound => Get("NoMusicFound");
    public static string TryADifferentSearch => Get("TryADifferentSearch");
    public static string NoSearchStarted => Get("NoSearchStarted");
    public static string NoSearchStartedDescription => Get("NoSearchStartedDescription");
    public static string NoResultsFound => Get("NoResultsFound");
    public static string TryDifferentSearch => Get("TryDifferentSearch");
    public static string ScanningLibrary => Get("ScanningLibrary");
    public static string MusicFolderNotSet => Get("MusicFolderNotSet");

    // Errors
    public static string UnableToPlay => Get("UnableToPlay");
    public static string PlayingMusic => Get("PlayingMusic");

    // Preferences
    public static string PlayerSettings => Get("PlayerSettings");
    public static string RepeatModeTitle => Get("RepeatModeTitle");
    public static string RepeatModeNone => Get("RepeatModeNone");
    public static string RepeatModeSong => Get("RepeatModeSong");
    public static string RepeatModeAll => Get("RepeatModeAll");
    public static string ReplayGain => Get("ReplayGain");
    public static string ReplayGainDescription => Get("ReplayGainDescription");
    public static string ReplayGainDisabled => Get("ReplayGainDisabled");
    public static string ReplayGainAlbum => Get("ReplayGainAlbum");
    public static string ReplayGainTrack => Get("ReplayGainTrack");
    public static string PowerSettings => Get("PowerSettings");
    public static string InhibitSuspend => Get("InhibitSuspend");
    public static string OnlyWhilePlaying => Get("OnlyWhilePlaying");
    public static string MusicFolders => Get("MusicFolders");
    public static string MusicFoldersDescription => Get("MusicFoldersDescription");
    public static string AddFolder => Get("AddFolder");
    public static string RemoveFolder => Get("RemoveFolder");
    public static string Close => Get("Close");

    // Shortcuts dialog
    public static string ShortcutsGeneral => Get("ShortcutsGeneral");
    public static string ShortcutsPreferences => Get("ShortcutsPreferences");
    public static string ShortcutsSearch => Get("ShortcutsSearch");
    public static string ShortcutsHelp => Get("ShortcutsHelp");
    public static string ShortcutsKeyboardShortcuts => Get("ShortcutsKeyboardShortcuts");
    public static string ShortcutsQuit => Get("ShortcutsQuit");
    public static string ShortcutsPlayback => Get("ShortcutsPlayback");
    public static string ShortcutsPlayPause => Get("ShortcutsPlayPause");
    public static string ShortcutsNextSong => Get("ShortcutsNextSong");
    public static string ShortcutsPreviousSong => Get("ShortcutsPreviousSong");
    public static string ShortcutsToggleRepeat => Get("ShortcutsToggleRepeat");
    public static string ShortcutsToggleShuffle => Get("ShortcutsToggleShuffle");
    public static string ShortcutsIncreaseVolume => Get("ShortcutsIncreaseVolume");
    public static string ShortcutsDecreaseVolume => Get("ShortcutsDecreaseVolume");
    public static string ShortcutsToggleMute => Get("ShortcutsToggleMute");
    public static string ShortcutsNavigation => Get("ShortcutsNavigation");
    public static string ShortcutsGoToAlbums => Get("ShortcutsGoToAlbums");
    public static string ShortcutsGoToArtists => Get("ShortcutsGoToArtists");
    public static string ShortcutsGoToPlaylists => Get("ShortcutsGoToPlaylists");
    public static string ShortcutsGoBack => Get("ShortcutsGoBack");

    // About
    public static string GnomePortProject => Get("GnomePortProject");
    public static string Copyright => Get("Copyright");
    public static string TranslatorCredits => Get("TranslatorCredits");
    public static string PortDescription => Get("PortDescription");
    public static string AboutDevelopers => Get("AboutDevelopers");
    public static string AboutDesigners => Get("AboutDesigners");
    public static string AboutTranslators => Get("AboutTranslators");
    public static string AboutLegal => Get("AboutLegal");
    public static string AboutReportIssue => Get("AboutReportIssue");
    public static string AboutSourceCode => Get("AboutSourceCode");
    public static string LicenseNotice => Get("LicenseNotice");
    public static string LicenseName => Get("LicenseName");
    public static string Website => Get("Website");

    // Preferences (Windows port): categories, processing, output, player controls
    public static string PrefsPlayback => Get("PrefsPlayback");
    public static string PrefsOutput => Get("PrefsOutput");
    public static string PrefsControls => Get("PrefsControls");
    public static string ChannelProcessing => Get("ChannelProcessing");
    public static string PhaseInvert => Get("PhaseInvert");
    public static string PhaseInvertDescription => Get("PhaseInvertDescription");
    public static string MonoOutput => Get("MonoOutput");
    public static string MonoOutputDescription => Get("MonoOutputDescription");
    public static string SwapChannels => Get("SwapChannels");
    public static string SwapChannelsDescription => Get("SwapChannelsDescription");
    public static string OutputDevice => Get("OutputDevice");
    public static string AudioInterface => Get("AudioInterface");
    public static string AudioInterfaceDescription => Get("AudioInterfaceDescription");
    public static string ExclusiveMode => Get("ExclusiveMode");
    public static string ExclusiveModeDescription => Get("ExclusiveModeDescription");
    public static string Device => Get("Device");
    public static string DefaultDevice => Get("DefaultDevice");
    public static string NoAsioDriver => Get("NoAsioDriver");
    public static string DeviceFormats => Get("DeviceFormats");
    public static string PcmRates => Get("PcmRates");
    public static string BitDepths => Get("BitDepths");
    public static string DsdRates => Get("DsdRates");
    public static string ChannelCounts => Get("ChannelCounts");
    public static string Checking => Get("Checking");
    public static string NotSupported => Get("NotSupported");
    public static string MixerFormat(string rate, string channels) => Format("MixerFormat", rate, channels);
    public static string ExclusiveUnsupported => Get("ExclusiveUnsupported");
    public static string DopSuffix => Get("DopSuffix");
    public static string NativeSuffix => Get("NativeSuffix");
    public static string MonoChannel => Get("MonoChannel");
    public static string StereoChannels => Get("StereoChannels");
    public static string IntBits(int bits) => Format("IntBits", bits);
    public static string FloatBits(int bits) => Format("FloatBits", bits);
    public static string DsdTransport => Get("DsdTransport");
    public static string DsdConvert => Get("DsdConvert");
    public static string DsdDop => Get("DsdDop");
    public static string DsdNative => Get("DsdNative");
    public static string DsdConvertDescription => Get("DsdConvertDescription");
    public static string DsdDopDescription => Get("DsdDopDescription");
    public static string DsdNativeDescription => Get("DsdNativeDescription");
    public static string DsdBitstreamNote => Get("DsdBitstreamNote");
    public static string DsdNotPossible => Get("DsdNotPossible");
    public static string DsdGain => Get("DsdGain");
    public static string DsdGainDescription => Get("DsdGainDescription");
    public static string Features => Get("Features");
    public static string FeatureMiniPlayer => Get("FeatureMiniPlayer");
    public static string FeatureMiniPlayerDescription => Get("FeatureMiniPlayerDescription");
    public static string FeatureOutputButton => Get("FeatureOutputButton");
    public static string FeatureOutputButtonDescription => Get("FeatureOutputButtonDescription");
    public static string FeatureVolume => Get("FeatureVolume");
    public static string FeatureVolumeDescription => Get("FeatureVolumeDescription");
    public static string FeatureLyrics => Get("FeatureLyrics");
    public static string FeatureLyricsDescription => Get("FeatureLyricsDescription");
    public static string LoadLocalLyrics => Get("LoadLocalLyrics");
    public static string LoadLocalLyricsDescription => Get("LoadLocalLyricsDescription");
    public static string DownloadLyrics => Get("DownloadLyrics");
    public static string DownloadLyricsDescription => Get("DownloadLyricsDescription");
    public static string Display => Get("Display");
    public static string ShowAudioFormat => Get("ShowAudioFormat");
    public static string ShowAudioFormatDescription => Get("ShowAudioFormatDescription");
    public static string OutputFallback => Get("OutputFallback");
    public static string NoPlaybackDevice => Get("NoPlaybackDevice");

    // Song properties (Windows port)
    public static string Properties => Get("Properties");
    public static string Tags => Get("Tags");
    public static string PropFile => Get("PropFile");
    public static string PropAudio => Get("PropAudio");
    public static string PropStatistics => Get("PropStatistics");
    public static string PropType => Get("PropType");
    public static string PropSize => Get("PropSize");
    public static string PropBytes(string bytes) => Format("PropBytes", bytes);
    public static string PropModified => Get("PropModified");
    public static string PropLocation => Get("PropLocation");
    public static string PropDuration => Get("PropDuration");
    public static string PropFormat => Get("PropFormat");
    public static string PropSampleRate => Get("PropSampleRate");
    public static string PropBitDepth => Get("PropBitDepth");
    public static string PropBitrate => Get("PropBitrate");
    public static string PropEncoder => Get("PropEncoder");
    public static string PropTagFormat => Get("PropTagFormat");
    public static string PropAdded => Get("PropAdded");
    public static string PropLastPlayed => Get("PropLastPlayed");
    public static string PropPlayCount => Get("PropPlayCount");
    public static string PropFavorite => Get("PropFavorite");
    public static string PropNever => Get("PropNever");
    public static string PropYes => Get("PropYes");
    public static string PropNo => Get("PropNo");
    public static string PropTrackGain => Get("PropTrackGain");
    public static string PropTrackPeak => Get("PropTrackPeak");
    public static string PropAlbumGain => Get("PropAlbumGain");
    public static string PropAlbumPeak => Get("PropAlbumPeak");
    public static string PropNotCalculated => Get("PropNotCalculated");
    public static string PropTitle => Get("PropTitle");
    public static string PropArtist => Get("PropArtist");
    public static string PropAlbumArtist => Get("PropAlbumArtist");
    public static string PropAlbum => Get("PropAlbum");
    public static string PropYear => Get("PropYear");
    public static string PropTrack => Get("PropTrack");
    public static string PropDisc => Get("PropDisc");
    public static string PropGenre => Get("PropGenre");
    public static string PropComposer => Get("PropComposer");
    public static string PropConductor => Get("PropConductor");
    public static string PropPublisher => Get("PropPublisher");
    public static string PropGrouping => Get("PropGrouping");
    public static string PropComment => Get("PropComment");
    public static string PropNumberOf(string number, string total) => Format("PropNumberOf", number, total);

    // Song properties: lyrics (Windows port)
    public static string SetInstrumental => Get("SetInstrumental");
    public static string SetInstrumentalDescription => Get("SetInstrumentalDescription");
    public static string EditLyrics => Get("EditLyrics");
    public static string EditLyricsWith => Get("EditLyricsWith");
    public static string EditLyricsUnavailable => Get("EditLyricsUnavailable");
    public static string SearchLyricsOnline => Get("SearchLyricsOnline");
    public static string LyricsFromFile(string file) => Format("LyricsFromFile", file);
    public static string LyricsFromLrclib(string track) => Format("LyricsFromLrclib", track);
    public static string LyricsLrclibInstrumental => Get("LyricsLrclibInstrumental");
    public static string LyricsFileNotLoaded(string file) => Format("LyricsFileNotLoaded", file);
    public static string LyricsFailed => Get("LyricsFailed");
    public static string LyricsOpenFailed(string error) => Format("LyricsOpenFailed", error);
    public static string Retry => Get("Retry");
    public static string SearchLyricsTitle => Get("SearchLyricsTitle");
    public static string SearchSongTitle => Get("SearchSongTitle");
    public static string SearchArtistName => Get("SearchArtistName");
    public static string SearchNoResults => Get("SearchNoResults");
    public static string SearchFailed(string error) => Format("SearchFailed", error);
    public static string SelectResult => Get("SelectResult");
    public static string ResultSynced => Get("ResultSynced");
    public static string ResultNotSynced => Get("ResultNotSynced");
    public static string ResultInUse => Get("ResultInUse");
    public static string DownloadToLocal => Get("DownloadToLocal");
    public static string ReplaceLocalLyrics => Get("ReplaceLocalLyrics");
    public static string ReplaceLyricsConfirm(string file) => Format("ReplaceLyricsConfirm", file);
    public static string Replace => Get("Replace");
    public static string LyricsSaveFailed(string error) => Format("LyricsSaveFailed", error);
}
