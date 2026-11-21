namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// The intro storyboard (spec §4.10) as keyframed channels: every phase time and easing lives here, the layers only
/// draw. Cinematic (3.4 s): void → implosion → ignition → crystallization → wordmark → hand-off. Short (0.9 s):
/// crystallization, wordmark and hand-off; with reduced motion it becomes a pure crossfade. Off: the final state.
/// </summary>
internal static class IntroTimelineFactory
{
    public const double CinematicDuration = 3.4;
    public const double ShortDuration = 0.9;

    /// <summary>Length of the intro for <paramref name="mode"/> in seconds.</summary>
    public static double DurationOf(IntroMode mode) => mode switch
    {
        IntroMode.Cinematic => CinematicDuration,
        IntroMode.Short => ShortDuration,
        _ => 0,
    };

    /// <summary>When the hand-off starts (the overlay stops intercepting input from here on).</summary>
    public static double HandOffTime(IntroMode mode) => mode switch
    {
        IntroMode.Cinematic => 2.9,
        IntroMode.Short => 0.55,
        _ => 0,
    };

    /// <summary>
    /// Builds the timeline writing <paramref name="state"/>. <paramref name="render"/> is bound last, so it runs after
    /// every channel on each evaluation; <paramref name="handOff"/> fires when playback crosses the hand-off.
    /// </summary>
    public static Timeline Create(IntroMode mode, bool fadeOnly, IntroState state, Action render, Action handOff)
    {
        double duration = DurationOf(mode);
        var channels = new Channels(new Timeline());
        state.FadeOnly = fadeOnly && mode != IntroMode.Off;
        channels.Add(v => state.Time = v, (0, 0, null), (duration, duration, null));
        switch (mode)
        {
            case IntroMode.Cinematic:
                Cinematic(channels, state);
                break;
            case IntroMode.Short when fadeOnly:
                ShortFadeOnly(channels, state);
                break;
            case IntroMode.Short:
                Short(channels, state);
                break;
            default:
                channels.Add(v => state.SceneOpacity = v, (0, 0, null));
                channels.Add(v => state.DiamondOpacity = v, (0, 0, null));
                channels.Add(v => state.LogoReveal = v, (0, 1, null));
                break;
        }

        Timeline timeline = channels.Timeline;
        timeline.At(HandOffTime(mode), handOff);
        timeline.Bind(Track.Double().Key(0, 0), _ => render());
        timeline.Duration = duration;
        return timeline;
    }

