using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Metrics;

/// <summary>Rows of linear sRGB (three float planes) feeding one SSIMULACRA2 scale.</summary>
internal unsafe interface ILinearSource
{
    int Width { get; }

    int Height { get; }

    /// <summary>Writes <see cref="Width"/> linear R, G and B values of row <paramref name="y"/>.</summary>
    void Read(int y, float* r, float* g, float* b);
}

/// <summary>
/// Decodes <see cref="PixelBuffer"/> rows the way <c>ssimulacra2.exe</c> reads a PNG: samples × (1/255f) or
/// (1/65535f), optionally blended on the encoded values as <c>a·c + (1 − a)·bg</c> (float, no FMA), then linearised
/// with libjxl's sRGB decode or, for a non-sRGB ICC profile, its curves and matrix. 8-bit paths use exact tables.
/// </summary>
internal sealed unsafe class PixelLinearSource(PixelBuffer pixels, LinearDecoder decoder) : ILinearSource
{
    public int Width => pixels.Width;

    public int Height => pixels.Height;

    public void Read(int y, float* r, float* g, float* b) => decoder.Decode(pixels, y, r, g, b);
}

/// <summary>
/// Sample → linear sRGB conversion for one color space and one background (or none). Thread-safe after construction;
/// the sRGB instances are shared.
/// </summary>
internal sealed unsafe class LinearDecoder
{
    private const float Scale8 = 1.0f / 255;
    private const float Scale16 = 1.0f / 65535;

    private static readonly Lazy<LinearDecoder> s_srgb = new(() => new LinearDecoder(null, null));
    private static readonly Lazy<LinearDecoder> s_srgbOnDark = new(() => new LinearDecoder(null, Ssimulacra2.DarkBackground));
    private static readonly Lazy<LinearDecoder> s_srgbOnBright = new(() => new LinearDecoder(null, Ssimulacra2.BrightBackground));

    private readonly ColorSpace.IccLinearizer? _icc;
    private readonly float? _background;
    private readonly float[][] _tables8; // per channel: [code] when not blending, else [alpha * 256 + code]

    private LinearDecoder(ColorSpace.IccLinearizer? icc, float? background)
    {
        _icc = icc;
        _background = background;
        _tables8 = icc is null
            ? Enumerable.Repeat(BuildTable8(c => XybImage.SrgbToLinear(c), background), 3).ToArray()
            : [BuildTable8(c => icc.TransferCurve(0, c), background), BuildTable8(c => icc.TransferCurve(1, c), background), BuildTable8(c => icc.TransferCurve(2, c), background)];
    }

    /// <summary>A decoder for the given profile (null = sRGB) and blending background (null = no blending).</summary>
    public static LinearDecoder Get(ColorSpace.IccLinearizer? icc, float? background) => (icc, background) switch
    {
        (null, null) => s_srgb.Value,
        (null, Ssimulacra2.DarkBackground) => s_srgbOnDark.Value,
        (null, Ssimulacra2.BrightBackground) => s_srgbOnBright.Value,
        _ => new LinearDecoder(icc, background),
    };

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Decode(PixelBuffer px, int y, float* r, float* g, float* b)
    {
        if (px.Depth == SampleDepth.Eight)
        {
            Decode8(px.Row32(y), r, g, b);
        }
        else
        {
            Decode16(px.Row64(y), r, g, b);
        }

        if (_icc is { } icc)
        {
            for (int x = 0; x < px.Width; x++)
            {
                Vector3 v = icc.Apply(r[x], g[x], b[x]);
                (r[x], g[x], b[x]) = (v.X, v.Y, v.Z);
            }
        }
    }

    private static float[] BuildTable8(Func<float, float> curve, float? background)
    {
        if (background is not float bg)
        {
            var plain = new float[256];
            for (int c = 0; c < 256; c++)
            {
                plain[c] = curve(c * Scale8);
            }

            return plain;
        }

        var table = new float[256 * 256];
        for (int a = 0; a < 256; a++)
        {
            float alpha = a * Scale8;
            for (int c = 0; c < 256; c++)
            {
                table[(a * 256) + c] = curve(Blend(c * Scale8, alpha, bg));
            }
        }

        return table;
    }

    // ssimulacra2.cc AlphaBlend: a * c + (1 - a) * bg in float, two separately rounded products.
    private static float Blend(float encoded, float alpha, float bg) => (alpha * encoded) + ((1f - alpha) * bg);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Decode8(ReadOnlySpan<Rgba32> row, float* r, float* g, float* b)
    {
        float[] tr = _tables8[0], tg = _tables8[1], tb = _tables8[2];
        if (_background is null)
        {
            for (int x = 0; x < row.Length; x++)
            {
                Rgba32 p = row[x];
                (r[x], g[x], b[x]) = (tr[p.R], tg[p.G], tb[p.B]);
            }

            return;
        }

        for (int x = 0; x < row.Length; x++)
        {
            Rgba32 p = row[x];
            int a = p.A << 8;
            (r[x], g[x], b[x]) = (tr[a | p.R], tg[a | p.G], tb[a | p.B]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Decode16(ReadOnlySpan<Rgba64> row, float* r, float* g, float* b)
    {
        for (int x = 0; x < row.Length; x++)
        {
            Rgba64 p = row[x];
            float er = p.R * Scale16, eg = p.G * Scale16, eb = p.B * Scale16;
            if (_background is float bg)
            {
                float a = p.A * Scale16;
                (er, eg, eb) = (Blend(er, a, bg), Blend(eg, a, bg), Blend(eb, a, bg));
            }

            (r[x], g[x], b[x]) = (er, eg, eb);
        }

        if (_icc is { } icc)
        {
            for (int x = 0; x < row.Length; x++)
            {
                (r[x], g[x], b[x]) = (icc.TransferCurve(0, r[x]), icc.TransferCurve(1, g[x]), icc.TransferCurve(2, b[x]));
            }

            return;
        }

        SrgbToLinearInPlace(r, row.Length);
        SrgbToLinearInPlace(g, row.Length);
        SrgbToLinearInPlace(b, row.Length);
    }

    private static void SrgbToLinearInPlace(float* values, int count)
    {
        int x = 0;
        for (; x <= count - 8; x += 8)
        {
            XybImage.SrgbToLinear(Vector256.Load(values + x)).Store(values + x);
        }

        for (; x < count; x++)
        {
            values[x] = XybImage.SrgbToLinear(values[x]);
        }
    }
}
