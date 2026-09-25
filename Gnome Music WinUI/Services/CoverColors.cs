// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace Gnome_Music_WinUI.Services;

/// <summary>The color of a cover as an accent fill, for the light and the dark theme.</summary>
public readonly record struct CoverTint(Color Light, Color Dark);

/// <summary>
/// Picks the color of a cover (not in GNOME Music): for the player bar, the album
/// page's play button and the playing song in lists. It is the most prominent
/// colorful hue of the picture, or a gray when it has hardly any color. Colors are
/// compared in OKLab (https://bottosson.github.io/posts/oklab/), where distances
/// match what the eye sees, and toned like the Fluent accent fills: dark enough for
/// white glyphs in the light theme, light enough for black glyphs in the dark theme.
/// </summary>
public static class CoverColors
{
    /// <summary>
    /// Contrast of the light theme color with white. White glyphs need 4.5:1; as the
    /// text of the playing song, 5:1 with white keeps 4.5:1 on the light base (#F3F3F3).
    /// </summary>
    private const double LightContrast = 5.0;

    /// <summary>Contrast of the dark theme color with black; likewise 4.5:1 on the dark base (#202020).</summary>
    private const double DarkContrast = 6.0;

    /// <summary>Covers are scaled down to this many pixels a side before counting colors.</summary>
    private const int SampleSize = 48;

    /// <summary>Hue bins of 15°.</summary>
    private const int HueBins = 24;

    /// <summary>
    /// Below this weight per pixel a cover counts as gray: less than about 3 % of
    /// its area in a vivid color.
    /// </summary>
    private const double MinColorfulness = 0.003;

    private static readonly ConcurrentDictionary<string, Task<CoverTint?>> Tints = new();
    private static readonly double[] LinearTable = BuildLinearTable();

    /// <summary>The tint of the picture at <paramref name="path"/>; null if it cannot be read.</summary>
    public static Task<CoverTint?> GetTintAsync(string path) =>
        Tints.GetOrAdd(path, p => Task.Run(() => ReadTintAsync(p)));

