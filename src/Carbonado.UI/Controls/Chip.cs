using System.Windows;
using System.Windows.Controls;

namespace Carbonado.UI.Controls;

/// <summary>
/// Pill-shaped single choice (profile and format pickers) with an optional <see cref="Badge"/> such as a quality
/// grade or a count. Checked: iris wash with a Prisma edge; the badge turns Prisma.
/// </summary>
public class Chip : RadioButton
{
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(string), typeof(Chip), new FrameworkPropertyMetadata(null));

    /// <summary>Short tag shown after the label (e.g. "VS", "12"); null hides it.</summary>
    public string? Badge
    {
        get => (string?)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }
}
