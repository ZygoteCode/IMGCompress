using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMetadata = Carbonado.Core.Imaging.ImageMetadata;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Codecs;

public class PngCodecTests
{
    public static TheoryData<PngColorType, int> LegalTypesAndDepths => new()
    {
        { PngColorType.Gray, 1 }, { PngColorType.Gray, 2 }, { PngColorType.Gray, 4 }, { PngColorType.Gray, 8 }, { PngColorType.Gray, 16 },
        { PngColorType.Rgb, 8 }, { PngColorType.Rgb, 16 },
        { PngColorType.Palette, 1 }, { PngColorType.Palette, 2 }, { PngColorType.Palette, 4 }, { PngColorType.Palette, 8 },
        { PngColorType.GrayAlpha, 8 }, { PngColorType.GrayAlpha, 16 },
        { PngColorType.Rgba, 8 }, { PngColorType.Rgba, 16 },
    };

    [Theory]
    [MemberData(nameof(LegalTypesAndDepths))]
    public void Png_RoundTrip_AllColorTypesAndDepths(PngColorType type, int depth)
    {
        foreach (var (w, h) in new[] { (37, 23), (1, 1), (8, 3), (3, 9) })
        {
            using var source = Synthetic(type, depth, w, h, seed: w * h);
            foreach (PngFilterStrategy filter in Enum.GetValues<PngFilterStrategy>())
            {
                foreach (bool interlace in new[] { false, true })
                {
                    var options = new PngEncodeOptions(type, depth, CompressionLevel.Optimal, filter, Interlace: interlace);
                    byte[] png = Encode(source, null, options);
                    var ihdr = ReadIhdr(png);
                    Assert.Equal(((byte)type, (byte)depth, (byte)(interlace ? 1 : 0)), (ihdr.ColorType, ihdr.BitDepth, ihdr.Interlace));
                    using var decoded = PngTestFiles.Decode(new MemoryStream(png));
                    Assert.Equal(depth == 16 ? SampleDepth.Sixteen : SampleDepth.Eight, decoded.Pixels.Depth);
                    PixelAssert.Identical(source, decoded.Pixels);
                    Assert.Equal(ImageFormat.Png, decoded.SourceFormat);
                    Assert.Equal(1, decoded.FrameCount);
                }
            }
        }
    }

    [Fact]
    public void Png_DecodesInterlaced()
    {
        foreach (var make in new Func<PixelBuffer>[] { () => TestImages.PhotoLike(61, 47), () => TestImages.WithAlpha(33, 17), () => TestImages.Deep16(), () => TestImages.Palette(13, 7, 5) })
        {
            using var source = make();
            byte[] interlaced = Encode(source, null, new PngEncodeOptions(Interlace: true));
            Assert.Equal(1, ReadIhdr(interlaced).Interlace);
            using var a = PngTestFiles.Decode(new MemoryStream(interlaced));
            PixelAssert.Identical(source, a.Pixels);

            // WIC (independent decoder) agrees with our interlaced output.
            if (source.Depth == SampleDepth.Eight)
            {
                using var dir = new TempDir("png");
                File.WriteAllBytes(dir.File("i.png"), interlaced);
                using var wic = TestImages.LoadWithWic(dir.File("i.png"));
                PixelAssert.Identical(source, wic);
            }
        }
    }

