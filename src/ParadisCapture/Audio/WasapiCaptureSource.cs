using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ParadisCapture.Core.Audio;
using ParadisCapture.Core.Timing;
using ParadisCapture.Core.Util;
using ParadisCapture.Services;

namespace ParadisCapture.Audio;

/// <summary>
/// Captures one audio endpoint with WASAPI shared mode on a dedicated thread:
/// either loopback of an output device (what you hear) or a microphone.
///
/// <para>Every packet's QPC capture timestamp is handed to an <see cref="AudioSourceTimeline"/>,
/// which places it on the recording timeline. If the device disappears (unplugged mic, default
/// output switched to headphones…) the source reconnects on its own; the recording continues
/// with silence for this source in the meantime.</para>
/// </summary>
public sealed class WasapiCaptureSource : IDisposable
{
    private const long BufferDurationHns = 2_000_000; // 200 ms WASAPI buffer
    private const int PollIntervalMs = 10;

    private readonly string? _deviceId;
    private readonly bool _loopback;
    private readonly AudioSourceTimeline _timeline;
    private readonly string _label;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _thread;
    private bool _reportedProblem;

    /// <summary>Non-fatal problem for the user ("Microphone disconnected").</summary>
    public event Action<string>? Problem;

    /// <summary>The source is capturing again after a problem.</summary>
    public event Action<string>? Recovered;

    public WasapiCaptureSource(string? deviceId, bool loopback, AudioSourceTimeline timeline)
    {
        _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        _loopback = loopback;
        _timeline = timeline;
        _label = loopback ? "Computer audio" : "Microphone";
    }

    public string Label => _label;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = $"{_label} capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Waits until the device is open and capturing (or failed to open).</summary>
    public void WaitUntilStarted(TimeSpan timeout) => _started.Wait(timeout);

