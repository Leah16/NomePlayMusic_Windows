// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using System.Text;

namespace Gnome_Music_WinUI.Services.Audio;

/// <summary>
/// The header of a DSD file: DSF (Sony's DSD Stream File) or DSDIFF (.dff, Philips).
/// DST-compressed DSDIFF files are recognised but cannot be played.
/// </summary>
internal sealed class DsdFileInfo
{
    public bool IsDsf { get; private set; }

    public int Channels { get; private set; }

    /// <summary>DSD samples (bits) per second of one channel.</summary>
    public int SampleRate { get; private set; }

    /// <summary>The bytes of sound data of one channel.</summary>
    public long BytesPerChannel { get; private set; }

    /// <summary>Where the sound data starts in the file.</summary>
    public long DataOffset { get; private set; }

    /// <summary>DSF: the bytes of one channel in a block; the channels' blocks follow each other.</summary>
    public int BlockSize { get; private set; }

    /// <summary>DSF with 1 bit per sample: the first sample in each byte's least significant bit.</summary>
    public bool LsbFirst { get; private set; }

    /// <summary>DSDIFF: the data is DST compressed.</summary>
    public bool Compressed { get; private set; }

    /// <summary>Where an ID3v2 tag starts (the DSF metadata chunk, the DSDIFF "ID3 " chunk).</summary>
    public long? Id3Offset { get; private set; }

    /// <summary>DSDIFF "DIIN" chunk: the edited master's title and artist.</summary>
    public string? Title { get; private set; }

    public string? Artist { get; private set; }

    public double Duration => SampleRate > 0 ? BytesPerChannel * 8.0 / SampleRate : 0;

    public static bool IsDsdPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".dsf" or ".dff";

    public static DsdFileInfo Read(Stream stream)
    {
        stream.Position = 0;
        var magic = ReadBytes(stream, 4);
        var info = Encoding.ASCII.GetString(magic) switch
        {
            "DSD " => ReadDsf(stream),
            "FRM8" => ReadDff(stream),
            _ => throw new InvalidDataException("Not a DSF or DSDIFF file"),
        };

        if (info.Channels is < 1 or > 8 || info.SampleRate < AudioFormats.Dsd64Rate / 2 || info.BytesPerChannel <= 0)
            throw new InvalidDataException($"Unsupported DSD stream ({info.Channels} ch, {info.SampleRate} Hz)");
        return info;
    }

    private static DsdFileInfo ReadDsf(Stream stream)
    {
        var info = new DsdFileInfo { IsDsf = true };
        var header = ReadBytes(stream, 24);   // chunk size, file size, metadata pointer
        long metadata = BitConverter.ToInt64(header, 16);
        long pos = Math.Max(28, BitConverter.ToInt64(header, 0));
        while (pos + 12 <= stream.Length)
        {
            stream.Position = pos;
            var chunk = ReadBytes(stream, 12);
            string id = Encoding.ASCII.GetString(chunk, 0, 4);
            long size = BitConverter.ToInt64(chunk, 4);
            if (size < 12)
                break;

            if (id == "fmt ")
            {
                var fmt = ReadBytes(stream, 40);
                if (BitConverter.ToInt32(fmt, 4) != 0)
                    throw new InvalidDataException("Unsupported DSF format id");
                info.Channels = BitConverter.ToInt32(fmt, 12);
                info.SampleRate = BitConverter.ToInt32(fmt, 16);
                info.LsbFirst = BitConverter.ToInt32(fmt, 20) == 1;
                long samples = BitConverter.ToInt64(fmt, 24);
                info.BlockSize = BitConverter.ToInt32(fmt, 32);
                info.BytesPerChannel = (samples + 7) / 8;
            }
            else if (id == "data")
            {
                info.DataOffset = pos + 12;
                break;
            }

            pos += size;
        }

        if (info.DataOffset == 0 || info.BlockSize <= 0)
            throw new InvalidDataException("A DSF file without format or data");
        if (metadata > 0 && metadata + 10 <= stream.Length)
            info.Id3Offset = metadata;
        return info;
    }

