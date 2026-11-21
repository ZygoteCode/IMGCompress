using System.Reflection;
using Carbonado.UI.Cinematics;

namespace Carbonado.UI.Tests.Cinematics;

public class EasingsTests
{
    public static TheoryData<string> NamedEasings =>
    [
        nameof(Easings.Linear), nameof(Easings.Crystallize), nameof(Easings.Implode), nameof(Easings.Glide),
        nameof(Easings.Settle), nameof(Easings.ExpoOut), nameof(Easings.ExpoIn), nameof(Easings.SineInOut),
        nameof(Easings.ElasticOut),
    ];

    [Theory]
    [MemberData(nameof(NamedEasings))]
    public void Easings_StartAtZeroEndAtOne(string name)
    {
        Easing ease = Named(name);

        Assert.Equal(0, ease(0), 12);
        Assert.Equal(1, ease(1), 12);
        Assert.Equal(0, ease(-0.5), 12);
        Assert.Equal(1, ease(1.5), 12);
        for (int i = 0; i <= 200; i++)
        {
            Assert.True(double.IsFinite(ease(i / 200.0)), $"{name}({i / 200.0}) is not finite");
        }
    }

    [Fact]
    public void Bezier_MatchesKnownPoints()
    {
        // CSS "ease" curve: published reference value y(0.5) = 0.8024033877 (WebKit UnitBezier).
        Assert.Equal(0.8024033877, Easings.Bezier(0.25, 0.1, 0.25, 1)(0.5), 1e-6);

        // Symmetric "ease-in-out" passes exactly through the midpoint; the diagonal is the identity.
        Assert.Equal(0.5, Easings.Bezier(0.42, 0, 0.58, 1)(0.5), 1e-6);
        Assert.Equal(0.3, Easings.Bezier(0.3, 0.3, 0.7, 0.7)(0.3), 1e-6);

        // Dense comparison against an independent high-precision solver for the brand curves.
        foreach (var (x1, y1, x2, y2) in new[] { (0.16, 1.0, 0.3, 1.0), (0.7, 0.0, 0.84, 0.0), (0.4, 0.0, 0.2, 1.0), (0.25, 0.1, 0.25, 1.0) })
        {
            Easing ease = Easings.Bezier(x1, y1, x2, y2);
            for (int i = 0; i <= 1000; i++)
            {
                double x = i / 1000.0;
                Assert.True(Math.Abs(ease(x) - ReferenceBezier(x1, y1, x2, y2, x)) <= 1e-6, $"bezier({x1},{y1},{x2},{y2}) at {x}");
            }
        }
    }

    [Fact]
    public void BrandCurves_UseTheirSpecifiedControlPoints()
    {
        for (int i = 0; i <= 100; i++)
        {
            double x = i / 100.0;
            Assert.Equal(ReferenceBezier(0.16, 1, 0.3, 1, x), Easings.Crystallize(x), 1e-6);
            Assert.Equal(ReferenceBezier(0.7, 0, 0.84, 0, x), Easings.Implode(x), 1e-6);
            Assert.Equal(ReferenceBezier(0.4, 0, 0.2, 1, x), Easings.Glide(x), 1e-6);
        }
    }

    [Fact]
    public void Bezier_RejectsControlPointsOutsideTheUnitInterval() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Easings.Bezier(1.2, 0, 0.5, 1));

    [Fact]
    public void Settle_IsBackOutWithOvershoot115()
    {
        // Back-out with s = 1.15 peaks at u = 1 - 2s / (3(s + 1)) with ~4.9 % overshoot.
        double peak = 0;
        for (int i = 0; i <= 10_000; i++)
        {
            peak = Math.Max(peak, Easings.Settle(i / 10_000.0));
        }

        Assert.Equal(1.04874, peak, 5e-5);
        Assert.Equal(1 + (2.15 * Math.Pow(-0.5, 3)) + (1.15 * 0.25), Easings.Settle(0.5), 12);
    }

    [Fact]
    public void InAndOutCurves_HaveTheirCharacter()
    {
        Assert.True(Easings.Implode(0.5) < 0.2, "Implode starts slow");
        Assert.True(Easings.ExpoIn(0.5) < 0.05, "ExpoIn starts slow");
        Assert.True(Easings.Crystallize(0.25) > 0.6, "Crystallize starts fast");
        Assert.True(Easings.ExpoOut(0.5) > 0.95, "ExpoOut starts fast");
        Assert.Equal(0.5, Easings.SineInOut(0.5), 12);
        Assert.True(Easings.ElasticOut(0.1) > 1, "ElasticOut overshoots");
    }

    private static Easing Named(string name) =>
        (Easing)typeof(Easings).GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>Independent solver: bisection on the parametric x(s) to 1e-14, then y(s).</summary>
    private static double ReferenceBezier(double x1, double y1, double x2, double y2, double x)
    {
        static double Cubic(double p1, double p2, double s) =>
            (3 * (1 - s) * (1 - s) * s * p1) + (3 * (1 - s) * s * s * p2) + (s * s * s);

        double lo = 0, hi = 1;
        for (int i = 0; i < 200 && hi - lo > 1e-14; i++)
        {
            double mid = (lo + hi) / 2;
            if (Cubic(x1, x2, mid) < x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return Cubic(y1, y2, (lo + hi) / 2);
    }
}
