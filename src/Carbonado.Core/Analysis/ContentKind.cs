namespace Carbonado.Core.Analysis;

public enum ContentKind
{
    /// <summary>Natural imagery: many colors, smooth gradients, noise.</summary>
    Photo,

    /// <summary>Synthetic imagery: screenshots, UI, line art, flat colors.</summary>
    Graphic,

    /// <summary>Both (e.g. a photo with overlaid UI or text).</summary>
    Mixed,
}
