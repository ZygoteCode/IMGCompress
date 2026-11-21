using System.Buffers.Binary;
using System.Numerics;

namespace Carbonado.Core.Imaging;

/// <summary>
/// sRGB transfer functions and linearization of ICC matrix/TRC profiles to linear sRGB (D65 white).
/// </summary>
public static class ColorSpace
{
    private static readonly float[] s_srgbToLinear8 = BuildSrgbLut8();

    /// <summary>Linear-light value of every 8-bit sRGB code: entry <c>i</c> is <c>SrgbToLinear(i / 255f)</c>.</summary>
    public static ReadOnlySpan<float> SrgbToLinearLut8 => s_srgbToLinear8;

    /// <summary>sRGB EOTF (IEC 61966-2-1): encoded 0..1 to linear light 0..1. Negative inputs are mirrored.</summary>
    public static float SrgbToLinear(float encoded) => (float)SrgbToLinearPrecise(encoded);

    /// <summary>Inverse of <see cref="SrgbToLinear"/>: linear light 0..1 to encoded sRGB 0..1. Negative inputs are mirrored.</summary>
    public static float LinearToSrgb(float linear) => (float)LinearToSrgbPrecise(linear);

    /// <summary>
    /// Builds a converter from encoded RGB (0..1, the profile's own primaries and curves) to linear sRGB with a D65
    /// white, for RGB or gray matrix/TRC ICC profiles (TRC curves of type 'curv' or 'para' 0..4; the D50 PCS is adapted
    /// to D65 with Bradford). Returns null — meaning "treat the pixels as sRGB" — when <paramref name="icc"/> is null,
    /// is not a usable matrix/TRC profile (e.g. LUT-based, CMYK, Lab PCS, malformed) or is equivalent to sRGB.
    /// Output values are not clamped, so colors outside the sRGB gamut come out below 0 or above 1.
    /// </summary>
    public static Func<Vector3, Vector3>? TryCreateLinearizer(byte[]? icc) => IccLinearizer.TryCreate(icc) is { } l ? l.ToLinear : null;

    internal static double SrgbToLinearPrecise(double encoded)
    {
        double a = Math.Abs(encoded);
        double r = a <= 0.04045 ? a / 12.92 : Math.Pow((a + 0.055) / 1.055, 2.4);
        return encoded < 0 ? -r : r;
    }

    internal static double LinearToSrgbPrecise(double linear)
    {
        double a = Math.Abs(linear);
        double r = a <= 0.0031308 ? a * 12.92 : (1.055 * Math.Pow(a, 1 / 2.4)) - 0.055;
        return linear < 0 ? -r : r;
    }

    private static float[] BuildSrgbLut8()
    {
        var lut = new float[256];
        for (int i = 0; i < lut.Length; i++)
        {
            lut[i] = (float)SrgbToLinearPrecise(i / 255.0);
        }

        return lut;
    }

    /// <summary>A parsed RGB or gray matrix/TRC ICC profile that converts encoded values to linear sRGB.</summary>
    internal sealed class IccLinearizer
    {
        private const uint SigAcsp = 0x61637370; // 'acsp'
        private const uint SigRgb = 0x52474220; // 'RGB '
        private const uint SigGray = 0x47524159; // 'GRAY'
        private const uint SigXyz = 0x58595A20; // 'XYZ '
        private const uint SigCurv = 0x63757276; // 'curv'
        private const uint SigPara = 0x70617261; // 'para'
        private const uint TagRXyz = 0x7258595A; // 'rXYZ'
        private const uint TagGXyz = 0x6758595A; // 'gXYZ'
        private const uint TagBXyz = 0x6258595A; // 'bXYZ'
        private const uint TagRTrc = 0x72545243; // 'rTRC'
        private const uint TagGTrc = 0x67545243; // 'gTRC'
        private const uint TagBTrc = 0x62545243; // 'bTRC'
        private const uint TagKTrc = 0x6B545243; // 'kTRC'

        // ICC PCS illuminant D50 (s15Fixed16 values of the ICC specification) and D65 from xy (0.3127, 0.3290).
        private static readonly double[] s_d50 = [0.9642028809, 1.0, 0.8249053955];
        private static readonly double[] s_d65 = [0.3127 / 0.3290, 1.0, (1 - 0.3127 - 0.3290) / 0.3290];

