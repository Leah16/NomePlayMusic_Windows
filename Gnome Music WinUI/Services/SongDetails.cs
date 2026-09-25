// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services.Audio;
using Windows.Storage;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// What the song properties window shows (not in GNOME Music): the file, its audio
/// stream and all its tags, read when the window opens. The tags come from the file
/// itself where the port reads them (FLAC's Vorbis comments, ID3v2 in MP3, WAV, AIFF
/// and DSD files), else from the Windows property system, else from the library.
/// </summary>
public sealed class SongDetails
{
    private SongDetails(string path) => Path = path;

    public string Path { get; }

    /// <summary>Windows' name for the file type ("FLAC 文件").</summary>
    public string? TypeName { get; private set; }

    public long Size { get; private set; }

    public DateTime Modified { get; private set; }

    /// <summary>The audio stream as the decoder sees it; null when it cannot be opened.</summary>
    public SourceFormat? Format { get; private set; }

    /// <summary>"FLAC", "MP3", "DSD (DSF)", …</summary>
    public string? Codec { get; private set; }

    public double Duration { get; private set; }

    /// <summary>Bits per second: the stream's for lossy formats, the file's average otherwise.</summary>
    public int Bitrate { get; private set; }

    public string? Encoder { get; private set; }

    /// <summary>"ID3v2.3, ID3v1", "Vorbis Comment", …</summary>
    public string? TagFormat { get; private set; }

    public ReplayGainInfo ReplayGain { get; private set; }

    public string? Title { get; private set; }

    public string? Artist { get; private set; }

    public string? AlbumArtist { get; private set; }

    public string? Album { get; private set; }

    public string? Year { get; private set; }

    public string? Track { get; private set; }

    public string? TrackTotal { get; private set; }

    public string? Disc { get; private set; }

    public string? DiscTotal { get; private set; }

    public string? Genre { get; private set; }

    public string? Composer { get; private set; }

    public string? Conductor { get; private set; }

    public string? Publisher { get; private set; }

    public string? Grouping { get; private set; }

    public string? Comment { get; private set; }

    public static async Task<SongDetails> ReadAsync(CoreSong song)
    {
        var details = new SongDetails(song.FilePath);
        var dump = await Task.Run(() => details.ReadFile()).ConfigureAwait(true);

        // What Windows reads, for the formats the port does not parse itself.
        var props = new Dictionary<string, object>();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(song.FilePath);
            details.TypeName = file.DisplayType;
            props = new Dictionary<string, object>(await file.Properties.RetrievePropertiesAsync(PropertyKeys));
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot read the properties of {song.FilePath}: {ex.Message}");
        }

        string? Own(string key) => dump.Fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        string? Windows(string key) => props.TryGetValue(key, out var value) ? Text(value) : null;

        details.Title = Own("title") ?? Windows("System.Title") ?? song.Title;
        details.Artist = Own("artist") ?? Windows("System.Music.Artist") ?? song.RawArtist;
        details.AlbumArtist = Own("albumartist") ?? Windows("System.Music.AlbumArtist") ?? song.AlbumArtist;
        details.Album = Own("album") ?? Windows("System.Music.AlbumTitle") ?? (song.HasAlbum ? song.AlbumTitle : null);
        details.Year = Own("year") ?? Windows("System.Media.Year") ?? (song.Year > 0 ? song.Year.ToString() : null);
        details.Track = Own("track") ?? Windows("System.Music.TrackNumber") ?? (song.TrackNumber > 0 ? song.TrackNumber.ToString() : null);
        details.TrackTotal = Own("tracktotal");
        var (disc, discTotal) = TagDump.SplitNumber(Windows("System.Music.PartOfSet"));
        details.Disc = Own("disc") ?? disc;
        details.DiscTotal = Own("disctotal") ?? discTotal;
        details.Genre = Own("genre") ?? Windows("System.Music.Genre") ?? song.Record.Genre;
        details.Composer = Own("composer") ?? Windows("System.Music.Composer") ?? song.Composer;
        details.Conductor = Own("conductor") ?? Windows("System.Music.Conductor");
        details.Publisher = Own("publisher") ?? Windows("System.Media.Publisher");
        details.Grouping = Own("grouping") ?? Windows("System.Music.ContentGroupDescription");
        details.Comment = Own("comment") ?? Windows("System.Comment");
        details.Encoder = Own("encoder") ?? dump.Vendor ?? Windows("System.Media.EncodingSettings") ?? Windows("System.Media.EncodedBy");
        details.TagFormat = dump.Formats.Count > 0 ? string.Join(", ", dump.Formats) : null;
        return details;
    }

    private static readonly string[] PropertyKeys =
    {
        "System.Title", "System.Music.Artist", "System.Music.AlbumArtist", "System.Music.AlbumTitle",
        "System.Media.Year", "System.Music.TrackNumber", "System.Music.PartOfSet", "System.Music.Genre",
        "System.Music.Composer", "System.Music.Conductor", "System.Media.Publisher",
        "System.Music.ContentGroupDescription", "System.Comment", "System.Media.EncodingSettings",
        "System.Media.EncodedBy",
    };

    /// <summary>The file, the audio stream, the ReplayGain and the tags the port reads (worker thread).</summary>
    private TagDump.Result ReadFile()
    {
        try
        {
            var info = new FileInfo(Path);
            Size = info.Length;
            Modified = info.LastWriteTime;
        }
        catch (IOException ex)
        {
            Log.Warning($"Cannot read {Path}: {ex.Message}");
        }

        try
        {
            if (DsdFileInfo.IsDsdPath(Path))
            {
                using var reader = new DsdReader(Path);
                Format = reader.Format;
                Duration = reader.Info.Duration;
                Codec = reader.Info.IsDsf ? "DSD (DSF)" : "DSD (DSDIFF)";
            }
            else
            {
                using var decoder = new MediaFoundationDecoder(Path);
                Format = decoder.Format;
                Duration = decoder.Duration;
                Codec = decoder.Format.Codec;
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot open {Path} for its format: {ex.Message}");
        }

        if (Format is { Lossless: false, Bitrate: > 0 } lossy)
            Bitrate = lossy.Bitrate;
        else if (Duration > 0 && Size > 0)
            Bitrate = (int)(Size * 8 / Duration);

        ReplayGain = Services.ReplayGain.Read(Path);
        try
        {
            return TagDump.Read(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Log.Warning($"Cannot read the tags of {Path}: {ex.Message}");
            return new TagDump.Result();
        }
    }

    private static string? Text(object? value)
    {
        var text = value switch
        {
            null => null,
            string s => s,
            string[] array => string.Join(", ", array.Where(a => !string.IsNullOrWhiteSpace(a))),
            uint u => u > 0 ? u.ToString() : null,
            _ => value.ToString(),
        };
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
