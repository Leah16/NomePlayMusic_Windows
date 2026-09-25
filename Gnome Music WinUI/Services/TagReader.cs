// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Gnome_Music_WinUI.Services.Audio;

namespace Gnome_Music_WinUI.Services;

/// <summary>Values read by <see cref="TagReader"/>; null or 0 where a tag has none.</summary>
public sealed class FileTags
{
    public string? Title { get; set; }

    public string[]? Artists { get; set; }

    public string? AlbumArtist { get; set; }

    public string? Album { get; set; }

    public string? Composer { get; set; }

    public int Track { get; set; }

    public int Disc { get; set; }

    public int Year { get; set; }

    public bool Compilation { get; set; }

    /// <summary>In seconds; read for files Windows cannot (DSD).</summary>
    public double Duration { get; set; }

    public bool IsEmpty =>
        Title is null && Artists is null && AlbumArtist is null && Album is null && Composer is null
        && Track == 0 && Disc == 0 && Year == 0 && !Compilation && Duration == 0;

    /// <summary>Replaces the values read through the Windows property system.</summary>
    public void ApplyTo(SongRecord record)
    {
        if (Title is not null)
            record.Title = Title;
        if (Artists is not null)
            record.Artists = Artists;
        if (AlbumArtist is not null)
            record.AlbumArtist = AlbumArtist;
        if (Album is not null)
            record.Album = Album;
        if (Composer is not null)
            record.Composer = Composer;
        if (Track > 0)
            record.Track = Track;
        if (Disc > 0)
            record.Disc = Disc;
        if (Year > 0)
            record.Year = Year;
        if (Compilation)
            record.Compilation = true;
        if (Duration > 0)
            record.Duration = Duration;
    }
}

/// <summary>
/// Reads the tags that the Windows property system misses or garbles. LocalSearch
/// reads them with GStreamer, which handles all of these cases:
/// <list type="bullet">
/// <item>WAV: Windows only reads the RIFF INFO list, decoded with the ANSI code page,
/// and ignores the "id3 " chunk that most taggers write (with the album artist,
/// composer, disc number and cover).</item>
/// <item>AIFF: the "ID3 " chunk.</item>
/// <item>MP3: ID3v2 frames marked ISO-8859-1 that actually hold UTF-8; only these
/// values replace what Windows read.</item>
/// <item>DSF and DSDIFF (DSD), which Windows does not read at all: the ID3v2 tag (the
/// DSF metadata chunk, the DSDIFF "ID3 " chunk), else DSDIFF's title and artist, and
/// the duration.</item>
/// </list>
/// </summary>
public static class TagReader
{
    private static readonly HashSet<string> TextFrames = new(StringComparer.Ordinal)
    {
        "TIT2", "TT2", "TPE1", "TP1", "TPE2", "TP2", "TALB", "TAL", "TCOM", "TCM",
        "TRCK", "TRK", "TPOS", "TPA", "TYER", "TYE", "TDRC", "TCMP", "TCP",
    };

    private enum Container
    {
        Other,
        Mpeg,
        Wave,
        Aiff,
        Dsd,
    }

    public static FileTags? Read(string path)
    {
        var container = ContainerOf(path);
        if (container == Container.Other)
            return null;

        using var stream = OpenRead(path);
        var tags = new FileTags();
        switch (container)
        {
            case Container.Wave:
                ReadRiffInfo(stream, tags);
                if (FindChunk(stream, container, "id3 ", "ID3 ") is long waveTag)
                    ReadId3Text(stream, waveTag, tags, legacyOnly: false);
                break;
            case Container.Aiff:
                if (FindChunk(stream, container, "ID3 ", "id3 ") is long aiffTag)
                    ReadId3Text(stream, aiffTag, tags, legacyOnly: false);
                break;
            case Container.Mpeg:
                if (Id3v2.StartsAt(stream, 0))
                    ReadId3Text(stream, 0, tags, legacyOnly: true);
                break;
            case Container.Dsd:
                ReadDsd(stream, tags);
                break;
        }

        return tags.IsEmpty ? null : tags;
    }

    private static void ReadDsd(Stream stream, FileTags tags)
    {
        DsdFileInfo info;
        try
        {
            info = DsdFileInfo.Read(stream);
        }
        catch (InvalidDataException ex)
        {
            Log.Warning($"Not a playable DSD file: {ex.Message}");
            return;
        }

        tags.Duration = info.Duration;
        if (info.Id3Offset is long offset && Id3v2.StartsAt(stream, offset))
            ReadId3Text(stream, offset, tags, legacyOnly: false);
        tags.Title ??= info.Title;
        if (tags.Artists is null && info.Artist is not null)
            tags.Artists = new[] { info.Artist };
    }

