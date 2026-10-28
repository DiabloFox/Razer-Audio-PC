using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RazerAudioPC.Controls;

/// <summary>
/// Draws the Razer-style EQ: band labels on top, a green gradient area under a smooth
/// curve, a vertical guide per band, and a white handle per band the user can drag.
/// </summary>
public sealed class EqualizerGraph : FrameworkElement
{
    private const double LabelHeight = 28;
    private const double HandleRadius = 6;

    private int _dragBand = -1;

    public static readonly DependencyProperty BandLabelsProperty = DependencyProperty.Register(
        nameof(BandLabels), typeof(IReadOnlyList<string>), typeof(EqualizerGraph),
        new FrameworkPropertyMetadata(Array.Empty<string>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(EqualizerGraph),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(EqualizerGraph),
        new FrameworkPropertyMetadata(-12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(EqualizerGraph),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable), typeof(bool), typeof(EqualizerGraph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<string> BandLabels
    {
        get => (IReadOnlyList<string>)GetValue(BandLabelsProperty);
        set => SetValue(BandLabelsProperty, value);
    }

    public double[] Values
    {
        get => (double[])GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    /// <summary>Raised while dragging (band index, new value).</summary>
    public event Action<int, double>? BandChanging;

    /// <summary>Raised when the user releases a handle (band index, final value).</summary>
    public event Action<int, double>? BandChanged;

    public EqualizerGraph()
    {
        Cursor = Cursors.Hand;
        ClipToBounds = true;
    }

    private double ColumnX(int i, int count)
    {
        double pad = 22;
        if (count <= 1) return ActualWidth / 2;
        return pad + i * (ActualWidth - 2 * pad) / (count - 1);
    }

    private (double top, double bottom) PlotArea()
    {
        double top = LabelHeight + HandleRadius + 4;
        double bottom = Math.Max(top + 10, ActualHeight * 0.62);
        return (top, bottom);
    }

    private double ValueToY(double v)
    {
        var (top, bottom) = PlotArea();
        double t = (Maximum - Math.Clamp(v, Minimum, Maximum)) / (Maximum - Minimum);
        return top + t * (bottom - top);
    }

    private double YToValue(double y)
    {
        var (top, bottom) = PlotArea();
        double t = Math.Clamp((y - top) / (bottom - top), 0, 1);
        return Maximum - t * (Maximum - Minimum);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var values = Values ?? Array.Empty<double>();
        int n = values.Length;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (n == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface((FontFamily)Application.Current.Resources["RazerFont"], FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var pts = new Point[n];
        for (int i = 0; i < n; i++) pts[i] = new Point(ColumnX(i, n), ValueToY(values[i]));

        // Smooth curve through the band points (Catmull-Rom -> Bezier), extended to the edges.
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            ctx.LineTo(new Point(0, pts[0].Y), true, false);
            ctx.LineTo(pts[0], true, false);
            for (int i = 0; i < n - 1; i++)
            {
                var p0 = pts[Math.Max(i - 1, 0)];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = pts[Math.Min(i + 2, n - 1)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                ctx.BezierTo(c1, c2, p2, true, true);
            }
            ctx.LineTo(new Point(w, pts[n - 1].Y), true, false);
            ctx.LineTo(new Point(w, h), true, false);
        }
        geo.Freeze();

        var fill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromRgb(0x00, 0xFF, 0x00), 0),
                new(Color.FromRgb(0x22, 0xEE, 0x11), 0.55),
                new(Color.FromRgb(0x6F, 0xE8, 0x5A), 1),
            }, 90);
        dc.DrawGeometry(fill, null, geo);

        // Centre (0 dB) reference line
        var (top, bottom) = PlotArea();
        double zeroY = ValueToY(0);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 1), new Point(0, zeroY), new Point(w, zeroY));

        var guide = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), 2);
        var labels = BandLabels ?? Array.Empty<string>();
        for (int i = 0; i < n; i++)
        {
            double x = pts[i].X;
            dc.DrawLine(guide, new Point(x, top - HandleRadius), new Point(x, h * 0.88));

            string label = i < labels.Count ? labels[i] : "";
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, Brushes.White, dpi);
            dc.DrawText(ft, new Point(x - ft.Width / 2, 6));

            if (IsEditable || i == _dragBand)
            {
                dc.DrawEllipse(Brushes.White, null, pts[i], HandleRadius, HandleRadius);
            }
            if (i == _dragBand)
            {
                var vt = new FormattedText(values[i].ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + " dB",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, Brushes.Black, dpi);
                double bx = Math.Clamp(x - vt.Width / 2 - 6, 0, w - vt.Width - 12);
                double by = Math.Max(pts[i].Y - 30, LabelHeight);
                dc.DrawRoundedRectangle(Brushes.White, null, new Rect(bx, by, vt.Width + 12, vt.Height + 4), 4, 4);
                dc.DrawText(vt, new Point(bx + 6, by + 2));
            }
        }
    }

    private int HitBand(Point p)
    {
        int n = Values?.Length ?? 0;
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double d = Math.Abs(ColumnX(i, n) - p.X);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return bestDist <= Math.Max(18, ActualWidth / Math.Max(n, 1) / 2) ? best : -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEditable) return;
        var p = e.GetPosition(this);
        _dragBand = HitBand(p);
        if (_dragBand < 0) return;
        CaptureMouse();
        UpdateDrag(p);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragBand >= 0 && IsMouseCaptured) UpdateDrag(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragBand < 0) return;
        int band = _dragBand;
        _dragBand = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
        BandChanged?.Invoke(band, Values[band]);
    }

    private void UpdateDrag(Point p)
    {
        // Snap to whole steps: the device stores integer gains.
        double v = Math.Round(YToValue(p.Y));
        var copy = (double[])Values.Clone();
        if (copy[_dragBand] == v) return;
        copy[_dragBand] = v;
        Values = copy;
        BandChanging?.Invoke(_dragBand, v);
    }
}
