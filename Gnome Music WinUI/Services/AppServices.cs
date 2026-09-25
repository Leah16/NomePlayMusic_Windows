// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using Microsoft.UI.Dispatching;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// Composition root: the objects GNOME Music hangs off its Application instance
/// (coremodel, player, settings, …).
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppServices(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Settings = new Settings();
        UserData = new UserDataStore();
        PlaylistStore = new PlaylistStore();
        Art = new ArtService();
        Lyrics = new LyricsService(Settings);
        Model = new CoreModel(this);
        Player = new Player(this);
        InhibitSuspend = new InhibitSuspend(Player, Settings);
        PauseOnSuspend = new PauseOnSuspend(Player, dispatcher);
    }

    public DispatcherQueue Dispatcher { get; }

    public Settings Settings { get; }

    public UserDataStore UserData { get; }

    public PlaylistStore PlaylistStore { get; }

    public ArtService Art { get; }

    public LyricsService Lyrics { get; }

    public CoreModel Model { get; }

    public Player Player { get; }

    public InhibitSuspend InhibitSuspend { get; }

    public PauseOnSuspend PauseOnSuspend { get; }

    /// <summary>Writes pending changes to disk (called on exit).</summary>
    public void Flush()
    {
        Settings.Flush();
        UserData.Flush();
        PlaylistStore.Flush();
        Model.FlushCache();
    }

    public void Dispose()
    {
        Flush();
        InhibitSuspend.Dispose();
        PauseOnSuspend.Dispose();
        Player.Dispose();
        Model.Dispose();
    }
}
