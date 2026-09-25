// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Gnome_Music_WinUI.Services;
using Gnome_Music_WinUI.Services.Audio;
using Microsoft.Win32;

namespace Gnome_Music_WinUI.Helpers;

/// <summary>
/// Previous, play/pause and next under a window's thumbnail in the taskbar (not in GNOME
/// Music: the ITaskbarList3 thumbnail toolbar of Windows media players). Like other
/// players' they are always there: previous and next disabled when the queue has none,
/// play disabled with nothing to play (it starts an ended queue again, like the mini
/// player's). The glyphs are the player bar's, in the text color of the taskbar's theme,
/// drawn again when the theme or the window's scale changes. The window is subclassed
/// for the clicks (WM_COMMAND) until it is destroyed.
/// </summary>
internal sealed unsafe class TaskbarButtons : IDisposable
{
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_NCDESTROY = 0x0082;
    private const int THBN_CLICKED = 0x1800;
    private const uint THB_ICON = 0x2;
    private const uint THB_TOOLTIP = 0x4;
    private const uint THB_FLAGS = 0x8;
    private const uint THBF_DISABLED = 0x1;
    private const int SM_CXSMICON = 49;
    private const int CLSCTX_INPROC_SERVER = 1;
    private const nuint SubclassId = 0x4E50;

    private const uint PreviousId = 1;
    private const uint PlayPauseId = 2;
    private const uint NextId = 3;

    // The player bar's glyphs
    private const char PreviousGlyph = '';
    private const char PlayGlyph = '';
    private const char PauseGlyph = '';
    private const char NextGlyph = '';

    private static readonly Guid CLSID_TaskbarList = new("56FDF344-FD6D-11D0-958A-006097C9A090");
    private static readonly Guid IID_ITaskbarList3 = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

    /// <summary>Sent once the window has its taskbar button: when it first shows, shows again, or Explorer restarts.</summary>
    private static readonly uint TaskbarButtonCreated = RegisterWindowMessageW("TaskbarButtonCreated");

    private readonly IntPtr _hwnd;
    private readonly Player _player;
    private GCHandle _self;
    private IntPtr _taskbar;
    private bool _taskbarFailed;

    /// <summary>The buttons are on the window's taskbar button: from now on they are updated.</summary>
    private bool _added;

    private IntPtr _previousIcon;
    private IntPtr _playIcon;
    private IntPtr _pauseIcon;
    private IntPtr _nextIcon;

    public TaskbarButtons(IntPtr hwnd, Player player)
    {
        _hwnd = hwnd;
        _player = player;
        _self = GCHandle.Alloc(this);
        SetWindowSubclass(hwnd, &SubclassProc, SubclassId, GCHandle.ToIntPtr(_self));
        player.PropertyChanged += OnPlayerPropertyChanged;
        DrawIcons();
    }

