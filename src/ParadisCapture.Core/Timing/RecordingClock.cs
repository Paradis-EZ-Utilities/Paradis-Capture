using System.Diagnostics;

namespace ParadisCapture.Core.Timing;

/// <summary>
/// The single time base of a recording.
///
/// All capture timestamps in the app (Windows.Graphics.Capture frame times and WASAPI packet
/// QPC positions) are QueryPerformanceCounter values expressed in 100-nanosecond units ("hns").
/// This clock converts such a "source time" into "recording time": the position on the output
/// timeline, which starts at 0 when recording starts and does not advance while paused.
///
/// Because audio and video are both placed on this one timeline, they cannot drift apart,
/// no matter how long the recording runs.
/// </summary>
public sealed class RecordingClock
{
    public const long HnsPerSecond = 10_000_000;

    private readonly object _lock = new();
    private readonly Func<long> _now;
    private readonly List<(long Start, long End)> _pauses = new();
    private long _start = long.MinValue;
    private long _pauseStart = long.MinValue; // != MinValue while currently paused
    private long _pausedTotal;
    private long _stop = long.MinValue;

    public RecordingClock(Func<long>? nowHns = null) => _now = nowHns ?? QpcNowHns;

    /// <summary>Current QueryPerformanceCounter value in 100ns units (same base as WGC/WASAPI timestamps).</summary>
    public static long QpcNowHns()
    {
        long ts = Stopwatch.GetTimestamp();
        long f = Stopwatch.Frequency;
        return (ts / f) * HnsPerSecond + (ts % f) * HnsPerSecond / f;
    }

    public long SourceNow => _now();

    public bool IsStarted { get { lock (_lock) return _start != long.MinValue; } }
    public bool IsPaused { get { lock (_lock) return _pauseStart != long.MinValue; } }
    public bool IsStopped { get { lock (_lock) return _stop != long.MinValue; } }

    public void Start() => Start(_now());

    public void Start(long sourceTime)
    {
        lock (_lock)
        {
            if (_start != long.MinValue) throw new InvalidOperationException("Clock already started.");
            _start = sourceTime;
        }
    }

    public void Pause() => Pause(_now());

    public void Pause(long sourceTime)
    {
        lock (_lock)
        {
            if (_start == long.MinValue || _stop != long.MinValue || _pauseStart != long.MinValue) return;
            _pauseStart = Math.Max(sourceTime, LastBoundary());
        }
    }

    public void Resume() => Resume(_now());

    public void Resume(long sourceTime)
    {
        lock (_lock)
        {
            if (_pauseStart == long.MinValue) return;
            long end = Math.Max(sourceTime, _pauseStart);
            _pauses.Add((_pauseStart, end));
            _pausedTotal += end - _pauseStart;
            _pauseStart = long.MinValue;
        }
    }

    /// <summary>Stops the clock. Returns the final recording duration in hns.</summary>
    public long Stop() => Stop(_now());

    public long Stop(long sourceTime)
    {
        lock (_lock)
        {
            if (_start == long.MinValue) throw new InvalidOperationException("Clock not started.");
            if (_stop == long.MinValue)
            {
                // Stopping while paused ends the recording at the pause point.
                _stop = _pauseStart != long.MinValue ? _pauseStart : Math.Max(sourceTime, LastBoundary());
            }
            return _stop - _start - _pausedTotal;
        }
    }

    private long LastBoundary() => _pauses.Count > 0 ? _pauses[^1].End : _start;

    /// <summary>
    /// The current position on the recording timeline (hns). Frozen while paused and after stop.
    /// </summary>
    public long RecordingNow()
    {
        lock (_lock)
        {
            if (_start == long.MinValue) return 0;
            long t = _stop != long.MinValue ? _stop : _pauseStart != long.MinValue ? _pauseStart : _now();
            return Math.Max(0, t - _start - _pausedTotal);
        }
    }

    /// <summary>
    /// Maps a source timestamp to the recording timeline.
    /// <para>If the timestamp falls before the start or inside a completed pause, the result tells
    /// the caller how much of the data (from that timestamp) must be skipped so that the remainder
    /// begins exactly at the next active instant.</para>
    /// <para>Timestamps inside the current (ongoing) pause, or after stop, are <see cref="MapStatus.Drop"/>.</para>
    /// </summary>
    public MapResult Map(long sourceTime)
    {
        lock (_lock)
        {
            if (_start == long.MinValue) return MapResult.Dropped;

            long skip = 0;
            long s = sourceTime;
            if (s < _start)
            {
                skip = _start - s;
                s = _start;
            }

            long pausedBefore = 0;
            foreach (var (ps, pe) in _pauses)
            {
                if (s < ps) break;
                if (s < pe)
                {
                    skip += pe - s;
                    s = pe;
                }
                pausedBefore += pe - ps;
            }

            if (_pauseStart != long.MinValue && s >= _pauseStart) return MapResult.Dropped;
            if (_stop != long.MinValue && s >= _stop) return MapResult.Dropped;

            return new MapResult(MapStatus.Ok, s - _start - pausedBefore, skip);
        }
    }

    /// <summary>
    /// Maps a source timestamp to the recording timeline, clamping instead of dropping:
    /// before start → 0, inside a pause → the pause point. Used for video frames, where we only
    /// need to know "which frame is the newest one not later than time T".
    /// </summary>
    public long MapClamped(long sourceTime)
    {
        lock (_lock)
        {
            if (_start == long.MinValue || sourceTime <= _start) return 0;
            long s = sourceTime;
            long pausedBefore = 0;
            foreach (var (ps, pe) in _pauses)
            {
                if (s < ps) break;
                if (s < pe) s = ps; // inside a completed pause → the instant it began
                pausedBefore += Math.Min(s, pe) - ps;
                if (s == ps) break;
            }
            if (_pauseStart != long.MinValue && s > _pauseStart) s = _pauseStart;
            if (_stop != long.MinValue && s > _stop) s = _stop;
            return Math.Max(0, s - _start - pausedBefore);
        }
    }
}

public enum MapStatus { Ok, Drop }

public readonly record struct MapResult(MapStatus Status, long RecordingTime, long SkipHns)
{
    public static readonly MapResult Dropped = new(MapStatus.Drop, 0, 0);
}
