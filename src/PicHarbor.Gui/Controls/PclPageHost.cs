using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// 1:1 port of PCL2 MyPageRight cascading pop-in entry animation:
/// Automatically staggers child cards with:
/// - Opacity 0 -> 1 in 100ms
/// - TranslateY -16 -> 0 in 350ms with BackEase
/// - Staggered delay: +25ms per card
/// </summary>
public class PclPageHost : ContentControl
{
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

        int delay = 0;
        foreach (var control in animControls)
        {
            control.Opacity = 0.0;
            var translate = new TranslateTransform(0, -16);
            control.RenderTransform = translate;

            PclAnimation.AnimateDouble(control, OpacityProperty, 1.0, 120, PclAnimation.EaseOutFluentWeak, delayMs: delay);
            PclAnimation.AnimateDouble(translate, TranslateTransform.YProperty, 0.0, 350, PclAnimation.EaseOutBack, delayMs: delay);

            delay += 25; // PCL2 exact 25ms staggered delay
        }
    }

    private void FindAnimatableElements(DependencyObject parent, List<FrameworkElement> list)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
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
