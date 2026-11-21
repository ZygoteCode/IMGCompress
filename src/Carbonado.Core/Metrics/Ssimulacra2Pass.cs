using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Carbonado.Core.Metrics;

/// <summary>What one <see cref="Ssimulacra2Pass"/> computes.</summary>
internal enum PassMode
{
    /// <summary>Reference only: stores its XYB, blur(XYB) and blur(XYB²) planes for later scoring.</summary>
    BuildReference,

    /// <summary>Distorted image against stored reference planes.</summary>
    ScoreCached,

    /// <summary>Both images from scratch; nothing is stored.</summary>
    ScoreFused,
}

/// <summary>
/// One SSIMULACRA2 scale processed top to bottom in bands of rows, so working memory is O(width) instead of whole
/// planes: per band, a parallel horizontal phase (row pairs: linear rows → XYB → products → horizontal recursive
/// Gaussian into a ring of rows, plus the 2×2 downsampling for the next scale) and a parallel vertical phase
/// (64-column strips: vertical recursive Gaussian, then the SSIM and edge statistics). The per-column recurrence state
/// persists across bands, so results are identical to whole-plane processing; sums are reduced in a fixed order, so
/// they do not depend on scheduling.
/// </summary>
internal sealed unsafe class Ssimulacra2Pass
{
    /// <summary>Reference planes per scale: XYB (0..2), blur(XYB) (3..5), blur(XYB²) (6..8).</summary>
    public const int ReferencePlanes = 9;

    private const int Band = 64;
    private const int RingRows = Band + 16; // a band needs its rows plus 2·Radius of context
    private const int StripVectors = 8;
    private const int StripWidth = StripVectors * RecursiveGaussian.Lanes;
    private const int SumsPerStrip = 3 * 6;

    private static readonly float* s_zero = AllocAligned(StripWidth, clear: true); // process lifetime, never freed

    private readonly PassMode _mode;
    private readonly int _width, _height, _stride, _strips, _blurs, _xybSlots;
    private readonly ILinearSource? _reference, _distorted;
    private readonly FloatPlanes? _referencePlanes, _referenceNext, _distortedNext;
    private readonly float[]? _map;
    private readonly bool _mapTakesMax;
    private readonly ConcurrentBag<nint> _scratch = [];
    private float* _ring, _state;
    private double[] _sums = [];
    private int _n0, _n1;

    /// <param name="mode">What to compute.</param>
    /// <param name="width">Scale width.</param>
    /// <param name="height">Scale height.</param>
    /// <param name="reference">Reference rows (build and fused modes).</param>
    /// <param name="distorted">Distorted rows (scoring modes).</param>
    /// <param name="referencePlanes">The scale's <see cref="ReferencePlanes"/> planes: written (build) or read (cached).</param>
    /// <param name="referenceNext">Receives the reference's downsampled linear planes, or null at the last scale.</param>
    /// <param name="distortedNext">Receives the distorted image's downsampled linear planes, or null.</param>
    /// <param name="map">Scale-0 per-pixel error output (width × height), or null.</param>
    /// <param name="mapTakesMax">Combine with existing map values by maximum instead of overwriting.</param>
    public Ssimulacra2Pass(
        PassMode mode,
        int width,
        int height,
        ILinearSource? reference,
        ILinearSource? distorted,
        FloatPlanes? referencePlanes,
        FloatPlanes? referenceNext,
        FloatPlanes? distortedNext,
        float[]? map,
        bool mapTakesMax)
    {
        _mode = mode;
        _width = width;
        _height = height;
        _stride = (width + 15) & ~15;
        _strips = (_stride + StripWidth - 1) / StripWidth;
        _reference = reference;
        _distorted = distorted;
        _referencePlanes = referencePlanes;
        _referenceNext = referenceNext;
        _distortedNext = distortedNext;
        _map = map;
        _mapTakesMax = mapTakesMax;
        (_blurs, _xybSlots) = mode switch
        {
            PassMode.BuildReference => (6, 0),
            PassMode.ScoreCached => (9, 3),
            _ => (15, 6),
        };
    }