    /// <summary>The front cover (else the first picture) of the file's ID3v2 tag.</summary>
    public static byte[]? ReadFrontCover(string path)
    {
        using var stream = OpenRead(path);
        if (LocateId3v2(stream, path) is not long offset)
            return null;

        byte[]? first = null;
        foreach (var frame in Id3v2.ReadFrames(stream, offset, id => id is "APIC" or "PIC"))
        {
            if (Id3v2.ParsePicture(frame) is not { } picture)
                continue;
            if (picture.Type == Id3Picture.FrontCover)
                return picture.Data;
            first ??= picture.Data;
        }

        return first;
    }

    /// <summary>
    /// Where the ID3v2 tag starts: in the "id3 " chunk of WAV and AIFF files, at the
    /// beginning of other files (MP3).
    /// </summary>
    public static long? LocateId3v2(Stream stream, string path)
    {
        var container = ContainerOf(path);
        if (container is Container.Wave or Container.Aiff)
            return FindChunk(stream, container, "id3 ", "ID3 ");

        if (container == Container.Dsd)
        {
            try
            {
                return DsdFileInfo.Read(stream).Id3Offset is long offset && Id3v2.StartsAt(stream, offset) ? offset : null;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        return Id3v2.StartsAt(stream, 0) ? 0 : null;
    }

    /// <summary>Whether a WAV file has a LIST/INFO chunk (the properties window names it).</summary>
    public static bool HasRiffInfo(Stream stream)
    {
        foreach (var (id, offset, size) in EnumerateChunks(stream, Container.Wave))
        {
            if (id != "LIST" || size < 4)
                continue;
            stream.Position = offset;
            if (Encoding.ASCII.GetString(TagBytes.Read(stream, 4)) == "INFO")
                return true;
        }

        return false;
    }

    private static Container ContainerOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => Container.Mpeg,
        ".wav" => Container.Wave,
        ".aif" or ".aiff" or ".aifc" => Container.Aiff,
        ".dsf" or ".dff" => Container.Dsd,
        _ => Container.Other,
    };

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    private static void ReadId3Text(Stream stream, long offset, FileTags tags, bool legacyOnly)
    {
        foreach (var frame in Id3v2.ReadFrames(stream, offset, TextFrames.Contains))
        {
            var values = Id3v2.DecodeText(frame.Data, out bool legacy);
            if (values.Length == 0 || (legacyOnly && !legacy))
                continue;

            switch (frame.Id)
            {
                case "TIT2" or "TT2":
                    tags.Title = values[0];
                    break;
                case "TPE1" or "TP1":
                    tags.Artists = values;
                    break;
                case "TPE2" or "TP2":
                    tags.AlbumArtist = string.Join(", ", values);
                    break;
                case "TALB" or "TAL":
                    tags.Album = values[0];
                    break;
                case "TCOM" or "TCM":
                    tags.Composer = string.Join(", ", values);
                    break;
                case "TRCK" or "TRK" when LeadingNumber(values[0]) is > 0 and var track:
                    tags.Track = track;
                    break;
                case "TPOS" or "TPA" when LeadingNumber(values[0]) is > 0 and var disc:
                    tags.Disc = disc;
                    break;
                case "TYER" or "TYE" or "TDRC" when Year(values[0]) is > 0 and var year:
                    tags.Year = year;
                    break;
                case "TCMP" or "TCP":
                    tags.Compilation = values[0] == "1";
                    break;
            }
        }
    }

    /// <summary>The LIST/INFO chunk of a WAV file, with the text decoded by <see cref="LegacyText"/>.</summary>
    private static void ReadRiffInfo(Stream stream, FileTags tags)
    {
        foreach (var (id, offset, size) in EnumerateChunks(stream, Container.Wave))
        {
            if (id != "LIST" || size is < 4 or > 1024 * 1024)
                continue;

            stream.Position = offset;
            var list = TagBytes.Read(stream, (int)size);
            if (list.Length < 4 || Encoding.ASCII.GetString(list, 0, 4) != "INFO")
                continue;

            int pos = 4;
            while (pos + 8 <= list.Length)
            {
                string key = Encoding.ASCII.GetString(list, pos, 4);
                int length = TagBytes.LittleEndian(list, pos + 4);
                int start = pos + 8;
                if (length < 0 || start + length > list.Length)
                    break;

                pos = start + length + (length & 1);
                var value = LegacyText.Decode(list.AsSpan(start, length).TrimEnd((byte)0)).Trim();
                if (value.Length == 0)
                    continue;

                switch (key)
                {
                    case "INAM":
                        tags.Title = value;
                        break;
                    case "IART":
                        tags.Artists = new[] { value };
                        break;
                    case "IPRD":
                        tags.Album = value;
                        break;
                    case "ICRD":
                        tags.Year = Year(value);
                        break;
                    case "ITRK":
                        tags.Track = LeadingNumber(value);
                        break;
                    case "IPRT" when tags.Track == 0:
                        tags.Track = LeadingNumber(value);
                        break;
                }
            }
        }
    }

    private static long? FindChunk(Stream stream, Container container, string id, string alternativeId)
    {
        foreach (var chunk in EnumerateChunks(stream, container))
        {
            if (chunk.Id == id || chunk.Id == alternativeId)
                return chunk.Offset;
        }

        return null;
    }

    /// <summary>The top-level chunks of a RIFF/WAVE (little endian) or FORM/AIFF (big endian) file.</summary>
    private static IEnumerable<(string Id, long Offset, long Size)> EnumerateChunks(Stream stream, Container container)
    {
        stream.Position = 0;
        var header = TagBytes.Read(stream, 12);
        if (header.Length < 12)
            yield break;

        string magic = Encoding.ASCII.GetString(header, 0, 4);
        string form = Encoding.ASCII.GetString(header, 8, 4);
        bool littleEndian = container == Container.Wave;
        bool valid = littleEndian
            ? magic == "RIFF" && form == "WAVE"
            : magic == "FORM" && form is "AIFF" or "AIFC";
        if (!valid)
            yield break;

        long pos = 12;
        while (pos + 8 <= stream.Length)
        {
            stream.Position = pos;
            var chunk = TagBytes.Read(stream, 8);
            if (chunk.Length < 8)
                yield break;

            string id = Encoding.ASCII.GetString(chunk, 0, 4);
            long size = (uint)(littleEndian ? TagBytes.LittleEndian(chunk, 4) : TagBytes.BigEndian(chunk, 4));
            yield return (id, pos + 8, size);

            // Chunks are padded to an even size.
            pos += 8 + size + (size & 1);
        }
    }

    /// <summary>The number at the start of "7", "7/12", "07 of 12".</summary>
    private static int LeadingNumber(string text)
    {
        int value = 0;
        foreach (char c in text.TrimStart())
        {
            if (!char.IsAsciiDigit(c) || value > 100_000)
                break;
            value = value * 10 + (c - '0');
        }

        return value;
    }

    /// <summary>The first four digit number: "2026", "2026-08-14", "14 Aug 2026".</summary>
    private static int Year(string text)
    {
        for (int i = 0; i + 4 <= text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i]) && char.IsAsciiDigit(text[i + 1]) && char.IsAsciiDigit(text[i + 2]) && char.IsAsciiDigit(text[i + 3]))
                return int.Parse(text.AsSpan(i, 4));
        }

        return 0;
    }
}

