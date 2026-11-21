namespace Carbonado.UI.Cinematics.Intro;

/// <summary>How the startup intro plays (user setting; reduced motion turns <see cref="Cinematic"/> into <see cref="Short"/>).</summary>
public enum IntroMode
{
    /// <summary>The full 3.4 s storyboard: void, implosion, ignition, crystallization, wordmark, hand-off.</summary>
    Cinematic,

    /// <summary>0.9 s: crystallization, wordmark and hand-off only.</summary>
    Short,

    /// <summary>No intro: the shell shows directly.</summary>
    Off,
}