    private static async Task<CoverTint?> ReadTintAsync(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
            var transform = new BitmapTransform
            {
                ScaledWidth = SampleSize,
                ScaledHeight = SampleSize,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            return FromPixels(pixels.DetachPixelData());
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot read the colors of {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The tint of a picture given as BGRA pixels; null if all of them are transparent.</summary>
    public static CoverTint? FromPixels(byte[] bgra)
    {
        var weight = new double[HueBins];
        var sumL = new double[HueBins];
        var sumA = new double[HueBins];
        var sumB = new double[HueBins];
        double totalL = 0;
        int count = 0;

        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128)
                continue;

            var (l, a, b) = ToOklab(LinearTable[bgra[i + 2]], LinearTable[bgra[i + 1]], LinearTable[bgra[i]]);
            totalL += l;
            count++;

            // A pixel counts as much as it is colorful; grays, and colors too dark or
            // too light to tell, do not count.
            double w = Math.Max(0, Math.Sqrt(a * a + b * b) - 0.02) * Ramp(l, 0.12, 0.3) * (1 - Ramp(l, 0.9, 0.98));
            if (w <= 0)
                continue;

            int bin = (int)((Math.Atan2(b, a) / (2 * Math.PI) + 1) * HueBins) % HueBins;
            weight[bin] += w;
            sumL[bin] += w * l;
            sumA[bin] += w * a;
            sumB[bin] += w * b;
        }

        if (count == 0)
            return null;

        // The most prominent hue: the heaviest run of three neighbouring bins.
        int peak = 0;
        double peakWeight = 0;
        for (int i = 0; i < HueBins; i++)
        {
            double w = weight[(i + HueBins - 1) % HueBins] + weight[i] + weight[(i + 1) % HueBins];
            if (w > peakWeight)
            {
                peakWeight = w;
                peak = i;
            }
        }

        if (peakWeight / count < MinColorfulness)
        {
            // A darker gray in the light theme, so the progress bar's value stands
            // out against its gray track.
            double gray = totalL / count;
            return new CoverTint(Tone(gray, 0, 0, 0.30, 0.42, false), Tone(gray, 0, 0, 0.78, 0.86, true));
        }

        double sl = 0, sa = 0, sb = 0;
        for (int d = -1; d <= 1; d++)
        {
            int bin = (peak + d + HueBins) % HueBins;
            sl += sumL[bin];
            sa += sumA[bin];
            sb += sumB[bin];
        }

        double lightness = sl / peakWeight;
        double chroma = Math.Sqrt(sa * sa + sb * sb) / peakWeight;
        double hue = Math.Atan2(sb, sa);
        return new CoverTint(
            Tone(lightness, Math.Min(chroma, 0.16), hue, 0.40, 0.55, false),
            Tone(lightness, Math.Min(chroma, 0.14), hue, 0.72, 0.84, true));
    }

    /// <summary>
    /// The color (OKLCh) with its lightness in the range of an accent fill, then made
    /// darker (white glyphs) or lighter (black glyphs) until it has the contrast it needs.
    /// </summary>
    private static Color Tone(double l, double c, double h, double minL, double maxL, bool blackGlyphs)
    {
        l = Math.Clamp(l, minL, maxL);
        while (true)
        {
            var color = ToColor(l, c, h);
            double y = Luminance(color);
            double contrast = blackGlyphs ? (y + 0.05) / 0.05 : 1.05 / (y + 0.05);
            if (contrast >= (blackGlyphs ? DarkContrast : LightContrast) || l <= 0 || l >= 1)
                return color;

            l += blackGlyphs ? 0.01 : -0.01;
        }
    }

    /// <summary>OKLCh to sRGB, with the chroma reduced until the color fits in sRGB.</summary>
    private static Color ToColor(double l, double c, double h)
    {
        var rgb = ToLinearRgb(l, c, h);
        if (!Fits(rgb))
        {
            double lo = 0, hi = c;
            for (int i = 0; i < 16; i++)
            {
                double mid = (lo + hi) / 2;
                if (Fits(ToLinearRgb(l, mid, h)))
                    lo = mid;
                else
                    hi = mid;
            }

            rgb = ToLinearRgb(l, lo, h);
        }

        return Color.FromArgb(255, ToByte(rgb.R), ToByte(rgb.G), ToByte(rgb.B));
    }

    private static (double L, double A, double B) ToOklab(double r, double g, double b)
    {
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static (double R, double G, double B) ToLinearRgb(double lightness, double c, double h)
    {
        double a = c * Math.Cos(h), b = c * Math.Sin(h);
        double l = Cube(lightness + 0.3963377774 * a + 0.2158037573 * b);
        double m = Cube(lightness - 0.1055613458 * a - 0.0638541728 * b);
        double s = Cube(lightness - 0.0894841775 * a - 1.2914855480 * b);
        return (
            4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);

        static double Cube(double x) => x * x * x;
    }

    private static bool Fits((double R, double G, double B) rgb) =>
        rgb.R is >= -1e-4 and <= 1 + 1e-4 && rgb.G is >= -1e-4 and <= 1 + 1e-4 && rgb.B is >= -1e-4 and <= 1 + 1e-4;

    /// <summary>WCAG relative luminance.</summary>
    private static double Luminance(Color color) =>
        0.2126 * LinearTable[color.R] + 0.7152 * LinearTable[color.G] + 0.0722 * LinearTable[color.B];

    private static double Ramp(double x, double from, double to) => Math.Clamp((x - from) / (to - from), 0, 1);

    private static byte ToByte(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        double v = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Round(v * 255);
    }

    private static double[] BuildLinearTable()
    {
        var table = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            table[i] = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return table;
    }
}
