using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace BlueBubbles.Windows.Controls;

public sealed partial class TypingIndicator : UserControl
{
    private Storyboard? _storyboard;

    public TypingIndicator()
    {
        InitializeComponent();
        Loaded += (_, _) => StartAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    private void StartAnimation()
    {
        StopAnimation();

        // A staggered opacity pulse keeps the dots perfectly centered (no layout
        // shift) while reading as the familiar three-dot "typing" animation.
        _storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var dots = new[] { Dot1, Dot2, Dot3 };

        for (var i = 0; i < 3; i++)
        {
            var anim = new DoubleAnimation
            {
                From = 0.25,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                AutoReverse = true,
                BeginTime = TimeSpan.FromMilliseconds(i * 180),
                EasingFunction = new SineEase()
            };
            Storyboard.SetTarget(anim, dots[i]);
            Storyboard.SetTargetProperty(anim, "Opacity");
            _storyboard.Children.Add(anim);
        }

        _storyboard.Begin();
    }

    private void StopAnimation()
    {
        _storyboard?.Stop();
        _storyboard = null;
    }
}
