using ParadisCapture.Core.Audio;
using ParadisCapture.Core.Timing;
using ParadisCapture.Core.Video;

namespace ParadisCapture.Core.Tests;

/// <summary>
/// Whole-pipeline simulations of the properties that matter most in a long recording: the audio
/// and video tracks stay the same length as the recording, memory use stays flat, and pausing
/// never leaves a gap.
/// </summary>
public class LongRecordingTests
{
    private const long S = RecordingClock.HnsPerSecond;

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void AudioAndVideoTracksEndTogetherAfterThreeHours(int fps)
    {
        long now = 0;
        var clock = new RecordingClock(() => now);
        var ring = new AudioRing(48000 * 4);
        var timeline = new AudioSourceTimeline(clock, ring);
        var mixer = new AudioMixer();
        mixer.AddInput(ring);
        var schedule = new FrameSchedule(fps);
        clock.Start();

        // The device runs 120 ppm fast, as a real one might: it delivers slightly more than
        // 48000 frames per real second, while its timestamps stay on the real clock.
        double deviceRate = 48000 * 1.00012;
        const int packetFrames = 960;
        var packet = new float[packetFrames * 2];
        long videoFrames = 0, audioFrames = 0;
        long lastVideoTimestamp = -1;
        long deviceFramesDelivered = 0;
        long totalHns = 3 * 3600 * S;

        // Step in 20 ms ticks: whatever audio the device produced, then the video frames due.
        for (long t = 0; t <= totalHns; t += S / 50)
        {
            now = t;
            while ((deviceFramesDelivered + packetFrames) / deviceRate * S <= t)
            {
                timeline.Push(packet, 48000, (long)(deviceFramesDelivered / deviceRate * S));
                deviceFramesDelivered += packetFrames;
            }

            while (schedule.NextTime <= clock.RecordingNow())
            {
                Assert.True(schedule.NextTime > lastVideoTimestamp, "video timestamps must strictly increase");
                lastVideoTimestamp = schedule.NextTime;
                videoFrames++;
                schedule.Advance();
            }

            mixer.EmitUpTo(clock.RecordingNow() - S / 4, (pcm, frames, ts, dur) => audioFrames += frames);
        }

        long duration = clock.Stop(totalHns);
        mixer.EmitUpTo(duration, (pcm, frames, ts, dur) => audioFrames += frames);

        Assert.Equal(3 * 3600 * S, duration);
        // Audio track length must match the recording length exactly (sample-accurate).
        Assert.Equal(3L * 3600 * 48000, audioFrames);
        // Video track: one frame per interval, within a frame of the ideal count.
        Assert.InRange(videoFrames, 3L * 3600 * fps - 1, 3L * 3600 * fps + 1);
        // Last video timestamp must land inside the recording, not past its end.
        Assert.InRange(lastVideoTimestamp, duration - 2 * S / fps, duration);
        // Nothing accumulated: the ring never overflowed and never fell behind.
        Assert.Equal(0, ring.DroppedOverflowFrames);
        Assert.Equal(0, ring.DroppedLateFrames);
        Assert.Equal(0, timeline.ResyncCount);
    }

    /// <summary>
    /// A device whose timestamps fall progressively behind the system clock is pathological, but
    /// it must not cause audio to disappear: the timeline re-syncs and keeps delivering sound.
    /// </summary>
    [Fact]
    public void AudioSurvivesTimestampsThatLagTheSystemClock()
    {
        long now = 0;
        var clock = new RecordingClock(() => now);
        var ring = new AudioRing(48000 * 4);
        var timeline = new AudioSourceTimeline(clock, ring);
        var mixer = new AudioMixer();
        mixer.AddInput(ring);
        clock.Start();

        var packet = Enumerable.Repeat(0.25f, 960 * 2).ToArray();
        long delivered = 0;
        long nonSilentBlocks = 0, blocks = 0;

        for (long t = 0; t <= 1800 * S; t += S / 50)
        {
            now = t;
            // 48000 frames per second delivered, but timestamped as if the device ran 120 ppm slow.
            timeline.Push(packet, 48000, (long)(delivered / (48000 * 1.00012) * S));
            delivered += 960;
            mixer.EmitUpTo(clock.RecordingNow() - S / 4, (pcm, frames, ts, dur) =>
            {
                blocks++;
                if (pcm.Span[frames]!= 0) nonSilentBlocks++;
            });
        }

        // Audio keeps flowing throughout: no long silent stretches, and the drops are bounded.
        Assert.True(blocks > 100);
        Assert.True(nonSilentBlocks > blocks * 0.95, $"only {nonSilentBlocks}/{blocks} blocks carried audio");
        Assert.True(ring.DroppedLateFrames < 48000, $"{ring.DroppedLateFrames} frames lost");
    }

