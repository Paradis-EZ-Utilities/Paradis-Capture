using System.Buffers.Binary;

namespace ParadisCapture.Core.Audio;

/// <summary>Description of a captured PCM stream (what WASAPI hands us).</summary>
public readonly record struct CaptureFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, int ChannelMask = 0)
{
    public int BytesPerSample => BitsPerSample / 8;
    public int BlockAlign => BytesPerSample * Channels;

    public bool IsSupported =>
        SampleRate > 0 && Channels is >= 1 and <= 32 &&
        (IsFloat ? BitsPerSample is 32 or 64 : BitsPerSample is 8 or 16 or 24 or 32);

    public override string ToString() =>
        $"{SampleRate} Hz, {Channels} ch, {BitsPerSample}-bit {(IsFloat ? "float" : "PCM")}, mask 0x{ChannelMask:X}";
}

/// <summary>
/// Converts any interleaved PCM/float capture format into interleaved stereo float,
/// downmixing surround layouts and duplicating mono.
/// </summary>
public sealed class SampleConverter
{
    // Speaker position bits (WAVEFORMATEXTENSIBLE dwChannelMask).
    private const int FrontLeft = 0x1, FrontRight = 0x2, FrontCenter = 0x4, LowFrequency = 0x8,
        BackLeft = 0x10, BackRight = 0x20, FrontLeftOfCenter = 0x40, FrontRightOfCenter = 0x80,
        BackCenter = 0x100, SideLeft = 0x200, SideRight = 0x400;

    private const float Minus3dB = 0.70710678f;

    private readonly CaptureFormat _format;
    private readonly float[] _leftGain;
    private readonly float[] _rightGain;

    public SampleConverter(CaptureFormat format)
    {
        if (!format.IsSupported) throw new NotSupportedException($"Unsupported audio format: {format}");
        _format = format;
        (_leftGain, _rightGain) = BuildMatrix(format.Channels, format.ChannelMask);
    }

    public CaptureFormat Format => _format;

    internal static (float[] Left, float[] Right) BuildMatrix(int channels, int mask)
    {
        var left = new float[channels];
        var right = new float[channels];

        if (channels == 1)
        {
            left[0] = right[0] = 1f;
            return (left, right);
        }

        int[] speakers = ResolveSpeakers(channels, mask);
        for (int c = 0; c < channels; c++)
        {
            switch (speakers[c])
            {
                case FrontLeft: case FrontLeftOfCenter: left[c] = 1f; break;
                case FrontRight: case FrontRightOfCenter: right[c] = 1f; break;
                case FrontCenter: left[c] = right[c] = Minus3dB; break;
                case LowFrequency: break; // LFE is dropped in a stereo downmix
                case BackLeft: case SideLeft: left[c] = Minus3dB; break;
                case BackRight: case SideRight: right[c] = Minus3dB; break;
                case BackCenter: left[c] = right[c] = 0.5f; break;
                default: left[c] = right[c] = 0.5f; break;
            }
        }
        return (left, right);
    }

    private static int[] ResolveSpeakers(int channels, int mask)
    {
        if (mask == 0)
        {
            mask = channels switch
            {
                2 => FrontLeft | FrontRight,
                3 => FrontLeft | FrontRight | FrontCenter,
                4 => FrontLeft | FrontRight | BackLeft | BackRight,
                5 => FrontLeft | FrontRight | FrontCenter | BackLeft | BackRight,
                6 => FrontLeft | FrontRight | FrontCenter | LowFrequency | BackLeft | BackRight,
                8 => FrontLeft | FrontRight | FrontCenter | LowFrequency | BackLeft | BackRight | SideLeft | SideRight,
                _ => 0,
            };
        }

        var result = new int[channels];
        int bit = 1;
        int c = 0;
        while (c < channels && bit != 0 && mask != 0)
        {
            if ((mask & bit) != 0) result[c++] = bit;
            bit <<= 1;
        }
        // Channels beyond the mask (or no mask at all): treat first two as L/R, others as "both".
        for (; c < channels; c++) result[c] = c == 0 ? FrontLeft : c == 1 ? FrontRight : -1;
        return result;
    }

    /// <summary>
    /// Converts <paramref name="frames"/> frames from <paramref name="source"/> into
    /// <paramref name="stereo"/> (length ≥ frames*2).
    /// </summary>
    public void Convert(ReadOnlySpan<byte> source, int frames, Span<float> stereo)
    {
        int ch = _format.Channels;
        int bps = _format.BytesPerSample;
        int block = _format.BlockAlign;
        if (source.Length < frames * block) throw new ArgumentException("Source buffer too small.", nameof(source));
        if (stereo.Length < frames * 2) throw new ArgumentException("Destination buffer too small.", nameof(stereo));

        // Fast paths for the overwhelmingly common shared-mode formats.
        if (_format.IsFloat && bps == 4 && ch == 2)
        {
            for (int i = 0; i < frames * 2; i++)
                stereo[i] = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(i * 4, 4));
            return;
        }

        for (int f = 0; f < frames; f++)
        {
            float l = 0, r = 0;
            var frame = source.Slice(f * block, block);
            for (int c = 0; c < ch; c++)
            {
                float s = ReadSample(frame.Slice(c * bps, bps));
                l += s * _leftGain[c];
                r += s * _rightGain[c];
            }
            stereo[f * 2] = l;
            stereo[f * 2 + 1] = r;
        }
    }

    private float ReadSample(ReadOnlySpan<byte> b)
    {
        if (_format.IsFloat)
        {
            return _format.BitsPerSample == 32
                ? BinaryPrimitives.ReadSingleLittleEndian(b)
                : (float)BinaryPrimitives.ReadDoubleLittleEndian(b);
        }

        return _format.BitsPerSample switch
        {
            8 => (b[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
            24 => ((b[0] << 8 | b[1] << 16 | b[2] << 24) >> 8) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
            _ => 0f,
        };
    }
}