    [Fact]
    public void Png_Encode_AutoChoosesSmallestExactNonPaletteType()
    {
        using var gray = TestImages.Gray();
        using var photo = TestImages.PhotoLike(40, 30);
        using var alpha = TestImages.WithAlpha(40, 30);
        using var deep = TestImages.Deep16();
        using var grayAlpha = gray.Clone();
        grayAlpha.Row32(3)[5].A = 7;

        Assert.Equal(((byte)0, (byte)8), Type(gray));
        Assert.Equal(((byte)2, (byte)8), Type(photo));
        Assert.Equal(((byte)6, (byte)8), Type(alpha));
        Assert.Equal(((byte)2, (byte)16), Type(deep));
        Assert.Equal(((byte)4, (byte)8), Type(grayAlpha));

        // An RGB colour profile forbids the gray reduction (it would invalidate the profile).
        var rgbIcc = new ImageMetadata { Icc = SyntheticIcc("RGB ") };
        var ihdr = ReadIhdr(Encode(gray, rgbIcc, new PngEncodeOptions()));
        Assert.Equal((byte)2, ihdr.ColorType);
        var grayIcc = new ImageMetadata { Icc = SyntheticIcc("GRAY") };
        Assert.Equal((byte)0, ReadIhdr(Encode(gray, grayIcc, new PngEncodeOptions())).ColorType);

        static (byte, byte) Type(PixelBuffer px)
        {
            var i = ReadIhdr(Encode(px, null, new PngEncodeOptions()));
            return (i.ColorType, i.BitDepth);
        }
    }

    [Fact]
    public void Png_Encode_RejectsInexactExplicitType()
    {
        using var photo = TestImages.PhotoLike(40, 30);
        using var alpha = TestImages.WithAlpha(40, 30);
        using var deep = TestImages.Deep16();
        using var gray = TestImages.Gray();
        Assert.Throws<ArgumentException>(() => Encode(photo, null, new PngEncodeOptions(PngColorType.Gray)));
        Assert.Throws<ArgumentException>(() => Encode(photo, null, new PngEncodeOptions(PngColorType.Palette)));
        Assert.Throws<ArgumentException>(() => Encode(alpha, null, new PngEncodeOptions(PngColorType.Rgb)));
        Assert.Throws<ArgumentException>(() => Encode(deep, null, new PngEncodeOptions(PngColorType.Rgb, 8)));
        Assert.Throws<ArgumentException>(() => Encode(gray, null, new PngEncodeOptions(PngColorType.Gray, 4)));
        Assert.Throws<ArgumentException>(() => Encode(photo, null, new PngEncodeOptions(PngColorType.Rgb, 4)));
        Assert.Throws<ArgumentException>(() => Encode(photo, null, new PngEncodeOptions(PngColorType.Palette, 16)));
    }

    [Fact]
    public void Png_Encode_BinaryAlphaWithoutAlphaChannel_UsesTrnsKey()
    {
        foreach (var type in new[] { PngColorType.Rgb, PngColorType.Gray })
        {
            using var px = type == PngColorType.Gray ? TestImages.Gray(32, 16) : TestImages.PhotoLike(32, 16);
            for (int x = 0; x < 32; x += 3)
            {
                px.Row32(5)[x] = new Rgba32(1, 1, 1, 0);
            }

            // Make sure no opaque pixel uses the key colour.
            for (int y = 0; y < px.Height; y++)
            {
                foreach (ref var p in px.Row32(y))
                {
                    if (p.A == 255 && p.R == 1 && p.G == 1 && p.B == 1)
                    {
                        p = new Rgba32(2, 2, 2);
                    }
                }
            }

            byte[] png = Encode(px, null, new PngEncodeOptions(type));
            Assert.Contains(PngTestFiles.Chunks(png), c => c.Type == "tRNS");
            using var decoded = PngTestFiles.Decode(new MemoryStream(png));
            PixelAssert.Identical(px, decoded.Pixels);
        }
    }

    [Fact]
    public void Png_PreservesIccAndExif()
    {
        using var px = TestImages.PhotoLike(48, 32);
        byte[] icc = SyntheticIcc("RGB ");
        byte[] exif = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF/></x:xmpmeta>");
        var meta = new ImageMetadata { Icc = icc, Exif = exif, Xmp = xmp, DpiX = 300, DpiY = 150 };

        using (var decoded = PngTestFiles.Decode(new MemoryStream(Encode(px, meta, new PngEncodeOptions(WriteIcc: true, WriteExif: true)))))
        {
            Assert.Equal(icc, decoded.Metadata.Icc);
            Assert.Equal(exif, decoded.Metadata.Exif);
            Assert.Equal(xmp, decoded.Metadata.Xmp);
            Assert.Equal(300, decoded.Metadata.DpiX, 1);
            Assert.Equal(150, decoded.Metadata.DpiY, 1);
            PixelAssert.Identical(px, decoded.Pixels);
        }

        using (var bare = PngTestFiles.Decode(new MemoryStream(Encode(px, meta, new PngEncodeOptions(WriteIcc: false, WriteExif: false)))))
        {
            Assert.Null(bare.Metadata.Icc);
            Assert.Null(bare.Metadata.Exif);
            Assert.Null(bare.Metadata.Xmp);
        }

        // EncodeFast carries all metadata it is given (intermediates feed metadata to the encoders).
        var ms = new MemoryStream();
        PngEncoder.EncodeFast(ms, px, meta);
        using var fast = PngTestFiles.Decode(new MemoryStream(ms.ToArray()));
        Assert.Equal(icc, fast.Metadata.Icc);
        Assert.Equal(exif, fast.Metadata.Exif);
        Assert.Equal(xmp, fast.Metadata.Xmp);
    }