    private static DsdFileInfo ReadDff(Stream stream)
    {
        var info = new DsdFileInfo();
        var header = ReadBytes(stream, 12);   // size, form type
        if (Encoding.ASCII.GetString(header, 8, 4) != "DSD ")
            throw new InvalidDataException("Not a DSDIFF file");

        long end = Math.Min(stream.Length, 12 + BigEndian64(header, 0));
        long dataSize = 0;
        long pos = 16;
        while (pos + 12 <= end)
        {
            stream.Position = pos;
            var chunk = ReadBytes(stream, 12);
            string id = Encoding.ASCII.GetString(chunk, 0, 4);
            long size = BigEndian64(chunk, 4);
            long data = pos + 12;
            if (size < 0 || data + size > stream.Length + 1)
                break;

            switch (id)
            {
                case "PROP":
                    ReadDffProperties(stream, data, size, info);
                    break;
                case "DSD ":
                    info.DataOffset = data;
                    dataSize = size;
                    break;
                case "DST ":
                    info.Compressed = true;
                    info.DataOffset = data;
                    break;
                case "DIIN":
                    ReadDffMasterInfo(stream, data, size, info);
                    break;
                case "ID3 ":
                    info.Id3Offset = data;
                    break;
            }

            pos = data + size + (size & 1);
        }

        if (info.Compressed)
            throw new InvalidDataException("DST compressed DSDIFF files are not supported");
        if (info.DataOffset == 0 || info.Channels == 0)
            throw new InvalidDataException("A DSDIFF file without properties or sound data");

        info.BytesPerChannel = dataSize / info.Channels;
        return info;
    }

    private static void ReadDffProperties(Stream stream, long start, long size, DsdFileInfo info)
    {
        stream.Position = start;
        if (Encoding.ASCII.GetString(ReadBytes(stream, 4)) != "SND ")
            return;

        long pos = start + 4;
        long end = start + size;
        while (pos + 12 <= end)
        {
            stream.Position = pos;
            var chunk = ReadBytes(stream, 12);
            string id = Encoding.ASCII.GetString(chunk, 0, 4);
            long chunkSize = BigEndian64(chunk, 4);
            var data = ReadBytes(stream, (int)Math.Min(chunkSize, 64));
            switch (id)
            {
                case "FS  " when data.Length >= 4:
                    info.SampleRate = BigEndian32(data, 0);
                    break;
                case "CHNL" when data.Length >= 2:
                    info.Channels = data[0] << 8 | data[1];
                    break;
                case "CMPR" when data.Length >= 4:
                    info.Compressed = Encoding.ASCII.GetString(data, 0, 4) != "DSD ";
                    break;
            }

            pos += 12 + chunkSize + (chunkSize & 1);
        }
    }

    private static void ReadDffMasterInfo(Stream stream, long start, long size, DsdFileInfo info)
    {
        long pos = start;
        long end = start + size;
        while (pos + 12 <= end)
        {
            stream.Position = pos;
            var chunk = ReadBytes(stream, 12);
            string id = Encoding.ASCII.GetString(chunk, 0, 4);
            long chunkSize = BigEndian64(chunk, 4);
            if (id is "DITI" or "DIAR" && chunkSize is > 4 and < 4096)
            {
                var data = ReadBytes(stream, (int)chunkSize);
                int count = Math.Min(BigEndian32(data, 0), data.Length - 4);
                var text = count > 0 ? LegacyText.Decode(data.AsSpan(4, count)).Trim('\0', ' ') : "";
                if (text.Length > 0)
                {
                    if (id == "DITI")
                        info.Title = text;
                    else
                        info.Artist = text;
                }
            }

            pos += 12 + chunkSize + (chunkSize & 1);
        }
    }

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var bytes = new byte[count];
        if (stream.ReadAtLeast(bytes, count, throwOnEndOfStream: false) < count)
            throw new InvalidDataException("The DSD file ends early");
        return bytes;
    }

    private static int BigEndian32(byte[] b, int i) => b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3];

    private static long BigEndian64(byte[] b, int i) => (long)(uint)BigEndian32(b, i) << 32 | (uint)BigEndian32(b, i + 4);
}

/// <summary>
/// Reads the sound of a DSD file as interleaved bytes (a byte per channel per frame),
/// eight 1-bit samples in each, the first in the most significant bit.
/// </summary>
internal sealed class DsdReader : IDisposable
{
    /// <summary>A DSD byte of digital silence (a zero mean pattern).</summary>
    public const byte Silence = 0x69;

    private static readonly byte[] Reversed = BuildReversed();

    private readonly FileStream _stream;
    private readonly byte[] _block;
    private long _blockGroup = -1;
    private long _position;

    public DsdReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        try
        {
            Info = DsdFileInfo.Read(_stream);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }

