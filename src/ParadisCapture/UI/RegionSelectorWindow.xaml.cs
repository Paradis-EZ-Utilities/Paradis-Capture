using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ParadisCapture.Capture;
using ParadisCapture.Core.Video;
using ParadisCapture.Interop;
using ParadisCapture.Services;

namespace ParadisCapture.UI;

/// <summary>
/// Snipping-Tool-style region selection. One borderless window is placed over each monitor using
/// physical pixel coordinates (SetWindowPos), so the result is correct with mixed DPI scaling.
/// The overlay windows exclude themselves from capture, so they never appear in a recording.
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly Action<RegionTarget> _completed;
    private readonly Action _cancelled;
    private readonly List<RegionSelectorWindow> _siblings;
    private double _scale = 1.0;
    private Point _dragStartPhysical;
    private bool _dragging;
    private bool _finished;

    private RegionSelectorWindow(MonitorInfo monitor, List<RegionSelectorWindow> siblings, Action<RegionTarget> completed, Action cancelled)
    {
        InitializeComponent();
        _monitor = monitor;
        _siblings = siblings;
        _completed = completed;
        _cancelled = cancelled;
        Hint.Visibility = monitor.IsPrimary ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the overlay on every monitor. <paramref name="completed"/> runs with the chosen
    /// region; <paramref name="cancelled"/> runs if the user presses Esc or right-clicks.
    /// </summary>
    public static void Show(Action<RegionTarget> completed, Action cancelled)
    {
        var monitors = MonitorInfo.GetAll();
        if (monitors.Count == 0)
        {
            cancelled();
            return;
        }

        var windows = new List<RegionSelectorWindow>();
        foreach (var m in monitors)
        {
            var w = new RegionSelectorWindow(m, windows, completed, cancelled);
            windows.Add(w);
        }
        foreach (var w in windows) w.Show();
        windows.FirstOrDefault(w => w._monitor.IsPrimary)?.Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // Not excluded from capture: the overlay only exists before a recording starts.

        // Position in physical pixels; WPF's own Left/Top are DIPs relative to the primary monitor.
        var b = _monitor.Bounds;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height,
            NativeMethods.SWP_SHOWWINDOW);

        _scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        FullGeometry.Rect = new Rect(0, 0, b.Width / _scale, b.Height / _scale);
        Root.Width = b.Width / _scale;
        Root.Height = b.Height / _scale;
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape) Cancel();
        base.OnKeyDown(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) => Cancel();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragStartPhysical = ToPhysical(e.GetPosition(Root));
        _dragging = true;
        Hint.Visibility = Visibility.Collapsed;
        foreach (var w in _siblings) if (w != this) w.Hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        UpdateSelection(ToPhysical(e.GetPosition(Root)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var end = ToPhysical(e.GetPosition(Root));
        var raw = PixelRect.FromPoints((int)_dragStartPhysical.X, (int)_dragStartPhysical.Y, (int)end.X, (int)end.Y);
        if (raw.Width < 8 || raw.Height < 8)
        {
            // A click rather than a drag: treat it as "I changed my mind".
            Cancel();
            return;
        }

        var region = VideoGeometry.NormalizeRegion(raw, _monitor.Bounds.Width, _monitor.Bounds.Height);
        Log.Info($"Region selected on {_monitor.DeviceName}: {region}");
        Finish(() => _completed(new RegionTarget(_monitor, region)));
    }

    private void UpdateSelection(Point currentPhysical)
    {
        var r = PixelRect.FromPoints((int)_dragStartPhysical.X, (int)_dragStartPhysical.Y, (int)currentPhysical.X, (int)currentPhysical.Y);
        HoleGeometry.Rect = new Rect(r.X / _scale, r.Y / _scale, r.Width / _scale, r.Height / _scale);

        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Margin = new Thickness(r.X / _scale, r.Y / _scale, 0, 0);
        SelectionBorder.Width = Math.Max(0, r.Width / _scale);
        SelectionBorder.Height = Math.Max(0, r.Height / _scale);

        SizeText.Text = $"{r.Width} × {r.Height}";
        SizeLabel.Visibility = Visibility.Visible;
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double labelLeft = Math.Clamp(r.X / _scale, 0, Math.Max(0, Root.Width - SizeLabel.DesiredSize.Width));
        double below = (r.Bottom / _scale) + 8;
        double labelTop = below + SizeLabel.DesiredSize.Height > Root.Height ? Math.Max(0, (r.Y / _scale) - SizeLabel.DesiredSize.Height - 8) : below;
        SizeLabel.Margin = new Thickness(labelLeft, labelTop, 0, 0);
    }

    private Point ToPhysical(Point dip) => new(dip.X * _scale, dip.Y * _scale);

    private void Cancel() => Finish(_cancelled);

    private void Finish(Action callback)
    {
        if (_finished) return;
        foreach (var w in _siblings) w._finished = true;
        foreach (var w in _siblings.ToList()) w.Close();
        callback();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_finished) return;
        // Closed some other way (display change, Alt+F4): treat as cancel exactly once.
        _finished = true;
        foreach (var w in _siblings) w._finished = true;
        _cancelled();
    }
}
