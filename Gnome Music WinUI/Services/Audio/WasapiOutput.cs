// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Gnome_Music_WinUI.Services.Audio;

/// <summary>An audio output device's end of the pipeline.</summary>
internal interface IAudioOutput : IDisposable
{
    OutputFormat Format { get; }

    /// <summary>Frames that left the ring but were not heard yet.</summary>
    int LatencyFrames { get; }

    /// <summary>Where the frames come from; set before <see cref="Start"/>.</summary>
    void Attach(RenderSource source);

    /// <summary>Starts (or resumes) taking frames from the source.</summary>
    void Start();

    /// <summary>Stops taking frames; what the device holds stays.</summary>
    void Pause();

    /// <summary>Drops what the device holds. Only while paused.</summary>
    void Reset();

    /// <summary>The device failed while playing (unplugged, taken by another program).</summary>
    event Action<Exception>? Failed;
}

/// <summary>Windows Core Audio: devices, formats and what a device takes in exclusive mode.</summary>
internal static unsafe class Wasapi
{
    public const int ExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    public const int DeviceInUse = unchecked((int)0x8889000A);
    public const int DeviceInvalidated = unchecked((int)0x88890004);
    public const int ServiceNotRunning = unchecked((int)0x88890010);
    public const int EndpointCreateFailed = unchecked((int)0x8889000F);

