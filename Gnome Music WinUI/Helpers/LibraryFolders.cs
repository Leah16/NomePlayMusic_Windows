// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Linq;
using System.Threading.Tasks;
using Gnome_Music_WinUI.Services;
using Microsoft.Windows.Storage.Pickers;

namespace Gnome_Music_WinUI.Helpers;

/// <summary>
/// Management of the music folders. On Linux the indexed folders are Tracker's
/// business; on Windows the user picks them here or in Preferences.
/// </summary>
public static class LibraryFolders
{
    /// <summary>Asks for a folder and adds it to the library. Returns true if one was added.</summary>
    public static async Task<bool> AddFolderAsync()
    {
        var window = App.MainWindow;
        if (window is null)
            return false;

        try
        {
            var picker = new FolderPicker(window.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
            };
            var result = await picker.PickSingleFolderAsync();
            if (result is null || string.IsNullOrEmpty(result.Path))
                return false;

            Add(result.Path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"Folder picker failed: {ex.Message}");
            return false;
        }
    }

    public static void Add(string path)
    {
        var settings = App.Services.Settings;
        var folders = settings.LibraryFolders.ToList();
        if (folders.Any(f => string.Equals(f.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return;

        folders.Add(path);
        settings.LibraryFolders = folders;
        App.Services.Model.OnLibraryFoldersChanged();
    }

    public static void Remove(string path)
    {
        var settings = App.Services.Settings;
        var folders = settings.LibraryFolders
            .Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        settings.LibraryFolders = folders;
        App.Services.Model.OnLibraryFoldersChanged();
    }
}
