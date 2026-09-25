// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gnome_Music_WinUI.Helpers;

/// <summary>Port of the helpers in gnomemusic/utils.py.</summary>
public static class Utils
{
    /// <summary>
    /// utils.seconds_to_string(): unpadded minutes, no hours field, zero padded
    /// seconds, e.g. "0:05", "1:15", "62:05".
    /// </summary>
    public static string SecondsToString(double duration)
    {
        if (double.IsNaN(duration) || duration < 0)
            duration = 0;

        long seconds = (long)duration;
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>
    /// utils.get_title_from_cursor_dict() fallback: the file's display name with
    /// "_" replaced by " " (the extension is kept).
    /// </summary>
    public static string TitleFromPath(string path) => Path.GetFileName(path).Replace('_', ' ');

    /// <summary>Full case folding (approximated with invariant lower casing).</summary>
    public static string CaseFold(string text) => text.ToLowerInvariant().Replace("ß", "ss");

    /// <summary>utils.normalize_caseless(): NFKD(casefold(text)).</summary>
    public static string NormalizeCaseless(string? text) =>
        string.IsNullOrEmpty(text) ? "" : CaseFold(text).Normalize(NormalizationForm.FormKD);

    /// <summary>Removes combining marks from an NFKD string (tracker:unaccent).</summary>
    public static string Unaccent(string decomposed)
    {
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// utils.natural_sort_names(): compares the caseless forms, digit runs
    /// numerically ("Album 3" &lt; "Album 10").
    /// </summary>
    public static int NaturalCompare(string? a, string? b) =>
        NaturalKey.Create(a).CompareTo(NaturalKey.Create(b));

    /// <summary>
    /// Locale aware, case-insensitive comparison used to sort albums by title and
    /// artists by name (Gtk.StringSorter defaults).
    /// </summary>
    public static readonly StringComparer CollationComparer =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
}

/// <summary>Precomputed key for <see cref="Utils.NaturalCompare"/>.</summary>
public sealed class NaturalKey : IComparable<NaturalKey>
{
    private readonly List<object> _parts;

    private NaturalKey(List<object> parts)
    {
        _parts = parts;
    }

    public static NaturalKey Create(string? text)
    {
        var normalized = Utils.NormalizeCaseless(text);
        var parts = new List<object>();
        int i = 0;
        while (i < normalized.Length)
        {
            int start = i;
            bool digits = char.IsAsciiDigit(normalized[i]);
            while (i < normalized.Length && char.IsAsciiDigit(normalized[i]) == digits)
                i++;

            var run = normalized[start..i];
            if (digits)
                parts.Add(decimal.TryParse(run, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : decimal.MaxValue);
            else
                parts.Add(run);
        }

        return new NaturalKey(parts);
    }

    public int CompareTo(NaturalKey? other)
    {
        if (other is null)
            return 1;

        int count = Math.Min(_parts.Count, other._parts.Count);
        for (int i = 0; i < count; i++)
        {
            int c = (_parts[i], other._parts[i]) switch
            {
                (decimal x, decimal y) => x.CompareTo(y),
                (string x, string y) => string.CompareOrdinal(x, y),
                // Python compares int and str only in the leading position; put numbers first.
                (decimal, string) => -1,
                _ => 1,
            };
            if (c != 0)
                return c;
        }

        return _parts.Count.CompareTo(other._parts.Count);
    }
}
