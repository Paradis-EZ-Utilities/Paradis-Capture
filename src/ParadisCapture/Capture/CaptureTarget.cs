using ParadisCapture.Core.Video;
using ParadisCapture.Interop;
using Windows.Graphics.Capture;

namespace ParadisCapture.Capture;

/// <summary>What the user chose to record.</summary>
public abstract record CaptureTarget
{
    /// <summary>Text for the main window, e.g. "Window: Unity - MyGame".</summary>
    public abstract string Description { get; }

    /// <summary>Window title used to name the file, or null.</summary>
    public virtual string? NameHint => null;

    /// <summary>Creates the capture item and (for regions) the crop rectangle in item pixels.</summary>
    public abstract (GraphicsCaptureItem Item, PixelRect? Crop) Resolve();
}

public sealed record WindowTarget(GraphicsCaptureItem Item, string Title) : CaptureTarget
{
    public override string Description => $"Window: {Title}";
    public override string? NameHint => Title;
    public override (GraphicsCaptureItem, PixelRect?) Resolve() => (Item, null);
}

public sealed record MonitorTarget(MonitorInfo Monitor) : CaptureTarget
{
    public override string Description => $"Monitor: {Monitor.DisplayName}  ({Monitor.Bounds.Width} × {Monitor.Bounds.Height})";

    public override (GraphicsCaptureItem, PixelRect?) Resolve()
    {
        var current = MonitorInfo.Find(Monitor.DeviceName)
            ?? throw new RecorderException($"{Monitor.DisplayName} is no longer connected. Choose another monitor.");
        return (WinRTInterop.CreateItemForMonitor(current.Handle), null);
    }
}

/// <summary>A rectangle on one monitor, in physical pixels relative to that monitor's top-left.</summary>
public sealed record RegionTarget(MonitorInfo Monitor, PixelRect Region) : CaptureTarget
{
    public override string Description => $"Region: {Region.Width} × {Region.Height}  on {Monitor.DisplayName}";

    public override (GraphicsCaptureItem, PixelRect?) Resolve()
    {
        var current = MonitorInfo.Find(Monitor.DeviceName)
            ?? throw new RecorderException($"The monitor this region is on ({Monitor.DisplayName}) is no longer connected. Select the region again.");
        if (current.Bounds.Width != Monitor.Bounds.Width || current.Bounds.Height != Monitor.Bounds.Height)
        {
            throw new RecorderException("The monitor's resolution changed since the region was selected. Select the region again.");
        }
        return (WinRTInterop.CreateItemForMonitor(current.Handle), Region);
    }
}
