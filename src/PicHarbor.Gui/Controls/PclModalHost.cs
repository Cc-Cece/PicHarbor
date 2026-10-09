using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PicHarbor.Gui.Controls;

/// <summary>
/// Container for PCL2-style modal dialogs with smooth backdrop fade and elastic pop-in animations.
/// Automatically coordinates with WebView2 to avoid Win32 HWND airspace conflicts.
/// </summary>
public class PclModalHost : ContentControl
{
    private static readonly HashSet<PclModalHost> ActiveModals = new();
    public static event EventHandler<bool>? HasAnyModalOpenChanged;
    public static bool HasAnyModalOpen => ActiveModals.Count > 0;

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(PclModalHost),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOpenChanged));

    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(PclModalHost),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CloseOnClickOutsideProperty =
        DependencyProperty.Register(nameof(CloseOnClickOutside), typeof(bool), typeof(PclModalHost),
            new PropertyMetadata(true));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public bool CloseOnClickOutside
    {
        get => (bool)GetValue(CloseOnClickOutsideProperty);
        set => SetValue(CloseOnClickOutsideProperty, value);
    }

    private Grid? rootGrid;
    private ContentPresenter? contentPresenter;
    private ScaleTransform? scaleTransform;

    public PclModalHost()
    {
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;

        // Build default ControlTemplate
        var template = new ControlTemplate(typeof(PclModalHost));
        var gridFactory = new FrameworkElementFactory(typeof(Grid), "PART_RootGrid");
        gridFactory.SetValue(Panel.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)));
        gridFactory.SetValue(UIElement.OpacityProperty, 0.0);

        var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter), "PART_ContentPresenter");
        presenterFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenterFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenterFactory.SetValue(UIElement.OpacityProperty, 0.0);

        gridFactory.AppendChild(presenterFactory);
        template.VisualTree = gridFactory;
        Template = template;

        Loaded += PclModalHost_Loaded;
        Unloaded += PclModalHost_Unloaded;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (rootGrid != null)
        {
            rootGrid.MouseDown -= RootGrid_MouseDown;
        }

        rootGrid = GetTemplateChild("PART_RootGrid") as Grid;
        contentPresenter = GetTemplateChild("PART_ContentPresenter") as ContentPresenter;

        if (contentPresenter != null)
        {
            scaleTransform = new ScaleTransform(0.93, 0.93);
            contentPresenter.RenderTransformOrigin = new Point(0.5, 0.5);
            contentPresenter.RenderTransform = scaleTransform;
        }

        if (rootGrid != null)
        {
            rootGrid.MouseDown += RootGrid_MouseDown;
        }

        UpdateVisualState(animate: false);
    }

    private void PclModalHost_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateVisualState(animate: false);
    }

    private void PclModalHost_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ActiveModals.Remove(this))
        {
            HasAnyModalOpenChanged?.Invoke(null, HasAnyModalOpen);
        }
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PclModalHost host)
        {
            host.UpdateVisualState(animate: true);
        }
    }

    private void UpdateVisualState(bool animate)
    {
        if (rootGrid == null || contentPresenter == null || scaleTransform == null) return;

        if (IsOpen)
        {
            if (!ActiveModals.Contains(this))
            {
                ActiveModals.Add(this);
                HasAnyModalOpenChanged?.Invoke(null, true);
            }

            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            rootGrid.Visibility = Visibility.Visible;

            if (animate)
            {
                // Smooth backdrop fade-in (180ms)
                PclAnimation.AnimateDouble(rootGrid, OpacityProperty, 1.0, 180, PclAnimation.EaseOutFluentWeak);

                // Elastic scale-in for card dialog (240ms, EaseOutBack)
                PclAnimation.AnimateScale(scaleTransform, 1.0, 240, PclAnimation.EaseOutBack);
                PclAnimation.AnimateDouble(contentPresenter, OpacityProperty, 1.0, 180, PclAnimation.EaseOutFluentWeak);
            }
            else
            {
                rootGrid.Opacity = 1.0;
                scaleTransform.ScaleX = 1.0;
                scaleTransform.ScaleY = 1.0;
                contentPresenter.Opacity = 1.0;
            }
        }
        else
        {
            IsHitTestVisible = false;

            if (animate && Visibility == Visibility.Visible)
            {
                // Smooth fade-out and slight scale-down (140ms)
                PclAnimation.AnimateDouble(rootGrid, OpacityProperty, 0.0, 140, PclAnimation.EaseOutFluentMiddle);
                PclAnimation.AnimateScale(scaleTransform, 0.95, 140, PclAnimation.EaseOutFluentMiddle);
                PclAnimation.AnimateDouble(contentPresenter, OpacityProperty, 0.0, 140, PclAnimation.EaseOutFluentMiddle, onCompleted: () =>
                {
                    Visibility = Visibility.Collapsed;
                    rootGrid.Visibility = Visibility.Collapsed;
                    if (ActiveModals.Remove(this))
                    {
                        HasAnyModalOpenChanged?.Invoke(null, HasAnyModalOpen);
                    }
                });
            }
            else
            {
                Visibility = Visibility.Collapsed;
                rootGrid.Visibility = Visibility.Collapsed;
                rootGrid.Opacity = 0.0;
                scaleTransform.ScaleX = 0.93;
                scaleTransform.ScaleY = 0.93;
                contentPresenter.Opacity = 0.0;

                if (ActiveModals.Remove(this))
                {
                    HasAnyModalOpenChanged?.Invoke(null, HasAnyModalOpen);
                }
            }
        }
    }

    private void RootGrid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!CloseOnClickOutside) return;

        // If the click directly hit the root backdrop (outside the inner card content)
        if (ReferenceEquals(e.OriginalSource, rootGrid))
        {
            Close();
            e.Handled = true;
        }
    }

    public void Close()
    {
        if (CloseCommand != null && CloseCommand.CanExecute(null))
        {
            CloseCommand.Execute(null);
        }
        SetCurrentValue(IsOpenProperty, false);
    }

    public static PclModalHost? GetTopActiveModal()
    {
        return ActiveModals.LastOrDefault();
    }
}
