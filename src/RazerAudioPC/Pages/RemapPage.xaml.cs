using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RazerAudioPC.Bluetooth;
using RazerAudioPC.Device;

namespace RazerAudioPC.Pages;

public partial class RemapPage : UserControl, IPage
{
    private readonly MainWindow _window;
    private HammerheadDevice Device => _window.Device;
    private bool CallContext => CallTab.IsChecked == true;

    public string? Title => "Remap Gestures";
    public FrameworkElement HeaderRight { get; }

    public RemapPage(MainWindow window)
    {
        _window = window;
        InitializeComponent();

        var reset = new Button { Style = (Style)FindResource("IconButton"), ToolTip = "Reset to default" };
        reset.Content = new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 16 };
        reset.Click += Reset_Click;
        HeaderRight = reset;

        Device.PropertyChanged += OnDeviceChanged;
        Unloaded += (_, _) => Device.PropertyChanged -= OnDeviceChanged;
        Refresh();
    }

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HammerheadDevice.TouchLeft) or nameof(HammerheadDevice.TouchRight)) Refresh();
    }

    private void Context_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) Refresh();
    }

    private string Describe(TouchMap? map, Gesture g)
    {
        if (map == null) return "—";
        return CallContext ? TouchMap.Label(map.GetCall(g)) : TouchMap.Label(map.GetStream(g));
    }

    private void Refresh()
    {
        if (GestureList == null) return;
        GestureList.Children.Clear();
        foreach (var (gesture, label) in TouchMap.EditableGestures)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

            grid.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var l = new TextBlock { Text = Describe(Device.TouchLeft, gesture), FontSize = 11, Foreground = (Brush)FindResource("RazerGreen"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
            var r = new TextBlock { Text = Describe(Device.TouchRight, gesture), FontSize = 11, Foreground = (Brush)FindResource("RazerGreen"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(l, 1);
            Grid.SetColumn(r, 2);
            grid.Children.Add(l);
            grid.Children.Add(r);

            var card = new Button { Style = (Style)FindResource("CardButton"), Content = grid, Margin = new Thickness(0, 0, 0, 1), MinHeight = 52 };
            card.Padding = new Thickness(4, 0, 4, 0);
            var g = gesture;
            var title = label;
            card.Click += (_, _) => OpenPicker(g, title, Device.TouchLeft != null);
            GestureList.Children.Add(card);
        }

        MissingBud.Visibility = Device.TouchLeft == null || Device.TouchRight == null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- picker sheet (app's "SINGLE PRESS" dialog)

    private void OpenPicker(Gesture gesture, string title, bool left)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("SheetText"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 14),
        });

        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        var leftTab = new RadioButton { Style = (Style)FindResource("SegmentButton"), Content = "LEFT", GroupName = "side", IsChecked = left };
        var rightTab = new RadioButton { Style = (Style)FindResource("SegmentButton"), Content = "RIGHT", GroupName = "side", IsChecked = !left };
        seg.Children.Add(leftTab);
        seg.Children.Add(rightTab);
        panel.Children.Add(new Border
        {
            Child = seg, BorderBrush = Brushes.Black, BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16),
        });

        var list = new StackPanel();
        panel.Children.Add(list);

        void Build()
        {
            list.Children.Clear();
            bool isLeft = leftTab.IsChecked == true;
            var map = isLeft ? Device.TouchLeft : Device.TouchRight;
            if (map == null)
            {
                list.Children.Add(new TextBlock
                {
                    Text = $"The {(isLeft ? "left" : "right")} earbud isn't connected.", Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 30),
                });
                return;
            }

            if (CallContext)
            {
                var current = map.GetCall(gesture);
                foreach (var f in TouchMap.CallOptions(gesture))
                {
                    bool selected = f == current || (f == CallFunction.Answer && current == CallFunction.EndCall);
                    list.Children.Add(Row(TouchMap.Glyph(f), TouchMap.Label(f), f == CallFunction.Disable, selected, async () =>
                    {
                        var copy = new TouchMap(map.ToArray());
                        copy.SetCall(gesture, f);
                        await Device.WriteTouchMapAsync(isLeft, copy);
                        Build();
                    }));
                }
            }
            else
            {
                var current = map.GetStream(gesture);
                foreach (var f in TouchMap.StreamOptions(gesture))
                {
                    list.Children.Add(Row(TouchMap.Glyph(f), TouchMap.Label(f), f == StreamFunction.Disable, f == current, async () =>
                    {
                        var copy = new TouchMap(map.ToArray());
                        copy.SetStream(gesture, f);
                        await Device.WriteTouchMapAsync(isLeft, copy);
                        Build();
                    }));
                }
            }
        }

        leftTab.Checked += (_, _) => Build();
        rightTab.Checked += (_, _) => Build();
        Build();
        _window.OpenSheet(panel);
    }

    /// <summary>Opens the picker for the first gesture (used by --screenshot).</summary>
    public void OpenPickerForScreenshot() => OpenPicker(Gesture.SinglePress, "SINGLE PRESS", true);

    private RadioButton Row(string glyph, string label, bool danger, bool selected, Func<Task> onSelect)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Icon"), Text = glyph, FontSize = 16, Width = 24,
            Foreground = danger ? (Brush)FindResource("DangerRed") : null, Margin = new Thickness(0, 0, 16, 0),
        });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = FontWeights.SemiBold };
        if (danger) text.Foreground = (Brush)FindResource("DangerRed");
        content.Children.Add(text);

        var row = new RadioButton { Style = (Style)FindResource("SheetRow"), Content = content, IsChecked = selected, GroupName = "fn" };
        if (!danger)
        {
            // Follow the row foreground: dark normally, white when selected.
            var follow = new System.Windows.Data.Binding(nameof(Foreground)) { Source = row };
            ((TextBlock)content.Children[0]).SetBinding(TextBlock.ForegroundProperty, follow);
            text.SetBinding(TextBlock.ForegroundProperty, follow);
        }
        row.Click += async (_, _) => await onSelect();
        return row;
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        bool ok = true;
        if (Device.HasBud(true)) ok &= await Device.WriteTouchMapAsync(true, TouchMap.Default(true));
        if (Device.HasBud(false)) ok &= await Device.WriteTouchMapAsync(false, TouchMap.Default(false));
        if (ok) _window.ShowToast("Gestures reset to default.");
    }
}
