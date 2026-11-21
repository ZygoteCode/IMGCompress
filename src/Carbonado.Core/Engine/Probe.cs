namespace Carbonado.Core.Engine;

/// <summary>One evaluated point of a quality search: an encode at <paramref name="Parameter"/>, measured.</summary>
/// <param name="Parameter">The encoder parameter (on the adapter's <see cref="Encoders.ParameterScale"/>).</param>
/// <param name="Bytes">Size of the encoded file.</param>
/// <param name="Score">SSIMULACRA2 of the decoded file against the source.</param>
/// <param name="Path">The encoded file (owned by the caller of the search).</param>
public sealed record Probe(double Parameter, long Bytes, double Score, string Path);
