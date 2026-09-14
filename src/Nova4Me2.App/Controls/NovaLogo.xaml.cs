using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Nova4Me2.App.Controls;

public partial class NovaLogo : UserControl
{
    public NovaLogo()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(40)) { RepeatBehavior = RepeatBehavior.Forever };
            StarRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
            var pulse = new DoubleAnimation(0.75, 1.0, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Glow.BeginAnimation(OpacityProperty, pulse);
        };
    }
}
