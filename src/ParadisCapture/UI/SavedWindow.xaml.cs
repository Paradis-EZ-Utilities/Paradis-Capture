using System.Diagnostics;
using System.Windows;
using ParadisCapture.Recording;
using ParadisCapture.Services;

namespace ParadisCapture.UI;

/// <summary>Confirms where the recording went, with one click to open the folder or play it.</summary>
public partial class SavedWindow : Window
{
    private readonly RecordingResult _result;

    public SavedWindow(RecordingResult result)
    {
        InitializeComponent();
        _result = result;
        FileText.Text = result.FilePath;
        DetailText.Text = $"{FormatDuration(result.Duration)}  ·  {result.FileSizeBytes / (1024.0 * 1024.0):F0} MB";
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds} s" : $"{d.Seconds} s";

    private void OnOpenFolder(object sender, RoutedEventArgs e) => Dialogs.RevealInExplorer(_result.FilePath);

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_result.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the recording in the default player", ex);
            Dialogs.Error(this, "Couldn't open the file", "Windows couldn't open the recording. Try opening it from the folder.");
        }
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