    /// <summary>E_NOTFOUND: no default playback device (none is connected).</summary>
    public const int NotFound = unchecked((int)0x80070490);
    public const int BufferSizeNotAligned = unchecked((int)0x88890019);
    public const int UnsupportedFormat = unchecked((int)0x88890008);

    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EnumeratorInterface = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid AudioClientInterface = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid RenderClientInterface = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid PcmSubformat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubformat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey EndpointGuidKey = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 4);

    private static readonly ConcurrentDictionary<string, DeviceCapabilities> ProbeCache = new();

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PropertyKey
    {
        public readonly Guid Format;
        public readonly int Id;

        public PropertyKey(Guid format, int id)
        {
            Format = format;
            Id = id;
        }
    }

    /// <summary>The playback devices that are on, with their names.</summary>
    public static List<AudioDevice> ListDevices()
    {
        EnsureCom();
        var devices = new List<AudioDevice>();
        IntPtr enumerator = CreateEnumerator(), collection = IntPtr.Zero;
        try
        {
            NativeCom.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)NativeCom.Table(enumerator)[3])(enumerator, 0 /* render */, 1 /* active */, &collection), "EnumAudioEndpoints");
            uint count;
            NativeCom.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)NativeCom.Table(collection)[3])(collection, &count), "GetCount");
            for (uint i = 0; i < count; i++)
            {
                IntPtr device;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)NativeCom.Table(collection)[4])(collection, i, &device) < 0)
                    continue;
                try
                {
                    if (GetId(device) is { } id)
                        devices.Add(new AudioDevice(OutputApi.Wasapi, id, GetString(device, FriendlyNameKey) ?? id));
                }
                finally
                {
                    NativeCom.Release(ref device);
                }
            }
        }
        finally
        {
            NativeCom.Release(ref collection);
            NativeCom.Release(ref enumerator);
        }

        return devices;
    }

    /// <summary>The endpoint id DirectSound's device GUID belongs to (null: the default endpoint).</summary>
    public static string? EndpointOfDirectSoundDevice(Guid? directSound)
    {
        if (directSound is not { } guid)
            return null;

        EnsureCom();
        IntPtr enumerator = CreateEnumerator(), collection = IntPtr.Zero;
        try
        {
            NativeCom.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)NativeCom.Table(enumerator)[3])(enumerator, 0, 1, &collection), "EnumAudioEndpoints");
            uint count;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)NativeCom.Table(collection)[3])(collection, &count);
            for (uint i = 0; i < count; i++)
            {
                IntPtr device;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)NativeCom.Table(collection)[4])(collection, i, &device) < 0)
                    continue;
                try
                {
                    if (Guid.TryParse(GetString(device, EndpointGuidKey), out var endpoint) && endpoint == guid)
                        return GetId(device);
                }
                finally
                {
                    NativeCom.Release(ref device);
                }
            }
        }
        finally
        {
            NativeCom.Release(ref collection);
            NativeCom.Release(ref enumerator);
        }

        return null;
    }

    /// <summary>
    /// What the device takes in exclusive mode (rates, bit depths, channel counts, and
    /// the DSD rates whose DoP frames fit), and the shared mode mixer's format.
    /// </summary>
    public static DeviceCapabilities Probe(string? deviceId)
    {
        EnsureCom();
        var caps = new DeviceCapabilities();
        IntPtr device = IntPtr.Zero, client = IntPtr.Zero;
        try
        {
            device = GetDevice(deviceId);
            client = Activate(device);
            if (GetMixFormat(client) is { } mix)
            {
                caps.MixRate = mix.Rate;
                caps.MixChannels = mix.Channels;
                caps.MixBitDepth = mix.Sample is SampleFormat.Float32 or SampleFormat.Float64 ? -32 : mix.ValidBits;
            }

            int channels = Math.Max(2, Math.Min(caps.MixChannels, 2));
            var formats = new (SampleFormat Sample, int Valid, int Depth)[]
            {
                (SampleFormat.Int16, 16, 16),
                (SampleFormat.Int24, 24, 24),
                (SampleFormat.Int32, 24, 24),
                (SampleFormat.Int32, 32, 32),
                (SampleFormat.Float32, 32, -32),
            };
            var dop = new HashSet<int>();
            foreach (int rate in AudioFormats.PcmRates)
            {
                bool any = false;
                foreach (var (sample, valid, depth) in formats)
                {
                    if (!IsSupported(client, rate, channels, sample, valid))
                        continue;
                    any = true;
                    if (!caps.BitDepths.Contains(depth))
                        caps.BitDepths.Add(depth);
                    if (depth is 24 or 32)
                        dop.Add(rate);
                }

                if (any)
                    caps.PcmRates.Add(rate);
            }

            // The channel counts, at a format the device takes.
            int probeRate = caps.PcmRates.Count > 0 ? caps.PcmRates[0] : 48_000;
            var probeFormat = caps.BitDepths.Contains(16) ? (SampleFormat.Int16, 16)
                : caps.BitDepths.Contains(24) ? (SampleFormat.Int24, 24)
                : (SampleFormat.Float32, 32);
            for (int c = 1; c <= 8; c++)
            {
                if (IsSupported(client, probeRate, c, probeFormat.Item1, probeFormat.Item2)
                    || probeFormat.Item1 == SampleFormat.Int24 && IsSupported(client, probeRate, c, SampleFormat.Int32, 24))
                    caps.Channels.Add(c);
            }

            foreach (int dsd in AudioFormats.DsdRates)
            {
                if (dop.Contains(AudioFormats.DopRate(dsd)))
                    caps.DopRates.Add(dsd);
            }

            if (caps.PcmRates.Count > 0)
                ProbeCache[deviceId ?? ""] = caps;
            else if (ProbeCache.TryGetValue(deviceId ?? "", out var cached))
                return cached;   // in use by our own exclusive stream: what it took before
        }
        catch (Exception ex)
        {
            caps.Error = ex.Message;
        }
        finally
        {
            NativeCom.Release(ref client);
            NativeCom.Release(ref device);
        }

        return caps;
    }

    public static void EnsureCom() => CoInitializeEx(IntPtr.Zero, 0 /* multithreaded */);

    public static IntPtr CreateEnumerator()
    {
        NativeCom.Check(NativeCom.CoCreateInstance(EnumeratorClass, IntPtr.Zero, 1 /* in process */, EnumeratorInterface, out var enumerator), "MMDeviceEnumerator");
        return enumerator;
    }

    /// <summary>The device with the id, or the default playback device for music.</summary>
    public static IntPtr GetDevice(string? id)
    {
        IntPtr enumerator = CreateEnumerator(), device;
        try
        {
            int hr = id is null
                ? ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)NativeCom.Table(enumerator)[4])(enumerator, 0, 1 /* multimedia */, &device)
                : GetDeviceById(enumerator, id, &device);
            if (hr < 0)
                throw new AudioOutputException(id is null ? "No playback device" : "The playback device is not available", hr);
            return device;
        }
        finally
        {
            NativeCom.Release(ref enumerator);
        }
    }

    public static IntPtr Activate(IntPtr device)
    {
        IntPtr client;
        var iid = AudioClientInterface;
        NativeCom.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, IntPtr, IntPtr*, int>)NativeCom.Table(device)[3])(device, &iid, 0x17 /* all */, IntPtr.Zero, &client), "Activate");
        return client;
    }

    public static string? GetId(IntPtr device)
    {
        IntPtr text;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)NativeCom.Table(device)[5])(device, &text) < 0)
            return null;
        var id = Marshal.PtrToStringUni(text);
        NativeCom.CoTaskMemFree(text);
        return id;
    }

    public readonly record struct MixFormat(int Rate, int Channels, SampleFormat Sample, int ValidBits);

    public static MixFormat? GetMixFormat(IntPtr client)
    {
        IntPtr format;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)NativeCom.Table(client)[8])(client, &format) < 0)
            return null;
        try
        {
            return ReadFormat(format);
        }
        finally
        {
            NativeCom.CoTaskMemFree(format);
        }
    }

    public static MixFormat? ReadFormat(IntPtr format)
    {
        var b = new ReadOnlySpan<byte>((void*)format, 18);
        int tag = b[0] | b[1] << 8;
        int channels = b[2] | b[3] << 8;
        int rate = BitConverter.ToInt32(b[4..8]);
        int bits = b[14] | b[15] << 8;
        int extra = b[16] | b[17] << 8;
        int valid = bits;
        bool isFloat = tag == 3;
        if (tag == 0xFFFE && extra >= 22)
        {
            var e = new ReadOnlySpan<byte>((void*)format, 40);
            valid = e[18] | e[19] << 8;
            isFloat = new Guid(e[24..40]) == FloatSubformat;
        }

        SampleFormat? sample = (isFloat, bits) switch
        {
            (true, 32) => SampleFormat.Float32,
            (true, 64) => SampleFormat.Float64,
            (false, 16) => SampleFormat.Int16,
            (false, 24) => SampleFormat.Int24,
            (false, 32) => SampleFormat.Int32,
            _ => null,
        };
        return sample is { } s ? new MixFormat(rate, channels, s, valid) : null;
    }

    /// <summary>A WAVEFORMATEXTENSIBLE in task memory (free with CoTaskMemFree).</summary>
    public static IntPtr AllocFormat(int rate, int channels, SampleFormat sample, int validBits)
    {
        int bytes = sample.BytesPerSample();
        var format = Marshal.AllocCoTaskMem(40);
        var b = new Span<byte>((void*)format, 40);
        b.Clear();
        BitConverter.TryWriteBytes(b[0..2], (ushort)0xFFFE);
        BitConverter.TryWriteBytes(b[2..4], (ushort)channels);
        BitConverter.TryWriteBytes(b[4..8], rate);
        BitConverter.TryWriteBytes(b[8..12], rate * channels * bytes);
        BitConverter.TryWriteBytes(b[12..14], (ushort)(channels * bytes));
        BitConverter.TryWriteBytes(b[14..16], (ushort)(bytes * 8));
        BitConverter.TryWriteBytes(b[16..18], (ushort)22);
        BitConverter.TryWriteBytes(b[18..20], (ushort)validBits);
        BitConverter.TryWriteBytes(b[20..24], ChannelMask(channels));
        (sample is SampleFormat.Float32 or SampleFormat.Float64 ? FloatSubformat : PcmSubformat).TryWriteBytes(b[24..40]);
        return format;
    }

    public static int ChannelMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        3 => 0x7,
        4 => 0x33,
        5 => 0x37,
        6 => 0x3F,
        7 => 0x13F,
        8 => 0x63F,
        _ => 0,
    };

    public static bool IsSupported(IntPtr client, int rate, int channels, SampleFormat sample, int validBits)
    {
        var format = AllocFormat(rate, channels, sample, validBits);
        try
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr*, int>)NativeCom.Table(client)[7])(client, 1 /* exclusive */, format, null) == NativeCom.S_OK;
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
        }
    }

    private static int GetDeviceById(IntPtr enumerator, string id, IntPtr* device)
    {
        fixed (char* text = id)
            return ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)NativeCom.Table(enumerator)[5])(enumerator, text, device);
    }

    private static string? GetString(IntPtr device, PropertyKey key)
    {
        IntPtr store;
        if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)NativeCom.Table(device)[4])(device, 0 /* read */, &store) < 0)
            return null;
        try
        {
            using var value = new NativeCom.PropVariant();
            return ((delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, IntPtr, int>)NativeCom.Table(store)[5])(store, &key, value.Pointer) >= 0 ? value.String : null;
        }
        finally
        {
            NativeCom.Release(ref store);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int flags);
}