        private static readonly double[] s_bradford =
        [
            0.8951, 0.2664, -0.1614,
            -0.7502, 1.7135, 0.0367,
            0.0389, -0.0685, 1.0296,
        ];

        /// <summary>sRGB colorants adapted to the D50 PCS: what a well-formed sRGB ICC profile contains.</summary>
        private static readonly double[] s_srgbToXyzD50 = Multiply(Adaptation(s_d65, s_d50), RgbToXyz(0.64, 0.33, 0.30, 0.60, 0.15, 0.06, s_d65));
        private static readonly double[] s_xyzD50ToSrgb = Invert(s_srgbToXyzD50)!;

        private readonly ToneCurve[] _curves;
        private readonly float[] _matrix; // row-major profile linear RGB -> linear sRGB; identity for gray (kTRC) profiles

        private IccLinearizer(ToneCurve[] curves, float[] matrix)
        {
            _curves = curves;
            _matrix = matrix;
        }

        /// <summary>Parses <paramref name="icc"/>; null when it is missing, unusable or equivalent to sRGB.</summary>
        public static IccLinearizer? TryCreate(byte[]? icc)
        {
            if (icc is null || icc.Length < 132)
            {
                return null;
            }

            ReadOnlySpan<byte> p = icc;
            if (Be32(p, 36) != SigAcsp || Be32(p, 20) != SigXyz)
            {
                return null;
            }

            uint space = Be32(p, 16);
            uint tagCount = Be32(p, 128);
            if (tagCount > (uint)((p.Length - 132) / 12))
            {
                return null;
            }

            if (space == SigGray)
            {
                ToneCurve? k = ReadCurve(FindTag(p, tagCount, TagKTrc));
                return k is null || IsSrgbCurve(k) ? null : new IccLinearizer([k, k, k], [1, 0, 0, 0, 1, 0, 0, 0, 1]);
            }

            if (space != SigRgb)
            {
                return null;
            }

            double[]? r = ReadXyz(FindTag(p, tagCount, TagRXyz));
            double[]? g = ReadXyz(FindTag(p, tagCount, TagGXyz));
            double[]? b = ReadXyz(FindTag(p, tagCount, TagBXyz));
            ToneCurve? rc = ReadCurve(FindTag(p, tagCount, TagRTrc));
            ToneCurve? gc = ReadCurve(FindTag(p, tagCount, TagGTrc));
            ToneCurve? bc = ReadCurve(FindTag(p, tagCount, TagBTrc));
            if (r is null || g is null || b is null || rc is null || gc is null || bc is null)
            {
                return null;
            }

            double[] colorants = [r[0], g[0], b[0], r[1], g[1], b[1], r[2], g[2], b[2]];
            if (Invert(colorants) is null || IsSrgbLike(colorants, rc, gc, bc))
            {
                return null;
            }

            double[] m = Multiply(s_xyzD50ToSrgb, colorants);
            return new IccLinearizer([rc, gc, bc], Array.ConvertAll(m, v => (float)v));
        }

        /// <summary>Converts encoded RGB (0..1) to linear sRGB.</summary>
        public Vector3 ToLinear(Vector3 encoded)
        {
            float r = TransferCurve(0, encoded.X), g = TransferCurve(1, encoded.Y), b = TransferCurve(2, encoded.Z);
            return Apply(r, g, b);
        }

        /// <summary>The transfer curve of <paramref name="channel"/> (0..2): encoded value to the profile's linear value.</summary>
        public float TransferCurve(int channel, float encoded) => (float)_curves[channel].Eval(encoded);

        /// <summary>Applies the 3×3 matrix (profile linear RGB to linear sRGB).</summary>
        public Vector3 Apply(float r, float g, float b)
        {
            float[] m = _matrix;
            return new Vector3(
                (m[0] * r) + (m[1] * g) + (m[2] * b),
                (m[3] * r) + (m[4] * g) + (m[5] * b),
                (m[6] * r) + (m[7] * g) + (m[8] * b));
        }

