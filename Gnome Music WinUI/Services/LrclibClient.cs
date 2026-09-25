// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Gnome_Music_WinUI.Services;

/// <summary>A track in LRCLIB's database (the fields the app uses).</summary>
public sealed class LrclibTrack
{
    public long Id { get; set; }

    public string? TrackName { get; set; }

    public string? ArtistName { get; set; }

    public string? AlbumName { get; set; }

    public double? Duration { get; set; }

    public bool Instrumental { get; set; }

    public string? PlainLyrics { get; set; }

    public string? SyncedLyrics { get; set; }

    [JsonIgnore]
    public bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);

    [JsonIgnore]
    public bool HasLyrics => HasSynced || !string.IsNullOrWhiteSpace(PlainLyrics);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LrclibTrack))]
[JsonSerializable(typeof(LrclibTrack[]))]
internal sealed partial class LrclibJsonContext : JsonSerializerContext
{
}

/// <summary>What a song is looked up by: its title, artists, album and duration.</summary>
public sealed record LrclibQuery(string Title, string[] Artists, string? Album, double Duration)
{
    private static readonly char[] ArtistSeparators = { '/', ';', '、', '&', ',', '，' };

    public string Key { get; } = string.Join('\n',
        Title, string.Join('\u001f', Artists), Album ?? "", Duration.ToString(CultureInfo.InvariantCulture))
        .ToLowerInvariant();

    /// <summary>All the artists, then the first one alone ("A, B", "A/B" → "A").</summary>
    public IEnumerable<string> ArtistNames()
    {
        string all = string.Join(", ", Artists);
        yield return all;

        string first = Artists[0].Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        int feat = first.IndexOf(" feat", StringComparison.OrdinalIgnoreCase);
        if (feat > 0)
            first = first[..feat].Trim();
        if (first.Length > 0 && first != all)
            yield return first;
    }
}

/// <summary>
/// LRCLIB (https://lrclib.net), a free database of synced lyrics: its best match for a
/// song, and the searches of the song properties' lyrics page. Failures throw.
/// </summary>
public sealed class LrclibClient
{
    /// <summary>
    /// How far the duration of another recording may be off (LRCLIB's own lookup
    /// allows 2 s; the app's durations are whole seconds).
    /// </summary>
    private const double DurationTolerance = 3;

    private readonly HttpClient _http;

