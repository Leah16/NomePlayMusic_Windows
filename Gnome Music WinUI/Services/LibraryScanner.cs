// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// Metadata of one audio file as read from the Windows property system and
/// <see cref="TagReader"/>. This is what Tracker (LocalSearch) provides to GNOME
/// Music on Linux.
/// </summary>
public sealed class SongRecord
{
    public string Path { get; set; } = "";

    /// <summary>The <see cref="LibraryScanner.TagReaderVersion"/> the record was read with.</summary>
    public int ReaderVersion { get; set; }

    public long Size { get; set; }
    public long Modified { get; set; }
    public long Created { get; set; }
    public string? Title { get; set; }
    public string[]? Artists { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public int Track { get; set; }
    public int Disc { get; set; }
    public int Year { get; set; }
    public string? Genre { get; set; }
    public string? Composer { get; set; }
    public double Duration { get; set; }
    public bool Compilation { get; set; }
}

public sealed class LibraryCache
{
    public int Version { get; set; } = LibraryScanner.CacheVersion;
    public List<SongRecord> Songs { get; set; } = new();
}

public sealed class ScanResult
{
    public required List<SongRecord> Songs { get; init; }
    public required bool Changed { get; init; }
}

/// <summary>
/// Walks the music folders and reads tags of new or modified files. Unchanged files
/// (same size and modification time) are taken from the cache, so only the first
/// scan of a library is slow.
/// </summary>
public sealed class LibraryScanner
{
    public const int CacheVersion = 1;

    /// <summary>
    /// Bumped when tags are read differently, so cached records are read again
    /// (2: WAV "id3 " chunks and UTF-8 in legacy encoded tags).
    /// </summary>
    public const int TagReaderVersion = 2;

    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".adts", ".flac", ".wav", ".wma",
        ".ogg", ".oga", ".opus", ".aif", ".aiff", ".aifc", ".mka", ".ac3",
        ".dsf", ".dff",   // DSD, decoded by the port itself (Services/Audio/Dsd.cs)
    };

    private static readonly string[] PropertyKeys =
    {
        "System.Title",
        "System.Music.Artist",
        "System.Music.AlbumArtist",
        "System.Music.AlbumTitle",
        "System.Music.TrackNumber",
        "System.Music.PartOfSet",
        "System.Media.Year",
        "System.Music.Genre",
        "System.Music.Composer",
        "System.Media.Duration",
        "System.Music.IsCompilation",
    };

    public static bool IsAudioFile(string path) => AudioExtensions.Contains(System.IO.Path.GetExtension(path));

    /// <param name="partial">
    /// Called from a worker thread every couple of seconds while many files are read,
    /// with the library as known so far, so a first scan fills the views progressively.
    /// </param>
    public async Task<ScanResult> ScanAsync(
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, SongRecord> cache,
        Action<List<SongRecord>>? partial,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var files = await Task.Run(() => EnumerateAudioFiles(folders, cancellationToken), cancellationToken);

        var results = new ConcurrentBag<SongRecord>();
        var toRead = new List<FileInfo>();
        foreach (var file in files)
        {
            if (cache.TryGetValue(file.FullName, out var cached)
                && cached.Size == file.Length
                && cached.Modified == file.LastWriteTimeUtc.Ticks
                && cached.ReaderVersion == TagReaderVersion)
            {
                results.Add(cached);
            }
            else
            {
                toRead.Add(file);
            }
        }

        var partialTimer = Stopwatch.StartNew();
        var pending = new ConcurrentDictionary<string, bool>(toRead.Select(f => KeyValuePair.Create(f.FullName, true)), StringComparer.OrdinalIgnoreCase);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = cancellationToken,
        };
        await Parallel.ForEachAsync(toRead, options, async (file, token) =>
        {
            results.Add(await ReadRecordAsync(file).ConfigureAwait(false));
            pending.TryRemove(file.FullName, out _);

            if (partial is not null && partialTimer.ElapsedMilliseconds > 2000)
            {
                lock (partialTimer)
                {
                    if (partialTimer.ElapsedMilliseconds <= 2000)
                        return;
                    partialTimer.Restart();
                }

                // Songs still waiting to be read keep their previous metadata.
                var snapshot = results.ToList();
                snapshot.AddRange(pending.Keys.Select(p => cache.TryGetValue(p, out var old) ? old : null).OfType<SongRecord>());
                partial(snapshot);
            }
        }).ConfigureAwait(false);

