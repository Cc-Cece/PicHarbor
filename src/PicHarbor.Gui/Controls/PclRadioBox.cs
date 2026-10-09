using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MyRadioBox (circular option radio button):
/// - 18x18 outer ellipse with 1.1px stroke
/// - Center dot expands from 0 to 9px with BackEase elastic pop-in
/// - Outer ellipse breathes on check/uncheck
/// </summary>
public class PclRadioBox : RadioButton
{
    private Ellipse? shapeBorder;
    private Ellipse? shapeDot;
    private SolidColorBrush? borderBrush;

    static PclRadioBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclRadioBox),
            new FrameworkPropertyMetadata(typeof(PclRadioBox)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        shapeBorder = GetTemplateChild("PART_Border") as Ellipse;
        shapeDot = GetTemplateChild("PART_Dot") as Ellipse;

        if (shapeBorder != null)
        {
            borderBrush = (shapeBorder.Stroke as SolidColorBrush)?.Clone() ?? new SolidColorBrush(Color.FromRgb(0x34, 0x3D, 0x4A));
            shapeBorder.Stroke = borderBrush;
        }

        UpdateDotState(false);
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        UpdateDotState(true);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        UpdateDotState(true);
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
            Color target = IsChecked == true ? Color.FromRgb(0x0B, 0x5B, 0xCB) : Color.FromRgb(0x34, 0x3D, 0x4A);
            PclAnimation.AnimateColor(borderBrush, target, 150, PclAnimation.EaseOutFluentMiddle);
        }
    }

    private void UpdateDotState(bool animate)
    {
        if (shapeDot == null || shapeBorder == null) return;

        bool isChecked = IsChecked == true;
        double targetSize = isChecked ? 9.0 : 0.0;
        double targetOpacity = isChecked ? 1.0 : 0.0;

        if (animate)
        {
            // PCL2 exact physics: dot expands with BackEase, border breathes
            PclAnimation.AnimateDouble(shapeDot, WidthProperty, targetSize, 180, PclAnimation.EaseOutBack);
            PclAnimation.AnimateDouble(shapeDot, HeightProperty, targetSize, 180, PclAnimation.EaseOutBack);
            PclAnimation.AnimateDouble(shapeDot, OpacityProperty, targetOpacity, 120, PclAnimation.EaseOutFluentMiddle);

            if (borderBrush != null)
            {
                Color targetBorder = isChecked ? Color.FromRgb(0x0B, 0x5B, 0xCB) : Color.FromRgb(0x34, 0x3D, 0x4A);
                PclAnimation.AnimateColor(borderBrush, targetBorder, 120, PclAnimation.EaseOutFluentMiddle);
            }
        }
        else
        {
            shapeDot.Width = targetSize;
            shapeDot.Height = targetSize;
            shapeDot.Opacity = targetOpacity;
        }
    }
}
