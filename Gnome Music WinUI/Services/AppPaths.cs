// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// Locations of the files the app keeps for itself (library cache, playlists,
/// statistics, album art). GNOME Music keeps this data in Tracker and in
/// ~/.cache/media-art; on Windows it lives in the app's local data folder.
/// </summary>
public static class AppPaths
{
    private const string UnpackagedFolderName = "GnomeMusicWinUI";

    public static bool IsPackaged { get; } = DetectPackaged();

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string ArtCacheDirectory { get; } = Path.Combine(DataDirectory, "media-art");

    public static string LibraryCacheFile => Path.Combine(DataDirectory, "library.json");

    public static string UserDataFile => Path.Combine(DataDirectory, "songs.json");

    public static string PlaylistsFile => Path.Combine(DataDirectory, "playlists.json");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    private static string ResolveDataDirectory()
    {
        string dir;
        if (IsPackaged)
        {
            dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        else
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                UnpackagedFolderName);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool DetectPackaged()
    {
        uint length = 0;
        // APPMODEL_ERROR_NO_PACKAGE (15700) means the process has no package identity.
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
