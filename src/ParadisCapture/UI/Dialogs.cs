using System.Diagnostics;
using System.Windows;
using ParadisCapture.Services;

namespace ParadisCapture.UI;

/// <summary>One place for the handful of message boxes the app shows.</summary>
public static class Dialogs
{
    public static void Error(Window? owner, string title, string message)
    {
        Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static void Warning(Window? owner, string title, string message)
    {
        Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static void Info(Window? owner, string title, string message)
    {
        Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static bool Confirm(Window? owner, string title, string message)
    {
        return Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        string caption = $"Paradis Capture — {title}";
        return owner != null && owner.IsVisible
            ? MessageBox.Show(owner, message, caption, buttons, icon)
            : MessageBox.Show(message, caption, buttons, icon);
    }

    /// <summary>
    /// Opens a web address in the user's default browser. Returns false (and logs why) if Windows
    /// couldn't open it, so the caller can show the address instead.
    /// </summary>
    public static bool OpenUrl(string url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {url} in the default browser", ex);
            return false;
        }
    }

    /// <summary>Opens a folder in File Explorer, optionally selecting a file inside it.</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            bool isFile = File.Exists(path);
            var psi = new ProcessStartInfo("explorer.exe")
            {
                Arguments = isFile ? $"/select,\"{path}\"" : $"\"{path}\"",
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {path} in Explorer", ex);
        }
    }
}