    [Fact]
    public void PauseRemovesTheGapFromBothTracks()
    {
        long now = 0;
        var clock = new RecordingClock(() => now);
        var ring = new AudioRing(48000 * 4);
        var timeline = new AudioSourceTimeline(clock, ring);
        var mixer = new AudioMixer();
        mixer.AddInput(ring);
        var schedule = new FrameSchedule(30);
        clock.Start();

        long audioFrames = 0, videoFrames = 0;
        var packet = new float[480 * 2];

        // 10 s recording, 10 s paused, 10 s recording.
        for (long t = 0; t <= 30 * S; t += S / 100)
        {
            now = t;
            if (t == 10 * S) clock.Pause();
            if (t == 20 * S) clock.Resume();

            timeline.Push(packet, 48000, t);
            while (schedule.NextTime <= clock.RecordingNow()) { videoFrames++; schedule.Advance(); }
            mixer.EmitUpTo(clock.RecordingNow() - S / 4, (pcm, n, ts, dur) => audioFrames += n);
        }

        long duration = clock.Stop(30 * S);
        mixer.EmitUpTo(duration, (pcm, n, ts, dur) => audioFrames += n);

        Assert.Equal(20 * S, duration);            // the 10 s pause is gone
        Assert.Equal(20 * 48000, audioFrames);     // audio is 20 s, not 30
        Assert.InRange(videoFrames, 20 * 30 - 1, 20 * 30 + 1);
    }

    [Fact]
    public void RingMemoryIsBoundedWhenTheMixerStalls()
    {
        long now = 0;
        var clock = new RecordingClock(() => now);
        var ring = new AudioRing(48000); // 1 second of capacity
        var timeline = new AudioSourceTimeline(clock, ring);
        clock.Start();

        // Push 60 s of audio without ever mixing: the ring must discard, not grow.
        var packet = new float[4800 * 2];
        for (int i = 0; i < 600; i++)
        {
            now = (i + 1) * S / 10;
            timeline.Push(packet, 48000, i * S / 10);
        }

        Assert.True(ring.DroppedOverflowFrames > 0);
        Assert.Equal(48000, ring.CapacityFrames);
        // And the mixer can still carry on from where it is, with no exception.
        var mixer = new AudioMixer();
        mixer.AddInput(ring);
        long frames = 0;
        mixer.EmitUpTo(60 * S, (pcm, n, ts, dur) => frames += n);
        Assert.Equal(60 * 48000, frames);
    }

    [Fact]
    public void FortyFourPointOneKilohertzMicrophoneIsResampledToTheRightLength()
    {
        long now = 0;
        var clock = new RecordingClock(() => now);
        var ring = new AudioRing(48000 * 4);
        var timeline = new AudioSourceTimeline(clock, ring);
        clock.Start();

        var packet = new float[441 * 2]; // 10 ms at 44.1 kHz
        for (int i = 0; i < 6000; i++)   // 60 seconds
        {
            now = (i + 1) * S / 100;
            timeline.Push(packet, 44100, i * S / 100);
        }

        // 60 s of 44.1 kHz input must become ~60 s of 48 kHz output (within a millisecond).
        Assert.InRange(timeline.WritePosition, 60 * 48000 - 48, 60 * 48000 + 48);
        Assert.Equal(0, timeline.ResyncCount);
    }
}
