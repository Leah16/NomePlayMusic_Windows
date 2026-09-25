// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// A tiny, heavily blurred copy of a cover for a background (the mini player): the
/// picture scaled down to 32 × 32 and box-blurred three times each way, which comes
/// close to a Gaussian blur. Stretched over a window it becomes a soft field of the
/// cover's colors. <see cref="Blur"/> does the same to any small picture (the mini
/// player's lyrics).
/// </summary>
public static class CoverBlur
{
    private const int Size = 32;
    private const int Radius = 4;

    /// <summary>The blurred cover (BGRA, premultiplied), or null if the picture cannot be read.</summary>
    public static Task<SoftwareBitmap?> CreateAsync(string path) => Task.Run(async () =>
    {
        try
        {
            using var file = File.OpenRead(path);
            var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
            var transform = new BitmapTransform
            {
                ScaledWidth = Size,
                ScaledHeight = Size,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            return (SoftwareBitmap?)Blur(data.DetachPixelData(), Size, Size, Radius);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot blur the cover {path}: {ex.Message}");
            return null;
        }
    });

    /// <summary>
    /// Blurs a picture (BGRA, premultiplied; the pixels change) by
    /// <paramref name="radius"/> pixels, three times each way.
    /// </summary>
    public static SoftwareBitmap Blur(byte[] pixels, int width, int height, int radius)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(pixels, width, height, radius, horizontal: true);
            BoxBlur(pixels, width, height, radius, horizontal: false);
        }

        return SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>Averages each pixel with its neighbours along the rows or the columns; the edges repeat.</summary>
    private static void BoxBlur(byte[] pixels, int width, int height, int radius, bool horizontal)
    {
        int count = horizontal ? height : width;
        int length = horizontal ? width : height;
        var line = new byte[length * 4];
        for (int i = 0; i < count; i++)
        {
            for (int j = 0; j < length; j++)
                Array.Copy(pixels, Offset(i, j), line, j * 4, 4);

            for (int j = 0; j < length; j++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    int sum = 0;
                    for (int k = -radius; k <= radius; k++)
                        sum += line[Math.Clamp(j + k, 0, length - 1) * 4 + channel];
                    pixels[Offset(i, j) + channel] = (byte)(sum / (2 * radius + 1));
                }
            }
        }

        int Offset(int row, int position) => (horizontal ? row * width + position : position * width + row) * 4;
    }
}
