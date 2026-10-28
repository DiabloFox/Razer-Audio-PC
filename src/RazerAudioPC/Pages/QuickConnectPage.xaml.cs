using System.Windows;
using System.Windows.Controls;
using RazerAudioPC.Device;

namespace RazerAudioPC.Pages;

public partial class QuickConnectPage : UserControl, IPage
{
    private readonly MainWindow _window;

    public string? Title => "Quick Connect";

    public QuickConnectPage(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        void Update(object? s, EventArgs? e) =>
            Empty.Visibility = _window.Device.QuickConnectHosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _window.Device.QuickConnectHosts.CollectionChanged += Update;
        Unloaded += (_, _) => _window.Device.QuickConnectHosts.CollectionChanged -= Update;
        Update(null, null);
    }

    private async void Host_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not QuickConnectHost host || host.Connected) return;
        _window.ShowToast($"Switching to {host.Name}…");
        await _window.Device.ConnectQuickConnectHostAsync(host);
    }
}
