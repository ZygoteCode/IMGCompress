namespace Carbonado.Core.Analysis;

public enum AlphaUsage
{
    /// <summary>Every pixel fully opaque.</summary>
    Opaque,

    /// <summary>Only fully opaque or fully transparent pixels.</summary>
    Binary,

    /// <summary>At least one partially transparent pixel.</summary>
    Translucent,
}
