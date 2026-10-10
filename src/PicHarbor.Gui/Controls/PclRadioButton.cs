using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MyRadioButton (pill capsule navigation tab):
/// - Pill shape: CornerRadius="13.5", Height=27
/// - Vector logo + Label
/// - Checked: pure white background + ColorBrush2 (#0b5bcb) text (120ms)
/// - Hover: subtle translucent white glow in 90ms, leaves in 150ms
/// </summary>
public class PclRadioButton : RadioButton
{
    private Border? borderContainer;
    private SolidColorBrush? backgroundBrush;
    private SolidColorBrush? foregroundBrush;

    public static readonly DependencyProperty LogoProperty =
        DependencyProperty.Register(nameof(Logo), typeof(Geometry), typeof(PclRadioButton),
            new PropertyMetadata(null));

    public static readonly DependencyProperty LogoScaleProperty =
        DependencyProperty.Register(nameof(LogoScale), typeof(double), typeof(PclRadioButton),
            new PropertyMetadata(0.85));

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

    static PclRadioButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclRadioButton),
            new FrameworkPropertyMetadata(typeof(PclRadioButton)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        borderContainer = GetTemplateChild("PART_Border") as Border;
        if (borderContainer != null)
        {
            backgroundBrush = new SolidColorBrush(IsChecked == true ? Colors.White : Colors.Transparent);
            foregroundBrush = new SolidColorBrush(IsChecked == true ? Color.FromRgb(0x0B, 0x5B, 0xCB) : Colors.White);

            borderContainer.Background = backgroundBrush;
            Foreground = foregroundBrush;
        }
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        AnimateState(isHover: false);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        AnimateState(isHover: false);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (IsChecked != true)
        {
            AnimateState(isHover: true);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (IsChecked != true)
        {
            AnimateState(isHover: false);
        }
    }

    private void AnimateState(bool isHover)
    {
        if (backgroundBrush == null || foregroundBrush == null) return;

        if (IsChecked == true)
        {
            // Checked: White background, PCL blue foreground
            PclAnimation.AnimateColor(backgroundBrush, Colors.White, 120, PclAnimation.EaseOutFluentMiddle);
            PclAnimation.AnimateColor(foregroundBrush, Color.FromRgb(0x0B, 0x5B, 0xCB), 120, PclAnimation.EaseOutFluentMiddle);
        }
        else
        {
            // Unchecked: transparent or hover translucent white
            Color targetBg = isHover ? Color.FromArgb(45, 255, 255, 255) : Colors.Transparent;
            int duration = isHover ? 90 : 150; // PCL2 AnimationTimeOfMouseIn / Out

            PclAnimation.AnimateColor(backgroundBrush, targetBg, duration, PclAnimation.EaseOutFluentMiddle);
            PclAnimation.AnimateColor(foregroundBrush, Colors.White, duration, PclAnimation.EaseOutFluentMiddle);
        }
    }
}
