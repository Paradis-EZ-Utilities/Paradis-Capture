using ParadisCapture.Core.Timing;
using ParadisCapture.Core.Video;
using ParadisCapture.Interop;
using ParadisCapture.Services;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace ParadisCapture.Capture;

/// <summary>
/// Windows.Graphics.Capture session for a window or monitor.
///
/// <para>Each arriving frame (or just the selected region of it) is copied on the GPU into a small
/// ring of timestamped textures, and the WGC frame is released immediately. The encoder thread
/// later picks the frame whose capture time best matches each output frame's timestamp, so video
/// lines up with audio by capture time rather than by when it happened to be processed.</para>
/// </summary>
public sealed class ScreenCaptureSource : IDisposable
{
    private const int SlotCount = 5;
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private sealed class Slot
    {
        public ID3D11Texture2D? Texture;
        public int Width, Height;
        public long Time;
        public bool Valid;
    }

    private readonly GraphicsDevice _gfx;
    private readonly GraphicsCaptureItem _item;
    private readonly PixelRect? _crop;
    private readonly long _minStoreInterval;
    private readonly Slot[] _slots = Enumerable.Range(0, SlotCount).Select(_ => new Slot()).ToArray();
    private readonly ManualResetEventSlim _firstFrame = new(false);
    private readonly object _frameLock = new();

    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private RecordingClock? _clock;
    private int _nextSlot;
    private long _lastStoredTime = long.MinValue;
    private volatile bool _disposed;
    private long _framesArrived;

    /// <summary>The window was closed or the monitor disconnected.</summary>
    public event Action? ItemClosed;

    /// <summary>An unexpected error on the capture thread.</summary>
    public event Action<Exception>? Failed;

    public ScreenCaptureSource(GraphicsDevice gfx, GraphicsCaptureItem item, PixelRect? crop, bool captureCursor, int fps)
    {
        _gfx = gfx;
        _item = item;
        _crop = crop;
        _minStoreInterval = RecordingClock.HnsPerSecond * 3 / (4 * fps);

        var size = item.Size;
        _poolSize = new SizeInt32 { Width = Math.Max(1, size.Width), Height = Math.Max(1, size.Height) };
        SourceSize = new PixelSize(_poolSize.Width, _poolSize.Height);

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gfx.WinRTDevice, PixelFormat, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _item.Closed += OnItemClosed;

        Configure(_session, captureCursor, fps);
    }

    /// <summary>Size of the captured item when recording started (before cropping).</summary>
    public PixelSize SourceSize { get; }

    /// <summary>Size of the frames this source produces at start (the crop, or the whole item).</summary>
    public PixelSize FrameSize => _crop is { } c ? new PixelSize(c.Width, c.Height) : SourceSize;

    public long FramesArrived => Interlocked.Read(ref _framesArrived);

