using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using RazerAudioPC.Device;

namespace RazerAudioPC.Pages;

public partial class SettingsPage : UserControl, IPage
{
    private static readonly int[] Timeouts = { 0, 5, 15, 30, 45, 60 };

    private readonly MainWindow _window;
    private readonly Dictionary<int, RadioButton> _rows = new();
    private HammerheadDevice Device => _window.Device;

    public string? Title => "Settings";

    public SettingsPage(MainWindow window)
    {
        _window = window;
        InitializeComponent();

        foreach (int minutes in Timeouts)
        {
            string label = minutes == 0 ? "Never" : minutes == 60 ? "After 1 hour" : $"After {minutes} minutes";
            var row = new RadioButton { Style = (Style)FindResource("OptionRow"), Content = label, GroupName = "timeout" };
            int m = minutes;
            row.Click += async (_, _) => await Device.SetAutoPowerOffAsync(m);
            _rows[minutes] = row;
            TimeoutList.Children.Add(row);
        }

        StartupToggle.IsChecked = AppSettings.StartWithWindows;
        TrayToggle.IsChecked = App.Settings.CloseToTray;
        BatteryToggle.IsChecked = App.Settings.BatteryNotifications;

        Device.PropertyChanged += OnDeviceChanged;
        Unloaded += (_, _) => Device.PropertyChanged -= OnDeviceChanged;
        Refresh();
    }

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HammerheadDevice.AutoPowerOff)) Refresh();
    }

    private void Refresh()
    {
        if (_rows.TryGetValue(Device.AutoPowerOff, out var row)) row.IsChecked = true;
    }

    private void Startup_Click(object sender, RoutedEventArgs e) => AppSettings.StartWithWindows = StartupToggle.IsChecked == true;

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.CloseToTray = TrayToggle.IsChecked == true;
        _window.CloseToTray = App.Settings.CloseToTray;
        App.Settings.Save();
    }

    private void Battery_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.BatteryNotifications = BatteryToggle.IsChecked == true;
        App.Settings.Save();
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        Device.Reconnect();
        _window.GoBack();
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        App.FlushLog();
        Process.Start(new ProcessStartInfo(App.LogPath) { UseShellExecute = true });
    }
}
