using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Carbonado.UI.Controls;

/// <summary>
/// Metric card: icon chip and <see cref="Title"/> on top, a large <see cref="Value"/> (text, or any element such as
/// a <see cref="CountUpText"/>), a <see cref="Caption"/> below and optional extra <see cref="ContentControl.Content"/>
/// (sparkline, gauge) at the bottom. Raised obsidian with a prismatic hairline along the top edge.
/// </summary>
public class StatCard : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(StatCard), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(object), typeof(StatCard), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(StatCard), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(StatCard), new FrameworkPropertyMetadata(null));

    static StatCard()
    {
        FocusableProperty.OverrideMetadata(typeof(StatCard), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Metric name, shown small above the value (localized by the caller).</summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The metric: a string or an element (e.g. CountUpText).</summary>
    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Small context line under the value (localized by the caller).</summary>
    public string? Caption
    {
        get => (string?)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>Icon in the chip next to the title.</summary>
    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
