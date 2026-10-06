using System.Windows;
using ParadisCapture.Recording;
using ParadisCapture.Services;

namespace ParadisCapture.UI;

/// <summary>
/// Owns one <see cref="RecordingSession"/> on behalf of the UI: creates the recording bar and tray
/// icon, marshals the session's background callbacks onto the UI thread, and reports the result.
/// </summary>
public sealed class RecordingController
{
    private readonly Window _owner;
    private readonly RecordingOptions _options;
    private readonly bool _hideControls;
    private RecordingSession? _session;
    private RecordingBarWindow? _bar;
    private TrayIconController? _tray;
    private bool _finished;
    private Task? _stopTask;

    /// <summary>Raised on the UI thread exactly once: with a result, or with a user-facing error.</summary>
    public event Action<RecordingResult?, string?>? Finished;

    public RecordingController(Window owner, RecordingOptions options, bool hideControls)
    {
        _owner = owner;
        _options = options;
        _hideControls = hideControls;
    }

    public bool IsActive => _session?.State is RecordingState.Starting or RecordingState.Recording or RecordingState.Paused or RecordingState.Finishing;

    public void Start()
    {
        var session = new RecordingSession(_options);
        session.Completed += r => Post(() => Complete(r, null));
        session.Failed += m => Post(() => Complete(null, m));
        session.Warning += m => Post(() => ShowWarning(m));
        session.SaveProgress += p => Post(() => _bar?.ShowSaveProgress(p));

        session.Start();           // throws RecorderException if it can't start
        _session = session;

        _bar = new RecordingBarWindow(session, this);
        _bar.Show();
        if (_hideControls) _bar.MinimizeToTray();

        _tray = new TrayIconController(session, this);
    }

    public void TogglePause()
    {
        if (_session == null) return;
        if (_session.State == RecordingState.Recording) _session.Pause();
        else if (_session.State == RecordingState.Paused) _session.Resume();
        _bar?.RefreshState();
        _tray?.Refresh();
    }

    /// <summary>Stops the recording; the result arrives via <see cref="Finished"/>.</summary>
    public void Stop()
    {
        if (_session == null || _stopTask != null) return;
        _bar?.ShowFinishing();
        _stopTask = _session.StopAsync();
    }

    private void ShowWarning(string message)
    {
        _bar?.ShowMessage(message);
        _tray?.Notify(message);
    }

    private void Complete(RecordingResult? result, string? error)
    {
        if (_finished) return;
        _finished = true;

        _bar?.CloseBar();
        _bar = null;
        _tray?.Dispose();
        _tray = null;

        var session = _session;
        _session = null;
        session?.Dispose();

        Finished?.Invoke(result, error);
    }

    private void Post(Action action) => _owner.Dispatcher.BeginInvoke(action);
}
