using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MyCheckBox:
/// - 18x18 rounded border with 1.1px stroke
/// - Snappy pop-in checkmark animation via ScaleTransform and BackEase
/// </summary>
public class PclCheckBox : CheckBox
{
    private ScaleTransform? checkScale;
    private Border? boxBorder;
    private SolidColorBrush? borderBrush;

    static PclCheckBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclCheckBox),
            new FrameworkPropertyMetadata(typeof(PclCheckBox)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        var checkPath = GetTemplateChild("PART_CheckMark") as Path;
        if (checkPath != null)
        {
            double initialScale = IsChecked == true ? 1.0 : 0.0;
            checkScale = new ScaleTransform(initialScale, initialScale, 6, 6);
            checkPath.RenderTransform = checkScale;
        }

        boxBorder = GetTemplateChild("PART_BoxBorder") as Border;
        if (boxBorder != null)
        {
            borderBrush = (boxBorder.BorderBrush as SolidColorBrush)?.Clone() ?? new SolidColorBrush(Color.FromRgb(0x34, 0x3D, 0x4A));
            boxBorder.BorderBrush = borderBrush;
        }
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        if (checkScale != null)
        {
            // Snappy pop-in with BackEase overshoot
            PclAnimation.AnimateScale(checkScale, 1.0, 150, PclAnimation.EaseOutBack);
        }
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        if (checkScale != null)
        {
            // Smooth collapse
            PclAnimation.AnimateScale(checkScale, 0.0, 120, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (borderBrush != null)
        {
            PclAnimation.AnimateColor(borderBrush, Color.FromRgb(0x13, 0x70, 0xF3), 100, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (borderBrush != null)
        {
            PclAnimation.AnimateColor(borderBrush, Color.FromRgb(0x34, 0x3D, 0x4A), 150, PclAnimation.EaseOutFluentMiddle);
        }
    }
}
