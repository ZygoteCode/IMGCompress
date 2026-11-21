using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Carbonado.Core.Metrics;

/// <summary>
/// The σ = 1.5 recursive Gaussian of SSIMULACRA2 (Charalampidis 2016, three truncated-cosine modes, zero padding),
/// reproducing libjxl's float arithmetic operation for operation: the horizontal pass uses its 4-lane unrolled FMA
/// formulation and the vertical pass its column-vectorised FMA recurrence. The recurrence has its poles on the unit
/// circle, so its rounding drift is part of the reference scores; reproducing the exact operations makes the planes
/// bit-identical to <c>ssimulacra2.exe</c> (an exact convolution would score up to ~2.7 points higher at high quality).
/// </summary>
internal static unsafe class RecursiveGaussian
{
    /// <summary>N, the half-width of the recursion window: round(3.2795·σ + 0.2546) = 5 for σ = 1.5.</summary>
    public const int Radius = 5;

    /// <summary>Columns processed per vertical vector.</summary>
    public const int Lanes = 8;

    private static readonly Coefficients s_c = Coefficients.Create(1.5);

    // Horizontal pass: libjxl's 4-lane vectors per mode k (rg.mul_in / mul_prev / mul_prev2, plus mul_in shifted by
    // 1..3 lanes), duplicated in both 128-bit halves so one 256-bit operation advances two independent rows.
    private static readonly Vector256<float> s_in1 = s_c.LaneVector(s_c.MulIn, 0, 0), s_in3 = s_c.LaneVector(s_c.MulIn, 1, 0), s_in5 = s_c.LaneVector(s_c.MulIn, 2, 0);
    private static readonly Vector256<float> s_in1S1 = s_c.LaneVector(s_c.MulIn, 0, 1), s_in3S1 = s_c.LaneVector(s_c.MulIn, 1, 1), s_in5S1 = s_c.LaneVector(s_c.MulIn, 2, 1);
    private static readonly Vector256<float> s_in1S2 = s_c.LaneVector(s_c.MulIn, 0, 2), s_in3S2 = s_c.LaneVector(s_c.MulIn, 1, 2), s_in5S2 = s_c.LaneVector(s_c.MulIn, 2, 2);
    private static readonly Vector256<float> s_in1S3 = s_c.LaneVector(s_c.MulIn, 0, 3), s_in3S3 = s_c.LaneVector(s_c.MulIn, 1, 3), s_in5S3 = s_c.LaneVector(s_c.MulIn, 2, 3);
    private static readonly Vector256<float> s_prev1 = s_c.LaneVector(s_c.MulPrev, 0, 0), s_prev3 = s_c.LaneVector(s_c.MulPrev, 1, 0), s_prev5 = s_c.LaneVector(s_c.MulPrev, 2, 0);
    private static readonly Vector256<float> s_prev21 = s_c.LaneVector(s_c.MulPrev2, 0, 0), s_prev23 = s_c.LaneVector(s_c.MulPrev2, 1, 0), s_prev25 = s_c.LaneVector(s_c.MulPrev2, 2, 0);

    // Vertical pass, broadcast per mode.
    private static readonly Vector256<float> s_n2v1 = Vector256.Create(s_c.N2[0]), s_n2v3 = Vector256.Create(s_c.N2[1]), s_n2v5 = Vector256.Create(s_c.N2[2]);
    private static readonly Vector256<float> s_negD1v1 = Vector256.Create(-s_c.D1[0]), s_negD1v3 = Vector256.Create(-s_c.D1[1]), s_negD1v5 = Vector256.Create(-s_c.D1[2]);

    /// <summary>Floats of vertical-pass state per <see cref="Lanes"/>-column vector: 3 modes × a ring of 4 outputs.</summary>
    public const int StateFloatsPerVector = 3 * 4 * Lanes;

