using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MyTextBox:
/// - CornerRadius 3, 1px stroke
/// - Border color transitions on focus/hover (100ms)
/// - Integrated placeholder / hint text
/// </summary>
public class PclTextBox : TextBox
{
    private Border? mainBorder;
    private SolidColorBrush? borderBrush;
    private SolidColorBrush? backgroundBrush;

    public static readonly DependencyProperty HintTextProperty =
        DependencyProperty.Register(nameof(HintText), typeof(string), typeof(PclTextBox),
            new PropertyMetadata(string.Empty));

    public string HintText
    {
        get => (string)GetValue(HintTextProperty);
        set => SetValue(HintTextProperty, value);
    }

    static PclTextBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclTextBox),
            new FrameworkPropertyMetadata(typeof(PclTextBox)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        mainBorder = GetTemplateChild("PART_Border") as Border;
        if (mainBorder != null)
        {
            borderBrush = (mainBorder.BorderBrush as SolidColorBrush)?.Clone() ?? new SolidColorBrush(Color.FromRgb(0x96, 0xC0, 0xF9));
            backgroundBrush = (mainBorder.Background as SolidColorBrush)?.Clone() ?? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

            mainBorder.BorderBrush = borderBrush;
            mainBorder.Background = backgroundBrush;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        AnimateState();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        AnimateState();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        AnimateState();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        AnimateState();
    }

    private void AnimateState()
    {
        if (borderBrush == null) return;

        Color targetBorder = IsKeyboardFocused
            ? Color.FromRgb(0x13, 0x70, 0xF3) // ColorBrush3
            : (IsMouseOver ? Color.FromRgb(0x48, 0x90, 0xF5) : Color.FromRgb(0x96, 0xC0, 0xF9)); // ColorBrush4 / Bg0

        PclAnimation.AnimateColor(borderBrush, targetBorder, 100, PclAnimation.EaseOutFluentMiddle);
    }
}
