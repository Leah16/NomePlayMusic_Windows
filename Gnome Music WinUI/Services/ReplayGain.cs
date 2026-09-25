// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gnome_Music_WinUI.Services;

/// <summary>ReplayGain values of a file, in dB (gains) and linear amplitude (peaks).</summary>
public readonly record struct ReplayGainInfo(double? TrackGain, double? TrackPeak, double? AlbumGain, double? AlbumPeak)
{
    public bool IsEmpty => TrackGain is null && AlbumGain is null;
}

/// <summary>
/// ReplayGain support. GNOME Music inserts GStreamer's rgvolume/rglimiter; here the
/// REPLAYGAIN_* tags are read from ID3v2 (MP3, WAV, AIFF), APEv2, Vorbis comments
/// (FLAC) and iTunes freeform atoms (MP4/M4A), and the gain scales the player volume.
/// </summary>
public static class ReplayGain
{
    private static readonly ConcurrentDictionary<string, ReplayGainInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The volume factor for a file: 10^(gain/20) using the album or track gain
    /// (each falls back to the other, like rgvolume), limited so peaks do not clip.
    /// </summary>
    public static double Factor(ReplayGainInfo info, ReplayGainMode mode)
    {
        if (mode == ReplayGainMode.Disabled || info.IsEmpty)
            return 1.0;

        bool album = mode == ReplayGainMode.Album;
        double gain = (album ? info.AlbumGain ?? info.TrackGain : info.TrackGain ?? info.AlbumGain) ?? 0;
        double? peak = album ? info.AlbumPeak ?? info.TrackPeak : info.TrackPeak ?? info.AlbumPeak;
        double factor = Math.Pow(10, gain / 20);
        if (peak is > 0)
            factor = Math.Min(factor, 1.0 / peak.Value);   // rglimiter
        return Math.Clamp(factor, 0, 1);
    }

    public static ReplayGainInfo Read(string path)
    {
        if (Cache.TryGetValue(path, out var cached))
            return cached;

        var info = default(ReplayGainInfo);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".flac")
                ReadFlac(stream, tags);
            else if (ext is ".m4a" or ".m4b" or ".mp4" or ".aac" or ".alac")
                ReadMp4(stream, tags);
            else
            {
                ReadId3v2(stream, path, tags);
                if (tags.Count == 0)
                    ReadApe(stream, tags);
            }

            info = new ReplayGainInfo(
                Number(tags, "REPLAYGAIN_TRACK_GAIN"),
                Number(tags, "REPLAYGAIN_TRACK_PEAK"),
                Number(tags, "REPLAYGAIN_ALBUM_GAIN"),
                Number(tags, "REPLAYGAIN_ALBUM_PEAK"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            Log.Warning($"Cannot read ReplayGain tags of {path}: {ex.Message}");
        }

        Cache[path] = info;
        return info;
    }

