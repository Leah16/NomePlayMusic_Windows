// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Gnome_Music_WinUI.Services;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LibraryCache))]
[JsonSerializable(typeof(UserDataFile))]
[JsonSerializable(typeof(PlaylistsFile))]
[JsonSerializable(typeof(SettingsData))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}

/// <summary>Atomic, trim-safe JSON file persistence.</summary>
internal static class JsonStorage
{
    public static T? Load<T>(string path, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning($"Cannot load {path}: {ex.Message}");
            return null;
        }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            string tmp = path + ".tmp";
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, value, typeInfo);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Cannot save {path}: {ex.Message}");
        }
    }
}

/// <summary>
/// Coalesces bursts of changes into one write, off the UI thread.
/// </summary>
internal sealed class DebouncedSaver
{
    private readonly Action _save;
    private readonly TimeSpan _delay;
    private readonly object _lock = new();
    private CancellationTokenSource? _pending;

    public DebouncedSaver(Action save, TimeSpan delay)
    {
        _save = save;
        _delay = delay;
    }

    public void Schedule()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _pending?.Cancel();
            _pending = cts = new CancellationTokenSource();
        }

        _ = Task.Delay(_delay, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                    _save();
            },
            TaskScheduler.Default);
    }

    /// <summary>Writes immediately if a save is pending (used on exit).</summary>
    public void Flush()
    {
        bool pending;
        lock (_lock)
        {
            pending = _pending is { IsCancellationRequested: false };
            _pending?.Cancel();
            _pending = null;
        }

        if (pending)
            _save();
    }
}
