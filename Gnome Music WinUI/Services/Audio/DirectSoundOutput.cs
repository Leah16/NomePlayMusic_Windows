// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Gnome_Music_WinUI.Services.Audio;

/// <summary>
/// DirectSound: on current Windows it plays through the Windows mixer like WASAPI's
/// shared mode, so it takes any PCM rate and the mixer converts it. Its thread owns
/// the objects and keeps a ring buffer of a quarter second ahead of the play cursor.
/// </summary>
internal sealed unsafe class DirectSoundOutput : IAudioOutput
{
    private const int DSSCL_PRIORITY = 2;
    private const int DSBCAPS_GETCURRENTPOSITION2 = 0x10000;
    private const int DSBCAPS_GLOBALFOCUS = 0x8000;
    private const int DSBPLAY_LOOPING = 1;

    private static readonly Guid DirectSound8Interface = new("C50A7E93-F395-4834-9EF6-7FA99DE50966");

    private readonly Thread _thread;
    private readonly BlockingCollection<(Action Action, ManualResetEventSlim Done)> _commands = new();
    private readonly AutoResetEvent _commandEvent = new(false);
    private readonly Guid? _device;
    private readonly OutputRequest _request;
    private readonly ManualResetEventSlim _opened = new(false);
    private Exception? _openError;
    private IntPtr _sound;
    private IntPtr _buffer;
    private int _bufferBytes;
    private int _writeOffset;
    private int _latencyFrames;
    private volatile RenderSource? _source;
    private byte[] _scratch = Array.Empty<byte>();
    private bool _fresh = true;
    private bool _running;
    private bool _stop;

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDescription
    {
        public int Size;
        public int Flags;
        public int BufferBytes;
        public int Reserved;
        public IntPtr Format;
        public Guid Algorithm3D;
    }

    private DirectSoundOutput(Guid? device, OutputRequest request)
    {
        _device = device;
        _request = request;
        _thread = new Thread(Run) { Name = "DirectSound output", IsBackground = true, Priority = ThreadPriority.Highest };
    }

    public event Action<Exception>? Failed;

    public OutputFormat Format { get; private set; } = new(StreamKind.Pcm, 48_000, 2, SampleFormat.Float32);

    public int LatencyFrames => Volatile.Read(ref _latencyFrames);