    /// <summary>
    /// Horizontal pass (libjxl FastGaussian1D) over two independent rows of <paramref name="width"/> floats at once:
    /// row A in the low and row B in the high 128-bit half of each vector, so every lane performs exactly libjxl's
    /// 4-lane operations. For a single row pass the same pointers twice.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Horizontal(float* inA, float* outA, float* inB, float* outB, int width)
    {
        const int N = Radius;
        float pa1 = 0, pa3 = 0, pa5 = 0, qa1 = 0, qa3 = 0, qa5 = 0; // prev / prev2 of each mode (lane 0 of row A)
        float pb1 = 0, pb3 = 0, pb5 = 0, qb1 = 0, qb3 = 0, qb5 = 0;
        int n = -N + 1;
        int warmEnd = Math.Min(8, width); // RoundUpTo(N + 1, 4)
        for (; n < warmEnd; n++)
        {
            ScalarStep(inA, outA, width, n, ref pa1, ref pa3, ref pa5, ref qa1, ref qa3, ref qa5);
            ScalarStep(inB, outB, width, n, ref pb1, ref pb3, ref pb5, ref qb1, ref qb3, ref qb5);
        }

        int vectorEnd = width - N + 1 - 3;
        if (n < vectorEnd)
        {
            Vector256<float> v1 = Halves(pa1, pb1), v3 = Halves(pa3, pb3), v5 = Halves(pa5, pb5);
            Vector256<float> w1 = Halves(qa1, qb1), w3 = Halves(qa3, qb3), w5 = Halves(qa5, qb5);
            for (; n < vectorEnd; n += 4)
            {
                var sum = Halves(inA + n - N - 1, inB + n - N - 1) + Halves(inA + n + N - 1, inB + n + N - 1);
                var in0 = Lane(sum, 0);
                var o1 = in0 * s_in1;
                var o3 = in0 * s_in3;
                var o5 = in0 * s_in5;
                var in1 = Lane(sum, 1);
                o1 = Vector256.FusedMultiplyAdd(s_in1S1, in1, o1);
                o3 = Vector256.FusedMultiplyAdd(s_in3S1, in1, o3);
                o5 = Vector256.FusedMultiplyAdd(s_in5S1, in1, o5);
                var in2 = Lane(sum, 2);
                o1 = Vector256.FusedMultiplyAdd(s_in1S2, in2, o1);
                o3 = Vector256.FusedMultiplyAdd(s_in3S2, in2, o3);
                o5 = Vector256.FusedMultiplyAdd(s_in5S2, in2, o5);
                var in3 = Lane(sum, 3);
                o1 = Vector256.FusedMultiplyAdd(s_in1S3, in3, o1);
                o3 = Vector256.FusedMultiplyAdd(s_in3S3, in3, o3);
                o5 = Vector256.FusedMultiplyAdd(s_in5S3, in3, o5);
                o1 = Vector256.FusedMultiplyAdd(s_prev21, w1, o1);
                o3 = Vector256.FusedMultiplyAdd(s_prev23, w3, o3);
                o5 = Vector256.FusedMultiplyAdd(s_prev25, w5, o5);
                o1 = Vector256.FusedMultiplyAdd(s_prev1, v1, o1);
                o3 = Vector256.FusedMultiplyAdd(s_prev3, v3, o3);
                o5 = Vector256.FusedMultiplyAdd(s_prev5, v5, o5);
                w1 = Lane(o1, 2);
                w3 = Lane(o3, 2);
                w5 = Lane(o5, 2);
                v1 = Lane(o1, 3);
                v3 = Lane(o3, 3);
                v5 = Lane(o5, 3);
                var o = o1 + (o3 + o5);
                o.GetLower().Store(outA + n);
                o.GetUpper().Store(outB + n);
            }

            (pa1, pa3, pa5, qa1, qa3, qa5) = (v1.GetElement(0), v3.GetElement(0), v5.GetElement(0), w1.GetElement(0), w3.GetElement(0), w5.GetElement(0));
            (pb1, pb3, pb5, qb1, qb3, qb5) = (v1.GetElement(4), v3.GetElement(4), v5.GetElement(4), w1.GetElement(4), w3.GetElement(4), w5.GetElement(4));
        }

        for (; n < width; n++)
        {
            ScalarStep(inA, outA, width, n, ref pa1, ref pa3, ref pa5, ref qa1, ref qa3, ref qa5);
            ScalarStep(inB, outB, width, n, ref pb1, ref pb3, ref pb5, ref qb1, ref qb3, ref qb5);
        }
    }

