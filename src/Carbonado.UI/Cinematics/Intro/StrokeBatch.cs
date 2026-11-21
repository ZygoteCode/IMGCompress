using System.Windows;
using System.Windows.Media;
using Carbonado.UI.Brand;

namespace Carbonado.UI.Cinematics.Intro;

/// <summary>
/// Draws hundreds of particle segments per frame cheaply: segments are queued into a reused buffer and drawn as
/// plain lines with cached frozen pens, keyed by palette color, width class and quantized alpha (optionally preceded
/// by a wide, faint glow pass). No geometry objects are built per frame, so the per-frame garbage stays at the
/// drawing records themselves (a StreamGeometry per alpha bucket costs ~3 KB each and multiplies GC pauses).
/// </summary>
internal sealed class StrokeBatch
{
    /// <summary>Alpha quantization steps (pens are cached per step).</summary>
    public const int AlphaLevels = 24;

    private readonly Color[] _colors;
    private readonly double[] _widths;
    private readonly PenLineCap _cap;
    private readonly double _glowWidth;
    private readonly double _glowAlpha;
    private readonly Pen?[] _pens;
    private readonly Pen?[] _glowPens;
    private Segment[] _segments = new Segment[512];
    private int _count;

    /// <param name="colors">Palette; segments refer to it by index.</param>
    /// <param name="widths">Stroke width classes (in the drawing's units).</param>
    /// <param name="cap">Line caps of the core strokes.</param>
    /// <param name="glowWidth">Glow stroke width multiplier (0 = no glow pass).</param>
    /// <param name="glowAlpha">Glow alpha multiplier.</param>
    public StrokeBatch(IReadOnlyList<Color> colors, IReadOnlyList<double> widths, PenLineCap cap, double glowWidth = 0, double glowAlpha = 0)
    {
        _colors = [.. colors];
        _widths = [.. widths];
        _cap = cap;
        _glowWidth = glowWidth;
        _glowAlpha = glowAlpha;
        int keys = _colors.Length * _widths.Length * AlphaLevels;
        _pens = new Pen?[keys];
        _glowPens = new Pen?[keys];
    }

    /// <summary>Queues a segment; alpha below half a quantization step is dropped.</summary>
    public void Add(int color, int width, double alpha, Point from, Point to)
    {
        int level = (int)Math.Round(alpha * AlphaLevels) - 1;
        if (level < 0)
        {
            return;
        }

        if (_count == _segments.Length)
        {
            Array.Resize(ref _segments, _count * 2);
        }

        int key = (((color * _widths.Length) + width) * AlphaLevels) + Math.Min(level, AlphaLevels - 1);
        _segments[_count++] = new Segment(key, from, to);
    }

    /// <summary>Draws the queued segments (the whole glow pass first, then the cores, in queue order) and resets.</summary>
    public void Flush(DrawingContext dc)
    {
        if (_glowWidth > 0)
        {
            for (int i = 0; i < _count; i++)
            {
                ref readonly Segment s = ref _segments[i];
                dc.DrawLine(_glowPens[s.Key] ??= CreatePen(s.Key, _glowWidth, _glowAlpha), s.From, s.To);
            }
        }

        for (int i = 0; i < _count; i++)
        {
            ref readonly Segment s = ref _segments[i];
            dc.DrawLine(_pens[s.Key] ??= CreatePen(s.Key, 1, 1), s.From, s.To);
        }

        _count = 0;
    }

    private Pen CreatePen(int key, double widthFactor, double alphaFactor)
    {
        int level = key % AlphaLevels;
        int width = key / AlphaLevels % _widths.Length;
        int color = key / AlphaLevels / _widths.Length;
        double alpha = (level + 1) / (double)AlphaLevels * alphaFactor;
        var pen = new Pen(new SolidColorBrush(BrandPalette.WithAlpha(_colors[color], alpha)), _widths[width] * widthFactor)
        {
            StartLineCap = _cap,
            EndLineCap = _cap,
        };
        pen.Freeze();
        return pen;
    }

    private readonly record struct Segment(int Key, Point From, Point To);
}
