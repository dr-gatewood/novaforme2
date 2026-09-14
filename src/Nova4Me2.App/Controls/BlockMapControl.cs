using System.Windows;
using System.Windows.Media;
using Nova4Me2.Core.Recovery;

namespace Nova4Me2.App.Controls;

/// <summary>Draws a <see cref="BlockMap"/> as a grid of cells with a soft glow on the cells currently being read.</summary>
public sealed class BlockMapControl : FrameworkElement
{
    private BlockMap? _map;
    private int _cursorCell = -1;
    private double _pulse;
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    public BlockMapControl()
    {
        MinHeight = 120;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => { _pulse = (_pulse + 0.08) % (Math.PI * 2); if (_cursorCell >= 0) InvalidateVisual(); };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public void Update(BlockMap? map, long? cursorOffset)
    {
        _map = map;
        _cursorCell = map != null && cursorOffset is { } c ? map.CellOf(c) : -1;
        InvalidateVisual();
    }

    private static Color C(string key, Color fallback) => Application.Current.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var bg = new SolidColorBrush(C("Bg2", Colors.Black));
        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, w, h), 8, 8);
        if (_map == null) return;
        int cells = _map.Cells;
        int cols = (int)Math.Max(8, Math.Sqrt(cells * w / Math.Max(1, h)));
        int rows = (int)Math.Ceiling(cells / (double)cols);
        double cw = (w - 8) / cols, ch = (h - 8) / rows;
        double gap = Math.Min(1.5, cw * 0.12);
        var pending = new SolidColorBrush(C("PanelAlt", Colors.Gray));
        var good = new SolidColorBrush(C("Good", Colors.Green));
        var bad = new SolidColorBrush(C("Bad", Colors.Red));
        var slow = new SolidColorBrush(C("Warn", Colors.Orange));
        var skipped = new SolidColorBrush(Color.FromArgb(255, 200, 120, 60));
        var reading = new SolidColorBrush(C("Accent", Colors.DodgerBlue));
        pending.Freeze(); good.Freeze(); bad.Freeze(); slow.Freeze(); skipped.Freeze(); reading.Freeze();
        for (int i = 0; i < cells; i++)
        {
            int r = i / cols, c = i % cols;
            var rect = new Rect(4 + c * cw + gap / 2, 4 + r * ch + gap / 2, Math.Max(0.5, cw - gap), Math.Max(0.5, ch - gap));
            Brush b = _map.States[i] switch
            {
                BlockState.Good => good, BlockState.Bad => bad, BlockState.Slow => slow, BlockState.Skipped => skipped, BlockState.Reading => reading, _ => pending
            };
            dc.DrawRectangle(b, null, rect);
        }
        if (_cursorCell >= 0 && _cursorCell < cells)
        {
            int r = _cursorCell / cols, c = _cursorCell % cols;
            double glow = 3 + 3 * (0.5 + 0.5 * Math.Sin(_pulse));
            var rect = new Rect(4 + c * cw - glow, 4 + r * ch - glow, cw + 2 * glow, ch + 2 * glow);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(160, reading.Color.R, reading.Color.G, reading.Color.B)), 1.5);
            dc.DrawRoundedRectangle(null, pen, rect, 3, 3);
        }
    }
}
