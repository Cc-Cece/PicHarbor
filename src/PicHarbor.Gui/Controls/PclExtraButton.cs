using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MyExtraButton:
/// - Floating circular action button (40x40)
/// - Glowing drop shadow
/// - Elastic click and hover scale animation
/// </summary>
public class PclExtraButton : Button
{
    private ScaleTransform? scaleTransform;

    public static readonly DependencyProperty LogoProperty =
        DependencyProperty.Register(nameof(Logo), typeof(Geometry), typeof(PclExtraButton),
            new PropertyMetadata(null));

    public Geometry Logo
    {
        get => (Geometry)GetValue(LogoProperty);
        set => SetValue(LogoProperty, value);
    }

    static PclExtraButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclExtraButton),
            new FrameworkPropertyMetadata(typeof(PclExtraButton)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        var root = GetTemplateChild("PART_Root") as FrameworkElement;
        if (root != null)
        {
            scaleTransform = new ScaleTransform(1.0, 1.0);
            root.RenderTransform = scaleTransform;
            root.RenderTransformOrigin = new Point(0.5, 0.5);
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (scaleTransform != null)
        {
            PclAnimation.AnimateScale(scaleTransform, 1.1, 120, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (scaleTransform != null)
        {
            PclAnimation.AnimateScale(scaleTransform, 1.0, 150, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (scaleTransform != null)
        {
            PclAnimation.AnimateScale(scaleTransform, 0.9, 60, PclAnimation.EaseOutFluentExtraStrong);
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (scaleTransform != null)
        {
            PclAnimation.AnimateScale(scaleTransform, 1.1, 200, PclAnimation.EaseOutBack);
        }
    }
}