    /// <summary>The DirectSound devices; the first (a null GUID) is the default one.</summary>
    public static List<AudioDevice> ListDevices()
    {
        var devices = new List<AudioDevice>();
        var handle = GCHandle.Alloc(devices);
        try
        {
            DirectSoundEnumerateW(&OnDevice, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return devices;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int OnDevice(Guid* guid, char* description, char* module, IntPtr context)
    {
        var devices = (List<AudioDevice>)GCHandle.FromIntPtr(context).Target!;
        if (guid != null)   // the first, "Primary Sound Driver", is the default device (listed separately)
            devices.Add(new AudioDevice(OutputApi.DirectSound, guid->ToString("B"), new string(description)));
        return 1;
    }

    public static DirectSoundOutput Open(string? deviceId, OutputRequest request)
    {
        Guid? device = deviceId is not null && Guid.TryParse(deviceId, out var guid) ? guid : null;
        var output = new DirectSoundOutput(device, request);
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

    public void Attach(RenderSource source) => _source = source;

    /// <summary>Plays: a fresh buffer is filled first; after a pause it goes on where it stopped.</summary>
    public void Start() => Invoke(() =>
    {
        if (_running)
            return;
        if (_fresh)
        {
            Fill(prefill: true);
            _fresh = false;
        }

        NativeCom.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, int, int>)NativeCom.Table(_buffer)[12])(_buffer, 0, 0, DSBPLAY_LOOPING), "Play");
        _running = true;
    });

    public void Pause() => Invoke(() =>
    {
        if (!_running)
            return;
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)NativeCom.Table(_buffer)[18])(_buffer);
        _running = false;
    });

    /// <summary>Silences the buffer and starts it over from the beginning.</summary>
    public void Reset() => Invoke(() =>
    {
        if (_running)
            return;
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)NativeCom.Table(_buffer)[13])(_buffer, 0);
        WriteSilence(0, _bufferBytes);
        _writeOffset = 0;
        _fresh = true;
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
                throw new AudioOutputException("The DirectSound output does not respond");
        }

        if (error is not null)
            throw error;
    }

    private void Run()
    {
        CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            try
            {
                OpenBuffer();
            }
            catch (Exception ex)
            {
                _openError = ex;
                Release();
                return;
            }
            finally
            {
                _opened.Set();
            }

            while (!_stop)
            {
                _commandEvent.WaitOne(10);
                while (_commands.TryTake(out var command))
                {
                    command.Action();
                    command.Done.Set();
                }

                if (_running && !_stop)
                {
                    try
                    {
                        Fill(prefill: false);
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
            Release();
            while (_commands.TryTake(out var command))
                command.Done.Set();
        }
    }

    private void OpenBuffer()
    {
        if (_request.Kind != StreamKind.Pcm)
            throw new AudioOutputException("DirectSound only plays PCM");

        Guid device = _device ?? Guid.Empty;
        IntPtr sound;
        NativeCom.Check(DirectSoundCreate8(_device is null ? null : &device, &sound, IntPtr.Zero), "DirectSoundCreate8");
        _sound = sound;
        NativeCom.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)NativeCom.Table(_sound)[6])(_sound, GetDesktopWindow(), DSSCL_PRIORITY), "SetCooperativeLevel");

        // Floats first (DirectSound 8 mixes them), 16-bit integers when they are refused.
        int channels = Math.Clamp(_request.Channels, 1, 8);
        int rate = _request.SampleRate;
        foreach (var (sample, valid) in new[] { (SampleFormat.Float32, 32), (SampleFormat.Int16, 16) })
        {
            foreach (int count in channels == 2 ? new[] { 2 } : new[] { channels, 2 })
            {
                var format = Wasapi.AllocFormat(rate, count, sample, valid);
                try
                {
                    int bytesPerFrame = count * sample.BytesPerSample();
                    int bytes = rate / 4 * bytesPerFrame;   // 250 ms
                    var description = new BufferDescription
                    {
                        Size = sizeof(BufferDescription),
                        Flags = DSBCAPS_GETCURRENTPOSITION2 | DSBCAPS_GLOBALFOCUS,
                        BufferBytes = bytes,
                        Format = format,
                    };
                    IntPtr buffer;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, BufferDescription*, IntPtr*, IntPtr, int>)NativeCom.Table(_sound)[3])(_sound, &description, &buffer, IntPtr.Zero) < 0)
                        continue;

                    _buffer = buffer;
                    _bufferBytes = bytes;
                    Format = new OutputFormat(StreamKind.Pcm, rate, count, sample);
                    _latencyFrames = rate / 4;
                    WriteSilence(0, bytes);
                    return;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(format);
                }
            }
        }

        throw new AudioOutputException("DirectSound refused the stream's format");
    }

    /// <summary>Writes up to the play cursor (before starting: all but a frame of the buffer).</summary>
    private void Fill(bool prefill)
    {
        int play, write;
        NativeCom.Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int>)NativeCom.Table(_buffer)[4])(_buffer, &play, &write), "GetCurrentPosition");
        int frameBytes = Format.BytesPerFrame;
        int free = prefill ? _bufferBytes - frameBytes : (play - _writeOffset + _bufferBytes) % _bufferBytes;
        free = free / frameBytes * frameBytes;
        Volatile.Write(ref _latencyFrames, (_bufferBytes - free) / frameBytes);
        if (free < frameBytes * 64)
            return;

        int frames = free / frameBytes;
        if (_scratch.Length < free)
            _scratch = new byte[free];
        var source = _source;
        if (source is not null)
            source.Render(_scratch, frames);
        else
            Array.Clear(_scratch, 0, free);
        Write(_writeOffset, _scratch.AsSpan(0, free));
        _writeOffset = (_writeOffset + free) % _bufferBytes;
    }

    private void WriteSilence(int offset, int count)
    {
        if (_scratch.Length < count)
            _scratch = new byte[count];
        Array.Clear(_scratch, 0, count);
        Write(offset, _scratch.AsSpan(0, count));
    }

    private void Write(int offset, ReadOnlySpan<byte> data)
    {
        byte* first, second;
        int firstBytes, secondBytes;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, byte**, int*, byte**, int*, int, int>)NativeCom.Table(_buffer)[11])(
            _buffer, offset, data.Length, &first, &firstBytes, &second, &secondBytes, 0);
        if (hr == unchecked((int)0x88780096))   // DSERR_BUFFERLOST
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)NativeCom.Table(_buffer)[20])(_buffer);
            return;
        }

        NativeCom.Check(hr, "Lock");
        data[..firstBytes].CopyTo(new Span<byte>(first, firstBytes));
        if (second != null && secondBytes > 0)
            data.Slice(firstBytes, secondBytes).CopyTo(new Span<byte>(second, secondBytes));
        ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int, byte*, int, int>)NativeCom.Table(_buffer)[19])(_buffer, first, firstBytes, second, secondBytes);
    }

    private void Release()
    {
        if (_buffer != IntPtr.Zero && _running)
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)NativeCom.Table(_buffer)[18])(_buffer);
        _running = false;
        NativeCom.Release(ref _buffer);
        NativeCom.Release(ref _sound);
    }

    [DllImport("dsound.dll")]
    private static extern int DirectSoundCreate8(Guid* device, IntPtr* sound, IntPtr outer);

    [DllImport("dsound.dll")]
    private static extern int DirectSoundEnumerateW(delegate* unmanaged[Stdcall]<Guid*, char*, char*, IntPtr, int> callback, IntPtr context);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int flags);
}
