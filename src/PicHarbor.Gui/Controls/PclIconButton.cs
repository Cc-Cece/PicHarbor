using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

public enum PclIconTheme
{
    White,
    Color,
    Close
}

/// <summary>
/// 1:1 port of PCL2 MyIconButton:
/// - 400ms 0.8 press scale down with Power=4
/// - 250ms 1.05 elastic pop bounce back on release
/// - Smooth hover background glow in 120ms
/// </summary>
public class PclIconButton : Button
{
    private ScaleTransform? scaleTransform;
    private Border? borderContainer;
    private SolidColorBrush? backgroundBrush;

    public static readonly DependencyProperty LogoProperty =
        DependencyProperty.Register(nameof(Logo), typeof(Geometry), typeof(PclIconButton),
            new PropertyMetadata(null));

    public static readonly DependencyProperty LogoScaleProperty =
        DependencyProperty.Register(nameof(LogoScale), typeof(double), typeof(PclIconButton),
            new PropertyMetadata(0.75));

    public static readonly DependencyProperty IconThemeProperty =
        DependencyProperty.Register(nameof(IconTheme), typeof(PclIconTheme), typeof(PclIconButton),
            new PropertyMetadata(PclIconTheme.White));

    public Geometry Logo
    {
        get => (Geometry)GetValue(LogoProperty);
        set => SetValue(LogoProperty, value);
    }

    public double LogoScale
    {
        get => (double)GetValue(LogoScaleProperty);
        set => SetValue(LogoScaleProperty, value);
    }

    public PclIconTheme IconTheme
    {
        get => (PclIconTheme)GetValue(IconThemeProperty);
        set => SetValue(IconThemeProperty, value);
    }

    static PclIconButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclIconButton),
            new FrameworkPropertyMetadata(typeof(PclIconButton)));
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

            backgroundBrush = new SolidColorBrush(Colors.Transparent);
            borderContainer.Background = backgroundBrush;
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (backgroundBrush != null)
        {
            Color hoverColor = IconTheme switch
            {
                PclIconTheme.Close => Color.FromArgb(200, 239, 68, 68), // Red hover for close button
                PclIconTheme.White => Color.FromArgb(50, 255, 255, 255), // PCL2: 50, 255, 255, 255
                _ => Color.FromArgb(40, 19, 112, 243)
            };
            PclAnimation.AnimateColor(backgroundBrush, hoverColor, 120, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (backgroundBrush != null)
        {
            PclAnimation.AnimateColor(backgroundBrush, Colors.Transparent, 150, PclAnimation.EaseOutFluentMiddle);
        }
        if (scaleTransform != null)
        {
            PclAnimation.AnimateScale(scaleTransform, 1.0, 250, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (scaleTransform != null)
        {
            // PCL2 exact physics: 400ms down to 0.8 with Power=4
            PclAnimation.AnimateScale(scaleTransform, 0.8, 400, PclAnimation.EaseOutFluentStrong);
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (scaleTransform != null)
        {
            // PCL2 exact physics: bounce to 1.05 and settle to 1.0 with BackEase
            PclAnimation.AnimateScale(scaleTransform, 1.0, 250, PclAnimation.EaseOutBack);
        }
    }
}
