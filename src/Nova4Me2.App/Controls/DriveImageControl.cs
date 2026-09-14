using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nova4Me2.App.Controls;

/// <summary>
/// CPU-Z style product picture. If assets/&lt;model&gt;.png or assets/&lt;vendor&gt;.png exists it is shown; otherwise a
/// stylised M.2 / 2.5" drive is drawn with the model text and a vendor colour badge (no trademarked artwork is bundled).
/// </summary>
public sealed class DriveImageControl : FrameworkElement
{
    private string _model = "", _vendor = "", _capacity = "", _formFactor = "";
    private Color _brand = Colors.SteelBlue;
    private BitmapImage? _image;

    public DriveImageControl() { Width = 320; Height = 150; }

    public void Set(string model, string vendorName, string vendorKey, string brandHex, string capacity, string formFactor)
    {
        _model = model; _vendor = vendorName; _capacity = capacity; _formFactor = formFactor;
        try { _brand = (Color)ColorConverter.ConvertFromString(brandHex); } catch { _brand = Colors.SteelBlue; }
        _image = null;
        string assets = Path.Combine(AppContext.BaseDirectory, "assets");
        foreach (var candidate in new[] { Sanitize(model) + ".png", vendorKey + ".png" })
        {
            string p = Path.Combine(assets, candidate);
            if (File.Exists(p))
            {
                try { var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(p); bi.EndInit(); bi.Freeze(); _image = bi; break; }
                catch { }
            }
        }
        InvalidateVisual();
    }

    private static string Sanitize(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s.Trim(); }

    private static Color C(string key, Color fallback) => Application.Current.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (_image != null)
        {
            double scale = Math.Min(w / _image.PixelWidth, h / _image.PixelHeight);
            double iw = _image.PixelWidth * scale, ih = _image.PixelHeight * scale;
            dc.DrawImage(_image, new Rect((w - iw) / 2, (h - ih) / 2, iw, ih));
            return;
        }
        bool sata = _formFactor.Contains("2.5", StringComparison.Ordinal);
        var text = new SolidColorBrush(Colors.White);
        var muted = new SolidColorBrush(Color.FromArgb(200, 230, 235, 245));
        if (!sata)
        {
            // M.2 2280 stick: dark PCB with gold edge connector and two chips.
            double pw = w * 0.9, ph = Math.Min(h * 0.42, 62), px = (w - pw) / 2, py = (h - ph) / 2;
            var pcb = new LinearGradientBrush(Color.FromRgb(28, 32, 40), Color.FromRgb(16, 18, 24), 90);
            dc.DrawRoundedRectangle(pcb, new Pen(new SolidColorBrush(Color.FromRgb(60, 66, 80)), 1), new Rect(px, py, pw, ph), 4, 4);
            // Edge connector (M key).
            var gold = new LinearGradientBrush(Color.FromRgb(230, 190, 90), Color.FromRgb(180, 140, 50), 90);
            dc.DrawRectangle(gold, null, new Rect(px + pw - 26, py + 4, 22, ph - 8));
            for (int i = 0; i < 9; i++) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(120, 90, 30)), null, new Rect(px + pw - 26, py + 6 + i * (ph - 12) / 9, 22, 1));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 32, 40)), null, new Rect(px + pw - 26, py + ph * 0.62, 22, 5));
            // Mounting notch on the left.
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(90, 96, 110)), null, new Point(px + 8, py + ph / 2), 3.5, 3.5);
            // Controller and NAND packages.
            var chip = new SolidColorBrush(Color.FromRgb(10, 10, 12));
            var chipPen = new Pen(new SolidColorBrush(Color.FromRgb(70, 74, 86)), 0.8);
            dc.DrawRoundedRectangle(chip, chipPen, new Rect(px + 22, py + 8, ph * 0.8, ph - 16), 2, 2);
            dc.DrawRoundedRectangle(chip, chipPen, new Rect(px + 30 + ph * 0.8, py + 8, ph * 1.2, ph - 16), 2, 2);
            dc.DrawRoundedRectangle(chip, chipPen, new Rect(px + 40 + ph * 2.0, py + 8, ph * 1.2, ph - 16), 2, 2);
            // Label sticker with brand colour band.
            double lx = px + 44 + ph * 3.2, lw = Math.Max(40, pw - (lx - px) - 34);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(240, 242, 246)), null, new Rect(lx, py + 7, lw, ph - 14), 3, 3);
            dc.DrawRectangle(new SolidColorBrush(_brand), null, new Rect(lx, py + 7, lw, 9));
            Draw(dc, _vendor.ToUpperInvariant(), 8, FontWeights.Bold, new SolidColorBrush(Color.FromRgb(20, 24, 32)), lx + 4, py + 19, lw - 8);
            Draw(dc, _model, 7.5, FontWeights.Normal, new SolidColorBrush(Color.FromRgb(40, 46, 60)), lx + 4, py + 31, lw - 8);
            Draw(dc, _capacity, 7.5, FontWeights.Normal, new SolidColorBrush(Color.FromRgb(40, 46, 60)), lx + 4, py + 41, lw - 8);
        }
        else
        {
            double pw = Math.Min(w * 0.6, h * 1.4), ph = pw * 0.7, px = (w - pw) / 2, py = (h - ph) / 2;
            var body = new LinearGradientBrush(Color.FromRgb(36, 40, 48), Color.FromRgb(18, 20, 26), 45);
            dc.DrawRoundedRectangle(body, new Pen(new SolidColorBrush(Color.FromRgb(70, 76, 90)), 1), new Rect(px, py, pw, ph), 8, 8);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(240, 242, 246)), null, new Rect(px + 14, py + 14, pw - 28, ph - 28), 4, 4);
            dc.DrawRectangle(new SolidColorBrush(_brand), null, new Rect(px + 14, py + 14, pw - 28, 14));
            Draw(dc, _vendor.ToUpperInvariant(), 10, FontWeights.Bold, new SolidColorBrush(Color.FromRgb(20, 24, 32)), px + 20, py + 34, pw - 40);
            Draw(dc, _model, 9, FontWeights.Normal, new SolidColorBrush(Color.FromRgb(40, 46, 60)), px + 20, py + 50, pw - 40);
            Draw(dc, _capacity, 9, FontWeights.Normal, new SolidColorBrush(Color.FromRgb(40, 46, 60)), px + 20, py + 64, pw - 40);
        }
        // Vendor badge (text on brand colour) under the drawing.
        var badge = new FormattedText(_vendor.Length > 0 ? _vendor : "Unknown vendor", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 12, text, 1.0);
        double bw = badge.Width + 22, bh = badge.Height + 8;
        var brect = new Rect((w - bw) / 2, h - bh - 2, bw, bh);
        dc.DrawRoundedRectangle(new SolidColorBrush(_brand), null, brect, bh / 2, bh / 2);
        dc.DrawText(badge, new Point(brect.X + 11, brect.Y + 4));
    }

    private static void Draw(DrawingContext dc, string s, double size, FontWeight weight, Brush brush, double x, double y, double maxW)
    {
        if (string.IsNullOrEmpty(s)) return;
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, 1.0) { MaxTextWidth = Math.Max(10, maxW), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(ft, new Point(x, y));
    }
}
