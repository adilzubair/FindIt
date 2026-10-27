using System.Windows;
using System.Windows.Media;

namespace FindIt.App;

/// <summary>Clips an element and its content to rounded corners. A Border's CornerRadius rounds only
/// its own background, and ClipToBounds clips to a square rectangle, so images inside keep sharp corners.</summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0d, RadiusChanged));

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void RadiusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        element.SizeChanged -= SizeChanged;
        if ((double)e.NewValue > 0) element.SizeChanged += SizeChanged;
        Apply(element);
    }

    private static void SizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var radius = GetRadius(element);
        if (radius <= 0 || element.ActualWidth <= 0 || element.ActualHeight <= 0) { element.ClearValue(UIElement.ClipProperty); return; }
        var clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
        clip.Freeze();
        element.Clip = clip;
    }
}
