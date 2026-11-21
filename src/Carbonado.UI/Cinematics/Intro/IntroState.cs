namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// Every animated channel of the intro at one instant. The timeline built by <see cref="IntroTimelineFactory"/>
/// writes these values for time t; the layers only read them, so a frame is a pure function of t. Unbound channels
/// keep their resting defaults. Values are 0..1 unless stated otherwise.
/// </summary>
internal sealed class IntroState
{
    public const int LetterCount = 9;
    public const int GlintCount = 6;

    /// <summary>Seconds since the start of the intro.</summary>
    public double Time;

    /// <summary>Reduced motion: transforms collapse to fades (no spin, flight, rise, tracking or typing).</summary>
    public bool FadeOnly;

    // ----- Void -----
    public double Grain;
    public double Dust;

    // ----- Implosion -----
    /// <summary>Implosion clock: particles leave at their seeded delay and all reach the core at 1.</summary>
    public double Implosion;
    public double Rings;
    public double CoreHeat;
    public double Shake;

    /// <summary>Camera scale around the core (1 = rest).</summary>
    public double Zoom = 1;
    public double Aberration;

    // ----- Ignition -----
    public double Flash;

    /// <summary>Shockwave front, already eased: 0 at the core, 1 past the screen corners.</summary>
    public double Shockwave;
    public double Ripple;
    public double Rays;
    public double RaysAlpha;
    public double Sparks;

    // ----- Crystallization -----
    public double DiamondScale;

    /// <summary>Spin around the vertical axis, degrees (landing on a multiple of 45° matches the 2D mark).</summary>
    public double DiamondSpin;

    /// <summary>View elevation, degrees: 0 = side view, 90 = top view (the mark).</summary>
    public double DiamondTilt = 18;

    /// <summary>White-hot birth glow of the freshly formed diamond.</summary>
    public double DiamondHeat;
    public double DiamondGlow;
    public double DiamondOpacity = 1;
    public double Emissive;

    /// <summary>Light rig rotation, degrees.</summary>
    public double LightSweep;

    /// <summary>Spectral fringing of the diamond (chromatic aberration amount).</summary>
    public double Fringe;

    /// <summary>Dispersion fan sweep progress.</summary>
    public double Dispersion;
    public double DispersionAlpha;

    /// <summary>Facet glint progress per scheduled sparkle (visible strictly between 0 and 1).</summary>
    public readonly double[] Glints = new double[GlintCount];

    // ----- Wordmark -----
    /// <summary>Per-letter reveal progress (eased; staggered by the timeline).</summary>
    public readonly double[] Letters = new double[LetterCount];
    public double Sheen;
    public double Tagline;
    public double TextExit;

    // ----- Hand-off -----
    /// <summary>Shared-element flight of the diamond to the logo slot (eased).</summary>
    public double Flight;
    public double SceneOpacity = 1;
    public double LogoReveal;
    public double LogoGlint;
}
