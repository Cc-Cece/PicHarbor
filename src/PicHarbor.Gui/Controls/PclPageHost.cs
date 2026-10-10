using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

public enum PclEntranceDirection
{
    Vertical,   // Canvas cards: Top-to-bottom (-16px -> 0px)
    Horizontal  // Sidebar items: Left-to-right (-25px -> 0px)
}

/// <summary>
/// 1:1 port of PCL2 MyPageRight & MyPageLeft cascading pop-in entry animations:
/// - Staggers children with authentic PCL2 BackEase curves and timings
/// </summary>
public class PclPageHost : ContentControl
{
    public static readonly DependencyProperty DirectionProperty =
        DependencyProperty.Register(nameof(Direction), typeof(PclEntranceDirection), typeof(PclPageHost),
            new PropertyMetadata(PclEntranceDirection.Vertical));

    public PclEntranceDirection Direction
    {
        get => (PclEntranceDirection)GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    public PclPageHost()
    {
        Loaded += PclPageHost_Loaded;
        IsVisibleChanged += PclPageHost_IsVisibleChanged;
    }

    private void PclPageHost_Loaded(object sender, RoutedEventArgs e)
    {
        PlayEntranceAnimation();
    }

    private void PclPageHost_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Visibility == Visibility.Visible)
        {
            PlayEntranceAnimation();
        }
    }

    public void PlayEntranceAnimation()
    {
        if (Content is not FrameworkElement root) return;

        var animControls = new List<FrameworkElement>();
        FindAnimatableElements(root, animControls);

        if (animControls.Count == 0)
        {
            if (root is Panel p)
            {
                foreach (UIElement child in p.Children)
                {
                    if (child is FrameworkElement fe && fe.Visibility != Visibility.Collapsed)
                    {
                        animControls.Add(fe);
                    }
                }
            }
            else
            {
                animControls.Add(root);
            }
        }

        int delay = 0;
        foreach (var control in animControls)
        {
            control.Opacity = 0.0;

            if (Direction == PclEntranceDirection.Horizontal)
            {
                // PCL2 MyPageLeft exact physics: -25px slide in from left in 300ms
                var translate = new TranslateTransform(-25, 0);
                control.RenderTransform = translate;

                PclAnimation.AnimateDouble(control, OpacityProperty, 1.0, 100, PclAnimation.EaseOutFluentWeak, delayMs: delay);
                PclAnimation.AnimateDouble(translate, TranslateTransform.XProperty, 0.0, 300, PclAnimation.EaseOutBack, delayMs: delay);

                delay += 15; // PCL2 sidebar staggered delay
            }
            else
            {
                // PCL2 MyPageRight exact physics: -16px slide in from top in 350ms
                var translate = new TranslateTransform(0, -16);
                control.RenderTransform = translate;

                PclAnimation.AnimateDouble(control, OpacityProperty, 1.0, 120, PclAnimation.EaseOutFluentWeak, delayMs: delay);
                PclAnimation.AnimateDouble(translate, TranslateTransform.YProperty, 0.0, 350, PclAnimation.EaseOutBack, delayMs: delay);

                delay += 25; // PCL2 card staggered delay
            }
        }
    }

    private void FindAnimatableElements(DependencyObject parent, List<FrameworkElement> list)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (Direction == PclEntranceDirection.Horizontal)
            {
                if (child is PclListItem or TextBlock)
                {
                    list.Add((FrameworkElement)child);
                }
                else if (child is DependencyObject dep)
                {
                    FindAnimatableElements(dep, list);
                }
            }
            else
            {
                if (child is PclCard card)
                {
                    list.Add(card);
                }
                else if (child is DependencyObject dep)
                {
                    FindAnimatableElements(dep, list);
                }
            }
        }
    }
}