/// <summary>
/// A WASAPI stream, shared (through the Windows mixer, at its format) or exclusive
/// (the device alone, at the file's own format when it takes it: bit-perfect).
/// Event driven; its thread owns the audio client, and the other methods pass it
/// commands.
/// </summary>
internal sealed unsafe class WasapiOutput : IAudioOutput
{
    private readonly Thread _thread;
    private readonly AutoResetEvent _bufferEvent = new(false);
    private readonly BlockingCollection<(Action Action, ManualResetEventSlim Done)> _commands = new();
    private readonly AutoResetEvent _commandEvent = new(false);
    private readonly string? _deviceId;
    private readonly bool _exclusive;
    private readonly OutputRequest _request;
    private readonly ManualResetEventSlim _opened = new(false);
    private Exception? _openError;
    private IntPtr _client;
    private IntPtr _render;
    private int _bufferFrames;
    private int _latencyFrames;
    private volatile RenderSource? _source;
    private bool _running;
    private bool _stop;

    private WasapiOutput(string? deviceId, bool exclusive, OutputRequest request)
    {
        _deviceId = deviceId;
        _exclusive = exclusive;
        _request = request;
        _thread = new Thread(Run) { Name = "WASAPI output", IsBackground = true, Priority = ThreadPriority.Highest };
    }

