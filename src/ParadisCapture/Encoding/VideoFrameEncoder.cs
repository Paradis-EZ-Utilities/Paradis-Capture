using ParadisCapture.Capture;
using ParadisCapture.Services;
using ParadisCapture.Video;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ParadisCapture.Encoding;

/// <summary>
/// Produces one encoded video frame per call: picks the right captured frame, converts it to NV12
/// on the GPU, and hands it to the <see cref="MediaWriter"/> — either as a GPU texture (normal
/// path) or copied to system memory (fallback for encoders that can't take textures).
/// </summary>
public sealed class VideoFrameEncoder : IDisposable
{
    private readonly GraphicsDevice _gfx;
    private readonly MediaWriter _writer;
    private readonly VideoConverter _converter;
    private readonly int _width, _height;

    // GPU path
    private readonly IMFVideoSampleAllocatorEx? _allocator;

    // System-memory path
    private readonly ID3D11Texture2D? _nv12;
    private readonly ID3D11Texture2D? _staging;

    private long _droppedFrames;

    public VideoFrameEncoder(GraphicsDevice gfx, MediaWriter writer, VideoConverter converter)
    {
        _gfx = gfx;
        _writer = writer;
        _converter = converter;
        _width = writer.Video.Width;
        _height = writer.Video.Height;

        if (writer.Mode == EncoderMode.GpuTextures)
        {
            _allocator = CreateAllocator(gfx, _width, _height);
        }
        else
        {
            lock (gfx.Sync)
            {
                _nv12 = gfx.CreateTexture(_width, _height, Format.NV12, BindFlags.RenderTarget);
                _staging = gfx.CreateTexture(_width, _height, Format.NV12, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
            }
        }
    }

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    private static IMFVideoSampleAllocatorEx CreateAllocator(GraphicsDevice gfx, int width, int height)
    {
        IntPtr ptr = MediaFactory.MFCreateVideoSampleAllocatorEx(typeof(IMFVideoSampleAllocatorEx).GUID);
        var allocator = new IMFVideoSampleAllocatorEx(ptr);
        try
        {
            allocator.SetDirectXManager(gfx.DeviceManager);
            using var attrs = MediaFactory.MFCreateAttributes(2);
            attrs.Set(TransformAttributeKeys.D3D11Bindflags, (uint)BindFlags.RenderTarget);
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2);
            MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
            // Up to 12 frames may be queued inside the encoder before we start dropping.
            allocator.InitializeSampleAllocatorEx(4, 12, attrs, type);
            return allocator;
        }
        catch
        {
            allocator.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encodes the frame for recording time <paramref name="timestamp"/>. Called from the video thread only.
    /// Returns false if the frame was dropped because the encoder is saturated.
    /// </summary>
    public bool EncodeFrame(ScreenCaptureSource source, long timestamp, long duration)
    {
        return _allocator != null
            ? EncodeGpu(source, timestamp, duration)
            : EncodeSystemMemory(source, timestamp, duration);
    }

    private bool EncodeGpu(ScreenCaptureSource source, long timestamp, long duration)
    {
        IMFSample sample;
        try
        {
            sample = _allocator!.AllocateSample();
        }
        catch (SharpGenException ex) when (ex.HResult == MFGuids.MF_E_SAMPLEALLOCATOR_EMPTY)
        {
            // Encoder is behind (e.g. GPU busy with a game). Dropping a frame keeps timing correct.
            if (Interlocked.Increment(ref _droppedFrames) % 30 == 1) Log.Warn($"Encoder busy; dropped {DroppedFrames} frame(s) so far");
            return false;
        }

        using (sample)
        {
            using (var buffer = sample.GetBufferByIndex(0))
            {
                using var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
                IntPtr texPtr = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
                using var texture = new ID3D11Texture2D(texPtr);
                uint slice = dxgiBuffer.SubresourceIndex;

                lock (_gfx.Sync)
                {
                    _converter.Convert(source.SelectFrame(timestamp), texture, slice);
                }

                try { buffer.CurrentLength = buffer.MaxLength; } catch { /* not required by all encoders */ }
            }

            sample.SampleTime = timestamp;
            sample.SampleDuration = duration;
            _writer.WriteVideo(sample);
        }
        return true;
    }

    private unsafe bool EncodeSystemMemory(ScreenCaptureSource source, long timestamp, long duration)
    {
        int ySize = _width * _height;
        int total = ySize * 3 / 2;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(total);

        lock (_gfx.Sync)
        {
            _converter.Convert(source.SelectFrame(timestamp), _nv12!, 0);
            _gfx.Context.CopyResource(_staging!, _nv12!);
            var map = _gfx.Context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                buffer.Lock(out IntPtr dst, out _, out _);
                try
                {
                    byte* src = (byte*)map.DataPointer;
                    byte* d = (byte*)dst;
                    uint pitch = map.RowPitch;
                    // Y plane, then interleaved UV plane (which follows Height rows of the Y plane).
                    for (int y = 0; y < _height; y++)
                        Buffer.MemoryCopy(src + y * pitch, d + y * _width, _width, _width);
                    byte* uvSrc = src + pitch * (uint)_height;
                    byte* uvDst = d + ySize;
                    for (int y = 0; y < _height / 2; y++)
                        Buffer.MemoryCopy(uvSrc + y * pitch, uvDst + y * _width, _width, _width);
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                _gfx.Context.Unmap(_staging!, 0);
            }
        }
        buffer.CurrentLength = total;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp;
        sample.SampleDuration = duration;
        _writer.WriteVideo(sample);
        return true;
    }

    public void Dispose()
    {
        _allocator?.UninitializeSampleAllocator();
        _allocator?.Dispose();
        lock (_gfx.Sync)
        {
            _nv12?.Dispose();
            _staging?.Dispose();
        }
    }
}
