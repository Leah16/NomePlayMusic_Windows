// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Gnome_Music_WinUI.Helpers;
using Microsoft.Windows.System.Power;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// Keeps the system awake while music plays if the "inhibit-suspend" setting is on
/// (inhibitsuspend.py). Uses a Win32 power request, whose reason ("Playing music")
/// shows up in "powercfg /requests" like the GNOME inhibitor reason does.
/// </summary>
public sealed class InhibitSuspend : IDisposable
{
    private readonly Player _player;
    private readonly Settings _settings;
    private IntPtr _request = IntPtr.Zero;
    private bool _active;

    public InhibitSuspend(Player player, Settings settings)
    {
        _player = player;
        _settings = settings;
        _player.PropertyChanged += OnChanged;
        _settings.PropertyChanged += OnChanged;
        Update();
    }

    public void Dispose()
    {
        _player.PropertyChanged -= OnChanged;
        _settings.PropertyChanged -= OnChanged;
        SetActive(false);
        if (_request != IntPtr.Zero)
        {
            CloseHandle(_request);
            _request = IntPtr.Zero;
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Player.State) or nameof(Settings.InhibitSuspend))
            Update();
    }

    /// <summary>Held while PLAYING or LOADING with the setting on (inhibitsuspend.py).</summary>
    private void Update() =>
        SetActive(_settings.InhibitSuspend && _player.State is PlayerState.Playing or PlayerState.Loading);

    private void SetActive(bool active)
    {
        if (_active == active)
            return;

        try
        {
            if (_request == IntPtr.Zero)
            {
                var context = new REASON_CONTEXT
                {
                    Version = 0,
                    Flags = 0x1, // POWER_REQUEST_CONTEXT_SIMPLE_STRING
                    SimpleReasonString = Strings.PlayingMusic,
                };
                _request = PowerCreateRequest(ref context);
                if (_request == IntPtr.Zero || _request == new IntPtr(-1))
                {
                    _request = IntPtr.Zero;
                    return;
                }
            }

            bool ok = active
                ? PowerSetRequest(_request, PowerRequestType.SystemRequired)
                : PowerClearRequest(_request, PowerRequestType.SystemRequired);
            if (ok)
                _active = active;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning($"Power requests are unavailable: {ex.Message}");
        }
    }

    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr handle, PowerRequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr handle, PowerRequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Pauses playback when the system goes to sleep (pauseonsuspend.py), so music does
/// not blast out when the lid is opened again.
/// </summary>
public sealed class PauseOnSuspend : IDisposable
{
    private readonly Player _player;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public PauseOnSuspend(Player player, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _player = player;
        _dispatcher = dispatcher;
        try
        {
            PowerManager.SystemSuspendStatusChanged += OnSuspendStatusChanged;
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot watch the suspend status: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            PowerManager.SystemSuspendStatusChanged -= OnSuspendStatusChanged;
        }
        catch (Exception)
        {
            // Nothing to clean up.
        }
    }

    private void OnSuspendStatusChanged(object? sender, object e)
    {
        if (PowerManager.SystemSuspendStatus != SystemSuspendStatus.Entering)
            return;

        _dispatcher.TryEnqueue(() =>
        {
            if (_player.State == PlayerState.Playing)
                _player.Pause();
        });
    }
}
