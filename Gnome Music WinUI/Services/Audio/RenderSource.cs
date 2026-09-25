// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;

namespace Gnome_Music_WinUI.Services.Audio;

/// <summary>
/// The processing that follows the player's controls at once, so it runs where the
/// frames leave for the device. Written by the UI thread, read by the render thread.
/// </summary>
internal sealed class DspSettings
{
    private float _gain = 1;

    /// <summary>Volume × ReplayGain; 0 while muted.</summary>
    public float Gain
    {
        get => Volatile.Read(ref _gain);
        set => Volatile.Write(ref _gain, value);
    }

    /// <summary>Invert the polarity of every channel.</summary>
    public volatile bool Invert;

    /// <summary>Left and right mixed, out of both.</summary>
    public volatile bool Mono;

    /// <summary>Left and right swapped.</summary>
    public volatile bool Swap;
}

/// <summary>
/// Hands an output the frames from the ring in the device's sample format. PCM is
/// processed (<see cref="DspSettings"/>) and converted here; 16-bit output gets
/// triangular dither, except for samples that are exact (a 16-bit file at full volume
/// stays bit-perfect). DoP frames are built here with their alternating marker, so the
/// DAC keeps its lock through gaps, which are filled with DSD silence.
/// </summary>
internal sealed class RenderSource
{
    private readonly FrameRing _ring;
    private readonly DspSettings _dsp;
    private readonly int _channels;
    private readonly SampleFormat _sample;
    private byte[] _scratch = Array.Empty<byte>();
    private bool _marker;
    private uint _random = 0x9E3779B9;

    public RenderSource(FrameRing ring, OutputFormat format, DspSettings dsp)
    {
        _ring = ring;
        Format = format;
        _dsp = dsp;
        _channels = format.Channels;
        _sample = format.Sample;
    }

    public OutputFormat Format { get; }

    public FrameRing Ring => _ring;

    /// <summary>Times the ring ran dry while playing.</summary>
    public int Underruns { get; private set; }

    /// <summary>When false, gaps are not counted as underruns (the stream is ending or paused).</summary>
    public volatile bool ExpectData;

    /// <summary>Fills <paramref name="frames"/> interleaved frames of <paramref name="output"/>.</summary>
    public void Render(Span<byte> output, int frames)
    {
        int ringBytes = frames * _ring.FrameBytes;
        if (_scratch.Length < ringBytes)
            _scratch = new byte[ringBytes];

        int got = _ring.Read(_scratch, frames);
        if (got < frames && ExpectData)
            Underruns++;

        switch (Format.Kind)
        {
            case StreamKind.Pcm:
                RenderPcm(output, frames, got);
                break;
            case StreamKind.Dop:
                RenderDop(output, frames, got);
                break;
            default:
                RenderDsd(output, frames, got);
                break;
        }
    }

    private void RenderPcm(Span<byte> output, int frames, int got)
    {
        var samples = MemoryMarshal.Cast<byte, float>(_scratch.AsSpan(0, got * _ring.FrameBytes));
        int channels = _channels;
        if (channels >= 2 && _dsp.Swap)
        {
            for (int i = 0; i < samples.Length; i += channels)
                (samples[i], samples[i + 1]) = (samples[i + 1], samples[i]);
        }

        if (channels >= 2 && _dsp.Mono)
        {
            for (int i = 0; i < samples.Length; i += channels)
                samples[i] = samples[i + 1] = (samples[i] + samples[i + 1]) * 0.5f;
        }

        float gain = _dsp.Invert ? -_dsp.Gain : _dsp.Gain;
        if (gain != 1)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= gain;
        }

