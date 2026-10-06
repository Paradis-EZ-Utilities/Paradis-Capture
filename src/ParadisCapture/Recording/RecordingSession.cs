using SharpGen.Runtime;
using ParadisCapture.Audio;
using ParadisCapture.Capture;
using ParadisCapture.Core.Audio;
using ParadisCapture.Core.Timing;
using ParadisCapture.Core.Util;
using ParadisCapture.Core.Video;
using ParadisCapture.Encoding;
using ParadisCapture.Interop;
using ParadisCapture.Services;
using ParadisCapture.Video;
using Vortice.MediaFoundation;

namespace ParadisCapture.Recording;

public enum RecordingState { Idle, Starting, Recording, Paused, Finishing, Finished, Failed }

public sealed record RecordingOptions(
    CaptureTarget Target,
    string SaveFolder,
    bool SystemAudio,
    bool Microphone,
    string? MicrophoneDeviceId,
    string? OutputDeviceId,
    int Fps,
    VideoQuality Quality,
    bool CaptureCursor);

public sealed record RecordingResult(string FilePath, TimeSpan Duration, long FileSizeBytes);

/// <summary>
/// One recording, from Record to Stop. Owns the capture, audio and encoding pipeline and the
/// threads that drive them. Every public member is safe to call from the UI thread; all callbacks
/// are raised on background threads (the UI marshals them).
/// </summary>
public sealed class RecordingSession : IDisposable
{
    private const int MinFreeMegabytes = 300;
    private const long MixerLagHns = 2_500_000; // 250 ms

    private readonly RecordingOptions _options;
    private readonly RecordingClock _clock = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<WasapiCaptureSource> _audioSources = new();
    private readonly AudioMixer _mixer = new();
    private readonly object _stateLock = new();

    private GraphicsDevice? _gfx;
    private ScreenCaptureSource? _capture;
    private VideoConverter? _converter;
    private MediaWriter? _writer;
    private VideoFrameEncoder? _encoder;
    private FrameSchedule? _schedule;
    private Thread? _videoThread;
    private Thread? _audioThread;
    private string _inProgressPath = "";
    private string _finalPath = "";
    private int _state = (int)RecordingState.Idle;
    private volatile bool _stopRequested;
    private Exception? _failure;
    private long _skippedFrames;
    private long _nextSpaceCheck;
    private bool _lowSpaceWarned;
    private bool _mediaFoundationStarted;

    public RecordingSession(RecordingOptions options) => _options = options;

    public RecordingState State => (RecordingState)Volatile.Read(ref _state);

    public PixelSize OutputSize { get; private set; }

    public string TargetDescription => _options.Target.Description;

    /// <summary>Where the finished file will be written.</summary>
    public string FinalPath => _finalPath;

    /// <summary>Recording elapsed time (frozen while paused).</summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(_clock.RecordingNow());

    /// <summary>Fired once when the recording ends on its own or after <see cref="StopAsync"/>.</summary>
    public event Action<RecordingResult>? Completed;

    /// <summary>Fired once if the recording fails. The message is user-facing.</summary>
    public event Action<string>? Failed;

    /// <summary>A non-fatal problem the user should know about (mic disconnected, …).</summary>
    public event Action<string>? Warning;

    /// <summary>Progress of the final "saving" step, 0..1.</summary>
    public event Action<double>? SaveProgress;

