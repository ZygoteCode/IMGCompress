using System.Numerics;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Tests.Imaging;

public class ColorSpaceTests
{
    [Fact]
    public void Srgb_LinearRoundTrip()
    {
        for (int i = 0; i < 256; i++)
        {
            float lin = ColorSpace.SrgbToLinear(i / 255f);
            Assert.Equal(lin, ColorSpace.SrgbToLinearLut8[i], 1e-7f);
            Assert.Equal(i, (int)MathF.Round(ColorSpace.LinearToSrgb(lin) * 255));
        }

        var rng = new Random(5);
        for (int i = 0; i < 10_000; i++)
        {
            float x = (float)rng.NextDouble();
            Assert.Equal(x, ColorSpace.SrgbToLinear(ColorSpace.LinearToSrgb(x)), 1e-6f);
        }

        Assert.Equal(256, ColorSpace.SrgbToLinearLut8.Length);
        Assert.Equal(0.21404114f, ColorSpace.SrgbToLinear(0.5f), 1e-6f);
        Assert.Equal(0.7353570f, ColorSpace.LinearToSrgb(0.5f), 1e-6f);
        Assert.Equal(1f / 12.92f * 0.02f, ColorSpace.SrgbToLinear(0.02f), 1e-9f);
        Assert.Equal(-ColorSpace.SrgbToLinear(0.3f), ColorSpace.SrgbToLinear(-0.3f));
    }

    [Fact]
    public void IccLinearizer_DisplayP3_DiffersFromSrgb()
    {
        var p3 = ColorSpace.TryCreateLinearizer(IccTestProfiles.DisplayP3);
        Assert.NotNull(p3);

        // P3 red lies outside sRGB: linear sRGB ≈ (1.2249, -0.0421, -0.0196).
        Vector3 red = p3(new Vector3(1, 0, 0));
        Assert.Equal(1.2249f, red.X, 3e-3f);
        Assert.Equal(-0.0421f, red.Y, 3e-3f);
        Assert.Equal(-0.0196f, red.Z, 3e-3f);

        // Same white point and tone curve: neutrals map to the sRGB linear value.
        foreach (float v in new[] { 0f, 0.25f, 0.5f, 1f })
        {
            Vector3 gray = p3(new Vector3(v, v, v));
            float expected = ColorSpace.SrgbToLinear(v);
            Assert.Equal(expected, gray.X, 2e-3f);
            Assert.Equal(expected, gray.Y, 2e-3f);
            Assert.Equal(expected, gray.Z, 2e-3f);
        }

        // An sRGB profile, a missing profile and garbage all mean "treat as sRGB".
        Assert.Null(ColorSpace.TryCreateLinearizer(IccTestProfiles.Srgb));
        Assert.Null(ColorSpace.TryCreateLinearizer(null));
        Assert.Null(ColorSpace.TryCreateLinearizer(new byte[300]));
        Assert.Null(ColorSpace.TryCreateLinearizer(IccTestProfiles.DisplayP3[..140]));
    }

    [Fact]
    public void IccLinearizer_ReadsCurvGammaTablesAndGrayProfiles()
    {
        byte[] gamma22 = IccTestProfiles.Curv((ushort)Math.Round(2.2 * 256));
        var gray = ColorSpace.TryCreateLinearizer(IccTestProfiles.Gray(gamma22));
        Assert.NotNull(gray);
        Vector3 g = gray(new Vector3(0.5f, 0.25f, 1f));
        Assert.Equal(MathF.Pow(0.5f, 2.2f), g.X, 2e-3f);
        Assert.Equal(MathF.Pow(0.25f, 2.2f), g.Y, 2e-3f);
        Assert.Equal(1f, g.Z, 2e-3f);

        // A sampled linear table with sRGB colorants is the identity on linear values.
        ushort[] table = Enumerable.Range(0, 1024).Select(i => (ushort)Math.Round(i * 65535.0 / 1023)).ToArray();
        byte[] linearSrgb = IccTestProfiles.Rgb([0.436066, 0.222488, 0.013916], [0.385147, 0.716873, 0.097076], [0.143066, 0.060608, 0.714096], IccTestProfiles.Curv(table));
        var lin = ColorSpace.TryCreateLinearizer(linearSrgb);
        Assert.NotNull(lin);
        Vector3 c = lin(new Vector3(0.2f, 0.5f, 0.8f));
        Assert.Equal(0.2f, c.X, 2e-3f);
        Assert.Equal(0.5f, c.Y, 2e-3f);
        Assert.Equal(0.8f, c.Z, 2e-3f);
    }
}
