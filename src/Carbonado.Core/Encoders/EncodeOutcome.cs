namespace Carbonado.Core.Encoders;

/// <summary>Result of <see cref="IEncoderAdapter.EncodeAsync"/>.</summary>
/// <param name="Success">The output was written.</param>
/// <param name="OutputPath">The requested output path.</param>
/// <param name="Bytes">Size of the output (0 on failure).</param>
/// <param name="Elapsed">Wall-clock time of the whole encode (all steps).</param>
/// <param name="CommandLine">The commands that ran, for diagnostics (steps joined with " &amp;&amp; ").</param>
/// <param name="Error">A readable reason when <paramref name="Success"/> is false.</param>
public sealed record EncodeOutcome(bool Success, string OutputPath, long Bytes, TimeSpan Elapsed, string CommandLine, string? Error);
