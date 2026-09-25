// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gnome_Music_WinUI.Services.Audio;

/// <summary>The Windows audio interface the music goes out through.</summary>
public enum OutputApi
{
    Wasapi,
    DirectSound,
    Asio,
}

/// <summary>How DSD files reach the output.</summary>
public enum DsdMode
{
    /// <summary>Converted to PCM; works with every output.</summary>
    ConvertToPcm,

    /// <summary>DSD over PCM: the bits packed into 24-bit frames for a DAC that unpacks them.</summary>
    Dop,

    /// <summary>The bits as they are, through an ASIO driver that takes DSD.</summary>
    Native,
}

/// <summary>What the frames of a stream hold.</summary>
public enum StreamKind
{
    Pcm,

    /// <summary>DSD over PCM frames (two DSD bytes and a marker per channel).</summary>
    Dop,

    /// <summary>DSD bytes, eight 1-bit samples each.</summary>
    Dsd,
}

/// <summary>How a sample is written in an output buffer (little endian).</summary>
public enum SampleFormat
{
    Int16,

    /// <summary>Packed in 3 bytes.</summary>
    Int24,

    /// <summary>MSB aligned; also 24 valid bits in a 32-bit container.</summary>
    Int32,

    Float32,
    Float64,

    /// <summary>ASIO: 24-bit values in the low bits of 32 (and the same for 20, 18 and 16 bits).</summary>
    Int32Lsb24,
    Int32Lsb20,
    Int32Lsb18,
    Int32Lsb16,

    /// <summary>Eight DSD samples per byte, the first in the most significant bit.</summary>
    DsdMsb,

    /// <summary>Eight DSD samples per byte, the first in the least significant bit.</summary>
    DsdLsb,
}

public static class AudioFormats
{
    /// <summary>The rate of DSD64: 64 × 44.1 kHz.</summary>
    public const int Dsd64Rate = 2_822_400;

    /// <summary>The PCM rates probed and listed, in Hz.</summary>
    public static readonly int[] PcmRates =
    {
        44_100, 48_000, 88_200, 96_000, 176_400, 192_000, 352_800, 384_000, 705_600, 768_000,
    };

    /// <summary>The DSD rates probed and listed: DSD64 to DSD512 (44.1 kHz family).</summary>
    public static readonly int[] DsdRates = { 2_822_400, 5_644_800, 11_289_600, 22_579_200 };

    public static int BytesPerSample(this SampleFormat format) => format switch
    {
        SampleFormat.Int16 => 2,
        SampleFormat.Int24 => 3,
        SampleFormat.Float64 => 8,
        SampleFormat.DsdMsb or SampleFormat.DsdLsb => 1,
        _ => 4,
    };

    public static bool IsDsd(this SampleFormat format) => format is SampleFormat.DsdMsb or SampleFormat.DsdLsb;

    /// <summary>The DoP frame rate of a DSD rate: 16 DSD samples per frame.</summary>
    public static int DopRate(int dsdRate) => dsdRate / 16;

    /// <summary>"44.1 kHz", "96 kHz", "2.8224 MHz".</summary>
    public static string RateText(int rate) => rate >= 1_000_000
        ? (rate / 1_000_000.0).ToString("0.####", CultureInfo.InvariantCulture) + " MHz"
        : (rate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz";

    /// <summary>"DSD64", "DSD128"; the 48 kHz family is named the same way.</summary>
    public static string DsdName(int rate)
    {
        foreach (int baseRate in new[] { 44_100, 48_000 })
        {
            if (rate % baseRate == 0 && rate / baseRate is var multiple && multiple >= 64 && (multiple & (multiple - 1)) == 0)
                return "DSD" + multiple.ToString(CultureInfo.InvariantCulture);
        }

        return "DSD " + RateText(rate);
    }
}

/// <summary>The format of a file as it is decoded, for display.</summary>
/// <param name="Codec">"FLAC", "MP3", "PCM", "DSD", …</param>
/// <param name="BitsPerSample">0 for lossy formats, 1 for DSD.</param>
/// <param name="Bitrate">Bits per second of a lossy stream; 0 when unknown.</param>
/// <param name="DsdRate">The DSD sample rate; 0 for PCM.</param>
public sealed record SourceFormat(
    string Codec, int SampleRate, int Channels, int BitsPerSample, bool Lossless, bool Float, int Bitrate, int DsdRate)
{
    public bool IsDsd => DsdRate > 0;

    /// <summary>"24 bit · 96 kHz", "32 bit float · 48 kHz", "320 kbps · 44.1 kHz", "DSD64 · 2.8224 MHz".</summary>
    public string Describe()
    {
        if (IsDsd)
            return $"{AudioFormats.DsdName(DsdRate)} · {AudioFormats.RateText(DsdRate)}";

        string rate = AudioFormats.RateText(SampleRate);
        if (Lossless && BitsPerSample > 0)
            return $"{BitsPerSample} bit{(Float ? " float" : "")} · {rate}";
        if (Bitrate > 0)
            return $"{(Bitrate + 500) / 1000} kbps · {rate}";
        return rate;
    }
}

/// <summary>The format frames go to an output device in.</summary>
public sealed record OutputFormat(StreamKind Kind, int SampleRate, int Channels, SampleFormat Sample)
{
    public int BytesPerFrame => Channels * Sample.BytesPerSample();

    public override string ToString() =>
        $"{Kind} {Sample} {AudioFormats.RateText(SampleRate)} {Channels}ch";
}

/// <summary>
/// What the pipeline asks an output for. For PCM the output may choose another rate,
/// channel count or sample format (the pipeline converts); for DoP and DSD it must
/// take the request as it is.
/// </summary>
public sealed record OutputRequest(StreamKind Kind, int SampleRate, int Channels, int BitsPerSample, bool Float);

/// <summary>An output device. A null id is the system's (or the API's) default device.</summary>
public sealed record AudioDevice(OutputApi Api, string? Id, string Name);

/// <summary>The output as the settings choose it.</summary>
public sealed record OutputSettings(OutputApi Api, string? DeviceId, bool Exclusive, DsdMode DsdMode, double DsdGainDb);

/// <summary>What a device plays, as far as Windows or its driver tells.</summary>
public sealed class DeviceCapabilities
{
    /// <summary>PCM rates the device takes as they are (WASAPI exclusive mode, ASIO).</summary>
    public List<int> PcmRates { get; } = new();

    /// <summary>Bit depths it takes: 16, 24, 32; a float format is listed as -32.</summary>
    public List<int> BitDepths { get; } = new();

    public List<int> Channels { get; } = new();

    /// <summary>DSD rates that fit in DoP frames the device takes (if the DAC unpacks DoP).</summary>
    public List<int> DopRates { get; } = new();

    /// <summary>DSD rates an ASIO driver takes natively.</summary>
    public List<int> NativeDsdRates { get; } = new();

    /// <summary>The format of the Windows mixer for the device (shared mode), if any.</summary>
    public int MixRate { get; set; }

    public int MixChannels { get; set; }

    /// <summary>The mixer's bit depth; a float format is -32.</summary>
    public int MixBitDepth { get; set; }

    /// <summary>Why nothing could be found out.</summary>
    public string? Error { get; set; }
}

/// <summary>An output could not be opened or failed while playing.</summary>
public sealed class AudioOutputException : Exception
{
    public AudioOutputException(string message, int hresult = 0)
        : base(hresult != 0 ? $"{message} (0x{hresult:X8})" : message)
    {
        HResult = hresult;
    }
}
