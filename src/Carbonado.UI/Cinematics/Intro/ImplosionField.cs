using System.Windows;
using System.Windows.Media;
using Carbonado.UI.Brand;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// Carbon under pressure: drifting dust in the void, ~420 particles spiralling into the core with motion streaks
/// (Implode easing, angular speed rising as they close in), converging pressure rings, and the spark burst of the
/// ignition. Every particle follows a seeded closed-form trajectory, so any instant renders directly.
/// </summary>
internal sealed class ImplosionField : IntroLayer
{
    private const int StreakCount = 420;
    private const int DustCount = 110;
    private const int SparkCount = 180;
    private const int RingCount = 5;

    // Palette indices of the batches.
    private const int White = 0, Ice = 1, Aqua = 2, Iris = 3, Orchid = 4, Rose = 5, Ember = 6, Mist = 7;

    private static readonly double[] s_widths = [0.9, 1.5, 2.3];

    private readonly DrawingVisual _visual;
    private readonly Color[] _ringTones;
    private readonly StrokeBatch _tails;
    private readonly StrokeBatch _heads;
    private readonly Streak[] _streaks = new Streak[StreakCount];
    private readonly Mote[] _dust = new Mote[DustCount];
    private readonly Spark[] _sparks = new Spark[SparkCount];

    public ImplosionField()
    {
        _visual = Add(new DrawingVisual());
        BrandPalette p = BrandPalette.Current;
        Color[] palette = [p.Diamond50, p.Ice, p.Aqua, p.Iris, p.Orchid, p.Rose, p.Ember, p.Mist300];
        _ringTones = [p.Iris, p.Aqua, p.Ice];
        _tails = new StrokeBatch(palette, s_widths, PenLineCap.Flat);
        _heads = new StrokeBatch(palette, s_widths, PenLineCap.Round, glowWidth: 3.6, glowAlpha: 0.2);

        for (int i = 0; i < StreakCount; i++)
        {
            double delay = 0.42 * Math.Pow(Hash01(i, 3), 1.4);
            _streaks[i] = new Streak(
                Angle: 2 * Math.PI * Hash01(i, 1),
                Radius: 0.24 + (0.9 * Hash01(i, 2)),
                Delay: delay,
                Arrival: 1 - (0.1 * Hash01(i, 4)),
                Swirl: 0.22 + (0.3 * Hash01(i, 5)),
                Width: Hash01(i, 6) < 0.62 ? 0 : Hash01(i, 6) < 0.92 ? 1 : 2,
                Color: PickColor(Hash01(i, 7)),
                Brightness: 0.55 + (0.45 * Hash01(i, 8)));
        }

        for (int i = 0; i < DustCount; i++)
        {
            double tone = Hash01(i, 17);
            _dust[i] = new Mote(
                Angle: 2 * Math.PI * Hash01(i, 11),
                Radius: 0.1 + (0.95 * Math.Sqrt(Hash01(i, 12))),
                Drift: (Hash01(i, 13) - 0.5) * 0.16,
                Wobble: 0.6 + Hash01(i, 14),
                Phase: 2 * Math.PI * Hash01(i, 15),
                PullStart: 0.08 + (0.5 * Hash01(i, 16)),
                Color: tone < 0.6 ? Mist : tone < 0.85 ? Iris : Ice,
                Brightness: 0.4 + (0.55 * Hash01(i, 18)),
                Twinkle: 1.2 + (2.5 * Hash01(i, 19)),
                Near: Hash01(i, 20) < 0.35);
        }

        for (int i = 0; i < SparkCount; i++)
        {
            double tone = Hash01(i, 27);
            _sparks[i] = new Spark(
                Angle: 2 * Math.PI * Hash01(i, 21),
                Distance: 0.22 + (0.85 * Math.Pow(Hash01(i, 22), 1.3)),
                Life: 0.3 + (0.7 * Hash01(i, 23)),
                Curl: (Hash01(i, 24) - 0.5) * 0.5,
                Width: Hash01(i, 25) < 0.7 ? 0 : 1,
                Color: tone < 0.3 ? White : tone < 0.55 ? Ice : tone < 0.75 ? Ember : tone < 0.88 ? Aqua : Rose,
                Brightness: 0.6 + (0.4 * Hash01(i, 26)));
        }
    }

    public override void Render(IntroState state, in IntroLayout layout)
    {
        _visual.Transform = layout.DesignTransform;
        using DrawingContext dc = _visual.RenderOpen();
        if (layout.IsEmpty)
        {
            return;
        }

        double reach = layout.Reach;
        DrawRings(dc, state, reach);
        AddDust(state, reach);
        AddStreaks(state, reach);
        AddSparks(state, reach);
        _tails.Flush(dc);
        _heads.Flush(dc);
    }

    private static int PickColor(double u) => u switch
    {
        < 0.16 => White,
        < 0.38 => Ice,
        < 0.62 => Aqua,
        < 0.8 => Iris,
        < 0.89 => Orchid,
        < 0.95 => Rose,
        _ => Ember,
    };

    private static Point StreakAt(in Streak p, double implosion, double reach, out double local, out double rho)
    {
        local = Clamp01((implosion - p.Delay) / (p.Arrival - p.Delay));
        double eased = (0.45 * local) + (0.55 * Easings.Implode(local));
        rho = 1 - eased;

        // Angular speed rises as the particle closes in (angular momentum), giving the vortex its whirl.
        double angle = p.Angle + (p.Swirl * ((1.3 * (1 - rho)) + (1 / (rho + 0.07)) - (1 / 1.07)));
        return Polar(p.Radius * reach * rho, angle);
    }