    public event Action<Exception>? Failed;

    public OutputFormat Format { get; private set; } = new(StreamKind.Pcm, 48_000, 2, SampleFormat.Float32);

    public int LatencyFrames => Volatile.Read(ref _latencyFrames);

    /// <summary>Opens the device for the request; throws when it cannot be done.</summary>
    public static WasapiOutput Open(string? deviceId, bool exclusive, OutputRequest request)
    {
        var output = new WasapiOutput(deviceId, exclusive, request);
        output._thread.Start();
        output._opened.Wait();
        if (output._openError is { } error)
        {
            output._thread.Join(2000);   // after a failed open the thread ends by itself
            output.DisposeHandles();
            throw error;
        }

        return output;
    }

    /// <summary>Where the frames come from; set before <see cref="Start"/>.</summary>
    public void Attach(RenderSource source) => _source = source;

    public void Start() => Invoke(() =>
    {
        if (_running)
            return;
        FillBuffer(prefill: true);
        NativeCom.Check(Call(10), "Start");
        _running = true;
    });

    public void Pause() => Invoke(() =>
    {
        if (!_running)
            return;
        Call(11);
        _running = false;
    });

    public void Reset() => Invoke(() =>
    {
        if (!_running)
            Call(12);
    });

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            try
            {
                Invoke(() => _stop = true);
            }
            catch (AudioOutputException ex)
            {
                Log.Warning(ex.Message);
            }

