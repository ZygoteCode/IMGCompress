using System.Buffers.Binary;
using System.Windows.Media.Imaging;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.TestSupport;

namespace Carbonado.Core.Tests.Imaging;

public class FormatSnifferTests
{
    public static TheoryData<string, string, ImageFormat> Magics => new()
    {
        { "png", "89504E470D0A1A0A0000000D49484452", ImageFormat.Png },

        // signature, IHDR (13 data bytes + CRC), then an acTL chunk header.
        { "apng", "89504E470D0A1A0A" + "0000000D49484452" + "00000001000000010806000000" + "00000000" + "00000008" + Hex("acTL"), ImageFormat.Apng },
        { "jpeg", "FFD8FFE000104A464946", ImageFormat.Jpeg },
        { "gif87a", Hex("GIF87a") + "0100010000", ImageFormat.Gif },
        { "gif89a", Hex("GIF89a") + "0100010000", ImageFormat.Gif },
        { "bmp", "424D3A000000000000003600000028000000", ImageFormat.Bmp },
        { "tiff-le", "49492A0008000000", ImageFormat.Tiff },
        { "tiff-be", "4D4D002A00000008", ImageFormat.Tiff },
        { "webp", Hex("RIFF") + "24000000" + Hex("WEBPVP8 "), ImageFormat.WebP },
        { "avif", "0000001C" + Hex("ftypavif") + "00000000" + Hex("avifmif1miaf"), ImageFormat.Avif },
        { "avis", "00000018" + Hex("ftypavis") + "00000000" + Hex("avismsf1"), ImageFormat.Avif },
        { "avif-compat", "00000018" + Hex("ftypmif1") + "00000000" + Hex("mif1avif"), ImageFormat.Avif },
        { "heic", "00000018" + Hex("ftypheic") + "00000000" + Hex("mif1heic"), ImageFormat.Heic },
        { "heix", "00000018" + Hex("ftypheix") + "00000000" + Hex("mif1heix"), ImageFormat.Heic },
        { "mif1+heic", "00000018" + Hex("ftypmif1") + "00000000" + Hex("mif1heic"), ImageFormat.Heic },
        { "jxl-codestream", "FF0AFA1F", ImageFormat.Jxl },
        { "jxl-container", "0000000C4A584C200D0A870A", ImageFormat.Jxl },
        { "ico", "000001000100101000000100200068040000", ImageFormat.Ico },
        { "qoi", Hex("qoif") + "0000000100000001", ImageFormat.Qoi },
        { "pbm-ascii", Hex("P1\n1 1\n0"), ImageFormat.Pnm },
        { "pgm-ascii", Hex("P2 1 1 255 0"), ImageFormat.Pnm },
        { "ppm-ascii", Hex("P3\n1 1\n255\n"), ImageFormat.Pnm },
        { "pbm", Hex("P4\n1 1\n"), ImageFormat.Pnm },
        { "pgm", Hex("P5\n1 1\n255\n"), ImageFormat.Pnm },
        { "ppm", Hex("P6\r\n1 1\r\n255\r\n"), ImageFormat.Pnm },
        { "pam", Hex("P7\nWIDTH 1\n"), ImageFormat.Pnm },
        { "psd", Hex("8BPS") + "0001000000000000", ImageFormat.Psd },
        { "exr", "762F310102000000", ImageFormat.Exr },
        { "hdr", Hex("#?RADIANCE\n"), ImageFormat.Hdr },
        { "hdr-rgbe", Hex("#?RGBE\n"), ImageFormat.Hdr },
        { "jp2", "0000000C6A5020200D0A870A", ImageFormat.Jp2 },
        { "j2k", "FF4FFF51002F", ImageFormat.Jp2 },
        { "dds", Hex("DDS ") + "7C000000", ImageFormat.Dds },
        { "jxr", "4949BC01080000", ImageFormat.Jxr },
    };

    [Theory]
    [MemberData(nameof(Magics))]
    public void Detect_RecognizesEveryMagic(string name, string hex, ImageFormat expected)
    {
        Assert.False(string.IsNullOrEmpty(name));
        byte[] header = Convert.FromHexString(hex);
        Assert.Equal(expected, FormatSniffer.Detect(header));

        // The extension never overrides a recognised signature.
        Assert.Equal(expected, FormatSniffer.Detect(header, ".tga"));
    }

