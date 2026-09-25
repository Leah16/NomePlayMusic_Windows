// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Diagnostics;
using System.IO;

namespace Gnome_Music_WinUI.Services;

/// <summary>
/// Minimal logger (musiclogger.py): writes to the debugger and to
/// gnome-music.log in the app's data folder.
/// </summary>
public static class Log
{
    private static readonly object Lock = new();
    private static readonly string FilePath = Path.Combine(AppPaths.DataDirectory, "gnome-music.log");

    static Log()
    {
        try
        {
            // Keep the log small: start over when it grows past 1 MB.
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1024 * 1024)
                File.Delete(FilePath);
        }
        catch (IOException)
        {
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        lock (Lock)
        {
            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
