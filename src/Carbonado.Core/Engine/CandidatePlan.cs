using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Engine;

/// <summary>One candidate the engine will try for an image: an encoder, what it produces, and its chroma choice.</summary>
/// <param name="Encoder">The adapter to run.</param>
/// <param name="Kind">Lossy (quality-searched), lossless, JPEG transcode or same-format optimization.</param>
/// <param name="Chroma">Chroma subsampling for AVIF (<see cref="ChromaMode.Auto"/> elsewhere).</param>
public sealed record CandidatePlan(IEncoderAdapter Encoder, EncodeKind Kind, ChromaMode Chroma)
{
    /// <summary>The format the candidate produces.</summary>
    public OutputFormat Format => Encoder.Format;

    /// <summary>"avif.avifenc Lossy 444" style label for logs.</summary>
    public override string ToString() => Chroma == ChromaMode.Auto ? $"{Encoder.Id} {Kind}" : $"{Encoder.Id} {Kind} {Chroma}";
}
