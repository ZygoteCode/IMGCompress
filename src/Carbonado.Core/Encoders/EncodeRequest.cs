namespace Carbonado.Core.Encoders;

/// <summary>One encode attempt.</summary>
/// <param name="Source">The prepared source (pixels, metadata, intermediates).</param>
/// <param name="Kind">What to produce.</param>
/// <param name="Parameter">Quality parameter on the adapter's <see cref="IEncoderAdapter.Scale"/>; required for <see cref="EncodeKind.Lossy"/>, ignored otherwise.</param>
/// <param name="Effort">CPU/density trade-off, mapped by each adapter to its own settings.</param>
/// <param name="Chroma">Chroma subsampling preference (AVIF); <see cref="ChromaMode.Auto"/> lets the adapter decide.</param>
/// <param name="Threads">Thread budget for the encoder (≥ 1).</param>
/// <param name="OutputPath">Where the result is written (replaced if it exists).</param>
public sealed record EncodeRequest(
    PreparedSource Source,
    EncodeKind Kind,
    double? Parameter,
    EffortLevel Effort,
    ChromaMode Chroma,
    int Threads,
    string OutputPath);
