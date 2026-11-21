using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Engine;

public enum QualityMode
{
    /// <summary>Smallest output whose SSIMULACRA2 score is ≥ <see cref="CompressionProfile.TargetScore"/>.</summary>
    TargetScore,

    /// <summary>Visible pixels identical (or bit-exact JPEG reconstruction).</summary>
    Lossless,

    /// <summary>Highest quality whose size is ≤ <see cref="CompressionProfile.TargetBytes"/>.</summary>
    TargetSize,
}

public enum FormatStrategy
{
    /// <summary>Race all allowed formats, keep the smallest verified result.</summary>
    Auto,

    /// <summary>Stay in the source format family.</summary>
    KeepSource,

    /// <summary>Use exactly the first entry of <see cref="CompressionProfile.Formats"/>.</summary>
    Fixed,
}

/// <summary>Downscale so the image fits within the box (never upscales).</summary>
public sealed record ResizeSpec(int MaxWidth, int MaxHeight);

/// <summary>A named compression recipe.</summary>
public sealed record CompressionProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? NameIt { get; init; }

    /// <summary>Diamond clarity grade shown as a badge: FL, VVS, VS, SI, I (or empty).</summary>
    public string Grade { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? DescriptionIt { get; init; }

    public QualityMode Mode { get; init; } = QualityMode.TargetScore;

    /// <summary>SSIMULACRA2 target (30..99). Ignored for <see cref="QualityMode.Lossless"/>.</summary>
    public double TargetScore { get; init; } = 85;

    public long? TargetBytes { get; init; }

    public FormatStrategy Strategy { get; init; } = FormatStrategy.Auto;

    public IReadOnlyList<OutputFormat> Formats { get; init; } =
        [OutputFormat.Jxl, OutputFormat.Avif, OutputFormat.WebP, OutputFormat.Jpeg, OutputFormat.Png];

    public EffortLevel Effort { get; init; } = EffortLevel.Balanced;

    public MetadataPolicy Metadata { get; init; } = MetadataPolicy.KeepColorProfile;

    public ResizeSpec? Resize { get; init; }

    /// <summary>Keep the original when no candidate is smaller.</summary>
    public bool NeverLarger { get; init; } = true;

    /// <summary>Allow JPEG → JPEG XL reversible transcode when JXL is an allowed format.</summary>
    public bool AllowJpegTranscode { get; init; } = true;

    /// <summary>Allow changing RGB under fully transparent pixels (invisible) for density.</summary>
    public bool CleanInvisiblePixels { get; init; } = true;

    public bool BuiltIn { get; init; }

    public string DisplayName(string language) =>
        language.StartsWith("it", StringComparison.OrdinalIgnoreCase) && NameIt is not null ? NameIt : Name;

    public string? DisplayDescription(string language) =>
        language.StartsWith("it", StringComparison.OrdinalIgnoreCase) && DescriptionIt is not null ? DescriptionIt : Description;
}