    public LrclibClient(string appVersion)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://lrclib.net/api/"),
            Timeout = TimeSpan.FromSeconds(10),
        };

        // LRCLIB asks clients to name themselves.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GnomeMusicWinUI", appVersion));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(unofficial Windows port of GNOME Music)"));
    }

    /// <summary>
    /// LRCLIB's best match for the song. When that has no synced lyrics, or there is
    /// none, the other recordings of the song with about the same duration are
    /// searched, the synced ones first. With several artists, the first one alone is
    /// tried too, and at last the recordings with the title whose artist is about the
    /// same ("邓福如" for "邓福如 Afu").
    /// </summary>
    public async Task<LrclibTrack?> FindAsync(LrclibQuery query)
    {
        if (query.Artists.Length == 0)
        {
            // Untagged files: only a recording with the same duration is trusted.
            if (query.Duration <= 0)
                return null;
            return Best(await SearchAsync(("q", query.Title)).ConfigureAwait(false), query.Duration);
        }

        LrclibTrack? plain = null;
        foreach (var artist in query.ArtistNames())
        {
            var match = await GetAsync(
                ("track_name", query.Title),
                ("artist_name", artist),
                ("album_name", query.Album),
                ("duration", query.Duration > 0 ? query.Duration.ToString(CultureInfo.InvariantCulture) : null)).ConfigureAwait(false);
            if (match is not null && (match.HasSynced || match.Instrumental && !match.HasLyrics))
                return match;

            var best = Best(await SearchAsync(("track_name", query.Title), ("artist_name", artist)).ConfigureAwait(false), query.Duration);
            if (best is not null && best.HasSynced)
                return best;

            plain ??= match is not null && match.HasLyrics ? match : best;
        }

        if (query.Duration > 0)
        {
            var found = await SearchAsync(("track_name", query.Title)).ConfigureAwait(false);
            var best = Best(found?.Where(t => SameArtist(t.ArtistName, query.Artists)).ToArray(), query.Duration);
            if (best is not null && (best.HasSynced || plain is null))
                return best;
        }

        return plain;
    }

    /// <summary>
    /// The recordings LRCLIB finds for a title and an artist (either may be empty; at
    /// most 20), as it orders them.
    /// </summary>
    public async Task<LrclibTrack[]> SearchAsync(string title, string? artist)
    {
        var found = string.IsNullOrWhiteSpace(title)
            ? await SearchAsync(("q", artist)).ConfigureAwait(false)
            : await SearchAsync(("track_name", title), ("artist_name", artist)).ConfigureAwait(false);
        return found ?? Array.Empty<LrclibTrack>();
    }

    /// <summary>
    /// Search results in the order to offer them: synced lyrics first, then plain ones,
    /// then instrumentals; recordings of about the song's duration first within each.
    /// </summary>
    public static IReadOnlyList<LrclibTrack> Rank(IEnumerable<LrclibTrack> tracks, double duration) => tracks
        .OrderByDescending(t => t.HasSynced)
        .ThenByDescending(t => t.HasLyrics)
        .ThenByDescending(t => t.Instrumental)
        .ThenBy(t => duration > 0 && t.Duration is > 0 ? Math.Abs(t.Duration.Value - duration) : 0)
        .ToList();

    /// <summary>Whether one name contains the other, ignoring case, spaces and punctuation.</summary>
    private static bool SameArtist(string? name, string[] artists)
    {
        static string Letters(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        string found = Letters(name ?? "");
        return found.Length > 0 && artists.Select(Letters).Any(a => a.Length > 0 && (a.Contains(found) || found.Contains(a)));
    }

    private async Task<LrclibTrack?> GetAsync(params (string Name, string? Value)[] parameters)
    {
        using var response = await RequestAsync("get?" + QueryString(parameters)).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, LrclibJsonContext.Default.LrclibTrack).ConfigureAwait(false);
    }

    private async Task<LrclibTrack[]?> SearchAsync(params (string Name, string? Value)[] parameters)
    {
        using var response = await RequestAsync("search?" + QueryString(parameters)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, LrclibJsonContext.Default.LrclibTrackArray).ConfigureAwait(false);
    }

    /// <summary>A GET that is tried once more a second later when LRCLIB is busy (5xx) or does not answer.</summary>
    private async Task<HttpResponseMessage> RequestAsync(string url)
    {
        try
        {
            var response = await _http.GetAsync(url).ConfigureAwait(false);
            if ((int)response.StatusCode < 500)
                return response;
            response.Dispose();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }

        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        return await _http.GetAsync(url).ConfigureAwait(false);
    }

    /// <summary>
    /// The recording with about the song's duration (any recording when the duration
    /// is unknown) that has synced lyrics, else plain lyrics, else is an instrumental;
    /// the closest duration first.
    /// </summary>
    private static LrclibTrack? Best(LrclibTrack[]? tracks, double duration)
    {
        double Distance(LrclibTrack t) => duration > 0 && t.Duration is > 0 ? Math.Abs(t.Duration.Value - duration) : 0;

        return tracks?
            .Where(t => t.HasLyrics || t.Instrumental)
            .Where(t => duration <= 0 || t.Duration is > 0 && Distance(t) <= DurationTolerance)
            .OrderByDescending(t => t.HasSynced)
            .ThenByDescending(t => t.HasLyrics)
            .ThenBy(Distance)
            .FirstOrDefault();
    }

    private static string QueryString((string Name, string? Value)[] parameters) =>
        string.Join('&', parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value!.Trim())));
}
