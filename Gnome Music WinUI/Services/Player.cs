// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Models;
using Gnome_Music_WinUI.Services.Audio;
using Microsoft.UI.Dispatching;

namespace Gnome_Music_WinUI.Services;

/// <summary>Playback state (player.py Playback).</summary>
public enum PlayerState
{
    Stopped = 0,
    Loading = 1,
    Paused = 2,
    Playing = 3,
}

/// <summary>
/// The music player (player.py): drives the <see cref="AudioEngine"/> from the
/// <see cref="PlayQueue"/>, keeps the play statistics and exposes the state the
/// player toolbar shows. All members must be used on the UI thread.
/// </summary>
public sealed class Player : ObservableObject, IDisposable
{
    /// <summary>"Previous" restarts the current song after this many seconds.</summary>
    private const double PreviousThreshold = 5;

    private readonly AppServices _services;
    private readonly AudioEngine _engine;
    private readonly MediaControls _controls = new();
    private readonly PlayQueue _queue = new();
    private readonly DispatcherQueueTimer _clock;
    private int _timelineTicks;
    private PlayerState _state = PlayerState.Stopped;
    private RepeatMode _repeatMode;
    private double _position;
    private double _duration = -1;
    private double _volume = 1.0;
    private double _replayGainFactor = 1.0;
    private bool _muted;
    private CoreSong? _loadedSong;
    private double _playedSeconds;
    private double _lastTickPosition;
    private bool _newClock;

    /// <summary>The song loaded last has not played yet: once it does, it goes to the top of Recently Played.</summary>
    private bool _newHistory;

