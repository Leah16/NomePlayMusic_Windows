// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Models;

namespace Gnome_Music_WinUI.Services;

public enum LyricsStatus
{
    Found,

    /// <summary>The song is set as instrumental, or LRCLIB knows it as one.</summary>
    Instrumental,

    NotFound,

    /// <summary>LRCLIB could not be reached; the next lookup tries again.</summary>
    Failed,
}

/// <summary>Where lyrics come from.</summary>
public enum LyricsSource
{
    /// <summary>Nothing was looked up: the song is set as instrumental.</summary>
    Song,

    /// <summary>The song's lyrics file (<see cref="LyricsFile"/>).</summary>
    File,

    Lrclib,
}

public sealed record LyricsResult(LyricsStatus Status, LyricsSource Source, Lyrics? Lyrics = null)
{
    /// <summary>The LRCLIB record the lyrics (or the instrumental) come from.</summary>
    public LrclibTrack? Track { get; init; }

    /// <summary>
    /// The song's lyrics file as it was when the lyrics were looked up, while local
    /// lyrics are loaded: when it changes, so may the lyrics.
    /// </summary>
    public FileStamp? LocalFile { get; init; }
}

/// <summary>
/// The lyrics of songs (not in GNOME Music), looked up only while the lyrics show: the
/// playing song and the next one, and the song whose properties are open. A song set as
/// instrumental has none. Else, with "Load local lyrics" on, its lyrics file comes first
/// (<see cref="LyricsFile"/>); without one, or with the setting off, LRCLIB is asked,
/// once per session, and with "Download lyrics" on its lyrics are kept as the song's
/// lyrics file. Both settings are off by default.
/// </summary>
public sealed class LyricsService
{
    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;

    private readonly Settings _settings;
    private readonly LrclibClient _lrclib = new(AppVersion());
    private readonly ConcurrentDictionary<string, Task<LyricsResult>> _lookups = new();
    private readonly SemaphoreSlim _gate = new(2);
    private readonly bool _simplifiedChinese = PrefersSimplifiedChinese();

    public LyricsService(Settings settings)
    {
        _settings = settings;
        settings.PropertyChanged += (_, e) =>
        {
            // Looked up again, the lyrics showing are also saved once downloads are on.
            if (e.PropertyName is nameof(Settings.LoadLocalLyrics) or nameof(Settings.DownloadLyrics))
                Changed?.Invoke(this, null);
        };
    }

    /// <summary>
    /// Raised on the UI thread when the lyrics of a song, or of every song (null), may
    /// have changed: it was set as instrumental or not, its lyrics were saved, or local
    /// lyrics or downloads were turned on or off.
    /// </summary>
    public event EventHandler<CoreSong?>? Changed;

    /// <summary>The song's lyrics as the settings find them; a lookup that failed is tried again on the next call.</summary>
    public Task<LyricsResult> GetAsync(CoreSong song)
    {
        if (song.Instrumental)
            return Task.FromResult(new LyricsResult(LyricsStatus.Instrumental, LyricsSource.Song));

        bool local = _settings.LoadLocalLyrics;
        bool download = _settings.DownloadLyrics;
        string file = LyricsFile.PathFor(song.FilePath);
        var query = new LrclibQuery(song.Title, song.Record.Artists ?? Array.Empty<string>(), song.HasAlbum ? song.AlbumTitle : null, song.Duration);
        return Task.Run(async () =>
        {
            FileStamp? stamp = null;
            if (local)
            {
                stamp = LyricsFile.Stamp(file);
                if (stamp.Value.Exists && ReadFile(file) is { } lyrics)
                    return new LyricsResult(LyricsStatus.Found, LyricsSource.File, lyrics) { LocalFile = stamp };
            }

            var lookup = _lookups.GetOrAdd(query.Key, _ => LookupAsync(query));
            var result = await lookup.ConfigureAwait(false);
            if (result.Status == LyricsStatus.Failed)
                _lookups.TryRemove(new KeyValuePair<string, Task<LyricsResult>>(query.Key, lookup));

            if (download && result.Track is { HasLyrics: true } track && SaveNew(file, track) && local)
                stamp = LyricsFile.Stamp(file);
            return result with { LocalFile = stamp };
        });
    }

