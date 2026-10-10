using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MyListItem:
/// - Hover zoom-in background pill (Scale 0.85 -> 1.0, Opacity 0 -> 1 in 120ms)
/// - Left accent indicator bar on selection
/// </summary>
public class PclListItem : RadioButton
{
    private Border? hoverPill;
    private Border? checkIndicator;
    private ScaleTransform? hoverScale;

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(PclListItem),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LogoProperty =
        DependencyProperty.Register(nameof(Logo), typeof(Geometry), typeof(PclListItem),
            new PropertyMetadata(null));

    public static readonly DependencyProperty LogoScaleProperty =
        DependencyProperty.Register(nameof(LogoScale), typeof(double), typeof(PclListItem),
            new PropertyMetadata(1.0));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

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

    static PclListItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclListItem),
            new FrameworkPropertyMetadata(typeof(PclListItem)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        hoverPill = GetTemplateChild("PART_HoverPill") as Border;
        checkIndicator = GetTemplateChild("PART_Indicator") as Border;

        if (hoverPill != null)
        {
            hoverScale = new ScaleTransform(0.85, 0.85);
            hoverPill.RenderTransform = hoverScale;
            hoverPill.RenderTransformOrigin = new Point(0.5, 0.5);
            hoverPill.Opacity = 0.0;
        }

        UpdateSelectionState(false);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (hoverPill != null && hoverScale != null)
        {
            PclAnimation.AnimateDouble(hoverPill, OpacityProperty, 1.0, 120, PclAnimation.EaseOutFluentMiddle);
            PclAnimation.AnimateScale(hoverScale, 1.0, 120, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverPill != null && hoverScale != null)
        {
            PclAnimation.AnimateDouble(hoverPill, OpacityProperty, 0.0, 150, PclAnimation.EaseOutFluentMiddle);
            PclAnimation.AnimateScale(hoverScale, 0.85, 150, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        UpdateSelectionState(true);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        UpdateSelectionState(true);
    }

    private void UpdateSelectionState(bool animate)
    {
        if (checkIndicator == null) return;

        double targetHeight = IsChecked == true ? 18.0 : 0.0;
        double targetOpacity = IsChecked == true ? 1.0 : 0.0;

        if (animate)
        {
            PclAnimation.AnimateDouble(checkIndicator, HeightProperty, targetHeight, 150, PclAnimation.EaseOutFluentMiddle);
            PclAnimation.AnimateDouble(checkIndicator, OpacityProperty, targetOpacity, 150, PclAnimation.EaseOutFluentMiddle);
        }
        else
        {
            checkIndicator.Height = targetHeight;
            checkIndicator.Opacity = targetOpacity;
        }
    }
}
