using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using ParadisCapture.Capture;
using ParadisCapture.Core.Video;
using ParadisCapture.Recording;
using ParadisCapture.Services;

namespace ParadisCapture.UI;

/// <summary>
/// The whole main screen: pick a target, toggle audio, press Record. While recording, this window
/// hides and the small <see cref="RecordingBarWindow"/> takes over.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private HotkeyService? _hotkeys;
    private RecordingController? _controller;
    private CaptureTarget? _target;
    private bool _busy;
    private bool _closeAfterRecording;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        SystemAudioToggle.IsChecked = _settings.RecordSystemAudio;
        MicrophoneToggle.IsChecked = _settings.RecordMicrophone;
        UpdateFolderText();
        UpdateHotkeyHint();
        UpdatePresetText();
        UpdateRecordButton();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hotkeys = new HotkeyService(this);
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys(reportConflicts: true);
        // Deliberately not excluded from capture: the main window (and Settings/About) must show up in
        // screenshots and other recorders. It hides itself while recording instead.
    }

    // ------------------------------------------------------------- target selection

    private async void OnSelectWindow(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var picked = await CapturePickerService.PickWindowAsync(new WindowInteropHelper(this).Handle);
            if (picked != null) SetTarget(picked);
        }
        catch (RecorderException ex)
        {
            Dialogs.Error(this, "Couldn't choose a window", ex.UserMessage);
        }
        catch (Exception ex)
        {
            Log.Error("Window picker failed", ex);
            Dialogs.Error(this, "Couldn't choose a window", "Windows wouldn't show the window picker. Try recording a monitor or a region instead.");
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnSelectRegion(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        Hide();

        RegionSelectorWindow.Show(
            region =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    SetTarget(region);
                    Restore();
                    _busy = false;
                });
            },
            () =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    Restore();
                    _busy = false;
                });
            });
    }

    private void OnSelectMonitor(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var monitors = MonitorInfo.GetAll();
        if (monitors.Count == 0)
        {
            Dialogs.Error(this, "No display found", "Windows didn't report any connected display.");
            return;
        }
        if (monitors.Count == 1)
        {
            SetTarget(new MonitorTarget(monitors[0]));
            return;
        }

        var chosen = MonitorPickerWindow.Pick(this, monitors);
        if (chosen != null) SetTarget(new MonitorTarget(chosen));
    }

    private void SetTarget(CaptureTarget target)
    {
        _target = target;
        TargetText.Text = target.Description;
        TargetDetailText.Text = target switch
        {
            WindowTarget => "The window is recorded even if other windows cover it.",
            MonitorTarget m => $"Everything on {m.Monitor.DisplayName} is recorded.",
            RegionTarget r => $"{r.Region.Width} × {r.Region.Height} pixels on {r.Monitor.DisplayName}.",
            _ => "",
        };
        StatusText.Text = "";
        UpdateRecordButton();
    }

    private void UpdateRecordButton()
    {
        bool recording = _controller?.IsActive == true;
        RecordButton.IsEnabled = _target != null && !recording;
        RecordButton.Content = recording ? "RECORDING…" : "RECORD";
    }

    // ------------------------------------------------------------- audio & folder

    private void OnAudioToggled(object sender, RoutedEventArgs e)
    {
        _settings.RecordSystemAudio = SystemAudioToggle.IsChecked == true;
        _settings.RecordMicrophone = MicrophoneToggle.IsChecked == true;
        SettingsService.Save(_settings);
    }

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where recordings are saved",
            InitialDirectory = Directory.Exists(_settings.SaveFolder) ? _settings.SaveFolder : AppSettings.DefaultSaveFolder,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _settings.SaveFolder = dialog.FolderName;
            SettingsService.Save(_settings);
            UpdateFolderText();
        }
    }

    private void OnOpenFolder(object sender, MouseButtonEventArgs e)
    {
        try { Directory.CreateDirectory(_settings.SaveFolder); } catch (Exception ex) { Log.Warn("Creating the save folder", ex); }
        Dialogs.RevealInExplorer(_settings.SaveFolder);
    }

    private void UpdateFolderText()
    {
        FolderText.Text = _settings.SaveFolder;
        FolderText.ToolTip = $"Open {_settings.SaveFolder}";
    }

    // ------------------------------------------------------------- settings

    private void OnOpenSettings(object sender, MouseButtonEventArgs e)
    {
        if (_controller?.IsActive == true)
        {
            Dialogs.Info(this, "Settings", "Settings can't be changed while a recording is running.");
            return;
        }

        var window = new SettingsWindow(_settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            SettingsService.Save(_settings);
            SystemAudioToggle.IsChecked = _settings.RecordSystemAudio;
            MicrophoneToggle.IsChecked = _settings.RecordMicrophone;
            UpdateFolderText();
            UpdateHotkeyHint();
            UpdatePresetText();
            ApplyHotkeys(reportConflicts: true);
        }
    }

    private void ApplyHotkeys(bool reportConflicts)
    {
        if (_hotkeys == null) return;
        var failed = _hotkeys.Apply(new[]
        {
            (HotkeyAction.StartStop, _settings.StartStopHotkey),
            (HotkeyAction.PauseResume, _settings.PauseResumeHotkey),
        });

        if (reportConflicts && failed.Count > 0)
        {
            string list = string.Join("\n", failed.Select(f => $"• {f.Text}"));
            StatusText.Text = "Some hotkeys are in use by another app. You can change them in Settings.";
            Log.Warn($"Hotkeys unavailable:\n{list}");
        }
    }

    private void UpdatePresetText() => PresetText.Text = $"· {_settings.Quality}, {_settings.FrameRate} FPS";

    private void UpdateHotkeyHint()
    {
        HotkeyHintText.Text = string.IsNullOrWhiteSpace(_settings.StartStopHotkey)
            ? ""
            : $"{_settings.StartStopHotkey} to start/stop";
    }

    private void OnHotkey(HotkeyAction action)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (action)
            {
                case HotkeyAction.StartStop:
                    if (_controller?.IsActive == true) _controller.Stop();
                    else OnRecord(this, new RoutedEventArgs());
                    break;
                case HotkeyAction.PauseResume:
                    _controller?.TogglePause();
                    break;
            }
        });
    }

    // ------------------------------------------------------------- recording

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_busy || _controller?.IsActive == true) return;
        if (_target == null)
        {
            StatusText.Text = "Choose a window, region or monitor first.";
            return;
        }

        var options = new RecordingOptions(
            _target,
            _settings.SaveFolder,
            SystemAudioToggle.IsChecked == true,
            MicrophoneToggle.IsChecked == true,
            _settings.MicrophoneDeviceId,
            _settings.OutputDeviceId,
            _settings.FrameRate,
            _settings.Quality,
            _settings.CaptureCursor);

        _controller = new RecordingController(this, options, _settings.HideControlsWhileRecording);
        _controller.Finished += OnRecordingFinished;

        StatusText.Text = "";
        // Hide before capture starts so the (capturable) main window isn't in the first frames.
        Hide();
        try
        {
            _controller.Start();
        }
        catch (RecorderException ex)
        {
            _controller = null;
            Restore();
            Dialogs.Error(this, "Couldn't start recording", ex.UserMessage);
        }
        catch
        {
            _controller = null;
            Restore();
            throw;
        }
        finally
        {
            UpdateRecordButton();
        }
    }

    private void OnRecordingFinished(RecordingResult? result, string? error)
    {
        _controller = null;
        UpdateRecordButton();

        if (_closeAfterRecording)
        {
            Close();
            return;
        }

        Restore();

        if (error != null)
        {
            Dialogs.Error(this, "Recording stopped", error);
            StatusText.Text = "";
            return;
        }

        if (result != null)
        {
            StatusText.Text = $"Saved {Path.GetFileName(result.FilePath)}  ·  {FormatDuration(result.Duration)}  ·  {result.FileSizeBytes / (1024.0 * 1024.0):F0} MB";
            var dialog = new SavedWindow(result) { Owner = this };
            dialog.ShowDialog();
        }
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";

    private void Restore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_controller?.IsActive == true)
        {
            e.Cancel = true;
            if (_closeAfterRecording) return; // already stopping; the window closes when it's done

            if (!Dialogs.Confirm(this, "Recording in progress", "A recording is still running. Stop it and save the file now?"))
            {
                return;
            }

            // Don't block the UI thread: saving a long recording takes a while and the bar shows
            // its progress. The window closes from OnRecordingFinished.
            _closeAfterRecording = true;
            StatusText.Text = "Saving the recording before closing…";
            _controller.Stop();
            return;
        }

        base.OnClosing(e);
        _hotkeys?.Dispose();
        Application.Current.Shutdown();
    }
}
