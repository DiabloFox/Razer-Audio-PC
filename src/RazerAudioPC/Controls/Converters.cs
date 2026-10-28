using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RazerAudioPC.Controls;

public sealed class BatteryTextConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is int v ? $"{v}%" : "--";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Battery percent → width of the fill bar (parameter = full width).</summary>
public sealed class BatteryWidthConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        double full = double.Parse(p?.ToString() ?? "20", CultureInfo.InvariantCulture);
        return value is int v ? Math.Max(1, full * v / 100.0) : 0.0;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Battery percent → green, or red when low.</summary>
public sealed class BatteryBrushConverter : IValueConverter
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x44, 0xD6, 0x2C));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE8, 0x20, 0x2A));
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is int v && v <= 15 ? Red : Green;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is Color col ? new SolidColorBrush(col) : Brushes.Transparent;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class OnOffConverter : IValueConverter
{
    public string On { get; set; } = "On";
    public string Off { get; set; } = "Off";
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? On : Off;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
