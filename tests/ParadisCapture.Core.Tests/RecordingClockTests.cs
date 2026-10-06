using ParadisCapture.Core.Timing;

namespace ParadisCapture.Core.Tests;

public class RecordingClockTests
{
    private long _now;
    private RecordingClock NewClock() => new(() => _now);
    private const long S = RecordingClock.HnsPerSecond;

    [Fact]
    public void RecordingTimeAdvancesFromZero()
    {
        _now = 1000 * S;
        var c = NewClock();
        Assert.Equal(0, c.RecordingNow());
        c.Start();
        _now += 5 * S;
        Assert.Equal(5 * S, c.RecordingNow());
        Assert.Equal(new MapResult(MapStatus.Ok, 2 * S, 0), c.Map(1002 * S));
    }

    [Fact]
    public void PauseFreezesAndRemovesGap()
    {
        _now = 0;
        var c = NewClock();
        c.Start();
        _now = 10 * S; c.Pause();
        _now = 70 * S;
        Assert.Equal(10 * S, c.RecordingNow());         // frozen during pause
        Assert.Equal(MapStatus.Drop, c.Map(30 * S).Status); // ongoing pause → drop
        c.Resume();
        _now = 75 * S;
        Assert.Equal(15 * S, c.RecordingNow());         // no 60 s gap

        // Data from inside the completed pause is skipped up to the resume point.
        var m = c.Map(65 * S);
        Assert.Equal(MapStatus.Ok, m.Status);
        Assert.Equal(10 * S, m.RecordingTime);
        Assert.Equal(5 * S, m.SkipHns);

        // After the pause, times shift back by the pause length.
        Assert.Equal(12 * S, c.Map(72 * S).RecordingTime);
        // Before the pause, untouched.
        Assert.Equal(4 * S, c.Map(4 * S).RecordingTime);
    }

    [Fact]
    public void DataBeforeStartIsSkipped()
    {
        _now = 100 * S;
        var c = NewClock();
        c.Start();
        var m = c.Map(99 * S);
        Assert.Equal(MapStatus.Ok, m.Status);
        Assert.Equal(0, m.RecordingTime);
        Assert.Equal(1 * S, m.SkipHns);
    }

    [Fact]
    public void StopFreezesAndDropsLaterData()
    {
        _now = 0;
        var c = NewClock();
        c.Start();
        _now = 8 * S;
        Assert.Equal(8 * S, c.Stop());
        _now = 20 * S;
        Assert.Equal(8 * S, c.RecordingNow());
        Assert.Equal(MapStatus.Drop, c.Map(9 * S).Status);
        Assert.Equal(MapStatus.Ok, c.Map(7 * S).Status);
    }

    [Fact]
    public void StopWhilePausedEndsAtPausePoint()
    {
        _now = 0;
        var c = NewClock();
        c.Start();
        _now = 3 * S; c.Pause();
        _now = 50 * S;
        Assert.Equal(3 * S, c.Stop());
    }

    [Fact]
    public void MapClampedCollapsesPauses()
    {
        _now = 0;
        var c = NewClock();
        c.Start();
        _now = 10 * S; c.Pause();
        _now = 20 * S; c.Resume();
        Assert.Equal(0, c.MapClamped(-5 * S));
        Assert.Equal(5 * S, c.MapClamped(5 * S));
        Assert.Equal(10 * S, c.MapClamped(15 * S)); // inside pause → pause point
        Assert.Equal(12 * S, c.MapClamped(22 * S));
        _now = 30 * S; c.Pause();
        Assert.Equal(20 * S, c.MapClamped(35 * S)); // inside ongoing pause
    }

    [Fact]
    public void MultiplePausesAccumulate()
    {
        _now = 0;
        var c = NewClock();
        c.Start();
        for (int i = 0; i < 100; i++)
        {
            _now += 2 * S; c.Pause();
            _now += 1 * S; c.Resume();
        }
        _now += 1 * S;
        Assert.Equal(201 * S, c.RecordingNow());
        Assert.Equal(201 * S, c.Map(_now).RecordingTime);
    }
}
