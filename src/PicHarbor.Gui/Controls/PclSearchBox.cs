using System.Windows;
using System.Windows.Controls;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MySearchBox:
/// - Rounded white card container (Height=40) with subtle shadow breathing
/// - Left search magnifying glass icon
/// - Borderless text input
/// - Right clear button with fade-in/fade-out
/// </summary>
public class PclSearchBox : Control
{
    private TextBox? searchInput;
    private FrameworkElement? clearButton;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(PclSearchBox),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public static readonly DependencyProperty HintTextProperty =
        DependencyProperty.Register(nameof(HintText), typeof(string), typeof(PclSearchBox),
            new PropertyMetadata("搜索..."));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string HintText
    {
        get => (string)GetValue(HintTextProperty);
        set => SetValue(HintTextProperty, value);
    }

    static PclSearchBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PclSearchBox),
            new FrameworkPropertyMetadata(typeof(PclSearchBox)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        searchInput = GetTemplateChild("PART_Input") as TextBox;
        clearButton = GetTemplateChild("PART_ClearButton") as FrameworkElement;

        if (searchInput != null)
        {
            searchInput.TextChanged += (_, _) =>
            {
                Text = searchInput.Text;
                UpdateClearVisibility();
            };
        }

        if (clearButton is Button btn)
        {
            btn.Click += (_, _) =>
            {
                if (searchInput != null)
                {
                    searchInput.Text = string.Empty;
                    searchInput.Focus();
                }
            };
        }

        UpdateClearVisibility();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PclSearchBox sb && sb.searchInput != null)
        {
            string newText = (string)e.NewValue ?? string.Empty;
            if (sb.searchInput.Text != newText)
            {
                sb.searchInput.Text = newText;
            }
            sb.UpdateClearVisibility();
        }
    }

    private void UpdateClearVisibility()
    {
        if (clearButton == null) return;
        bool hasText = !string.IsNullOrEmpty(Text);
        double targetOpacity = hasText ? 1.0 : 0.0;
        clearButton.IsHitTestVisible = hasText;
        PclAnimation.AnimateDouble(clearButton, OpacityProperty, targetOpacity, 120, PclAnimation.EaseOutFluentMiddle);
    }
}
