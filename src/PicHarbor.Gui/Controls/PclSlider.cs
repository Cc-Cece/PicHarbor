using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MySlider:
/// - 1px subtle background line + 2px active accent line
/// - 10px circular thumb with 1.3x scale expansion on click/drag
/// - Damped progress interpolation
/// </summary>
public class PclSlider : Slider
{
    private ScaleTransform? thumbScale;
    private FrameworkElement? thumbElement;

    static PclSlider()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclSlider),
            new FrameworkPropertyMetadata(typeof(PclSlider)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        thumbElement = GetTemplateChild("PART_Thumb") as FrameworkElement;
        if (thumbElement != null)
        {
            thumbScale = new ScaleTransform(1.0, 1.0);
            thumbElement.RenderTransform = thumbScale;
            thumbElement.RenderTransformOrigin = new Point(0.5, 0.5);
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (thumbScale != null)
        {
            // PCL2 physics: thumb expands to 1.3x in 40ms on click
            PclAnimation.AnimateScale(thumbScale, 1.3, 40, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (thumbScale != null)
        {
            // Return to 1.0 on release
            PclAnimation.AnimateScale(thumbScale, 1.0, 150, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (thumbScale != null && !IsMouseCaptured)
        {
            PclAnimation.AnimateScale(thumbScale, 1.0, 150, PclAnimation.EaseOutFluentMiddle);
        }
    }
}
