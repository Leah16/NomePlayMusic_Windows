// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Helpers;
using Gnome_Music_WinUI.Services.Audio;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// The devices to choose from for an audio interface: in Preferences → Music Output
/// and behind the player bar's output button (not in GNOME Music).
/// </summary>
public static class OutputDevices
{
    /// <summary>
    /// The devices of an interface, listed off the UI thread: the default device first,
    /// then the ones that are there. ASIO has no default device (its first driver plays
    /// then), so without a driver its list is empty.
    /// </summary>
    public static async Task<List<AudioDevice>> ListAsync(OutputApi api)
    {
        List<AudioDevice> devices;
        try
        {
            devices = await Task.Run(() => api switch
            {
                OutputApi.DirectSound => DirectSoundOutput.ListDevices(),
                OutputApi.Asio => AsioOutput.ListDevices(),
                _ => Wasapi.ListDevices(),
            });
        }
        catch (Exception ex)
        {
            Log.Warning($"Cannot list the {api} devices: {ex.Message}");
            devices = new List<AudioDevice>();
        }

        var items = new List<AudioDevice>();
        if (api != OutputApi.Asio)
            items.Add(new AudioDevice(api, null, Strings.DefaultDevice));
        items.AddRange(devices);
        return items;
    }

    /// <summary>The device the settings chose among them; the first one when it is gone (or none was chosen).</summary>
    public static AudioDevice? Chosen(IReadOnlyList<AudioDevice> devices, string? saved) =>
        devices.FirstOrDefault(d => d.Id == saved) ?? devices.FirstOrDefault();
}
