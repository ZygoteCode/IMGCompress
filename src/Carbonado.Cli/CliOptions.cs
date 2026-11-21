using System.Globalization;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;

namespace Carbonado.Cli;

/// <summary>A command line that cannot be run (exit code 2); the message is printed as-is.</summary>
internal sealed class CliUsageException(string message) : Exception(message);

/// <summary>The parsed command line of <c>carbonado-cli</c>.</summary>
internal sealed record CliOptions
{
    /// <summary>Lowest and highest accepted <c>-q</c> score (the profile editor's range).</summary>
    public const double MinQuality = 30, MaxQuality = 99;

    public const string Usage =
        """
        Usage: carbonado <paths…> [options]

        Compresses images (files, folders or wildcards such as *.png) to the smallest verified output.

        Options:
          -o, --output <dir>      Write outputs to <dir> (folder trees are mirrored); default: a "Carbonado"
                                  folder next to each image
          -p, --profile <id>      Profile: flawless, vvs, vs (default), si, i, web, keep, jpeg-archive, social,
                                  email, legacy-1 … legacy-4
          -f, --format <format>   auto (race the profile's formats) or one of jxl, avif, webp, jpeg, png
          -q, --quality <score>   SSIMULACRA2 target, 30–99 (85 = excellent, 90 = visually lossless)
              --lossless          Lossless only: every visible pixel kept, or JPEG rebuilt bit for bit
          -r, --recursive         Include subfolders of folder arguments
              --effort <level>    fast, balanced (default), max or insane
              --json              One JSON object per file instead of text lines
              --dry-run           List the images that would be processed; write nothing
              --data-dir <dir>    Keep temporary files and logs in <dir>
          -h, --help              Show this help

        Exit codes: 0 every file succeeded (or was kept), 1 at least one failed, 2 invalid command line.

        """;

    public IReadOnlyList<string> Paths { get; init; } = [];

    public string? OutputDir { get; init; }

    public string ProfileId { get; init; } = BuiltInProfiles.Default.Id;

    /// <summary>A fixed output format; null keeps the profile's choice (or races its formats with <see cref="AutoFormat"/>).</summary>
    public OutputFormat? Format { get; init; }

    /// <summary><c>-f auto</c>: race the profile's formats even if the profile fixes or keeps one.</summary>
    public bool AutoFormat { get; init; }

    public double? Quality { get; init; }

    public bool Lossless { get; init; }

    public bool Recursive { get; init; }

    public EffortLevel? Effort { get; init; }

    public bool Json { get; init; }

    public bool DryRun { get; init; }

    public string? DataDir { get; init; }

    public bool Help { get; init; }

    /// <summary>Parses the arguments (options and paths in any order; "--" ends options; "--name=value" also works).</summary>
    /// <exception cref="CliUsageException">Unknown option, missing or invalid value, no paths, or conflicting options.</exception>
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new CliOptions();
        var paths = new List<string>();
        bool onlyPaths = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (onlyPaths || arg.Length < 2 || arg[0] != '-')
            {
                paths.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                onlyPaths = true;
                continue;
            }