    private static double? Number(Dictionary<string, string> tags, string key)
    {
        if (!tags.TryGetValue(key, out var value))
            return null;

        // "-6.54 dB", "+1.20 dB", "0.988"
        var text = value.Trim().Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static void AddTag(Dictionary<string, string> tags, string key, string value)
    {
        key = key.Trim().TrimEnd('\0');
        if (key.StartsWith("REPLAYGAIN_", StringComparison.OrdinalIgnoreCase))
            tags[key] = value.TrimEnd('\0');
    }

    // ------------------------------------------------------------------
    // ID3v2 (TXXX frames)
    // ------------------------------------------------------------------

    private static void ReadId3v2(Stream stream, string path, Dictionary<string, string> tags)
    {
        if (TagReader.LocateId3v2(stream, path) is not long offset)
            return;

        foreach (var frame in Id3v2.ReadFrames(stream, offset, id => id is "TXXX" or "TXX"))
        {
            if (frame.Data.Length == 0)
                continue;

            var (description, value) = ParseUserText(frame.Data, 0, frame.Data.Length);
            AddTag(tags, description, value);
        }
    }

    private static (string Description, string Value) ParseUserText(byte[] data, int start, int length)
    {
        int encoding = data[start];
        var enc = encoding switch
        {
            1 => Encoding.Unicode,           // UTF-16 with BOM
            2 => Encoding.BigEndianUnicode,  // UTF-16BE
            3 => Encoding.UTF8,
            _ => Encoding.Latin1,
        };
        int unit = encoding is 1 or 2 ? 2 : 1;
        int pos = start + 1;
        int end = start + length;

        // The description ends with a (1 or 2 byte) null terminator.
        int descEnd = pos;
        while (descEnd + unit <= end && !(data[descEnd] == 0 && (unit == 1 || data[descEnd + 1] == 0)))
            descEnd += unit;

        string Decode(int from, int to)
        {
            if (to <= from)
                return "";
            // Honour a byte order mark in UTF-16 strings.
            if (encoding == 1 && to - from >= 2 && data[from] == 0xFE && data[from + 1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(data, from + 2, to - from - 2);
            if (encoding == 1 && to - from >= 2 && data[from] == 0xFF && data[from + 1] == 0xFE)
                return Encoding.Unicode.GetString(data, from + 2, to - from - 2);
            return enc.GetString(data, from, to - from);
        }

        var description = Decode(pos, descEnd);
        var value = Decode(Math.Min(end, descEnd + unit), end);
        return (description, value);
    }

    // ------------------------------------------------------------------
    // APEv2 (footer at the end of the file, before an ID3v1 tag)
    // ------------------------------------------------------------------

    private static void ReadApe(Stream stream, Dictionary<string, string> tags)
    {
        foreach (long footerOffset in new[] { stream.Length - 32, stream.Length - 128 - 32 })
        {
            if (footerOffset < 0)
                continue;

            stream.Position = footerOffset;
            var footer = TagBytes.Read(stream, 32);
            if (footer.Length < 32 || Encoding.ASCII.GetString(footer, 0, 8) != "APETAGEX")
                continue;

            int tagSize = TagBytes.LittleEndian(footer, 12);
            int count = TagBytes.LittleEndian(footer, 16);
            long start = footerOffset + 32 - tagSize;
            if (start < 0 || tagSize > 1024 * 1024)
                return;

            stream.Position = start;
            var data = TagBytes.Read(stream, tagSize - 32);
            int pos = 0;
            for (int i = 0; i < count && pos + 8 < data.Length; i++)
            {
                int valueSize = TagBytes.LittleEndian(data, pos);
                pos += 8;
                int keyEnd = Array.IndexOf(data, (byte)0, pos);
                if (keyEnd < 0 || keyEnd + 1 + valueSize > data.Length)
                    return;

                var key = Encoding.ASCII.GetString(data, pos, keyEnd - pos);
                var value = Encoding.UTF8.GetString(data, keyEnd + 1, valueSize);
                AddTag(tags, key, value);
                pos = keyEnd + 1 + valueSize;
            }

            return;
        }
    }

    // ------------------------------------------------------------------
    // FLAC (VORBIS_COMMENT metadata block)
    // ------------------------------------------------------------------

    private static void ReadFlac(Stream stream, Dictionary<string, string> tags)
    {
        stream.Position = 0;
        var magic = TagBytes.Read(stream, 4);
        if (magic.Length < 4 || Encoding.ASCII.GetString(magic) != "fLaC")
            return;

        bool last = false;
        while (!last)
        {
            var header = TagBytes.Read(stream, 4);
            if (header.Length < 4)
                return;

            last = (header[0] & 0x80) != 0;
            int type = header[0] & 0x7F;
            int length = (header[1] << 16) | (header[2] << 8) | header[3];
            if (type == 4)
            {
                ReadVorbisComments(TagBytes.Read(stream, length), tags);
                return;
            }

            stream.Position += length;
        }
    }

    private static void ReadVorbisComments(byte[] data, Dictionary<string, string> tags)
    {
        int pos = 0;
        int vendorLength = TagBytes.LittleEndian(data, pos);
        pos += 4 + vendorLength;
        if (pos + 4 > data.Length)
            return;

        int count = TagBytes.LittleEndian(data, pos);
        pos += 4;
        for (int i = 0; i < count && pos + 4 <= data.Length; i++)
        {
            int length = TagBytes.LittleEndian(data, pos);
            pos += 4;
            if (length < 0 || pos + length > data.Length)
                return;

            var comment = Encoding.UTF8.GetString(data, pos, length);
            pos += length;
            int eq = comment.IndexOf('=');
            if (eq > 0)
                AddTag(tags, comment[..eq], comment[(eq + 1)..]);
        }
    }

    // ------------------------------------------------------------------
    // MP4 (moov/udta/meta/ilst/---- freeform atoms)
    // ------------------------------------------------------------------

    private static void ReadMp4(Stream stream, Dictionary<string, string> tags)
    {
        stream.Position = 0;
        ReadAtoms(stream, stream.Length, tags, depth: 0);
    }

    private static void ReadAtoms(Stream stream, long end, Dictionary<string, string> tags, int depth)
    {
        while (stream.Position + 8 <= end && depth < 8)
        {
            long start = stream.Position;
            var header = TagBytes.Read(stream, 8);
            long size = (uint)TagBytes.BigEndian(header, 0);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (size == 1)
            {
                var large = TagBytes.Read(stream, 8);
                size = ((long)TagBytes.BigEndian(large, 0) << 32) | (uint)TagBytes.BigEndian(large, 4);
            }
            else if (size == 0)
            {
                size = end - start;
            }

            long atomEnd = start + size;
            if (size < 8 || atomEnd > end)
                return;

            switch (type)
            {
                case "moov" or "udta" or "ilst":
                    ReadAtoms(stream, atomEnd, tags, depth + 1);
                    break;
                case "meta":
                    stream.Position += 4;   // version and flags
                    ReadAtoms(stream, atomEnd, tags, depth + 1);
                    break;
                case "----":
                    ReadFreeform(stream, atomEnd, tags);
                    break;
            }

            stream.Position = atomEnd;
        }
    }

    private static void ReadFreeform(Stream stream, long end, Dictionary<string, string> tags)
    {
        string? name = null;
        string? value = null;
        while (stream.Position + 8 <= end)
        {
            long start = stream.Position;
            var header = TagBytes.Read(stream, 8);
            long size = (uint)TagBytes.BigEndian(header, 0);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (size < 8 || start + size > end)
                return;

            if (type == "name")
            {
                var data = TagBytes.Read(stream, (int)size - 8);
                name = data.Length > 4 ? Encoding.UTF8.GetString(data, 4, data.Length - 4) : null;
            }
            else if (type == "data")
            {
                var data = TagBytes.Read(stream, (int)size - 8);
                value = data.Length > 8 ? Encoding.UTF8.GetString(data, 8, data.Length - 8) : null;
            }

            stream.Position = start + size;
        }

        if (name is not null && value is not null)
            AddTag(tags, name, value);
    }
}
