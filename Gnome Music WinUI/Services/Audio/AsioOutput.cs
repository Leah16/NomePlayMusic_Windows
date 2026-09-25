// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Gnome_Music_WinUI.Services.Audio;

/// <summary>
/// ASIO: the sound card's own driver, loaded into the process, bypassing Windows. The
/// drivers are single threaded COM objects, so all calls go through one thread with a
/// message loop (<see cref="AsioThread"/>); the driver calls back on its own thread
/// for each buffer. Only one ASIO driver can be open at a time.
/// </summary>
internal sealed unsafe class AsioOutput : IAudioOutput
{
    private const int AseOk = 0;
    private const int AseSuccess = 0x3F4847A0;
    private const int SetIoFormat = 0x23111961;
    private const int CanDoIoFormat = 0x23112004;
    private const int PcmIoFormat = 0;
    private const int DsdIoFormat = 1;

    private static readonly ConcurrentDictionary<string, DeviceCapabilities> ProbeCache = new();
    private static volatile AsioOutput? _active;
    private static IntPtr _callbacks;

    private readonly string _name;
    private IntPtr _driver;
    private IntPtr _bufferInfos;
    private int _bufferSize;
    private int _frames;
    private int _outputChannels;
    private bool _dsdFormat;
    private bool _running;
    private bool _outputReady;
    private volatile RenderSource? _source;
    private byte[] _interleaved = Array.Empty<byte>();

    private AsioOutput(string name) => _name = name;

    public event Action<Exception>? Failed;

    public OutputFormat Format { get; private set; } = new(StreamKind.Pcm, 48_000, 2, SampleFormat.Int32);