    public void Dispose()
    {
        if (!_self.IsAllocated)
            return;

        _player.PropertyChanged -= OnPlayerPropertyChanged;
        RemoveWindowSubclass(_hwnd, &SubclassProc, SubclassId);
        _self.Free();
        NativeCom.Release(ref _taskbar);
        GlyphIcon.Destroy(ref _previousIcon);
        GlyphIcon.Destroy(ref _playIcon);
        GlyphIcon.Destroy(ref _pauseIcon);
        GlyphIcon.Destroy(ref _nextIcon);
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Player.CurrentSong) or nameof(Player.HasNext) or nameof(Player.HasPrevious) or nameof(Player.State))
            Update();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nint refData)
    {
        try
        {
            if (GCHandle.FromIntPtr(refData).Target is TaskbarButtons buttons && buttons.OnMessage(message, wParam, lParam))
                return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            // Nothing may be thrown back into the window procedure.
            Log.Warning($"Taskbar buttons: {ex.Message}");
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>True when the message was a click of a button, handled here.</summary>
    private bool OnMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_COMMAND && ((long)wParam >> 16 & 0xFFFF) == THBN_CLICKED)
        {
            switch ((uint)((long)wParam & 0xFFFF))
            {
                case PreviousId:
                    _player.Previous();
                    return true;
                case PlayPauseId:
                    _player.PlayPause();
                    return true;
                case NextId:
                    _player.Next();
                    return true;
            }
        }
        else if (message == TaskbarButtonCreated)
        {
            // A new taskbar button, without the buttons yet.
            _added = false;
            Update(created: true);
        }
        else if (message == WM_SETTINGCHANGE && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet"
            || message == WM_DPICHANGED)
        {
            DrawIcons();
        }
        else if (message == WM_NCDESTROY)
        {
            Dispose();
        }

        return false;
    }

    /// <summary>The glyphs at the window's small icon size, in TextFillColorPrimary of the taskbar's theme.</summary>
    private void DrawIcons()
    {
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForWindow(_hwnd));
        uint color = TaskbarIsLight() ? 0xE4000000 : 0xFFFFFFFF;
        var previous = _previousIcon;
        var play = _playIcon;
        var pause = _pauseIcon;
        var next = _nextIcon;
        _previousIcon = GlyphIcon.Create(PreviousGlyph, size, color);
        _playIcon = GlyphIcon.Create(PlayGlyph, size, color);
        _pauseIcon = GlyphIcon.Create(PauseGlyph, size, color);
        _nextIcon = GlyphIcon.Create(NextGlyph, size, color);

        // The old icons go once the buttons show the new ones.
        Update();
        GlyphIcon.Destroy(ref previous);
        GlyphIcon.Destroy(ref play);
        GlyphIcon.Destroy(ref pause);
        GlyphIcon.Destroy(ref next);
    }

    /// <summary>
    /// Shows the buttons as the player has them now: added to the taskbar button the
    /// first time (that fails until the window has one), then updated.
    /// </summary>
    private void Update(bool created = false)
    {
        if (!EnsureTaskbar())
            return;

        bool loaded = _player.CurrentSong is not null;
        bool playing = _player.State == PlayerState.Playing;
        var buttons = stackalloc THUMBBUTTON[3];
        Set(&buttons[0], PreviousId, _previousIcon, Strings.Previous, Enabled(loaded && _player.HasPrevious));
        Set(&buttons[1], PlayPauseId, playing ? _pauseIcon : _playIcon, playing ? Strings.Pause : Strings.Play,
            Enabled(loaded || _player.Queue.Count > 0));
        Set(&buttons[2], NextId, _nextIcon, Strings.Next, Enabled(loaded && _player.HasNext));

        // ThumbBarAddButtons once per taskbar button, then ThumbBarUpdateButtons; a taskbar
        // button that already has them takes the update.
        int hr;
        if (_added)
        {
            hr = Call(16, buttons);
        }
        else
        {
            hr = Call(15, buttons);
            if (hr < 0 && created)
                hr = Call(16, buttons);
            if (hr >= 0)
                Log.Info("Taskbar buttons added");
        }

        if (hr >= 0)
            _added = true;
        else if (created || _added)
            Log.Warning($"Cannot show the taskbar buttons: 0x{hr:X8}");
    }

    /// <summary>ITaskbarList3::ThumbBarAddButtons (15) or ThumbBarUpdateButtons (16) with the three buttons.</summary>
    private int Call(int method, THUMBBUTTON* buttons) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, THUMBBUTTON*, int>)NativeCom.Table(_taskbar)[method])(_taskbar, _hwnd, 3, buttons);

    private static uint Enabled(bool enabled) => enabled ? 0 : THBF_DISABLED;

    private static void Set(THUMBBUTTON* button, uint id, IntPtr icon, string tip, uint flags)
    {
        button->Mask = THB_ICON | THB_TOOLTIP | THB_FLAGS;
        button->Id = id;
        button->Bitmap = 0;
        button->Icon = icon;
        int length = Math.Min(tip.Length, 259);
        for (int i = 0; i < length; i++)
            button->Tip[i] = tip[i];
        button->Tip[length] = '\0';
        button->Flags = flags;
    }

    private bool EnsureTaskbar()
    {
        if (_taskbar != IntPtr.Zero)
            return true;
        if (_taskbarFailed)
            return false;

        int hr = NativeCom.CoCreateInstance(CLSID_TaskbarList, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_ITaskbarList3, out _taskbar);
        if (hr >= 0)
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, int>)NativeCom.Table(_taskbar)[3])(_taskbar);   // HrInit
        if (hr < 0)
        {
            _taskbarFailed = true;
            NativeCom.Release(ref _taskbar);
            Log.Warning($"No taskbar buttons: 0x{hr:X8}");
            return false;
        }

        return true;
    }

    /// <summary>The taskbar is light (Settings → Personalization → Colors → Windows mode), where glyphs are dark.</summary>
    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public IntPtr Icon;
        public fixed char Tip[260];
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
