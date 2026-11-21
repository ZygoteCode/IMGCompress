using System.Text;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Codecs;

public class PnmCodecTests
{
    [Fact]
    public void Pnm_RoundTrip_8And16bit()
    {
        foreach (var make in new Func<PixelBuffer>[] { () => TestImages.WithAlpha(37, 21), () => TestImages.PhotoLike(37, 21), () => TestImages.Deep16() })
        {
            using var px = make();
            var pam = new MemoryStream();
            PnmCodec.WritePam(pam, px);
            Assert.StartsWith("P7\n", Encoding.ASCII.GetString(pam.ToArray(), 0, 3));
            using (var decoded = PnmCodec.Decode(new MemoryStream(pam.ToArray())))
            {
                PixelAssert.Identical(px, decoded.Pixels);
                Assert.Equal(ImageFormat.Pnm, decoded.SourceFormat);
                Assert.Equal(px.Depth == SampleDepth.Sixteen ? 16 : 8, decoded.SignificantBits);
            }

            var ppm = new MemoryStream();
            PnmCodec.WritePpm(ppm, px);
            Assert.StartsWith("P6\n", Encoding.ASCII.GetString(ppm.ToArray(), 0, 3));
            using var opaque = PnmCodec.Decode(new MemoryStream(ppm.ToArray()));
            using var expected = Opaque(px);
            PixelAssert.Identical(expected, opaque.Pixels);
        }
    }

    [Fact]
    public void Pam_DropsOpaqueAlphaChannel()
    {
        using var px = TestImages.PhotoLike(9, 5);
        var pam = new MemoryStream();
        PnmCodec.WritePam(pam, px);
        string header = Encoding.ASCII.GetString(pam.ToArray(), 0, 60);
        Assert.Contains("DEPTH 3\n", header);
        Assert.Contains("TUPLTYPE RGB\n", header);
        Assert.Equal(HeaderLength(pam.ToArray()) + (9 * 5 * 3), pam.Length);

        using var gray = TestImages.Gray(9, 5);
        var grayPam = new MemoryStream();
        PnmCodec.WritePam(grayPam, gray, allowGray: true);
        Assert.Contains("TUPLTYPE GRAYSCALE\n", Encoding.ASCII.GetString(grayPam.ToArray(), 0, 60));
        using var decoded = PnmCodec.Decode(new MemoryStream(grayPam.ToArray()));
        PixelAssert.Identical(gray, decoded.Pixels);
    }

    [Fact]
    public void Pam_GrayAlpha_Decodes()
    {
        foreach (string tuple in new[] { "GRAYSCALE_ALPHA", "GRAY_ALPHA" })
        {
            var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes($"P7\n# comment\nWIDTH 3\nHEIGHT 1\nDEPTH 2\nMAXVAL 255\nTUPLTYPE {tuple}\nENDHDR\n"));
            ms.Write([10, 255, 200, 0, 77, 128]);
            using var decoded = PnmCodec.Decode(new MemoryStream(ms.ToArray()));
            Assert.Equal([new Rgba32(10, 10, 10, 255), new Rgba32(200, 200, 200, 0), new Rgba32(77, 77, 77, 128)], decoded.Pixels.Row32(0).ToArray());
        }