    /// <summary>
    /// Runs one pass per scale of <paramref name="sizes"/>, each scale reading the previous one's downsampled linear
    /// planes. <paramref name="referencePlanes"/> are written (build) or read (cached) per scale; the map, when given,
    /// receives scale 0. Returns the norms per scale (null entries when building).
    /// </summary>
    public static ScaleNorms?[] RunPyramid(
        PassMode mode,
        IReadOnlyList<(int Width, int Height)> sizes,
        ILinearSource? reference,
        ILinearSource? distorted,
        IReadOnlyList<FloatPlanes>? referencePlanes,
        float[]? map,
        bool mapTakesMax,
        CancellationToken ct)
    {
        var norms = new ScaleNorms?[sizes.Count];
        FloatPlanes? ownedReference = null, ownedDistorted = null;
        try
        {
            for (int s = 0; s < sizes.Count; s++)
            {
                bool hasNext = s + 1 < sizes.Count;
                FloatPlanes? referenceNext = hasNext && reference is not null ? new FloatPlanes(3, sizes[s + 1].Width, sizes[s + 1].Height) : null;
                FloatPlanes? distortedNext = hasNext && distorted is not null ? new FloatPlanes(3, sizes[s + 1].Width, sizes[s + 1].Height) : null;
                try
                {
                    var pass = new Ssimulacra2Pass(mode, sizes[s].Width, sizes[s].Height, reference, distorted, referencePlanes?[s], referenceNext, distortedNext, s == 0 ? map : null, mapTakesMax);
                    norms[s] = pass.Run(ct);
                }
                catch
                {
                    referenceNext?.Dispose();
                    distortedNext?.Dispose();
                    throw;
                }

                ownedReference?.Dispose();
                ownedDistorted?.Dispose();
                (ownedReference, ownedDistorted) = (referenceNext, distortedNext);
                (reference, distorted) = (referenceNext, distortedNext);
            }
        }
        finally
        {
            ownedReference?.Dispose();
            ownedDistorted?.Dispose();
        }

        return norms;
    }

    /// <summary>Runs the pass; returns the scale's norms, or null when building the reference.</summary>
    public ScaleNorms? Run(CancellationToken ct)
    {
        long ringFloats = (long)(_blurs + _xybSlots) * RingRows * _stride;
        long stateFloats = (long)_strips * _blurs * StripVectors * RecursiveGaussian.StateFloatsPerVector;
        _ring = AllocAligned(ringFloats, clear: true); // padding columns must read as zero
        _state = AllocAligned(stateFloats, clear: true); // the recurrences start from zero
        _sums = new double[_strips * SumsPerStrip];
        var options = new ParallelOptions { CancellationToken = ct };
        try
        {
            int bands = (_height + Band - 1) / Band;
            for (int band = 0; band < bands; band++)
            {
                ct.ThrowIfCancellationRequested();
                int first = band * Band;
                _n0 = band == 0 ? -RecursiveGaussian.Radius + 1 : first;
                _n1 = Math.Min(_height, first + Band);

                // Horizontal rows needed by steps n0..n1-1 (bottom row n + Radius - 1) not produced by earlier bands.
                int rowStart = band == 0 ? 0 : first + RecursiveGaussian.Radius - 1;
                int rowEnd = Math.Min(_height, _n1 + RecursiveGaussian.Radius - 1);
                if (rowStart < rowEnd)
                {
                    Parallel.For(rowStart / 2, (rowEnd + 1) / 2, options, HorizontalPair);
                }

                Parallel.For(0, _strips, options, VerticalStrip);
            }

            return _mode == PassMode.BuildReference ? null : ScaleNorms.FromSums(_sums, _strips, (long)_width * _height);
        }
        finally
        {
            NativeMemory.AlignedFree(_ring);
            NativeMemory.AlignedFree(_state);
            _ring = _state = null;
            while (_scratch.TryTake(out nint p))
            {
                NativeMemory.AlignedFree((void*)p);
            }
        }
    }

