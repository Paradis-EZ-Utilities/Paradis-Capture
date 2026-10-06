using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ParadisCapture.Core.Video;
using ParadisCapture.Interop;

namespace ParadisCapture.Capture;

/// <summary>A connected display. All coordinates are physical pixels in virtual-screen space.</summary>
public sealed record MonitorInfo(IntPtr Handle, string DeviceName, int Number, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary, uint Dpi)
{
    public string DisplayName => IsPrimary ? $"Display {Number} (main)" : $"Display {Number}";

    public string Description => $"{DisplayName}  ·  {Bounds.Width} × {Bounds.Height}  ·  {Dpi * 100 / 96}%";

    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc proc = (IntPtr h, IntPtr hdc, ref NativeMethods.RECT r, IntPtr data) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(h, ref info))
            {
                uint dpi = 96;
                try
                {
                    if (NativeMethods.GetDpiForMonitor(h, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out _) == 0) dpi = dx;
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }

                list.Add(new MonitorInfo(
                    h,
                    info.szDevice,
                    ParseNumber(info.szDevice, list.Count + 1),
                    ToRect(info.rcMonitor),
                    ToRect(info.rcWork),
                    (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    dpi));
            }
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        // Renumber densely in the user-visible order (primary first is how Windows numbers them in most setups).
        return list.OrderBy(m => m.Number).ToList();
    }

    public static MonitorInfo? Primary()
    {
        var all = GetAll();
        return all.FirstOrDefault(m => m.IsPrimary) ?? all.FirstOrDefault();
    }

    /// <summary>Re-resolves a monitor by device name (handles can change after display reconfiguration).</summary>
    public static MonitorInfo? Find(string deviceName) =>
        GetAll().FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    private static PixelRect ToRect(NativeMethods.RECT r) => new(r.Left, r.Top, r.Width, r.Height);

    private static int ParseNumber(string deviceName, int fallback)
    {
        var m = Regex.Match(deviceName ?? "", @"DISPLAY(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : fallback;
    }
}