    /// <summary>
    /// Advances the vertical recurrence of <paramref name="vectors"/> × <see cref="Lanes"/> columns through steps
    /// <paramref name="n0"/>..<paramref name="n1"/>-1 (libjxl VerticalStrip; the first step is -<see cref="Radius"/>+1).
    /// <paramref name="input"/> holds the horizontal-pass rows, already offset to the strip's first column;
    /// <paramref name="zero"/> is a zero row of at least vectors × Lanes floats; the output of step n ≥ 0 goes to
    /// <paramref name="outBase"/> + n × <paramref name="outStride"/>; <paramref name="state"/> holds
    /// <see cref="StateFloatsPerVector"/> floats per vector and must start zeroed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Vertical(in RowRing input, float* zero, int height, int vectors, int n0, int n1, float* state, float* outBase, nint outStride)
    {
        const int N = Radius;
        for (int n = n0; n < n1; n++)
        {
            int top = n - N - 1, bottom = n + N - 1;
            float* t = top >= 0 ? input.Row(top) : zero;
            float* b = bottom < height ? input.Row(bottom) : zero;
            int s0 = n & 3, s1 = (n - 1) & 3, s2 = (n - 2) & 3;
            float* o = n >= 0 ? outBase + (n * outStride) : null;
            for (int v = 0; v < vectors; v++)
            {
                var sum = Vector256.Load(t + (v * Lanes)) + Vector256.Load(b + (v * Lanes));
                float* st = state + (v * StateFloatsPerVector);
                var y1 = Step(st, s0, s1, s2, sum, s_n2v1, s_negD1v1);
                var y3 = Step(st + (4 * Lanes), s0, s1, s2, sum, s_n2v3, s_negD1v3);
                var y5 = Step(st + (8 * Lanes), s0, s1, s2, sum, s_n2v5, s_negD1v5);
                if (o != null)
                {
                    (y1 + (y3 + y5)).Store(o + (v * Lanes));
                }
            }
        }
    }

    /// <summary>A ring of rows: row y lives at <c>Base + (y mod Rows) · Stride</c>.</summary>
    public readonly struct RowRing(float* @base, int rows, nint stride)
    {
        public float* Row(int y) => @base + ((y % rows) * stride);
    }