    private float* RingRow(int slot, int y) => _ring + ((((long)slot * RingRows) + (y % RingRows)) * _stride);

    private float* Rent()
    {
        if (_scratch.TryTake(out nint p))
        {
            return (float*)p;
        }

        // Horizontal phase: 2 rows × 3 channels × 2 images + up to 9 product rows. Vertical phase: blurs × Band ×
        // strip outputs, then a strip-wide map accumulator (doubles).
        long floats = Math.Max(21L * _stride, ((long)_blurs * Band * StripWidth) + (2 * StripWidth));
        return AllocAligned(floats, clear: false);
    }

    private void Return(float* p) => _scratch.Add((nint)p);

    /// <summary>64-byte aligned native floats, so 256-bit accesses never split cache lines.</summary>
    private static float* AllocAligned(long floats, bool clear)
    {
        var p = (float*)NativeMemory.AlignedAlloc((nuint)(floats * sizeof(float)), 64);
        if (clear)
        {
            NativeMemory.Clear(p, (nuint)(floats * sizeof(float)));
        }

        return p;
    }

    /// <summary>Rows 2j and 2j+1: linear rows, XYB, horizontal blurs, and downsampled row j of the next scale.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void HorizontalPair(int j)
    {
        float* scratch = Rent();
        try
        {
            int y0 = 2 * j, y1 = Math.Min(y0 + 1, _height - 1);
            float* lin = scratch; // [image][row][channel] rows of _stride floats
            float* products = scratch + (12L * _stride);
            for (int y = y0; y <= y1; y++)
            {
                int r = y - y0;
                float* ref0 = lin + (((0 * 2) + r) * 3L * _stride);
                float* dist0 = lin + (((1 * 2) + r) * 3L * _stride);
                _reference?.Read(y, ref0, ref0 + _stride, ref0 + (2L * _stride));
                _distorted?.Read(y, dist0, dist0 + _stride, dist0 + (2L * _stride));
                HorizontalRow(y, ref0, dist0, products);
            }

            float* refRow1 = lin + (y1 > y0 ? 3L * _stride : 0);
            float* distRow1 = lin + (((1 * 2) + (y1 > y0 ? 1 : 0)) * 3L * _stride);
            for (int c = 0; c < 3; c++)
            {
                if (_referenceNext is { } rn)
                {
                    XybImage.DownsampleRow(lin + (c * (long)_stride), refRow1 + (c * (long)_stride), _width, rn.Row(c, j));
                }

                if (_distortedNext is { } dn)
                {
                    XybImage.DownsampleRow(lin + ((6 + c) * (long)_stride), distRow1 + (c * (long)_stride), _width, dn.Row(c, j));
                }
            }
        }
        finally
        {
            Return(scratch);
        }
    }