    public int LatencyFrames { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChannelInfo
    {
        public int Channel;
        public int IsInput;
        public int IsActive;
        public int Group;
        public int Type;
        public fixed byte Name[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferInfo
    {
        public int IsInput;
        public int Channel;
        public IntPtr Buffer0;
        public IntPtr Buffer1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoFormat
    {
        public int Type;
        public fixed byte Future[508];
    }

    /// <summary>The installed ASIO drivers (by name, which is also their id).</summary>
    public static List<AudioDevice> ListDevices()
    {
        var devices = new List<AudioDevice>();
        try
        {
            using var asio = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASIO");
            if (asio is null)
                return devices;

            foreach (var name in asio.GetSubKeyNames())
            {
                using var key = asio.OpenSubKey(name);
                if (key?.GetValue("CLSID") is string clsid && Guid.TryParse(clsid, out _))
                    devices.Add(new AudioDevice(OutputApi.Asio, name, key.GetValue("Description") as string ?? name));
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot list the ASIO drivers: {ex.Message}");
        }

        return devices;
    }

    public static AsioOutput Open(string? name, OutputRequest request)
    {
        name ??= ListDevices() is { Count: > 0 } devices ? devices[0].Id : null;
        if (name is null)
            throw new AudioOutputException("No ASIO driver is installed");

        var output = new AsioOutput(name);
        AsioThread.Invoke(() => output.OpenDriver(request));
        return output;
    }

    /// <summary>What the driver takes; the open driver's answer is remembered while it plays.</summary>
    public static DeviceCapabilities Probe(string? name)
    {
        name ??= ListDevices() is { Count: > 0 } devices ? devices[0].Id : null;
        if (name is null)
            return new DeviceCapabilities { Error = "No ASIO driver is installed" };
        if (_active is { } active && active._name == name && ProbeCache.TryGetValue(name, out var cached))
            return cached;

        var caps = new DeviceCapabilities();
        try
        {
            AsioThread.Invoke(() =>
            {
                if (_active is not null)
                    throw new AudioOutputException("Another ASIO driver is playing");
                IntPtr driver = LoadDriver(name);
                try
                {
                    ProbeDriver(driver, caps);
                }
                finally
                {
                    NativeCom.Release(ref driver);
                }
            });
            ProbeCache[name] = caps;
        }
        catch (Exception ex)
        {
            caps.Error = ex.Message;
        }

        return caps;
    }

    public void Attach(RenderSource source) => _source = source;

    public void Start() => AsioThread.Invoke(() =>
    {
        if (_running || _driver == IntPtr.Zero)
            return;
        int result = Thiscall(_driver, 7);
        if (result != AseOk)
            throw new AudioOutputException("The ASIO driver does not start", result);
        _running = true;
    });

    public void Pause() => AsioThread.Invoke(() =>
    {
        if (!_running)
            return;
        Thiscall(_driver, 8);
        _running = false;
    });

    public void Reset()
    {
        // The driver's buffers hold a few milliseconds; nothing to drop.
    }

    public void Dispose()
    {
        try
        {
            AsioThread.Invoke(CloseDriver);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot close the ASIO driver: {ex.Message}");
        }
    }

    private void OpenDriver(OutputRequest request)
    {
        if (_active is not null)
            throw new AudioOutputException("Another ASIO driver is open");

        _driver = LoadDriver(_name);
        try
        {
            // What it takes, before it is set up (the settings show this while it plays).
            var caps = new DeviceCapabilities();
            ProbeDriver(_driver, caps);
            ProbeCache[_name] = caps;

            int inputs, outputs;
            Thiscall(_driver, 9, &inputs, &outputs);
            if (outputs <= 0)
                throw new AudioOutputException("The ASIO driver has no outputs");

            var kind = request.Kind;
            int rate = request.SampleRate;
            if (kind == StreamKind.Dsd)
            {
                var format = new IoFormat { Type = DsdIoFormat };
                if (Future(_driver, SetIoFormat, &format) != AseSuccess)
                    throw new AudioOutputException("The ASIO driver does not take DSD");
                _dsdFormat = true;
            }

            if (CanSampleRate(_driver, rate) != AseOk)
            {
                if (kind != StreamKind.Pcm)
                    throw new AudioOutputException($"The ASIO driver does not run at {AudioFormats.RateText(rate)}");
                rate = 0;
                foreach (int candidate in WasapiOutput.RateCandidates(request.SampleRate))
                {
                    if (CanSampleRate(_driver, candidate) == AseOk)
                    {
                        rate = candidate;
                        break;
                    }
                }

                if (rate == 0)
                    throw new AudioOutputException("The ASIO driver takes none of the rates tried");
            }

            int result = ((delegate* unmanaged[Thiscall]<IntPtr, double, int>)NativeCom.Table(_driver)[14])(_driver, rate);
            if (result != AseOk && result != AseSuccess)
                throw new AudioOutputException("The ASIO driver does not change its rate", result);

            var info = new ChannelInfo { Channel = 0, IsInput = 0 };
            Thiscall(_driver, 18, &info);
            var sample = SampleFormatOf(info.Type) ?? throw new AudioOutputException($"Unsupported ASIO sample type {info.Type}");
            if (kind == StreamKind.Pcm && sample.IsDsd() || kind == StreamKind.Dsd && !sample.IsDsd()
                || kind == StreamKind.Dop && sample is not (SampleFormat.Int24 or SampleFormat.Int32 or SampleFormat.Int32Lsb24))
                throw new AudioOutputException($"The ASIO driver's sample type ({sample}) does not suit {kind}");

            // PCM: mono goes out of the first two outputs, more channels than there are outputs are mixed down.
            _outputChannels = kind == StreamKind.Pcm ? Math.Min(Math.Max(request.Channels, 2), outputs) : request.Channels;
            if (_outputChannels > outputs)
                throw new AudioOutputException("The ASIO driver has too few outputs");
            Format = new OutputFormat(kind, rate, _outputChannels, sample);

            int min, max, preferred, granularity;
            Thiscall(_driver, 11, &min, &max, &preferred, &granularity);
            _bufferSize = preferred > 0 ? preferred : Math.Max(min, 512);
            _frames = sample.IsDsd() ? _bufferSize / 8 : _bufferSize;

            _bufferInfos = Marshal.AllocHGlobal(sizeof(BufferInfo) * _outputChannels);
            var infos = (BufferInfo*)_bufferInfos;
            for (int c = 0; c < _outputChannels; c++)
                infos[c] = new BufferInfo { IsInput = 0, Channel = c };

            EnsureCallbacks();
            _active = this;
            result = ((delegate* unmanaged[Thiscall]<IntPtr, BufferInfo*, int, int, IntPtr, int>)NativeCom.Table(_driver)[19])(_driver, infos, _outputChannels, _bufferSize, _callbacks);
            if (result != AseOk)
            {
                _active = null;
                throw new AudioOutputException("The ASIO driver cannot make its buffers", result);
            }

            int inputLatency, outputLatency;
            Thiscall(_driver, 10, &inputLatency, &outputLatency);
            LatencyFrames = (sample.IsDsd() ? outputLatency / 8 : outputLatency) + _frames;
            _outputReady = Thiscall(_driver, 23) == AseOk;
        }
        catch
        {
            CloseDriver();
            throw;
        }
    }

    private void CloseDriver()
    {
        if (_driver == IntPtr.Zero)
            return;

        if (_running)
            Thiscall(_driver, 8);
        _running = false;
        if (_active == this)
        {
            Thiscall(_driver, 20);   // disposeBuffers
            _active = null;
        }

        if (_dsdFormat)
        {
            var format = new IoFormat { Type = PcmIoFormat };
            Future(_driver, SetIoFormat, &format);
            _dsdFormat = false;
        }

        NativeCom.Release(ref _driver);
        if (_bufferInfos != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_bufferInfos);
            _bufferInfos = IntPtr.Zero;
        }
    }

    /// <summary>Fills the half of the double buffer the driver asks for.</summary>
    private void OnBufferSwitch(int index)
    {
        var source = _source;
        var infos = (BufferInfo*)_bufferInfos;
        int bytes = Format.Sample.BytesPerSample();
        int frameBytes = Format.BytesPerFrame;
        if (_interleaved.Length < _frames * frameBytes)
            _interleaved = new byte[_frames * frameBytes];

        if (source is not null)
            source.Render(_interleaved, _frames);
        else
            _interleaved.AsSpan(0, _frames * frameBytes).Fill(Format.Sample.IsDsd() ? DsdReader.Silence : (byte)0);

        for (int c = 0; c < _outputChannels; c++)
        {
            var target = (byte*)(index == 0 ? infos[c].Buffer0 : infos[c].Buffer1);
            if (target == null)
                continue;
            fixed (byte* from = _interleaved)
            {
                byte* input = from + c * bytes;
                for (int f = 0; f < _frames; f++, input += frameBytes, target += bytes)
                {
                    for (int b = 0; b < bytes; b++)
                        target[b] = input[b];
                }
            }
        }

        if (_outputReady)
            Thiscall(_driver, 23);
    }

    private static void ProbeDriver(IntPtr driver, DeviceCapabilities caps)
    {
        int inputs, outputs;
        Thiscall(driver, 9, &inputs, &outputs);
        for (int c = 1; c <= Math.Min(outputs, 8); c++)
            caps.Channels.Add(c);

        var info = new ChannelInfo { Channel = 0, IsInput = 0 };
        Thiscall(driver, 18, &info);
        var sample = SampleFormatOf(info.Type);
        int depth = sample switch
        {
            SampleFormat.Int16 or SampleFormat.Int32Lsb16 => 16,
            SampleFormat.Int24 or SampleFormat.Int32Lsb24 or SampleFormat.Int32Lsb20 or SampleFormat.Int32Lsb18 => 24,
            SampleFormat.Int32 => 32,
            SampleFormat.Float32 or SampleFormat.Float64 => -32,
            _ => 0,
        };
        if (depth != 0)
            caps.BitDepths.Add(depth);

        foreach (int rate in AudioFormats.PcmRates)
        {
            if (CanSampleRate(driver, rate) == AseOk)
                caps.PcmRates.Add(rate);
        }

        foreach (int dsd in AudioFormats.DsdRates)
        {
            if (depth is 24 or 32 && CanSampleRate(driver, AudioFormats.DopRate(dsd)) == AseOk)
                caps.DopRates.Add(dsd);
        }

        var dsdFormat = new IoFormat { Type = DsdIoFormat };
        if (Future(driver, CanDoIoFormat, &dsdFormat) == AseSuccess && Future(driver, SetIoFormat, &dsdFormat) == AseSuccess)
        {
            foreach (int dsd in AudioFormats.DsdRates)
            {
                if (CanSampleRate(driver, dsd) == AseOk)
                    caps.NativeDsdRates.Add(dsd);
            }

            var pcmFormat = new IoFormat { Type = PcmIoFormat };
            Future(driver, SetIoFormat, &pcmFormat);
        }
    }

    private static IntPtr LoadDriver(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASIO\" + name);
        if (key?.GetValue("CLSID") is not string text || !Guid.TryParse(text, out var clsid))
            throw new AudioOutputException($"The ASIO driver \"{name}\" is not installed");

        // An ASIO driver answers to its class id as interface id.
        int hr = NativeCom.CoCreateInstance(clsid, IntPtr.Zero, 1 /* in process */, clsid, out var driver);
        if (hr < 0)
            throw new AudioOutputException($"The ASIO driver \"{name}\" cannot be loaded", hr);

        if (((delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int>)NativeCom.Table(driver)[3])(driver, AsioThread.WindowHandle) != 1)
        {
            var message = new byte[128];
            fixed (byte* text2 = message)
                ((delegate* unmanaged[Thiscall]<IntPtr, byte*, void>)NativeCom.Table(driver)[6])(driver, text2);
            NativeCom.Release(ref driver);
            throw new AudioOutputException($"The ASIO driver \"{name}\" does not start: {Encoding.ASCII.GetString(message).TrimEnd('\0')}");
        }

        return driver;
    }

    private static SampleFormat? SampleFormatOf(int type) => type switch
    {
        16 => SampleFormat.Int16,
        17 => SampleFormat.Int24,
        18 => SampleFormat.Int32,
        19 => SampleFormat.Float32,
        20 => SampleFormat.Float64,
        24 => SampleFormat.Int32Lsb16,
        25 => SampleFormat.Int32Lsb18,
        26 => SampleFormat.Int32Lsb20,
        27 => SampleFormat.Int32Lsb24,
        32 => SampleFormat.DsdLsb,
        33 => SampleFormat.DsdMsb,
        _ => null,   // big endian types, and DSD one sample per byte
    };

    private static int CanSampleRate(IntPtr driver, double rate) =>
        ((delegate* unmanaged[Thiscall]<IntPtr, double, int>)NativeCom.Table(driver)[12])(driver, rate);

    private static int Future(IntPtr driver, int selector, void* argument) =>
        ((delegate* unmanaged[Thiscall]<IntPtr, int, void*, int>)NativeCom.Table(driver)[22])(driver, selector, argument);

    private static int Thiscall(IntPtr driver, int slot) =>
        ((delegate* unmanaged[Thiscall]<IntPtr, int>)NativeCom.Table(driver)[slot])(driver);

    private static int Thiscall(IntPtr driver, int slot, void* a) =>
        ((delegate* unmanaged[Thiscall]<IntPtr, void*, int>)NativeCom.Table(driver)[slot])(driver, a);

    private static int Thiscall(IntPtr driver, int slot, void* a, void* b) =>
        ((delegate* unmanaged[Thiscall]<IntPtr, void*, void*, int>)NativeCom.Table(driver)[slot])(driver, a, b);

    private static int Thiscall(IntPtr driver, int slot, void* a, void* b, void* c, void* d) =>
        ((delegate* unmanaged[Thiscall]<IntPtr, void*, void*, void*, void*, int>)NativeCom.Table(driver)[slot])(driver, a, b, c, d);

    // ------------------------------------------------------------------
    // Callbacks, called by the driver on its own thread
    // ------------------------------------------------------------------

    private static void EnsureCallbacks()
    {
        if (_callbacks != IntPtr.Zero)
            return;

        _callbacks = Marshal.AllocHGlobal(4 * IntPtr.Size);
        var table = (IntPtr*)_callbacks;
        table[0] = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, void>)&BufferSwitch;
        table[1] = (IntPtr)(delegate* unmanaged[Cdecl]<double, void>)&SampleRateChanged;
        table[2] = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, IntPtr, double*, int>)&Message;
        table[3] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr>)&BufferSwitchTimeInfo;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void BufferSwitch(int index, int processNow)
    {
        try
        {
            _active?.OnBufferSwitch(index);
        }
        catch (Exception ex)
        {
            Log.Warning($"ASIO buffer: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr BufferSwitchTimeInfo(IntPtr time, int index, int processNow)
    {
        try
        {
            _active?.OnBufferSwitch(index);
        }
        catch (Exception ex)
        {
            Log.Warning($"ASIO buffer: {ex.Message}");
        }

        return time;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void SampleRateChanged(double rate)
    {
        if (_active is { } output && Math.Abs(rate - output.Format.SampleRate) > 1)
            output.Failed?.Invoke(new AudioOutputException("The ASIO driver changed its sample rate"));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Message(int selector, int value, IntPtr message, double* option)
    {
        switch (selector)
        {
            case 1:   // selector supported
                return value is 1 or 2 or 3 or 4 or 5 or 6 ? 1 : 0;
            case 2:   // engine version
                return 2;
            case 3:   // reset request: the driver's settings changed
            case 4:   // buffer size change
                _active?.Failed?.Invoke(new AudioOutputException("The ASIO driver asks to be reset"));
                return 1;
            case 5:   // resync
            case 6:   // latencies changed
                return 1;
            default:
                return 0;   // no time info (bufferSwitch is used), no time code
        }
    }
}

/// <summary>
/// The thread ASIO drivers are created and called on: single threaded COM, with a
/// message loop for the drivers that need one (their windows, timers).
/// </summary>
internal static class AsioThread
{
    private const int WM_APP = 0x8000;

    private static readonly object Lock = new();
    private static readonly ConcurrentQueue<(Action Action, ManualResetEventSlim Done)> Work = new();
    private static Thread? _thread;
    private static int _threadId;

    /// <summary>The main window, the owner of the drivers' windows.</summary>
    public static IntPtr WindowHandle { get; set; }

    public static void Invoke(Action action)
    {
        EnsureThread();
        if (Environment.CurrentManagedThreadId == _thread!.ManagedThreadId)
        {
            action();
            return;
        }

        var done = new ManualResetEventSlim(false);
        Exception? error = null;
        Work.Enqueue((() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, done));
        PostThreadMessageW(_threadId, WM_APP, IntPtr.Zero, IntPtr.Zero);
        if (!done.Wait(10_000))
            throw new AudioOutputException("The ASIO driver does not respond");
        if (error is not null)
            throw error;
    }

    private static void EnsureThread()
    {
        lock (Lock)
        {
            if (_thread is not null)
                return;

            using var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);   // makes the message queue
                _threadId = GetCurrentThreadId();
                ready.Set();
                while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    if (message.Message == WM_APP && message.Window == IntPtr.Zero)
                    {
                        while (Work.TryDequeue(out var item))
                        {
                            item.Action();
                            item.Done.Set();
                        }

                        continue;
                    }

                    TranslateMessage(message);
                    DispatchMessageW(message);
                }
            })
            {
                Name = "ASIO",
                IsBackground = true,
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public int Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public int Time;
        public int X;
        public int Y;
        public int Private;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg message, IntPtr window, int min, int max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Msg message, IntPtr window, int min, int max, int remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(in Msg message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(int thread, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();
}
