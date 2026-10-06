using ParadisCapture.Core.Audio;
using ParadisCapture.Core.Timing;

namespace ParadisCapture.Core.Tests;

/// <summary>
/// Simulates audio devices whose clocks are not exactly 48 kHz (real devices are off by tens to
/// hundreds of ppm) and checks the audio stays locked to the recording timeline over hours.
/// </summary>
public class AudioSyncTests
{
    private const long S = RecordingClock.HnsPerSecond;

    private sealed class Sim
    {
        public long Now;
        public readonly RecordingClock Clock;
        public readonly AudioRing Ring = new(48000 * 4);
        public readonly AudioSourceTimeline Timeline;
        public readonly AudioMixer Mixer = new();
        public long MaxDiffWithinBlock; // for continuity checks
        public float LastSample = float.NaN;
        public double MaxJump;
        public long EmittedFrames;

        public Sim()
        {
            Clock = new RecordingClock(() => Now);
            Timeline = new AudioSourceTimeline(Clock, Ring);
            Mixer.AddInput(Ring);
        }

        public void Mix(long upTo, bool trackJumps = false)
        {
            Mixer.EmitUpTo(upTo, (pcm, frames, ts, dur) =>
            {
                Assert.Equal(AudioMixer.FramesToHns(EmittedFrames), ts);
                EmittedFrames += frames;
                if (!trackJumps) return;
                var span = pcm.Span;
                for (int i = 0; i < frames; i++)
                {
                    float v = span[i * 2] / 32767f;
                    if (!float.IsNaN(LastSample)) MaxJump = Math.Max(MaxJump, Math.Abs(v - LastSample));
                    LastSample = v;
                }
            });
        }
    }

    [Theory]
    [InlineData(48000, 150.0)]   // device clock fast by 150 ppm
    [InlineData(48000, -200.0)]  // device clock slow by 200 ppm
    [InlineData(44100, 80.0)]    // 44.1 kHz device needing conversion
    public void StaysInSyncForThreeHours(int deviceRate, double ppm)
    {
        var sim = new Sim();
        sim.Now = 5000 * S;
        sim.Clock.Start();
        long start = sim.Now;

        double trueRate = deviceRate * (1 + ppm / 1e6); // frames per real second
        int packetFrames = deviceRate / 50;              // 20 ms packets in device time
        var packet = new float[packetFrames * 2];
        var rng = new Random(1);
        long deviceFrames = 0;
        double phase = 0;
        double maxAbsErrorMs = 0;
        long totalSeconds = 3 * 3600;

        while (true)
        {
            double captureSeconds = deviceFrames / trueRate;
            if (captureSeconds > totalSeconds) break;
            long captureTime = start + (long)(captureSeconds * S);

            for (int i = 0; i < packetFrames; i++)
            {
                float v = (float)(0.5 * Math.Sin(phase));
                phase += 2 * Math.PI * 440 / deviceRate;
                packet[i * 2] = packet[i * 2 + 1] = v;
            }

            // WASAPI timestamps have a little jitter.
            long jitter = (long)((rng.NextDouble() - 0.5) * 0.0006 * S);
            sim.Now = captureTime + (long)(packetFrames / trueRate * S) + 2 * S / 1000;
            sim.Timeline.Push(packet, deviceRate, captureTime + jitter);
            deviceFrames += packetFrames;

            // Mixer runs ~300 ms behind real time.
            sim.Mix(sim.Clock.RecordingNow() - 3 * S / 10, trackJumps: captureSeconds > totalSeconds - 60);

            // Compare where the next frame is written to where its timestamp says it belongs.
            long expected = (long)((deviceFrames / trueRate) * 48000);
            double errMs = Math.Abs(sim.Timeline.WritePosition - expected) / 48.0;
            if (captureSeconds > 60) maxAbsErrorMs = Math.Max(maxAbsErrorMs, errMs);
        }

        Assert.Equal(0, sim.Timeline.ResyncCount);
        Assert.True(maxAbsErrorMs < 3.0, $"timing error reached {maxAbsErrorMs:F2} ms");
        Assert.Equal(0, sim.Ring.DroppedOverflowFrames);
        // A 440 Hz sine at 0.5 amplitude changes by at most ~0.03 per sample; anything larger is a click.
        Assert.True(sim.MaxJump < 0.06, $"discontinuity of {sim.MaxJump:F3} detected");
    }