    /// <summary>What LRCLIB finds for a title and an artist, for the user to choose from.</summary>
    public Task<LrclibTrack[]> SearchAsync(string title, string? artist) => _lrclib.SearchAsync(title, artist);

    /// <summary>The lyrics of an LRCLIB record as they show (null when it has none).</summary>
    public Lyrics? ToLyrics(LrclibTrack track) => Lyrics.Create(ToScript(track.SyncedLyrics), ToScript(track.PlainLyrics));

    /// <summary>Saves the lyrics of an LRCLIB record as the song's lyrics file, in place of one that is there.</summary>
    public async Task SaveAsync(CoreSong song, LrclibTrack track)
    {
        string text = FileText(track) ?? throw new ArgumentException("The track has no lyrics.", nameof(track));
        string file = LyricsFile.PathFor(song.FilePath);
        await Task.Run(() => LyricsFile.Write(file, text, replace: true));
        Log.Info($"Saved the lyrics of {song.FilePath}");
        Changed?.Invoke(this, song);
    }

    /// <summary>The song was set as instrumental, or not any more.</summary>
    internal void OnInstrumentalChanged(CoreSong song) => Changed?.Invoke(this, song);

    private async Task<LyricsResult> LookupAsync(LrclibQuery query)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var track = await _lrclib.FindAsync(query).ConfigureAwait(false);
            return track is null ? new LyricsResult(LyricsStatus.NotFound, LyricsSource.Lrclib) : ToResult(track);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot get the lyrics of \"{query.Title}\": {ex.Message}");
            return new LyricsResult(LyricsStatus.Failed, LyricsSource.Lrclib);
        }
        finally
        {
            _gate.Release();
        }
    }

    private LyricsResult ToResult(LrclibTrack track)
    {
        if (ToLyrics(track) is { } lyrics)
            return new LyricsResult(LyricsStatus.Found, LyricsSource.Lrclib, lyrics) { Track = track };
        return new LyricsResult(track.Instrumental ? LyricsStatus.Instrumental : LyricsStatus.NotFound, LyricsSource.Lrclib) { Track = track };
    }

    /// <summary>The lyrics of the song's lyrics file; null when it has none or cannot be read.</summary>
    private Lyrics? ReadFile(string file)
    {
        try
        {
            return Lyrics.Parse(ToScript(LyricsFile.Read(file))!);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Cannot read {file}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Keeps downloaded lyrics as the song's lyrics file, unless it has one (which may be the user's own).</summary>
    private bool SaveNew(string file, LrclibTrack track)
    {
        try
        {
            if (!LyricsFile.Write(file, FileText(track)!, replace: false))
                return false;

            Log.Info($"Saved the lyrics as {file}");
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Cannot save the lyrics as {file}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The text a lyrics file gets: the synced lyrics, else the plain ones, as they show.</summary>
    private string? FileText(LrclibTrack track) =>
        track.HasSynced ? ToScript(track.SyncedLyrics) : track.HasLyrics ? ToScript(track.PlainLyrics) : null;

    /// <summary>
    /// With the Simplified Chinese UI, lyrics in traditional characters show in
    /// simplified ones, like the rest of the app. Japanese lyrics (with kana) keep
    /// their kanji.
    /// </summary>
    private string? ToScript(string? text)
    {
        if (!_simplifiedChinese || string.IsNullOrEmpty(text) || text.Any(c => c is >= '぀' and <= 'ヿ'))
            return text;

        int length = LCMapStringEx("zh-CN", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (length <= 0)
            return text;

        var buffer = new char[length];
        length = LCMapStringEx("zh-CN", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return length > 0 ? new string(buffer, 0, length) : text;
    }

    private static bool PrefersSimplifiedChinese()
    {
        string language;
        try
        {
            language = Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault() ?? "";
        }
        catch (Exception)
        {
            language = CultureInfo.CurrentUICulture.Name;
        }

        return language.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-SG", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (InvalidOperationException)
        {
            return typeof(LyricsService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(
        string lpLocaleName, uint dwMapFlags, string lpSrcStr, int cchSrc, [Out] char[]? lpDestStr, int cchDest,
        IntPtr lpVersionInformation, IntPtr lpReserved, IntPtr sortHandle);
}
