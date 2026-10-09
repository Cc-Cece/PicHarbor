using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

public enum PclButtonType
{
    Normal,
    Hero,
    Highlight,
    Danger
}

/// <summary>
/// 1:1 port of PCL2 MyButton with authentic physics:
/// - 80ms 0.955 press scale down with Power=5
/// - 300ms elastic bounce back on release with Power=3
/// - 800ms smooth recovery on leave with Power=4
/// - 100ms in / 200ms out color transitions
/// </summary>
public class PclButton : Button
{
    private ScaleTransform? scaleTransform;
    private Border? borderContainer;
    private SolidColorBrush? dynamicBackgroundBrush;
    private SolidColorBrush? dynamicBorderBrush;

    public static readonly DependencyProperty ButtonTypeProperty =
        DependencyProperty.Register(nameof(ButtonType), typeof(PclButtonType), typeof(PclButton),
            new PropertyMetadata(PclButtonType.Normal, OnButtonTypeChanged));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PclButton),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(PclButton),
            new PropertyMetadata(new CornerRadius(4)));

    public PclButtonType ButtonType
    {
        get => (PclButtonType)GetValue(ButtonTypeProperty);
        set => SetValue(ButtonTypeProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static PclButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclButton),
            new FrameworkPropertyMetadata(typeof(PclButton)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        borderContainer = GetTemplateChild("PART_Border") as Border;
        if (borderContainer != null)
        {
            scaleTransform = new ScaleTransform(1.0, 1.0);
            borderContainer.RenderTransform = scaleTransform;
            borderContainer.RenderTransformOrigin = new Point(0.5, 0.5);

            dynamicBackgroundBrush = new SolidColorBrush(GetDefaultBgColor());
            dynamicBorderBrush = new SolidColorBrush(GetDefaultBorderColor());

            borderContainer.Background = dynamicBackgroundBrush;
            borderContainer.BorderBrush = dynamicBorderBrush;
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        AnimateHover(true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        AnimateHover(false);

        if (scaleTransform != null)
        {
            PclAnimation.AnimateScale(scaleTransform, 1.0, 800, PclAnimation.EaseOutFluentStrong);
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (scaleTransform != null)
        {
            // PCL2 exact physics: 80ms down to 0.955 with Power=5
            PclAnimation.AnimateScale(scaleTransform, 0.955, 80, PclAnimation.EaseOutFluentExtraStrong);
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);

        if (scaleTransform != null)
        {
            // PCL2 exact physics: 300ms bounce back to 1.0 with Power=3
            PclAnimation.AnimateScale(scaleTransform, 1.0, 300, PclAnimation.EaseOutFluentMiddle, delayMs: 10);
        }
    }

    private void AnimateHover(bool isHover)
    {
        if (dynamicBackgroundBrush == null || dynamicBorderBrush == null) return;

        int duration = isHover ? 100 : 200; // PCL2 AnimationColorIn / Out
        Color targetBg = isHover ? GetHoverBgColor() : GetDefaultBgColor();
        Color targetBorder = isHover ? GetHoverBorderColor() : GetDefaultBorderColor();

        PclAnimation.AnimateColor(dynamicBackgroundBrush, targetBg, duration, PclAnimation.EaseOutFluentMiddle);
        PclAnimation.AnimateColor(dynamicBorderBrush, targetBorder, duration, PclAnimation.EaseOutFluentMiddle);
    }

    private Color GetDefaultBgColor() => ButtonType switch
    {
        PclButtonType.Danger => Color.FromArgb(0x40, 0xFB, 0xDD, 0xDD),
        _ => Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) // ColorBrushHalfWhite
    };

    private Color GetHoverBgColor() => ButtonType switch
    {
        PclButtonType.Danger => Color.FromArgb(0x80, 0xFB, 0xDD, 0xDD),
        _ => Color.FromRgb(0xE0, 0xEA, 0xFD) // ColorBrush7
    };

    public PclButton()
    {
        Loaded += (s, e) => AnimateHover(false);
        IsEnabledChanged += (s, e) => AnimateHover(false);
    }

    private Color GetDefaultBorderColor()
    {
        if (!IsEnabled) return Color.FromRgb(0xA6, 0xA6, 0xA6); // ColorBrushGray4
        return ButtonType switch
        {
            PclButtonType.Hero => Color.FromRgb(0x0B, 0x5B, 0xCB),      // ColorBrush2
            PclButtonType.Highlight => Color.FromRgb(0x0B, 0x5B, 0xCB), // ColorBrush2
            PclButtonType.Danger => Color.FromRgb(0xCE, 0x21, 0x11),
            _ => Color.FromRgb(0x34, 0x3D, 0x4A)                        // ColorBrush1
        };
    }

    private Color GetHoverBorderColor()
    {
        if (!IsEnabled) return Color.FromRgb(0xA6, 0xA6, 0xA6);
        return ButtonType switch
        {
            PclButtonType.Hero => Color.FromRgb(0x13, 0x70, 0xF3),      // ColorBrush3
            PclButtonType.Highlight => Color.FromRgb(0x13, 0x70, 0xF3), // ColorBrush3
            PclButtonType.Danger => Color.FromRgb(0xFF, 0x4C, 0x4C),
            _ => Color.FromRgb(0x13, 0x70, 0xF3)                        // ColorBrush3
        };
    }

    private static void OnButtonTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PclButton btn)
        {
            btn.AnimateHover(false);
        }
    }
}
