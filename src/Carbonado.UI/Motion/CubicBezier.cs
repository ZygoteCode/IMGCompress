namespace Carbonado.UI.Motion;

/// <summary>
/// CSS-style cubic-bezier timing curve from (0,0) to (1,1) with control points (x1,y1) and (x2,y2).
/// <see cref="Evaluate"/> solves x(t) = progress with Newton iterations and a bisection fallback (error ≤ 1e-6).
/// </summary>
public readonly struct CubicBezier
{
    private readonly double _cx;
    private readonly double _bx;
    private readonly double _ax;
    private readonly double _cy;
    private readonly double _by;
    private readonly double _ay;

    public CubicBezier(double x1, double y1, double x2, double y2)
    {
        if (x1 is < 0 or > 1 || x2 is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(x1), "Control point x values must be within [0, 1].");
        }

        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        _cx = 3.0 * x1;
        _bx = (3.0 * (x2 - x1)) - _cx;
        _ax = 1.0 - _cx - _bx;
        _cy = 3.0 * y1;
        _by = (3.0 * (y2 - y1)) - _cy;
        _ay = 1.0 - _cy - _by;
    }

    public double X1 { get; }

    public double Y1 { get; }

    public double X2 { get; }

    public double Y2 { get; }

    /// <summary>Eased value for linear progress <paramref name="progress"/> in [0, 1] (clamped).</summary>
    public double Evaluate(double progress)
    {
        if (progress <= 0)
        {
            return 0;
        }

        if (progress >= 1)
        {
            return 1;
        }

        return SampleY(SolveX(progress));
    }

    private double SampleX(double t) => ((((_ax * t) + _bx) * t) + _cx) * t;

    private double SampleY(double t) => ((((_ay * t) + _by) * t) + _cy) * t;

    private double SampleDerivativeX(double t) => (((3.0 * _ax * t) + (2.0 * _bx)) * t) + _cx;

    private double SolveX(double x)
    {
        const double epsilon = 1e-7;
        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double error = SampleX(t) - x;
            if (Math.Abs(error) < epsilon)
            {
                return t;
            }

            double d = SampleDerivativeX(t);
            if (Math.Abs(d) < 1e-6)
            {
                break;
            }

            t -= error / d;
        }

        double lo = 0;
        double hi = 1;
        t = x;
        for (int i = 0; i < 64; i++)
        {
            double value = SampleX(t);
            if (Math.Abs(value - x) < epsilon)
            {
                return t;
            }

            if (value < x)
            {
                lo = t;
            }
            else
            {
                hi = t;
            }

            t = (lo + hi) / 2;
        }

        return t;
    }
}