    /// <summary>XYB of row <paramref name="y"/>, the inputs of every blur, then the horizontal blurs two rows at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void HorizontalRow(int y, float* refLinear, float* distLinear, float* products)
    {
        long s = _stride;
        float** inputs = stackalloc float*[15]; // per blur slot, in ring order
        switch (_mode)
        {
            case PassMode.BuildReference:
            {
                // Blurs per channel: XYB1, XYB1².
                var planes = _referencePlanes!;
                XybImage.FromLinear(refLinear, refLinear + s, refLinear + (2 * s), planes.Row(0, y), planes.Row(1, y), planes.Row(2, y), _width);
                for (int c = 0; c < 3; c++)
                {
                    float* i1 = planes.Row(c, y);
                    inputs[2 * c] = i1;
                    inputs[(2 * c) + 1] = Multiply(i1, i1, products + (c * s));
                }

                break;
            }

            case PassMode.ScoreCached:
            {
                // Blurs per channel: XYB2, XYB2², XYB1·XYB2.
                XybImage.FromLinear(distLinear, distLinear + s, distLinear + (2 * s), RingRow(_blurs, y), RingRow(_blurs + 1, y), RingRow(_blurs + 2, y), _width);
                for (int c = 0; c < 3; c++)
                {
                    float* i1 = _referencePlanes!.Row(c, y), i2 = RingRow(_blurs + c, y);
                    inputs[3 * c] = i2;
                    inputs[(3 * c) + 1] = Multiply(i2, i2, products + (2 * c * s));
                    inputs[(3 * c) + 2] = Multiply(i1, i2, products + (((2 * c) + 1) * s));
                }

                break;
            }

            default:
            {
                // Blurs per channel: XYB1, XYB1², XYB2, XYB2², XYB1·XYB2.
                XybImage.FromLinear(refLinear, refLinear + s, refLinear + (2 * s), RingRow(_blurs, y), RingRow(_blurs + 1, y), RingRow(_blurs + 2, y), _width);
                XybImage.FromLinear(distLinear, distLinear + s, distLinear + (2 * s), RingRow(_blurs + 3, y), RingRow(_blurs + 4, y), RingRow(_blurs + 5, y), _width);
                for (int c = 0; c < 3; c++)
                {
                    float* i1 = RingRow(_blurs + c, y), i2 = RingRow(_blurs + 3 + c, y);
                    inputs[5 * c] = i1;
                    inputs[(5 * c) + 1] = Multiply(i1, i1, products + (3 * c * s));
                    inputs[(5 * c) + 2] = i2;
                    inputs[(5 * c) + 3] = Multiply(i2, i2, products + (((3 * c) + 1) * s));
                    inputs[(5 * c) + 4] = Multiply(i1, i2, products + (((3 * c) + 2) * s));
                }

                break;
            }
        }

        for (int k = 0; k < _blurs; k += 2)
        {
            int k2 = Math.Min(k + 1, _blurs - 1); // an odd last blur runs in both halves
            RecursiveGaussian.Horizontal(inputs[k], RingRow(k, y), inputs[k2], RingRow(k2, y), _width);
        }
    }

    /// <summary>Element-wise float product into <paramref name="output"/>, which it returns.</summary>
    private float* Multiply(float* a, float* b, float* output)
    {
        int x = 0;
        for (; x <= _width - 8; x += 8)
        {
            (Vector256.Load(a + x) * Vector256.Load(b + x)).Store(output + x);
        }

        for (; x < _width; x++)
        {
            output[x] = a[x] * b[x];
        }

        return output;
    }

