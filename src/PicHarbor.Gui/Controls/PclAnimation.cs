using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 ModAnimation engine easing functions and animation helpers.
/// </summary>
public static class PclAnimation
{
    // Easing functions identical to PCL2 AniEaseOutFluent
    public static readonly IEasingFunction EaseOutFluentWeak = new PowerEase { Power = 2, EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseOutFluentMiddle = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseOutFluentStrong = new PowerEase { Power = 4, EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseOutFluentExtraStrong = new PowerEase { Power = 5, EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseOutFluentScroll = new PowerEase { Power = 6, EasingMode = EasingMode.EaseOut };

    // Back ease for elastic pop-ins
    public static readonly IEasingFunction EaseOutBack = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction EaseOutBackStrong = new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut };

    public static void AnimateDouble(UIElement target, DependencyProperty property, double toValue, int durationMs, IEasingFunction? easing = null, int delayMs = 0, Action? onCompleted = null)
    {
        var anim = new DoubleAnimation
        {
            To = toValue,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing ?? EaseOutFluentMiddle,
            BeginTime = delayMs > 0 ? TimeSpan.FromMilliseconds(delayMs) : TimeSpan.Zero
        };

        if (onCompleted != null)
        {
            anim.Completed += (_, _) => onCompleted();
        }

        target.BeginAnimation(property, anim);
    }

    public static void AnimateDouble(Animatable target, DependencyProperty property, double toValue, int durationMs, IEasingFunction? easing = null, int delayMs = 0, Action? onCompleted = null)
    {
        var anim = new DoubleAnimation
        {
            To = toValue,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing ?? EaseOutFluentMiddle,
            BeginTime = delayMs > 0 ? TimeSpan.FromMilliseconds(delayMs) : TimeSpan.Zero
        };

        if (onCompleted != null)
        {
            anim.Completed += (_, _) => onCompleted();
        }

        target.BeginAnimation(property, anim);
    }

    public static void AnimateScale(ScaleTransform transform, double toScale, int durationMs, IEasingFunction? easing = null, int delayMs = 0)
    {
        var anim = new DoubleAnimation
        {
            To = toScale,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing ?? EaseOutFluentMiddle,
            BeginTime = delayMs > 0 ? TimeSpan.FromMilliseconds(delayMs) : TimeSpan.Zero
        };

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    public static void AnimateColor(SolidColorBrush brush, Color toColor, int durationMs, IEasingFunction? easing = null)
    {
        var anim = new ColorAnimation
        {
            To = toColor,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing ?? EaseOutFluentMiddle
        };

        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}
