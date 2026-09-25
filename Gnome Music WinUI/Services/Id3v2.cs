// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Gnome_Music_WinUI.Services;

/// <summary>A frame of an ID3v2 tag: its identifier (3 characters in ID3v2.2) and data.</summary>
public sealed record Id3Frame(string Id, byte[] Data);

/// <summary>An attached picture (APIC, or PIC in ID3v2.2).</summary>
public sealed record Id3Picture(string MimeType, int Type, byte[] Data)
{
    public const int FrontCover = 3;
}

/// <summary>
/// ID3v2.2/2.3/2.4 reader: tag and frame headers, extended headers,
/// unsynchronisation and frame format flags. Compressed and encrypted frames are
/// skipped, as are frames that were not asked for (the stream seeks over them, so
/// large pictures are not read while scanning).
/// </summary>
public static class Id3v2
{
    /// <summary>Frames larger than this are skipped.</summary>
    private const int MaxFrameSize = 16 * 1024 * 1024;

    public static bool StartsAt(Stream stream, long offset)
    {
        stream.Position = offset;
        var header = TagBytes.Read(stream, 3);
        return header.Length == 3 && header[0] == 'I' && header[1] == 'D' && header[2] == '3';
    }

    /// <summary>The frames of the tag at <paramref name="offset"/> whose id is <paramref name="wanted"/>.</summary>
    public static IEnumerable<Id3Frame> ReadFrames(Stream stream, long offset, Func<string, bool> wanted)
    {
        stream.Position = offset;
        var header = TagBytes.Read(stream, 10);
        if (header.Length < 10 || header[0] != 'I' || header[1] != 'D' || header[2] != '3')
            yield break;

        int version = header[3];
        int flags = header[5];

        // ID3v2.2 used this flag for a compression scheme that was never defined.
        if (version is < 2 or > 4 || (version == 2 && (flags & 0x40) != 0))
            yield break;

        Stream source = stream;
        long pos = offset + 10;
        long end = Math.Min(stream.Length, pos + TagBytes.Syncsafe(header, 6));

        // ID3v2.2/2.3 unsynchronise the whole tag; ID3v2.4 does it per frame.
        if ((flags & 0x80) != 0 && version < 4)
        {
            source = new MemoryStream(RemoveUnsynchronisation(TagBytes.Read(stream, (int)(end - pos))));
            pos = 0;
            end = source.Length;
        }

        if ((flags & 0x40) != 0)
        {
            source.Position = pos;
            var extended = TagBytes.Read(source, 4);
            if (extended.Length < 4)
                yield break;

            // ID3v2.3 does not count the size field itself, ID3v2.4 does.
            pos += version == 3 ? 4 + (uint)TagBytes.BigEndian(extended, 0) : TagBytes.Syncsafe(extended, 0);
        }

        int idLength = version == 2 ? 3 : 4;
        int headerLength = version == 2 ? 6 : 10;
        while (pos + headerLength <= end)
        {
            source.Position = pos;
            var frameHeader = TagBytes.Read(source, headerLength);
            if (frameHeader.Length < headerLength || !IsFrameId(frameHeader, idLength))
                yield break;   // padding

            string id = Encoding.ASCII.GetString(frameHeader, 0, idLength);
            long size = version switch
            {
                2 => frameHeader[3] << 16 | frameHeader[4] << 8 | frameHeader[5],
                3 => (uint)TagBytes.BigEndian(frameHeader, 4),
                _ => TagBytes.Syncsafe(frameHeader, 4),
            };
            long dataStart = pos + headerLength;
            if (size <= 0 || dataStart + size > end)
                yield break;

            pos = dataStart + size;
            if (!wanted(id) || size > MaxFrameSize)
                continue;

            // Additions to the frame header, in the order of their flags.
            int format = version == 2 ? 0 : frameHeader[9];
            int prefix = 0;
            if (version == 3)
            {
                if ((format & 0xC0) != 0)
                    continue;   // compressed or encrypted
                if ((format & 0x20) != 0)
                    prefix = 1;   // grouping identity
            }
            else if (version == 4)
            {
                if ((format & 0x0C) != 0)
                    continue;   // compressed or encrypted
                if ((format & 0x40) != 0)
                    prefix += 1;   // grouping identity
                if ((format & 0x01) != 0)
                    prefix += 4;   // data length indicator
            }

            source.Position = dataStart;
            var data = TagBytes.Read(source, (int)size);
            if (prefix > 0)
                data = data.Length > prefix ? data[prefix..] : Array.Empty<byte>();
            if (version == 4 && ((format & 0x02) != 0 || (flags & 0x80) != 0))
                data = RemoveUnsynchronisation(data);

            yield return new Id3Frame(id, data);
        }
    }

