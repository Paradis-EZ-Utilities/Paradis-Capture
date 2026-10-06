using NAudio.Dsp;
using ParadisCapture.Core.Timing;

namespace ParadisCapture.Core.Audio;

/// <summary>
/// Places one audio source's captured packets onto the 48 kHz recording timeline.
///
/// <para>Every packet carries the QPC time at which its first frame was captured. That time is
/// mapped through the <see cref="RecordingClock"/> to an output frame position. Packets are written
/// back-to-back while they stay close to where their timestamps say they belong; a large mismatch
/// (a silent gap in loopback capture, a device glitch, pause/resume) causes a re-sync to the
/// timestamp.</para>
///
/// <para>Small, slowly growing mismatches are the audio device's crystal running slightly fast or
/// slow relative to the system clock. Those are corrected continuously by nudging the resampling
/// ratio by a few hundred parts-per-million at most, which is inaudible. This is what keeps a
/// three-hour lecture in sync.</para>
/// </summary>
public sealed class AudioSourceTimeline
{
    public const int OutputSampleRate = 48_000;

    /// <summary>Mismatches beyond this are treated as a discontinuity and re-synced (100 ms).</summary>
    public const int ResyncThresholdFrames = OutputSampleRate / 10;

    /// <summary>Maximum rate correction (±0.2%, ~3.5 cents of pitch — inaudible).</summary>
    public const double MaxCorrection = 0.002;

    /// <summary>Correction per frame of smoothed error: 1 ms of error → +0.02% rate.</summary>
    public const double CorrectionGain = 0.0002 / 48.0;

    private const double ErrorSmoothing = 0.05;

    private readonly RecordingClock _clock;
    private readonly AudioRing _ring;
    private WdlResampler? _resampler;
    private int _inputRate;
    private bool _hasPosition;
    private long _writePosition;
    private double _smoothedError;
    private float[] _output = new float[4096];
    private long _resyncCount;

    public AudioSourceTimeline(RecordingClock clock, AudioRing ring)
    {
        _clock = clock;
        _ring = ring;
    }

    public AudioRing Ring => _ring;

    /// <summary>Number of hard re-syncs so far (diagnostics).</summary>
    public long ResyncCount => _resyncCount;

    /// <summary>Current smoothed timing error in output frames (diagnostics; positive = audio behind).</summary>
    public double SmoothedErrorFrames => _smoothedError;

    /// <summary>Next output frame position (diagnostics/tests).</summary>
    public long WritePosition => _writePosition;

    /// <summary>Forget continuity (device restarted, format changed, …). The next packet re-syncs.</summary>
    public void Reset()
    {
        _hasPosition = false;
        _resampler = null;
        _inputRate = 0;
    }

    /// <summary>
    /// Pushes one captured packet (interleaved stereo float at <paramref name="inputRate"/>).
    /// Called from the capture thread only.
    /// </summary>
    public void Push(ReadOnlySpan<float> stereo, int inputRate, long captureTimeHns, bool discontinuity = false)
    {
        int frames = stereo.Length / 2;
        if (frames == 0 || inputRate <= 0) return;

        var map = _clock.Map(captureTimeHns);
        if (map.Status == MapStatus.Drop)
        {
            _hasPosition = false;
            return;
        }

        if (map.SkipHns > 0)
        {
            long skipFrames = (map.SkipHns * inputRate + RecordingClock.HnsPerSecond - 1) / RecordingClock.HnsPerSecond;
            if (skipFrames >= frames)
            {
                _hasPosition = false;
                return;
            }
            stereo = stereo.Slice((int)skipFrames * 2);
            frames -= (int)skipFrames;
            discontinuity = true; // crossing a start/pause boundary
        }

        if (_resampler == null || inputRate != _inputRate)
        {
            _resampler = CreateResampler();
            _inputRate = inputRate;
            discontinuity = true;
        }

        long expected = map.RecordingTime * OutputSampleRate / RecordingClock.HnsPerSecond;
        long error = expected - _writePosition;

        // The mixer has already emitted everything below this point, so audio written there would
        // be thrown away. It shouldn't happen (the mixer deliberately stays behind), but a device
        // whose timestamps lag badly could cause it — losing audio outright is far worse than a
        // one-off jump, so re-sync to the earliest position that is still usable.
        long floor = _ring.ReadPosition;

        if (!_hasPosition || discontinuity || Math.Abs(error) > ResyncThresholdFrames || _writePosition < floor)
        {
            if (_hasPosition) _resyncCount++;
            _writePosition = Math.Max(expected, floor);
            _smoothedError = 0;
            _resampler.Reset();
            _hasPosition = true;
        }
        else
        {
            _smoothedError += ErrorSmoothing * (error - _smoothedError);
        }

        double correction = Math.Clamp(_smoothedError * CorrectionGain, -MaxCorrection, MaxCorrection);
        _resampler.SetRates(inputRate, OutputSampleRate * (1.0 + correction));

        int produced = Resample(stereo, frames, inputRate);
        if (produced > 0)
        {
            _ring.Write(_writePosition, _output.AsSpan(0, produced * 2));
            _writePosition += produced;
        }
    }

    private static WdlResampler CreateResampler()
    {
        var r = new WdlResampler();
        // Linear interpolation + 2-pass IIR anti-alias filter: the configuration NAudio uses for
        // its WdlResamplingSampleProvider. Cheap and clean for speech and general audio.
        r.SetMode(true, 2, false);
        r.SetFilterParms();
        r.SetFeedMode(true); // input-driven: we push whatever the device delivered
        return r;
    }

    private int Resample(ReadOnlySpan<float> stereo, int frames, int inputRate)
    {
        var rs = _resampler!;
        int maxOut = (int)Math.Ceiling(frames * (OutputSampleRate * (1 + MaxCorrection)) / inputRate) + 64;
        if (_output.Length < maxOut * 2) _output = new float[maxOut * 2];

        int needed = rs.ResamplePrepare(frames, 2, out float[] inBuffer, out int inOffset);
        int take = Math.Min(needed, frames);
        stereo.Slice(0, take * 2).CopyTo(inBuffer.AsSpan(inOffset, take * 2));
        return rs.ResampleOut(_output, 0, take, maxOut, 2);
    }
}
