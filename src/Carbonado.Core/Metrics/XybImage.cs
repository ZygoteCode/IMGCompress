using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Carbonado.Core.Metrics;

/// <summary>
/// Conversions into SSIMULACRA2's image representation — linear sRGB, then libjxl's XYB made positive
/// (X·14 + 0.42, Y + 0.01, B − Y + 0.55) — reproducing libjxl's float operations exactly (FMA order, the rational
/// polynomial sRGB decode, the bit-trick cube root), so every plane matches <c>ssimulacra2.exe</c> bit for bit.
/// </summary>
internal static unsafe class XybImage
{
    // lib/jxl/cms/opsin_params.h (float constants; the derived entries are float expressions).
    private static readonly float s_m00 = 0.30f, s_m01 = Sub(Sub(1.0f, 0.078f), 0.30f), s_m02 = 0.078f;
    private static readonly float s_m10 = 0.23f, s_m11 = Sub(Sub(1.0f, 0.078f), 0.23f), s_m12 = 0.078f;
    private static readonly float s_m20 = 0.24342268924547819f, s_m21 = 0.20476744424496821f;
    private static readonly float s_m22 = Sub(Sub(1.0f, 0.24342268924547819f), 0.20476744424496821f);
    private static readonly float s_bias = 0.0037930732552754493f;
    private static readonly float s_negCbrtBias = -MathF.Cbrt(s_bias);

    // TF_SRGB::DisplayFromEncoded (lib/jxl/cms/transfer_functions-inl.h).
    private const float P0 = 2.200248328e-04f, P1 = 1.043637593e-02f, P2 = 1.624820318e-01f, P3 = 7.961564959e-01f, P4 = 8.210152774e-01f;
    private const float Q0 = 2.631846970e-01f, Q1 = 1.076976492e+00f, Q2 = 4.987528350e-01f, Q3 = -5.512498495e-02f, Q4 = 6.521209011e-03f;
    private const float Threshold = 0.04045f;
    private static readonly float s_lowDivInv = 1.0f / 12.92f;

    /// <summary>libjxl's sRGB EOTF: x/12.92 below 0.04045, else a degree-4/4 rational polynomial; sign-symmetric.</summary>
    public static float SrgbToLinear(float encoded)
    {
        float x = MathF.Abs(encoded);
        float yp = MathF.FusedMultiplyAdd(MathF.FusedMultiplyAdd(MathF.FusedMultiplyAdd(MathF.FusedMultiplyAdd(P4, x, P3), x, P2), x, P1), x, P0);
        float yq = MathF.FusedMultiplyAdd(MathF.FusedMultiplyAdd(MathF.FusedMultiplyAdd(MathF.FusedMultiplyAdd(Q4, x, Q3), x, Q2), x, Q1), x, Q0);
        float magnitude = x > Threshold ? yp / yq : x * s_lowDivInv;
        return MathF.CopySign(magnitude, encoded);
    }

    /// <summary>Vector form of <see cref="SrgbToLinear(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> SrgbToLinear(Vector256<float> encoded)
    {
        var sign = Vector256.Create(-0.0f);
        var x = Vector256.AndNot(encoded, sign);
        var yp = Vector256.FusedMultiplyAdd(Vector256.FusedMultiplyAdd(Vector256.FusedMultiplyAdd(Vector256.FusedMultiplyAdd(
            Vector256.Create(P4), x, Vector256.Create(P3)), x, Vector256.Create(P2)), x, Vector256.Create(P1)), x, Vector256.Create(P0));
        var yq = Vector256.FusedMultiplyAdd(Vector256.FusedMultiplyAdd(Vector256.FusedMultiplyAdd(Vector256.FusedMultiplyAdd(
            Vector256.Create(Q4), x, Vector256.Create(Q3)), x, Vector256.Create(Q2)), x, Vector256.Create(Q1)), x, Vector256.Create(Q0));
        var magnitude = Vector256.ConditionalSelect(Vector256.GreaterThan(x, Vector256.Create(Threshold)), yp / yq, x * Vector256.Create(s_lowDivInv));
        return Vector256.AndNot(magnitude, sign) | (encoded & sign);
    }

