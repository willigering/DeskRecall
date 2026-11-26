using System.Windows;
using System.Windows.Media.Animation;

namespace DeskRecall.Views;

/// <summary>Sanftes Auf- und Zuklappen über MaxHeight und Deckkraft (ca. 180 ms, EaseOut).</summary>
public static class ExpandChrome
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen", typeof(bool), typeof(ExpandChrome), new PropertyMetadata(false, OnIsOpenChanged));

    public static bool GetIsOpen(DependencyObject d) => (bool)d.GetValue(IsOpenProperty);
    public static void SetIsOpen(DependencyObject d, bool value) => d.SetValue(IsOpenProperty, value);

    static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        HookLoaded(el);
        var open = (bool)e.NewValue;
        if (!el.IsLoaded) { ApplyInstant(el, open); return; }
        Animate(el, open);
    }

    static void HookLoaded(FrameworkElement el)
    {
        el.Loaded -= OnLoaded;
        el.Loaded += OnLoaded;
    }

    static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var el = (FrameworkElement)sender;
        el.Loaded -= OnLoaded;
        ApplyInstant(el, GetIsOpen(el));
    }

    static void ApplyInstant(FrameworkElement el, bool open)
    {
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);
        if (open)
        {
            el.Visibility = Visibility.Visible;
            el.MaxHeight = double.PositiveInfinity;
            el.Opacity = 1;
        }
        else
        {
            el.MaxHeight = 0;
            el.Opacity = 0;
            el.Visibility = Visibility.Collapsed;
        }
    }

    static void Animate(FrameworkElement el, bool open)
    {
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Visibility = Visibility.Visible;

        double fromH, toH, fromO, toO;
        if (open)
        {
            el.MaxHeight = double.PositiveInfinity;
            el.Opacity = 1;
            el.UpdateLayout();
            var width = el.ActualWidth > 1 ? el.ActualWidth : 400;
            el.Measure(new Size(width, double.PositiveInfinity));
            toH = Math.Max(el.DesiredSize.Height, 1);
            fromH = 0;
            fromO = 0;
            toO = 1;
            // Lokale Werte = Endzustand, damit FillBehavior.Stop nicht auf 0 zurückspringt.
            el.MaxHeight = double.PositiveInfinity;
            el.Opacity = 1;
        }
        else
        {
            fromH = el.ActualHeight > 0 ? el.ActualHeight : el.DesiredSize.Height;
            if (fromH <= 0) { ApplyInstant(el, false); return; }
            toH = 0;
            fromO = el.Opacity > 0 ? el.Opacity : 1;
            toO = 0;
            el.MaxHeight = 0;
            el.Opacity = 0;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        var hAnim = new DoubleAnimation(fromH, toH, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var oAnim = new DoubleAnimation(fromO, toO, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        hAnim.Completed += (_, _) => ApplyInstant(el, open);
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, hAnim);
        el.BeginAnimation(UIElement.OpacityProperty, oAnim);
    }
}
