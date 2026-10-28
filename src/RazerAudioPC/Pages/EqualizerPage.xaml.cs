using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RazerAudioPC.Bluetooth;
using RazerAudioPC.Device;

namespace RazerAudioPC.Pages;

public partial class EqualizerPage : UserControl, IPage
{
    private static readonly EqPreset[] Order =
    {
        EqPreset.Custom, EqPreset.DefaultThx, EqPreset.Amplified, EqPreset.Vocal, EqPreset.EnhancedBass, EqPreset.EnhancedClarity,
    };

    private readonly MainWindow _window;
    private readonly Dictionary<EqPreset, RadioButton> _rows = new();
    private DateTime _lastSend = DateTime.MinValue;
    private HammerheadDevice Device => _window.Device;

    public string? Title => "Equalizer";

    public EqualizerPage(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        Graph.BandLabels = Presets.BandLabels;

        foreach (var preset in Order)
        {
            var row = new RadioButton { Style = (Style)FindResource("OptionRow"), Content = Presets.Name(preset), GroupName = "eq" };
            row.Checked += async (_, _) =>
            {
                if (Device.EqPreset != preset) await Device.SetEqPresetAsync(preset);
                Refresh();
            };
            _rows[preset] = row;
            PresetList.Children.Add(row);
        }

        Graph.BandChanging += OnBandChanging;
        Graph.BandChanged += (_, _) => _ = SendBands();
        Device.PropertyChanged += OnDeviceChanged;
        Unloaded += (_, _) => Device.PropertyChanged -= OnDeviceChanged;
        Refresh();
    }

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HammerheadDevice.EqPreset) or nameof(HammerheadDevice.EqBands)) Refresh();
    }

    private void Refresh()
    {
        var preset = Device.EqPreset;
        if (_rows.TryGetValue(preset, out var row) && row.IsChecked != true) row.IsChecked = true;

        bool custom = preset == EqPreset.Custom;
        CurveTitle.Text = Presets.Name(preset);
        Graph.IsEditable = custom;
        if (Graph.IsMouseCaptured) return; // don't fight the user's drag with device echoes
        Graph.Values = custom
            ? Device.EqBands.Select(b => (double)(b - Presets.BandFlat)).ToArray()
            : Presets.CurveFor(preset);
        FlatButton.IsEnabled = custom;
        Hint.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
    }

    private byte[] CurrentBands() => Graph.Values.Select(v => (byte)(v + Presets.BandFlat)).ToArray();

    private void OnBandChanging(int band, double value)
    {
        // Live preview while dragging, throttled like the official app (≤ 1 write / 500 ms).
        if ((DateTime.UtcNow - _lastSend).TotalMilliseconds >= 500) _ = SendBands();
    }

    private async Task SendBands()
    {
        _lastSend = DateTime.UtcNow;
        await Device.SetEqBandsAsync(CurrentBands());
    }

    private async void Flat_Click(object sender, RoutedEventArgs e)
    {
        Graph.Values = new double[Presets.BandCount];
        await Device.SetEqBandsAsync(Enumerable.Repeat((byte)Presets.BandFlat, Presets.BandCount).ToArray());
    }
}
