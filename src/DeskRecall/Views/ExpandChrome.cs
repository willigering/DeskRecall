using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeskRecall.Views;

/// <summary>
/// Sanftes Auf- und Zuklappen über RenderTransform (ScaleY) und Deckkraft – ohne das Layout
/// in jedem Animationsrahmen neu zu messen. Der Platz im Layout ändert sich nur beim Öffnen
/// (einmal Visible) und am Ende des Schließens (Collapsed).
/// </summary>
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
        try { Animate(el, open); }
        catch (InvalidOperationException) { ApplyInstant(el, open); } // Animation nie wichtiger als die Anzeige
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

    static ScaleTransform EnsureScale(FrameworkElement el)
    {
        // Aus einem DataTemplate stammende Transformationen sind eingefroren (Freezable) und lassen sich nicht animieren:
        // dann eine eigene, veränderbare Instanz verwenden.
        if (el.RenderTransform is ScaleTransform s && !s.IsFrozen && !s.IsSealed) return s;
        s = new ScaleTransform(1, 1);
        el.RenderTransform = s;
        el.RenderTransformOrigin = new Point(0, 0);
        return s;
    }

    static void StopAnimations(FrameworkElement el, ScaleTransform scale)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    static void ApplyInstant(FrameworkElement el, bool open)
    {
        var scale = EnsureScale(el);
        StopAnimations(el, scale);
        if (open)
        {
            el.MaxHeight = double.PositiveInfinity;
            el.Visibility = Visibility.Visible;
            el.Opacity = 1;
            scale.ScaleY = 1;
        }
        else
        {
            el.Opacity = 0;
            scale.ScaleY = 0;
            el.Visibility = Visibility.Collapsed;
            el.MaxHeight = 0;
        }
    }

    static void Animate(FrameworkElement el, bool open)
    {
        var scale = EnsureScale(el);
        StopAnimations(el, scale);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);

        if (open)
        {
            el.MaxHeight = double.PositiveInfinity;
            el.Visibility = Visibility.Visible;
            // Lokale Werte = Endzustand, damit FillBehavior.Stop nicht zurückspringt.
            el.Opacity = 1;
            scale.ScaleY = 1;
            var fromO = 0d;
            var oAnim = new DoubleAnimation(fromO, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            var yAnim = new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            oAnim.Completed += (_, _) => ApplyInstant(el, true);
            el.BeginAnimation(UIElement.OpacityProperty, oAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, yAnim);
        }
        else
        {
            var fromO = el.Opacity > 0 ? el.Opacity : 1;
            var fromY = scale.ScaleY > 0 ? scale.ScaleY : 1;
            el.Opacity = 0;
            scale.ScaleY = 0;
            var oAnim = new DoubleAnimation(fromO, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            var yAnim = new DoubleAnimation(fromY, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
            oAnim.Completed += (_, _) => ApplyInstant(el, false);
            el.BeginAnimation(UIElement.OpacityProperty, oAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, yAnim);
        }
    }
}
