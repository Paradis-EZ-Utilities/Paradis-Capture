using System.Windows;
using System.Windows.Threading;
using ParadisCapture.Capture;
using ParadisCapture.Services;
using ParadisCapture.UI;

namespace ParadisCapture;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One recorder at a time: a second instance would fight over devices and file names.
        _singleInstance = new Mutex(initiallyOwned: true, "ParadisCapture.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Paradis Capture is already running.", "Paradis Capture", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        Log.Initialize();
        Log.Info($"Paradis Capture starting (v{AppInfo.Version}) on {Environment.OSVersion.VersionString}, .NET {Environment.Version}");

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Asking for borderless capture access needs a visible window; do it after showing.
        _ = CapturePickerService.RequestCaptureAccessAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Paradis Capture exiting");
        Log.Shutdown();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        Dialogs.Error(MainWindow, "Something went wrong",
            "Paradis Capture hit an unexpected problem. If a recording was running, whatever was recorded so far has been saved.\n\n" +
            $"Technical details are in:\n{Log.Directory}");
    }
}
