// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Gnome_Music_WinUI.Services.Audio;

internal enum PipelineEventKind
{
    /// <summary>A loaded song is ready; its duration and format are known.</summary>
    Opened,

    /// <summary>The next song is now heard (without a gap when the formats allow).</summary>
    Advanced,

    /// <summary>The last song ended and nothing was queued after it.</summary>
    EndOfStream,

    /// <summary>A song cannot be played.</summary>
    Failed,

    /// <summary>
    /// There is no playback device: the song waits, paused where it was, and plays on
    /// once there is one (not the song's fault, so it is not failed).
    /// </summary>
    NoDevice,

    State,

    /// <summary>The output opened, or fell back to the default device.</summary>
    OutputChanged,
}

internal sealed record PipelineEvent(
    PipelineEventKind Kind,
    int Generation,
    object? Tag,
    double Duration = 0,
    SourceFormat? Format = null,
    EngineState State = EngineState.Stopped,
    string? Message = null,
    OutputFormat? Output = null);

/// <summary>
/// Decodes songs on its own thread into a ring of frames for the output device's
/// thread: resampled and remapped PCM, DoP frames or DSD bytes, depending on the file,
/// the settings and what the device takes. The next song is decoded into the same ring
/// right after the current one when the device format stays the same, so they join
/// without a gap; otherwise the ring plays out and the device is opened again. Without
/// any playback device (the only one unplugged, a remote session disconnected) the song
/// is held where it was, paused: Play tries again, and a device that comes back soon
/// after the music lost it resumes it. The methods only queue commands; what happens is
/// told through <see cref="Events"/>.
/// </summary>
internal sealed class PlaybackPipeline : IDisposable
{
    private const int ChunkFrames = 4096;

    /// <summary>A device that comes back this soon after the playing music lost its own resumes it.</summary>
    internal static TimeSpan ResumeWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Opens a device's output (the test harness stands in a missing device).</summary>
    internal static Func<OutputSettings, OutputRequest, IAudioOutput> OpenDevice { get; set; } = (settings, request) => settings.Api switch
    {
        OutputApi.DirectSound => DirectSoundOutput.Open(settings.DeviceId, request),
        OutputApi.Asio => AsioOutput.Open(settings.DeviceId, request),
        _ => WasapiOutput.Open(settings.DeviceId, settings.Exclusive, request),
    };

    private readonly DspSettings _dsp;
    private readonly Thread _thread;
    private readonly object _lock = new();
    private readonly Queue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<Track> _tracks = new();
    private bool _disposed;

    private OutputSettings _settings;
    private IAudioOutput? _output;
    private OutputRequest? _request;
    private bool _fellBack;
    private FrameRing? _ring;
    private RenderSource? _source;
    private Track? _decoding;
    private Track? _next;
    private bool _playing;
    private bool _pendingSwitch;
    private bool _draining;
    private long _drainDeadline;
    private volatile Snapshot? _snapshot;

    /// <summary>The song waiting for a playback device; null while there is one (or no song).</summary>
    private volatile Held? _held;

    private float[] _decodeBuffer = new float[ChunkFrames * 2];
    private float[] _mapBuffer = new float[ChunkFrames * 2];
    private float[] _resampleBuffer = new float[ChunkFrames * 2];
    private byte[] _dsdBuffer = new byte[ChunkFrames * 2 * 2];
    private byte[] _rawBuffer = new byte[ChunkFrames * 2 * 2];

    private sealed record Snapshot(object Tag, FrameRing Ring, IAudioOutput Output, long SegmentStart, double StartSeconds, double FrameRate, double Duration);

    /// <summary>
    /// A song held without a playback device: where it waits, whether it was playing when
    /// the device went (and since when), and why it could not go on.
    /// </summary>
    private sealed record Held(int Generation, string Path, object Tag, double Seconds, double Duration, bool WasPlaying, long Since, string Reason);

    private enum StartResult
    {
        Started,

        /// <summary>No playback device: the song is held (<see cref="_held"/>).</summary>
        Held,

        /// <summary>The song cannot be played (told already).</summary>
        Failed,
    }

    /// <summary>A song being decoded (or about to be).</summary>
    private sealed class Track : IDisposable
    {
        public Track(int generation, string path, object tag)
        {
            Generation = generation;
            Path = path;
            Tag = tag;
        }

