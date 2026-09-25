// SPDX-License-Identifier: GPL-2.0-or-later
using System;

namespace Gnome_Music_WinUI.Services.Audio;

/// <summary>
/// Changes the sample rate of interleaved float frames by any ratio: a windowed sinc
/// (Kaiser, 24 zero crossings, about 90 dB of stop band) read from a table of 512
/// phases with linear interpolation between them. When lowering the rate the cut-off
/// follows the new Nyquist frequency. It keeps its state between calls, so one stream
/// can go through it in pieces, and songs of the same rate join without a seam.
/// </summary>
internal sealed class Resampler
{
    private const int ZeroCrossings = 24;
    private const int Phases = 512;

    private readonly int _channels;
    private readonly double _step;
    private readonly int _half;
    private readonly int _taps;
    private readonly float[] _table;
    private readonly float[] _coefficients;
    private float[] _history;
    private int _historyFrames;
    private double _position;

    public Resampler(int channels, int inRate, int outRate)
    {
        _channels = channels;
        InRate = inRate;
        OutRate = outRate;
        _step = (double)inRate / outRate;

        double cutoff = 0.5 * Math.Min(1.0, (double)outRate / inRate) * 0.96;   // cycles per input sample
        _half = (int)Math.Ceiling(ZeroCrossings / (2 * cutoff));
        _taps = 2 * _half;
        _table = new float[(Phases + 1) * _taps];
        _coefficients = new float[_taps];
        double beta = 8.6;
        double i0Beta = Filters.BesselI0(beta);
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            for (int i = 0; i < _taps; i++)
            {
                double t = frac + _half - 1 - i;   // distance from the output to input i
                double sinc = t == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * t) / (Math.PI * t);
                double r = t / _half;
                double window = Math.Abs(r) >= 1 ? 0 : Filters.BesselI0(beta * Math.Sqrt(1 - r * r)) / i0Beta;
                _table[p * _taps + i] = (float)(sinc * window);
            }
        }

        _history = new float[(_taps + 4096) * channels];
        Reset();
    }

    public int InRate { get; }

    public int OutRate { get; }

    public int Channels => _channels;

    /// <summary>The most frames <see cref="Process"/> can give for <paramref name="inputFrames"/>.</summary>
    public int MaxOutputFrames(int inputFrames) => (int)Math.Ceiling((inputFrames + _taps) / _step) + 2;

    public void Reset()
    {
        // Zeros before the first frame: the first output lands on it.
        _historyFrames = _half - 1;
        Array.Clear(_history, 0, _historyFrames * _channels);
        _position = _half - 1;
    }

    /// <summary>Takes input frames and writes the output frames that are ready; returns the floats written.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int frames = input.Length / _channels;
        EnsureCapacity(_historyFrames + frames);
        input[..(frames * _channels)].CopyTo(_history.AsSpan(_historyFrames * _channels));
        _historyFrames += frames;
        return Produce(output, _historyFrames);
    }

    /// <summary>At the end of the stream: lets out the last frames (the filter's tail).</summary>
    public int Flush(Span<float> output)
    {
        int last = _historyFrames;   // outputs up to the last real frame
        EnsureCapacity(_historyFrames + _half + 1);
        Array.Clear(_history, _historyFrames * _channels, (_half + 1) * _channels);
        _historyFrames += _half + 1;
        int written = Produce(output, Math.Min(_historyFrames, last + _half));
        Reset();
        return written;
    }

    private int Produce(Span<float> output, int limit)
    {
        int written = 0;
        int maxFrames = output.Length / _channels;
        int channels = _channels;
        var history = _history;
        while (written < maxFrames)
        {
            int index = (int)_position;
            if (index + _half >= limit)
                break;

            double frac = _position - index;
            double phase = frac * Phases;
            int p = (int)phase;
            float f = (float)(phase - p);
            var row0 = _table.AsSpan(p * _taps, _taps);
            var row1 = _table.AsSpan((p + 1) * _taps, _taps);
            for (int i = 0; i < _taps; i++)
                _coefficients[i] = row0[i] + f * (row1[i] - row0[i]);

            int first = (index - _half + 1) * channels;
            for (int c = 0; c < channels; c++)
            {
                float sum = 0;
                int k = first + c;
                for (int i = 0; i < _taps; i++, k += channels)
                    sum += history[k] * _coefficients[i];
                output[written * channels + c] = sum;
            }

            written++;
            _position += _step;
        }

        // Drop the frames no later output needs.
        int keepFrom = Math.Max(0, (int)_position - _half + 1);
        if (keepFrom > 0)
        {
            keepFrom = Math.Min(keepFrom, _historyFrames);
            Array.Copy(history, keepFrom * channels, history, 0, (_historyFrames - keepFrom) * channels);
            _historyFrames -= keepFrom;
            _position -= keepFrom;
        }

        return written * channels;
    }

    private void EnsureCapacity(int frames)
    {
        if (frames * _channels > _history.Length)
            Array.Resize(ref _history, Math.Max(frames * _channels, _history.Length * 2));
    }
}

/// <summary>
/// Maps frames between channel layouts in the WAVE order (FL, FR, FC, LFE, BL, BR,
/// SL, SR). Surround goes down to stereo with the centre and the surrounds at -3 dB
/// (and the whole scaled so it cannot clip), the LFE left out.
/// </summary>
internal static class ChannelMapper
{
    public static void Map(ReadOnlySpan<float> input, int inChannels, Span<float> output, int outChannels, int frames)
    {
        if (inChannels == outChannels)
        {
            input[..(frames * inChannels)].CopyTo(output);
            return;
        }

        const float Minus3dB = 0.70710678f;
        for (int f = 0; f < frames; f++)
        {
            var i = input.Slice(f * inChannels, inChannels);
            var o = output.Slice(f * outChannels, outChannels);
            o.Clear();

            // First to stereo (or mono).
            float left, right;
            if (inChannels == 1)
            {
                left = right = i[0];
            }
            else if (inChannels == 2)
            {
                left = i[0];
                right = i[1];
            }
            else
            {
                float centre = inChannels > 2 ? i[2] * Minus3dB : 0;
                float backLeft = inChannels > 4 ? i[4] * Minus3dB : 0;
                float backRight = inChannels > 5 ? i[5] * Minus3dB : 0;
                float sideLeft = inChannels > 6 ? i[6] * Minus3dB : 0;
                float sideRight = inChannels > 7 ? i[7] * Minus3dB : 0;
                float scale = 1 / (1 + (inChannels > 2 ? Minus3dB : 0) + (inChannels > 4 ? Minus3dB : 0) + (inChannels > 6 ? Minus3dB : 0));
                left = (i[0] + centre + backLeft + sideLeft) * scale;
                right = (i[1] + centre + backRight + sideRight) * scale;
            }

            if (outChannels == 1)
            {
                o[0] = (left + right) * 0.5f;
            }
            else if (inChannels <= 2 || outChannels == 2)
            {
                o[0] = left;
                o[1] = right;
            }
            else
            {
                // Surround to another surround layout: the channels both have.
                for (int c = 0; c < Math.Min(inChannels, outChannels); c++)
                    o[c] = i[c];
            }
        }
    }
}
