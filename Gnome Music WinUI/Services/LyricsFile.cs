// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using System.Text;

namespace Gnome_Music_WinUI.Services;

/// <summary>A file's time and size when it was looked at; Exists is false when there was none.</summary>
public readonly record struct FileStamp(bool Exists, DateTime Time, long Length);

/// <summary>
/// A song's lyrics file (not in GNOME Music): the LRC file with the song's name in its
/// folder ("Song.flac" → "Song.lrc"), as other players keep them. It is read in the
/// encodings such files come in and written as UTF-8 with a BOM and Windows line ends,
/// which every player and editor reads.
/// </summary>
public static class LyricsFile
{
    private const int ERROR_FILE_EXISTS = unchecked((int)0x80070050);

    static LyricsFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string PathFor(string songPath) => Path.ChangeExtension(songPath, ".lrc");

    public static FileStamp Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(true, info.LastWriteTimeUtc, info.Length) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return default;
        }
    }

    /// <summary>
    /// The text of a lyrics file with "\n" line ends: UTF-8 or UTF-16 by their BOM,
    /// UTF-8 without one, else the system's ANSI code page (GBK on Chinese Windows,
    /// where older LRC files come from). Throws when the file cannot be read.
    /// </summary>
    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else
            text = DecodeWithoutBom(bytes);

        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>
    /// Writes lyrics to a file: a new one only when <paramref name="replace"/> is false
    /// (false is returned when there is one already), else in place of the old one,
    /// which stays whole if writing fails.
    /// </summary>
    public static bool Write(string path, string text, bool replace)
    {
        var bytes = Encode(text);
        if (!replace)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                return true;
            }
            catch (IOException ex) when (ex.HResult == ERROR_FILE_EXISTS)
            {
                return false;
            }
        }

        string temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static byte[] Encode(string text)
    {
        string content = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n').Replace("\n", "\r\n") + "\r\n";
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = new byte[preamble.Length + Encoding.UTF8.GetByteCount(content)];
        preamble.CopyTo(bytes, 0);
        Encoding.UTF8.GetBytes(content, 0, content.Length, bytes, preamble.Length);
        return bytes;
    }

    private static string DecodeWithoutBom(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Code page 0: the system's ANSI code page, once the provider is registered.
            return Encoding.GetEncoding(0).GetString(bytes);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
