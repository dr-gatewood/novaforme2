using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Nova4Me2.App.Services;

public static class Animations
{
    public static void FadeIn(UIElement e, int ms = 220, double fromY = 8)
    {
        e.Opacity = 0;
        var tt = new TranslateTransform(0, fromY);
        e.RenderTransform = tt;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public static void FadeOut(UIElement e, int ms, Action done)
    {
        var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        a.Completed += (_, _) => done();
        e.BeginAnimation(UIElement.OpacityProperty, a);
    }

    public static void SlideIn(FrameworkElement e, double fromX, int ms = 260)
    {
        var tt = new TranslateTransform(fromX, 0);
        e.RenderTransform = tt;
        e.Opacity = 0;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)));
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public static void SlideOut(FrameworkElement e, double toX, int ms, Action done)
    {
        var tt = e.RenderTransform as TranslateTransform ?? new TranslateTransform();
        e.RenderTransform = tt;
        var a = new DoubleAnimation(toX, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        a.Completed += (_, _) => done();
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms)));
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    /// <summary>Animate a ProgressBar to a new value instead of jumping, so the bar visibly grows.</summary>
    public static void Grow(System.Windows.Controls.ProgressBar bar, double value)
    {
        double target = Math.Clamp(value, bar.Minimum, bar.Maximum);
        if (target < bar.Value) { bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null); bar.Value = target; return; }
        var a = new DoubleAnimation(target, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, a);
    }

    public static void Pulse(UIElement e)
    {
        var a = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        e.BeginAnimation(UIElement.OpacityProperty, a);
    }

    public static void StopPulse(UIElement e)
    {
        e.BeginAnimation(UIElement.OpacityProperty, null);
        e.Opacity = 1;
    }
}