    [Fact]
    public void Png_Palette_WithTrns_DecodesAlpha()
    {
        // 2-bit palette, 3 entries, tRNS for the first two only: index 2 must stay opaque.
        var raw = new MemoryStream();
        byte[] rows = [0b00_01_10_00, 0b10_10_01_00];
        foreach (byte r in rows)
        {
            raw.WriteByte(0);
            raw.WriteByte(r);
        }

        var png = new MemoryStream();
        png.Write(PngTestFiles.Signature);
        PngTestFiles.WriteChunk(png, "IHDR", PngTestFiles.Ihdr(4, 2, bitDepth: 2, colorType: 3));
        PngTestFiles.WriteChunk(png, "PLTE", [255, 0, 0, 0, 255, 0, 0, 0, 255]);
        PngTestFiles.WriteChunk(png, "tRNS", [0, 128]);
        PngTestFiles.WriteChunk(png, "IDAT", ZlibCompress(raw.ToArray()));
        PngTestFiles.WriteChunk(png, "IEND", []);

        using var decoded = PngTestFiles.Decode(new MemoryStream(png.ToArray()));
        Assert.Equal(SampleDepth.Eight, decoded.Pixels.Depth);
        Assert.Equal(
            [new Rgba32(255, 0, 0, 0), new Rgba32(0, 255, 0, 128), new Rgba32(0, 0, 255, 255), new Rgba32(255, 0, 0, 0)],
            decoded.Pixels.Row32(0).ToArray());
        Assert.Equal(
            [new Rgba32(0, 0, 255, 255), new Rgba32(0, 0, 255, 255), new Rgba32(0, 255, 0, 128), new Rgba32(255, 0, 0, 0)],
            decoded.Pixels.Row32(1).ToArray());
    }

