using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MyCard:
/// - 90ms shadow breathing: idle opacity 0.07 -> hover opacity 0.30
/// - Collapsible Swap header with chevron rotation in 250ms
/// - Smooth content height animation in 150ms with Power=5
/// </summary>
public class PclCard : HeaderedContentControl
{
    private DropShadowEffect? cardShadow;
    private RotateTransform? chevronRotate;
    private FrameworkElement? contentContainer;
    private Border? headerBorder;
    private double naturalContentHeight = double.NaN;

    public static readonly DependencyProperty CanSwapProperty =
        DependencyProperty.Register(nameof(CanSwap), typeof(bool), typeof(PclCard),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsSwappedProperty =
        DependencyProperty.Register(nameof(IsSwapped), typeof(bool), typeof(PclCard),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsSwappedChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PclCard),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(PclCard),
            new PropertyMetadata(new CornerRadius(5)));

    public bool CanSwap
    {
        get => (bool)GetValue(CanSwapProperty);
        set => SetValue(CanSwapProperty, value);
    }

    public bool IsSwapped
    {
        get => (bool)GetValue(IsSwappedProperty);
        set => SetValue(IsSwappedProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static PclCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclCard),
            new FrameworkPropertyMetadata(typeof(PclCard)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        var rootBorder = GetTemplateChild("PART_CardBorder") as Border;
        if (rootBorder != null)
        {
            cardShadow = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 1,
                Direction = 270,
                Color = Color.FromRgb(0x1E, 0x29, 0x3B),
                Opacity = 0.07 // PCL2 DropShadowIdleOpacity
            };
            rootBorder.Effect = cardShadow;
        }

        var chevronPath = GetTemplateChild("PART_Chevron") as Path;
        if (chevronPath != null)
        {
            chevronRotate = new RotateTransform(IsSwapped ? 0 : 180);
            chevronPath.RenderTransform = chevronRotate;
            chevronPath.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        contentContainer = GetTemplateChild("PART_ContentContainer") as FrameworkElement;
        headerBorder = GetTemplateChild("PART_HeaderBorder") as Border;

        if (headerBorder != null)
        {
            headerBorder.MouseLeftButtonDown += HeaderBorder_MouseLeftButtonDown;
        }

        if (contentContainer != null && IsSwapped)
        {
            contentContainer.Visibility = Visibility.Collapsed;
        }
    }

    private void HeaderBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CanSwap)
        {
            IsSwapped = !IsSwapped;
            e.Handled = true;
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (cardShadow != null)
        {
            // PCL2 shadow breathing in 90ms: 0.07 -> 0.28
            PclAnimation.AnimateDouble(cardShadow, DropShadowEffect.OpacityProperty, 0.28, 90, PclAnimation.EaseOutFluentMiddle);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (cardShadow != null)
        {
            // PCL2 shadow breathing in 90ms: back to 0.07
            PclAnimation.AnimateDouble(cardShadow, DropShadowEffect.OpacityProperty, 0.07, 90, PclAnimation.EaseOutFluentMiddle);
        }
    }

    private static void OnIsSwappedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PclCard card)
        {
            card.AnimateSwap((bool)e.NewValue);
        }
    }

    private void AnimateSwap(bool swapped)
    {
        // 1. Rotate chevron arrow: 180 (expanded) -> 0 (collapsed) in 250ms with Power=5
        if (chevronRotate != null)
        {
            double targetAngle = swapped ? 0 : 180;
            PclAnimation.AnimateDouble(chevronRotate, RotateTransform.AngleProperty, targetAngle, 250, PclAnimation.EaseOutFluentExtraStrong);
        }

        // 2. Smooth height collapse/expand
        if (contentContainer == null) return;

        if (swapped)
        {
            // Collapse
            naturalContentHeight = contentContainer.ActualHeight > 0 ? contentContainer.ActualHeight : 100;
            contentContainer.Height = naturalContentHeight;

            PclAnimation.AnimateDouble(contentContainer, HeightProperty, 0, 150, PclAnimation.EaseOutFluentExtraStrong, onCompleted: () =>
            {
                if (IsSwapped)
                {
                    contentContainer.Visibility = Visibility.Collapsed;
                }
            });
            PclAnimation.AnimateDouble(contentContainer, OpacityProperty, 0, 120, PclAnimation.EaseOutFluentMiddle);
        }
        else
        {
            // Expand
            contentContainer.Visibility = Visibility.Visible;
            double targetHeight = double.IsNaN(naturalContentHeight) || naturalContentHeight <= 0 ? 150 : naturalContentHeight;

            PclAnimation.AnimateDouble(contentContainer, HeightProperty, targetHeight, 180, PclAnimation.EaseOutFluentExtraStrong, onCompleted: () =>
            {
                contentContainer.Height = double.NaN; // Restore auto sizing
            });
            PclAnimation.AnimateDouble(contentContainer, OpacityProperty, 1.0, 150, PclAnimation.EaseOutFluentMiddle);
        }
    }
}
