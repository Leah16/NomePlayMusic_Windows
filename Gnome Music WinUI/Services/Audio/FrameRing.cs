// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Threading;

namespace Gnome_Music_WinUI.Services.Audio;

/// <summary>
/// The frames between the decoding thread (the only writer) and an output's render
/// thread (the only reader), without locks. The counts of frames written and read
/// only grow, so positions in the stream can be told by them.
/// </summary>
internal sealed class FrameRing
{
    private readonly byte[] _buffer;
    private long _written;
    private long _read;

    public FrameRing(int capacityFrames, int frameBytes)
    {
        Capacity = Math.Max(1, capacityFrames);
        FrameBytes = frameBytes;
        _buffer = new byte[(long)Capacity * frameBytes];
    }

    public int Capacity { get; }

    public int FrameBytes { get; }

    /// <summary>Frames written since the ring was made.</summary>
    public long TotalWritten => Volatile.Read(ref _written);

    /// <summary>Frames read (or dropped by <see cref="Clear"/>) since the ring was made.</summary>
    public long TotalRead => Volatile.Read(ref _read);

    public int Available => (int)(TotalWritten - TotalRead);

    public int Free => Capacity - Available;

    /// <summary>Writer: copies in as many whole frames as fit; returns their number.</summary>
    public int Write(ReadOnlySpan<byte> frames)
    {
        long written = _written;
        int free = Capacity - (int)(written - Volatile.Read(ref _read));
        int count = Math.Min(free, frames.Length / FrameBytes);
        if (count <= 0)
            return 0;

        int start = (int)(written % Capacity);
        int first = Math.Min(count, Capacity - start);
        frames[..(first * FrameBytes)].CopyTo(_buffer.AsSpan(start * FrameBytes));
        if (count > first)
            frames.Slice(first * FrameBytes, (count - first) * FrameBytes).CopyTo(_buffer);

        Volatile.Write(ref _written, written + count);
        return count;
    }

    /// <summary>Reader: copies out up to <paramref name="maxFrames"/> frames; returns their number.</summary>
    public int Read(Span<byte> destination, int maxFrames)
    {
        long read = _read;
        int available = (int)(Volatile.Read(ref _written) - read);
        int count = Math.Min(Math.Min(available, maxFrames), destination.Length / FrameBytes);
        if (count <= 0)
            return 0;

        int start = (int)(read % Capacity);
        int first = Math.Min(count, Capacity - start);
        _buffer.AsSpan(start * FrameBytes, first * FrameBytes).CopyTo(destination);
        if (count > first)
            _buffer.AsSpan(0, (count - first) * FrameBytes).CopyTo(destination[(first * FrameBytes)..]);

        Volatile.Write(ref _read, read + count);
        return count;
    }

    /// <summary>Drops what was not read yet. Only while the reader is stopped.</summary>
    public void Clear() => Volatile.Write(ref _read, Volatile.Read(ref _written));
}