/// <summary>
/// Text in tags that have no encoding of their own (RIFF INFO) or claim to be
/// ISO-8859-1 (ID3v2), which in practice is often UTF-8 or a local code page.
/// Like GStreamer's gst_tag_freeform_string_to_utf8(), valid UTF-8 is taken as
/// UTF-8. Anything else is decoded with the system's ANSI code page, as the Windows
/// property handlers do, so tags written in GBK on Chinese systems keep working.
/// </summary>
public static class LegacyText
{
    private static readonly Lazy<Encoding> AnsiEncoding = new(CreateAnsiEncoding);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (Ascii.IsValid(bytes))
            return Encoding.ASCII.GetString(bytes);

        return Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : AnsiEncoding.Value.GetString(bytes);
    }

    private static Encoding CreateAnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int codePage = (int)GetACP();
            return codePage == 65001 ? Encoding.UTF8 : Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}

/// <summary>Byte helpers shared by the tag readers.</summary>
internal static class TagBytes
{
    /// <summary>Reads <paramref name="count"/> bytes, or fewer at the end of the stream.</summary>
    public static byte[] Read(Stream stream, int count)
    {
        if (count <= 0)
            return Array.Empty<byte>();

        var buffer = new byte[count];
        int read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return read == count ? buffer : buffer[..read];
    }

    public static int Syncsafe(byte[] b, int i) =>
        (b[i] & 0x7F) << 21 | (b[i + 1] & 0x7F) << 14 | (b[i + 2] & 0x7F) << 7 | (b[i + 3] & 0x7F);

    public static int BigEndian(byte[] b, int i) => b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3];

    public static int LittleEndian(byte[] b, int i) => b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24;
}
