using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;
using Carbonado.Core.Migration;

namespace Carbonado.Core.Engine;

/// <summary>The profiles shipped with Carbonado (design spec §4.5). Grades follow diamond clarity: FL, VVS, VS, SI, I.</summary>
public static class BuiltInProfiles
{
    private static readonly IReadOnlyList<OutputFormat> s_maxDensity =
        [OutputFormat.Jxl, OutputFormat.Avif, OutputFormat.WebP, OutputFormat.Jpeg, OutputFormat.Png];

    public static CompressionProfile Flawless { get; } = new()
    {
        Id = "flawless",
        Name = "Absolute purity",
        NameIt = "Purezza assoluta",
        Grade = "FL",
        Description = "Lossless: every visible pixel identical, verified after encoding. JPEGs become reversible JPEG XL.",
        DescriptionIt = "Senza perdita: ogni pixel visibile identico, verificato dopo la codifica. I JPEG diventano JPEG XL reversibili.",
        Mode = QualityMode.Lossless,
        TargetScore = 100,
        Formats = [OutputFormat.Jxl, OutputFormat.WebP, OutputFormat.Png],
        Effort = EffortLevel.Max,
        BuiltIn = true,
    };

    public static CompressionProfile Vvs { get; } = Quality("vvs", "Invisible", "Invisibile", "VVS", 90,
        "Visually lossless: impossible to tell apart even in a flicker test at 1:1.",
        "Visivamente identica: indistinguibile anche alternando le immagini al 100%.");

    public static CompressionProfile Vs { get; } = Quality("vs", "Excellent", "Eccellente", "VS", 85,
        "Excellent quality: no visible difference side by side at 1:1. The best balance for most images.",
        "Qualità eccellente: nessuna differenza visibile affiancate al 100%. Il miglior equilibrio per quasi tutte le immagini.");

    public static CompressionProfile Si { get; } = Quality("si", "Balanced", "Equilibrio", "SI", 78,
        "Very high quality, much smaller files: artifacts only visible when hunting for them.",
        "Qualità molto alta, file molto più piccoli: artefatti visibili solo cercandoli.");

    public static CompressionProfile I { get; } = Quality("i", "Maximum reduction", "Massima riduzione", "I", 65,
        "Smallest files with good quality: ideal for previews, chats and archives of drafts.",
        "File minimi con buona qualità: ideale per anteprime, chat e archivi di bozze.");

    public static CompressionProfile Web { get; } = new()
    {
        Id = "web",
        Name = "Universal web",
        NameIt = "Web universale",
        Grade = "VS",
        Description = "Formats every browser opens (AVIF, WebP, JPEG; PNG for lossless graphics) at excellent quality.",
        DescriptionIt = "Formati che ogni browser apre (AVIF, WebP, JPEG; PNG per grafiche senza perdita) a qualità eccellente.",
        Mode = QualityMode.TargetScore,
        TargetScore = 85,
        Formats = [OutputFormat.Avif, OutputFormat.WebP, OutputFormat.Jpeg, OutputFormat.Png],
        BuiltIn = true,
    };

    public static CompressionProfile Keep { get; } = new()
    {
        Id = "keep",
        Name = "Same format",
        NameIt = "Stesso formato",
        Grade = "VS",
        Description = "Keeps each file's format: JPEG stays JPEG, PNG stays PNG, smaller and verified.",
        DescriptionIt = "Mantiene il formato di ogni file: i JPEG restano JPEG, i PNG restano PNG, più leggeri e verificati.",
        Mode = QualityMode.TargetScore,
        TargetScore = 85,
        Strategy = FormatStrategy.KeepSource,
        BuiltIn = true,
    };

    public static CompressionProfile JpegArchive { get; } = new()
    {
        Id = "jpeg-archive",
        Name = "JPEG → JXL archive",
        NameIt = "Archivio JPEG → JXL",
        Grade = "FL",
        Description = "Reversible JPEG XL: about 20–30% smaller and the original JPEG can be rebuilt bit for bit.",
        DescriptionIt = "JPEG XL reversibile: circa il 20–30% in meno e il JPEG originale si ricostruisce bit per bit.",
        Mode = QualityMode.Lossless,
        TargetScore = 100,
        Strategy = FormatStrategy.Fixed,
        Formats = [OutputFormat.Jxl],
        Effort = EffortLevel.Max,
        BuiltIn = true,
    };

    public static CompressionProfile Social { get; } = new()
    {
        Id = "social",
        Name = "Social (2048 px)",
        NameIt = "Social (2048 px)",
        Grade = "SI",
        Description = "Resized to fit 2048 px and encoded in web formats at very high quality.",
        DescriptionIt = "Ridimensionata entro 2048 px e codificata in formati web ad altissima qualità.",
        Mode = QualityMode.TargetScore,
        TargetScore = 80,
        Formats = [OutputFormat.Avif, OutputFormat.WebP, OutputFormat.Jpeg],
        Resize = new ResizeSpec(2048, 2048),
        Metadata = MetadataPolicy.StripAll,
        BuiltIn = true,
    };

    public static CompressionProfile Email { get; } = new()
    {
        Id = "email",
        Name = "Email (≤ 300 KB)",
        NameIt = "Email (≤ 300 KB)",
        Description = "Fits 1600 px and at most 300 KB per image, as JPEG or WebP, with the best quality that fits.",
        DescriptionIt = "Entro 1600 px e al massimo 300 KB per immagine, in JPEG o WebP, con la miglior qualità possibile.",
        Mode = QualityMode.TargetSize,
        TargetBytes = 300 * 1024,
        Formats = [OutputFormat.Jpeg, OutputFormat.WebP],
        Resize = new ResizeSpec(1600, 1600),
        Metadata = MetadataPolicy.StripAll,
        BuiltIn = true,
    };

    /// <summary>All built-in profiles in display order (legacy profiles last).</summary>
    public static IReadOnlyList<CompressionProfile> All { get; } =
    [
        Flawless, Vvs, Vs, Si, I, Web, Keep, JpegArchive, Social, Email,
        .. LegacyLevels.All,
    ];

    /// <summary>The default profile ("vs").</summary>
    public static CompressionProfile Default => Vs;

    public static CompressionProfile? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The profile equivalent to IMGCompress 6 "Compression level <paramref name="level"/>" (1–4).</summary>
    public static CompressionProfile Legacy(int level) => LegacyLevels.ProfileFor(level);

    private static CompressionProfile Quality(string id, string name, string nameIt, string grade, double target, string description, string descriptionIt) => new()
    {
        Id = id,
        Name = name,
        NameIt = nameIt,
        Grade = grade,
        Description = description,
        DescriptionIt = descriptionIt,
        Mode = QualityMode.TargetScore,
        TargetScore = target,
        Formats = s_maxDensity,
        BuiltIn = true,
    };
}
