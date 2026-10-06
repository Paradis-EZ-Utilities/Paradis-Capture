using System.Windows;
using System.Windows.Forms;
using System.Linq;
using ParadisCapture.Recording;
using ParadisCapture.Services;
using Application = System.Windows.Application;

namespace ParadisCapture.UI;

/// <summary>
/// Tray icon shown while recording, so the recorder can be stopped without any window on screen
/// (useful during full-screen games). WinForms' NotifyIcon is the only tray API in the box.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly RecordingSession _session;
    private readonly RecordingController _controller;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _pauseItem;

    public TrayIconController(RecordingSession session, RecordingController controller)
    {
        _session = session;
        _controller = controller;

        var menu = new ContextMenuStrip();
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) =>
        {
            controller.TogglePause();
            Refresh();
        });
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Stop and save", null, (_, _) => controller.Stop()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Show recording controls", null, (_, _) => ShowBar()));

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "Paradis Capture",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowBar();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/recording.ico"))?.Stream;
            if (stream != null) return new System.Drawing.Icon(stream);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray icon could not be loaded", ex);
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Refresh()
    {
        var elapsed = _session.Elapsed;
        bool paused = _session.State == RecordingState.Paused;
        string time = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        // NotifyIcon.Text is limited to 63 characters.
        _icon.Text = Truncate(paused ? $"Paradis Capture — paused at {time}" : $"Paradis Capture — recording {time}", 63);
        _pauseItem.Text = paused ? "Resume" : "Pause";
    }

    public void Notify(string message)
    {
        try
        {
            _icon.BalloonTipTitle = "Paradis Capture";
            _icon.BalloonTipText = Truncate(message, 250);
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray notification failed", ex);
        }
    }

    private void ShowBar()
    {
        var app = Application.Current;
        app?.Dispatcher.BeginInvoke(() =>
        {
            var bar = app.Windows.OfType<RecordingBarWindow>().FirstOrDefault();
            if (bar == null) return;
            bar.Show();
            bar.Activate();
        });
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