    /// <summary>Converts <paramref name="count"/> linear sRGB pixels to positive XYB planes (libjxl LinearRGBToXYB + MakePositiveXYB).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void FromLinear(float* r, float* g, float* b, float* outX, float* outY, float* outB, int count)
    {
        int i = 0;
        for (; i <= count - 8; i += 8)
        {
            var vr = Vector256.Load(r + i);
            var vg = Vector256.Load(g + i);
            var vb = Vector256.Load(b + i);
            var bias = Vector256.Create(s_bias);
            var m0 = Vector256.FusedMultiplyAdd(Vector256.Create(s_m00), vr, Vector256.FusedMultiplyAdd(Vector256.Create(s_m01), vg, Vector256.FusedMultiplyAdd(Vector256.Create(s_m02), vb, bias)));
            var m1 = Vector256.FusedMultiplyAdd(Vector256.Create(s_m10), vr, Vector256.FusedMultiplyAdd(Vector256.Create(s_m11), vg, Vector256.FusedMultiplyAdd(Vector256.Create(s_m12), vb, bias)));
            var m2 = Vector256.FusedMultiplyAdd(Vector256.Create(s_m20), vr, Vector256.FusedMultiplyAdd(Vector256.Create(s_m21), vg, Vector256.FusedMultiplyAdd(Vector256.Create(s_m22), vb, bias)));
            var add = Vector256.Create(s_negCbrtBias);
            m0 = CubeRootAndAdd(ZeroIfNegative(m0), add);
            m1 = CubeRootAndAdd(ZeroIfNegative(m1), add);
            m2 = CubeRootAndAdd(ZeroIfNegative(m2), add);
            var half = Vector256.Create(0.5f);
            var x = half * (m0 - m1);
            var y = half * (m0 + m1);
            ((m2 - y) + Vector256.Create(0.55f)).Store(outB + i);
            ((x * Vector256.Create(14f)) + Vector256.Create(0.42f)).Store(outX + i);
            (y + Vector256.Create(0.01f)).Store(outY + i);
        }

        for (; i < count; i++)
        {
            float m0 = MathF.FusedMultiplyAdd(s_m00, r[i], MathF.FusedMultiplyAdd(s_m01, g[i], MathF.FusedMultiplyAdd(s_m02, b[i], s_bias)));
            float m1 = MathF.FusedMultiplyAdd(s_m10, r[i], MathF.FusedMultiplyAdd(s_m11, g[i], MathF.FusedMultiplyAdd(s_m12, b[i], s_bias)));
            float m2 = MathF.FusedMultiplyAdd(s_m20, r[i], MathF.FusedMultiplyAdd(s_m21, g[i], MathF.FusedMultiplyAdd(s_m22, b[i], s_bias)));
            m0 = CubeRootAndAdd(ZeroIfNegative(m0), s_negCbrtBias);
            m1 = CubeRootAndAdd(ZeroIfNegative(m1), s_negCbrtBias);
            m2 = CubeRootAndAdd(ZeroIfNegative(m2), s_negCbrtBias);
            float x = 0.5f * (m0 - m1), y = 0.5f * (m0 + m1);
            outB[i] = (m2 - y) + 0.55f;
            outX[i] = (x * 14f) + 0.42f;
            outY[i] = y + 0.01f;
        }
    }

    /// <summary>
    /// One output row of the 2×2 box downsampling (libjxl ssimulacra2 Downsample): ((a + b) + c) + d, times 0.25,
    /// with the last column and row replicated. <paramref name="row1"/> is the second input row (or the first again).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void DownsampleRow(float* row0, float* row1, int width, float* output)
    {
        int outWidth = (width + 1) / 2;
        for (int ox = 0; ox < outWidth; ox++)
        {
            int x0 = 2 * ox, x1 = Math.Min(x0 + 1, width - 1);
            output[ox] = (((row0[x0] + row0[x1]) + row1[x0]) + row1[x1]) * 0.25f;
        }
    }

    // libjxl CubeRootAndAdd (lib/jxl/base/fast_math-inl.h): x^(-1/3) by an exponent bit trick and Newton steps, then
    // r²·x + add. Inputs are never negative.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> CubeRootAndAdd(Vector256<float> x, Vector256<float> add)
    {
        var k13 = Vector256.Create(1.0f / 3);
        var k43 = Vector256.Create(4.0f / 3);
        var xa3 = k13 * x;
        var m1 = x.AsInt32();
        var m2 = Vector256.Create(0x54800000) - (Vector256.ShiftRightArithmetic(m1, 23) * Vector256.Create(0x002AAAAA));
        var r = Vector256.AndNot(m2, Vector256.Equals(m1, Vector256<int>.Zero)).AsSingle();
        for (int it = 0; it < 3; it++)
        {
            var r2 = r * r;
            r = Vector256.FusedMultiplyAdd(-xa3, r2 * r2, k43 * r);
        }

        var rr = r * r;
        r = Vector256.FusedMultiplyAdd(k13, Vector256.FusedMultiplyAdd(-x, rr * rr, r), r);
        rr = r * r;
        return Vector256.FusedMultiplyAdd(rr, x, add);
    }

    private static float CubeRootAndAdd(float x, float add)
    {
        int m1 = BitConverter.SingleToInt32Bits(x);
        float r = BitConverter.Int32BitsToSingle(m1 == 0 ? 0 : 0x54800000 - ((m1 >> 23) * 0x002AAAAA));
        float xa3 = (1.0f / 3) * x;
        for (int it = 0; it < 3; it++)
        {
            float r2 = r * r;
            r = MathF.FusedMultiplyAdd(-xa3, r2 * r2, (4.0f / 3) * r);
        }

        float rr = r * r;
        r = MathF.FusedMultiplyAdd(1.0f / 3, MathF.FusedMultiplyAdd(-x, rr * rr, r), r);
        rr = r * r;
        return MathF.FusedMultiplyAdd(rr, x, add);
    }

    // Highway ZeroIfNegative: zero where the sign bit is set (so -0 becomes +0, which the cube root needs).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> ZeroIfNegative(Vector256<float> v) => Vector256.AndNot(v, Vector256.ShiftRightArithmetic(v.AsInt32(), 31).AsSingle());

    private static float ZeroIfNegative(float v) => BitConverter.SingleToInt32Bits(v) < 0 ? 0f : v;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float Sub(float a, float b) => a - b; // keeps the derived constants in float arithmetic
}
