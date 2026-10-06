using System.Runtime.InteropServices;
using ParadisCapture.Interop;
using ParadisCapture.Services;
using Windows.Graphics.Capture;
using WinRT;

namespace ParadisCapture.Capture;

/// <summary>
/// The standard Windows capture picker ("Choose what to share"), which is the only supported way
/// to let a user pick an arbitrary window: Windows itself grants the capture permission for the
/// chosen window, and it handles elevated and protected windows correctly.
/// </summary>
public static class CapturePickerService
{
    /// <summary>
    /// Asks the user to choose a window. Returns null if they cancelled.
    /// </summary>
    public static async Task<WindowTarget?> PickWindowAsync(IntPtr ownerWindow)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new RecorderException("This version of Windows doesn't support screen capture (Windows 10 version 2004 or later is required).");
        }

        var picker = new GraphicsCapturePicker();
        picker.As<IInitializeWithWindow>().Initialize(ownerWindow);

        GraphicsCaptureItem? item;
        try
        {
            item = await picker.PickSingleItemAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("The Windows capture picker failed", ex);
            throw new RecorderException("Windows couldn't open the \"choose what to share\" picker. Try recording a monitor or a region instead.", ex);
        }

        if (item == null) return null;
        Log.Info($"Picked capture item \"{item.DisplayName}\" ({item.Size.Width} x {item.Size.Height})");
        return new WindowTarget(item, string.IsNullOrWhiteSpace(item.DisplayName) ? "Selected window" : item.DisplayName);
    }

    /// <summary>
    /// Asks Windows for capture permission once at startup. Granting it is what allows the
    /// recorder to hide the yellow capture border. Never fails the app.
    /// </summary>
    public static async Task RequestCaptureAccessAsync()
    {
        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            Log.Info($"Borderless capture access: {status}");
        }
        catch (Exception ex)
        {
            Log.Info($"Borderless capture access unavailable: {Log.Describe(ex)}");
        }
    }
}