            _thread.Join(2000);
        }

        DisposeHandles();
    }

    private void DisposeHandles()
    {
        _commands.Dispose();
        _bufferEvent.Dispose();
        _commandEvent.Dispose();
        _opened.Dispose();
    }

    /// <summary>Runs the action on the output's thread and waits for it (not if the thread ended).</summary>
    private void Invoke(Action action)
    {
        if (!_thread.IsAlive)
            return;
        if (Thread.CurrentThread == _thread)
        {
            action();
            return;
        }

        // Not disposed: after a time-out the thread may still set it.
        var done = new ManualResetEventSlim(false);
        Exception? error = null;
        _commands.Add((() =>
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
        _commandEvent.Set();
        long start = Environment.TickCount64;
        while (!done.Wait(50))
        {
            if (!_thread.IsAlive)
                return;
            if (Environment.TickCount64 - start > 5000)
                throw new AudioOutputException("The WASAPI output does not respond");
        }

        if (error is not null)
            throw error;
    }

    private void Run()
    {
        Wasapi.EnsureCom();
        int taskIndex = 0;
        var task = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        try
        {
            try
            {
                OpenClient();
            }
            catch (Exception ex)
            {
                _openError = ex;
                ReleaseClient();
                return;
            }
            finally
            {
                _opened.Set();
            }

            var handles = new WaitHandle[] { _commandEvent, _bufferEvent };
            while (!_stop)
            {
                int which = WaitHandle.WaitAny(handles, 1000);
                while (_commands.TryTake(out var command))
                {
                    command.Action();
                    command.Done.Set();
                }

                if (which == 1 && _running && !_stop)
                {
                    try
                    {
                        FillBuffer(prefill: false);
                    }
                    catch (Exception ex)
                    {
                        _running = false;
                        Failed?.Invoke(ex);
                    }
                }
            }
        }
        finally
        {
            ReleaseClient();
            if (task != IntPtr.Zero)
                AvRevertMmThreadCharacteristics(task);
            while (_commands.TryTake(out var command))
                command.Done.Set();
        }
    }

    private void OpenClient()
    {
        IntPtr device = Wasapi.GetDevice(_deviceId);
        try
        {
            _client = Wasapi.Activate(device);
            if (!_exclusive)
            {
                var mix = Wasapi.GetMixFormat(_client) ?? throw new AudioOutputException("The device's mixer format is not supported");
                Format = new OutputFormat(StreamKind.Pcm, mix.Rate, mix.Channels, mix.Sample);
                var format = Wasapi.AllocFormat(mix.Rate, mix.Channels, mix.Sample, mix.ValidBits);
                try
                {
                    NativeCom.Check(Initialize(0, 0x00040000 /* event callback */, 500_000, 0, format), "Initialize (shared)");
                }
                finally
                {
                    Marshal.FreeCoTaskMem(format);
                }
            }
            else
            {
                OpenExclusive(device);
            }

            uint frames;
            NativeCom.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)NativeCom.Table(_client)[4])(_client, &frames), "GetBufferSize");
            _bufferFrames = (int)frames;
            long latency;
            ((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)NativeCom.Table(_client)[5])(_client, &latency);
            _latencyFrames = _bufferFrames + (int)(latency * Format.SampleRate / 10_000_000);

            NativeCom.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)NativeCom.Table(_client)[13])(_client, _bufferEvent.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            IntPtr render;
            var iid = Wasapi.RenderClientInterface;
            NativeCom.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)NativeCom.Table(_client)[14])(_client, &iid, &render), "GetService");
            _render = render;
        }
        finally
        {
            NativeCom.Release(ref device);
        }
    }

    /// <summary>The request's format if the device takes it, else the nearest it takes.</summary>
    private void OpenExclusive(IntPtr device)
    {
        var request = _request;
        var candidates = new List<(int Rate, int Channels, SampleFormat Sample, int Valid)>();
        var samples = SampleCandidates(request);
        var rates = request.Kind == StreamKind.Pcm ? RateCandidates(request.SampleRate) : new[] { request.SampleRate };
        var channelCounts = request.Kind == StreamKind.Pcm && request.Channels != 2 ? new[] { request.Channels, 2 } : new[] { request.Channels };
        foreach (int rate in rates)
        {
            foreach (int channels in channelCounts)
            {
                foreach (var (sample, valid) in samples)
                    candidates.Add((rate, channels, sample, valid));
            }
        }

        foreach (var (rate, channels, sample, valid) in candidates)
        {
            if (!Wasapi.IsSupported(_client, rate, channels, sample, valid))
                continue;

            long period, minimum;
            ((delegate* unmanaged[Stdcall]<IntPtr, long*, long*, int>)NativeCom.Table(_client)[9])(_client, &period, &minimum);
            long duration = Math.Max(period, 200_000);   // 20 ms: room for a garbage collection
            var format = Wasapi.AllocFormat(rate, channels, sample, valid);
            try
            {
                int hr = Initialize(1, 0x00040000, duration, duration, format);
                if (hr == Wasapi.BufferSizeNotAligned)
                {
                    uint frames;
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)NativeCom.Table(_client)[4])(_client, &frames);
                    NativeCom.Release(ref _client);
                    _client = Wasapi.Activate(device);
                    duration = (long)(10_000_000.0 * frames / rate + 0.5);
                    hr = Initialize(1, 0x00040000, duration, duration, format);
                }

                if (hr == Wasapi.ExclusiveModeNotAllowed)
                    throw new AudioOutputException("Exclusive mode is not allowed for this device (Sound settings)", hr);
                if (hr == Wasapi.DeviceInUse)
                    throw new AudioOutputException("Another program is using the device in exclusive mode", hr);
                NativeCom.Check(hr, "Initialize (exclusive)");
                Format = new OutputFormat(request.Kind, rate, channels, sample);
                return;
            }
            finally
            {
                Marshal.FreeCoTaskMem(format);
            }
        }

        throw new AudioOutputException(request.Kind == StreamKind.Pcm
            ? "The device takes none of the formats tried in exclusive mode"
            : $"The device does not take {AudioFormats.RateText(request.SampleRate)} 24-bit frames for DoP");
    }

    private static List<(SampleFormat, int)> SampleCandidates(OutputRequest request)
    {
        if (request.Kind != StreamKind.Pcm)
            return new() { (SampleFormat.Int24, 24), (SampleFormat.Int32, 24), (SampleFormat.Int32, 32) };
        if (request.Float)
            return new() { (SampleFormat.Float32, 32), (SampleFormat.Int32, 32), (SampleFormat.Int32, 24), (SampleFormat.Int24, 24), (SampleFormat.Int16, 16) };
        return request.BitsPerSample switch
        {
            <= 16 => new() { (SampleFormat.Int16, 16), (SampleFormat.Int24, 24), (SampleFormat.Int32, 24), (SampleFormat.Int32, 32), (SampleFormat.Float32, 32) },
            <= 24 => new() { (SampleFormat.Int24, 24), (SampleFormat.Int32, 24), (SampleFormat.Int32, 32), (SampleFormat.Float32, 32), (SampleFormat.Int16, 16) },
            _ => new() { (SampleFormat.Int32, 32), (SampleFormat.Float32, 32), (SampleFormat.Int32, 24), (SampleFormat.Int24, 24), (SampleFormat.Int16, 16) },
        };
    }

    /// <summary>The rate itself, then its multiples and fractions, then the common rates.</summary>
    public static int[] RateCandidates(int rate)
    {
        var seen = new HashSet<int>();
        var result = new List<int>();
        foreach (int candidate in new[] { rate, rate * 2, rate * 4, rate / 2, rate / 4, 48_000, 44_100, 96_000, 88_200, 192_000, 176_400 })
        {
            if (candidate is >= 8000 and <= 768_000 && seen.Add(candidate))
                result.Add(candidate);
        }

        return result.ToArray();
    }

    /// <summary>
    /// Writes what the device has room for. An exclusive stream takes a whole buffer at
    /// each event; before starting (also after a pause) only the room left.
    /// </summary>
    private void FillBuffer(bool prefill)
    {
        var source = _source;
        int frames = _bufferFrames;
        if (!_exclusive || prefill)
        {
            uint padding;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)NativeCom.Table(_client)[6])(_client, &padding);
            if (hr < 0)
                throw new AudioOutputException("The playback device stopped", hr);
            frames = _bufferFrames - (int)padding;
            if (!_exclusive)
                Volatile.Write(ref _latencyFrames, (int)padding);
        }

        if (frames <= 0)
            return;

        byte* data;
        int result = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte**, int>)NativeCom.Table(_render)[3])(_render, (uint)frames, &data);
        if (result < 0)
            throw new AudioOutputException(result == Wasapi.DeviceInvalidated ? "The playback device was removed" : "The playback device stopped", result);

        var buffer = new Span<byte>(data, frames * Format.BytesPerFrame);
        if (source is not null)
            source.Render(buffer, frames);
        else
            buffer.Clear();
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)NativeCom.Table(_render)[4])(_render, (uint)frames, 0);
    }

    private int Initialize(int mode, int flags, long duration, long periodicity, IntPtr format) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, IntPtr, Guid*, int>)NativeCom.Table(_client)[3])(_client, mode, flags, duration, periodicity, format, null);

    private int Call(int slot) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)NativeCom.Table(_client)[slot])(_client);

    private void ReleaseClient()
    {
        if (_client != IntPtr.Zero && _running)
            Call(11);
        _running = false;
        NativeCom.Release(ref _render);
        NativeCom.Release(ref _client);
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref int index);

    [DllImport("avrt.dll")]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}