        /// <summary>Colorants within 1.5e-3 of sRGB's and every curve within 1/512 (encoded) of the sRGB curve.</summary>
        private static bool IsSrgbLike(double[] colorants, ToneCurve r, ToneCurve g, ToneCurve b)
        {
            for (int i = 0; i < 9; i++)
            {
                if (Math.Abs(colorants[i] - s_srgbToXyzD50[i]) > 1.5e-3)
                {
                    return false;
                }
            }

            return IsSrgbCurve(r) && IsSrgbCurve(g) && IsSrgbCurve(b);
        }

        private static bool IsSrgbCurve(ToneCurve curve)
        {
            for (int i = 0; i <= 256; i++)
            {
                double x = i / 256.0;
                if (Math.Abs(LinearToSrgbPrecise(curve.Eval(x)) - x) > 1.0 / 512)
                {
                    return false;
                }
            }

            return true;
        }

        private static uint Be32(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadUInt32BigEndian(p.Slice(offset, 4));

        private static double S15Fixed16(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadInt32BigEndian(p.Slice(offset, 4)) / 65536.0;

        /// <summary>Returns the tag's bytes, or an empty span when the tag is absent or out of bounds.</summary>
        private static ReadOnlySpan<byte> FindTag(ReadOnlySpan<byte> p, uint tagCount, uint signature)
        {
            for (int i = 0; i < tagCount; i++)
            {
                int e = 132 + (i * 12);
                if (Be32(p, e) != signature)
                {
                    continue;
                }

                uint offset = Be32(p, e + 4), size = Be32(p, e + 8);
                return offset < p.Length && size <= p.Length - offset ? p.Slice((int)offset, (int)size) : default;
            }

            return default;
        }

        private static double[]? ReadXyz(ReadOnlySpan<byte> tag) =>
            tag.Length >= 20 && Be32(tag, 0) == SigXyz ? [S15Fixed16(tag, 8), S15Fixed16(tag, 12), S15Fixed16(tag, 16)] : null;

        private static ToneCurve? ReadCurve(ReadOnlySpan<byte> tag)
        {
            if (tag.Length < 12)
            {
                return null;
            }

            uint type = Be32(tag, 0);
            if (type == SigCurv)
            {
                uint count = Be32(tag, 8);
                if (count > (uint)((tag.Length - 12) / 2))
                {
                    return null;
                }

                if (count == 0)
                {
                    return ToneCurve.Parametric(1, 1, 0, 0, 0, 0, 0);
                }

                if (count == 1)
                {
                    return ToneCurve.Parametric(BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(12, 2)) / 256.0, 1, 0, 0, 0, 0, 0);
                }

                var table = new double[count];
                for (int i = 0; i < table.Length; i++)
                {
                    table[i] = BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(12 + (2 * i), 2)) / 65535.0;
                }

                return ToneCurve.Table(table);
            }

            if (type != SigPara)
            {
                return null;
            }

            int function = BinaryPrimitives.ReadUInt16BigEndian(tag.Slice(8, 2));
            int parameters = function switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
            if (parameters < 0 || tag.Length < 12 + (4 * parameters))
            {
                return null;
            }

            Span<double> v = stackalloc double[7];
            for (int i = 0; i < parameters; i++)
            {
                v[i] = S15Fixed16(tag, 12 + (4 * i));
            }

            // Normalised to Y = X >= d ? (aX + b)^g + e : cX + f (ICC.1 parametricCurveType).
            return function switch
            {
                0 => ToneCurve.Parametric(v[0], 1, 0, 0, 0, 0, 0),
                1 when v[1] != 0 => ToneCurve.Parametric(v[0], v[1], v[2], 0, -v[2] / v[1], 0, 0),
                2 when v[1] != 0 => ToneCurve.Parametric(v[0], v[1], v[2], 0, -v[2] / v[1], v[3], v[3]),
                3 => ToneCurve.Parametric(v[0], v[1], v[2], v[3], v[4], 0, 0),
                4 => ToneCurve.Parametric(v[0], v[1], v[2], v[3], v[4], v[5], v[6]),
                _ => null,
            };
        }