    // y_n = n2·sum − d1·y_{n−1} − y_{n−2}, computed as MulAdd(n2, sum, NegMulSub(d1, y_{n−1}, y_{n−2})).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Step(float* ring, int s0, int s1, int s2, Vector256<float> sum, Vector256<float> n2, Vector256<float> negD1)
    {
        var prev = Vector256.Load(ring + (s1 * Lanes));
        var prev2 = Vector256.Load(ring + (s2 * Lanes));
        var y = Vector256.FusedMultiplyAdd(n2, sum, Vector256.FusedMultiplyAdd(negD1, prev, -prev2));
        y.Store(ring + (s0 * Lanes));
        return y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScalarStep(float* input, float* output, int width, int n, ref float p1, ref float p3, ref float p5, ref float q1, ref float q3, ref float q5)
    {
        int left = n - Radius - 1, right = n + Radius - 1;
        float sum = (left >= 0 ? input[left] : 0f) + (right < width ? input[right] : 0f);
        var c = s_c;
        float o1 = MathF.FusedMultiplyAdd(c.MulPrev2[0], q1, sum * c.MulIn[0]);
        float o3 = MathF.FusedMultiplyAdd(c.MulPrev2[4], q3, sum * c.MulIn[4]);
        float o5 = MathF.FusedMultiplyAdd(c.MulPrev2[8], q5, sum * c.MulIn[8]);
        o1 = MathF.FusedMultiplyAdd(c.MulPrev[0], p1, o1);
        o3 = MathF.FusedMultiplyAdd(c.MulPrev[4], p3, o3);
        o5 = MathF.FusedMultiplyAdd(c.MulPrev[8], p5, o5);
        (q1, q3, q5, p1, p3, p5) = (p1, p3, p5, o1, o3, o5);
        if (n >= 0)
        {
            output[n] = o1 + (o3 + o5);
        }
    }

    /// <summary>Broadcasts lane <paramref name="i"/> of each 128-bit half across that half.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Lane(Vector256<float> v, [ConstantExpected(Min = 0, Max = 3)] int i) =>
        Vector256.Shuffle(v, Vector256.Create(i, i, i, i, i + 4, i + 4, i + 4, i + 4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Halves(float* a, float* b) => Vector256.Create(Vector128.Load(a), Vector128.Load(b));

    private static Vector256<float> Halves(float a, float b) => Vector256.Create(Vector128.Create(a), Vector128.Create(b));

    /// <summary>libjxl CreateRecursiveGaussian: coefficients derived in double, stored as float.</summary>
    private sealed class Coefficients
    {
        public float[] N2 { get; } = new float[3];

        public float[] D1 { get; } = new float[3];

        /// <summary>Per mode k, 4 lanes: n2, −d1·n2, (d1² − 1)·n2, (−d1³ + 2·d1)·n2.</summary>
        public float[] MulIn { get; } = new float[12];

        public float[] MulPrev { get; } = new float[12];

        public float[] MulPrev2 { get; } = new float[12];

        public static Coefficients Create(double sigma)
        {
            double radius = Math.Round((3.2795 * sigma) + 0.2546);
            if ((int)radius != Radius)
            {
                throw new InvalidOperationException("Unexpected recursive Gaussian radius.");
            }

            double piDiv2R = Math.PI / (2.0 * radius);
            double[] omega = [piDiv2R, 3.0 * piDiv2R, 5.0 * piDiv2R];
            double p1 = +1.0 / Math.Tan(0.5 * omega[0]), p3 = -1.0 / Math.Tan(0.5 * omega[1]), p5 = +1.0 / Math.Tan(0.5 * omega[2]);
            double r1 = +p1 * p1 / Math.Sin(omega[0]), r3 = -p3 * p3 / Math.Sin(omega[1]), r5 = +p5 * p5 / Math.Sin(omega[2]);
            double negHalfSigma2 = -0.5 * sigma * sigma, recipRadius = 1.0 / radius;
            double[] rho = new double[3];
            for (int i = 0; i < 3; i++)
            {
                rho[i] = Math.Exp(negHalfSigma2 * omega[i] * omega[i]) * recipRadius;
            }

            double d13 = (p1 * r3) - (r1 * p3), d35 = (p3 * r5) - (r3 * p5), d51 = (p5 * r1) - (r5 * p1);
            double recipD13 = 1.0 / d13;
            double zeta15 = d35 * recipD13, zeta35 = d51 * recipD13;
            double[] a = Invert3x3([p1, p3, p5, r1, r3, r5, zeta15, zeta35, 1]);
            double[] gamma = [1, (radius * radius) - (sigma * sigma), (zeta15 * rho[0]) + (zeta35 * rho[1]) + rho[2]];
            var c = new Coefficients();
            for (int i = 0; i < 3; i++)
            {
                double beta = (a[i * 3] * gamma[0]) + (a[(i * 3) + 1] * gamma[1]) + (a[(i * 3) + 2] * gamma[2]);
                double n2 = -beta * Math.Cos(omega[i] * (radius + 1.0));
                double d1 = -2.0 * Math.Cos(omega[i]);
                double d2 = d1 * d1;
                c.N2[i] = (float)n2;
                c.D1[i] = (float)d1;
                c.MulPrev[(4 * i) + 0] = (float)-d1;
                c.MulPrev[(4 * i) + 1] = (float)(d2 - 1.0);
                c.MulPrev[(4 * i) + 2] = (float)((-d2 * d1) + (2.0 * d1));
                c.MulPrev[(4 * i) + 3] = (float)((d2 * d2) - (3.0 * d2) + 1.0);
                c.MulPrev2[(4 * i) + 0] = -1.0f;
                c.MulPrev2[(4 * i) + 1] = (float)d1;
                c.MulPrev2[(4 * i) + 2] = (float)(-d2 + 1.0);
                c.MulPrev2[(4 * i) + 3] = (float)((d2 * d1) - (2.0 * d1));
                c.MulIn[(4 * i) + 0] = (float)n2;
                c.MulIn[(4 * i) + 1] = (float)(-d1 * n2);
                c.MulIn[(4 * i) + 2] = (float)((d2 * n2) - n2);
                c.MulIn[(4 * i) + 3] = (float)((-d2 * d1 * n2) + (2.0 * d1 * n2));
            }

            return c;
        }

        /// <summary>
        /// The 4 lanes of mode <paramref name="mode"/> shifted up by <paramref name="shift"/> lanes (zeros below), in both
        /// 128-bit halves.
        /// </summary>
        public Vector256<float> LaneVector(float[] table, int mode, int shift)
        {
            Span<float> v = stackalloc float[4];
            for (int lane = 0; lane < 4; lane++)
            {
                v[lane] = lane >= shift ? table[(4 * mode) + lane - shift] : 0f;
            }

            var half = Vector128.Create(v[0], v[1], v[2], v[3]);
            return Vector256.Create(half, half);
        }

        private static double[] Invert3x3(double[] m)
        {
            double[] t =
            [
                (m[4] * m[8]) - (m[5] * m[7]), (m[2] * m[7]) - (m[1] * m[8]), (m[1] * m[5]) - (m[2] * m[4]),
                (m[5] * m[6]) - (m[3] * m[8]), (m[0] * m[8]) - (m[2] * m[6]), (m[2] * m[3]) - (m[0] * m[5]),
                (m[3] * m[7]) - (m[4] * m[6]), (m[1] * m[6]) - (m[0] * m[7]), (m[0] * m[4]) - (m[1] * m[3]),
            ];
            double inv = 1.0 / ((m[0] * t[0]) + (m[1] * t[3]) + (m[2] * t[6]));
            return Array.ConvertAll(t, v => v * inv);
        }
    }
}
