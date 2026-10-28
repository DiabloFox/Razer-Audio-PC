using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RazerAudioPC.Bluetooth;
using RazerAudioPC.Device;

namespace RazerAudioPC.Pages;

public partial class AncPage : UserControl, IPage
{
    private readonly MainWindow _window;
    private bool _updating;
    private HammerheadDevice Device => _window.Device;

    public string? Title => "Active Noise Cancellation";

    public AncPage(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        Device.PropertyChanged += OnDeviceChanged;
        Unloaded += (_, _) => Device.PropertyChanged -= OnDeviceChanged;
        Refresh();
    }

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        _updating = true;
        byte anc = Device.Anc;
        OffRow.IsChecked = anc == (byte)AncMode.Off;
        AmbientRow.IsChecked = anc == (byte)AncMode.Ambient;
        OnRow.IsChecked = anc is > 0 and <= 10;
        LevelPanel.Visibility = OnRow.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (!LevelSlider.IsMouseCaptureWithin) LevelSlider.Value = Device.AncLevel;
        LevelText.Text = $"{(int)LevelSlider.Value}";

        // The app only allows On / Ambient while an earbud is worn.
        OnRow.IsEnabled = AmbientRow.IsEnabled = Device.AnyWorn || OnRow.IsChecked == true || AmbientRow.IsChecked == true;
        WearHint.Visibility = Device.AnyWorn ? Visibility.Collapsed : Visibility.Visible;
        FitButton.IsEnabled = Device.WornLeft && Device.WornRight;
        _updating = false;
    }

    private async void Off_Click(object sender, RoutedEventArgs e) => await Device.SetAncAsync((byte)AncMode.Off);
    private async void On_Click(object sender, RoutedEventArgs e) => await Device.SetAncAsync((byte)Device.AncLevel);
    private async void Ambient_Click(object sender, RoutedEventArgs e) => await Device.SetAncAsync((byte)AncMode.Ambient);

    private void Level_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LevelText != null) LevelText.Text = $"{(int)e.NewValue}";
    }

    private async void Level_Commit(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        byte level = (byte)LevelSlider.Value;
        if (level != Device.Anc) await Device.SetAncAsync(level);
    }

    private async void Fit_Click(object sender, RoutedEventArgs e)
    {
        FitButton.IsEnabled = false;
        FitButton.Content = "TESTING…";
        FitLeft.Text = FitRight.Text = "…";
        FitLeft.Foreground = FitRight.Foreground = Brushes.White;

        var result = await Device.RunFitTestAsync();

        FitButton.Content = "START FIT TEST";
        FitButton.IsEnabled = Device.WornLeft && Device.WornRight;
        if (result is { } r)
        {
            Show(FitLeft, r.Left);
            Show(FitRight, r.Right);
        }
        else
        {
            FitLeft.Text = FitRight.Text = "—";
        }
    }

    private void Show(TextBlock t, bool good)
    {
        t.Text = good ? "Good fit" : "Adjust / try another tip";
        t.Foreground = good ? (Brush)FindResource("RazerGreen") : new SolidColorBrush(Color.FromRgb(0xE8, 0xA1, 0x3A));
    }
}