    /// <summary>Vertical recursion of every blur for one strip of columns over the band, then its statistics.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void VerticalStrip(int strip)
    {
        int x0 = strip * StripWidth;
        int vectors = Math.Min(StripVectors, (_stride - x0) / RecursiveGaussian.Lanes);
        int firstRow = Math.Max(_n0, 0);
        float* scratch = _mode == PassMode.BuildReference ? null : Rent();
        try
        {
            for (int blur = 0; blur < _blurs; blur++)
            {
                var rows = new RecursiveGaussian.RowRing(_ring + ((long)blur * RingRows * _stride) + x0, RingRows, _stride);
                float* state = _state + ((((long)strip * _blurs) + blur) * StripVectors * RecursiveGaussian.StateFloatsPerVector);
                float* outBase;
                nint outStride;
                if (_mode == PassMode.BuildReference)
                {
                    // Outputs go straight into the reference planes: blur(XYB) → 3 + c, blur(XYB²) → 6 + c.
                    int plane = (blur % 2 == 0 ? 3 : 6) + (blur / 2);
                    outBase = _referencePlanes!.Row(plane, 0) + x0;
                    outStride = _referencePlanes.Stride;
                }
                else
                {
                    outBase = scratch + ((long)blur * Band * StripWidth) - ((long)firstRow * StripWidth);
                    outStride = StripWidth;
                }

                RecursiveGaussian.Vertical(rows, s_zero, _height, vectors, _n0, _n1, state, outBase, outStride);
            }

            if (scratch != null)
            {
                Accumulate(strip, x0, firstRow, scratch);
            }
        }
        finally
        {
            if (scratch != null)
            {
                Return(scratch);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Accumulate(int strip, int x0, int firstRow, float* scratch)
    {
        int count = Math.Min(StripWidth, _width - x0);
        double* mapAcc = (double*)(scratch + ((long)_blurs * Band * StripWidth));
        bool fused = _mode == PassMode.ScoreFused;
        fixed (double* sums = &_sums[strip * SumsPerStrip])
        {
            for (int y = firstRow; y < _n1; y++)
            {
                if (_map is not null)
                {
                    new Span<double>(mapAcc, count).Clear();
                }

                float* rowOut = scratch + ((long)(y - firstRow) * StripWidth);
                for (int c = 0; c < 3; c++)
                {
                    float* i1, mu1, s11, i2, blurs;
                    if (fused)
                    {
                        // Blurs per channel: XYB1, XYB1², XYB2, XYB2², XYB1·XYB2.
                        i1 = RingRow(_blurs + c, y) + x0;
                        i2 = RingRow(_blurs + 3 + c, y) + x0;
                        mu1 = rowOut + Plane(5 * c);
                        s11 = rowOut + Plane((5 * c) + 1);
                        blurs = rowOut + Plane((5 * c) + 2);
                    }
                    else
                    {
                        // Reference planes from the cache; blurs per channel: XYB2, XYB2², XYB1·XYB2.
                        var planes = _referencePlanes!;
                        i1 = planes.Row(c, y) + x0;
                        mu1 = planes.Row(3 + c, y) + x0;
                        s11 = planes.Row(6 + c, y) + x0;
                        i2 = RingRow(_blurs + c, y) + x0;
                        blurs = rowOut + Plane(3 * c);
                    }

                    AccumulateRow(i1, mu1, s11, i2, blurs, blurs + Plane(1), blurs + Plane(2), count, sums + (c * 6), _map is null ? null : mapAcc);
                }

                if (_map is not null)
                {
                    StoreMapRow(y, x0, count, mapAcc);
                }
            }
        }
    }

    private static long Plane(int blur) => (long)blur * Band * StripWidth;

    /// <summary>
    /// libjxl SSIMMap and EdgeDiffMap for one row segment of one channel, with the same float/double mix per pixel:
    /// adds Σd, Σd⁴, Σartifact, Σartifact⁴, Σdetail-lost, Σdetail-lost⁴ to <paramref name="sums"/> and, when
    /// <paramref name="map"/> is not null, d + |edge difference| of each pixel to it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void AccumulateRow(float* i1, float* mu1, float* s11, float* i2, float* mu2, float* s22, float* s12, int count, double* sums, double* map)
    {
        var one = Vector256.Create(1.0);
        var zero = Vector256<double>.Zero;
        var two = Vector256.Create(2f);
        var c2 = Vector256.Create(Ssimulacra2.C2);
        Vector256<double> sd = zero, sd4 = zero, sa = zero, sa4 = zero, sl = zero, sl4 = zero;
        int x = 0;
        for (; x <= count - 8; x += 8)
        {
            var m1 = Vector256.Load(mu1 + x);
            var m2 = Vector256.Load(mu2 + x);
            var diff = m1 - m2;
            var (dl, dh) = Vector256.Widen(diff * diff);
            var numM = Vector256.Narrow(one - dl, one - dh);
            var numS = (two * (Vector256.Load(s12 + x) - (m1 * m2))) + c2;
            var denS = ((Vector256.Load(s11 + x) - (m1 * m1)) + (Vector256.Load(s22 + x) - (m2 * m2))) + c2;
            var (ql, qh) = Vector256.Widen((numM * numS) / denS);
            var dLo = Vector256.MaxNative(one - ql, zero);
            var dHi = Vector256.MaxNative(one - qh, zero);
            var (a2l, a2h) = Vector256.Widen(Vector256.Abs(Vector256.Load(i2 + x) - m2));
            var (a1l, a1h) = Vector256.Widen(Vector256.Abs(Vector256.Load(i1 + x) - m1));
            var eLo = ((one + a2l) / (one + a1l)) - one;
            var eHi = ((one + a2h) / (one + a1h)) - one;
            Add(dLo, eLo, ref sd, ref sd4, ref sa, ref sa4, ref sl, ref sl4);
            Add(dHi, eHi, ref sd, ref sd4, ref sa, ref sa4, ref sl, ref sl4);
            if (map != null)
            {
                (Vector256.Load(map + x) + dLo + Vector256.Abs(eLo)).Store(map + x);
                (Vector256.Load(map + x + 4) + dHi + Vector256.Abs(eHi)).Store(map + x + 4);
            }
        }

        double td = 0, td4 = 0, ta = 0, ta4 = 0, tl = 0, tl4 = 0;
        for (; x < count; x++)
        {
            float m1 = mu1[x], m2 = mu2[x];
            float diff = m1 - m2;
            float numM = (float)(1.0 - (diff * diff));
            float numS = (2 * (s12[x] - (m1 * m2))) + Ssimulacra2.C2;
            float denS = ((s11[x] - (m1 * m1)) + (s22[x] - (m2 * m2))) + Ssimulacra2.C2;
            double d = Math.Max(1.0 - (numM * numS / denS), 0.0);
            double e = ((1.0 + Math.Abs(i2[x] - m2)) / (1.0 + Math.Abs(i1[x] - m1))) - 1.0;
            double art = Math.Max(e, 0.0), lost = Math.Max(-e, 0.0);
            td += d;
            td4 += Quartic(d);
            ta += art;
            ta4 += Quartic(art);
            tl += lost;
            tl4 += Quartic(lost);
            if (map != null)
            {
                map[x] += d + Math.Abs(e);
            }
        }

        sums[0] += Vector256.Sum(sd) + td;
        sums[1] += Vector256.Sum(sd4) + td4;
        sums[2] += Vector256.Sum(sa) + ta;
        sums[3] += Vector256.Sum(sa4) + ta4;
        sums[4] += Vector256.Sum(sl) + tl;
        sums[5] += Vector256.Sum(sl4) + tl4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(Vector256<double> d, Vector256<double> e, ref Vector256<double> sd, ref Vector256<double> sd4, ref Vector256<double> sa, ref Vector256<double> sa4, ref Vector256<double> sl, ref Vector256<double> sl4)
    {
        var zero = Vector256<double>.Zero;
        var art = Vector256.MaxNative(e, zero);
        var lost = Vector256.MaxNative(-e, zero);
        sd += d;
        sd4 += Quartic(d);
        sa += art;
        sa4 += Quartic(art);
        sl += lost;
        sl4 += Quartic(lost);
    }

    // libjxl quartic(): x *= x; x *= x.
    private static double Quartic(double x) => (x * x) * (x * x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Quartic(Vector256<double> x)
    {
        var x2 = x * x;
        return x2 * x2;
    }

    private void StoreMapRow(int y, int x0, int count, double* acc)
    {
        Span<float> row = _map.AsSpan((y * _width) + x0, count);
        for (int x = 0; x < count; x++)
        {
            float e = (float)Math.Min(1.0, acc[x] / 3);
            row[x] = _mapTakesMax ? Math.Max(row[x], e) : e;
        }
    }
}
