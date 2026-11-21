namespace Carbonado.Core.Encoders;

/// <summary>The quality parameter space of an encoder, for the quality search.</summary>
/// <param name="Min">Smallest accepted value.</param>
/// <param name="Max">Largest accepted value.</param>
/// <param name="HigherIsBetter">True when larger values mean higher quality (e.g. a 0–100 quality); false for distances/CRF.</param>
/// <param name="Resolution">Smallest meaningful step; values are rounded to multiples of it.</param>
public sealed record ParameterScale(double Min, double Max, bool HigherIsBetter, double Resolution)
{
    /// <summary>The scale of adapters without a quality parameter (lossless/optimize only).</summary>
    public static ParameterScale None { get; } = new(0, 0, true, 1);

    /// <summary>Rounds to <see cref="Resolution"/> and clamps into [<see cref="Min"/>, <see cref="Max"/>].</summary>
    public double Normalize(double value)
    {
        double rounded = Resolution > 0 ? Math.Round(value / Resolution, MidpointRounding.AwayFromZero) * Resolution : value;
        return Math.Clamp(Math.Round(rounded, 6), Min, Max);
    }
}