    /// <summary>
    /// The values of a text frame (ID3v2.4 separates several values with NULs).
    /// <paramref name="legacy"/> tells whether the frame claims ISO-8859-1 but holds
    /// non-ASCII bytes, which <see cref="LegacyText"/> decodes.
    /// </summary>
    public static string[] DecodeText(byte[] data, out bool legacy)
    {
        legacy = false;
        if (data.Length < 2)
            return Array.Empty<string>();

        var body = data.AsSpan(1);
        string text;
        switch (data[0])
        {
            case 1:
                text = DecodeUtf16(body, bigEndian: false);
                break;
            case 2:
                text = DecodeUtf16(body, bigEndian: true);
                break;
            case 3:
                text = Encoding.UTF8.GetString(body);
                break;
            default:
                legacy = !Ascii.IsValid(body);
                text = LegacyText.Decode(body);
                break;
        }

        return text.Split('\0')
            .Select(v => v.Replace("﻿", "").Trim())
            .Where(v => v.Length > 0)
            .ToArray();
    }

    public static Id3Picture? ParsePicture(Id3Frame frame)
    {
        var data = frame.Data;
        if (data.Length < 4)
            return null;

        int encoding = data[0];
        int pos;
        string mimeType;
        if (frame.Id == "PIC")
        {
            // ID3v2.2: a three letter image format instead of a MIME type.
            mimeType = Encoding.ASCII.GetString(data, 1, 3);
            pos = 4;
        }
        else
        {
            int mimeEnd = Array.IndexOf(data, (byte)0, 1);
            if (mimeEnd < 0)
                return null;

            mimeType = Encoding.ASCII.GetString(data, 1, mimeEnd - 1);
            pos = mimeEnd + 1;
        }

        if (pos >= data.Length)
            return null;

        int type = data[pos++];
        pos = SkipTerminatedString(data, pos, wide: encoding is 1 or 2);
        return pos < data.Length ? new Id3Picture(mimeType, type, data[pos..]) : null;
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            bigEndian = true;
            bytes = bytes[2..];
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            bigEndian = false;
            bytes = bytes[2..];
        }

        bytes = bytes[..(bytes.Length & ~1)];
        return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(bytes);
    }

    private static int SkipTerminatedString(byte[] data, int pos, bool wide)
    {
        if (!wide)
        {
            int end = Array.IndexOf(data, (byte)0, pos);
            return end < 0 ? data.Length : end + 1;
        }

        for (int i = pos; i + 1 < data.Length; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
                return i + 2;
        }

        return data.Length;
    }

    private static bool IsFrameId(byte[] header, int length)
    {
        for (int i = 0; i < length; i++)
        {
            byte c = header[i];
            if (c is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')))
                return false;
        }

        return true;
    }

    /// <summary>Undoes unsynchronisation: every 0xFF 0x00 was written for a 0xFF.</summary>
    private static byte[] RemoveUnsynchronisation(byte[] data)
    {
        var output = new byte[data.Length];
        int count = 0;
        for (int i = 0; i < data.Length; i++)
        {
            output[count++] = data[i];
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0x00)
                i++;
        }

        return count == data.Length ? output : output[..count];
    }
}
