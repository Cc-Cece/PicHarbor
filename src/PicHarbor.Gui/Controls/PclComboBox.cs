using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MyComboBox:
/// - 3px corner radius, 1px stroke
/// - Border and background color transitions on hover (100ms) and open (10ms)
/// - Rotating chevron arrow (0 -> 180 in 200ms)
/// - White floating dropdown card with PCL2 item highlights
/// </summary>
public class PclComboBox : ComboBox
{
    private Border? mainBorder;
    private SolidColorBrush? borderBrush;
    private SolidColorBrush? backgroundBrush;

    static PclComboBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclComboBox),
            new FrameworkPropertyMetadata(typeof(PclComboBox)));
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

    protected override void OnDropDownOpened(EventArgs e)
    {
        base.OnDropDownOpened(e);
        AnimateState();
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e);
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
        if (borderBrush == null || backgroundBrush == null) return;

        Color targetBorder;
        Color targetBg;
        int duration;

        if (IsDropDownOpen)
        {
            targetBorder = Color.FromRgb(0x13, 0x70, 0xF3); // ColorBrush3
            targetBg = Color.FromRgb(0xE0, 0xEA, 0xFD);     // ColorBrush7
            duration = 50;
        }
        else if (IsMouseOver)
        {
            targetBorder = Color.FromRgb(0x48, 0x90, 0xF5); // ColorBrush4
            targetBg = Color.FromRgb(0xE0, 0xEA, 0xFD);     // ColorBrush7
            duration = 100;
        }
        else
        {
            targetBorder = Color.FromRgb(0x96, 0xC0, 0xF9); // ColorBrushBg0
            targetBg = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF); // ColorBrushHalfWhite
            duration = 150;
        }

        PclAnimation.AnimateColor(borderBrush, targetBorder, duration, PclAnimation.EaseOutFluentMiddle);
        PclAnimation.AnimateColor(backgroundBrush, targetBg, duration, PclAnimation.EaseOutFluentMiddle);
    }
}
