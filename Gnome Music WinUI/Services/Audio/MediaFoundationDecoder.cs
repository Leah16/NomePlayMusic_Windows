// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Gnome_Music_WinUI.Services.Audio;

/// <summary>A decoder of a file into PCM frames of 32-bit floats.</summary>
internal interface IPcmDecoder : IDisposable
{
    /// <summary>The format of the file, for display.</summary>
    SourceFormat Format { get; }

    /// <summary>The rate of the frames <see cref="Read"/> gives.</summary>
    int SampleRate { get; }

    int Channels { get; }

    /// <summary>In seconds; 0 when unknown.</summary>
    double Duration { get; }

    /// <summary>Reads interleaved frames; returns the number of floats read, 0 at the end.</summary>
    int Read(Span<float> destination);

    void Seek(double seconds);
}

/// <summary>
/// Decodes what Windows can play (MP3, AAC, FLAC, ALAC, WAV, AIFF, WMA, …) with the
/// Media Foundation source reader, the decoders the old MediaPlayer used.
/// </summary>
internal sealed unsafe class MediaFoundationDecoder : IPcmDecoder
{
    private const uint FirstAudioStream = 0xFFFFFFFD;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint MediaSourceIndex = 0xFFFFFFFF;
    private const int EndOfStreamFlag = 0x2;
    private const int MediaTypeChangedFlag = 0x20;

    private static readonly Guid MajorTypeKey = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid ChannelsKey = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SampleRateKey = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid BitsKey = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid ValidBitsKey = new("d9bf8d6a-9530-4b7c-9ddf-ff6fd58bbd06");
    private static readonly Guid BytesPerSecondKey = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid BitrateKey = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid DurationKey = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private static int _started;

    private IntPtr _reader;
    private readonly bool _float;
    private readonly int _bits;
    private float[] _pending = Array.Empty<float>();
    private int _pendingOffset;
    private int _pendingCount;
    private long _skipTo = -1;
    private long _skipFrames;
    private bool _ended;

