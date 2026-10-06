namespace ParadisCapture.Core.Audio;

/// <summary>
/// Interleaved stereo float buffer addressed by absolute output frame position (48 kHz frames
/// since recording start). One ring per audio source. The capture thread writes into it at the
/// positions computed from packet timestamps; the mixer reads (and clears) contiguous ranges.
/// Anything never written reads as silence, which is exactly what we want for gaps.
/// </summary>
public sealed class AudioRing
{
    private readonly object _lock = new();
    private readonly float[] _buffer;
    private readonly int _capacityFrames;
    private long _readPosition;   // frames before this have been consumed by the mixer
    private long _droppedLateFrames;
    private long _droppedOverflowFrames;

    public AudioRing(int capacityFrames)
    {
        if (capacityFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        _capacityFrames = capacityFrames;
        _buffer = new float[capacityFrames * 2];
    }

    public int CapacityFrames => _capacityFrames;

    public long ReadPosition { get { lock (_lock) return _readPosition; } }

    /// <summary>Frames that arrived after the mixer had already emitted their slot.</summary>
    public long DroppedLateFrames { get { lock (_lock) return _droppedLateFrames; } }

    /// <summary>Frames that were too far ahead of the mixer to fit.</summary>
    public long DroppedOverflowFrames { get { lock (_lock) return _droppedOverflowFrames; } }

    /// <summary>Writes stereo frames starting at absolute frame <paramref name="position"/>.</summary>
    public void Write(long position, ReadOnlySpan<float> stereo)
    {
        int frames = stereo.Length / 2;
        if (frames == 0) return;

        lock (_lock)
        {
            long start = position;
            long end = position + frames;

            if (start < _readPosition)
            {
                long late = Math.Min(end, _readPosition) - start;
                _droppedLateFrames += late;
                start = _readPosition;
            }

            long limit = _readPosition + _capacityFrames;
            if (end > limit)
            {
                long over = end - Math.Max(start, limit);
                _droppedOverflowFrames += over;
                end = limit;
            }

            if (start >= end) return;

            int srcOffset = (int)(start - position) * 2;
            int count = (int)(end - start);
            int idx = (int)(start % _capacityFrames);
            int first = Math.Min(count, _capacityFrames - idx);
            stereo.Slice(srcOffset, first * 2).CopyTo(_buffer.AsSpan(idx * 2, first * 2));
            if (first < count)
            {
                stereo.Slice(srcOffset + first * 2, (count - first) * 2).CopyTo(_buffer.AsSpan(0, (count - first) * 2));
            }
        }
    }

    /// <summary>
    /// Adds the next <paramref name="frames"/> frames (starting at the read position) into
    /// <paramref name="destination"/> scaled by <paramref name="gain"/>, clears them, and advances
    /// the read position.
    /// </summary>
    public void MixInto(Span<float> destination, int frames, float gain = 1f)
    {
        lock (_lock)
        {
            int remaining = frames;
            int destOffset = 0;
            while (remaining > 0)
            {
                int idx = (int)(_readPosition % _capacityFrames);
                int run = Math.Min(remaining, _capacityFrames - idx);
                var src = _buffer.AsSpan(idx * 2, run * 2);
                var dst = destination.Slice(destOffset, run * 2);
                for (int i = 0; i < src.Length; i++) dst[i] += src[i] * gain;
                src.Clear();
                remaining -= run;
                destOffset += run * 2;
                _readPosition += run;
            }
        }
    }
}
