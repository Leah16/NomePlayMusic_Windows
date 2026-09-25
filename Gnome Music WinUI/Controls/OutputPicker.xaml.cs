// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Services;
using Gnome_Music_WinUI.Services.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gnome_Music_WinUI.Controls;

/// <summary>
/// The player bar's audio output (not in GNOME Music): the interface, exclusive mode and
/// the device, as Preferences → Music Output has them, applied at once (a song playing
/// moves to the new output). The devices listed before show at once while they are
/// listed again. Its accent is the color of the song's cover (the player bar paints it).
/// </summary>
public sealed partial class OutputPicker : UserControl
{
    private readonly Settings _settings = App.Services.Settings;
    private readonly Dictionary<OutputApi, List<AudioDevice>> _devices = new();
    private bool _loading;
    private int _lookup;

    public OutputPicker()
    {
        InitializeComponent();
    }

    /// <summary>The settings into the controls, as the flyout opens.</summary>
    public void Load()
    {
        var api = _settings.OutputApi;
        _loading = true;
        ApiBar.SelectedItem = ApiBar.Items[(int)api];
        ExclusiveSwitch.IsOn = _settings.WasapiExclusive;
        _loading = false;
        UpdateRows();
        _ = ShowDevicesAsync(api);
    }

    private OutputApi Api => (OutputApi)Math.Max(0, ApiBar.Items.IndexOf(ApiBar.SelectedItem));

    private void OnApiChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_loading || ApiBar.SelectedItem is null)
            return;

        var api = Api;
        _settings.OutputApi = api;
        UpdateRows();
        _ = ShowDevicesAsync(api);
    }

    private void OnExclusiveToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            _settings.WasapiExclusive = ExclusiveSwitch.IsOn;
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && DeviceList.SelectedItem is AudioDevice device)
            _settings.SetOutputDevice(device.Api, device.Id);
    }

    /// <summary>Exclusive mode is WASAPI's.</summary>
    private void UpdateRows() =>
        ExclusiveRow.Visibility = Api == OutputApi.Wasapi ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The devices of an interface: those listed before at once, then those there now.</summary>
    private async Task ShowDevicesAsync(OutputApi api)
    {
        int lookup = ++_lookup;
        _devices.TryGetValue(api, out var known);
        ShowDevices(api, known);

        var devices = await OutputDevices.ListAsync(api);
        if (lookup != _lookup)
            return;

        // The same devices stay as they are (no flicker); the chosen one is selected again.
        if (known is null || !known.SequenceEqual(devices))
            _devices[api] = known = devices;
        ShowDevices(api, known);
    }

    /// <summary>The list with the chosen device selected; while there is none yet, a ring.</summary>
    private void ShowDevices(OutputApi api, List<AudioDevice>? devices)
    {
        _loading = true;
        if (!ReferenceEquals(DeviceList.ItemsSource, devices))
            DeviceList.ItemsSource = devices;
        DeviceList.SelectedItem = devices is null ? null : OutputDevices.Chosen(devices, _settings.GetOutputDevice(api));
        _loading = false;

        LoadingRing.IsActive = devices is null;
        EmptyText.Visibility = devices is { Count: 0 } ? Visibility.Visible : Visibility.Collapsed;
        DeviceList.Visibility = devices is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }
}