    public MediaFoundationDecoder(string path)
    {
        Startup();
        IntPtr stream = IntPtr.Zero;
        try
        {
            Check(MFCreateFile(1 /* read */, 0 /* fail if missing */, 2 /* allow write sharing */, path, out stream), "MFCreateFile");
            Check(MFCreateSourceReaderFromByteStream(stream, IntPtr.Zero, out _reader), "MFCreateSourceReaderFromByteStream");
        }
        finally
        {
            NativeCom.Release(ref stream);
        }

        try
        {
            Call(3 + 1, AllStreams, 0);   // SetStreamSelection: nothing …
            Check(Call(3 + 1, FirstAudioStream, 1), "SetStreamSelection");   // … but the first audio stream

            Format = ReadNativeFormat();

            // Floats keep 24-bit samples exact. Should no converter take the stream to
            // floats, integers of its own size are asked for and converted here.
            int pcmBits = Format.BitsPerSample switch { <= 16 => 16, <= 24 => 24, _ => 32 };
            if (!TrySetOutput(FloatSubtype, 32) && !TrySetOutput(PcmSubtype, pcmBits))
                throw new InvalidDataException("No PCM output for this file");

            IntPtr current = GetCurrentType();
            try
            {
                SampleRate = GetUInt32(current, SampleRateKey);
                Channels = GetUInt32(current, ChannelsKey);
                _bits = GetUInt32(current, BitsKey);
                _float = GetGuid(current, SubtypeKey) == FloatSubtype;
            }
            finally
            {
                NativeCom.Release(ref current);
            }

            if (SampleRate <= 0 || Channels <= 0 || !_float && _bits is not (8 or 16 or 24 or 32))
                throw new InvalidDataException($"Unsupported decoded format ({SampleRate} Hz, {Channels} ch, {_bits} bit)");

            // Windows cuts a FLAC file's duration to whole seconds; its header has it exactly.
            using var duration = new NativeCom.PropVariant();
            if (Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase) && FlacDuration(path) is double exact)
                Duration = exact;
            else if (GetPresentationAttribute(MediaSourceIndex, DurationKey, duration.Pointer) >= 0 && duration.Type == NativeCom.PropVariant.VT_UI8)
                Duration = duration.Int64 / 10_000_000.0;
        }
        catch
        {
            NativeCom.Release(ref _reader);
            throw;
        }
    }

    public SourceFormat Format { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public double Duration { get; }

    public int Read(Span<float> destination)
    {
        int total = 0;
        int wanted = destination.Length / Channels * Channels;
        while (total < wanted)
        {
            if (_pendingCount == 0)
            {
                if (_ended || !ReadSample())
                    break;
                continue;
            }

            int count = Math.Min(_pendingCount, wanted - total);
            _pending.AsSpan(_pendingOffset, count).CopyTo(destination[total..]);
            _pendingOffset += count;
            _pendingCount -= count;
            total += count;
        }

        return total;
    }

    /// <summary>
    /// Seeks a little before the position (sources land on frames and seek points, early
    /// or late) and drops what comes before it, so the position is sample accurate.
    /// </summary>
    public void Seek(double seconds)
    {
        long position = (long)(Math.Max(0, seconds) * 10_000_000);
        using var value = new NativeCom.PropVariant();
        value.SetInt64(Math.Max(0, position - 5_000_000));
        var timeFormat = Guid.Empty;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr, int>)NativeCom.Table(_reader)[8])(_reader, &timeFormat, value.Pointer), "SetCurrentPosition");
        _pendingCount = 0;
        _pendingOffset = 0;
        _skipTo = position;
        _skipFrames = 0;
        _ended = false;
    }

    public void Dispose() => NativeCom.Release(ref _reader);

    /// <summary>Decodes the next sample into <see cref="_pending"/>; false at the end.</summary>
    private bool ReadSample()
    {
        uint index, flags;
        long timestamp;
        IntPtr sample;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)NativeCom.Table(_reader)[9])(
            _reader, FirstAudioStream, 0, &index, &flags, &timestamp, &sample);
        Check(hr, "ReadSample");
        if ((flags & MediaTypeChangedFlag) != 0)
            Log.Warning("The decoded format changed while playing; it is not followed.");

        if (sample == IntPtr.Zero)
        {
            if ((flags & EndOfStreamFlag) != 0)
            {
                _ended = true;
                return false;
            }

            return true;   // a gap or a format change: try again
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)NativeCom.Table(sample)[41])(sample, &buffer), "ConvertToContiguousBuffer");
            byte* data;
            uint max, length;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)NativeCom.Table(buffer)[3])(buffer, &data, &max, &length), "Lock");
            try
            {
                Convert(new ReadOnlySpan<byte>(data, (int)length));
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)NativeCom.Table(buffer)[4])(buffer);
            }
        }
        finally
        {
            NativeCom.Release(ref buffer);
            NativeCom.Release(ref sample);
        }

        // After a seek the first sample may start before the position asked for (FLAC
        // lands on a frame or seek point up to a second early): drop up to there, over
        // as many samples as it takes.
        if (_skipTo >= 0)
        {
            _skipFrames = Math.Max(0, (_skipTo - timestamp) * SampleRate / 10_000_000);
            _skipTo = -1;
        }

        if (_skipFrames > 0)
        {
            int skip = (int)Math.Min(_pendingCount, _skipFrames * Channels);
            _pendingOffset += skip;
            _pendingCount -= skip;
            _skipFrames -= skip / Channels;
        }

        if ((flags & EndOfStreamFlag) != 0)
            _ended = true;
        return true;
    }

    private void Convert(ReadOnlySpan<byte> data)
    {
        int bytesPerSample = _float ? 4 : _bits / 8;
        int samples = data.Length / bytesPerSample;
        if (_pending.Length < samples)
            _pending = new float[Math.Max(samples, _pending.Length * 2)];
        _pendingOffset = 0;
        _pendingCount = samples / Channels * Channels;
        var output = _pending.AsSpan(0, _pendingCount);

        if (_float)
        {
            MemoryMarshal.Cast<byte, float>(data)[.._pendingCount].CopyTo(output);
            return;
        }

        switch (_bits)
        {
            case 8:
                for (int i = 0; i < output.Length; i++)
                    output[i] = (data[i] - 128) / 128f;
                break;
            case 16:
                var shorts = MemoryMarshal.Cast<byte, short>(data);
                for (int i = 0; i < output.Length; i++)
                    output[i] = shorts[i] / 32768f;
                break;
            case 24:
                for (int i = 0, j = 0; i < output.Length; i++, j += 3)
                    output[i] = (data[j] << 8 | data[j + 1] << 16 | data[j + 2] << 24) / 2147483648f;
                break;
            default:
                var ints = MemoryMarshal.Cast<byte, int>(data);
                for (int i = 0; i < output.Length; i++)
                    output[i] = ints[i] / 2147483648f;
                break;
        }
    }

    private SourceFormat ReadNativeFormat()
    {
        IntPtr native;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)NativeCom.Table(_reader)[5])(_reader, FirstAudioStream, 0, &native), "GetNativeMediaType");
        try
        {
            var subtype = GetGuid(native, SubtypeKey);
            int rate = GetUInt32(native, SampleRateKey);
            int channels = GetUInt32(native, ChannelsKey);
            int bits = GetUInt32(native, ValidBitsKey) is > 0 and var valid ? valid : GetUInt32(native, BitsKey);
            int bitrate = GetUInt32(native, BitrateKey) is > 0 and var avg ? avg : GetUInt32(native, BytesPerSecondKey) * 8;

            // Subtypes of the form XXXXXXXX-0000-0010-8000-00AA00389B71 carry a format tag.
            uint tag = BitConverter.ToUInt32(subtype.ToByteArray(), 0);
            var (codec, lossless) = tag switch
            {
                0x0001 => ("PCM", true),
                0x0003 => ("PCM", true),
                0xF1AC => ("FLAC", true),
                0x6C61 => ("ALAC", true),
                0x0163 => ("WMA Lossless", true),
                0x0055 => ("MP3", false),
                0x0050 => ("MPEG", false),
                0x1610 => ("AAC", false),
                0x0161 or 0x0162 => ("WMA", false),
                0x704F => ("Opus", false),
                _ => ("Audio", false),
            };
            bool isFloat = tag == 0x0003;
            return new SourceFormat(codec, rate, channels, lossless ? bits : 0, lossless, isFloat, lossless ? 0 : bitrate, 0);
        }
        finally
        {
            NativeCom.Release(ref native);
        }
    }

    private bool TrySetOutput(Guid subtype, int bits)
    {
        Check(MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            SetGuid(type, MajorTypeKey, AudioType);
            SetGuid(type, SubtypeKey, subtype);
            SetUInt32(type, BitsKey, bits);
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)NativeCom.Table(_reader)[7])(_reader, FirstAudioStream, null, type) >= 0;
        }
        finally
        {
            NativeCom.Release(ref type);
        }
    }

    private IntPtr GetCurrentType()
    {
        IntPtr type;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)NativeCom.Table(_reader)[6])(_reader, FirstAudioStream, &type), "GetCurrentMediaType");
        return type;
    }

    private int Call(int slot, uint a, int b) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)NativeCom.Table(_reader)[slot])(_reader, a, b);

    private int GetPresentationAttribute(uint index, Guid key, IntPtr value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr, int>)NativeCom.Table(_reader)[12])(_reader, index, &key, value);

    // IMFAttributes: GetUINT32 is slot 7, GetGUID 10, SetUINT32 21, SetGUID 24.
    private static int GetUInt32(IntPtr attributes, Guid key)
    {
        uint value;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)NativeCom.Table(attributes)[7])(attributes, &key, &value) >= 0 ? (int)value : 0;
    }

    private static Guid GetGuid(IntPtr attributes, Guid key)
    {
        Guid value;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)NativeCom.Table(attributes)[10])(attributes, &key, &value) >= 0 ? value : Guid.Empty;
    }

    private static void SetUInt32(IntPtr attributes, Guid key, int value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)NativeCom.Table(attributes)[21])(attributes, &key, (uint)value), "SetUINT32");

    private static void SetGuid(IntPtr attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)NativeCom.Table(attributes)[24])(attributes, &key, &value), "SetGUID");

    /// <summary>The length in a FLAC file's STREAMINFO block (after an ID3v2 tag, if any).</summary>
    private static double? FlacDuration(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[10];
            if (stream.ReadAtLeast(head, 10, throwOnEndOfStream: false) < 10)
                return null;

            long start = 0;
            if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                start = 10 + (head[6] << 21 | head[7] << 14 | head[8] << 7 | head[9]) + ((head[5] & 0x10) != 0 ? 10 : 0);

            var info = new byte[42];
            stream.Position = start;
            if (stream.ReadAtLeast(info, 42, throwOnEndOfStream: false) < 42
                || info[0] != 'f' || info[1] != 'L' || info[2] != 'a' || info[3] != 'C' || (info[4] & 0x7F) != 0)
                return null;

            int rate = info[18] << 12 | info[19] << 4 | info[20] >> 4;
            long samples = (long)(info[21] & 0x0F) << 32 | (long)info[22] << 24 | (long)info[23] << 16 | (long)info[24] << 8 | info[25];
            return rate > 0 && samples > 0 ? (double)samples / rate : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidDataException($"Media Foundation {what} failed (0x{hr:X8})");
    }

    private static void Startup()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            Check(MFStartup(0x00020070, 1 /* lite: no sockets */), "MFStartup");
    }

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IntPtr type);

    [DllImport("mfplat.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateFile(int accessMode, int openMode, int flags, string path, out IntPtr stream);

    [DllImport("mfreadwrite.dll")]
    private static extern int MFCreateSourceReaderFromByteStream(IntPtr stream, IntPtr attributes, out IntPtr reader);
}
