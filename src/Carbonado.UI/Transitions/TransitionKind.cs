namespace Carbonado.UI.Transitions;

/// <summary>How <see cref="TransitionHost"/> swaps its content.</summary>
public enum TransitionKind
{
    /// <summary>Signature page transition: the outgoing snapshot drifts up and disperses (chromatic aberration)
    /// while the incoming page rises out of a blur; staggered children follow.</summary>
    Refraction,

    /// <summary>Plain crossfade with staggered children.</summary>
    Fade,

    /// <summary>Instant swap.</summary>
    None,
}
