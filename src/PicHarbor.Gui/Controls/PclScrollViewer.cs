using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MyScrollViewer:
/// Intercepts mouse wheel events and smoothly animates vertical scrolling
/// over 300ms using PCL2 AniEaseOutFluent(Power=6) damped physics.
/// </summary>
public class PclScrollViewer : ScrollViewer
{
    private double targetVerticalOffset;

    public static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.Register(nameof(AnimatedVerticalOffset), typeof(double), typeof(PclScrollViewer),
            new PropertyMetadata(0.0, OnAnimatedVerticalOffsetChanged));

    public double AnimatedVerticalOffset
    {
        get => (double)GetValue(AnimatedVerticalOffsetProperty);
        set => SetValue(AnimatedVerticalOffsetProperty, value);
    }

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PclScrollViewer sv)
        {
            sv.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (ScrollableHeight <= 0 || e.Delta == 0)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }

        // Do not intercept if inside open dropdowns
        if (e.OriginalSource is DependencyObject dep)
        {
            if (FindParent<ComboBox>(dep) is { IsDropDownOpen: true })
            {
                base.OnPreviewMouseWheel(e);
                return;
            }
        }

        e.Handled = true;

        // Calculate smooth destination offset
        targetVerticalOffset = Math.Clamp(targetVerticalOffset - (e.Delta * 0.8), 0, ScrollableHeight);

        var anim = new DoubleAnimation
        {
            To = targetVerticalOffset,
            Duration = TimeSpan.FromMilliseconds(300), // PCL2 exact 300ms duration
            EasingFunction = PclAnimation.EaseOutFluentScroll // PCL2 exact Power=6 easing
        };

        BeginAnimation(AnimatedVerticalOffsetProperty, anim);
    }

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);

        // Keep target offset in sync if user drags the scrollbar thumb directly
        if (Math.Abs(VerticalOffset - targetVerticalOffset) > 100 && e.VerticalChange != 0)
        {
            targetVerticalOffset = VerticalOffset;
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parent = child;
        while (parent != null)
        {
            if (parent is T typed) return typed;
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
    }
}