    [Fact]
    public void Png_RejectsBadCrc_WithInvalidDataException()
    {
        using var px = TestImages.Palette(16, 16, 6);
        byte[] png = Encode(px, null, new PngEncodeOptions(PngColorType.Palette));
        foreach (var chunk in PngTestFiles.Chunks(png).Where(c => c.Type is "IHDR" or "PLTE" or "IDAT"))
        {
            // Flip one payload bit, then one CRC bit.
            foreach (int offset in new[] { chunk.Offset + 8 + (chunk.Length / 2), chunk.Offset + 8 + chunk.Length + 1 })
            {
                byte[] bad = png.ToArray();
                bad[offset] ^= 0x10;
                var ex = Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(bad)).Dispose());
                Assert.Contains("CRC", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Png_Truncated_Throws()
    {
        using var px = TestImages.PhotoLike(64, 48);
        byte[] png = Encode(px, new ImageMetadata { Exif = [0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0] }, new PngEncodeOptions(WriteExif: true));
        var chunks = PngTestFiles.Chunks(png);
        var lastIdat = chunks.Last(c => c.Type == "IDAT");
        int imageDataEnd = lastIdat.Offset + 12 + lastIdat.Length;
        for (int cut = 0; cut < png.Length; cut++)
        {
            var truncated = new MemoryStream(png, 0, cut);
            if (cut < imageDataEnd)
            {
                Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(truncated).Dispose());
            }
            else
            {
                // All image data present: only the IEND marker is missing; pixels must be complete and exact.
                using var decoded = PngTestFiles.Decode(truncated);
                PixelAssert.Identical(px, decoded.Pixels);
            }
        }
    }

    [Fact]
    public void Png_RejectsGarbageAndStructuralErrors()
    {
        Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream([])));
        Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(Encoding.ASCII.GetBytes("definitely not a png file"))));

        using var px = TestImages.Gradient(8, 8);
        byte[] good = PngTestFiles.Still(px);

        // Unknown critical chunk before IDAT.
        var ms = new MemoryStream();
        ms.Write(good, 0, 33);
        PngTestFiles.WriteChunk(ms, "ABCD", [1, 2, 3]);
        ms.Write(good, 33, good.Length - 33);
        Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(ms.ToArray())));

        // Illegal colour type / depth combination.
        var bad = new MemoryStream();
        bad.Write(PngTestFiles.Signature);
        PngTestFiles.WriteChunk(bad, "IHDR", PngTestFiles.Ihdr(8, 8, bitDepth: 4, colorType: 2));
        PngTestFiles.WriteChunk(bad, "IEND", []);
        Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(bad.ToArray())));

        // Missing IDAT.
        var empty = new MemoryStream();
        empty.Write(PngTestFiles.Signature);
        PngTestFiles.WriteChunk(empty, "IHDR", PngTestFiles.Ihdr(8, 8, bitDepth: 8, colorType: 2));
        PngTestFiles.WriteChunk(empty, "IEND", []);
        Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(empty.ToArray())));

        // Image data that inflates to fewer bytes than the IHDR promises.
        var shortData = new MemoryStream();
        shortData.Write(PngTestFiles.Signature);
        PngTestFiles.WriteChunk(shortData, "IHDR", PngTestFiles.Ihdr(8, 8, bitDepth: 8, colorType: 2));
        PngTestFiles.WriteChunk(shortData, "IDAT", ZlibCompress(new byte[(8 * 3) + 1]));
        PngTestFiles.WriteChunk(shortData, "IEND", []);
        Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(shortData.ToArray())));

        // Hostile sizes are rejected before any allocation (no OverflowException/OutOfMemory).
        foreach (var (w, h) in new[] { (int.MaxValue, 1), (40_000, 40_000), (300_000_000, 1) })
        {
            var huge = new MemoryStream();
            huge.Write(PngTestFiles.Signature);
            PngTestFiles.WriteChunk(huge, "IHDR", PngTestFiles.Ihdr(w, h, bitDepth: 16, colorType: 6));
            PngTestFiles.WriteChunk(huge, "IDAT", ZlibCompress(new byte[16]));
            PngTestFiles.WriteChunk(huge, "IEND", []);
            Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(huge.ToArray())));
        }

        // Unknown filter type.
        var badFilter = new byte[(8 * 3) + 1];
        badFilter[0] = 9;
        var filterPng = new MemoryStream();
        filterPng.Write(PngTestFiles.Signature);
        PngTestFiles.WriteChunk(filterPng, "IHDR", PngTestFiles.Ihdr(8, 1, bitDepth: 8, colorType: 2));
        PngTestFiles.WriteChunk(filterPng, "IDAT", ZlibCompress(badFilter));
        PngTestFiles.WriteChunk(filterPng, "IEND", []);
        Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(filterPng.ToArray())));
    }

    [Fact]
    public void Png_DecodesWpfEncodedFiles()
    {
        using var dir = new TempDir("png");
        var images = new Func<PixelBuffer>[]
        {
            () => TestImages.Gradient(), () => TestImages.PhotoLike(), () => TestImages.Graphic(), () => TestImages.WithAlpha(),
            () => TestImages.Palette(), () => TestImages.Gray(),
        };
        int i = 0;
        foreach (var make in images)
        {
            using var px = make();
            string path = dir.File($"wpf{i++}.png");
            TestImages.SavePngWithWic(px, path);
            using var ours = PngTestFiles.DecodeFile(path);
            using var wic = TestImages.LoadWithWic(path);
            PixelAssert.Identical(wic, ours.Pixels);
        }

        // 16-bit: WPF writes Rgba64; compare against WIC's own 16-bit decode.
        using var deep = TestImages.Deep16();
        string deepPath = dir.File("deep.png");
        TestImages.SavePngWithWic(deep, deepPath);
        using var ours16 = PngTestFiles.DecodeFile(deepPath);
        Assert.Equal(SampleDepth.Sixteen, ours16.Pixels.Depth);
        using var wic16 = LoadWithWic16(deepPath);
        PixelAssert.Identical(wic16, ours16.Pixels);
        PixelAssert.Identical(deep, ours16.Pixels);
        Assert.Equal(16, ours16.SignificantBits);
    }

    [Fact]
    public void Png_ApngFirstFrameAndCount()
    {
        using var a = TestImages.PhotoLike(20, 12, seed: 1);
        using var b = TestImages.PhotoLike(20, 12, seed: 2);
        using var c = TestImages.PhotoLike(20, 12, seed: 3);
        using var decoded = PngTestFiles.Decode(new MemoryStream(PngTestFiles.Apng(a, b, c)));
        Assert.Equal(ImageFormat.Apng, decoded.SourceFormat);
        Assert.Equal(3, decoded.FrameCount);
        Assert.True(decoded.IsAnimated);
        PixelAssert.Identical(a, decoded.Pixels);
    }

    [Fact]
    public void Png_EncodeFast_LargeImage_RoundTripsAndCompresses()
    {
        // Large enough for the parallel deflate path.
        using var px = TestImages.PhotoLike(1600, 1200);
        var ms = new MemoryStream();
        PngEncoder.EncodeFast(ms, px, null);
        byte[] png = ms.ToArray();
        Assert.True(png.Length < 1600 * 1200 * 3, $"{png.Length} bytes");
        Assert.Equal(((byte)2, (byte)8), (ReadIhdr(png).ColorType, ReadIhdr(png).BitDepth));
        using var decoded = PngTestFiles.Decode(new MemoryStream(png));
        PixelAssert.Identical(px, decoded.Pixels);

        // An independent decoder accepts the stitched zlib stream.
        using var dir = new TempDir("png");
        File.WriteAllBytes(dir.File("big.png"), png);
        using var wic = TestImages.LoadWithWic(dir.File("big.png"));
        PixelAssert.Identical(px, wic);
    }

    [Fact]
    public void Png_ParallelDeflate_IsUsedAndProducesOneValidZlibStream()
    {
        using var px = TestImages.PhotoLike(1400, 1100);
        var layout = new PngLayout { ColorType = PngColorType.Rgb, BitDepth = 8 };
        var settings = new PngWriteSettings(PngFilterStrategy.Sub, 1, ZLibCompressionStrategy.Default, WriteIcc: false, WriteExif: false, AllowParallel: true);
        var chunks = new MemoryStream();
        var idat = new PngIdatStream(chunks, 1 << 30);
        Assert.True(PngEncoder.TryCompressParallel(idat, px, layout, settings));
        idat.Complete();

        // Unwrap the IDAT payload and inflate it as one zlib stream (Adler-32 included).
        byte[] framed = chunks.ToArray();
        int length = BinaryPrimitives.ReadInt32BigEndian(framed);
        var inflated = new MemoryStream();
        using (var z = new ZLibStream(new MemoryStream(framed, 8, length), CompressionMode.Decompress))
        {
            z.CopyTo(inflated);
        }

        Assert.Equal((long)px.Height * ((px.Width * 3) + 1), inflated.Length);
        byte[] raw = inflated.ToArray();
        Assert.All(Enumerable.Range(0, px.Height), y => Assert.Equal(1, raw[y * ((px.Width * 3) + 1)]));

        // The combined Adler-32 is really checked: damaging it makes inflate fail.
        framed[8 + length - 1] ^= 0x01;
        using var damaged = new ZLibStream(new MemoryStream(framed, 8, length), CompressionMode.Decompress);
        Assert.Throws<InvalidDataException>(() => damaged.CopyTo(Stream.Null));
    }

    [Fact]
    public void Png_SmallestSize_IsSmallerThanFast()
    {
        using var px = TestImages.Graphic();
        var fast = new MemoryStream();
        PngEncoder.EncodeFast(fast, px, null);
        byte[] best = Encode(px, null, new PngEncodeOptions());
        Assert.True(best.Length < fast.Length, $"{best.Length} vs {fast.Length}");
    }

    internal static byte[] Encode(PixelBuffer px, ImageMetadata? meta, PngEncodeOptions options)
    {
        var ms = new MemoryStream();
        PngEncoder.Encode(ms, px, meta, options);
        return ms.ToArray();
    }

    internal static (byte BitDepth, byte ColorType, byte Interlace) ReadIhdr(byte[] png)
    {
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        return (png[24], png[25], png[28]);
    }

    internal static byte[] SyntheticIcc(string colorSpace)
    {
        var icc = new byte[132];
        BinaryPrimitives.WriteUInt32BigEndian(icc, (uint)icc.Length);
        Encoding.ASCII.GetBytes("mntr").CopyTo(icc, 12);
        Encoding.ASCII.GetBytes(colorSpace).CopyTo(icc, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(icc, 20);
        Encoding.ASCII.GetBytes("acsp").CopyTo(icc, 36);
        return icc;
    }

    /// <summary>Pixels exactly representable in <paramref name="type"/>/<paramref name="depth"/>: noise mixed with flat runs.</summary>
    internal static PixelBuffer Synthetic(PngColorType type, int depth, int w, int h, int seed)
    {
        var rng = new Random(seed);
        var px = PixelBuffer.Create(w, h, depth == 16 ? SampleDepth.Sixteen : SampleDepth.Eight);
        int max = depth == 16 ? 65535 : 255;
        Rgba64[] palette = Enumerable.Range(0, 1 << Math.Min(depth, 8))
            .Select(i => new Rgba64((ushort)rng.Next(256), (ushort)rng.Next(256), (ushort)rng.Next(256), (ushort)(i % 3 == 0 ? rng.Next(256) : 255)))
            .ToArray();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool flat = ((x / 4) + y) % 3 == 0;
                int r = flat ? max / 3 : rng.Next(max + 1), g = flat ? max / 5 : rng.Next(max + 1), b = flat ? max / 7 : rng.Next(max + 1);
                int a = flat ? max : rng.Next(max + 1);
                Rgba64 p = type switch
                {
                    PngColorType.Gray when depth < 8 => Gray(rng.Next(1 << depth) * (255 / ((1 << depth) - 1)), 255),
                    PngColorType.Gray => Gray(r, max),
                    PngColorType.GrayAlpha => Gray(r, a),
                    PngColorType.Rgb => new Rgba64((ushort)r, (ushort)g, (ushort)b, (ushort)max),
                    PngColorType.Palette => palette[flat ? 0 : rng.Next(palette.Length)],
                    _ => new Rgba64((ushort)r, (ushort)g, (ushort)b, (ushort)a),
                };
                if (px.Depth == SampleDepth.Sixteen)
                {
                    px.Row64(y)[x] = p;
                }
                else
                {
                    px.Row32(y)[x] = new Rgba32((byte)p.R, (byte)p.G, (byte)p.B, (byte)p.A);
                }
            }
        }

        return px;

        static Rgba64 Gray(int v, int a) => new((ushort)v, (ushort)v, (ushort)v, (ushort)a);
    }

    internal static byte[] ZlibCompress(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    private static PixelBuffer LoadWithWic16(string path)
    {
        using var fs = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var rgba = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Rgba64, null, 0);
        int w = rgba.PixelWidth, h = rgba.PixelHeight;
        var bytes = new byte[w * h * 8];
        rgba.CopyPixels(bytes, w * 8, 0);
        var px = PixelBuffer.Create(w, h, SampleDepth.Sixteen, clear: false);
        for (int y = 0; y < h; y++)
        {
            bytes.AsSpan(y * w * 8, w * 8).CopyTo(px.RowBytes(y));
        }

        return px;
    }
}
