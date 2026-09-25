// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gnome_Music_WinUI.Helpers;

/// <summary>
/// Keeps a window's client area square (the mini player, not in GNOME Music). Dragging
/// an edge sizes the other side along with it, and a corner follows the side the pointer
/// moved most (WM_SIZING); any other change of size, a snap for one, keeps the smaller
/// side (WM_WINDOWPOSCHANGING). The window is subclassed until it is destroyed.
/// </summary>
internal static unsafe class SquareWindow
{
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint WM_NCDESTROY = 0x0082;
    private const uint WM_SIZING = 0x0214;
    private const uint SWP_NOSIZE = 0x0001;
    private const nuint SubclassId = 0x5351;

    // WM_SIZING's edges
    private const int WMSZ_LEFT = 1;
    private const int WMSZ_RIGHT = 2;
    private const int WMSZ_TOP = 3;
    private const int WMSZ_TOPLEFT = 4;
    private const int WMSZ_TOPRIGHT = 5;
    private const int WMSZ_BOTTOM = 6;
    private const int WMSZ_BOTTOMLEFT = 7;

    public static void Attach(IntPtr hwnd) => SetWindowSubclass(hwnd, &SubclassProc, SubclassId, 0);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nint refData)
    {
        switch (message)
        {
            case WM_SIZING:
            {
                var result = DefSubclassProc(hwnd, message, wParam, lParam);
                OnSizing(hwnd, (int)wParam, (RECT*)lParam);
                return result;
            }

            case WM_WINDOWPOSCHANGING:
            {
                var position = (WINDOWPOS*)lParam;
                if ((position->Flags & SWP_NOSIZE) == 0 && Frame(hwnd, out int frameWidth, out int frameHeight, out _))
                {
                    int width = position->Width - frameWidth;
                    int height = position->Height - frameHeight;
                    if (width > 0 && height > 0 && width != height)
                    {
                        int size = Math.Min(width, height);
                        position->Width = size + frameWidth;
                        position->Height = size + frameHeight;
                    }
                }

                break;
            }

            case WM_NCDESTROY:
                RemoveWindowSubclass(hwnd, &SubclassProc, SubclassId);
                break;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>Squares the rectangle being dragged: the dragged edges move, the opposite ones stay.</summary>
    private static void OnSizing(IntPtr hwnd, int edge, RECT* rect)
    {
        if (!Frame(hwnd, out int frameWidth, out int frameHeight, out int current))
            return;

        int width = rect->Right - rect->Left - frameWidth;
        int height = rect->Bottom - rect->Top - frameHeight;
        int size = edge switch
        {
            WMSZ_LEFT or WMSZ_RIGHT => width,
            WMSZ_TOP or WMSZ_BOTTOM => height,
            _ => Math.Abs(width - current) >= Math.Abs(height - current) ? width : height,
        };

        // A side edge grows the window downwards, the top or bottom edge to the right; a
        // corner moves only itself.
        if (edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT)
            rect->Left = rect->Right - size - frameWidth;
        else
            rect->Right = rect->Left + size + frameWidth;

        if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT)
            rect->Top = rect->Bottom - size - frameHeight;
        else
            rect->Bottom = rect->Top + size + frameHeight;
    }

    /// <summary>How much wider and higher the window is than its client area, and the client's width now.</summary>
    private static bool Frame(IntPtr hwnd, out int width, out int height, out int client)
    {
        width = height = client = 0;
        if (!GetWindowRect(hwnd, out var window) || !GetClientRect(hwnd, out var inner))
            return false;

        width = window.Right - window.Left - inner.Right;
        height = window.Bottom - window.Top - inner.Bottom;
        client = inner.Right;
        return true;
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
    private struct WINDOWPOS
    {
        public IntPtr Hwnd;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [DllImport("comctl32.dll")]
    private static extern int SetWindowSubclass(IntPtr hwnd,
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, nuint, nint, IntPtr> proc, nuint id, nint refData);

    [DllImport("comctl32.dll")]
    private static extern int RemoveWindowSubclass(IntPtr hwnd,
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, nuint, nint, IntPtr> proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