/// <summary>
/// Tells when the default playback device changes or a device goes away, so an output
/// that follows the default device moves with it (as the old MediaPlayer did).
/// </summary>
internal sealed class DeviceWatcher : IDisposable
{
    private readonly Client _client;
    private IntPtr _enumerator;
    private IntPtr _callback;

    public DeviceWatcher()
    {
        _client = new Client(this);
        try
        {
            Wasapi.EnsureCom();
            _enumerator = Wasapi.CreateEnumerator();
            _callback = Marshal.GetComInterfaceForObject<Client, IMMNotificationClient>(_client);
            Register(6);
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot watch the audio devices: {ex.Message}");
        }
    }

    /// <summary>The default playback device changed (raised on a system thread).</summary>
    public event Action? DefaultChanged;

    /// <summary>A device was removed or disabled (its id; raised on a system thread).</summary>
    public event Action<string>? DeviceLost;

    public void Dispose()
    {
        if (_enumerator == IntPtr.Zero)
            return;
        Register(7);
        Marshal.Release(_callback);
        _callback = IntPtr.Zero;
        NativeCom.Release(ref _enumerator);
    }

    private unsafe void Register(int slot) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)NativeCom.Table(_enumerator)[slot])(_enumerator, _callback);

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);

        [PreserveSig]
        int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);

        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, Wasapi.PropertyKey key);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class Client : IMMNotificationClient
    {
        private readonly DeviceWatcher _owner;

        public Client(DeviceWatcher owner) => _owner = owner;

        public int OnDeviceStateChanged(string id, int state)
        {
            if (state != 1)
                _owner.DeviceLost?.Invoke(id);
            return 0;
        }

        public int OnDeviceAdded(string id) => 0;

        public int OnDeviceRemoved(string id)
        {
            _owner.DeviceLost?.Invoke(id);
            return 0;
        }

        public int OnDefaultDeviceChanged(int flow, int role, string? id)
        {
            if (flow == 0 && role == 1)   // render, multimedia
                _owner.DefaultChanged?.Invoke();
            return 0;
        }

        public int OnPropertyValueChanged(string id, Wasapi.PropertyKey key) => 0;
    }
}
