using ParadisCapture.Core.Timing;

namespace ParadisCapture.Core.Audio;

/// <summary>
/// Pulls contiguous 48 kHz stereo blocks from every source ring, sums them, and converts to
/// 16-bit PCM for the AAC encoder. The mixer is clocked by the recording clock (not by any audio
/// device), so the audio track always has exactly as many samples as the elapsed recording time.
/// Missing data — a source with nothing playing, a disconnected mic — is silence.
/// </summary>
public sealed class AudioMixer
{
    public const int SampleRate = AudioSourceTimeline.OutputSampleRate;
    public const int Channels = 2;
    public const int MaxBlockFrames = SampleRate / 10; // 100 ms

    private readonly List<(AudioRing Ring, float Gain)> _inputs = new();
    private readonly float[] _mix = new float[MaxBlockFrames * Channels];
    private readonly short[] _pcm = new short[MaxBlockFrames * Channels];
    private long _emittedFrames;

    public long EmittedFrames => _emittedFrames;

    public void AddInput(AudioRing ring, float gain = 1f) => _inputs.Add((ring, gain));

    public int InputCount => _inputs.Count;

    /// <summary>Converts a frame position to its exact timestamp in hns.</summary>
    public static long FramesToHns(long frames) => frames * RecordingClock.HnsPerSecond / SampleRate;

    public static long HnsToFrames(long hns) => hns * SampleRate / RecordingClock.HnsPerSecond;

    /// <summary>
    /// Emits all audio up to <paramref name="recordingTimeHns"/>, in blocks of at most 100 ms.
    /// <paramref name="write"/> receives (pcm, frameCount, timestampHns, durationHns).
    /// </summary>
    public void EmitUpTo(long recordingTimeHns, Action<ReadOnlyMemory<short>, int, long, long> write)
    {
        long target = HnsToFrames(recordingTimeHns);
        while (_emittedFrames < target)
        {
            int frames = (int)Math.Min(MaxBlockFrames, target - _emittedFrames);
            MixBlock(frames);

            long start = FramesToHns(_emittedFrames);
            long end = FramesToHns(_emittedFrames + frames);
            _emittedFrames += frames;
            write(_pcm.AsMemory(0, frames * Channels), frames, start, end - start);
        }
    }

    private void MixBlock(int frames)
    {
        var mix = _mix.AsSpan(0, frames * Channels);
        mix.Clear();
        foreach (var (ring, gain) in _inputs)
        {
            ring.MixInto(mix, frames, gain);
        }

        var pcm = _pcm.AsSpan(0, frames * Channels);
        for (int i = 0; i < mix.Length; i++)
        {
            pcm[i] = ToPcm16(mix[i]);
        }
    }

    /// <summary>Float → 16-bit with gentle soft-clipping above ~-1 dBFS instead of hard wrap/clip.</summary>
    internal static short ToPcm16(float x)
    {
        if (float.IsNaN(x)) return 0;
        const float knee = 0.89f;
        float a = Math.Abs(x);
        if (a > knee)
        {
            // Smoothly compress (knee, ∞) into (knee, 1).
            float over = a - knee;
            a = knee + (1f - knee) * (over / (over + (1f - knee)));
            x = MathF.CopySign(a, x);
        }
        int v = (int)MathF.Round(x * 32767f);
        return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    }
}
