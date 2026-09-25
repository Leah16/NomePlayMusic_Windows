// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Models;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// The system media transport controls (media keys, the media flyout, the lock
/// screen), the Windows counterpart of MPRIS. The music no longer plays through a
/// MediaPlayer, so they are driven by hand: a MediaPlayer that never plays only
/// provides them. Button events are raised on a system thread.
/// </summary>
public sealed class MediaControls : IDisposable
{
    private readonly MediaPlayer _host = new();
    private readonly SystemMediaTransportControls _controls;
    private CoreSong? _song;

    public MediaControls()
    {
        _host.CommandManager.IsEnabled = false;
        _controls = _host.SystemMediaTransportControls;
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsStopEnabled = false;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.PlaybackPositionChangeRequested += (_, e) => SeekRequested?.Invoke(e.RequestedPlaybackPosition.TotalSeconds);
    }

    public event Action? PlayPressed;

    public event Action? PausePressed;

    public event Action? NextPressed;

    public event Action? PreviousPressed;

    public event Action<double>? SeekRequested;

    public void SetNavigation(bool next, bool previous)
    {
        _controls.IsNextEnabled = next;
        _controls.IsPreviousEnabled = previous;
    }

    public void SetState(PlayerState state) => _controls.PlaybackStatus = state switch
    {
        PlayerState.Playing => MediaPlaybackStatus.Playing,
        PlayerState.Paused => MediaPlaybackStatus.Paused,
        PlayerState.Loading => MediaPlaybackStatus.Changing,
        _ => _song is null ? MediaPlaybackStatus.Closed : MediaPlaybackStatus.Stopped,
    };

    public void SetTimeline(double position, double duration)
    {
        if (duration <= 0)
            return;

        _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(duration),
            MaxSeekTime = TimeSpan.FromSeconds(duration),
            Position = TimeSpan.FromSeconds(Math.Clamp(position, 0, duration)),
        });
    }

    /// <summary>Shows the song (null: nothing) with its album art, if known.</summary>
    public async Task ShowAsync(CoreSong? song, string? artPath)
    {
        _song = song;
        var updater = _controls.DisplayUpdater;
        if (song is null)
        {
            updater.ClearAll();
            updater.Update();
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            return;
        }

        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = song.Title;
        updater.MusicProperties.Artist = song.Artist;
        updater.MusicProperties.AlbumTitle = song.AlbumTitle;
        updater.MusicProperties.AlbumArtist = song.Album?.Artist ?? "";
        updater.MusicProperties.TrackNumber = song.TrackNumber > 0 ? (uint)song.TrackNumber : 0;
        updater.Thumbnail = null;
        if (artPath is not null)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(artPath);
                if (_song != song)
                    return;
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            }
            catch (Exception ex)
            {
                Log.Warning($"Cannot load art {artPath}: {ex.Message}");
            }
        }

        updater.Update();
    }

    public void Dispose()
    {
        _controls.ButtonPressed -= OnButtonPressed;
        _host.Dispose();
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                PlayPressed?.Invoke();
                break;
            case SystemMediaTransportControlsButton.Pause:
                PausePressed?.Invoke();
                break;
            case SystemMediaTransportControlsButton.Next:
                NextPressed?.Invoke();
                break;
            case SystemMediaTransportControlsButton.Previous:
                PreviousPressed?.Invoke();
                break;
        }
    }
}
