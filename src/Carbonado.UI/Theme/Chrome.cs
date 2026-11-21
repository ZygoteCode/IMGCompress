using System.Windows;
using System.Windows.Media;

namespace Carbonado.UI.Theme;

/// <summary>
/// Attached properties read by Carbonado control templates, so one template serves several looks
/// (corner radius, hover layer, leading icon, placeholder text).
/// </summary>
public static class Chrome
{
    /// <summary>Corner radius of the control chrome (buttons, inputs, list items).</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Chrome), new FrameworkPropertyMetadata(default(CornerRadius)));

    /// <summary>Fill of the layer that fades in on hover.</summary>
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(Chrome), new FrameworkPropertyMetadata(null));

    /// <summary>Edge of the layer that fades in on hover.</summary>
    public static readonly DependencyProperty HoverBorderBrushProperty = DependencyProperty.RegisterAttached(
        "HoverBorderBrush", typeof(Brush), typeof(Chrome), new FrameworkPropertyMetadata(null));

    /// <summary>Optional leading icon (20×20 grid geometry from Icons.xaml).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Chrome), new FrameworkPropertyMetadata(null));

    /// <summary>Hint shown by text inputs while they are empty (bind it to a localized string).</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Chrome), new FrameworkPropertyMetadata(null));

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

    public static Brush? GetHoverBackground(DependencyObject element) => (Brush?)element.GetValue(HoverBackgroundProperty);

    public static void SetHoverBackground(DependencyObject element, Brush? value) => element.SetValue(HoverBackgroundProperty, value);

    public static Brush? GetHoverBorderBrush(DependencyObject element) => (Brush?)element.GetValue(HoverBorderBrushProperty);

    public static void SetHoverBorderBrush(DependencyObject element, Brush? value) => element.SetValue(HoverBorderBrushProperty, value);

    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);

    public static string? GetPlaceholder(DependencyObject element) => (string?)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string? value) => element.SetValue(PlaceholderProperty, value);
}
