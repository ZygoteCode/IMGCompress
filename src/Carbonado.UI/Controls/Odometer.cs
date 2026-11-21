using System.Globalization;
using System.Text;

namespace Carbonado.UI.Controls;

/// <summary>One character of an odometer reading; digits carry how far they have rolled toward the next digit.</summary>
internal readonly record struct OdometerGlyph(char Char, int Digit, double Roll)
{
    /// <summary>True for digit glyphs (they sit in fixed-width cells and can roll).</summary>
    public bool IsDigit => Digit >= 0;
}

/// <summary>
/// Formats <see cref="CountUpText"/> values as rolling odometer readings: digits come from the truncated value and
/// the lowest digit rolls continuously; a higher digit rolls only while every digit below it shows 9 (mechanical
/// carry). The unit and decimals come from a reference value so they stay fixed for a whole count.
/// </summary>
internal static class Odometer
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Layout (scale, decimals, suffix) for <paramref name="format"/>, chosen from <paramref name="reference"/>.</summary>
    public static (double Scale, int Decimals, string Suffix) Layout(CountFormat format, double reference, NumberFormatInfo numbers)
    {
        switch (format)
        {
            case CountFormat.Score:
                return (1, 1, string.Empty);
            case CountFormat.Percent:
                return (100, 0, numbers.PercentPositivePattern == 0 ? " " + numbers.PercentSymbol : numbers.PercentSymbol);
            case CountFormat.Bytes:
                double magnitude = Math.Abs(reference);
                int unit = 0;
                while (unit < ByteUnits.Length - 1 && magnitude >= 1024)
                {
                    magnitude /= 1024;
                    unit++;
                }

                int decimals = unit == 0 ? 0 : magnitude < 10 ? 2 : magnitude < 100 ? 1 : 0;
                return (1 / Math.Pow(1024, unit), decimals, " " + ByteUnits[unit]);
            default:
                return (1, 0, string.Empty);
        }
    }

    /// <summary>
    /// Reading of <paramref name="value"/> laid out for <paramref name="reference"/>. Rolling readings truncate and
    /// roll the lowest digit by the remainder; settled readings round to the displayed precision.
    /// </summary>
    public static IReadOnlyList<OdometerGlyph> Read(double value, CountFormat format, double reference, NumberFormatInfo numbers, bool rolling = true)
    {
        var (scale, decimals, suffix) = Layout(format, reference, numbers);
        double scaled = Math.Abs(value) * scale * Math.Pow(10, decimals);
        if (!double.IsFinite(scaled))
        {
            scaled = 0;
        }

        // Tiny epsilon: 0.29 * 100 must read 29, not 28 rolling to 29.
        double whole = rolling ? Math.Floor(scaled + 1e-6) : Math.Round(scaled, MidpointRounding.AwayFromZero);
        double roll = rolling ? Math.Max(0, scaled - whole) : 0;
        string digits = whole.ToString("0", CultureInfo.InvariantCulture).PadLeft(decimals + 1, '0');

        var glyphs = new List<OdometerGlyph>(digits.Length + 8);
        if (value < 0 && (whole > 0 || roll > 0))
        {
            glyphs.AddRange(numbers.NegativeSign.Select(c => new OdometerGlyph(c, -1, 0)));
        }

        int integerDigits = digits.Length - decimals;
        int groupSize = numbers.NumberGroupSizes is [> 0 and var size, ..] ? size : 3;
        bool lowerAllNines = true;
        var rolls = new double[digits.Length];
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            rolls[i] = lowerAllNines ? roll : 0;
            lowerAllNines &= digits[i] == '9';
        }

        for (int i = 0; i < digits.Length; i++)
        {
            if (i == integerDigits)
            {
                glyphs.AddRange(numbers.NumberDecimalSeparator.Select(c => new OdometerGlyph(c, -1, 0)));
            }
            else if (i > 0 && i < integerDigits && (integerDigits - i) % groupSize == 0)
            {
                glyphs.AddRange(numbers.NumberGroupSeparator.Select(c => new OdometerGlyph(c, -1, 0)));
            }

            glyphs.Add(new OdometerGlyph(digits[i], digits[i] - '0', rolls[i]));
        }

        glyphs.AddRange(suffix.Select(c => new OdometerGlyph(c, -1, 0)));
        return glyphs;
    }

    /// <summary>The settled text of <paramref name="value"/> (its own canonical layout, rounded).</summary>
    public static string Format(double value, CountFormat format, NumberFormatInfo numbers) =>
        Text(Read(value, format, value, numbers, rolling: false));

    /// <summary>
    /// <paramref name="value"/> rounded to the precision it is displayed with for <paramref name="reference"/>, so a
    /// count ends exactly on a whole reading (no half-rolled digit snapping at the end).
    /// </summary>
    public static double Quantize(double value, CountFormat format, double reference, NumberFormatInfo numbers)
    {
        var (scale, decimals, _) = Layout(format, reference, numbers);
        double unit = scale * Math.Pow(10, decimals);
        return Math.Round(value * unit, MidpointRounding.AwayFromZero) / unit;
    }

    /// <summary>Characters of a reading (rolling digits show their current digit).</summary>
    public static string Text(IReadOnlyList<OdometerGlyph> glyphs)
    {
        var text = new StringBuilder(glyphs.Count);
        foreach (var glyph in glyphs)
        {
            text.Append(glyph.Char);
        }

        return text.ToString();
    }
}