    private static void Cinematic(Channels c, IntroState s)
    {
        // Void: obsidian, grain fades in, carbon dust drifts.
        c.Add(v => s.Grain = v, (0, 0, null), (0.45, 0.06, Easings.SineInOut), (2.95, 0.06, null), (3.4, 0, Easings.Glide));
        c.Add(v => s.Dust = v, (0.02, 0, null), (0.4, 1, Easings.SineInOut));

        // Implosion: everything is pulled into the core; pressure, shake and dispersion build up.
        c.Add(v => s.Implosion = v, (0.35, 0, null), (1.25, 1, null));
        c.Add(v => s.Rings = v, (0.4, 0, null), (1.25, 1, null));
        c.Add(v => s.CoreHeat = v, (0.12, 0, null), (0.5, 0.17, Easings.SineInOut), (1.0, 0.42, Easings.SineInOut), (1.25, 1, Easings.ExpoIn), (1.6, 0, Easings.ExpoOut));
        c.Add(v => s.Shake = v, (0.5, 0, null), (1.25, 1, Easings.ExpoIn), (1.85, 0, Easings.ExpoOut));
        c.Add(v => s.Zoom = v, (0.35, 1, null), (1.25, 1.08, Easings.Implode), (1.36, 0.99, Easings.ExpoOut), (2.1, 1, Easings.SineInOut));
        c.Add(v => s.Aberration = v, (0.55, 0, null), (1.25, 0.9, Easings.ExpoIn), (1.3, 1, null), (2.0, 0, Easings.ExpoOut));

        // Ignition: white-hot flash, shockwave with refraction, god rays, sparks.
        c.Add(v => s.Flash = v, (1.25, 0, null), (1.29, 1, Easings.ExpoOut), (1.85, 0, Easings.ExpoOut));
        c.Add(v => s.Shockwave = v, (1.26, 0, null), (2.2, 1, Easings.ExpoOut));
        c.Add(v => s.Ripple = v, (1.25, 0, null), (1.31, 1, null), (2.0, 0, Easings.SineInOut));
        c.Add(v => s.Rays = v, (1.25, 0, null), (1.65, 1, Easings.ExpoOut));
        c.Add(v => s.RaysAlpha = v, (1.25, 0, null), (1.3, 1, Easings.ExpoOut), (1.7, 0.6, Easings.SineInOut), (2.4, 0, Easings.Glide));
        c.Add(v => s.Sparks = v, (1.26, 0, null), (2.3, 1, null));

        // Crystallization: the diamond is born white-hot inside the flash, settles, spins and cools; glints and fire.
        c.Add(v => s.DiamondScale = v, (1.4, 0, null), (2.05, 1, Easings.Settle));
        c.Add(v => s.DiamondSpin = v, (1.4, -220, null), (2.62, 0, Easings.Crystallize), (2.9, 10, null), (3.3, 90, Easings.Glide));
        c.Add(v => s.DiamondTilt = v, (2.9, 18, null), (3.3, 90, Easings.Glide));
        c.Add(v => s.DiamondHeat = v, (1.4, 1, null), (2.0, 0, Easings.ExpoOut));
        c.Add(v => s.DiamondGlow = v, (1.3, 0, null), (1.62, 1, Easings.ExpoOut), (2.65, 0.7, Easings.SineInOut), (3.16, 0.5, null), (3.34, 0, Easings.Glide));
        c.Add(v => s.Emissive = v, (1.4, 0, null), (1.85, 0.55, Easings.SineInOut), (2.7, 0.25, Easings.SineInOut));
        c.Add(v => s.LightSweep = v, (1.4, 0, null), (3.4, 310, null));
        c.Add(v => s.Fringe = v, (1.4, 0, null), (1.62, 0.9, Easings.ExpoOut), (2.4, 0.3, null), (2.9, 0, Easings.SineInOut));
        c.Add(v => s.Dispersion = v, (1.45, 0, null), (2.45, 1, Easings.Glide));
        c.Add(v => s.DispersionAlpha = v, (1.45, 0, null), (1.72, 1, Easings.SineInOut), (2.1, 0.8, null), (2.45, 0, Easings.SineInOut));
        Glints(c, s, [1.7, 1.94, 2.18, 2.44, 2.7, 2.98], 0.32);

        // Wordmark: letters cut in (blur → sharp, tracking 0.6 → 0.18 em, rise), sheen, tagline types in.
        Letters(c, s, start: 2.1, stagger: 0.045, duration: 0.45);
        c.Add(v => s.Sheen = v, (2.5, 0, null), (2.92, 1, Easings.SineInOut));
        c.Add(v => s.Tagline = v, (2.42, 0, null), (2.84, 1, null));
        c.Add(v => s.TextExit = v, (2.92, 0, null), (3.16, 1, Easings.Glide));

        // Hand-off: the diamond flies to the logo slot while the intro dissolves.
        c.Add(v => s.Flight = v, (2.9, 0, null), (3.32, 1, Easings.Glide));
        c.Add(v => s.SceneOpacity = v, (3.02, 1, null), (3.38, 0, Easings.Glide));
        c.Add(v => s.LogoReveal = v, (3.2, 0, null), (3.36, 1, Easings.SineInOut));
        c.Add(v => s.DiamondOpacity = v, (3.22, 1, null), (3.37, 0, Easings.SineInOut));
        c.Add(v => s.LogoGlint = v, (3.24, 0, null), (3.31, 1, Easings.ExpoOut), (3.4, 0, Easings.SineInOut));
    }