        int bytes = _sample.BytesPerSample();
        var target = output[..(got * channels * bytes)];
        switch (_sample)
        {
            case SampleFormat.Float32:
                var floats = MemoryMarshal.Cast<byte, float>(target);
                for (int i = 0; i < samples.Length; i++)
                    floats[i] = Math.Clamp(samples[i], -1f, 1f);
                break;
            case SampleFormat.Float64:
                var doubles = MemoryMarshal.Cast<byte, double>(target);
                for (int i = 0; i < samples.Length; i++)
                    doubles[i] = Math.Clamp(samples[i], -1f, 1f);
                break;
            case SampleFormat.Int16:
                var shorts = MemoryMarshal.Cast<byte, short>(target);
                for (int i = 0; i < samples.Length; i++)
                {
                    float s = samples[i] * 32768f;
                    if (s != MathF.Round(s))
                        s = MathF.Round(s + Dither());
                    shorts[i] = (short)Math.Clamp(s, -32768f, 32767f);
                }

                break;
            case SampleFormat.Int24:
                for (int i = 0, j = 0; i < samples.Length; i++, j += 3)
                {
                    int v = (int)Math.Clamp(MathF.Round(samples[i] * 8388608f), -8388608f, 8388607f);
                    target[j] = (byte)v;
                    target[j + 1] = (byte)(v >> 8);
                    target[j + 2] = (byte)(v >> 16);
                }

                break;
            case SampleFormat.Int32:
                var ints = MemoryMarshal.Cast<byte, int>(target);
                for (int i = 0; i < samples.Length; i++)
                    ints[i] = (int)Math.Clamp(Math.Round(samples[i] * 2147483648.0), int.MinValue, int.MaxValue);
                break;
            default:
                // ASIO: values of 24, 20, 18 or 16 bits in the low bits of 32.
                int bits = _sample switch
                {
                    SampleFormat.Int32Lsb24 => 24,
                    SampleFormat.Int32Lsb20 => 20,
                    SampleFormat.Int32Lsb18 => 18,
                    _ => 16,
                };
                float scale = 1 << (bits - 1);
                var words = MemoryMarshal.Cast<byte, int>(target);
                for (int i = 0; i < samples.Length; i++)
                    words[i] = (int)Math.Clamp(MathF.Round(samples[i] * scale), -scale, scale - 1);
                break;
        }

        output[(got * channels * bytes)..(frames * channels * bytes)].Clear();
    }

    private void RenderDop(Span<byte> output, int frames, int got)
    {
        int channels = _channels;
        int bytes = _sample.BytesPerSample();
        for (int f = 0; f < frames; f++)
        {
            byte marker = _marker ? (byte)0xFA : (byte)0x05;
            _marker = !_marker;
            for (int c = 0; c < channels; c++)
            {
                byte first = DsdReader.Silence, second = DsdReader.Silence;
                if (f < got)
                {
                    first = _scratch[(f * channels + c) * 2];
                    second = _scratch[(f * channels + c) * 2 + 1];
                }

                var sample = output.Slice((f * channels + c) * bytes, bytes);
                switch (_sample)
                {
                    case SampleFormat.Int24:
                        sample[0] = second;
                        sample[1] = first;
                        sample[2] = marker;
                        break;
                    case SampleFormat.Int32:
                        BinaryPrimitives.WriteInt32LittleEndian(sample, marker << 24 | first << 16 | second << 8);
                        break;
                    default:   // Int32Lsb24: the 24-bit word, sign extended
                        BinaryPrimitives.WriteInt32LittleEndian(sample, (marker << 24 | first << 16 | second << 8) >> 8);
                        break;
                }
            }
        }
    }

    private void RenderDsd(Span<byte> output, int frames, int got)
    {
        int count = got * _channels;
        bool lsb = _sample == SampleFormat.DsdLsb;
        for (int i = 0; i < count; i++)
            output[i] = lsb ? Reverse(_scratch[i]) : _scratch[i];
        output[count..(frames * _channels)].Fill(lsb ? Reverse(DsdReader.Silence) : DsdReader.Silence);
    }

    private static byte Reverse(byte b)
    {
        b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
        b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
        return (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
    }

    /// <summary>Triangular dither of ±1 LSB.</summary>
    private float Dither()
    {
        return (Next() - Next()) * (1f / 4294967296f);

        uint Next()
        {
            _random ^= _random << 13;
            _random ^= _random >> 17;
            _random ^= _random << 5;
            return _random;
        }
    }
}
