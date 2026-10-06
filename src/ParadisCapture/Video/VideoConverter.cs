using ParadisCapture.Capture;
using ParadisCapture.Core.Video;
using ParadisCapture.Services;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;
using Vortice.Mathematics;

namespace ParadisCapture.Video;

/// <summary>
/// Converts captured BGRA frames into NV12 frames of the fixed output size using the GPU's
/// video processor (ID3D11VideoProcessor). This one step does color conversion (BT.709, limited
/// range: what every player expects), letterboxing when a window's size changes, and downscaling
/// only when a source exceeds encoder limits. No CPU copies.
/// </summary>
public sealed class VideoConverter : IDisposable
{
    private readonly GraphicsDevice _gfx;
    private readonly int _outWidth;
    private readonly int _outHeight;
    private readonly int _fps;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private int _procInWidth, _procInHeight;

    private ID3D11Texture2D? _inputTexture;
    private ID3D11VideoProcessorInputView? _inputView;
    private ID3D11Texture2D? _blackTexture;

    private readonly Dictionary<(IntPtr, uint), ID3D11VideoProcessorOutputView> _outputViews = new();

    public VideoConverter(GraphicsDevice gfx, int outWidth, int outHeight, int fps)
    {
        _gfx = gfx;
        _outWidth = outWidth;
        _outHeight = outHeight;
        _fps = fps;

        lock (_gfx.Sync)
        {
            EnsureProcessor(outWidth, outHeight);
            if (_enumerator!.CheckVideoProcessorFormat(Format.NV12, out var support).Failure ||
                !support.HasFlag(VideoProcessorFormatSupport.Output))
            {
                throw new RecorderException("Your graphics hardware can't convert video to the format the H.264 encoder needs. Make sure your graphics driver is up to date.");
            }
        }
    }

    private void EnsureProcessor(int inWidth, int inHeight)
    {
        if (_processor != null && inWidth == _procInWidth && inHeight == _procInHeight) return;

        DisposeProcessor();

        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)_fps, 1),
            InputWidth = (uint)inWidth,
            InputHeight = (uint)inHeight,
            OutputFrameRate = new Rational((uint)_fps, 1),
            OutputWidth = (uint)_outWidth,
            OutputHeight = (uint)_outHeight,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _gfx.VideoDevice.CreateVideoProcessorEnumerator(desc);
        _processor = _gfx.VideoDevice.CreateVideoProcessor(_enumerator, 0);
        _procInWidth = inWidth;
        _procInHeight = inHeight;

        var vc = _gfx.VideoContext;
        // Input: full-range RGB (desktop pixels). Output: BT.709 limited-range YCbCr.
        vc.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 1 });
        vc.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { Usage = 0, RGB_Range = 1, YCbCr_Matrix = 1, Nominal_Range = 1 });
        vc.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        vc.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        vc.VideoProcessorSetStreamOutputRate(_processor, 0, VideoProcessorOutputRate.Normal, false, null);
        vc.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, _outWidth, _outHeight));
        vc.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });

        // Views are tied to the enumerator; recreate them lazily.
        DisposeViews();
    }

    /// <summary>
    /// Renders the source frame (or black, if <paramref name="source"/> is null) into
    /// <paramref name="target"/>, an NV12 texture of the output size. Caller holds <see cref="GraphicsDevice.Sync"/>.
    /// </summary>
    public void Convert((ID3D11Texture2D Texture, int Width, int Height)? source, ID3D11Texture2D target, uint arraySlice)
    {
        // No frame yet (a minimized or protected window delivers none): render a black frame,
        // which keeps the video track continuous. Using a real source is more portable across
        // drivers than asking the video processor to blt with no enabled stream.
        var src = source ?? BlackFrame();
        EnsureProcessor(src.Width, src.Height);

        var outputView = GetOutputView(target, arraySlice);
        var vc = _gfx.VideoContext;

        if (!ReferenceEquals(src.Texture, _inputTexture) || _inputView == null)
        {
            _inputView?.Dispose();
            _inputTexture = src.Texture;
            _inputView = _gfx.VideoDevice.CreateVideoProcessorInputView(src.Texture, _enumerator!, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
        }

        var dest = VideoGeometry.Fit(src.Width, src.Height, _outWidth, _outHeight);
        vc.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RawRect(0, 0, src.Width, src.Height));
        vc.VideoProcessorSetStreamDestRect(_processor!, 0, true, new RawRect(dest.X, dest.Y, dest.Right, dest.Bottom));

        var stream = new VideoProcessorStream { Enable = true, OutputIndex = 0, InputFrameOrField = 0, InputSurface = _inputView };
        vc.VideoProcessorBlt(_processor!, outputView, 0, new[] { stream }).CheckError();
    }

    /// <summary>A black BGRA texture the size of the output, created once and reused.</summary>
    private (ID3D11Texture2D Texture, int Width, int Height) BlackFrame()
    {
        if (_blackTexture == null)
        {
            _blackTexture = _gfx.CreateTexture(_outWidth, _outHeight, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
            using var rtv = _gfx.Device.CreateRenderTargetView(_blackTexture);
            _gfx.Context.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));
        }
        return (_blackTexture, _outWidth, _outHeight);
    }

    private ID3D11VideoProcessorOutputView GetOutputView(ID3D11Texture2D target, uint arraySlice)
    {
        var key = (target.NativePointer, arraySlice);
        if (_outputViews.TryGetValue(key, out var view)) return view;

        if (_outputViews.Count > 64) DisposeViews(); // allocator textures are reused; this only guards against leaks

        var desc = target.Description;
        var viewDesc = desc.ArraySize > 1
            ? new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayVideoProcessorOutputView { MipSlice = 0, FirstArraySlice = arraySlice, ArraySize = 1 },
            }
            : new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            };
        view = _gfx.VideoDevice.CreateVideoProcessorOutputView(target, _enumerator!, viewDesc);
        _outputViews[key] = view;
        return view;
    }

    private void DisposeViews()
    {
        foreach (var v in _outputViews.Values) v.Dispose();
        _outputViews.Clear();
        _inputView?.Dispose();
        _inputView = null;
        _inputTexture = null;
    }

    private void DisposeBlack()
    {
        _blackTexture?.Dispose();
        _blackTexture = null;
    }

    private void DisposeProcessor()
    {
        DisposeViews();
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    public void Dispose()
    {
        lock (_gfx.Sync)
        {
            DisposeProcessor();
            DisposeBlack();
        }
    }
}
