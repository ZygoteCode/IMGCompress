using System.Globalization;
using Carbonado.App.Localization;

namespace Carbonado.App.Services;

/// <summary>Human formatting for sizes, percentages, durations and scores (culture-aware).</summary>
public static class FormatText
{
    private static readonly string[] s_units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>"0 B", "999 B", "1,2 KB", "34,5 MB", "1,02 GB" (binary multiples, 3 significant digits).</summary>
    public static string Bytes(long bytes, CultureInfo? culture = null)
    {
        culture ??= Loc.Culture;
        if (bytes < 0)
        {
            return "-" + Bytes(-bytes, culture);
        }

        if (bytes < 1024)
        {
            return string.Format(culture, "{0} B", bytes);
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < s_units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, culture) + " " + s_units[unit];
    }

    /// <summary>Fraction 0..1 as "42%" / "4,2%" (one decimal below 10%).</summary>
    public static string Percent(double fraction, CultureInfo? culture = null)
    {
        culture ??= Loc.Culture;
        double p = fraction * 100;
        string format = Math.Abs(p) < 10 && p != 0 ? "0.0" : "0";
        return p.ToString(format, culture) + "%";
    }

    /// <summary>"850 ms", "4,2 s", "3 min 05 s", "1 h 02 min".</summary>
    public static string Duration(TimeSpan d, CultureInfo? culture = null)
    {
        culture ??= Loc.Culture;
        if (d.TotalSeconds < 1)
        {
            return string.Format(culture, "{0:0} ms", d.TotalMilliseconds);
        }

        if (d.TotalMinutes < 1)
        {
            return d.TotalSeconds.ToString(d.TotalSeconds < 10 ? "0.0" : "0", culture) + " s";
        }

        if (d.TotalHours < 1)
        {
            return string.Format(culture, "{0} min {1:00} s", (int)d.TotalMinutes, d.Seconds);
        }

        return string.Format(culture, "{0} h {1:00} min", (int)d.TotalHours, d.Minutes);
    }

    /// <summary>SSIMULACRA2 score with one decimal ("91,3").</summary>
    public static string Score(double score, CultureInfo? culture = null) =>
        score.ToString("0.0", culture ?? Loc.Culture);

    /// <summary>Diamond clarity grade for a perceptual score (FL is reserved for verified lossless).</summary>
    public static string GradeFor(double score) => score switch
    {
        >= 90 => "VVS",
        >= 85 => "VS",
        >= 78 => "SI",
        _ => "I",
    };
}
