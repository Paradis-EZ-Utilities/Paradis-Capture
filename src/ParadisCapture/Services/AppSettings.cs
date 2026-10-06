using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ParadisCapture.Core.Video;

namespace ParadisCapture.Services;

public sealed class AppSettings
{
    public string SaveFolder { get; set; } = DefaultSaveFolder;
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMicrophone { get; set; }
    /// <summary>Endpoint id, or null for the Windows default device.</summary>
    public string? MicrophoneDeviceId { get; set; }
    /// <summary>Endpoint id of the output device whose sound is recorded, or null for the default.</summary>
    public string? OutputDeviceId { get; set; }
    public int FrameRate { get; set; } = 30;
    public VideoQuality Quality { get; set; } = VideoQuality.Standard;
    public bool CaptureCursor { get; set; } = true;
    /// <summary>Hotkey text such as "Ctrl+Shift+F9"; empty = disabled.</summary>
    public string StartStopHotkey { get; set; } = "Ctrl+Shift+F9";
    public string PauseResumeHotkey { get; set; } = "Ctrl+Shift+F10";
    /// <summary>While recording, hide the control window and use the tray icon instead.</summary>
    public bool HideControlsWhileRecording { get; set; }

    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Recordings");

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        if (FrameRate != 60) FrameRate = 30;
        if (!Enum.IsDefined(Quality)) Quality = VideoQuality.Standard;
        if (string.IsNullOrWhiteSpace(SaveFolder)) SaveFolder = DefaultSaveFolder;
        StartStopHotkey ??= "";
        PauseResumeHotkey ??= "";
    }
}

/// <summary>Loads/saves settings as JSON in %LocalAppData%\ParadisCapture\settings.json.</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParadisCapture", "settings.json");

    /// <summary>Where builds from before the rename (Simple Recorder) kept their settings.</summary>
    private static string LegacyFilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleRecorder", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath) && File.Exists(LegacyFilePath) && ImportLegacy() is { } imported)
            {
                return imported;
            }
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (s != null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Settings file could not be read; using defaults", ex);
        }
        return new AppSettings();
    }

    /// <summary>
    /// Carries the save folder, devices, hotkeys and other choices over from Simple Recorder. The
    /// old quality names don't map onto the new presets, so quality starts at the Standard default.
    /// </summary>
    private static AppSettings? ImportLegacy()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(LegacyFilePath)) as JsonObject;
            if (node == null) return null;
            node.Remove(nameof(AppSettings.Quality));
            var s = node.Deserialize<AppSettings>(Options);
            if (s == null) return null;
            s.Normalize();
            Save(s);
            Log.Info("Imported settings from Simple Recorder");
            return s;
        }
        catch (Exception ex)
        {
            Log.Warn("Old Simple Recorder settings could not be imported; using defaults", ex);
            return null;
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Settings could not be saved", ex);
        }
    }
}
