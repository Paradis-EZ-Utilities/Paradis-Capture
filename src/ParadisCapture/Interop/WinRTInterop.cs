using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ParadisCapture.Interop;

/// <summary>
/// The few COM interop calls needed to bridge Windows.Graphics.Capture (WinRT) and Direct3D 11.
/// Implemented with raw vtable calls so no runtime-callable wrappers are created per frame.
/// </summary>
internal static unsafe class WinRTInterop
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd) => CreateItem(hwnd, forMonitor: false);

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor) => CreateItem(hmonitor, forMonitor: true);

    private static GraphicsCaptureItem CreateItem(IntPtr handle, bool forMonitor)
    {
        using IObjectReference factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        IntPtr interop = IntPtr.Zero;
        IntPtr itemPtr = IntPtr.Zero;
        try
        {
            Guid iidInterop = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in iidInterop, out interop));

            // IGraphicsCaptureItemInterop : IUnknown { CreateForWindow (slot 3); CreateForMonitor (slot 4) }
            void** vtbl = *(void***)interop;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[forMonitor ? 4 : 3];
            Guid iidItem = IID_IGraphicsCaptureItem;
            int hr = fn(interop, handle, &iidItem, &itemPtr);
            Marshal.ThrowExceptionForHR(hr);
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
            if (interop != IntPtr.Zero) Marshal.Release(interop);
        }
    }

    /// <summary>Wraps a D3D11 device as the WinRT IDirect3DDevice that the capture frame pool needs.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>Gets the ID3D11Texture2D behind a captured frame's surface (caller disposes).</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        IntPtr access = IntPtr.Zero;
        try
        {
            Guid iidAccess = IID_IDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, in iidAccess, out access));

            // IDirect3DDxgiInterfaceAccess : IUnknown { GetInterface (slot 3) }
            void** vtbl = *(void***)access;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
            Guid iidTexture = IID_ID3D11Texture2D;
            IntPtr texture;
            Marshal.ThrowExceptionForHR(fn(access, &iidTexture, &texture));
            return new ID3D11Texture2D(texture); // takes ownership of the reference
        }
        finally
        {
            if (access != IntPtr.Zero) Marshal.Release(access);
            if (surfacePtr != IntPtr.Zero) Marshal.Release(surfacePtr);
        }
    }
}

/// <summary>
/// WinRT pickers and dialogs need to know which window owns them. Implemented here because
/// the WinRT projection doesn't expose IInitializeWithWindow.
/// </summary>
[ComImport, Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithWindow
{
    void Initialize(IntPtr hwnd);
}
