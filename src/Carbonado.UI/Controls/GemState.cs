namespace Carbonado.UI.Controls;

/// <summary>Status shown by a <see cref="GemProgressRing"/> (one per queue item).</summary>
public enum GemState
{
    /// <summary>Waiting: dim facet ring with a small gem.</summary>
    Idle,

    /// <summary>Encoding: Prisma progress traced along the octagon (or a spinning facet when indeterminate).</summary>
    Working,

    /// <summary>Compressed and verified: emerald ring with a check.</summary>
    Done,

    /// <summary>Original kept (nothing beat it): topaz ring with an equals mark.</summary>
    Kept,

    /// <summary>Failed: ruby ring with a cross.</summary>
    Failed,
}