    public void Stop()
    {
        _stop.Set();
        if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            Log.Warn($"{_label} capture thread did not stop in time");
        }
    }

    private void Run()
    {
        using var enumerator = new MMDeviceEnumerator();
        while (!_stop.IsSet)
        {
            MMDevice? device = null;
            try
            {
                device = OpenDevice(enumerator);
                if (device == null)
                {
                    ReportProblem(_loopback ? "No audio output device found; computer audio is not being recorded." : "No microphone found; the microphone is not being recorded.");
                    _started.Set();
                    _stop.Wait(2000);
                    continue;
                }

                CaptureLoop(enumerator, device);
            }
            catch (Exception ex) when (!_stop.IsSet)
            {
                int hr = ex.HResult;
                Log.Warn($"{_label} capture interrupted", ex);
                ReportProblem(FriendlyErrors.IsAudioDeviceGone(hr) || ex is COMException
                    ? (_loopback ? "The audio output device changed or was disconnected. Reconnecting…" : "The microphone was disconnected. The recording continues without it.")
                    : $"{_label} stopped working. The recording continues without it.");
                _timeline.Reset();
                _started.Set();
                _stop.Wait(1000);
            }
            catch (Exception ex)
            {
                Log.Warn($"{_label} capture error during stop", ex);
            }
            finally
            {
                device?.Dispose();
            }
        }
    }

    private MMDevice? OpenDevice(MMDeviceEnumerator enumerator)
    {
        var flow = _loopback ? DataFlow.Render : DataFlow.Capture;
        if (_deviceId != null)
        {
            try
            {
                var d = enumerator.GetDevice(_deviceId);
                if (d.State == DeviceState.Active) return d;
                d.Dispose();
            }
            catch (Exception ex)
            {
                Log.Info($"{_label}: selected device unavailable ({Log.Describe(ex)}); using the Windows default");
            }
        }

        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Console)) return null;
        return enumerator.GetDefaultAudioEndpoint(flow, _loopback ? Role.Console : Role.Communications);
    }

    private void CaptureLoop(MMDeviceEnumerator enumerator, MMDevice device)
    {
        using var client = device.AudioClient;
        WaveFormat mixFormat = client.MixFormat;
        var format = ToCaptureFormat(mixFormat);
        var converter = new SampleConverter(format);

        client.Initialize(AudioClientShareMode.Shared,
            _loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None,
            BufferDurationHns, 0, mixFormat, Guid.Empty);

        using var capture = client.AudioCaptureClient;
        client.Start();
        Log.Info($"{_label}: capturing \"{device.FriendlyName}\" ({format})");

        if (_reportedProblem)
        {
            _reportedProblem = false;
            Recovered?.Invoke($"{_label} is being recorded again.");
        }
        _started.Set();

        string followingId = device.ID;
        bool followDefault = _deviceId == null || !string.Equals(_deviceId, device.ID, StringComparison.OrdinalIgnoreCase);
        var flow = _loopback ? DataFlow.Render : DataFlow.Capture;
        var role = _loopback ? Role.Console : Role.Communications;
        long nextDefaultCheck = Environment.TickCount64 + 1000;
        float[] stereo = new float[4096];

        try
        {
            while (!_stop.Wait(PollIntervalMs))
            {
                int packetFrames = capture.GetNextPacketSize();
                while (packetFrames > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags, out _, out long qpcPosition);
                    try
                    {
                        if (frames > 0)
                        {
                            if (stereo.Length < frames * 2) stereo = new float[frames * 2];
                            var dst = stereo.AsSpan(0, frames * 2);
                            if ((flags & AudioClientBufferFlags.Silent) != 0)
                            {
                                dst.Clear();
                            }
                            else
                            {
                                unsafe
                                {
                                    converter.Convert(new ReadOnlySpan<byte>((void*)data, frames * format.BlockAlign), frames, dst);
                                }
                            }

                            long timestamp = SanitizeTimestamp(qpcPosition, frames, format.SampleRate);
                            _timeline.Push(dst, format.SampleRate, timestamp, (flags & AudioClientBufferFlags.DataDiscontinuity) != 0);
                        }
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                    packetFrames = capture.GetNextPacketSize();
                }

                if (followDefault && Environment.TickCount64 >= nextDefaultCheck)
                {
                    nextDefaultCheck = Environment.TickCount64 + 1000;
                    if (DefaultChanged(enumerator, flow, role, followingId))
                    {
                        Log.Info($"{_label}: Windows default device changed; switching");
                        _timeline.Reset();
                        return; // outer loop reopens on the new default
                    }
                }
            }
        }
        finally
        {
            try { client.Stop(); } catch { /* device may already be gone */ }
        }
    }

    private static bool DefaultChanged(MMDeviceEnumerator enumerator, DataFlow flow, Role role, string currentId)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return false;
            using var d = enumerator.GetDefaultAudioEndpoint(flow, role);
            return !string.Equals(d.ID, currentId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// WASAPI's QPC position is the capture time of the packet's first frame in 100 ns units.
    /// A few drivers report 0 or nonsense; fall back to "now minus packet length".
    /// </summary>
    private static long SanitizeTimestamp(long qpcPosition, int frames, int sampleRate)
    {
        long now = RecordingClock.QpcNowHns();
        long estimate = now - frames * RecordingClock.HnsPerSecond / sampleRate;
        if (qpcPosition <= 0 || Math.Abs(qpcPosition - estimate) > 2 * RecordingClock.HnsPerSecond) return estimate;
        return qpcPosition;
    }

    private static CaptureFormat ToCaptureFormat(WaveFormat wf)
    {
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;
        int mask = 0;
        if (wf is WaveFormatExtensible ext)
        {
            isFloat = ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
            mask = ReadChannelMask(ext);
        }
        return new CaptureFormat(wf.SampleRate, wf.Channels, wf.BitsPerSample, isFloat, mask);
    }

    private static int ReadChannelMask(WaveFormatExtensible ext)
    {
        // dwChannelMask is private in NAudio, but Serialize writes the WAVEFORMATEXTENSIBLE bytes:
        // cbSize (4) + WAVEFORMATEX (18) + wValidBitsPerSample (2) + dwChannelMask (4) + SubFormat (16).
        try
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            ext.Serialize(w);
            w.Flush();
            byte[] bytes = ms.ToArray();
            return bytes.Length >= 28 ? BitConverter.ToInt32(bytes, 24) : 0;
        }
        catch (Exception ex)
        {
            Log.Info($"Channel mask unavailable; using a default layout: {Log.Describe(ex)}");
            return 0;
        }
    }

    private void ReportProblem(string message)
    {
        if (_reportedProblem) return;
        _reportedProblem = true;
        Problem?.Invoke(message);
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
        _started.Dispose();
    }
}