    private void AddStreaks(IntroState state, double reach)
    {
        double implosion = state.Implosion;
        if (implosion <= 0)
        {
            return;
        }

        const double trail = 0.1;
        for (int i = 0; i < _streaks.Length; i++)
        {
            ref readonly Streak p = ref _streaks[i];
            Point head = StreakAt(p, implosion, reach, out double local, out double rho);
            if (local <= 0 || local >= 1)
            {
                continue;
            }

            Point tail = StreakAt(p, implosion - trail, reach, out _, out _);
            double radius = p.Radius * reach * rho;
            double alpha = p.Brightness * SmoothStep(0, 0.12, local) * (0.45 + (0.55 * (1 - rho))) * Clamp01(radius / 16);
            AddStreak(p.Color, p.Width, alpha, 0.4, tail, head);
        }
    }

    private static Point MoteAt(in Mote m, double t, double implosion, double reach, out double pull)
    {
        // The camera drifts through the dust: near motes stream outward faster (parallax) until the pull wins.
        double push = 1 + ((m.Near ? 0.05 : 0.015) * t);
        double radius = (m.Radius * reach * push) + (7 * Math.Sin((t * m.Wobble) + m.Phase));
        double angle = m.Angle + (m.Drift * t);
        pull = Easings.Implode(Clamp01((implosion - m.PullStart) / (1 - m.PullStart)));
        return Polar(radius * (1 - pull), angle + (2.4 * pull));
    }

    private void AddDust(IntroState state, double reach)
    {
        if (state.Dust <= 0)
        {
            return;
        }

        double t = state.Time;
        for (int i = 0; i < _dust.Length; i++)
        {
            ref readonly Mote m = ref _dust[i];
            Point now = MoteAt(m, t, state.Implosion, reach, out double pull);
            if (pull >= 1)
            {
                continue;
            }

            Point before = MoteAt(m, t - 0.06, state.Implosion - 0.067, reach, out _);
            double twinkle = 0.55 + (0.45 * Math.Sin((t * m.Twinkle) + m.Phase));
            double alpha = state.Dust * m.Brightness * twinkle * (1 - (pull * pull * pull));
            if (before == now)
            {
                now.X += 0.05;
            }

            _heads.Add(m.Color, m.Near ? 1 : 0, m.Near ? alpha : alpha * 0.8, before, now);
        }
    }

    private void DrawRings(DrawingContext dc, IntroState state, double reach)
    {
        if (state.Rings <= 0 || state.Rings >= 1)
        {
            return;
        }

        for (int j = 0; j < RingCount; j++)
        {
            double start = j * 0.14;
            double q = Clamp01((state.Rings - start) / (1 - start));
            if (q <= 0 || q >= 1)
            {
                continue;
            }

            double radius = reach * (0.92 - (0.07 * j)) * (1 - Easings.Implode(q));
            double alpha = Math.Pow(Math.Sin(Math.PI * q), 0.7) * (0.12 + (0.2 * q)) * Clamp01(radius / 40);
            Color tone = _ringTones[j % _ringTones.Length];
            var glow = new Pen(new SolidColorBrush(BrandPalette.WithAlpha(tone, alpha * 0.16)), 12);
            var line = new Pen(new SolidColorBrush(BrandPalette.WithAlpha(tone, alpha)), 1.1);
            glow.Freeze();
            line.Freeze();
            dc.DrawEllipse(null, glow, default, radius, radius);
            dc.DrawEllipse(null, line, default, radius, radius);
        }
    }

    private void AddSparks(IntroState state, double reach)
    {
        double s = state.Sparks;
        if (s <= 0 || s >= 1)
        {
            return;
        }

        for (int i = 0; i < _sparks.Length; i++)
        {
            ref readonly Spark k = ref _sparks[i];
            double q = Clamp01(s / k.Life);
            if (q >= 1)
            {
                continue;
            }

            double distance = reach * k.Distance;
            double r = 4 + (distance * Easings.ExpoOut(q));
            double rt = 4 + (distance * Easings.ExpoOut(Math.Max(0, q - 0.014)));
            Point head = Polar(r, k.Angle + (k.Curl * q));
            Point tail = Polar(rt, k.Angle + (k.Curl * Math.Max(0, q - 0.014)));
            AddStreak(k.Color, k.Width, Math.Pow(1 - q, 1.6) * k.Brightness, 0.45, tail, head);
        }
    }

    /// <summary>
    /// Queues a motion streak: a bright head with a dimmer tail behind it. Short (slow) streaks are a single head
    /// segment: the split would not be visible and each segment is one more draw record per frame.
    /// </summary>
    private void AddStreak(int color, int width, double alpha, double tailAlpha, Point tail, Point head)
    {
        const double SplitLength = 7;
        double dx = head.X - tail.X, dy = head.Y - tail.Y;
        if ((dx * dx) + (dy * dy) < SplitLength * SplitLength)
        {
            _heads.Add(color, width, alpha, tail, head);
            return;
        }

        var mid = new Point(tail.X + (dx * 0.6), tail.Y + (dy * 0.6));
        _tails.Add(color, width, alpha * tailAlpha, tail, mid);
        _heads.Add(color, width, alpha, mid, head);
    }

    private readonly record struct Streak(double Angle, double Radius, double Delay, double Arrival, double Swirl, int Width, int Color, double Brightness);

    private readonly record struct Mote(double Angle, double Radius, double Drift, double Wobble, double Phase, double PullStart, int Color, double Brightness, double Twinkle, bool Near);

    private readonly record struct Spark(double Angle, double Distance, double Life, double Curl, int Width, int Color, double Brightness);
}
