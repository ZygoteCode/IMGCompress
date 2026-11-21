namespace Carbonado.UI.Controls;

/// <summary>How a <see cref="CountUpText"/> formats its value (culture-aware digits and separators).</summary>
public enum CountFormat
{
    /// <summary>Whole number with group separators: 12,840.</summary>
    Integer,

    /// <summary>Byte count scaled to B / KB / MB / GB / TB (1024-based, three significant digits): 3.42 GB.</summary>
    Bytes,

    /// <summary>Fraction 0..1 shown as a whole percentage: 0.912 → 91%.</summary>
    Percent,

    /// <summary>Quality score with one decimal: 87.4.</summary>
    Score,
}