    private static void Configure(GraphicsCaptureSession session, bool captureCursor, int fps)
    {
        const string sessionType = "Windows.Graphics.Capture.GraphicsCaptureSession";
        try
        {
            if (ApiInformation.IsPropertyPresent(sessionType, nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
                session.IsCursorCaptureEnabled = captureCursor;
        }
        catch (Exception ex) { Log.Warn("Could not set cursor capture", ex); }

        try
        {
            // Hides the yellow capture border on Windows 11 (allowed after GraphicsCaptureAccess
            // was requested at startup). Purely cosmetic; ignore if not permitted.
            if (ApiInformation.IsPropertyPresent(sessionType, nameof(GraphicsCaptureSession.IsBorderRequired)))
                session.IsBorderRequired = false;
        }
        catch (Exception ex) { Log.Info($"Capture border stays visible: {Log.Describe(ex)}"); }

        try
        {
            // Windows 11 24H2+: don't deliver frames much faster than we record (saves GPU work on
            // high refresh rate monitors).
            if (ApiInformation.IsPropertyPresent(sessionType, nameof(GraphicsCaptureSession.MinUpdateInterval)))
                session.MinUpdateInterval = TimeSpan.FromTicks(RecordingClock.HnsPerSecond / (2 * fps));
        }
        catch (Exception ex) { Log.Info($"MinUpdateInterval not applied: {Log.Describe(ex)}"); }
    }

    public void Start() => _session!.StartCapture();

    /// <summary>Starts timestamping frames against the recording clock.</summary>
    public void AttachClock(RecordingClock clock) => _clock = clock;

    /// <summary>Waits (bounded) for the first frame. Minimized windows deliver none; that's fine.</summary>
    public bool WaitForFirstFrame(TimeSpan timeout) => _firstFrame.Wait(timeout);

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        if (!_disposed) ItemClosed?.Invoke();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        lock (_frameLock)
        {
            if (_disposed) return;
            SizeInt32 newSize = default;
            bool resized = false;
            try
            {
                using (var frame = sender.TryGetNextFrame())
                {
                    if (frame == null) return;
                    Interlocked.Increment(ref _framesArrived);

                    var content = frame.ContentSize;
                    if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                    {
                        resized = content.Width > 0 && content.Height > 0;
                        newSize = content;
                    }

                    StoreFrame(frame, content);
                }

                if (resized)
                {
                    // Window resized (or monitor mode changed): future frames come at the new size.
                    _poolSize = newSize;
                    sender.Recreate(_gfx.WinRTDevice, PixelFormat, 2, newSize);
                }
            }
            catch (Exception ex) when (!_disposed)
            {
                Failed?.Invoke(ex);
            }
        }
    }

    private void StoreFrame(Direct3D11CaptureFrame frame, SizeInt32 content)
    {
        var clock = _clock;
        bool clockRunning = clock != null && clock.IsStarted;
        long time = clockRunning ? clock!.MapClamped(frame.SystemRelativeTime.Ticks) : 0;

        // Frames arriving faster than we can use them aren't copied (saves GPU bandwidth on
        // high refresh rate displays). Before recording starts, keep refreshing one slot.
        bool haveFrame = _lastStoredTime != long.MinValue;
        if (clockRunning && haveFrame && time - _lastStoredTime < _minStoreInterval) return;

        var full = new PixelRect(0, 0, Math.Min(content.Width, _poolSize.Width), Math.Min(content.Height, _poolSize.Height));
        var box = _crop is { } c ? c.Intersect(full) : full;
        if (box.IsEmpty) return;

        using var texture = WinRTInterop.GetTexture(frame.Surface);
        lock (_gfx.Sync)
        {
            int index = clockRunning ? _nextSlot : 0;
            var slot = _slots[index];
            if (slot.Texture == null || slot.Width != box.Width || slot.Height != box.Height)
            {
                slot.Texture?.Dispose();
                slot.Texture = _gfx.CreateTexture(box.Width, box.Height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
                slot.Width = box.Width;
                slot.Height = box.Height;
            }

            _gfx.Context.CopySubresourceRegion(slot.Texture, 0, 0, 0, 0, texture, 0,
                new Box(box.X, box.Y, 0, box.Right, box.Bottom, 1));
            slot.Time = time;
            slot.Valid = true;
            _lastStoredTime = time;
            if (clockRunning) _nextSlot = (_nextSlot + 1) % SlotCount;
        }

        _firstFrame.Set();
    }

    /// <summary>
    /// The stored frame to show at recording time <paramref name="target"/>, or null if none has
    /// arrived yet. Must be called while holding <see cref="GraphicsDevice.Sync"/>.
    /// </summary>
    public (ID3D11Texture2D Texture, int Width, int Height)? SelectFrame(long target)
    {
        Span<long> times = stackalloc long[SlotCount];
        Span<bool> valid = stackalloc bool[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            times[i] = _slots[i].Time;
            valid[i] = _slots[i].Valid && _slots[i].Texture != null;
        }
        int best = FrameSelection.Choose(times, valid, target);
        if (best < 0) return null;
        var s = _slots[best];
        return (s.Texture!, s.Width, s.Height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _item.Closed -= OnItemClosed;
        lock (_frameLock)
        {
            if (_pool != null) _pool.FrameArrived -= OnFrameArrived;
            try { _session?.Dispose(); } catch (Exception ex) { Log.Warn("Capture session dispose", ex); }
            try { _pool?.Dispose(); } catch (Exception ex) { Log.Warn("Frame pool dispose", ex); }
            _session = null;
            _pool = null;
        }

        lock (_gfx.Sync)
        {
            foreach (var s in _slots)
            {
                s.Texture?.Dispose();
                s.Texture = null;
                s.Valid = false;
            }
        }
        _firstFrame.Dispose();
    }
}