    // ---------------------------------------------------------------- start

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, (int)RecordingState.Starting, (int)RecordingState.Idle) != (int)RecordingState.Idle)
            throw new InvalidOperationException("This session was already started.");

        try
        {
            StartCore();
            Volatile.Write(ref _state, (int)RecordingState.Recording);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _state, (int)RecordingState.Failed);
            CleanUpPipeline();
            MediaWriter.TryDelete(_inProgressPath);
            ShutdownMediaFoundation();
            throw Translate(ex, starting: true);
        }
    }

    private void StartCore()
    {
        PrepareOutputFile();

        MediaFactory.MFStartup().CheckError();
        _mediaFoundationStarted = true;

        _gfx = GraphicsDevice.Create();
        Log.Info($"Recording on {_gfx.AdapterName}");

        var (item, crop) = _options.Target.Resolve();
        _capture = new ScreenCaptureSource(_gfx, item, crop, _options.CaptureCursor, _options.Fps);
        _capture.ItemClosed += OnCaptureItemClosed;
        _capture.Failed += OnCaptureFailed;

        var frameSize = _capture.FrameSize;
        OutputSize = crop is { } c
            ? new PixelSize(VideoGeometry.MakeEven(c.Width), VideoGeometry.MakeEven(c.Height))
            : VideoGeometry.OutputSizeFor(frameSize.Width, frameSize.Height);

        int bitrate = EncoderSettings.BitrateFor(OutputSize.Width, OutputSize.Height, _options.Fps, _options.Quality);
        int audioBitrate = EncoderSettings.AudioBitrateFor(_options.Quality);
        var format = new VideoStreamFormat(OutputSize.Width, OutputSize.Height, _options.Fps, bitrate,
            EncoderSettings.GopSizeFor(_options.Fps, _options.Quality), audioBitrate);
        Log.Info($"Output {OutputSize} @ {_options.Fps} fps, {_options.Quality}: video {bitrate / 1_000_000.0:F2} Mbit/s, " +
                 $"audio {audioBitrate / 1000} kbit/s, keyframe every {format.GopSize} frames, source {frameSize}, crop {crop?.ToString() ?? "none"}");

        bool withAudio = _options.SystemAudio || _options.Microphone;
        _writer = CreateWriter(format, withAudio);
        _converter = new VideoConverter(_gfx, OutputSize.Width, OutputSize.Height, _options.Fps);
        _encoder = new VideoFrameEncoder(_gfx, _writer, _converter);
        _schedule = new FrameSchedule(_options.Fps);

        if (withAudio) SetUpAudio();

        // Give capture and audio a moment to produce their first data, then start the clock: the
        // first video frame and the first audio sample then both land at (or very near) time zero.
        _capture.Start();
        _capture.WaitForFirstFrame(TimeSpan.FromMilliseconds(700));
        foreach (var s in _audioSources) s.WaitUntilStarted(TimeSpan.FromMilliseconds(700));

        _capture.AttachClock(_clock);
        _clock.Start();

        _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "Video encode", Priority = ThreadPriority.AboveNormal };
        _videoThread.SetApartmentState(ApartmentState.MTA);
        _videoThread.Start();

        if (withAudio)
        {
            _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "Audio encode", Priority = ThreadPriority.AboveNormal };
            _audioThread.SetApartmentState(ApartmentState.MTA);
            _audioThread.Start();
        }

        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED | NativeMethods.ES_DISPLAY_REQUIRED);
    }

    private MediaWriter CreateWriter(VideoStreamFormat format, bool withAudio)
    {
        // Try the zero-copy GPU path first, then progressively simpler paths. Some drivers only
        // accept system-memory samples, and Windows N editions without the Media Feature Pack
        // have no H.264 encoder at all (the last attempt then fails with a clear message).
        var modes = new[] { EncoderMode.GpuTextures, EncoderMode.SystemMemory, EncoderMode.Software };
        Exception? last = null;
        foreach (var mode in modes)
        {
            try
            {
                var writer = MediaWriter.Create(_inProgressPath, format, withAudio, mode, _gfx, fragmented: true);
                if (mode != EncoderMode.GpuTextures) Log.Warn($"Using the {mode} encoder path (hardware texture encoding was unavailable)");
                return writer;
            }
            catch (Exception ex)
            {
                Log.Warn($"Encoder path {mode} unavailable", ex);
                last = ex;
            }
        }
        throw last!;
    }

    private void PrepareOutputFile()
    {
        string folder = _options.SaveFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            throw new RecorderException($"The save folder couldn't be created:\n{folder}\n\nChoose a different folder in Settings.", ex);
        }

        if (NativeMethods.GetDiskFreeSpaceEx(folder, out ulong freeBytes, out _, out _) && freeBytes < (ulong)MinFreeMegabytes * 1024 * 1024)
        {
            throw new RecorderException($"There's only {freeBytes / (1024 * 1024)} MB free where recordings are saved. Free up some space or choose another folder.");
        }

        _finalPath = FileNaming.MakeUnique(folder, FileNaming.BuildFileName(_options.Target.NameHint, DateTime.Now));
        _inProgressPath = FileNaming.InProgressPathFor(_finalPath);
    }

    private void SetUpAudio()
    {
        // ~8 s of buffer per source: far more than any scheduling hiccup needs, ~3 MB each.
        if (_options.SystemAudio)
        {
            AddAudioSource(_options.OutputDeviceId, loopback: true);
        }
        if (_options.Microphone)
        {
            AddAudioSource(_options.MicrophoneDeviceId, loopback: false);
        }
    }

    private void AddAudioSource(string? deviceId, bool loopback)
    {
        var ring = new AudioRing(AudioMixer.SampleRate * 8);
        var timeline = new AudioSourceTimeline(_clock, ring);
        // Mixing two sources at full gain can clip; -3 dB each keeps headroom while staying loud.
        _mixer.AddInput(ring, _options.SystemAudio && _options.Microphone ? 0.71f : 1f);

        var source = new WasapiCaptureSource(deviceId, loopback, timeline);
        source.Problem += m => Warning?.Invoke(m);
        source.Recovered += m => Warning?.Invoke(m);
        _audioSources.Add(source);
        source.Start();
    }

    // ---------------------------------------------------------------- pause / resume / stop

    public void Pause()
    {
        lock (_stateLock)
        {
            if (State != RecordingState.Recording) return;
            _clock.Pause();
            Volatile.Write(ref _state, (int)RecordingState.Paused);
        }
        Log.Info($"Paused at {Elapsed:hh\\:mm\\:ss}");
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            if (State != RecordingState.Paused) return;
            _clock.Resume();
            Volatile.Write(ref _state, (int)RecordingState.Recording);
        }
        Log.Info($"Resumed at {Elapsed:hh\\:mm\\:ss}");
    }

    /// <summary>
    /// Stops recording, flushes the encoders and converts the file to a regular MP4.
    /// Returns when the file is ready (or raises <see cref="Failed"/>).
    /// </summary>
    public Task StopAsync() => Task.Run(Stop);

    private void Stop()
    {
        lock (_stateLock)
        {
            var state = State;
            if (state is RecordingState.Finishing or RecordingState.Finished) return;
            if (state is RecordingState.Idle or RecordingState.Failed)
            {
                CleanUpPipeline();
                return;
            }
            Volatile.Write(ref _state, (int)RecordingState.Finishing);
        }

        long duration = _clock.Stop();
        _stopRequested = true;
        Log.Info($"Stopping after {TimeSpan.FromTicks(duration):hh\\:mm\\:ss} (video frames skipped: {_skippedFrames}, encoder drops: {_encoder?.DroppedFrames ?? 0})");

        // The encoder can block inside Media Foundation, so allow plenty of time; if a thread
        // really is wedged we must not then dispose the objects it is still using.
        bool threadsExited = (_videoThread?.Join(TimeSpan.FromSeconds(30)) ?? true)
                           & (_audioThread?.Join(TimeSpan.FromSeconds(30)) ?? true);
        if (!threadsExited) Log.Error("An encoding thread did not stop; leaving its resources to the process to reclaim");
        foreach (var s in _audioSources) s.Stop();

        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);

        try
        {
            _writer?.Finish();
        }
        catch (Exception ex)
        {
            Log.Error("Finalizing the recording failed", ex);
            _failure ??= ex;
        }

        CleanUpPipeline(disposeEncoder: threadsExited);

        if (_failure != null && !File.Exists(_inProgressPath))
        {
            Fail(_failure);
            return;
        }

        try
        {
            string path = FinishFile(duration);
            var info = new FileInfo(path);
            Volatile.Write(ref _state, (int)RecordingState.Finished);
            Log.Info($"Saved {path} ({info.Length / (1024.0 * 1024.0):F1} MB)");
            ShutdownMediaFoundation();

            if (_failure != null)
            {
                Warning?.Invoke($"{Translate(_failure, starting: false).Message}\n\nWhat was recorded before that was saved.");
            }
            Completed?.Invoke(new RecordingResult(path, TimeSpan.FromTicks(duration), info.Length));
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// Converts the fragmented MP4 written during recording into a regular MP4. If that fails,
    /// the fragmented file is kept under the final name — it plays, it just lacks a global index.
    /// </summary>
    private string FinishFile(long durationHns)
    {
        if (!File.Exists(_inProgressPath))
        {
            throw new RecorderException("The recording file is missing, so nothing could be saved. The log file has details.");
        }

        if (_writer?.Fragmented != true)
        {
            File.Move(_inProgressPath, _finalPath, overwrite: false);
            return _finalPath;
        }

        // Converting writes a second copy alongside the first, so it needs as much free space
        // again. Without it, keep the (playable) fragmented file rather than filling the disk.
        long size = new FileInfo(_inProgressPath).Length;
        if (NativeMethods.GetDiskFreeSpaceEx(_options.SaveFolder, out ulong free, out _, out _) && (long)free < size + 64 * 1024 * 1024)
        {
            Log.Warn($"Skipping the final conversion: {free / (1024 * 1024)} MB free, {size / (1024 * 1024)} MB needed");
            File.Move(_inProgressPath, _finalPath, overwrite: false);
            Warning?.Invoke("There wasn't enough free disk space to finish converting the file, so it was saved in a slightly unusual MP4 layout. It plays normally.");
            return _finalPath;
        }

        try
        {
            SaveProgress?.Invoke(0);
            Remuxer.Remux(_inProgressPath, _finalPath, durationHns, new Progress<double>(p => SaveProgress?.Invoke(p)), _cancel.Token);
            SaveProgress?.Invoke(1);
            MediaWriter.TryDelete(_inProgressPath);
            return _finalPath;
        }
        catch (Exception ex)
        {
            Log.Error("Converting the recording to a standard MP4 failed; keeping the fragmented file", ex);
            MediaWriter.TryDelete(_finalPath);
            try
            {
                File.Move(_inProgressPath, _finalPath, overwrite: false);
                Warning?.Invoke("The recording was saved, but in a slightly unusual MP4 layout. It plays normally; some older editors may need it converted.");
                return _finalPath;
            }
            catch (Exception moveEx)
            {
                Log.Error("Could not rename the recording either", moveEx);
                Warning?.Invoke($"The recording was saved as:\n{_inProgressPath}");
                return _inProgressPath;
            }
        }
    }

    // ---------------------------------------------------------------- loops

    private void VideoLoop()
    {
        var schedule = _schedule!;
        var capture = _capture!;
        var encoder = _encoder!;
        NativeMethods.timeBeginPeriod(1);
        IntPtr timer = NativeMethods.CreateWaitableTimerExW(IntPtr.Zero, null, NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, NativeMethods.TIMER_ALL_ACCESS);
        using var waitHandle = timer != IntPtr.Zero ? new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(timer, true) } : null;

        try
        {
            while (!_stopRequested)
            {
                if (State == RecordingState.Paused)
                {
                    Thread.Sleep(20);
                    continue;
                }

                long now = _clock.RecordingNow();
                CheckFreeSpace(now);
                long due = schedule.NextTime;
                if (now < due)
                {
                    Sleep(waitHandle, due - now);
                    continue;
                }

                long skipped = schedule.SkipIfBehind(now);
                if (skipped > 0)
                {
                    _skippedFrames += skipped;
                    Log.Warn($"Encoding fell behind; skipped {skipped} frame(s)");
                }

                long timestamp = schedule.NextTime;
                long frameDuration = schedule.NextDuration;
                if (encoder.EncodeFrame(capture, timestamp, frameDuration))
                {
                    schedule.Advance();
                }
                else
                {
                    schedule.Advance(); // dropped by a saturated encoder: keep the timeline honest
                    Thread.Sleep(1);
                }
            }

            // One last frame at the exact end time so the video track isn't short.
            long end = _clock.RecordingNow();
            if (schedule.NextTime <= end) encoder.EncodeFrame(capture, schedule.NextTime, schedule.NextDuration);
        }
        catch (Exception ex)
        {
            OnPipelineFailure(ex);
        }
        finally
        {
            NativeMethods.timeEndPeriod(1);
        }
    }

    /// <summary>
    /// Watches free disk space every few seconds: warns the user once when it gets low, and stops
    /// the recording cleanly before the disk actually fills (a disk-full write mid-file is messier).
    /// </summary>
    private void CheckFreeSpace(long recordingNow)
    {
        if (recordingNow < _nextSpaceCheck) return;
        _nextSpaceCheck = recordingNow + 5 * RecordingClock.HnsPerSecond;

        if (!NativeMethods.GetDiskFreeSpaceEx(_options.SaveFolder, out ulong free, out _, out _)) return;
        long freeMb = (long)(free / (1024 * 1024));

        if (freeMb < 100)
        {
            Log.Warn($"Only {freeMb} MB left; stopping the recording to keep the file intact");
            Warning?.Invoke("The disk is nearly full, so the recording was stopped and saved.");
            _ = StopAsync();
        }
        else if (freeMb < MinFreeMegabytes && !_lowSpaceWarned)
        {
            _lowSpaceWarned = true;
            Warning?.Invoke($"Only {freeMb} MB of disk space left. The recording will stop automatically if it runs out.");
        }
    }

    private static void Sleep(ManualResetEvent? timer, long hns)
    {
        long ms = hns / 10_000;
        if (timer != null)
        {
            long due = -hns; // negative = relative
            if (NativeMethods.SetWaitableTimer(timer.SafeWaitHandle.DangerousGetHandle(), ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                timer.WaitOne((int)Math.Max(1, ms + 15));
                return;
            }
        }
        Thread.Sleep((int)Math.Clamp(ms, 1, 20));
    }

    private void AudioLoop()
    {
        var writer = _writer!;
        try
        {
            while (!_stopRequested)
            {
                // Stay 250 ms behind the clock, comfortably more than one audio packet plus the
                // capture thread's poll interval, so every packet lands in its slot before the
                // mixer reaches it. This is encoder latency only; it does not shift A/V sync.
                EmitAudio(_clock.RecordingNow() - MixerLagHns);
                Thread.Sleep(20);
            }
            EmitAudio(_clock.RecordingNow()); // flush everything up to the stop time
        }
        catch (Exception ex)
        {
            OnPipelineFailure(ex);
        }

        void EmitAudio(long upTo)
        {
            if (upTo <= 0) return;
            _mixer.EmitUpTo(upTo, (pcm, frames, timestamp, duration) => writer.WriteAudio(pcm.Span, timestamp, duration));
        }
    }

    // ---------------------------------------------------------------- failure handling

    private void OnCaptureItemClosed()
    {
        if (_stopRequested || State is RecordingState.Finishing or RecordingState.Finished) return;
        Log.Info("Capture target closed; stopping the recording");
        Warning?.Invoke("What you were recording was closed, so the recording was stopped and saved.");
        _ = StopAsync();
    }

    private void OnCaptureFailed(Exception ex) => OnPipelineFailure(ex);

    /// <summary>
    /// A pipeline error. The first one is remembered and the recording stops; whatever was already
    /// written stays on disk.
    /// </summary>
    private void OnPipelineFailure(Exception ex)
    {
        if (_stopRequested) return;
        if (Interlocked.CompareExchange(ref _failure, ex, null) != null) return;
        Log.Error("Recording pipeline error", ex);
        _ = StopAsync();
    }

    private void Fail(Exception ex)
    {
        Volatile.Write(ref _state, (int)RecordingState.Failed);
        var translated = Translate(ex, starting: false);
        Log.Error("Recording failed", ex);
        ShutdownMediaFoundation();
        Failed?.Invoke(translated.Message);
    }

    private static Exception Translate(Exception ex, bool starting)
    {
        if (ex is RecorderException) return ex;
        if (ex is OperationCanceledException) return new RecorderException("The recording was cancelled.", ex);
        if (ex is UnauthorizedAccessException or IOException or SharpGenException or System.Runtime.InteropServices.COMException)
        {
            int hr = ex.HResult;
            return new RecorderException(starting ? FriendlyErrors.ForStartFailure(hr) : FriendlyErrors.ForWriteFailure(hr), ex);
        }
        return new RecorderException(
            starting
                ? "The recording couldn't be started. The log file has the technical details."
                : "The recording stopped because of an unexpected error. Anything already recorded was saved.",
            ex);
    }

    // ---------------------------------------------------------------- teardown

    private void CleanUpPipeline(bool disposeEncoder = true)
    {
        if (_capture != null)
        {
            _capture.ItemClosed -= OnCaptureItemClosed;
            _capture.Failed -= OnCaptureFailed;
        }
        foreach (var s in _audioSources) s.Dispose();
        _audioSources.Clear();

        _capture?.Dispose();
        _capture = null;

        if (!disposeEncoder)
        {
            // A thread is still inside the encoder. Drop our references and let the process
            // clean up at exit: freeing these now would crash that thread.
            _encoder = null;
            _converter = null;
            _writer = null;
            _gfx = null;
            return;
        }

        _encoder?.Dispose();
        _encoder = null;
        _converter?.Dispose();
        _converter = null;
        _writer?.Dispose();
        _writer = null;
        _gfx?.Dispose();
        _gfx = null;
    }

    private void ShutdownMediaFoundation()
    {
        if (!_mediaFoundationStarted) return;
        _mediaFoundationStarted = false;
        try { MediaFactory.MFShutdown(); } catch (Exception ex) { Log.Warn("MFShutdown", ex); }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _stopRequested = true;
        try
        {
            if (State is RecordingState.Recording or RecordingState.Paused) Stop();
        }
        catch (Exception ex)
        {
            Log.Warn("Stopping during dispose", ex);
        }
        CleanUpPipeline();
        ShutdownMediaFoundation();
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        _cancel.Dispose();
    }
}
