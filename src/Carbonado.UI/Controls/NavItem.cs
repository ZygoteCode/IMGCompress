using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Carbonado.UI.Controls;

/// <summary>
/// Destination in a <see cref="NavRail"/>: icon over a short label; the tooltip shows the label and its
/// keyboard <see cref="Shortcut"/>. Usable directly in XAML or generated for bound items.
/// </summary>
public class NavItem : ListBoxItem
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(NavItem), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(NavItem), new FrameworkPropertyMetadata(null, OnHintChanged));

    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(
        nameof(Shortcut), typeof(string), typeof(NavItem), new FrameworkPropertyMetadata(null, OnHintChanged));

    /// <summary>Destination icon (20×20 grid geometry); Prisma-tinted while selected.</summary>
    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Short destination name (localized by the caller).</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Gesture text such as "Ctrl+1".</summary>
    public string? Shortcut
    {
        get => (string?)GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    private static void OnHintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (NavItem)d;
        string hint = string.IsNullOrEmpty(item.Shortcut) ? item.Label ?? string.Empty : $"{item.Label}   {item.Shortcut}";
        // Generate the hint tooltip unless the caller supplied a custom one.
        object local = item.ReadLocalValue(ToolTipProperty);
        if (local == DependencyProperty.UnsetValue || local is string or null)
        {
            item.ToolTip = string.IsNullOrEmpty(hint) ? null : hint;
        }

        AutomationProperties.SetName(item, item.Label ?? string.Empty);
        AutomationProperties.SetAcceleratorKey(item, item.Shortcut ?? string.Empty);
    }
}
