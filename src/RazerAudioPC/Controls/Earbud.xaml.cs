using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RazerAudioPC.Controls;

public partial class Earbud : UserControl
{
    public static readonly DependencyProperty IsRightProperty = DependencyProperty.Register(
        nameof(IsRight), typeof(bool), typeof(Earbud), new PropertyMetadata(false, (d, _) => ((Earbud)d).ApplySide()));

    public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(
        nameof(GlowColor), typeof(Color), typeof(Earbud),
        new PropertyMetadata(Color.FromRgb(0x44, 0xD6, 0x2C), (d, _) => ((Earbud)d).ApplyGlow()));

    public static readonly DependencyProperty IsLitProperty = DependencyProperty.Register(
        nameof(IsLit), typeof(bool), typeof(Earbud), new PropertyMetadata(true, (d, _) => ((Earbud)d).ApplyGlow()));

    /// <summary>Mirrors the bud so the ear tip points the other way.</summary>
    public bool IsRight
    {
        get => (bool)GetValue(IsRightProperty);
        set => SetValue(IsRightProperty, value);
    }

    public Color GlowColor
    {
        get => (Color)GetValue(GlowColorProperty);
        set => SetValue(GlowColorProperty, value);
    }

    public bool IsLit
    {
        get => (bool)GetValue(IsLitProperty);
        set => SetValue(IsLitProperty, value);
    }

    public Earbud()
    {
        InitializeComponent();
        RenderTransformOrigin = new Point(0.5, 0.5);
        ApplySide();
    }

    private void ApplySide() => RenderTransform = new ScaleTransform(IsRight ? 1 : -1, 1);

    private void ApplyGlow()
    {
        var c = IsLit ? GlowColor : Color.FromRgb(0x30, 0x30, 0x30);
        GlowStop.Color = c;
        Logo.Fill = new SolidColorBrush(c);
        Glow.Opacity = IsLit ? 0.85 : 0;
    }
}
