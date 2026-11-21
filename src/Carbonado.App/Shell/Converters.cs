using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Carbonado.App.Localization;
using Carbonado.App.Pages.Archive;
using Carbonado.App.Pages.Compare;
using Carbonado.App.Pages.Engines;
using Carbonado.Core.Imaging;
using Carbonado.App.Pages.Forge;
using Carbonado.App.Services;
using Carbonado.Core.Engine;
using Carbonado.UI.Controls;

namespace Carbonado.App.Shell;

/// <summary>bool → Visible/Collapsed (Invert flips it).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

/// <summary>non-null (and non-empty string) → Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value is string s ? s.Length > 0 : value is not null;
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>int &gt; 0 → Visible.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is int n && n > 0) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Enum value ↔ bool for radio-style choices; ConverterParameter is the enum member name.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string name && string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType.IsEnum ? targetType : Nullable.GetUnderlyingType(targetType)!, name) : Binding.DoNothing;
}

/// <summary>Queue item status → gem ring state.</summary>
public sealed class QueueStatusToGemStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        QueueItemStatus.Working => GemState.Working,
        QueueItemStatus.Done => GemState.Done,
        QueueItemStatus.Kept => GemState.Kept,
        QueueItemStatus.Failed => GemState.Failed,
        _ => GemState.Idle,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>App comparison mode ↔ viewer mode (same member names).</summary>
public sealed class ComparisonModeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ComparisonMode m ? Enum.Parse<CompareMode>(m.ToString()) : CompareMode.Split;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CompareMode m ? Enum.Parse<ComparisonMode>(m.ToString()) : ComparisonMode.Split;
}

/// <summary>Profile → localized name.</summary>
public sealed class ProfileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CompressionProfile p ? p.DisplayName(Loc.Language) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Profile → localized description.</summary>
public sealed class ProfileDescriptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CompressionProfile p ? p.DisplayDescription(Loc.Language) ?? string.Empty : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Enum value → localized label "L.&lt;Prefix&gt;.&lt;Value&gt;" (ConverterParameter = prefix, e.g. "Mode").</summary>
public sealed class LocalizedEnumConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? string.Empty : Loc.Get($"L.{parameter}.{value}");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Byte count → "1.2 MB".</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long b ? FormatText.Bytes(b) : value is int i ? FormatText.Bytes(i) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Fraction 0..1 → "−42%" ("" for 0).</summary>
public sealed class SavedPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && d > 0 ? "−" + FormatText.Percent(d) : "0%";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Grade text → visibility (hidden when empty).</summary>
public sealed class GradeVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Resource key ("Icon.Forge") → Geometry from the application resources.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current?.TryFindResource(key) as System.Windows.Media.Geometry : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Theme brush for an output format (used by charts and legends; colors come from Tokens.xaml).</summary>
public static class FormatBrushes
{
    public static System.Windows.Media.Brush For(OutputFormat format) => Lookup(format switch
    {
        OutputFormat.Jxl => "B.Iris",
        OutputFormat.Avif => "B.Aqua",
        OutputFormat.WebP => "B.Emerald",
        OutputFormat.Jpeg => "B.Ember",
        OutputFormat.Png => "B.Rose",
        _ => "B.Topaz",
    });

    internal static System.Windows.Media.Brush Lookup(string key) =>
        Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
}

/// <summary>Format shares → donut slices.</summary>
public sealed class FormatSlicesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<FormatShare> shares
            ? shares.Select(s => new DonutSlice(s.Label, s.Count, FormatBrushes.For(s.Format))).ToList()
            : new List<DonutSlice>();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Output format → its theme brush.</summary>
public sealed class FormatBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is OutputFormat f ? FormatBrushes.For(f) : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Engine integrity → badge brush (Verified emerald, Unverified topaz, Modified ruby, Missing mist).</summary>
public sealed class EngineStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => FormatBrushes.Lookup(value switch
    {
        EngineStatus.Verified => "B.Success",
        EngineStatus.Unverified => "B.Warning",
        EngineStatus.Modified => "B.Danger",
        _ => "B.TextTertiary",
    });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool → opacity (1 or 0.35) for available/unavailable chips.</summary>
public sealed class AvailabilityOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1.0 : 0.35;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>ImageSource → UniformToFill ImageBrush (null → no brush). Avoids binding inside a Freezable in templates.</summary>
public sealed class ThumbnailBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not System.Windows.Media.ImageSource source)
        {
            return null;
        }

        var brush = new System.Windows.Media.ImageBrush(source) { Stretch = System.Windows.Media.Stretch.UniformToFill };
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