    [Fact]
    public void Detect_RejectsLookalikes()
    {
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Detect("BM is not a bitmap"u8));
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Detect("P8 not netpbm"u8));
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Detect("P5x"u8));
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Detect(Convert.FromHexString("00000018" + Hex("ftypisom") + "00000000" + Hex("isommp41"))));
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Detect(Hex2("RIFF\0\0\0\0WAVEfmt ")));
    }

    [Fact]
    public void Detect_TgaOnlyByExtension()
    {
        byte[] tga = new byte[18 + (4 * 4)];
        tga[2] = 2; // uncompressed true-colour
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(14), 2);
        tga[16] = 32;
        tga[17] = 8;

        Assert.Equal(ImageFormat.Tga, FormatSniffer.Detect(tga, ".tga"));
        Assert.Equal(ImageFormat.Tga, FormatSniffer.Detect(tga, "TGA"));
        Assert.Equal(ImageFormat.Tga, FormatSniffer.Detect(tga, @"C:\some.dir\Picture.Tga"));
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Detect(tga));
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Detect(tga, ".png"));

        // A header that cannot be TGA stays unknown even with the extension.
        tga[2] = 77;
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Detect(tga, ".tga"));

        using var dir = new TempDir("sniff");
        tga[2] = 2;
        File.WriteAllBytes(dir.File("image.tga"), tga);
        Assert.Equal(ImageFormat.Tga, FormatSniffer.DetectFile(dir.File("image.tga")));
    }

    [Fact]
    public void Detect_ContentWinsOverWrongExtension()
    {
        using var px = TestImages.Gradient(16, 8);
        byte[] png = PngTestFiles.Still(px);
        Assert.Equal(ImageFormat.Png, FormatSniffer.Detect(png, ".jpg"));
        Assert.Equal(ImageFormat.Jpeg, FormatSniffer.Detect([0xFF, 0xD8, 0xFF, 0xDB], "photo.png"));

        using var dir = new TempDir("sniff");
        File.WriteAllBytes(dir.File("really-a-png.jpg"), png);
        Assert.Equal(ImageFormat.Png, FormatSniffer.DetectFile(dir.File("really-a-png.jpg")));
        TestImages.SaveJpegWithWic(px, dir.File("really-a-jpeg.webp"));
        Assert.Equal(ImageFormat.Jpeg, FormatSniffer.DetectFile(dir.File("really-a-jpeg.webp")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("FF")]
    [InlineData("FFD8")]
    [InlineData("8950")]
    [InlineData("4749463839")]
    [InlineData("52494646000000005745")]
    [InlineData("0000000C4A58")]
    [InlineData("50")]
    public void Detect_EmptyOrShort_IsUnknown(string hex)
    {
        byte[] header = Convert.FromHexString(hex);
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Detect(header));
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Detect(header, ".tga"));
    }

    [Fact]
    public void DetectFile_EmptyFile_IsUnknown()
    {
        using var dir = new TempDir("sniff");
        File.WriteAllBytes(dir.File("empty.png"), []);
        Assert.Equal(ImageFormat.Unknown, FormatSniffer.DetectFile(dir.File("empty.png")));
        Assert.False(FormatSniffer.IsAnimated(dir.File("empty.png"), ImageFormat.Gif));
    }

    [Fact]
    public void DetectFile_FindsAcTLBeyondTheHeader()
    {
        using var a = TestImages.Gradient(8, 8);
        using var b = TestImages.Graphic(8, 8);
        byte[] apng = PngTestFiles.Apng(a, b);

        // Push acTL far past the first 64 bytes with a large ancillary chunk after IHDR.
        using var ms = new MemoryStream();
        ms.Write(apng, 0, 33);
        PngTestFiles.WriteChunk(ms, "tEXt", new byte[5000]);
        ms.Write(apng, 33, apng.Length - 33);
        byte[] padded = ms.ToArray();

        Assert.Equal(ImageFormat.Png, FormatSniffer.Detect(padded.AsSpan(0, FormatSniffer.HeaderLength)));
        using var dir = new TempDir("sniff");
        File.WriteAllBytes(dir.File("anim.png"), padded);
        Assert.Equal(ImageFormat.Apng, FormatSniffer.DetectFile(dir.File("anim.png")));
        Assert.True(FormatSniffer.IsAnimated(dir.File("anim.png"), ImageFormat.Apng));
    }

    [Fact]
    public void DetectFile_ReadsLongFtypBoxes()
    {
        // 20 compatible brands push "avif" past the first 64 bytes.
        var box = new MemoryStream();
        var brands = string.Concat(Enumerable.Repeat("mif1", 20)) + "avif";
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(16 + brands.Length));
        "ftypmif1"u8.CopyTo(header.AsSpan(4));
        box.Write(header);
        box.Write(System.Text.Encoding.ASCII.GetBytes(brands));
        using var dir = new TempDir("sniff");
        File.WriteAllBytes(dir.File("x.heic"), box.ToArray());
        Assert.Equal(ImageFormat.Avif, FormatSniffer.DetectFile(dir.File("x.heic")));
    }

    [Fact]
    public void IsAnimated_GifApngWebp()
    {
        using var dir = new TempDir("sniff");
        using var a = TestImages.Gradient(24, 16);
        using var b = TestImages.Graphic(24, 16);
        using var c = TestImages.Palette(24, 16, 4);

        // GIF: WPF writes one image descriptor per frame.
        SaveGif(dir.File("anim.gif"), a, b, c);
        SaveGif(dir.File("still.gif"), a);
        Assert.Equal(ImageFormat.Gif, FormatSniffer.DetectFile(dir.File("anim.gif")));
        Assert.True(FormatSniffer.IsAnimated(dir.File("anim.gif"), ImageFormat.Gif));
        Assert.Equal(3, FormatSniffer.CountFrames(dir.File("anim.gif"), ImageFormat.Gif));
        Assert.False(FormatSniffer.IsAnimated(dir.File("still.gif"), ImageFormat.Gif));

        // APNG with 3 frames vs. still PNG.
        File.WriteAllBytes(dir.File("anim.png"), PngTestFiles.Apng(a, b, c));
        File.WriteAllBytes(dir.File("one-frame.png"), PngTestFiles.Apng(a));
        File.WriteAllBytes(dir.File("still.png"), PngTestFiles.Still(a));
        Assert.Equal(ImageFormat.Apng, FormatSniffer.DetectFile(dir.File("anim.png")));
        Assert.True(FormatSniffer.IsAnimated(dir.File("anim.png"), ImageFormat.Apng));
        Assert.Equal(3, FormatSniffer.CountFrames(dir.File("anim.png"), ImageFormat.Apng));
        Assert.Equal(ImageFormat.Apng, FormatSniffer.DetectFile(dir.File("one-frame.png")));
        Assert.False(FormatSniffer.IsAnimated(dir.File("one-frame.png"), ImageFormat.Apng));
        Assert.Equal(ImageFormat.Png, FormatSniffer.DetectFile(dir.File("still.png")));
        Assert.False(FormatSniffer.IsAnimated(dir.File("still.png"), ImageFormat.Png));

        // WebP: extended container with ANIM + ANMF frames vs. a simple lossy file.
        File.WriteAllBytes(dir.File("anim.webp"), WebPContainer(("VP8X", 10), ("ANIM", 6), ("ANMF", 33), ("ANMF", 17)));
        File.WriteAllBytes(dir.File("still.webp"), WebPContainer(("VP8 ", 25)));
        Assert.Equal(ImageFormat.WebP, FormatSniffer.DetectFile(dir.File("anim.webp")));
        Assert.True(FormatSniffer.IsAnimated(dir.File("anim.webp"), ImageFormat.WebP));
        Assert.False(FormatSniffer.IsAnimated(dir.File("still.webp"), ImageFormat.WebP));

        // AVIF image sequence (avis) vs. still AVIF.
        File.WriteAllBytes(dir.File("seq.avif"), Convert.FromHexString("00000018" + Hex("ftypavis") + "00000000" + Hex("avismsf1")));
        File.WriteAllBytes(dir.File("still.avif"), Convert.FromHexString("00000018" + Hex("ftypavif") + "00000000" + Hex("avifmif1")));
        Assert.True(FormatSniffer.IsAnimated(dir.File("seq.avif"), ImageFormat.Avif));
        Assert.False(FormatSniffer.IsAnimated(dir.File("still.avif"), ImageFormat.Avif));

        // Formats without animation.
        Assert.False(FormatSniffer.IsAnimated(dir.File("still.png"), ImageFormat.Jpeg));
    }

    [Fact]
    public void IsAnimated_ToleratesTruncatedFiles()
    {
        using var dir = new TempDir("sniff");
        using var a = TestImages.Gradient(24, 16);
        using var b = TestImages.Graphic(24, 16);
        SaveGif(dir.File("anim.gif"), a, b);
        byte[] gif = File.ReadAllBytes(dir.File("anim.gif"));
        for (int cut = 1; cut < gif.Length; cut += 7)
        {
            File.WriteAllBytes(dir.File("cut.gif"), gif[..cut]);
            _ = FormatSniffer.IsAnimated(dir.File("cut.gif"), ImageFormat.Gif);
        }

        File.WriteAllBytes(dir.File("cut.webp"), WebPContainer(("VP8X", 10), ("ANIM", 6), ("ANMF", 33))[..30]);
        Assert.False(FormatSniffer.IsAnimated(dir.File("cut.webp"), ImageFormat.WebP));
    }

    private static void SaveGif(string path, params PixelBuffer[] frames)
    {
        var encoder = new GifBitmapEncoder();
        foreach (var f in frames)
        {
            encoder.Frames.Add(BitmapFrame.Create(TestImages.ToBitmapSource(f)));
        }

        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static byte[] WebPContainer(params (string FourCc, int Size)[] chunks)
    {
        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        foreach (var (fourCc, size) in chunks)
        {
            body.Write(System.Text.Encoding.ASCII.GetBytes(fourCc));
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)size);
            body.Write(len);
            body.Write(new byte[size + (size & 1)]);
        }

        using var file = new MemoryStream();
        file.Write("RIFF"u8);
        var total = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(total, (uint)body.Length);
        file.Write(total);
        body.Position = 0;
        body.CopyTo(file);
        return file.ToArray();
    }

    private static string Hex(string ascii) => Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(ascii));

    private static byte[] Hex2(string ascii) => System.Text.Encoding.ASCII.GetBytes(ascii);
}
