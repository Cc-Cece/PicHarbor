using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

public enum PclHintType
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// 1:1 port of PCL2 MyHint:
/// - 3px thick left stripe callout border
/// - Subtle tinted background
/// - Supports close / dismiss button
/// </summary>
public class PclHint : ContentControl
{
    public static readonly DependencyProperty HintTypeProperty =
        DependencyProperty.Register(nameof(HintType), typeof(PclHintType), typeof(PclHint),
            new PropertyMetadata(PclHintType.Info, OnHintTypeChanged));

    public static readonly DependencyProperty CanCloseProperty =
        DependencyProperty.Register(nameof(CanClose), typeof(bool), typeof(PclHint),
            new PropertyMetadata(false));

    public PclHintType HintType
    {
        get => (PclHintType)GetValue(HintTypeProperty);
        set => SetValue(HintTypeProperty, value);
    }

    public bool CanClose
    {
        get => (bool)GetValue(CanCloseProperty);
        set => SetValue(CanCloseProperty, value);
    }

    static PclHint()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclHint),
            new FrameworkPropertyMetadata(typeof(PclHint)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        var closeBtn = GetTemplateChild("PART_CloseButton") as Button;
        if (closeBtn != null)
        {
            closeBtn.Click += (_, _) =>
            {
                PclAnimation.AnimateDouble(this, OpacityProperty, 0, 150, onCompleted: () =>
                {
                    Visibility = Visibility.Collapsed;
                });
            };
        }

        UpdateColors();
    }

    private static void OnHintTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PclHint hint)
        {
            hint.UpdateColors();
        }
    }

    private void UpdateColors()
    {
        var border = GetTemplateChild("PART_Border") as Border;
        if (border == null) return;

        (Color stripe, Color bg) = HintType switch
        {
            PclHintType.Success => (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromArgb(0x20, 0x10, 0xB9, 0x81)),
            PclHintType.Warning => (Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B)),
            PclHintType.Error => (Color.FromRgb(0xEF, 0x44, 0x44), Color.FromArgb(0x20, 0xEF, 0x44, 0x44)),
            _ => (Color.FromRgb(0x13, 0x70, 0xF3), Color.FromArgb(0x20, 0x13, 0x70, 0xF3)) // Info
        };

        border.BorderBrush = new SolidColorBrush(stripe);
        border.Background = new SolidColorBrush(bg);
    }
}
