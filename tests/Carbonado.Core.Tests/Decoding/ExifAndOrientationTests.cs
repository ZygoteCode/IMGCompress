using Carbonado.Core.Decoding;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Tests.Decoding;

public class ExifAndOrientationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exif_ReadOrientation(bool bigEndian)
    {
        Assert.Equal(6, ExifReader.ReadOrientation(ExifTestData.Build(bigEndian, 6, withGps: true)));
        Assert.Equal(8, ExifReader.ReadOrientation(ExifTestData.Build(bigEndian, 8, withGps: false)));
        Assert.Equal(1, ExifReader.ReadOrientation(ExifTestData.Build(bigEndian, null, withGps: true)));

        // Out-of-range values and garbage read as "normal".
        Assert.Equal(1, ExifReader.ReadOrientation(ExifTestData.Build(bigEndian, 9, withGps: false)));
        Assert.Equal(1, ExifReader.ReadOrientation([]));
        Assert.Equal(1, ExifReader.ReadOrientation("II*\0\xFF\xFF\xFF\x7F"u8));
        Assert.Equal(1, ExifReader.ReadOrientation("not tiff at all"u8));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exif_StripGps_RemovesGpsIfd(bool bigEndian)
    {
        byte[] exif = ExifTestData.Build(bigEndian, 6, withGps: true);
        byte[] latitude = bigEndian ? [0, 0, 0, 45, 0, 0, 0, 1, 0, 0, 0, 27, 0, 0, 0, 1, 0, 0, 0x0B, 0xB8, 0, 0, 0, 100] : ExifTestData.Latitude;
        Assert.True(exif.AsSpan().IndexOf(latitude) >= 0);

        byte[] stripped = ExifReader.StripGps(exif);
        Assert.Equal(exif.Length, stripped.Length);
        Assert.True(stripped.AsSpan().IndexOf(latitude) < 0, "GPS coordinates still present");
        Assert.False(ExifReader.HasGps(stripped));
        Assert.True(ExifReader.HasGps(exif));
        Assert.Equal(6, ExifReader.ReadOrientation(stripped));
        Assert.True(stripped.AsSpan().IndexOf("Canon\0"u8) >= 0, "other tags must survive");
        Assert.NotSame(exif, stripped);

        // Nothing to strip: an equal copy.
        byte[] clean = ExifTestData.Build(bigEndian, 3, withGps: false);
        Assert.Equal(clean, ExifReader.StripGps(clean));
        Assert.Throws<InvalidDataException>(() => ExifReader.StripGps("MM\0*\0\0\xFF\xFF"u8.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exif_WithOrientation_UpdatesOrInserts(bool bigEndian)
    {
        byte[] withTag = ExifTestData.Build(bigEndian, 6, withGps: true);
        byte[] updated = ExifReader.WithOrientation(withTag, 1);
        Assert.Equal(1, ExifReader.ReadOrientation(updated));
        Assert.Equal(withTag.Length, updated.Length);
        Assert.Equal(6, ExifReader.ReadOrientation(withTag));

        byte[] without = ExifTestData.Build(bigEndian, null, withGps: true);
        byte[] inserted = ExifReader.WithOrientation(without, 8);
        Assert.Equal(8, ExifReader.ReadOrientation(inserted));
        Assert.True(ExifReader.HasGps(inserted), "existing tags stay reachable");
        Assert.True(inserted.AsSpan().IndexOf("Canon\0"u8) >= 0);
        Assert.Equal(without, ExifReader.WithOrientation(without, 1));

        byte[] minimal = ExifReader.CreateMinimal(5);
        Assert.Equal(5, ExifReader.ReadOrientation(minimal));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExifReader.WithOrientation(withTag, 9));
    }

    [Theory]
    [InlineData(1, 3, 2, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(2, 3, 2, new[] { 2, 1, 0, 5, 4, 3 })]
    [InlineData(3, 3, 2, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(4, 3, 2, new[] { 3, 4, 5, 0, 1, 2 })]
    [InlineData(5, 2, 3, new[] { 0, 3, 1, 4, 2, 5 })]
    [InlineData(6, 2, 3, new[] { 3, 0, 4, 1, 5, 2 })]
    [InlineData(7, 2, 3, new[] { 5, 2, 4, 1, 3, 0 })]
    [InlineData(8, 2, 3, new[] { 2, 5, 1, 4, 0, 3 })]
    public void Orientation_Apply_AllEight(ushort orientation, int width, int height, int[] expected)
    {
        // Source 3×2:  0 1 2 / 3 4 5 (value in R).
        foreach (var depth in new[] { SampleDepth.Eight, SampleDepth.Sixteen })
        {
            using var src = PixelBuffer.Create(3, 2, depth);
            for (int i = 0; i < 6; i++)
            {
                Set(src, i % 3, i / 3, i);
            }

            var dst = Orientation.Apply(src, orientation);
            try
            {
                Assert.Equal((width, height), (dst.Width, dst.Height));
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.Equal(expected[i], Get(dst, i % width, i / width));
                }

                Assert.True(orientation == 1 ? ReferenceEquals(src, dst) : !ReferenceEquals(src, dst));
            }
            finally
            {
                if (!ReferenceEquals(src, dst))
                {
                    dst.Dispose();
                }
            }
        }
    }

    [Fact]
    public void Orientation_LargeImage_MatchesReferenceTransform()
    {
        // Larger than one tile in both directions, odd sizes.
        using var src = Carbonado.Core.Tests.TestSupport.TestImages.PhotoLike(197, 131);
        foreach (ushort o in new ushort[] { 5, 6, 7, 8 })
        {
            using var dst = Orientation.Apply(src, o);
            for (int y = 0; y < dst.Height; y += 7)
            {
                for (int x = 0; x < dst.Width; x += 5)
                {
                    var (sx, sy) = o switch
                    {
                        5 => (y, x),
                        6 => (y, src.Height - 1 - x),
                        7 => (src.Width - 1 - y, src.Height - 1 - x),
                        _ => (src.Width - 1 - y, x),
                    };
                    Assert.Equal(src.Row32(sy)[sx], dst.Row32(y)[x]);
                }
            }
        }
    }

    private static void Set(PixelBuffer px, int x, int y, int v)
    {
        if (px.Depth == SampleDepth.Eight)
        {
            px.Row32(y)[x] = new Rgba32((byte)v, 0, 0);
        }
        else
        {
            px.Row64(y)[x] = new Rgba64((ushort)(v * 1000), 0, 0);
        }
    }

    private static int Get(PixelBuffer px, int x, int y) => px.Depth == SampleDepth.Eight ? px.Row32(y)[x].R : px.Row64(y)[x].R / 1000;
}
