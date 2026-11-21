using Carbonado.Core.Imaging;

namespace Carbonado.Core.Tests.Imaging;

public class PixelBufferTests
{
    [Fact]
    public void Create_ClearsMemory()
    {
        using var px = PixelBuffer.Create(37, 11, SampleDepth.Eight);
        for (int y = 0; y < px.Height; y++)
        {
            Assert.All(px.RowBytes(y).ToArray(), b => Assert.Equal(0, b));
        }
    }

    [Theory]
    [InlineData(SampleDepth.Eight, 4)]
    [InlineData(SampleDepth.Sixteen, 8)]
    public void RowSpans_HaveExactLength(SampleDepth depth, int bpp)
    {
        using var px = PixelBuffer.Create(13, 5, depth);
        Assert.Equal(bpp, px.BytesPerPixel);
        Assert.Equal(13 * bpp, px.Stride);
        Assert.Equal(13L * 5 * bpp, px.ByteLength);
        Assert.Equal(13 * bpp, px.RowBytes(4).Length);
        if (depth == SampleDepth.Eight)
        {
            Assert.Equal(13, px.Row32(0).Length);
            Assert.Throws<InvalidOperationException>(() => px.Row64(0));
        }
        else
        {
            Assert.Equal(13, px.Row64(0).Length);
            Assert.Throws<InvalidOperationException>(() => px.Row32(0));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => px.RowBytes(5));
    }

    [Fact]
    public void ToDepth_RoundTrip8_16_8_IsExact()
    {
        using var px = PixelBuffer.Create(256, 1, SampleDepth.Eight);
        var row = px.Row32(0);
        for (int i = 0; i < 256; i++)
        {
            row[i] = new Rgba32((byte)i, (byte)(255 - i), (byte)(i * 7), (byte)(i ^ 0x5A));
        }

        using var deep = px.ToDepth(SampleDepth.Sixteen);
        Assert.Equal(257 * 200, deep.Row64(0)[200].R);
        using var back = deep.ToDepth(SampleDepth.Eight);
        Assert.True(px.AsSpan().SequenceEqual(back.AsSpan()));
    }

    [Fact]
    public void ToDepth_16To8_RoundsToNearest()
    {
        using var px = PixelBuffer.Create(4, 1, SampleDepth.Sixteen);
        var row = px.Row64(0);
        row[0] = new Rgba64(0, 128, 129, 65535);
        row[1] = new Rgba64(257, 385, 386, 32896);
        using var eight = px.ToDepth(SampleDepth.Eight);
        var r = eight.Row32(0);
        Assert.Equal(new Rgba32(0, 0, 1, 255), r[0]);
        Assert.Equal(new Rgba32(1, 1, 2, 128), r[1]);
    }

    [Fact]
    public void Clone_IsIndependentCopy()
    {
        using var px = PixelBuffer.Create(3, 3, SampleDepth.Eight);
        px.Row32(1)[1] = new Rgba32(9, 8, 7, 6);
        using var copy = px.Clone();
        px.Row32(1)[1] = default;
        Assert.Equal(new Rgba32(9, 8, 7, 6), copy.Row32(1)[1]);
    }

    [Fact]
    public void Dispose_Twice_IsSafe_AndAccessThrows()
    {
        var px = PixelBuffer.Create(2, 2, SampleDepth.Eight);
        px.Dispose();
        px.Dispose();
        Assert.True(px.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => px.RowBytes(0));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-5, 3)]
    public void Create_RejectsNonPositiveSizes(int w, int h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelBuffer.Create(w, h, SampleDepth.Eight));

    [Fact]
    public void FormatInfo_Extensions()
    {
        Assert.Equal(".jxl", FormatInfo.Extension(OutputFormat.Jxl));
        Assert.True(FormatInfo.IsInputExtension("photo.JPEG"));
        Assert.False(FormatInfo.IsInputExtension("notes.txt"));
        Assert.Equal(OutputFormat.Png, FormatInfo.FromImageFormat(ImageFormat.Apng));
    }
}
