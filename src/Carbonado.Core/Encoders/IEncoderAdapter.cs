using Carbonado.Core.Analysis;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Encoders;

/// <summary>An encoder (usually an external tool) producing one output format.</summary>
public interface IEncoderAdapter
{
    /// <summary>The kinds every adapter could produce; adapters narrow it through <see cref="Kinds"/>.</summary>
    static readonly IReadOnlyCollection<EncodeKind> AllKinds = [EncodeKind.Lossy, EncodeKind.Lossless, EncodeKind.JpegTranscode, EncodeKind.Optimize];

    /// <summary>Stable identifier, e.g. "jxl.cjxl".</summary>
    string Id { get; }

    OutputFormat Format { get; }

    /// <summary>Tools that must be installed for the adapter to work.</summary>
    IReadOnlyList<ToolId> RequiredTools { get; }

    /// <summary>The kinds this adapter can produce for at least some inputs (<see cref="Supports"/> refines it per image).</summary>
    IReadOnlyCollection<EncodeKind> Kinds => AllKinds;

    /// <summary>The quality parameter space (<see cref="ParameterScale.None"/> for parameterless adapters).</summary>
    ParameterScale Scale { get; }

    /// <summary>Whether <paramref name="kind"/> can be produced for this image and source format.</summary>
    bool Supports(EncodeKind kind, ImageAnalysis a, ImageFormat source);

    /// <summary>A first parameter estimate for an SSIMULACRA2 <paramref name="targetScore"/>.</summary>
    double InitialGuess(double targetScore, ImageAnalysis a);

    /// <summary>
    /// The arguments of the adapter's main (quality-defining) tool step, with <paramref name="inputPath"/> and
    /// <see cref="EncodeRequest.OutputPath"/> used verbatim (the step <see cref="EncodeAsync"/> describes first in its command line).
    /// </summary>
    IReadOnlyList<string> BuildArguments(EncodeRequest r, string inputPath);

    /// <summary>Encodes; never throws for tool failures (see <see cref="EncodeOutcome.Error"/>), only for cancellation.</summary>
    Task<EncodeOutcome> EncodeAsync(EncodeRequest r, CancellationToken ct);
}
