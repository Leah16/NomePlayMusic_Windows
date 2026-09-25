// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>A notification shown by <see cref="ToastOverlay"/> (AdwToast).</summary>
public sealed class Toast
{
    public Toast(string title)
    {
        Title = title;
    }

    public string Title { get; }

    public string? ButtonLabel { get; init; }

    /// <summary>Seconds before the toast hides itself (AdwToast default: 5).</summary>
    public double Timeout { get; init; } = 5;

    /// <summary>Raised when the action button is clicked, before <see cref="Dismissed"/>.</summary>
    public event Action? ButtonClicked;

    /// <summary>Raised once when the toast goes away, whatever the reason.</summary>
    public event Action? Dismissed;

    internal void RaiseButtonClicked() => ButtonClicked?.Invoke();

    internal void RaiseDismissed() => Dismissed?.Invoke();
}

/// <summary>
/// Shows toasts at the bottom of the content, above the player bar, like
/// AdwToastOverlay: one at a time, later ones wait in a queue.
/// </summary>
public sealed partial class ToastOverlay : UserControl
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Queue<Toast> _pending = new();
    private Toast? _current;

    public ToastOverlay()
    {
        InitializeComponent();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Dismiss();
    }

    public void Show(Toast toast)
    {
        if (_current is not null)
        {
            _pending.Enqueue(toast);
            return;
        }

        Display(toast);
    }

    /// <summary>Dismisses the current toast and every queued one (on exit).</summary>
    public void DismissAll()
    {
        Dismiss(animate: false);
        while (_pending.Count > 0)
            _pending.Dequeue().RaiseDismissed();
    }

    /// <summary>Dismisses the current toast and shows the next one, if any.</summary>
    public void Dismiss(bool animate = true)
    {
        _timer.Stop();
        var toast = _current;
        _current = null;
        if (toast is null)
            return;

        ToastBorder.Opacity = 0;
        ToastBorder.Translation = new Vector3(0, 24, 32);
        toast.RaiseDismissed();

        if (!animate)
        {
            ToastBorder.Visibility = Visibility.Collapsed;
            return;
        }

        _ = ShowNextAfterAnimationAsync();
    }

    private void Display(Toast toast)
    {
        _current = toast;
        TitleText.Text = toast.Title;
        ActionButton.Content = toast.ButtonLabel;
        ActionButton.Visibility = toast.ButtonLabel is null ? Visibility.Collapsed : Visibility.Visible;

        ToastBorder.Visibility = Visibility.Visible;
        ToastBorder.Opacity = 1;
        ToastBorder.Translation = new Vector3(0, 0, 32);

        var peer = FrameworkElementAutomationPeer.FromElement(TitleText) ?? FrameworkElementAutomationPeer.CreatePeerForElement(TitleText);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, toast.Title, "toast");

        _timer.Interval = TimeSpan.FromSeconds(toast.Timeout);
        _timer.Start();
    }

    private async Task ShowNextAfterAnimationAsync()
    {
        await Task.Delay(220);
        if (_current is not null)
            return;

        if (_pending.Count > 0)
            Display(_pending.Dequeue());
        else
            ToastBorder.Visibility = Visibility.Collapsed;
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        _current?.RaiseButtonClicked();
        Dismiss();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Dismiss();
}
