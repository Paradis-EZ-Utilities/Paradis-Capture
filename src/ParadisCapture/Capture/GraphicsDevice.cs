using ParadisCapture.Interop;
using ParadisCapture.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Windows.Graphics.DirectX.Direct3D11;

namespace ParadisCapture.Capture;

/// <summary>
/// One Direct3D 11 device shared by capture, color conversion and the hardware encoder for the
/// duration of a recording. Frames never leave the GPU on the normal path.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    /// <summary>
    /// Serializes our own multi-step use of the immediate context across the capture thread and
    /// the encoder thread. (Media Foundation's own use is serialized by D3D multithread protection.)
    /// </summary>
    public readonly object Sync = new();

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11VideoDevice VideoDevice { get; }
    public ID3D11VideoContext VideoContext { get; }
    public IDirect3DDevice WinRTDevice { get; }
    public string AdapterName { get; }

    private IMFDXGIDeviceManager? _manager;

    private GraphicsDevice(ID3D11Device device, string adapterName)
    {
        Device = device;
        Context = device.ImmediateContext;
        AdapterName = adapterName;

        using (var mt = device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            mt?.SetMultithreadProtected(true);
        }

        VideoDevice = device.QueryInterface<ID3D11VideoDevice>();
        VideoContext = Context.QueryInterface<ID3D11VideoContext>();
        WinRTDevice = WinRTInterop.CreateDirect3DDevice(device);
    }

    public static GraphicsDevice Create()
    {
        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

        var hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out ID3D11Device? device);
        if (hr.Failure || device == null)
        {
            throw new RecorderException(
                "Your graphics hardware doesn't support the video features screen recording needs. Make sure your graphics driver is up to date.",
                new InvalidOperationException($"D3D11CreateDevice failed: 0x{hr.Code:X8}"));
        }

        string adapter = "unknown adapter";
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var a = dxgiDevice.GetAdapter();
            adapter = $"{a.Description.Description} (feature level {device.FeatureLevel})";
        }
        catch
        {
            // informational only
        }

        try
        {
            return new GraphicsDevice(device, adapter);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>The DXGI device manager that lets Media Foundation's hardware encoder use this device.</summary>
    public IMFDXGIDeviceManager DeviceManager
    {
        get
        {
            if (_manager == null)
            {
                var manager = MediaFactory.MFCreateDXGIDeviceManager();
                manager.ResetDevice(Device).CheckError();
                _manager = manager;
            }
            return _manager;
        }
    }

    public ID3D11Texture2D CreateTexture(int width, int height, Format format, BindFlags bind, ResourceUsage usage = ResourceUsage.Default, CpuAccessFlags cpu = CpuAccessFlags.None)
    {
        return Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            BindFlags = bind,
            CPUAccessFlags = cpu,
        });
    }

    public void Dispose()
    {
        _manager?.Dispose();
        (WinRTDevice as IDisposable)?.Dispose();
        VideoContext.Dispose();
        VideoDevice.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}
