using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Nova4Me2.App.Controls;

/// <summary>Circular arc gauge (0..100) with an animated sweep, used for the health score and the boot-fix likelihood.</summary>
public sealed class GaugeControl : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(GaugeControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(GaugeControl), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(GaugeControl), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InvertColorsProperty = DependencyProperty.Register(nameof(InvertColors), typeof(bool), typeof(GaugeControl), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    /// <summary>When true, high values are "bad" (red) — unused for now but available.</summary>
    public bool InvertColors { get => (bool)GetValue(InvertColorsProperty); set => SetValue(InvertColorsProperty, value); }

    public GaugeControl() { Width = 150; Height = 150; }

    public void AnimateTo(double v)
    {
        var a = new DoubleAnimation(Math.Clamp(v, 0, 100), TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(ValueProperty, a);
    }

    private static Color C(string key, Color fallback) => Application.Current.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double size = Math.Min(w, h), cx = w / 2, cy = h / 2, r = size / 2 - 10;
        double v = Math.Clamp(Value, 0, 100);
        Color good = C("Good", Colors.Green), warn = C("Warn", Colors.Orange), bad = C("Bad", Colors.Red);
        Color col = v >= 70 ? good : v >= 40 ? warn : bad;
        if (InvertColors) col = v >= 70 ? bad : v >= 40 ? warn : good;
        var track = new Pen(new SolidColorBrush(C("PanelAlt", Colors.Gray)), 12) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var arcPen = new Pen(new SolidColorBrush(col), 12) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        const double start = 135, sweepMax = 270;
        dc.DrawGeometry(null, track, Arc(cx, cy, r, start, sweepMax));
        if (v > 0.5) dc.DrawGeometry(null, arcPen, Arc(cx, cy, r, start, sweepMax * v / 100));
        var textBrush = new SolidColorBrush(C("Text", Colors.White));
        var muted = new SolidColorBrush(C("TextMuted", Colors.Gray));
        // FormattedText anchors at the left edge of its layout box, so centre the box on cx explicitly.
        double bigW = size * 0.9;
        var big = new FormattedText($"{v:0}{Suffix}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size * 0.22, textBrush, 1.0) { TextAlignment = TextAlignment.Center, MaxTextWidth = bigW, MaxLineCount = 1 };
        dc.DrawText(big, new Point(cx - bigW / 2, cy - big.Height / 2 - size * 0.02));
        if (!string.IsNullOrEmpty(Label))
        {
            double lblW = size * 0.72;
            var lbl = new FormattedText(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Max(9, size * 0.075), muted, 1.0) { TextAlignment = TextAlignment.Center, MaxTextWidth = lblW, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(lbl, new Point(cx - lblW / 2, cy + size * 0.15));
        }
    }

    private static Geometry Arc(double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(cx - r * Math.Cos(a0 - Math.PI / 2 + Math.PI / 2) , cy + r * Math.Sin(a0));
        p0 = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
        var p1 = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