        private static double[] RgbToXyz(double xr, double yr, double xg, double yg, double xb, double yb, double[] white)
        {
            double[] p =
            [
                xr / yr, xg / yg, xb / yb,
                1, 1, 1,
                (1 - xr - yr) / yr, (1 - xg - yg) / yg, (1 - xb - yb) / yb,
            ];
            double[] s = MultiplyVector(Invert(p)!, white);
            return [p[0] * s[0], p[1] * s[1], p[2] * s[2], p[3] * s[0], p[4] * s[1], p[5] * s[2], p[6] * s[0], p[7] * s[1], p[8] * s[2]];
        }

        /// <summary>Bradford chromatic adaptation from white <paramref name="from"/> to white <paramref name="to"/>.</summary>
        private static double[] Adaptation(double[] from, double[] to)
        {
            double[] src = MultiplyVector(s_bradford, from), dst = MultiplyVector(s_bradford, to);
            double[] scale = [dst[0] / src[0], 0, 0, 0, dst[1] / src[1], 0, 0, 0, dst[2] / src[2]];
            return Multiply(Invert(s_bradford)!, Multiply(scale, s_bradford));
        }

        private static double[] Multiply(double[] a, double[] b)
        {
            var m = new double[9];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    m[(i * 3) + j] = (a[i * 3] * b[j]) + (a[(i * 3) + 1] * b[3 + j]) + (a[(i * 3) + 2] * b[6 + j]);
                }
            }

            return m;
        }

        private static double[] MultiplyVector(double[] a, double[] v) =>
        [
            (a[0] * v[0]) + (a[1] * v[1]) + (a[2] * v[2]),
            (a[3] * v[0]) + (a[4] * v[1]) + (a[5] * v[2]),
            (a[6] * v[0]) + (a[7] * v[1]) + (a[8] * v[2]),
        ];

        private static double[]? Invert(double[] m)
        {
            double c0 = (m[4] * m[8]) - (m[5] * m[7]);
            double c1 = (m[5] * m[6]) - (m[3] * m[8]);
            double c2 = (m[3] * m[7]) - (m[4] * m[6]);
            double det = (m[0] * c0) + (m[1] * c1) + (m[2] * c2);
            if (!double.IsFinite(det) || Math.Abs(det) < 1e-9)
            {
                return null;
            }

            double k = 1 / det;
            return
            [
                c0 * k, ((m[2] * m[7]) - (m[1] * m[8])) * k, ((m[1] * m[5]) - (m[2] * m[4])) * k,
                c1 * k, ((m[0] * m[8]) - (m[2] * m[6])) * k, ((m[2] * m[3]) - (m[0] * m[5])) * k,
                c2 * k, ((m[1] * m[6]) - (m[0] * m[7])) * k, ((m[0] * m[4]) - (m[1] * m[3])) * k,
            ];
        }
    }

    /// <summary>An ICC tone curve: a sampled table (linear interpolation) or a parametric function.</summary>
    private sealed class ToneCurve
    {
        private readonly double[]? _table;
        private readonly double _g, _a, _b, _c, _d, _e, _f;

        private ToneCurve(double[]? table, double g, double a, double b, double c, double d, double e, double f)
        {
            _table = table;
            (_g, _a, _b, _c, _d, _e, _f) = (g, a, b, c, d, e, f);
        }

        public static ToneCurve Table(double[] table) => new(table, 0, 0, 0, 0, 0, 0, 0);

        public static ToneCurve Parametric(double g, double a, double b, double c, double d, double e, double f) => new(null, g, a, b, c, d, e, f);

        /// <summary>Evaluates the curve; tables clamp to 0..1, functions are mirrored for negative inputs.</summary>
        public double Eval(double x)
        {
            if (_table is { } t)
            {
                double pos = Math.Clamp(x, 0, 1) * (t.Length - 1);
                int i = Math.Min((int)pos, t.Length - 2);
                return t[i] + ((t[i + 1] - t[i]) * (pos - i));
            }

            double ax = Math.Abs(x);
            double y = ax >= _d ? Math.Pow(Math.Max(0, (_a * ax) + _b), _g) + _e : (_c * ax) + _f;
            return x < 0 ? -y : y;
        }
    }
}
