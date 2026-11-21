using System.Windows;
using System.Windows.Media;

namespace Carbonado.UI.Controls;

/// <summary>
/// Frozen geometry builders shared by the ring, gauge and donut controls. Angles are degrees measured clockwise
/// from 12 o'clock.
/// </summary>
internal static class Arcs
{
    /// <summary>Point on the circle at <paramref name="degrees"/> (0 = top, clockwise).</summary>
    public static Point On(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(center.X + (radius * Math.Sin(rad)), center.Y - (radius * Math.Cos(rad)));
    }

    /// <summary>Open circular arc for stroking; sweeps of 360° and more give the full circle.</summary>
    public static Geometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        if (sweepDegrees >= 359.99)
        {
            return Frozen(new EllipseGeometry(center, radius, radius));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(On(center, radius, startDegrees), false, false);
            ctx.ArcTo(On(center, radius, startDegrees + sweepDegrees), new Size(radius, radius), 0,
                sweepDegrees > 180, SweepDirection.Clockwise, true, true);
        }

        return Frozen(geometry);
    }

    /// <summary>Filled ring sector between <paramref name="inner"/> and <paramref name="outer"/> radii.</summary>
    public static Geometry Sector(Point center, double outer, double inner, double startDegrees, double sweepDegrees)
    {
        sweepDegrees = Math.Min(sweepDegrees, 359.99);
        bool large = sweepDegrees > 180;
        double end = startDegrees + sweepDegrees;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(On(center, outer, startDegrees), true, true);
            ctx.ArcTo(On(center, outer, end), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(On(center, inner, end), true, true);
            ctx.ArcTo(On(center, inner, startDegrees), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, true);
        }

        return Frozen(geometry);
    }

    /// <summary>
    /// The first <paramref name="fraction"/> of an octagon's perimeter (flat top, circumradius
    /// <paramref name="radius"/>), starting at the middle of the top edge and running clockwise. 1 = closed octagon.
    /// </summary>
    public static Geometry Octagon(Point center, double radius, double fraction = 1)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        Span<Point> corners = stackalloc Point[8];
        for (int k = 0; k < 8; k++)
        {
            corners[k] = On(center, radius, 22.5 + (k * 45));
        }

        var start = new Point((corners[7].X + corners[0].X) / 2, corners[0].Y);
        double edge = (corners[0] - corners[7]).Length;
        double remaining = fraction * edge * 8;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, fraction >= 1);
            var from = start;
            for (int k = 0; k <= 8 && remaining > 0; k++)
            {
                var to = k == 8 ? start : corners[k];
                double length = (to - from).Length;
                if (length >= remaining)
                {
                    ctx.LineTo(from + ((to - from) * (remaining / length)), true, true);
                    break;
                }

                ctx.LineTo(to, true, true);
                remaining -= length;
                from = to;
            }
        }

        return Frozen(geometry);
    }

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
