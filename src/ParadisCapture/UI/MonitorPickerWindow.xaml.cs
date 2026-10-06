using System.Windows;
using System.Windows.Controls;
using ParadisCapture.Capture;

namespace ParadisCapture.UI;

/// <summary>Monitor chooser, shown only when more than one display is connected.</summary>
public partial class MonitorPickerWindow : Window
{
    private MonitorInfo? _picked;

    private MonitorPickerWindow(IReadOnlyList<MonitorInfo> monitors)
    {
        InitializeComponent();
        MonitorList.ItemsSource = monitors;
    }

    public static MonitorInfo? Pick(Window owner, IReadOnlyList<MonitorInfo> monitors)
    {
        var window = new MonitorPickerWindow(monitors) { Owner = owner };
        window.ShowDialog();
        return window._picked;
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        _picked = (MonitorInfo)((Button)sender).Tag;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
