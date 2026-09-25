// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using Gnome_Music_WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Gnome_Music_WinUI;

/// <summary>The application (application.py).</summary>
public partial class App : Application
{
    private static AppServices? _services;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Error("Unhandled exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception (domain)", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>The application-wide services (player, library, settings…).</summary>
    public static AppServices Services => _services ?? throw new InvalidOperationException("App not started");

    public static MainWindow? MainWindow => (Current as App)?._window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Log.Info($"Starting (packaged: {AppPaths.IsPackaged}, data: {AppPaths.DataDirectory})");
            _services = new AppServices(DispatcherQueue.GetForCurrentThread());
            _window = new MainWindow();
            _window.Closed += (_, _) => _services?.Dispose();
            _window.Activate();
            _services.Model.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Start-up failed", ex);
            throw;
        }
    }
}
