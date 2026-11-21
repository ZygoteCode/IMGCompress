using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Migration;

/// <summary>
/// Maps the four "Compression level" choices of IMGCompress 6 to Carbonado profiles. The legacy levels picked the
/// output format only (1 JPG, 2 WEBP, 3 AVIF, 4 JXL) and produced it through a destructive multi-generation chain;
/// the Carbonado equivalents keep the format but encode once from the original pixels at "very high" quality
/// (SSIMULACRA2 80). Transparent inputs fall back to the second format, as the legacy "PNG transparency" option did.
/// </summary>
public static class LegacyLevels
{
    public const int Min = 1;
    public const int Max = 4;

    /// <summary>The legacy default selection (level 4, JXL).</summary>
    public const int Default = 4;

    public static IReadOnlyList<CompressionProfile> All { get; } = [Create(1), Create(2), Create(3), Create(4)];

    public static string ProfileId(int level) => $"legacy-{Clamp(level)}";

    public static CompressionProfile ProfileFor(int level) => All[Clamp(level) - 1];

    public static OutputFormat Format(int level) => Clamp(level) switch
    {
        1 => OutputFormat.Jpeg,
        2 => OutputFormat.WebP,
        3 => OutputFormat.Avif,
        _ => OutputFormat.Jxl,
    };

    private static int Clamp(int level) => Math.Clamp(level, Min, Max);

    private static CompressionProfile Create(int level)
    {
        var format = Format(level);
        string name = FormatInfo.DisplayName(format);
        return new CompressionProfile
        {
            Id = ProfileId(level),
            Name = $"IMGCompress level {level} ({name})",
            NameIt = $"IMGCompress livello {level} ({name})",
            Grade = "SI",
            Description = $"Same output format as legacy level {level}, encoded once from the original pixels at very high quality.",
            DescriptionIt = $"Stesso formato del vecchio livello {level}, codificato una sola volta dai pixel originali ad altissima qualità.",
            Mode = QualityMode.TargetScore,
            TargetScore = 80,
            Strategy = FormatStrategy.Fixed,
            // Transparent sources cannot be JPEG: the legacy app wrote PNG (level 1) or WebP (levels 2-4) for them.
            Formats = format == OutputFormat.Jpeg ? [OutputFormat.Jpeg, OutputFormat.Png] : [format, OutputFormat.WebP],
            BuiltIn = true,
        };
    }
}