        _block = new byte[Info.IsDsf ? Info.BlockSize * Info.Channels : 1 << 16];
    }

    public DsdFileInfo Info { get; }

    public int Channels => Info.Channels;

    public int SampleRate => Info.SampleRate;

    public SourceFormat Format => new("DSD", Info.SampleRate, Info.Channels, 1, true, false, 0, Info.SampleRate);

    /// <summary>Reads up to <c>destination.Length / Channels</c> frames; returns how many.</summary>
    public int Read(Span<byte> destination)
    {
        int channels = Info.Channels;
        int frames = (int)Math.Min(destination.Length / channels, Info.BytesPerChannel - _position);
        if (frames <= 0)
            return 0;

        if (!Info.IsDsf)
        {
            _stream.Position = Info.DataOffset + _position * channels;
            int read = _stream.ReadAtLeast(destination[..(frames * channels)], frames * channels, throwOnEndOfStream: false) / channels;
            _position += read;
            return read;
        }

        // DSF: a block of each channel in turn; the last blocks are padded.
        int done = 0;
        int blockSize = Info.BlockSize;
        while (done < frames)
        {
            long group = _position / blockSize;
            int offset = (int)(_position % blockSize);
            if (group != _blockGroup)
            {
                _stream.Position = Info.DataOffset + group * blockSize * channels;
                int got = _stream.ReadAtLeast(_block, _block.Length, throwOnEndOfStream: false);
                if (got < _block.Length)
                    _block.AsSpan(got).Fill(Info.LsbFirst ? Reversed[Silence] : Silence);
                _blockGroup = group;
            }

            int count = Math.Min(frames - done, blockSize - offset);
            for (int c = 0; c < channels; c++)
            {
                var source = _block.AsSpan(c * blockSize + offset, count);
                for (int i = 0; i < count; i++)
                {
                    byte b = source[i];
                    destination[(done + i) * channels + c] = Info.LsbFirst ? Reversed[b] : b;
                }
            }

            done += count;
            _position += count;
        }

        return done;
    }

    public void Seek(double seconds)
    {
        long bytes = (long)(Math.Max(0, seconds) * Info.SampleRate / 8);
        _position = Math.Clamp(bytes & ~1L, 0, Info.BytesPerChannel);
    }

    public void Dispose() => _stream.Dispose();

    private static byte[] BuildReversed()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int r = 0;
            for (int bit = 0; bit < 8; bit++)
                r |= (i >> bit & 1) << (7 - bit);
            table[i] = (byte)r;
        }

        return table;
    }
}

/// <summary>
/// Converts DSD to PCM at twice the family's base rate (88.2 or 96 kHz) in two steps:
/// a filter over the bits, run on whole bytes through tables, down to four times the
/// base rate, then a steep low-pass (flat to 20 kHz, 100 dB down from 30 kHz) that
/// halves the rate. The DSD reference level (50 % modulation) comes out at -6 dBFS;
/// the gain raises it.
/// </summary>
internal sealed class DsdToPcmDecoder : IPcmDecoder
{
    private const int ChunkBytes = 4096;

    private readonly DsdReader _reader;
    private readonly int _channels;
    private readonly int _step1;           // input bytes per stage 1 output
    private readonly int _tables;          // bytes in the stage 1 window
    private readonly float[] _table1;      // _tables × 256 partial sums
    private readonly float[] _taps2;       // stage 2 filter, gain included
    private readonly byte[] _input;        // interleaved DSD bytes read
    private readonly byte[][] _bits;       // per channel: stage 1 history and input
    private readonly float[][] _mid;       // per channel: stage 2 history and input
    private int _bitsLength;
    private int _midLength;
    private float[] _pending = Array.Empty<float>();
    private int _pendingOffset;
    private int _pendingCount;
    private bool _ended;