    private static void Short(Channels c, IntroState s)
    {
        c.Add(v => s.Grain = v, (0, 0.045, null), (0.9, 0, Easings.Glide));
        c.Add(v => s.Flash = v, (0, 0, null), (0.05, 0.4, Easings.ExpoOut), (0.4, 0, Easings.ExpoOut));
        c.Add(v => s.DiamondScale = v, (0, 0.55, null), (0.38, 1, Easings.Settle));
        c.Add(v => s.DiamondSpin = v, (0, -80, null), (0.46, 0, Easings.Crystallize), (0.55, 4, null), (0.85, 90, Easings.Glide));
        c.Add(v => s.DiamondTilt = v, (0.55, 18, null), (0.85, 90, Easings.Glide));
        c.Add(v => s.DiamondHeat = v, (0, 0.85, null), (0.32, 0, Easings.ExpoOut));
        c.Add(v => s.DiamondGlow = v, (0, 0.2, null), (0.15, 1, Easings.ExpoOut), (0.55, 0.65, null), (0.82, 0, Easings.Glide));
        c.Add(v => s.Emissive = v, (0, 0.45, null), (0.45, 0.22, null));
        c.Add(v => s.LightSweep = v, (0, 0, null), (0.9, 120, null));
        c.Add(v => s.Fringe = v, (0, 0.6, null), (0.45, 0, Easings.SineInOut));
        Glints(c, s, [0.24], 0.3);
        Letters(c, s, start: 0.08, stagger: 0.02, duration: 0.28);
        c.Add(v => s.Sheen = v, (0.3, 0, null), (0.58, 1, Easings.SineInOut));
        c.Add(v => s.Tagline = v, (0.2, 0, null), (0.48, 1, null));
        c.Add(v => s.TextExit = v, (0.6, 0, null), (0.76, 1, Easings.Glide));
        c.Add(v => s.Flight = v, (0.55, 0, null), (0.85, 1, Easings.Glide));
        c.Add(v => s.SceneOpacity = v, (0.58, 1, null), (0.86, 0, Easings.Glide));
        c.Add(v => s.LogoReveal = v, (0.74, 0, null), (0.88, 1, Easings.SineInOut));
        c.Add(v => s.DiamondOpacity = v, (0.76, 1, null), (0.88, 0, Easings.SineInOut));
        c.Add(v => s.LogoGlint = v, (0.76, 0, null), (0.82, 1, Easings.ExpoOut), (0.9, 0, Easings.SineInOut));
    }

    /// <summary>Reduced motion: the same beats as crossfades only (no scale, spin, flight, rise or typing).</summary>
    private static void ShortFadeOnly(Channels c, IntroState s)
    {
        c.Add(v => s.DiamondScale = v, (0, 1, null));
        c.Add(v => s.LightSweep = v, (0, 35, null));
        c.Add(v => s.Emissive = v, (0, 0.25, null));
        c.Add(v => s.DiamondOpacity = v, (0, 0, null), (0.25, 1, Easings.SineInOut), (0.6, 1, null), (0.86, 0, Easings.SineInOut));
        c.Add(v => s.DiamondGlow = v, (0, 0, null), (0.25, 0.8, Easings.SineInOut), (0.6, 0.6, null), (0.86, 0, Easings.SineInOut));
        Letters(c, s, start: 0.12, stagger: 0.015, duration: 0.3);
        c.Add(v => s.Tagline = v, (0.2, 0, null), (0.45, 1, Easings.SineInOut));
        c.Add(v => s.TextExit = v, (0.6, 0, null), (0.84, 1, Easings.SineInOut));
        c.Add(v => s.SceneOpacity = v, (0.6, 1, null), (0.88, 0, Easings.SineInOut));
        c.Add(v => s.LogoReveal = v, (0.62, 0, null), (0.88, 1, Easings.SineInOut));
    }

    private static void Letters(Channels c, IntroState s, double start, double stagger, double duration)
    {
        for (int i = 0; i < IntroState.LetterCount; i++)
        {
            int index = i;
            double begin = start + (i * stagger);
            c.Add(v => s.Letters[index] = v, (begin, 0, null), (begin + duration, 1, Easings.Crystallize));
        }
    }

    private static void Glints(Channels c, IntroState s, double[] starts, double duration)
    {
        for (int i = 0; i < starts.Length; i++)
        {
            int index = i;
            c.Add(v => s.Glints[index] = v, (starts[i], 0, null), (starts[i] + duration, 1, null));
        }
    }

    /// <summary>Terse channel binding: one <see cref="Track{T}"/> of doubles per state field.</summary>
    private readonly record struct Channels(Timeline Timeline)
    {
        public void Add(Action<double> apply, params (double Time, double Value, Easing? Ease)[] keys)
        {
            Track<double> track = Track.Double();
            foreach (var (time, value, ease) in keys)
            {
                track.Key(time, value, ease);
            }

            Timeline.Bind(track, apply);
        }
    }
}