        var songs = results.ToList();
        bool changed = toRead.Count > 0 || songs.Count != cache.Count;
        Log.Info($"Scan: {songs.Count} songs, {toRead.Count} read in {stopwatch.ElapsedMilliseconds} ms");
        return new ScanResult { Songs = songs, Changed = changed };
    }

    public static List<FileInfo> EnumerateAudioFiles(IReadOnlyList<string> folders, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FileInfo>();
        foreach (var folder in NormalizeFolders(folders))
        {
            if (!Directory.Exists(folder))
                continue;

            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AudioExtensions.Contains(file.Extension) && seen.Add(file.FullName))
                        files.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"Cannot enumerate {folder}: {ex.Message}");
            }
        }

        return files;
    }

    /// <summary>Removes duplicates and folders nested in other folders of the list.</summary>
    public static List<string> NormalizeFolders(IEnumerable<string> folders)
    {
        var full = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(f)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.Length)
            .ToList();

        var result = new List<string>();
        foreach (var folder in full)
        {
            bool nested = result.Any(parent =>
                folder.StartsWith(parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!nested)
                result.Add(folder);
        }

        return result;
    }

    public static async Task<SongRecord> ReadRecordAsync(FileInfo file)
    {
        var record = new SongRecord
        {
            Path = file.FullName,
            Size = file.Length,
            Modified = file.LastWriteTimeUtc.Ticks,
            Created = file.CreationTimeUtc.Ticks,
        };

        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(file.FullName);
            var props = await storageFile.Properties.RetrievePropertiesAsync(PropertyKeys);

            record.Title = GetString(props, "System.Title");
            record.Artists = GetStrings(props, "System.Music.Artist");
            record.AlbumArtist = GetString(props, "System.Music.AlbumArtist");
            record.Album = GetString(props, "System.Music.AlbumTitle");
            record.Track = (int)GetUInt(props, "System.Music.TrackNumber");
            record.Year = (int)GetUInt(props, "System.Media.Year");
            record.Genre = JoinOrNull(GetStrings(props, "System.Music.Genre"));
            record.Composer = JoinOrNull(GetStrings(props, "System.Music.Composer"));
            record.Compilation = props.TryGetValue("System.Music.IsCompilation", out var comp) && comp is bool b && b;

            if (props.TryGetValue("System.Media.Duration", out var duration) && duration is ulong ticks)
                record.Duration = ticks / 10_000_000.0;

            record.Disc = ParsePartOfSet(GetString(props, "System.Music.PartOfSet"));
        }
        catch (Exception ex)
        {
            // Unsupported container or unreadable file: keep it with what we know,
            // like Tracker does for files it cannot extract.
            Log.Warning($"Cannot read the tags of {file.FullName}: {ex.Message}");
        }

        try
        {
            // What the property system misses or garbles (WAV "id3 " chunks, UTF-8 in legacy encoded tags).
            TagReader.Read(file.FullName)?.ApplyTo(record);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot read the tags of {file.FullName}: {ex.Message}");
        }

        record.ReaderVersion = TagReaderVersion;
        return record;
    }

    private static int ParsePartOfSet(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        int slash = value.IndexOf('/');
        var first = slash >= 0 ? value[..slash] : value;
        return int.TryParse(first.Trim(), out int disc) && disc > 0 ? disc : 0;
    }

    private static string? GetString(IDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var value) || value is null)
            return null;

        var text = value switch
        {
            string s => s,
            string[] array => string.Join("; ", array),
            _ => value.ToString(),
        };
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string[]? GetStrings(IDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var value) || value is null)
            return null;

        string[] values = value switch
        {
            string[] array => array,
            string s => new[] { s },
            _ => Array.Empty<string>(),
        };
        var cleaned = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
        return cleaned.Length > 0 ? cleaned : null;
    }

    private static uint GetUInt(IDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var value) || value is null)
            return 0;

        return value switch
        {
            uint u => u,
            int i when i > 0 => (uint)i,
            ushort us => us,
            ulong ul when ul <= uint.MaxValue => (uint)ul,
            _ => 0,
        };
    }

    private static string? JoinOrNull(string[]? values) => values is { Length: > 0 } ? string.Join(", ", values) : null;
}
