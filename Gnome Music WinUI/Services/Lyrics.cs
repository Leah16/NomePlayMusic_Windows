// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Gnome_Music_WinUI.Services;

/// <summary>A line of lyrics; synced lyrics have the time the line starts at.</summary>
public sealed record LyricLine(TimeSpan? Time, string Text);

/// <summary>
/// The lyrics of a song (not in GNOME Music): lines with their times from an LRC text
/// (synced), or the lines of a plain text.
/// </summary>
public sealed partial class Lyrics
{
    private Lyrics(IReadOnlyList<LyricLine> lines, bool synced)
    {
        Lines = lines;
        IsSynced = synced;
    }

    /// <summary>
    /// The lines in order. An empty line of synced lyrics ends the line before it (a
    /// pause); in plain lyrics it separates stanzas.
    /// </summary>
    public IReadOnlyList<LyricLine> Lines { get; }

    public bool IsSynced { get; }

    /// <summary>
    /// The synced lyrics of an LRC text, else the plain text; null when there are no
    /// lines. An LRC text that gives every line the same time counts as plain text.
    /// </summary>
    public static Lyrics? Create(string? synced, string? plain)
    {
        List<LyricLine>? parsed = null;
        if (!string.IsNullOrWhiteSpace(synced))
        {
            parsed = ParseLrc(synced);
            if (parsed.Where(l => l.Text.Length > 0).Select(l => l.Time).Distinct().Count() > 1)
                return new Lyrics(parsed, true);
        }

        var lines = !string.IsNullOrWhiteSpace(plain)
            ? plain.Split('\n').Select(l => new LyricLine(null, l.Trim())).ToList()
            : parsed?.Select(l => new LyricLine(null, l.Text)).ToList();
        if (lines is null)
            return null;

        // No empty lines around the text
        int start = lines.FindIndex(l => l.Text.Length > 0);
        int end = lines.FindLastIndex(l => l.Text.Length > 0);
        return start < 0 ? null : new Lyrics(lines.GetRange(start, end - start + 1), false);
    }

    /// <summary>
    /// The lyrics of a lyrics file: synced when its lines have times, else its lines as
    /// plain text, without the tags ([ar:…], and times that are all the same).
    /// </summary>
    public static Lyrics? Parse(string text)
    {
        var synced = Create(text, null);
        if (synced is { IsSynced: true })
            return synced;

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.AsSpan().Trim();
            bool tagged = false;
            while (line.Length > 0 && line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close < 0 || !IsTag(line[1..close]))
                    break;   // text in brackets, like "[Chorus]"

                tagged = true;
                line = line[(close + 1)..].TrimStart();
            }

            // A line of tags alone is not a line of the lyrics.
            if (!tagged || line.Length > 0)
                lines.Add(line.ToString());
        }

        return Create(null, string.Join('\n', lines));
    }

    /// <summary>A time ("01:02.03") or an ID tag ("ar:Artist", "offset:+100").</summary>
    private static bool IsTag(ReadOnlySpan<char> tag)
    {
        if (TryParseTime(tag, out _))
            return true;

        int colon = tag.IndexOf(':');
        if (colon <= 0)
            return false;

        foreach (char c in tag[..colon])
        {
            if (!char.IsAsciiLetter(c) && c != '#')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the timed lines of an LRC text, sorted by time: "[mm:ss.xx]text", with
    /// several times for a repeated line, "[offset:±ms]" and the word times of
    /// enhanced LRC ("&lt;mm:ss.xx&gt;"). Other tags ([ar:…], [ti:…]) are skipped.
    /// </summary>
    public static List<LyricLine> ParseLrc(string text)
    {
        var lines = new List<(TimeSpan Time, string Text)>();
        var offset = TimeSpan.Zero;
        var times = new List<TimeSpan>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.AsSpan().Trim();
            times.Clear();
            while (line.Length > 0 && line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close < 0)
                    break;

                var tag = line[1..close];
                if (TryParseTime(tag, out var time))
                {
                    times.Add(time);
                }
                else if (times.Count > 0)
                {
                    break;   // text that starts with brackets
                }
                else if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(tag[7..].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int ms))
                {
                    offset = TimeSpan.FromMilliseconds(ms);
                }

                line = line[(close + 1)..].TrimStart();
            }

            if (times.Count == 0)
                continue;

            string content = WordTimeRegex().Replace(line.ToString(), "").Trim();
            foreach (var time in times)
                lines.Add((time, content));
        }

        // A positive offset shows the lines earlier.
        return lines
            .Select(l => new LyricLine(l.Time > offset ? l.Time - offset : TimeSpan.Zero, l.Text))
            .OrderBy(l => l.Time)
            .ToList();
    }

    /// <summary>mm:ss, mm:ss.f to mm:ss.ffffff, or mm:ss:ff.</summary>
    private static bool TryParseTime(ReadOnlySpan<char> tag, out TimeSpan time)
    {
        time = default;
        int colon = tag.IndexOf(':');
        if (colon <= 0 || !int.TryParse(tag[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
            return false;

        var rest = tag[(colon + 1)..];
        int separator = rest.IndexOfAny('.', ':');
        var secondsText = separator < 0 ? rest : rest[..separator];
        if (secondsText.Length is 0 or > 2
            || !int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            || seconds >= 60)
        {
            return false;
        }

        double fraction = 0;
        if (separator >= 0)
        {
            var fractionText = rest[(separator + 1)..];
            if (fractionText.Length is 0 or > 6
                || !int.TryParse(fractionText, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                return false;
            }

            fraction = value / Math.Pow(10, fractionText.Length);
        }

        time = TimeSpan.FromSeconds(minutes * 60 + seconds + fraction);
        return true;
    }

    [GeneratedRegex(@"<\d+:\d+(?:[.:]\d+)?>")]
    private static partial Regex WordTimeRegex();
}