    public DsdToPcmDecoder(string path, double gainDb)
    {
        _reader = new DsdReader(path);
        try
        {
            _channels = _reader.Channels;
            int rate = _reader.SampleRate;
            int baseRate = rate % 48_000 == 0 && rate % 44_100 != 0 ? 48_000 : 44_100;
            _step1 = Math.Max(1, rate / (4 * baseRate) / 8);
            int midRate = rate / (_step1 * 8);
            SampleRate = midRate / 2;

            // Stage 1: flat to 30 kHz, 100 dB down where images would fold under 30 kHz.
            var taps1 = Filters.KaiserLowPass(rate, 30_000, midRate - 30_000, 100, multipleOf: 8);
            _tables = taps1.Length / 8;
            _table1 = new float[_tables * 256];
            for (int j = 0; j < _tables; j++)
            {
                for (int b = 0; b < 256; b++)
                {
                    double sum = 0;
                    for (int i = 0; i < 8; i++)
                        sum += taps1[8 * j + i] * ((b >> (7 - i) & 1) != 0 ? 1 : -1);
                    _table1[j * 256 + b] = (float)sum;
                }
            }

            // Stage 2: the audio band, with the gain.
            var taps2 = Filters.KaiserLowPass(midRate, 20_000, 30_000, 100, multipleOf: 1);
            double gain = Math.Pow(10, gainDb / 20);
            _taps2 = new float[taps2.Length];
            for (int i = 0; i < taps2.Length; i++)
                _taps2[i] = (float)(taps2[i] * gain);

            _input = new byte[ChunkBytes * _channels];
            _bits = new byte[_channels][];
            _mid = new float[_channels][];
            for (int c = 0; c < _channels; c++)
            {
                _bits[c] = new byte[_tables + ChunkBytes];
                _mid[c] = new float[_taps2.Length + ChunkBytes / _step1 + 2];
            }

            ResetHistory();
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public SourceFormat Format => _reader.Format;

    public int SampleRate { get; }

    public int Channels => _channels;

    public double Duration => _reader.Info.Duration;

    public int Read(Span<float> destination)
    {
        int total = 0;
        int wanted = destination.Length / _channels * _channels;
        while (total < wanted)
        {
            if (_pendingCount == 0)
            {
                if (_ended || !Decode())
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

    public void Seek(double seconds)
    {
        _reader.Seek(seconds);
        ResetHistory();
        _pendingCount = 0;
        _ended = false;
    }

    public void Dispose() => _reader.Dispose();

    private void ResetHistory()
    {
        for (int c = 0; c < _channels; c++)
        {
            _bits[c].AsSpan(0, _tables - 1).Fill(DsdReader.Silence);
            Array.Clear(_mid[c]);
        }

        _bitsLength = _tables - 1;
        _midLength = _taps2.Length - 1;
    }

    /// <summary>Converts a chunk of DSD into <see cref="_pending"/>; false at the end.</summary>
    private bool Decode()
    {
        int frames = _reader.Read(_input);
        if (frames == 0)
        {
            _ended = true;
            return false;
        }

        int outFrames = 0;
        for (int c = 0; c < _channels; c++)
        {
            // Stage 1: DSD bytes to PCM at four times the base rate.
            var bits = _bits[c];
            for (int i = 0; i < frames; i++)
                bits[_bitsLength + i] = _input[i * _channels + c];
            int length = _bitsLength + frames;

            var mid = _mid[c];
            int midLength = _midLength;
            int start = 0;
            for (; start + _tables <= length; start += _step1)
            {
                float sum = 0;
                for (int j = 0; j < _tables; j++)
                    sum += _table1[(j << 8) + bits[start + j]];
                mid[midLength++] = sum;
            }

            Array.Copy(bits, start, bits, 0, length - start);
            if (c == _channels - 1)
                _bitsLength = length - start;

            // Stage 2: halve the rate.
            int taps = _taps2.Length;
            int needed = (midLength - taps) / 2 + 1;
            if (needed > 0 && _pending.Length < needed * _channels)
                _pending = new float[Math.Max(needed * _channels, _pending.Length * 2)];

            int m = 0;
            int pos = 0;
            for (; pos + taps <= midLength; pos += 2, m++)
            {
                float sum = 0;
                for (int k = 0; k < taps; k++)
                    sum += _taps2[k] * mid[pos + k];
                _pending[m * _channels + c] = sum;
            }

            Array.Copy(mid, pos, mid, 0, midLength - pos);
            if (c == _channels - 1)
                _midLength = midLength - pos;
            outFrames = m;
        }

        _pendingOffset = 0;
        _pendingCount = outFrames * _channels;
        return true;
    }
}

/// <summary>FIR filter design.</summary>
internal static class Filters
{
    /// <summary>
    /// A linear phase low-pass by the Kaiser window method, flat to <paramref name="pass"/>
    /// and <paramref name="attenuation"/> dB down from <paramref name="stop"/> (in Hz at
    /// <paramref name="rate"/>), normalised to a gain of 1.
    /// </summary>
    public static double[] KaiserLowPass(double rate, double pass, double stop, double attenuation, int multipleOf)
    {
        double transition = 2 * Math.PI * (stop - pass) / rate;
        int length = (int)Math.Ceiling((attenuation - 8) / (2.285 * transition)) + 1;
        length = (length + multipleOf - 1) / multipleOf * multipleOf;
        double beta = attenuation > 50 ? 0.1102 * (attenuation - 8.7) : 0.5842 * Math.Pow(attenuation - 21, 0.4) + 0.07886 * (attenuation - 21);
        double cutoff = (pass + stop) / 2 / rate;   // cycles per sample
        double centre = (length - 1) / 2.0;
        double i0Beta = BesselI0(beta);

        var taps = new double[length];
        double sum = 0;
        for (int n = 0; n < length; n++)
        {
            double t = n - centre;
            double sinc = t == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * t) / (Math.PI * t);
            double ratio = t / (centre == 0 ? 1 : centre);
            double window = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - ratio * ratio))) / i0Beta;
            taps[n] = sinc * window;
            sum += taps[n];
        }

        for (int n = 0; n < length; n++)
            taps[n] /= sum;
        return taps;
    }

    public static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 50; k++)
        {
            term *= half / k;
            double add = term * term;
            sum += add;
            if (add < sum * 1e-16)
                break;
        }

        return sum;
    }
}