        // 16-bit gray+alpha.
        var deep = new MemoryStream();
        deep.Write(Encoding.ASCII.GetBytes("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 2\nMAXVAL 65535\nTUPLTYPE GRAYSCALE_ALPHA\nENDHDR\n"));
        deep.Write([0x12, 0x34, 0xFF, 0x00]);
        using var d16 = PnmCodec.Decode(new MemoryStream(deep.ToArray()));
        Assert.Equal(new Rgba64(0x1234, 0x1234, 0x1234, 0xFF00), d16.Pixels.Row64(0)[0]);
    }

    [Fact]
    public void Pnm_DecodesAsciiBitmapAndOddMaxval()
    {
        using (var p1 = Decode("P1\n# bitmap\n3 2\n1 0 1\n0 1 0\n"))
        {
            Assert.Equal([new Rgba32(0, 0, 0), new Rgba32(255, 255, 255), new Rgba32(0, 0, 0)], p1.Pixels.Row32(0).ToArray());
            Assert.Equal(new Rgba32(0, 0, 0), p1.Pixels.Row32(1)[1]);
        }

        using (var p2 = Decode("P2 2 1 15 0 15"))
        {
            Assert.Equal([new Rgba32(0, 0, 0), new Rgba32(255, 255, 255)], p2.Pixels.Row32(0).ToArray());
            Assert.Equal(4, p2.SignificantBits);
        }

        using (var p3 = Decode("P3\n1 1\n255\n1 2 3\n"))
        {
            Assert.Equal(new Rgba32(1, 2, 3), p3.Pixels.Row32(0)[0]);
        }

        var p4 = new MemoryStream();
        p4.Write(Encoding.ASCII.GetBytes("P4\n10 1\n"));
        p4.Write([0b1010_0000, 0b0100_0000]);
        using (var bitmap = PnmCodec.Decode(new MemoryStream(p4.ToArray())))
        {
            Assert.Equal(0, bitmap.Pixels.Row32(0)[0].R);
            Assert.Equal(255, bitmap.Pixels.Row32(0)[1].R);
            Assert.Equal(0, bitmap.Pixels.Row32(0)[2].R);
            Assert.Equal(0, bitmap.Pixels.Row32(0)[9].R);
            Assert.Equal(255, bitmap.Pixels.Row32(0)[8].R);
        }

        // 10-bit samples (djxl writes maxval 1023) scale to the full 16-bit range.
        var p5 = new MemoryStream();
        p5.Write(Encoding.ASCII.GetBytes("P5\n2 1\n1023\n"));
        p5.Write([0x03, 0xFF, 0x02, 0x00]);
        using var tenBit = PnmCodec.Decode(new MemoryStream(p5.ToArray()));
        Assert.Equal(SampleDepth.Sixteen, tenBit.Pixels.Depth);
        Assert.Equal(10, tenBit.SignificantBits);
        Assert.Equal(65535, tenBit.Pixels.Row64(0)[0].R);
        Assert.Equal((ushort)Math.Round(512 * 65535.0 / 1023), tenBit.Pixels.Row64(0)[1].R);
    }

    [Fact]
    public void Pnm_Truncated_Or_Malformed_Throws()
    {
        using var px = TestImages.PhotoLike(8, 4);
        var pam = new MemoryStream();
        PnmCodec.WritePam(pam, px);
        byte[] bytes = pam.ToArray();
        foreach (int cut in new[] { 0, 2, 10, HeaderLength(bytes), bytes.Length - 1 })
        {
            Assert.Throws<InvalidDataException>(() => PnmCodec.Decode(new MemoryStream(bytes, 0, cut)).Dispose());
        }

        Assert.Throws<InvalidDataException>(() => Decode("P6\n0 1\n255\n"));
        Assert.Throws<InvalidDataException>(() => Decode("P6\n1 1\n70000\n"));
        Assert.Throws<InvalidDataException>(() => Decode("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 5\nMAXVAL 255\nENDHDR\n12345"));
        Assert.Throws<InvalidDataException>(() => Decode("P9\n1 1\n255\n"));
        Assert.Throws<InvalidDataException>(() => Decode("P6\n2000000000 1\n255\n"));
        Assert.Throws<InvalidDataException>(() => Decode("P5\n40000 40000\n65535\n"));
        Assert.Throws<InvalidDataException>(() => Decode("P2 2 1 255 0 x"));
    }

    private static DecodedImage Decode(string text) => PnmCodec.Decode(new MemoryStream(Encoding.ASCII.GetBytes(text)));

    private static int HeaderLength(byte[] pam) => Encoding.ASCII.GetString(pam).IndexOf("ENDHDR\n", StringComparison.Ordinal) + 7;

    private static PixelBuffer Opaque(PixelBuffer px)
    {
        var copy = px.Clone();
        for (int y = 0; y < copy.Height; y++)
        {
            if (copy.Depth == SampleDepth.Eight)
            {
                foreach (ref var p in copy.Row32(y))
                {
                    p.A = 255;
                }
            }
            else
            {
                foreach (ref var p in copy.Row64(y))
                {
                    p.A = 65535;
                }
            }
        }

        return copy;
    }
}
