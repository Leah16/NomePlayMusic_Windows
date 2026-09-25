// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services.Audio;
using Microsoft.UI.Dispatching;

namespace Gnome_Music_WinUI.Services;

public enum EngineState
{
    Stopped,
    Loading,
    Paused,
    Playing,
}

/// <summary>
/// The counterpart of gstplayer.py (GStreamer playbin): plays songs through the
/// <see cref="PlaybackPipeline"/>, which decodes them itself and sends them out through
/// the output the settings choose (WASAPI shared or exclusive, DirectSound, ASIO). The
/// next song is queued so that it follows without a gap, like playbin's
/// "about-to-finish". All members are used, and all events raised, on the UI thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DspSettings _dsp = new();
    private readonly PlaybackPipeline _pipeline;
    private readonly DeviceWatcher _watcher;
    private TaskCompletionSource? _loading;
    private int _generation;
    private CoreSong? _song;
    private EngineState _state = EngineState.Stopped;
    private double _volume = 1;
    private bool _muted;

    public AudioEngine(DispatcherQueue dispatcher, OutputSettings settings)
    {
        _dispatcher = dispatcher;
        _pipeline = new PlaybackPipeline(settings, _dsp);
        _pipeline.Events += e => _dispatcher.TryEnqueue(() => OnPipelineEvent(e));
        _watcher = new DeviceWatcher();
        _watcher.DefaultChanged += _pipeline.DefaultDeviceChanged;
    }

    /// <summary>The last song finished and nothing was queued after it (playbin "eos").</summary>
    public event Action<CoreSong>? EndOfStream;

    /// <summary>The queued next song started playing ("stream-start" after a gapless switch).</summary>
    public event Action<CoreSong>? Advanced;

    /// <summary>A song could not be opened or decoded.</summary>
    public event Action<CoreSong, string>? Failed;

    /// <summary>
    /// There is no playback device (why): the song waits, paused where it was, and
    /// plays on when <see cref="Play"/> finds one.
    /// </summary>
    public event Action<CoreSong, string>? NoDevice;

    /// <summary>A song was opened and its duration is known.</summary>
    public event Action<CoreSong>? Opened;

    public event Action<EngineState>? StateChanged;

    public event Action<double>? DurationChanged;

    /// <summary>
    /// The output opened with a format; with a reason when the chosen output could not
    /// be used and the default device took over.
    /// </summary>
    public event Action<OutputFormat?, string?>? OutputChanged;

    public EngineState State => _state;

    /// <summary>The song being played.</summary>
    public CoreSong? Song => _song;

    /// <summary>The format of the song being played.</summary>
    public SourceFormat? Format { get; private set; }

    /// <summary>The format the output device runs at.</summary>
    public OutputFormat? OutputFormat { get; private set; }

    /// <summary>Playback position in seconds.</summary>
    public double Position
    {
        get => _song is null ? 0 : _pipeline.Position;
        set
        {
            if (_song is not null)
                _pipeline.Seek(Math.Clamp(value, 0, Math.Max(0, Duration)));
        }
    }

    /// <summary>Duration in seconds, or -1 when unknown.</summary>
    public double Duration { get; private set; } = -1;

    /// <summary>Linear volume (with ReplayGain), 0..1.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            UpdateGain();
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            UpdateGain();
        }
    }

    /// <summary>
    /// Opens <paramref name="song"/>, queues <paramref name="next"/> behind it and starts
    /// playing if <paramref name="play"/> is true. Completes when the song opened or failed.
    /// </summary>
    public Task LoadAsync(CoreSong song, CoreSong? next, bool play)
    {
        int generation = ++_generation;
        _loading?.TrySetResult();
        _loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _song = song;
        Format = null;
        Duration = -1;
        SetState(EngineState.Loading);
        _pipeline.Load(generation, song.FilePath, song, play);
        _pipeline.SetNext(generation, next?.FilePath, next);
        return _loading.Task;
    }

    /// <summary>Queues the song to play after the current one (null: none).</summary>
    public Task SetNextAsync(CoreSong? next)
    {
        if (_song is not null)
            _pipeline.SetNext(_generation, next?.FilePath, next);
        return Task.CompletedTask;
    }

    public void Play()
    {
        if (_song is not null)
            _pipeline.Play();
    }

    public void Pause() => _pipeline.Pause();

    public void Stop()
    {
        _generation++;
        _loading?.TrySetResult();
        _song = null;
        Format = null;
        _pipeline.Stop();
        SetState(EngineState.Stopped);
    }

    /// <summary>New output settings; the song playing moves over.</summary>
    public void Configure(OutputSettings settings) => _pipeline.Configure(settings);

    /// <summary>Polarity inversion, mono and swapped channels, at once.</summary>
    public void SetProcessing(bool invert, bool mono, bool swap)
    {
        _dsp.Invert = invert;
        _dsp.Mono = mono;
        _dsp.Swap = swap;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _pipeline.Dispose();
    }

    private void UpdateGain() => _dsp.Gain = _muted ? 0 : (float)_volume;

    private void OnPipelineEvent(PipelineEvent e)
    {
        if (e.Kind == PipelineEventKind.OutputChanged)
        {
            OutputFormat = e.Output;
            OutputChanged?.Invoke(e.Output, e.Message);
            return;
        }

        if (e.Generation != _generation || e.Tag is not CoreSong song)
            return;

        switch (e.Kind)
        {
            case PipelineEventKind.Opened when song == _song:
                Format = e.Format;
                Duration = e.Duration > 0 ? e.Duration : -1;
                _loading?.TrySetResult();
                Opened?.Invoke(song);
                DurationChanged?.Invoke(Duration);
                break;
            case PipelineEventKind.Advanced:
                _song = song;
                Format = e.Format;
                Duration = e.Duration > 0 ? e.Duration : -1;
                Advanced?.Invoke(song);
                DurationChanged?.Invoke(Duration);
                break;
            case PipelineEventKind.EndOfStream when song == _song:
                EndOfStream?.Invoke(song);
                break;
            case PipelineEventKind.Failed:
                if (song == _song)
                {
                    SetState(EngineState.Stopped);
                    _loading?.TrySetResult();
                }

                Failed?.Invoke(song, e.Message ?? "");
                break;
            case PipelineEventKind.NoDevice when song == _song:
                _loading?.TrySetResult();
                NoDevice?.Invoke(song, e.Message ?? "");
                break;
            case PipelineEventKind.State when song == _song:
                SetState(e.State);
                break;
        }
    }

    private void SetState(EngineState state)
    {
        if (_state == state)
            return;

        _state = state;
        StateChanged?.Invoke(state);
    }
}
