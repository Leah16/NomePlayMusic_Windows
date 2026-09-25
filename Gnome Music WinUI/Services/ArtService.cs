// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// Album art lookup and cache, the counterpart of GNOME Music's MediaArt handling
/// (embeddedart.py / storeart.py / mediaartloader.py). Art is looked up in this order:
/// 1. art embedded in the first song of the album, read through the Windows thumbnail
///    cache, else from the song's ID3v2 tag (Windows makes no thumbnails of WAV files),
/// 2. an image file next to the songs (cover.jpg, folder.jpg, …).
/// Found art is stored in the app's media-art folder; the result is a file path.
/// </summary>
public sealed partial class ArtService
{
    private const uint ThumbnailSize = 512;

    private static readonly string[] FolderArtNames =
    {
        "cover", "folder", "front", "album", "albumart", "albumartlarge", "albumartsmall", "thumb",
    };

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

    private readonly ConcurrentDictionary<string, Task<string?>> _lookups = new();
    private readonly SemaphoreSlim _gate = new(3);

    /// <summary>Bump to invalidate cached art after a change of the cache format.</summary>
    private const string CacheVersion = "2";

    public ArtService()
    {
        Directory.CreateDirectory(AppPaths.ArtCacheDirectory);

        // Cached art is always JPEG now; drop files of older cache formats.
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.ArtCacheDirectory, "*.bmp"))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        });
    }

    public Task<string?> GetAlbumArtAsync(CoreAlbum album)
    {
        var first = album.Songs.FirstOrDefault();
        return first is null ? Task.FromResult<string?>(null) : GetArtAsync(album.Id, first);
    }

    public Task<string?> GetSongArtAsync(CoreSong song)
    {
        return song.Album is { } album ? GetAlbumArtAsync(album) : GetArtAsync("song:" + song.Id, song);
    }

    /// <summary>Returns the cached art if it was already looked up, without starting a lookup.</summary>
    public string? TryGetCachedAlbumArt(CoreAlbum album)
    {
        var first = album.Songs.FirstOrDefault();
        if (first is null)
            return null;

        var key = CacheKey(album.Id, first);
        return _lookups.TryGetValue(key, out var task) && task.IsCompletedSuccessfully ? task.Result : null;
    }

    private Task<string?> GetArtAsync(string id, CoreSong source)
    {
        var key = CacheKey(id, source);
        return _lookups.GetOrAdd(key, k => Task.Run(() => LookupAsync(k, source.FilePath)));
    }

    private static string CacheKey(string id, CoreSong source) => $"{CacheVersion}|{id}|{source.FilePath}|{source.ModifiedTicks}";

    private async Task<string?> LookupAsync(string key, string songPath)
    {
        string hash = Hash(key);
        var cached = Path.Combine(AppPaths.ArtCacheDirectory, hash + ".jpg");
        if (File.Exists(cached))
            return cached;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            string? embedded = null;
            try
            {
                embedded = await ExtractThumbnailAsync(songPath, hash).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The thumbnail handler of some formats (MP4) throws when there is no
                // embedded picture; that simply means "look further".
            }

            embedded ??= await ExtractTaggedPictureAsync(songPath, hash).ConfigureAwait(false);
            return embedded ?? FindFolderArt(songPath);
        }
        catch (Exception ex)
        {
            Log.Warning($"Art lookup failed for {songPath}: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the art embedded in the song through the Windows thumbnail cache and
    /// stores it in the cache (EmbeddedArt + StoreArt in GNOME Music).
    /// </summary>
    private static async Task<string?> ExtractThumbnailAsync(string songPath, string hash)
    {
        if (!File.Exists(songPath))
            return null;

        var file = await StorageFile.GetFileFromPathAsync(songPath);
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, ThumbnailSize, ThumbnailOptions.ResizeThumbnail);

        // A generic file icon (ThumbnailType.Icon) means there is no album art.
        if (thumbnail is null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size == 0)
            return null;

        return await SaveAsJpegAsync(thumbnail, hash);
    }

    /// <summary>Reads the picture of the song's ID3v2 tag (APIC frame) and stores it in the cache.</summary>
    private static async Task<string?> ExtractTaggedPictureAsync(string songPath, string hash)
    {
        try
        {
            var picture = TagReader.ReadFrontCover(songPath);
            if (picture is null)
                return null;

            using var stream = new MemoryStream(picture).AsRandomAccessStream();
            return await SaveAsJpegAsync(stream, hash).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot read the picture in the tags of {songPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Stores an image in the cache as a JPEG of at most <see cref="ThumbnailSize"/> pixels.</summary>
    private static async Task<string> SaveAsJpegAsync(IRandomAccessStream image, string hash)
    {
        string target = Path.Combine(AppPaths.ArtCacheDirectory, hash + ".jpg");
        string tmp = target + ".tmp";
        var decoder = await BitmapDecoder.CreateAsync(image);
        double scale = Math.Min(1.0, (double)ThumbnailSize / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        using (var output = new InMemoryRandomAccessStream())
        {
            var properties = new BitmapPropertySet
            {
                ["ImageQuality"] = new BitmapTypedValue(0.9, Windows.Foundation.PropertyType.Single),
            };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, properties);
            encoder.SetSoftwareBitmap(SoftwareBitmap.Convert(pixels, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore));
            await encoder.FlushAsync();

            output.Seek(0);
            using var fileStream = File.Create(tmp);
            await output.AsStreamForRead().CopyToAsync(fileStream).ConfigureAwait(false);
        }

        File.Move(tmp, target, overwrite: true);
        return target;
    }

    private static string? FindFolderArt(string songPath)
    {
        var dir = Path.GetDirectoryName(songPath);
        if (dir is null || !Directory.Exists(dir))
            return null;

        var found = FindArtInDirectory(dir);
        if (found is null && DiscFolderRegex().IsMatch(Path.GetFileName(dir)))
        {
            // Multi-disc albums are often stored as Album/CD1, Album/CD2.
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null)
                found = FindArtInDirectory(parent);
        }

        return found;
    }

    private static string? FindArtInDirectory(string dir)
    {
        try
        {
            var images = Directory.EnumerateFiles(dir)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (images.Count == 0)
                return null;

            foreach (var name in FolderArtNames)
            {
                var match = images.FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return match;
            }

            // Windows Media Player stores "AlbumArt_{guid}_Large.jpg".
            return images.FirstOrDefault(f =>
                Path.GetFileName(f).StartsWith("AlbumArt_", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(f).EndsWith("_Large", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Hash(string key)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [GeneratedRegex(@"^(cd|disc|disk)\s*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscFolderRegex();
}
