using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ParadisCapture.Capture;
using ParadisCapture.Interop;
using ParadisCapture.Recording;
using ParadisCapture.Services;

namespace ParadisCapture.UI;

/// <summary>
/// The small always-on-top bar shown while recording: red dot, elapsed time, Pause and Stop.
/// Excluded from capture, so it never appears in the recording.
/// </summary>
public partial class RecordingBarWindow : Window
{
    private readonly RecordingSession _session;
    private readonly RecordingController _controller;
    private readonly DispatcherTimer _timer;
    private bool _finishing;
    private bool _closingForReal;

    public RecordingBarWindow(RecordingSession session, RecordingController controller)
    {
        InitializeComponent();
        _session = session;
        _controller = controller;
        TargetText.Text = $"{session.TargetDescription}  ·  recording at {session.OutputSize}";

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
        {
            Log.Warn("This Windows build can't hide the recording bar from captures; hide it to the tray to keep it out of the recording.");
            ShowMessage("This Windows version can't hide this bar from the recording. Use the — button to hide it to the tray.");
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // Only now does the window have its final size (SizeToContent), so place it here.
        PlaceAtBottomOfPrimaryMonitor();
    }

    /// <summary>Bottom centre of the primary display, above the taskbar, in physical pixels.</summary>
    private void PlaceAtBottomOfPrimaryMonitor()
    {
        var monitor = MonitorInfo.Primary();
        if (monitor == null) return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        int x = monitor.WorkArea.X + (monitor.WorkArea.Width - rect.Width) / 2;
        int y = monitor.WorkArea.Bottom - rect.Height - (int)(24 * monitor.Dpi / 96.0);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */);
    }

    private void Tick()
    {
        var elapsed = _session.Elapsed;
        ElapsedText.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        bool paused = _session.State == RecordingState.Paused;
        // Blink the dot while recording; keep it solid but dim while paused.
        Indicator.Opacity = paused ? 0.35 : (DateTime.UtcNow.Millisecond < 500 ? 1.0 : 0.35);
    }

    public void RefreshState()
    {
        bool paused = _session.State == RecordingState.Paused;
        PauseButton.Content = paused ? "Resume" : "Pause";
        TargetText.Text = paused
            ? $"Paused  ·  {_session.TargetDescription}"
            : $"{_session.TargetDescription}  ·  recording at {_session.OutputSize}";
        Tick();
    }

    public void ShowFinishing()
    {
        _finishing = true;
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        StopButton.Content = "Saving…";
        _timer.Stop();
        Show();
        WindowState = WindowState.Normal;
    }

    public void ShowSaveProgress(double fraction)
    {
        if (!_finishing) return;
        StopButton.Content = fraction >= 1 ? "Saving…" : $"Saving… {fraction * 100:F0}%";
    }

    public void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageText.Visibility = Visibility.Visible;
    }

    public void MinimizeToTray() => Hide();

    public void CloseBar()
    {
        _timer.Stop();
        _closingForReal = true;
        Close();
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        _controller.TogglePause();
        RefreshState();
    }

    private void OnStop(object sender, RoutedEventArgs e) => _controller.Stop();

    private void OnMinimizeToTray(object sender, RoutedEventArgs e) => MinimizeToTray();

    private void OnDragBar(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            try { DragMove(); } catch (InvalidOperationException) { /* mouse released already */ }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The bar can't be closed on its own: that would leave the recording running invisibly.
        if (!_closingForReal)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }
        base.OnClosing(e);
    }
}