    public Player(AppServices services)
    {
        _services = services;
        var settings = services.Settings;
        _engine = new AudioEngine(services.Dispatcher, settings.OutputSettings);
        _engine.Opened += OnEngineOpened;
        _engine.EndOfStream += OnEndOfStream;
        _engine.Advanced += OnEngineAdvanced;
        _engine.Failed += OnEngineFailed;
        _engine.NoDevice += (_, message) => NoPlaybackDevice?.Invoke(this, message);
        _engine.StateChanged += OnEngineStateChanged;
        _engine.DurationChanged += d =>
        {
            if (d > 0)
                Duration = d;
        };
        _engine.OutputChanged += (format, fallback) =>
        {
            OnPropertyChanged(nameof(OutputFormat));
            if (fallback is not null)
                OutputFallback?.Invoke(this, fallback);
        };
        _engine.SetProcessing(settings.PhaseInvert, settings.MonoOutput, settings.SwapChannels);

        _repeatMode = settings.Repeat;
        _queue.SetRepeatMode(_repeatMode);
        settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Settings.Repeat):
                    RepeatMode = settings.Repeat;
                    break;
                case nameof(Settings.ReplayGain) when _loadedSong is { } song:
                    ApplyReplayGain(song);   // applied immediately, like gstplayer.py
                    break;
                case nameof(Settings.OutputSettings):
                    _engine.Configure(settings.OutputSettings);
                    break;
                case nameof(Settings.PhaseInvert) or nameof(Settings.MonoOutput) or nameof(Settings.SwapChannels):
                    _engine.SetProcessing(settings.PhaseInvert, settings.MonoOutput, settings.SwapChannels);
                    break;
                case nameof(Settings.VolumeControlEnabled):
                    ApplyVolume();
                    OnPropertyChanged(nameof(VolumeControlEnabled));
                    break;
            }
        };
        ApplyVolume();

        // The system media transport controls (media keys, the media flyout, the lock
        // screen) are the Windows counterpart of MPRIS.
        _controls.NextPressed += () => services.Dispatcher.TryEnqueue(Next);
        _controls.PreviousPressed += () => services.Dispatcher.TryEnqueue(Previous);
        _controls.PlayPressed += () => services.Dispatcher.TryEnqueue(() => Play());
        _controls.PausePressed += () => services.Dispatcher.TryEnqueue(Pause);
        _controls.SeekRequested += seconds => services.Dispatcher.TryEnqueue(() => Seek(seconds));
        UpdateCommands();

        _clock = services.Dispatcher.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(200);
        _clock.Tick += (_, _) => OnClockTick();

        services.Model.LibraryChanged += (_, _) => OnLibraryChanged();
    }

    /// <summary>Raised when a song could not be played.</summary>
    public event EventHandler<CoreSong>? SongFailed;

    /// <summary>Raised when a new song starts playing (player.py "song-changed").</summary>
    public event EventHandler<CoreSong>? SongChanged;

    /// <summary>The chosen output could not be used and the default device took over (why).</summary>
    public event EventHandler<string>? OutputFallback;

    /// <summary>
    /// There is no playback device (why): the song stays, paused where it was, neither
    /// failed nor skipped; Play goes on once there is a device (not in GNOME Music).
    /// </summary>
    public event EventHandler<string>? NoPlaybackDevice;

    /// <summary>The format of the song playing, as its file has it; null when unknown.</summary>
    public SourceFormat? Format => _engine.Format;

    /// <summary>The format the output device runs at.</summary>
    public OutputFormat? OutputFormat => _engine.OutputFormat;

    /// <summary>The volume can be changed (Settings.VolumeControlEnabled); if not, it is full.</summary>
    public bool VolumeControlEnabled => _services.Settings.VolumeControlEnabled;

    public PlayerState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(IsPlaying));
        }
    }

    public bool IsPlaying => _state == PlayerState.Playing;

    /// <summary>The PLAYING song of the queue; null when stopped.</summary>
    public CoreSong? CurrentSong => _queue.CurrentSong;

    public PlayQueue Queue => _queue;

    public bool HasNext => _queue.HasNext;

    public bool HasPrevious => _queue.HasPrevious;

    /// <summary>Playback position in seconds.</summary>
    public double Position
    {
        get => _position;
        private set => SetProperty(ref _position, value);
    }

    /// <summary>Duration of the current song in seconds; -1 when unknown or stopped.</summary>
    public double Duration
    {
        get => _duration;
        private set => SetProperty(ref _duration, value);
    }

    public RepeatMode RepeatMode
    {
        get => _repeatMode;
        set
        {
            if (_repeatMode == value)
                return;

            _repeatMode = value;
            _queue.SetRepeatMode(value);
            _services.Settings.Repeat = value;
            OnPropertyChanged();
            NotifyQueue();
            QueueNextInEngine();
        }
    }

    /// <summary>
    /// Linear volume 0..1 (the pipeline volume). Not persisted: every start is at
    /// full volume, like GNOME Music.
    /// </summary>
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (SetProperty(ref _volume, value))
            {
                ApplyVolume();
                OnPropertyChanged(nameof(CubicVolume));
            }
        }
    }

    /// <summary>Volume on the slider scale: linear = cubic³ (GStreamer StreamVolume).</summary>
    public double CubicVolume
    {
        get => Math.Cbrt(_volume);
        set => Volume = Math.Pow(Math.Clamp(value, 0, 1), 3);
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (SetProperty(ref _muted, value))
                ApplyVolume();
        }
    }

    // ------------------------------------------------------------------
    // Starting playback
    // ------------------------------------------------------------------

    /// <summary>
    /// Makes <paramref name="source"/> the queue (coremodel.py active_core_object)
    /// and plays <paramref name="start"/>, or the first song (a random one in shuffle mode).
    /// </summary>
    public void PlaySongs(IReadOnlyList<CoreSong> songs, CoreSong? start, object? source, QueueType type)
    {
        if (songs.Count == 0)
            return;

        _queue.Load(songs, source, type);
        var song = _queue.SetSong(start);
        if (song is null)
            return;

        if (song.Validation == SongValidation.Failed && _repeatMode != RepeatMode.Song)
        {
            HandleError(song);
            return;
        }

        Load(song);
    }

    /// <summary>Plays the song at a position of the queue's play order (the play queue overlay).</summary>
    public void JumpTo(int position)
    {
        var song = _queue.JumpTo(position);
        if (song is null)
            return;

        if (song.Validation == SongValidation.Failed && _repeatMode != RepeatMode.Song)
        {
            HandleError(song);
            return;
        }

        Load(song);
    }

    /// <summary>Resumes, or starts the queue again after a stop.</summary>
    public void Play()
    {
        var current = _queue.CurrentSong;
        if (current is null)
        {
            current = _queue.SetSong(null);
            if (current is null)
                return;

            Load(current);
            return;
        }

        if (_loadedSong == current && _engine.Song == current)
            _engine.Play();
        else
            Load(current);
    }

    public void Pause() => _engine.Pause();

    public void PlayPause()
    {
        if (_state == PlayerState.Playing)
            Pause();
        else
            Play();
    }

    /// <summary>Stops playback, ends the queue and hides the player toolbar.</summary>
    public void Stop()
    {
        _engine.Stop();
        _clock.Stop();
        _loadedSong = null;
        _queue.End();
        Position = 0;
        Duration = -1;
        State = PlayerState.Stopped;
        NotifyQueue();
        OnPropertyChanged(nameof(Format));
        _ = _controls.ShowAsync(null, null);
    }

    public void Next()
    {
        if (_queue.Next() && _queue.CurrentSong is { } song)
            Load(song);
        NotifyQueue();
    }

    public void Previous()
    {
        double position = _engine.Position;
        if (position < PreviousThreshold && _queue.HasPrevious)
        {
            if (_queue.Previous() && _queue.CurrentSong is { } song)
                Load(song);
        }
        else if (position < PreviousThreshold && _queue.Position == 0 && _queue.CurrentSong is { } first)
        {
            Seek(0);
            if (_state != PlayerState.Playing)
                Load(first);
        }
        else
        {
            Seek(0);
        }

        NotifyQueue();
    }

    /// <summary>Seeks, ignoring positions beyond the duration (player.py set_position).</summary>
    public void Seek(double seconds)
    {
        if (seconds < 0)
            seconds = 0;
        if (Duration > 0 && seconds > Duration)
            return;

        _engine.Position = seconds;
        Position = seconds;
        _lastTickPosition = seconds;
        _controls.SetTimeline(seconds, Duration);
    }

    /// <summary>Ctrl+R: SONG → ALL → NONE; from NONE or SHUFFLE → SONG.</summary>
    public void ToggleRepeat()
    {
        RepeatMode = _repeatMode switch
        {
            RepeatMode.Song => RepeatMode.All,
            RepeatMode.All => RepeatMode.None,
            _ => RepeatMode.Song,
        };
    }

    /// <summary>Ctrl+S: SHUFFLE ↔ NONE.</summary>
    public void ToggleShuffle() =>
        RepeatMode = _repeatMode == RepeatMode.Shuffle ? RepeatMode.None : RepeatMode.Shuffle;

    /// <summary>Ctrl+plus: +0.1 cubic, then unmute (only unmutes when muted with volume).</summary>
    public void IncreaseVolume()
    {
        if (!VolumeControlEnabled)
            return;
        if (!Muted || _volume == 0)
            CubicVolume = Math.Min(1, CubicVolume + 0.1);
        Muted = false;
    }

    /// <summary>Ctrl+minus: −0.1 cubic, ignored while muted.</summary>
    public void DecreaseVolume()
    {
        if (VolumeControlEnabled && !Muted)
            CubicVolume = Math.Max(0, CubicVolume - 0.1);
    }

    public void ToggleMute()
    {
        if (VolumeControlEnabled)
            Muted = !Muted;
    }

    public void Dispose()
    {
        _clock.Stop();
        _engine.Dispose();
        _controls.Dispose();
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    /// <summary>
    /// The song playing, or paused, left the library: its folder was taken out of the
    /// music folders or deleted. The music stops and the queue empties, so the player
    /// bar goes and neither Play nor the media keys start songs of it again (not in
    /// GNOME Music).
    /// </summary>
    private void OnLibraryChanged()
    {
        if (_queue.CurrentSong is not { } song || ReferenceEquals(_services.Model.FindSong(song.FilePath), song))
            return;

        Log.Info($"The song playing left the library, playback stops: {song.FilePath}");
        _queue.Clear();
        Stop();
    }

    private async void Load(CoreSong song)
    {
        _loadedSong = song;
        Position = 0;
        Duration = song.Duration > 0 ? song.Duration : -1;
        _playedSeconds = 0;
        _lastTickPosition = 0;
        _newClock = true;
        _newHistory = true;
        NotifyQueue();
        ApplyReplayGain(song);

        string? art = song.Album is { } album ? _services.Art.TryGetCachedAlbumArt(album) : null;
        await _engine.LoadAsync(song, _queue.PeekNextPlayable(), play: true);
        await ShowInSystemAsync(song, art);
    }

    /// <summary>Shows the song in the media controls, with its album art once it has been looked up.</summary>
    private async System.Threading.Tasks.Task ShowInSystemAsync(CoreSong song, string? knownArt)
    {
        if (_engine.Song != song)
            return;

        await _controls.ShowAsync(song, knownArt);
        _controls.SetTimeline(0, Duration);
        if (knownArt is not null)
            return;

        var art = await _services.Art.GetSongArtAsync(song);
        if (art is not null && _engine.Song == song)
            await _controls.ShowAsync(song, art);
    }

    /// <summary>Queues the next song in the engine for a gapless transition.</summary>
    private void QueueNextInEngine()
    {
        if (_engine.Song is not null)
            _ = _engine.SetNextAsync(_queue.PeekNextPlayable());
    }

    /// <summary>
    /// The engine switched to the queued song without a gap (playbin
    /// "stream-start" after "about-to-finish"): advance the queue to it.
    /// </summary>
    private void OnEngineAdvanced(CoreSong song)
    {
        bool advanced = _queue.Next();
        if (!advanced || _queue.CurrentSong != song)
        {
            // The queue changed under the engine; play what the queue says.
            if (_queue.CurrentSong is { } current)
                Load(current);
            else
                Stop();
            return;
        }

        _loadedSong = song;
        Position = 0;
        Duration = song.Duration > 0 ? song.Duration : -1;
        _playedSeconds = 0;
        _lastTickPosition = 0;
        _newClock = true;
        _newHistory = true;
        NotifyQueue();
        ApplyReplayGain(song);
        OnPropertyChanged(nameof(Format));
        SongChanged?.Invoke(this, song);
        QueueNextInEngine();
        _ = ShowInSystemAsync(song, song.Album is { } album ? _services.Art.TryGetCachedAlbumArt(album) : null);
    }

    /// <summary>Scales the volume by the song's ReplayGain (rgvolume + rglimiter).</summary>
    private async void ApplyReplayGain(CoreSong song)
    {
        var mode = _services.Settings.ReplayGain;
        double factor = 1.0;
        if (mode != ReplayGainMode.Disabled)
        {
            var info = await System.Threading.Tasks.Task.Run(() => ReplayGain.Read(song.FilePath));
            if (_loadedSong != song)
                return;
            factor = ReplayGain.Factor(info, mode);
        }

        _replayGainFactor = factor;
        ApplyVolume();
    }

    /// <summary>The engine's gain: the volume (full when it cannot be changed) and the ReplayGain.</summary>
    private void ApplyVolume()
    {
        bool control = _services.Settings.VolumeControlEnabled;
        _engine.Volume = (control ? _volume : 1.0) * _replayGainFactor;
        _engine.Muted = control && _muted;
    }

    private void NotifyQueue()
    {
        OnPropertyChanged(nameof(CurrentSong));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(HasPrevious));
        UpdateCommands();
    }

    private void UpdateCommands() => _controls.SetNavigation(_queue.HasNext, _queue.CurrentSong is not null);

    private void OnEngineOpened(CoreSong song)
    {
        song.Validation = SongValidation.Succeeded;
        if (_engine.Duration > 0)
            Duration = _engine.Duration;
        OnPropertyChanged(nameof(Format));
        SongChanged?.Invoke(this, song);
    }

    /// <summary>End of stream: next song, or stop at the end of the queue.</summary>
    private void OnEndOfStream(CoreSong song)
    {
        if (_queue.Next() && _queue.CurrentSong is { } next)
        {
            Load(next);
            NotifyQueue();
        }
        else
        {
            Debug.WriteLine("[player] End of the queue, stopping the player.");
            Stop();
        }
    }

    private void OnEngineFailed(CoreSong song, string message) => HandleError(song);

    /// <summary>
    /// Marks the song failed and skips to the next playable one, unless repeating
    /// the song (player.py _on_error).
    /// </summary>
    private void HandleError(CoreSong song)
    {
        song.Validation = SongValidation.Failed;
        SongFailed?.Invoke(this, song);

        _engine.Stop();
        _clock.Stop();
        _loadedSong = null;
        if (_queue.HasNext && _repeatMode != RepeatMode.Song && _queue.Next() && _queue.CurrentSong is { } next)
        {
            Load(next);
        }
        else
        {
            _queue.End();
            State = PlayerState.Stopped;
        }

        NotifyQueue();
    }

    private void OnEngineStateChanged(EngineState state)
    {
        State = state switch
        {
            EngineState.Playing => PlayerState.Playing,
            EngineState.Paused => PlayerState.Paused,
            EngineState.Loading => PlayerState.Loading,
            _ => PlayerState.Stopped,
        };

        if (state == EngineState.Playing)
        {
            _lastTickPosition = _engine.Position;
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }

        _controls.SetState(State);
        _controls.SetTimeline(_engine.Position, Duration);
    }

    /// <summary>
    /// Updates the position and the play statistics: a song counts as played once
    /// per playback when more than half of it was actually listened to.
    /// </summary>
    private void OnClockTick()
    {
        if (_engine.Song is null)
            return;

        double position = _engine.Position;
        double delta = position - _lastTickPosition;
        if (delta > 0 && delta < 1)
            _playedSeconds += delta;
        _lastTickPosition = position;
        Position = position;

        // The media flyout's timeline: now and then, it runs on by itself in between.
        if (++_timelineTicks % 25 == 0)
            _controls.SetTimeline(position, Duration);

        // Recently Played (the port's history): as soon as the song is heard.
        if (_newHistory && _playedSeconds > 0 && _loadedSong is { } started)
        {
            _newHistory = false;
            _services.Model.OnSongStarted(started);
        }

        if (_newClock && Duration > 0 && position > 0 && _playedSeconds / Duration > 0.5 && _loadedSong is { } song)
        {
            _newClock = false;
            song.CountPlay();
        }
    }
}
