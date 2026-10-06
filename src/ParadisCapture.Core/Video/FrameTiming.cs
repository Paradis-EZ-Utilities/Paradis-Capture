using ParadisCapture.Core.Timing;

namespace ParadisCapture.Core.Video;

/// <summary>
/// Constant-frame-rate schedule. Frame k has timestamp k/fps on the recording timeline, computed
/// exactly in integer math so timestamps never accumulate rounding error.
/// </summary>
public sealed class FrameSchedule
{
    private readonly int _fps;
    private long _index;

    public FrameSchedule(int fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        _fps = fps;
    }

    public int Fps => _fps;
    public long Index => _index;

    public long TimeOf(long index) => index * RecordingClock.HnsPerSecond / _fps;

    public long NextTime => TimeOf(_index);

    /// <summary>Duration of the next frame (exact: next boundary minus this one).</summary>
    public long NextDuration => TimeOf(_index + 1) - TimeOf(_index);

    public long FrameInterval => RecordingClock.HnsPerSecond / _fps;

    public void Advance() => _index++;

    /// <summary>
    /// If the encoder fell more than <paramref name="maxLagFrames"/> behind (system stall), skip
    /// ahead so we resume at the present instead of bursting stale frames. Returns frames skipped.
    /// </summary>
    public long SkipIfBehind(long recordingNow, int maxLagFrames = 3)
    {
        long current = recordingNow * _fps / RecordingClock.HnsPerSecond;
        if (current - _index > maxLagFrames)
        {
            long skipped = current - _index;
            _index = current;
            return skipped;
        }
        return 0;
    }
}

public static class FrameSelection
{
    /// <summary>
    /// Index of the slot whose capture time is the newest one not later than <paramref name="target"/>.
    /// Falls back to the oldest available slot if all are newer, or -1 if no slot is valid.
    /// </summary>
    public static int Choose(ReadOnlySpan<long> slotTimes, ReadOnlySpan<bool> valid, long target)
    {
        int best = -1, oldest = -1;
        for (int i = 0; i < slotTimes.Length; i++)
        {
            if (!valid[i]) continue;
            if (slotTimes[i] <= target && (best < 0 || slotTimes[i] > slotTimes[best])) best = i;
            if (oldest < 0 || slotTimes[i] < slotTimes[oldest]) oldest = i;
        }
        return best >= 0 ? best : oldest;
    }
}