    [Fact]
    public void LoopbackSilenceGapIsFilledAndAudioResumesAtCorrectTime()
    {
        var sim = new Sim();
        sim.Now = 0;
        sim.Clock.Start();
        var packet = Enumerable.Repeat(0.25f, 480 * 2).ToArray();
        var collected = new List<short>();
        void Mix(long t) => sim.Mixer.EmitUpTo(t, (pcm, frames, ts, dur) => collected.AddRange(pcm.Span.ToArray()));

        // 1 second of audio, then nothing for 5 seconds (nothing playing), then 1 more second.
        for (int i = 0; i < 100; i++)
        {
            sim.Now = (i + 1) * S / 100;
            sim.Timeline.Push(packet, 48000, i * S / 100);
            Mix(sim.Now - 3 * S / 10);
        }
        for (int i = 100; i < 700; i++)
        {
            sim.Now = (i + 1) * S / 100;
            if (i >= 600) sim.Timeline.Push(packet, 48000, i * S / 100);
            Mix(sim.Now - 3 * S / 10);
        }
        sim.Now = 8 * S;
        Mix(8 * S);
        Assert.Equal(8 * 48000 * 2, collected.Count);

        short Sample(double seconds) => collected[(int)(seconds * 48000) * 2];
        Assert.NotEqual(0, Sample(0.5));
        Assert.Equal(0, Sample(3.0));       // the gap is silence
        Assert.NotEqual(0, Sample(6.5));    // resumes exactly at 6 s
        Assert.Equal(0, Sample(5.98));
        Assert.Equal(0, Sample(7.5));
    }

    [Fact]
    public void PausedAudioIsDroppedAndResumedAudioIsContiguous()
    {
        var sim = new Sim();
        sim.Now = 0;
        sim.Clock.Start();
        var loud = Enumerable.Repeat(0.25f, 480 * 2).ToArray();
        var marker = Enumerable.Repeat(-0.5f, 480 * 2).ToArray();

        for (int i = 0; i < 300; i++) // 3 s of audio, paused at 2 s → 4 s (wall), marker after resume
        {
            long t = i * S / 100;
            sim.Now = t + S / 100;
            if (i == 200) sim.Clock.Pause(2 * S);
            if (i == 250) { sim.Clock.Resume(25 * S / 10); }
            sim.Timeline.Push(i >= 250 ? marker : loud, 48000, t);
        }
        long total = sim.Clock.Stop(3 * S);
        Assert.Equal(25 * S / 10, total); // 3 s wall minus 0.5 s pause

        var collected = new List<short>();
        sim.Mixer.EmitUpTo(total, (pcm, frames, ts, dur) => collected.AddRange(pcm.Span.ToArray()));
        short Sample(double seconds) => collected[(int)(seconds * 48000) * 2];
        Assert.True(Sample(1.99) > 0);   // before pause: loud
        Assert.True(Sample(2.01) < 0);   // immediately after the pause point: post-resume marker
        Assert.True(Sample(2.45) < 0);
    }

    [Fact]
    public void TwoSourcesAreMixed()
    {
        long now = 0;
        var clock = new RecordingClock(() => now);
        clock.Start();
        var a = new AudioRing(48000);
        var b = new AudioRing(48000);
        new AudioSourceTimeline(clock, a).Push(Enumerable.Repeat(0.2f, 4800 * 2).ToArray(), 48000, 0);
        new AudioSourceTimeline(clock, b).Push(Enumerable.Repeat(0.3f, 4800 * 2).ToArray(), 48000, 0);
        var mixer = new AudioMixer();
        mixer.AddInput(a);
        mixer.AddInput(b);
        short mid = 0;
        mixer.EmitUpTo(S / 10, (pcm, frames, ts, dur) => mid = pcm.Span[2400 * 2]);
        Assert.InRange(mid / 32767.0, 0.48, 0.52);
    }

    [Fact]
    public void SoftClipNeverOverflows()
    {
        Assert.Equal(0, AudioMixer.ToPcm16(0));
        Assert.InRange(AudioMixer.ToPcm16(0.5f), 16380, 16386);
        Assert.InRange(AudioMixer.ToPcm16(3f), 30000, 32767);
        Assert.InRange(AudioMixer.ToPcm16(-30f), -32768, -30000);
        Assert.True(AudioMixer.ToPcm16(1.2f) > AudioMixer.ToPcm16(1.0f)); // monotonic
        Assert.Equal(0, AudioMixer.ToPcm16(float.NaN));
    }

    [Fact]
    public void MixerTimestampsAreExactAndContiguous()
    {
        var mixer = new AudioMixer();
        long expectedTs = 0;
        long frames = 0;
        for (int i = 1; i <= 1000; i++)
        {
            mixer.EmitUpTo(i * 333_333L, (pcm, n, ts, dur) =>
            {
                Assert.Equal(expectedTs, ts);
                expectedTs = ts + dur;
                frames += n;
            });
        }
        Assert.Equal(AudioMixer.HnsToFrames(1000 * 333_333L), frames);
    }
}
