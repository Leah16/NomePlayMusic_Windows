// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// All the text of a file's tags, for the properties window: FLAC's Vorbis comments
/// (with the encoder's vendor string) and the ID3v2 frames of MP3, WAV, AIFF, DSF and
/// DSDIFF files, under common names ("title", "tracktotal", …), and which tag formats
/// the file has.
/// </summary>
internal static class TagDump
{
    public sealed class Result
    {
        public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Formats { get; } = new();

        public string? Vendor { get; set; }
    }

    private static readonly Dictionary<string, string> VorbisNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TITLE"] = "title", ["ARTIST"] = "artist", ["ALBUMARTIST"] = "albumartist", ["ALBUM ARTIST"] = "albumartist",
        ["ALBUM"] = "album", ["DATE"] = "year", ["YEAR"] = "year", ["TRACKNUMBER"] = "track",
        ["TRACKTOTAL"] = "tracktotal", ["TOTALTRACKS"] = "tracktotal", ["DISCNUMBER"] = "disc",
        ["DISCTOTAL"] = "disctotal", ["TOTALDISCS"] = "disctotal", ["GENRE"] = "genre", ["COMPOSER"] = "composer",
        ["CONDUCTOR"] = "conductor", ["ORGANIZATION"] = "publisher", ["LABEL"] = "publisher",
        ["PUBLISHER"] = "publisher", ["GROUPING"] = "grouping", ["CONTENTGROUP"] = "grouping",
        ["COMMENT"] = "comment", ["DESCRIPTION"] = "comment", ["ENCODER"] = "encoder",
        ["ENCODED-BY"] = "encoder", ["ENCODEDBY"] = "encoder",
    };

    private static readonly Dictionary<string, string> Id3Names = new(StringComparer.Ordinal)
    {
        ["TIT2"] = "title", ["TT2"] = "title", ["TPE1"] = "artist", ["TP1"] = "artist",
        ["TPE2"] = "albumartist", ["TP2"] = "albumartist", ["TALB"] = "album", ["TAL"] = "album",
        ["TDRC"] = "year", ["TYER"] = "year", ["TYE"] = "year", ["TRCK"] = "track", ["TRK"] = "track",
        ["TPOS"] = "disc", ["TPA"] = "disc", ["TCON"] = "genre", ["TCO"] = "genre",
        ["TCOM"] = "composer", ["TCM"] = "composer", ["TPE3"] = "conductor", ["TP3"] = "conductor",
        ["TPUB"] = "publisher", ["TPB"] = "publisher", ["TIT1"] = "grouping", ["TT1"] = "grouping",
        ["GRP1"] = "grouping", ["TSSE"] = "encoder", ["TSS"] = "encoder", ["TENC"] = "encodedby", ["TEN"] = "encodedby",
        ["COMM"] = "comment", ["COM"] = "comment",
    };

    public static Result Read(string path)
    {
        var result = new Result();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".flac":
                ReadFlac(stream, result);
                break;
            case ".m4a" or ".m4b" or ".mp4" or ".alac":
                result.Formats.Add("MP4 (iTunes)");
                break;
            case ".wma":
                result.Formats.Add("ASF");
                break;
            case ".ogg" or ".oga" or ".opus":
                result.Formats.Add("Vorbis Comment");
                break;
            default:
                string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (TagReader.LocateId3v2(stream, path) is long offset)
                    ReadId3(stream, offset, result);
                if (extension == ".wav" && TagReader.HasRiffInfo(stream))
                    result.Formats.Add("RIFF INFO");
                if (extension == ".dff" && result.Formats.Count == 0)
                {
                    try
                    {
                        if (Audio.DsdFileInfo.Read(stream) is { } dff && (dff.Title ?? dff.Artist) is not null)
                            result.Formats.Add("DSDIFF DIIN");
                    }
                    catch (InvalidDataException)
                    {
                    }
                }

                if (EndsWith(stream, 128, "TAG"))
                    result.Formats.Add("ID3v1");
                if (EndsWith(stream, 32, "APETAGEX") || stream.Length > 160 && At(stream, stream.Length - 160, "APETAGEX"))
                    result.Formats.Add("APEv2");

                // What the port reads besides (RIFF INFO of WAV files, DSDIFF's master info).
                if (TagReader.Read(path) is { } tags)
                {
                    Add(result, "title", tags.Title);
                    Add(result, "artist", tags.Artists is { } a ? string.Join(", ", a) : null);
                    Add(result, "album", tags.Album);
                    Add(result, "albumartist", tags.AlbumArtist);
                    Add(result, "composer", tags.Composer);
                    Add(result, "year", tags.Year > 0 ? tags.Year.ToString() : null);
                    Add(result, "track", tags.Track > 0 ? tags.Track.ToString() : null);
                }

                break;
        }

        // "3/12" in a track or disc number
        foreach (var (key, total) in new[] { ("track", "tracktotal"), ("disc", "disctotal") })
        {
            if (result.Fields.TryGetValue(key, out var value) && value.Contains('/'))
            {
                var (number, count) = SplitNumber(value);
                result.Fields[key] = number ?? "";
                if (count is not null)
                    result.Fields.TryAdd(total, count);
            }
        }

        if (!result.Fields.ContainsKey("encoder") && result.Fields.TryGetValue("encodedby", out var encodedBy))
            result.Fields["encoder"] = encodedBy;
        return result;
    }

    /// <summary>"3/12" → ("3", "12").</summary>
    public static (string? Number, string? Total) SplitNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);

        int slash = value.IndexOf('/');
        if (slash < 0)
            return (value.Trim(), null);

        string number = value[..slash].Trim(), total = value[(slash + 1)..].Trim();
        return (number.Length > 0 ? number : null, total.Length > 0 ? total : null);
    }

    private static void ReadFlac(Stream stream, Result result)
    {
        long start = 0;
        if (Id3v2.StartsAt(stream, 0))
        {
            var header = TagBytes.Read(stream, 10);
            start = 10 + TagBytes.Syncsafe(header, 6);
            result.Formats.Add("ID3v2");
        }

        stream.Position = start;
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
            int length = header[1] << 16 | header[2] << 8 | header[3];
            if (type != 4)
            {
                stream.Position += length;
                continue;
            }

            var data = TagBytes.Read(stream, length);
            int pos = 0;
            int vendorLength = TagBytes.LittleEndian(data, pos);
            pos += 4;
            if (vendorLength < 0 || pos + vendorLength > data.Length)
                return;
            var vendor = Encoding.UTF8.GetString(data, pos, vendorLength).Trim();
            if (vendor.Length > 0)
                result.Vendor = vendor;
            pos += vendorLength;
            result.Formats.Insert(0, "Vorbis Comment");

            int count = pos + 4 <= data.Length ? TagBytes.LittleEndian(data, pos) : 0;
            pos += 4;
            for (int i = 0; i < count && pos + 4 <= data.Length; i++)
            {
                int size = TagBytes.LittleEndian(data, pos);
                pos += 4;
                if (size < 0 || pos + size > data.Length)
                    return;

                var comment = Encoding.UTF8.GetString(data, pos, size);
                pos += size;
                int eq = comment.IndexOf('=');
                if (eq > 0 && VorbisNames.TryGetValue(comment[..eq].Trim(), out var name))
                    Add(result, name, comment[(eq + 1)..], join: name is "artist" or "genre" or "composer");
            }

            return;
        }
    }

    private static void ReadId3(Stream stream, long offset, Result result)
    {
        stream.Position = offset + 3;
        int version = stream.ReadByte();
        result.Formats.Add(version is >= 2 and <= 4 ? $"ID3v2.{version}" : "ID3v2");

        foreach (var frame in Id3v2.ReadFrames(stream, offset, Id3Names.ContainsKey))
        {
            string name = Id3Names[frame.Id];
            if (name == "comment")
            {
                if (ParseComment(frame.Data) is { } comment)
                    Add(result, name, comment);
                continue;
            }

            var values = Id3v2.DecodeText(frame.Data, out _);
            if (values.Length > 0)
                Add(result, name, string.Join(", ", values));
        }
    }

    /// <summary>A COMM frame's text; iTunes' own comments (iTunNORM, iTunSMPB, …) are left out.</summary>
    private static string? ParseComment(byte[] data)
    {
        if (data.Length < 5)
            return null;

        int encoding = data[0];
        bool wide = encoding is 1 or 2;
        int pos = 4;   // encoding, language
        int end = pos;
        if (wide)
        {
            while (end + 1 < data.Length && !(data[end] == 0 && data[end + 1] == 0))
                end += 2;
        }
        else
        {
            while (end < data.Length && data[end] != 0)
                end++;
        }

        var description = Id3v2.DecodeText(new[] { (byte)encoding }.Concat(data[pos..end]).ToArray(), out _);
        if (description.Length > 0 && description[0].StartsWith("iTun", StringComparison.Ordinal))
            return null;

        int textStart = Math.Min(data.Length, end + (wide ? 2 : 1));
        var text = Id3v2.DecodeText(new[] { (byte)encoding }.Concat(data[textStart..]).ToArray(), out _);
        return text.Length > 0 ? string.Join(" ", text) : null;
    }

    private static void Add(Result result, string name, string? value, bool join = false)
    {
        value = value?.Trim().TrimEnd('\0');
        if (string.IsNullOrEmpty(value))
            return;

        if (!result.Fields.TryGetValue(name, out var existing))
            result.Fields[name] = value;
        else if (join && !existing.Split(", ").Contains(value))
            result.Fields[name] = existing + ", " + value;
    }

    private static bool EndsWith(Stream stream, int length, string magic) =>
        stream.Length >= length && At(stream, stream.Length - length, magic);

    private static bool At(Stream stream, long offset, string magic)
    {
        stream.Position = offset;
        var bytes = TagBytes.Read(stream, magic.Length);
        return bytes.Length == magic.Length && Encoding.ASCII.GetString(bytes) == magic;
    }
}
