// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Runtime.InteropServices;

namespace Gnome_Music_WinUI.Helpers;

/// <summary>
/// Icons (HICON) drawn from the symbol font of the app's glyphs, Segoe Fluent Icons (on
/// Windows 10 Segoe MDL2 Assets), for the places that take icons rather than XAML: the
/// buttons under the taskbar thumbnail (<see cref="TaskbarButtons"/>).
/// </summary>
internal static unsafe class GlyphIcon
{
    private const uint DT_CENTER = 0x1;
    private const uint DT_VCENTER = 0x4;
    private const uint DT_SINGLELINE = 0x20;
    private const uint DT_NOCLIP = 0x100;
    private const uint DT_NOPREFIX = 0x800;
    private const uint ANTIALIASED_QUALITY = 4;
    private const int TRANSPARENT = 1;

    private static readonly string[] Fonts = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };

    /// <summary>
    /// The glyph filling a square of <paramref name="size"/> pixels, in one color (ARGB)
    /// whose alpha its antialiased edges scale. Zero if it could not be made; free it
    /// with <see cref="Destroy"/>.
    /// </summary>
    public static IntPtr Create(char glyph, int size, uint argb) => ToIcon(Render(glyph, size, argb), size);

    /// <summary>The glyph's pixels, rows from the top, as 0xAARRGGBB with straight alpha.</summary>
    public static uint[] Render(char glyph, int size, uint argb)
    {
        var pixels = new uint[size * size];
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        var header = Header(size);
        IntPtr bitmap = CreateDIBSection(dc, &header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            DeleteObject(bitmap);
            DeleteDC(dc);
            return pixels;
        }

        // White on the bitmap's black: the coverage of each pixel becomes its alpha
        // (GDI draws no alpha of its own).
        IntPtr oldBitmap = SelectObject(dc, bitmap);
        IntPtr font = CreateSymbolFont(dc, size);
        IntPtr oldFont = SelectObject(dc, font);
        SetTextColor(dc, 0x00FFFFFF);
        SetBkMode(dc, TRANSPARENT);
        var rect = new RECT { Right = size, Bottom = size };
        DrawTextW(dc, &glyph, 1, &rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP | DT_NOPREFIX);
        GdiFlush();

        uint alpha = argb >> 24;
        uint rgb = argb & 0x00FFFFFF;
        var source = (uint*)bits;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint pixel = source[i];
            uint coverage = Math.Max(pixel & 0xFF, Math.Max(pixel >> 8 & 0xFF, pixel >> 16 & 0xFF));
            uint a = coverage * alpha / 255;
            pixels[i] = a == 0 ? 0 : a << 24 | rgb;
        }

        SelectObject(dc, oldFont);
        DeleteObject(font);
        SelectObject(dc, oldBitmap);
        DeleteObject(bitmap);
        DeleteDC(dc);
        return pixels;
    }

    /// <summary>Destroys an icon made here and clears the handle.</summary>
    public static void Destroy(ref IntPtr icon)
    {
        if (icon != IntPtr.Zero)
            DestroyIcon(icon);
        icon = IntPtr.Zero;
    }

    /// <summary>An alpha icon of the pixels; its mask is all opaque, the alpha shapes it.</summary>
    private static IntPtr ToIcon(uint[] pixels, int size)
    {
        var header = Header(size);
        IntPtr color = CreateDIBSection(IntPtr.Zero, &header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero)
            return IntPtr.Zero;

        fixed (uint* source = pixels)
            Buffer.MemoryCopy(source, (void*)bits, pixels.Length * 4L, pixels.Length * 4L);

        var maskBits = new byte[(size + 15) / 16 * 2 * size];
        IntPtr mask;
        fixed (byte* m = maskBits)
            mask = CreateBitmap(size, size, 1, 1, m);

        var info = new ICONINFO { IsIcon = 1, Mask = mask, Color = color };
        IntPtr icon = CreateIconIndirect(&info);
        DeleteObject(mask);
        DeleteObject(color);
        return icon;
    }

    /// <summary>The symbol font at an em of <paramref name="size"/> pixels: Segoe Fluent Icons, else Segoe MDL2 Assets.</summary>
    private static IntPtr CreateSymbolFont(IntPtr dc, int size)
    {
        var face = stackalloc char[64];
        foreach (string name in Fonts)
        {
            IntPtr font = CreateFontW(-size, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, ANTIALIASED_QUALITY, 0, name);
            IntPtr old = SelectObject(dc, font);
            bool found = GetTextFaceW(dc, 64, face) > 0 && string.Equals(new string(face), name, StringComparison.OrdinalIgnoreCase);
            SelectObject(dc, old);
            if (found || name == Fonts[^1])
                return font;
            DeleteObject(font);
        }

        return IntPtr.Zero;
    }

    /// <summary>A 32-bit top-down bitmap.</summary>
    private static BITMAPINFOHEADER Header(int size) => new()
    {
        Size = sizeof(BITMAPINFOHEADER),
        Width = size,
        Height = -size,
        Planes = 1,
        BitCount = 32,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int IsIcon;
        public int XHotspot;
        public int YHotspot;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, BITMAPINFOHEADER* info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, void* bits);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll")]
    private static extern int GetTextFaceW(IntPtr dc, int count, char* name);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr dc, uint color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("user32.dll")]
    private static extern int DrawTextW(IntPtr dc, char* text, int count, RECT* rect, uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ICONINFO* info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