        public int Generation { get; }

        public string Path { get; }

        public object Tag { get; }

        public StreamKind Kind { get; set; }

        public IPcmDecoder? Pcm { get; set; }

        public DsdReader? Dsd { get; set; }

        public SourceFormat Format => Pcm?.Format ?? Dsd!.Format;

        public double Duration => Pcm?.Duration ?? Dsd!.Info.Duration;

        public Resampler? Resampler { get; set; }

        public int OutChannels { get; set; }

        public long SegmentStart { get; set; }

        public double StartSeconds { get; set; }

        public bool Ended { get; set; }

        public void Dispose()
        {
            Pcm?.Dispose();
            Dsd?.Dispose();
            Pcm = null;
            Dsd = null;
        }
    }

    public PlaybackPipeline(OutputSettings settings, DspSettings dsp)
    {
        _settings = settings;
        _dsp = dsp;
        _thread = new Thread(Run) { Name = "Audio decoding", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Raised on the pipeline's thread.</summary>
    public event Action<PipelineEvent>? Events;

    /// <summary>The output format in use, if any.</summary>
    public OutputFormat? OutputFormat => _snapshot?.Output.Format;

    /// <summary>The position in the song being heard, in seconds (any thread).</summary>
    public double Position
    {
        get
        {
            var s = _snapshot;
            if (s is null)
                return _held?.Seconds ?? 0;

            long played = s.Ring.TotalRead - s.Output.LatencyFrames;
            double position = s.StartSeconds + Math.Max(0, played - s.SegmentStart) / s.FrameRate;
            return s.Duration > 0 ? Math.Min(position, s.Duration) : position;
        }
    }

    public void Load(int generation, string path, object tag, bool play) => Post(() => DoLoad(generation, path, tag, play));

    public void SetNext(int generation, string? path, object? tag) => Post(() => DoSetNext(generation, path, tag));

    public void Play() => Post(() =>
    {
        _playing = true;
        if (_held is { } held)
        {
            Resume(held, automatic: false);
            return;
        }

        if (_output is null || _tracks.Count == 0)
            return;
        TryOutput(() => _output.Start());
        _source!.ExpectData = true;
        RaiseState(EngineState.Playing);
    });

    public void Pause() => Post(() =>
    {
        _playing = false;
        if (_held is { } held)
        {
            // Paused by the user: it stays so when a device comes back.
            _held = held with { WasPlaying = false };
            return;
        }

        if (_output is null || _tracks.Count == 0)
            return;
        _source!.ExpectData = false;
        TryOutput(() => _output.Pause());
        RaiseState(EngineState.Paused);
    });

    public void Seek(double seconds) => Post(() => DoSeek(seconds));

    public void Stop() => Post(DoStop);

    /// <summary>
    /// New output settings: the song being heard goes on through the new output. The DSD
    /// settings only matter to DSD files: the one playing, or the next one.
    /// </summary>
    public void Configure(OutputSettings settings) => Post(() =>
    {
        var old = _settings;
        _settings = settings;
        if (settings == old && !_fellBack)
            return;

        bool output = settings.Api != old.Api || settings.DeviceId != old.DeviceId || settings.Exclusive != old.Exclusive || _fellBack;
        if (output && settings != old)
            _fellBack = false;   // a new choice: its failure is told again
        if (output || _tracks.Count > 0 && DsdFileInfo.IsDsdPath(_tracks[0].Path))
        {
            Reopen("settings changed");
        }
        else if (_next is { } next && DsdFileInfo.IsDsdPath(next.Path))
        {
            next.Dispose();
            _next = null;
            DoSetNext(next.Generation, next.Path, next.Tag);
        }
    });

    /// <summary>
    /// The default device changed: an output following it moves along. A device that
    /// comes back soon after the playing music lost its own (a driver restarting, a
    /// remote session reconnecting at once) plays the held song on; later, it waits for
    /// Play, so music does not start by itself long after it stopped.
    /// </summary>
    public void DefaultDeviceChanged() => Post(() =>
    {
        if (_held is { WasPlaying: true } held && Environment.TickCount64 - held.Since < ResumeWindow.TotalMilliseconds)
        {
            _playing = true;
            Resume(held, automatic: true);
            return;
        }

        if (_settings.DeviceId is null && _settings.Api != OutputApi.Asio && _output is not null)
            Reopen("default device changed");
    });

    public void Dispose()
    {
        Post(() => _disposed = true);
        _thread.Join(3000);
    }

    private void Post(Action command)
    {
        lock (_lock)
            _commands.Enqueue(command);
        _wake.Set();
    }

    // ------------------------------------------------------------------
    // The thread
    // ------------------------------------------------------------------

    private void Run()
    {
        Wasapi.EnsureCom();
        try
        {
            while (!_disposed)
            {
                while (true)
                {
                    Action? command;
                    lock (_lock)
                        command = _commands.Count > 0 ? _commands.Dequeue() : null;
                    if (command is null)
                        break;
                    try
                    {
                        command();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Audio pipeline command failed", ex);
                    }

                    if (_disposed)
                        return;
                }

                bool busy = false;
                try
                {
                    busy = Decode();
                    CheckProgress();
                }
                catch (Exception ex)
                {
                    Log.Error("Audio pipeline failed", ex);
                    FailPlaying(ex.Message);
                }

                // Idle, it only wakes for commands; paused, now and then.
                _wake.WaitOne(busy ? 2 : _tracks.Count == 0 ? Timeout.Infinite : _playing ? 15 : 100);
            }
        }
        finally
        {
            DisposeTracks();
            CloseOutput();
        }
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    private void DoLoad(int generation, string path, object tag, bool play)
    {
        DisposeTracks();
        _held = null;
        _playing = play;
        _pendingSwitch = _draining = false;
        RaiseState(EngineState.Loading, generation, tag);

        Track track;
        try
        {
            track = OpenTrack(generation, path, tag);
        }
        catch (Exception ex)
        {
            Fail(generation, tag, ex.Message);
            return;
        }

        // A song held for a device is opened all the same: it is fine.
        double duration = track.Duration;
        var format = track.Format;
        var start = StartTrack(track, seconds: 0);
        if (start == StartResult.Failed)
            return;
        if (start == StartResult.Started)
        {
            duration = track.Duration;   // DSD may have turned into PCM
            format = track.Format;
        }

        Raise(new PipelineEvent(PipelineEventKind.Opened, generation, tag, duration, format));
        if (start == StartResult.Held)
        {
            AnnounceHeld();
            return;
        }

        if (_playing)
            TryOutput(() => _output!.Start());
        RaiseState(_playing ? EngineState.Playing : EngineState.Paused, generation, tag);
    }

    private void DoSetNext(int generation, string? path, object? tag)
    {
        _next?.Dispose();
        _next = null;
        if (path is not null && tag is not null)
        {
            try
            {
                _next = OpenTrack(generation, path, tag);
            }
            catch (Exception ex)
            {
                // It fails again (and is skipped) when it is reached.
                Log.Warning($"Cannot prepare {path}: {ex.Message}");
            }
        }

        // The current song already ended: the new next one may still join it.
        if (_decoding is { Ended: true } && (_draining || _pendingSwitch))
        {
            _draining = _pendingSwitch = false;
            AfterTrackEnded();
        }
    }

    private void DoSeek(double seconds)
    {
        if (_held is { } held)
        {
            _held = held with { Seconds = Math.Clamp(seconds, 0, Math.Max(0, held.Duration)) };
            return;
        }

        if (_tracks.Count == 0 || _output is null || _ring is null)
            return;

        var track = _tracks[0];
        // A next song already decoding goes back to waiting.
        for (int i = _tracks.Count - 1; i >= 1; i--)
        {
            var later = _tracks[i];
            _tracks.RemoveAt(i);
            if (_next is null)
            {
                later.Pcm?.Seek(0);
                later.Dsd?.Seek(0);
                later.Resampler = null;
                later.Ended = false;
                _next = later;
            }
            else
            {
                later.Dispose();
            }
        }

        _pendingSwitch = _draining = false;
        _decoding = track;
        track.Ended = false;
        TryOutput(() => _output.Pause());
        _ring.Clear();
        TryOutput(() => _output.Reset());

        seconds = Math.Clamp(seconds, 0, Math.Max(0, track.Duration));
        track.Pcm?.Seek(seconds);
        track.Dsd?.Seek(seconds);
        track.Resampler?.Reset();
        track.SegmentStart = _ring.TotalWritten;
        track.StartSeconds = seconds;
        Publish();
        Decode();
        if (_playing)
            TryOutput(() => _output.Start());
    }

    private void DoStop()
    {
        DisposeTracks();
        _held = null;
        _playing = false;
        CloseOutput();
        RaiseState(EngineState.Stopped);
    }

    /// <summary>Opens the output again for the song being heard and goes on where it was.</summary>
    private void Reopen(string reason)
    {
        if (_tracks.Count == 0)
        {
            CloseOutput();
            return;
        }

        var track = _tracks[0];
        double position = Position;
        Log.Info($"Audio output: reopening ({reason}) at {position:0.0} s");
        for (int i = _tracks.Count - 1; i >= 1; i--)
        {
            _tracks[i].Dispose();
            _tracks.RemoveAt(i);
        }

        _pendingSwitch = _draining = false;
        CloseOutput();

        // The file may go out another way now (DSD: PCM, DoP or native; the gain).
        track.Dispose();
        Track reopened;
        try
        {
            reopened = OpenTrack(track.Generation, track.Path, track.Tag);
        }
        catch (Exception ex)
        {
            _tracks.Clear();
            Fail(track.Generation, track.Tag, ex.Message);
            return;
        }

        _tracks.Clear();
        var start = StartTrack(reopened, position);
        if (start == StartResult.Held)
            AnnounceHeld();
        else if (start == StartResult.Started && _playing)
            TryOutput(() => _output!.Start());
    }

    /// <summary>
    /// Plays the held song on from where it waits, if there is a device now. Else it goes
    /// on waiting: told again when the user pressed Play, quietly (and as long as it
    /// began) after a device came and went.
    /// </summary>
    private void Resume(Held held, bool automatic)
    {
        _held = null;
        RaiseState(EngineState.Loading, held.Generation, held.Tag);
        Track track;
        try
        {
            track = OpenTrack(held.Generation, held.Path, held.Tag);
        }
        catch (Exception ex)
        {
            Fail(held.Generation, held.Tag, ex.Message);
            return;
        }

        switch (StartTrack(track, held.Seconds))
        {
            case StartResult.Held when automatic:
                _held = _held! with { WasPlaying = true, Since = held.Since };
                RaiseState(EngineState.Paused, held.Generation, held.Tag);
                break;
            case StartResult.Held:
                AnnounceHeld();
                break;
            case StartResult.Started:
                if (_playing)
                    TryOutput(() => _output!.Start());
                RaiseState(_playing ? EngineState.Playing : EngineState.Paused, held.Generation, held.Tag);
                break;
        }
    }

    // ------------------------------------------------------------------
    // Tracks and the output
    // ------------------------------------------------------------------

    /// <summary>Opens a file for decoding, as the settings want it to go out.</summary>
    private Track OpenTrack(int generation, string path, object tag)
    {
        var track = new Track(generation, path, tag);
        if (DsdFileInfo.IsDsdPath(path))
        {
            bool bitstream = _settings.DsdMode switch
            {
                DsdMode.Native => _settings.Api == OutputApi.Asio,
                DsdMode.Dop => _settings.Api == OutputApi.Asio || _settings.Api == OutputApi.Wasapi && _settings.Exclusive,
                _ => false,
            };
            if (bitstream)
            {
                track.Dsd = new DsdReader(path);
                track.Kind = _settings.DsdMode == DsdMode.Native ? StreamKind.Dsd : StreamKind.Dop;
                return track;
            }

            track.Pcm = new DsdToPcmDecoder(path, _settings.DsdGainDb);
        }
        else
        {
            track.Pcm = new MediaFoundationDecoder(path);
        }

        track.Kind = StreamKind.Pcm;
        return track;
    }

    private static OutputRequest RequestFor(Track track)
    {
        if (track.Kind == StreamKind.Pcm)
        {
            var format = track.Pcm!.Format;
            int bits = format.Lossless && !format.IsDsd ? format.BitsPerSample : 24;
            return new OutputRequest(StreamKind.Pcm, track.Pcm.SampleRate, track.Pcm.Channels, bits, format.Float);
        }

        int rate = track.Dsd!.SampleRate;
        return track.Kind == StreamKind.Dop
            ? new OutputRequest(StreamKind.Dop, AudioFormats.DopRate(rate), track.Dsd.Channels, 24, false)
            : new OutputRequest(StreamKind.Dsd, rate, track.Dsd.Channels, 1, false);
    }

    /// <summary>
    /// Makes the track the one heard, from <paramref name="seconds"/>: opens an output
    /// that suits it (or keeps the open one), fills the ring. Without any playback device
    /// the track is held instead, for the caller to tell (<see cref="AnnounceHeld"/>).
    /// </summary>
    private StartResult StartTrack(Track track, double seconds)
    {
        try
        {
            EnsureOutput(track);
        }
        catch (Exception ex) when (IsNoDevice(ex))
        {
            Hold(track, seconds, ex.Message);
            return StartResult.Held;
        }
        catch (Exception ex)
        {
            track.Dispose();
            Fail(track.Generation, track.Tag, ex.Message);
            return StartResult.Failed;
        }

        TryOutput(() => _output!.Pause());
        _ring!.Clear();
        TryOutput(() => _output!.Reset());
        if (seconds > 0)
        {
            track.Pcm?.Seek(seconds);
            track.Dsd?.Seek(seconds);
        }

        PrepareChain(track, reuse: null);
        track.SegmentStart = _ring.TotalWritten;
        track.StartSeconds = seconds;
        _tracks.Add(track);
        _decoding = track;
        _source!.ExpectData = _playing;
        Publish();
        Decode();
        return StartResult.Started;
    }

    /// <summary>
    /// No playback device at all: none is connected, or the one there went away while it
    /// was being opened. The last output tried is always the default device (the
    /// fallback), so this is not about the chosen one.
    /// </summary>
    private static bool IsNoDevice(Exception ex) => ex is AudioOutputException
    {
        HResult: Wasapi.NotFound or Wasapi.DeviceInvalidated or Wasapi.ServiceNotRunning or Wasapi.EndpointCreateFailed,
    };

    /// <summary>Keeps the track's place until there is a device, paused; nothing stays open.</summary>
    private void Hold(Track track, double seconds, string reason)
    {
        double duration = track.Duration;
        track.Dispose();
        _decoding = null;
        _pendingSwitch = _draining = false;
        CloseOutput();
        _held = new Held(track.Generation, track.Path, track.Tag, seconds, duration, _playing, Environment.TickCount64, reason);
        _playing = false;
    }

    private void AnnounceHeld()
    {
        var held = _held!;
        Log.Warning($"{held.Tag} waits at {held.Seconds:0.0} s for a playback device: {held.Reason}");
        RaiseState(EngineState.Paused, held.Generation, held.Tag);
        Raise(new PipelineEvent(PipelineEventKind.NoDevice, held.Generation, held.Tag, Message: held.Reason));
    }

    /// <summary>
    /// An output for the track's request: the open one when it serves (a shared stream
    /// serves every PCM request). A DSD stream the output refuses is converted to PCM
    /// instead; an output that cannot be opened gives way to the default device.
    /// </summary>
    private void EnsureOutput(Track track)
    {
        var request = RequestFor(track);
        if (_output is not null && _request is not null && Serves(_request, request))
            return;

        CloseOutput();
        try
        {
            OpenOutput(_settings, request);
            _fellBack = false;
            return;
        }
        catch (Exception ex) when (track.Kind != StreamKind.Pcm)
        {
            Log.Warning($"{track.Kind} is not possible ({ex.Message}); converting DSD to PCM");
            track.Dispose();
            track.Pcm = new DsdToPcmDecoder(track.Path, _settings.DsdGainDb);
            track.Kind = StreamKind.Pcm;
            request = RequestFor(track);
        }
        catch (Exception ex)
        {
            Fallback(ex, request);
            return;
        }

        try
        {
            OpenOutput(_settings, request);
            _fellBack = false;
        }
        catch (Exception ex)
        {
            Fallback(ex, request);
        }
    }

    private void Fallback(Exception reason, OutputRequest request)
    {
        var shared = _settings with { Api = OutputApi.Wasapi, DeviceId = null, Exclusive = false };
        if (shared == _settings)
            throw reason;

        // Told once; later songs try the chosen output again, quietly.
        bool first = !_fellBack;
        Log.Warning($"Cannot open the output ({reason.Message}); using the default device");
        OpenOutput(shared, request);
        _fellBack = true;
        if (first)
            Raise(new PipelineEvent(PipelineEventKind.OutputChanged, 0, null, Message: reason.Message, Output: _output!.Format));
    }

    private bool Serves(OutputRequest open, OutputRequest wanted)
    {
        bool shared = _settings.Api == OutputApi.Wasapi && !_settings.Exclusive || _fellBack;
        return shared ? wanted.Kind == StreamKind.Pcm : open == wanted;
    }

    private void OpenOutput(OutputSettings settings, OutputRequest request)
    {
        var output = OpenDevice(settings, request);
        var format = output.Format;
        int frameBytes = format.Kind switch
        {
            StreamKind.Pcm => 4 * format.Channels,
            StreamKind.Dop => 2 * format.Channels,
            _ => format.Channels,
        };
        int capacity = format.Kind == StreamKind.Dsd ? format.SampleRate / 16 : Math.Max(8192, format.SampleRate / 2);
        _ring = new FrameRing(capacity, frameBytes);
        _source = new RenderSource(_ring, format, _dsp);
        output.Attach(_source);
        output.Failed += ex => Post(() =>
        {
            if (ReferenceEquals(output, _output))
            {
                Log.Warning($"Audio output failed: {ex.Message}");
                Reopen("output failed");
            }
        });
        _output = output;
        _request = request;
        Log.Info($"Audio output: {settings.Api}{(settings.Exclusive && settings.Api == OutputApi.Wasapi ? " exclusive" : "")} {format}");
        Raise(new PipelineEvent(PipelineEventKind.OutputChanged, 0, null, Output: format));
    }

    private void CloseOutput()
    {
        var output = _output;
        _output = null;
        _request = null;
        _snapshot = null;
        if (output is null)
            return;
        try
        {
            output.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot close the audio output: {ex.Message}");
        }
    }

    /// <summary>Sets the track up for the output: channels and rate (or a resampler carried over).</summary>
    private void PrepareChain(Track track, Resampler? reuse)
    {
        var format = _output!.Format;
        if (track.Kind != StreamKind.Pcm)
        {
            track.OutChannels = format.Channels;
            return;
        }

        track.OutChannels = format.Channels;
        int rate = track.Pcm!.SampleRate;
        if (rate == format.SampleRate)
            track.Resampler = null;
        else if (reuse is not null && reuse.InRate == rate && reuse.OutRate == format.SampleRate && reuse.Channels == format.Channels)
            track.Resampler = reuse;
        else
            track.Resampler = new Resampler(format.Channels, rate, format.SampleRate);
    }

    private void DisposeTracks()
    {
        foreach (var track in _tracks)
            track.Dispose();
        _tracks.Clear();
        _next?.Dispose();
        _next = null;
        _decoding = null;
        _snapshot = null;
    }

    // ------------------------------------------------------------------
    // Decoding
    // ------------------------------------------------------------------

    /// <summary>Fills the ring from the song being decoded; true if there is more to do soon.</summary>
    private bool Decode()
    {
        var track = _decoding;
        var ring = _ring;
        if (track is null || ring is null || _output is null || track.Ended)
            return false;

        int rounds = 0;
        while (!track.Ended && rounds++ < 8)
        {
            bool wrote = track.Kind switch
            {
                StreamKind.Pcm => DecodePcm(track, ring),
                StreamKind.Dop => DecodeDop(track, ring),
                _ => DecodeDsd(track, ring),
            };
            if (!wrote)
                return false;
        }

        if (track.Ended)
            AfterTrackEnded();
        return !track.Ended && ring.Free > ring.Capacity / 4;
    }

    private bool DecodePcm(Track track, FrameRing ring)
    {
        var decoder = track.Pcm!;
        int inChannels = decoder.Channels;
        int outChannels = track.OutChannels;
        var resampler = track.Resampler;
        int needed = resampler?.MaxOutputFrames(ChunkFrames) ?? ChunkFrames;
        if (ring.Free < needed + 64)
            return false;

        Grow(ref _decodeBuffer, ChunkFrames * inChannels);
        int read;
        try
        {
            read = decoder.Read(_decodeBuffer.AsSpan(0, ChunkFrames * inChannels));
        }
        catch (Exception ex)
        {
            Log.Warning($"Decoding stopped early: {ex.Message}");
            read = 0;
        }

        int frames = read / inChannels;
        if (frames == 0)
        {
            track.Ended = true;
            return true;
        }

        Span<float> mapped = _decodeBuffer.AsSpan(0, frames * inChannels);
        if (inChannels != outChannels)
        {
            Grow(ref _mapBuffer, frames * outChannels);
            ChannelMapper.Map(mapped, inChannels, _mapBuffer, outChannels, frames);
            mapped = _mapBuffer.AsSpan(0, frames * outChannels);
        }

        if (resampler is not null)
        {
            Grow(ref _resampleBuffer, resampler.MaxOutputFrames(frames) * outChannels);
            int count = resampler.Process(mapped, _resampleBuffer);
            mapped = _resampleBuffer.AsSpan(0, count);
        }

        ring.Write(MemoryMarshal.AsBytes(mapped));
        return true;
    }

    private bool DecodeDop(Track track, FrameRing ring)
    {
        int channels = track.OutChannels;
        if (ring.Free < ChunkFrames)
            return false;

        // Two DSD bytes of each channel make a DoP frame.
        Grow(ref _dsdBuffer, ChunkFrames * 2 * channels);
        int read = track.Dsd!.Read(_dsdBuffer.AsSpan(0, ChunkFrames * 2 * channels));
        if (read == 0)
        {
            track.Ended = true;
            return true;
        }

        if ((read & 1) != 0)
            _dsdBuffer.AsSpan(read * channels, channels).Fill(DsdReader.Silence);
        int frames = (read + 1) / 2;
        Grow(ref _rawBuffer, frames * 2 * channels);
        bool invert = _dsp.Invert, swap = _dsp.Swap && channels >= 2;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                int from = swap && c < 2 ? 1 - c : c;
                byte first = _dsdBuffer[(2 * f) * channels + from];
                byte second = _dsdBuffer[(2 * f + 1) * channels + from];
                if (invert)
                {
                    first ^= 0xFF;
                    second ^= 0xFF;
                }

                _rawBuffer[(f * channels + c) * 2] = first;
                _rawBuffer[(f * channels + c) * 2 + 1] = second;
            }
        }

        ring.Write(_rawBuffer.AsSpan(0, frames * 2 * channels));
        return true;
    }

    private bool DecodeDsd(Track track, FrameRing ring)
    {
        int channels = track.OutChannels;
        if (ring.Free < ChunkFrames * 2)
            return false;

        Grow(ref _rawBuffer, ChunkFrames * 2 * channels);
        var buffer = _rawBuffer.AsSpan(0, ChunkFrames * 2 * channels);
        int read = track.Dsd!.Read(buffer);
        if (read == 0)
        {
            track.Ended = true;
            return true;
        }

        bool invert = _dsp.Invert, swap = _dsp.Swap && channels >= 2;
        if (invert || swap)
        {
            for (int f = 0; f < read; f++)
            {
                int i = f * channels;
                if (swap)
                    (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
                if (invert)
                {
                    for (int c = 0; c < channels; c++)
                        buffer[i + c] ^= 0xFF;
                }
            }
        }

        ring.Write(buffer[..(read * channels)]);
        return true;
    }

    /// <summary>
    /// The song being decoded ended: the next one follows in the same stream when the
    /// output serves it; otherwise the ring plays out first (to switch the output, or
    /// to end).
    /// </summary>
    private void AfterTrackEnded()
    {
        var track = _decoding!;
        var next = _next;
        bool joins = next is not null && _request is not null && Serves(_request, RequestFor(next));
        var carried = joins && track.Resampler is { } r && next!.Kind == StreamKind.Pcm && r.InRate == next.Pcm!.SampleRate ? track.Resampler : null;
        if (carried is null)
            FlushResampler(track);

        if (joins)
        {
            _next = null;
            PrepareChain(next!, carried);
            next!.SegmentStart = _ring!.TotalWritten;
            next.StartSeconds = 0;
            _tracks.Add(next);
            _decoding = next;
            return;
        }

        _drainDeadline = 0;
        if (next is not null)
            _pendingSwitch = true;
        else
            _draining = true;
    }

    private void FlushResampler(Track track)
    {
        if (track.Resampler is not { } resampler || _ring is null)
            return;

        Grow(ref _resampleBuffer, resampler.MaxOutputFrames(resampler.InRate) * resampler.Channels);
        int count = resampler.Flush(_resampleBuffer);
        _ring.Write(MemoryMarshal.AsBytes(_resampleBuffer.AsSpan(0, count)));
    }

    /// <summary>Moves on to the next song when it is heard, switches the output or ends when the ring played out.</summary>
    private void CheckProgress()
    {
        var ring = _ring;
        var output = _output;
        if (ring is null || output is null || _tracks.Count == 0)
            return;

        long played = ring.TotalRead - output.LatencyFrames;
        while (_tracks.Count > 1 && played >= _tracks[1].SegmentStart)
        {
            _tracks[0].Dispose();
            _tracks.RemoveAt(0);
            var now = _tracks[0];
            Publish();
            Raise(new PipelineEvent(PipelineEventKind.Advanced, now.Generation, now.Tag, now.Duration, now.Format));
        }

        if (!_pendingSwitch && !_draining || ring.Available > 0 || !_playing)
            return;

        // The ring is empty; the device still holds its latency's worth.
        long nowTicks = Environment.TickCount64;
        if (_drainDeadline == 0)
        {
            _drainDeadline = nowTicks + (long)(1000.0 * output.LatencyFrames / FrameRate(output.Format)) + 20;
            _source!.ExpectData = false;
            return;
        }

        if (nowTicks < _drainDeadline)
            return;

        _drainDeadline = 0;
        if (_pendingSwitch)
        {
            _pendingSwitch = false;
            var next = _next!;
            _next = null;
            foreach (var track in _tracks)
                track.Dispose();
            _tracks.Clear();

            // The player moves on first, so a failure is the new song's (and it is skipped).
            Raise(new PipelineEvent(PipelineEventKind.Advanced, next.Generation, next.Tag, next.Duration, next.Format));
            var start = StartTrack(next, seconds: 0);
            if (start == StartResult.Held)
                AnnounceHeld();
            else if (start == StartResult.Started && _playing)
                TryOutput(() => _output!.Start());
            return;
        }

        _draining = false;
        var last = _tracks[0];
        TryOutput(() => output.Pause());
        _playing = false;
        Raise(new PipelineEvent(PipelineEventKind.EndOfStream, last.Generation, last.Tag));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void Publish()
    {
        if (_tracks.Count == 0 || _ring is null || _output is null)
        {
            _snapshot = null;
            return;
        }

        var track = _tracks[0];
        _snapshot = new Snapshot(track.Tag, _ring, _output, track.SegmentStart, track.StartSeconds, FrameRate(_output.Format), track.Duration);
    }

    /// <summary>Ring frames per second: samples, DoP frames, or DSD bytes.</summary>
    private static double FrameRate(OutputFormat format) => format.Kind == StreamKind.Dsd ? format.SampleRate / 8.0 : format.SampleRate;

    private void TryOutput(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warning($"Audio output: {ex.Message}");
        }
    }

    private void FailPlaying(string message)
    {
        if (_tracks.Count == 0)
            return;
        var track = _tracks[0];
        DisposeTracks();
        TryOutput(() => _output?.Pause());
        _playing = false;
        Fail(track.Generation, track.Tag, message);
    }

    private void Fail(int generation, object tag, string message)
    {
        Log.Warning($"Cannot play {tag}: {message}");
        RaiseState(EngineState.Stopped, generation, tag);
        Raise(new PipelineEvent(PipelineEventKind.Failed, generation, tag, Message: message));
    }

    private void RaiseState(EngineState state, int generation = -1, object? tag = null)
    {
        if (generation < 0 && _tracks.Count > 0)
        {
            generation = _tracks[0].Generation;
            tag = _tracks[0].Tag;
        }

        Raise(new PipelineEvent(PipelineEventKind.State, generation, tag, State: state));
    }

    private void Raise(PipelineEvent e)
    {
        try
        {
            Events?.Invoke(e);
        }
        catch (Exception ex)
        {
            Log.Error("Audio event handler failed", ex);
        }
    }

    private static void Grow<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length)
            buffer = new T[Math.Max(length, buffer.Length * 2)];
    }
}