            string name = arg;
            string? inline = null;
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                name = arg[..equals];
                inline = arg[(equals + 1)..];
            }

            string Value()
            {
                if (inline is not null)
                {
                    return inline;
                }

                if (i + 1 >= args.Count || args[i + 1].Length == 0)
                {
                    throw new CliUsageException($"Option {name} needs a value.");
                }

                return args[++i];
            }

            options = name switch
            {
                "-o" or "--output" => options with { OutputDir = Value() },
                "-p" or "--profile" => options with { ProfileId = Value() },
                "-f" or "--format" => ParseFormat(options, Value()),
                "-q" or "--quality" => options with { Quality = ParseQuality(Value()) },
                "--lossless" => Flag(options with { Lossless = true }, inline, name),
                "-r" or "--recursive" => Flag(options with { Recursive = true }, inline, name),
                "--effort" => options with { Effort = ParseEffort(Value()) },
                "--json" => Flag(options with { Json = true }, inline, name),
                "--dry-run" => Flag(options with { DryRun = true }, inline, name),
                "--data-dir" => options with { DataDir = Value() },
                "-h" or "--help" or "-?" => options with { Help = true },
                _ => throw new CliUsageException($"Unknown option {name}."),
            };
        }

        options = options with { Paths = paths };
        if (options.Help)
        {
            return options;
        }

        if (paths.Count == 0)
        {
            throw new CliUsageException("No images given.");
        }

        if (options.Lossless && options.Quality is not null)
        {
            throw new CliUsageException("--lossless and --quality cannot be combined.");
        }

        if (BuiltInProfiles.Find(options.ProfileId) is null)
        {
            throw new CliUsageException($"Unknown profile \"{options.ProfileId}\". Profiles: {string.Join(", ", BuiltInProfiles.All.Select(p => p.Id))}.");
        }

        return options;
    }

    /// <summary>The built-in profile with this command line's overrides (quality, lossless, format, effort).</summary>
    public CompressionProfile BuildProfile()
    {
        CompressionProfile profile = BuiltInProfiles.Find(ProfileId) ?? throw new CliUsageException($"Unknown profile \"{ProfileId}\".");
        if (Lossless)
        {
            profile = profile with { Mode = QualityMode.Lossless };
        }

        if (Quality is { } q)
        {
            profile = profile with { Mode = QualityMode.TargetScore, TargetScore = q };
        }

        if (Effort is { } effort)
        {
            profile = profile with { Effort = effort };
        }

        if (Format is { } format)
        {
            profile = profile with { Strategy = FormatStrategy.Fixed, Formats = [format] };
        }
        else if (AutoFormat)
        {
            profile = profile with { Strategy = FormatStrategy.Auto };
        }

        return profile;
    }

    /// <summary>Where outputs go: next to each image by default, or under <see cref="OutputDir"/> mirroring folder arguments.</summary>
    public OutputPlan BuildOutputPlan() => OutputDir is null
        ? new OutputPlan()
        : new OutputPlan(OutputLocation.CustomFolder, CustomFolder: Path.GetFullPath(OutputDir), MirrorTree: true);

    private static CliOptions Flag(CliOptions options, string? inline, string name) =>
        inline is null ? options : throw new CliUsageException($"Option {name} takes no value.");

    private static CliOptions ParseFormat(CliOptions options, string value) => value.ToLowerInvariant() switch
    {
        "auto" => options with { Format = null, AutoFormat = true },
        "jxl" => options with { Format = OutputFormat.Jxl, AutoFormat = false },
        "avif" => options with { Format = OutputFormat.Avif, AutoFormat = false },
        "webp" => options with { Format = OutputFormat.WebP, AutoFormat = false },
        "jpeg" or "jpg" => options with { Format = OutputFormat.Jpeg, AutoFormat = false },
        "png" => options with { Format = OutputFormat.Png, AutoFormat = false },
        _ => throw new CliUsageException($"Unknown format \"{value}\" (auto, jxl, avif, webp, jpeg or png)."),
    };

    private static double ParseQuality(string value)
    {
        if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double q) || !double.IsFinite(q))
        {
            throw new CliUsageException($"The quality \"{value}\" is not a number.");
        }

        return q is >= MinQuality and <= MaxQuality
            ? q
            : throw new CliUsageException($"The quality must be between {MinQuality} and {MaxQuality} (got {value}).");
    }

    private static EffortLevel ParseEffort(string value) => value.ToLowerInvariant() switch
    {
        "fast" => EffortLevel.Fast,
        "balanced" => EffortLevel.Balanced,
        "max" => EffortLevel.Max,
        "insane" => EffortLevel.Insane,
        _ => throw new CliUsageException($"Unknown effort \"{value}\" (fast, balanced, max or insane)."),
    };
}
