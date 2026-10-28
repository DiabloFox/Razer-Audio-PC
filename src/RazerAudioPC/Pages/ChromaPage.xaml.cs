using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RazerAudioPC.Bluetooth;
using RazerAudioPC.Device;

namespace RazerAudioPC.Pages;

public partial class ChromaPage : UserControl, IPage
{
    private static readonly (ChromaEffect Effect, string Name)[] Effects =
    {
        (ChromaEffect.Off, "Off"),
        (ChromaEffect.Static, "Static"),
        (ChromaEffect.Breathing, "Breathing"),
        (ChromaEffect.Spectrum, "Spectrum Cycling"),
        (ChromaEffect.AudioMeter, "Audio Meter"),
    };

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x44, 0xD6, 0x2C), Color.FromRgb(0x00, 0xFF, 0x00), Color.FromRgb(0xFF, 0x00, 0x00),
        Color.FromRgb(0xFF, 0x6A, 0x00), Color.FromRgb(0xFF, 0xD4, 0x00), Color.FromRgb(0x00, 0xFF, 0xFF),
        Color.FromRgb(0x00, 0x5B, 0xFF), Color.FromRgb(0x8A, 0x2B, 0xFF), Color.FromRgb(0xFF, 0x2D, 0xC8),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    private readonly MainWindow _window;
    private readonly Dictionary<ChromaEffect, RadioButton> _rows = new();
    private bool _updating;
    private HammerheadDevice Device => _window.Device;

    public string? Title => "Chroma Effect";

    public ChromaPage(MainWindow window)
    {
        _window = window;
        InitializeComponent();

        foreach (var (effect, name) in Effects)
        {
            var row = new RadioButton { Style = (Style)FindResource("OptionRow"), Content = name, GroupName = "fx" };
            var fx = effect;
            row.Click += async (_, _) => await Device.SetChromaAsync(fx, Device.ChromaColor);
            _rows[effect] = row;
            EffectList.Children.Add(row);
        }

        foreach (var c in Palette) Swatches.Children.Add(Swatch(new SolidColorBrush(c), c, null));
        var rainbow = new LinearGradientBrush(new GradientStopCollection
        {
            new(Colors.Red, 0), new(Colors.Yellow, 0.33), new(Colors.Lime, 0.5), new(Colors.Blue, 0.75), new(Colors.Magenta, 1),
        }, 45);
        Swatches.Children.Add(Swatch(rainbow, null, "Custom color…"));

        Device.PropertyChanged += OnDeviceChanged;
        Unloaded += (_, _) => Device.PropertyChanged -= OnDeviceChanged;
        Refresh();
    }

    private Button Swatch(Brush fill, Color? color, string? tooltip)
    {
        var ring = new Ellipse { Width = 34, Height = 34, StrokeThickness = 2, Stroke = Brushes.Transparent };
        var content = new Grid();
        content.Children.Add(ring);
        content.Children.Add(new Ellipse { Fill = fill, Width = 26, Height = 26 });
        var b = new Button
        {
            Width = 34, Height = 34, Margin = new Thickness(4), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = tooltip,
            Template = SwatchTemplate(), Content = content, Tag = (ring, color),
        };
        b.Click += async (_, _) =>
        {
            var c = color ?? PickColor();
            if (c == null) return;
            var effect = Device.ChromaEffect is ChromaEffect.Static or ChromaEffect.Breathing or ChromaEffect.AudioMeter
                ? Device.ChromaEffect
                : ChromaEffect.Static;
            await Device.SetChromaAsync(effect, c.Value);
        };
        return b;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        return new ControlTemplate(typeof(Button)) { VisualTree = cp };
    }

    private Color? PickColor()
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        var c = Device.ChromaColor;
        dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B)
            : null;
    }

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        _updating = true;
        if (_rows.TryGetValue(Device.ChromaEffect, out var row)) row.IsChecked = true;
        ColorSection.Visibility = Device.ChromaEffect is ChromaEffect.Static or ChromaEffect.Breathing or ChromaEffect.AudioMeter
            ? Visibility.Visible : Visibility.Collapsed;

        foreach (var child in Swatches.Children.OfType<Button>())
        {
            var (ring, color) = ((Ellipse, Color?))child.Tag;
            ring.Stroke = color == Device.ChromaColor ? Brushes.White : Brushes.Transparent;
        }

        if (!BrightnessSlider.IsMouseCaptureWithin) BrightnessSlider.Value = Device.ChromaBrightness;
        BrightnessText.Text = $"{Math.Round(BrightnessSlider.Value / 2.55)}%";
        _updating = false;
    }

    private void Brightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText != null) BrightnessText.Text = $"{Math.Round(e.NewValue / 2.55)}%";
    }

    private async void Brightness_Commit(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        int v = (int)BrightnessSlider.Value;
        if (v != Device.ChromaBrightness) await Device.SetChromaBrightnessAsync(v);
    }
}
