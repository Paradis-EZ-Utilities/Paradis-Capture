using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using ParadisCapture.Core.Video;
using ParadisCapture.Services;

namespace ParadisCapture.UI;

public sealed record AudioDeviceChoice(string? Id, string Name);

/// <summary>The small settings dialog. Changes are written back to the settings object on Save.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        FolderBox.Text = settings.SaveFolder;
        FpsBox.SelectedIndex = settings.FrameRate == 60 ? 1 : 0;
        QualityBox.SelectedIndex = (int)settings.Quality;
        CursorBox.IsChecked = settings.CaptureCursor;
        SystemAudioBox.IsChecked = settings.RecordSystemAudio;
        MicrophoneCheck.IsChecked = settings.RecordMicrophone;
        StartStopBox.Text = settings.StartStopHotkey;
        PauseResumeBox.Text = settings.PauseResumeHotkey;
        HideControlsBox.IsChecked = settings.HideControlsWhileRecording;

        MaxHeight = SystemParameters.WorkArea.Height;
        VersionText.Text = $"Version {AppInfo.Version}";

        LoadDevices();
        FpsBox.SelectionChanged += (_, _) => UpdateBitrateHint();
        QualityBox.SelectionChanged += (_, _) => UpdateBitrateHint();
        UpdateBitrateHint();
    }

    private void LoadDevices()
    {
        OutputDeviceBox.ItemsSource = Enumerate(DataFlow.Render, "Default output device (follows Windows)");
        MicrophoneDeviceBox.ItemsSource = Enumerate(DataFlow.Capture, "Default microphone (follows Windows)");
        OutputDeviceBox.SelectedItem = Select(OutputDeviceBox, _settings.OutputDeviceId);
        MicrophoneDeviceBox.SelectedItem = Select(MicrophoneDeviceBox, _settings.MicrophoneDeviceId);
    }

    private static List<AudioDeviceChoice> Enumerate(DataFlow flow, string defaultLabel)
    {
        var list = new List<AudioDeviceChoice> { new(null, defaultLabel) };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    list.Add(new AudioDeviceChoice(device.ID, device.FriendlyName));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list {flow} audio devices", ex);
        }
        return list;
    }

    private static AudioDeviceChoice? Select(ComboBox box, string? id)
    {
        var items = (List<AudioDeviceChoice>)box.ItemsSource;
        return items.FirstOrDefault(d => d.Id == id) ?? items.FirstOrDefault();
    }

    private void UpdateBitrateHint()
    {
        int fps = SelectedFps();
        var quality = SelectedQuality();
        int video = EncoderSettings.BitrateFor(1920, 1080, fps, quality);
        double gb = EncoderSettings.GigabytesPerHour(video, EncoderSettings.AudioBitrateFor(quality));
        string purpose = quality switch
        {
            VideoQuality.Compact => "Smallest files, for lectures, slides and code. Text stays sharp; fast motion looks softer.",
            VideoQuality.High => "Largest files, for games, animation and fast motion.",
            _ => "Good for most desktop recording.",
        };
        BitrateHint.Text = $"{purpose} A full-screen 1080p recording uses at most about {gb:F1} GB per hour, " +
                           "and less when the screen is mostly still. Resolution is never reduced.";
    }

    private int SelectedFps() => FpsBox.SelectedIndex == 1 ? 60 : 30;

    private VideoQuality SelectedQuality() => (VideoQuality)Math.Clamp(QualityBox.SelectedIndex, 0, 2);

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where recordings are saved",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : AppSettings.DefaultSaveFolder,
        };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    // ------------------------------------------------------------- hotkey capture

    private void OnHotkeyFocus(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        box.Tag = box.Text;
        box.Text = "Press the keys…";
    }

    private void OnHotkeyBlur(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.Text == "Press the keys…") box.Text = (string?)box.Tag ?? "";
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            box.Text = (string?)box.Tag ?? "";
            Keyboard.ClearFocus();
            return;
        }
        if (e.Key is Key.Back or Key.Delete)
        {
            box.Text = "";
            box.Tag = "";
            return;
        }

        string? text = Hotkey.Format(e.Key, Keyboard.Modifiers);
        if (text == null) return; // modifier alone, or no modifier: keep waiting

        box.Text = text;
        box.Tag = text;
    }

    private void OnClearStartStop(object sender, RoutedEventArgs e)
    {
        StartStopBox.Text = "";
        StartStopBox.Tag = "";
    }

    private void OnClearPauseResume(object sender, RoutedEventArgs e)
    {
        PauseResumeBox.Text = "";
        PauseResumeBox.Tag = "";
    }

    private void OnShowAbout(object sender, MouseButtonEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Visible;
    }

    private void OnHideAbout(object sender, RoutedEventArgs e)
    {
        AboutPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void OnOpenLogs(object sender, MouseButtonEventArgs e) => Dialogs.RevealInExplorer(Log.Directory);

    // ------------------------------------------------------------- save

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string folder = FolderBox.Text.Trim();
        if (folder.Length == 0) folder = AppSettings.DefaultSaveFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Log.Warn($"Save folder {folder} is not usable", ex);
            Dialogs.Error(this, "Folder not usable", $"Recordings can't be saved to:\n{folder}\n\nChoose another folder.");
            return;
        }

        string startStop = Normalize(StartStopBox);
        string pauseResume = Normalize(PauseResumeBox);
        if (startStop.Length > 0 && startStop == pauseResume)
        {
            Dialogs.Warning(this, "Hotkeys clash", "Start/stop and pause/resume can't use the same key combination.");
            return;
        }

        _settings.SaveFolder = folder;
        _settings.FrameRate = SelectedFps();
        _settings.Quality = SelectedQuality();
        _settings.CaptureCursor = CursorBox.IsChecked == true;
        _settings.RecordSystemAudio = SystemAudioBox.IsChecked == true;
        _settings.RecordMicrophone = MicrophoneCheck.IsChecked == true;
        _settings.OutputDeviceId = (OutputDeviceBox.SelectedItem as AudioDeviceChoice)?.Id;
        _settings.MicrophoneDeviceId = (MicrophoneDeviceBox.SelectedItem as AudioDeviceChoice)?.Id;
        _settings.StartStopHotkey = startStop;
        _settings.PauseResumeHotkey = pauseResume;
        _settings.HideControlsWhileRecording = HideControlsBox.IsChecked == true;
        _settings.Normalize();

        DialogResult = true;
    }

    private static string Normalize(TextBox box)
    {
        string text = box.Text == "Press the keys…" ? (string?)box.Tag ?? "" : box.Text;
        return text.Trim();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
