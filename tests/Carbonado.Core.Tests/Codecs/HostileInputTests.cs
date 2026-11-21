using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Decoding;

namespace Carbonado.Core.Tests.Codecs;

/// <summary>
/// Tests that measure allocations (process-wide managed bytes, peak commit charge): they run alone, so no concurrent
/// test is counted.
/// </summary>
[CollectionDefinition(nameof(MemorySensitive), DisableParallelization = true)]
public sealed class MemorySensitive;

/// <summary>Forged headers and chunk lengths are refused with <see cref="InvalidDataException"/> before any large allocation.</summary>
/// <remarks>
/// Each forged size would allocate at least 60 MB (most 1.5–2 GiB) if it were trusted. Managed allocations are counted
/// exactly; native pixel buffers show in the peak commit charge (a high-water mark, so the specific rejection message is
/// asserted as well: it comes from the check that runs before the allocation).
/// </remarks>
[Collection(nameof(MemorySensitive))]
public class HostileInputTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Png_ForgedDimensions_AreRefusedBeforeAllocating(bool interlaced)
    {
        // 16384 × 32768 RGBA (2 GiB of pixels) declared by a 100-byte file.
        byte[] png = Png(PngTestFiles.Ihdr(16384, 32768, bitDepth: 8, colorType: 6, interlace: (byte)(interlaced ? 1 : 0)), Zlib(new byte[64]));
        var used = Allocations.Measure(() =>
        {
            var ex = Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(png)));
            Assert.Contains("too small to hold", ex.Message);
        });
        used.AssertSmall();

        // A stream that cannot tell its length still has the pixel cap.
        var capped = Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(new NonSeekableStream(png), maxPixels: 1 << 20, Ct));
        Assert.Contains("exceeds", capped.Message);
    }

    [Fact]
    public void Png_ForgedPaletteAndTransparencyLengths_AreRefusedBeforeAllocating()
    {
        // Non-seekable streams: only the per-chunk validation stands between a forged length and a 2 GB array.
        const uint Huge = 0x7F00_0000;
        byte[] palette = Png(PngTestFiles.Ihdr(1, 1, bitDepth: 8, colorType: 3), Zlib([0, 0]), ("PLTE", Huge));
        byte[] trns = Png(PngTestFiles.Ihdr(1, 1, bitDepth: 8, colorType: 2), Zlib([0, 1, 2, 3]), ("tRNS", Huge));
        Allocations.Measure(() =>
        {
            var plte = Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(new NonSeekableStream(palette), Ct));
            Assert.Contains("PLTE", plte.Message);
            Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(new NonSeekableStream(trns), Ct));
            Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(palette)));
            Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(trns)));
        }).AssertSmall();

        // Seekable streams: a chunk longer than the rest of the file is a truncation, whatever its type (here a 60 MB
        // iCCP, under the metadata cap).
        byte[] icc = Png(PngTestFiles.Ihdr(1, 1, bitDepth: 8, colorType: 2), Zlib([0, 1, 2, 3]), ("iCCP", 60u << 20));
        Allocations.Measure(() =>
        {
            var truncated = Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(icc)));
            Assert.Contains("truncated", truncated.Message);
        }).AssertSmall();
    }

    [Fact]
    public void Png_InvalidOptionalChunks_AreIgnored()
    {
        byte[] rgb = [0, 10, 20, 30];

        // A malformed suggested palette in a truecolour image and a tRNS of the wrong size are ignored (as libpng does)…
        using (var decoded = PngTestFiles.Decode(new MemoryStream(Png(PngTestFiles.Ihdr(1, 1, 8, 2), Zlib(rgb), ("PLTE", new byte[770]), ("tRNS", new byte[2])))))
        {
            Assert.Equal(new Rgba32(10, 20, 30), decoded.Pixels.Row32(0)[0]);
        }

        // …and a gray image's tRNS must be exactly one sample.
        using (var gray = PngTestFiles.Decode(new MemoryStream(Png(PngTestFiles.Ihdr(1, 1, 8, 0), Zlib([0, 7]), ("tRNS", new byte[6])))))
        {
            Assert.Equal(new Rgba32(7, 7, 7), gray.Pixels.Row32(0)[0]);
        }

        // A palette image cannot do without its palette.
        var ex = Assert.Throws<InvalidDataException>(() => PngTestFiles.Decode(new MemoryStream(Png(PngTestFiles.Ihdr(1, 1, 8, 3), Zlib([0, 0]), ("PLTE", new byte[770])))));
        Assert.Contains("PLTE", ex.Message);
    }

    [Fact]
    public void Png_RepeatedMetadataChunks_FirstWins_AndTheFileStillDecodes()
    {
        byte[] first = ExifTestData.Build(false, 6, withGps: false), second = ExifTestData.Build(true, 3, withGps: true);
        byte[] icc = TestIcc.DisplayP3Gamma22();

        // A raw-profile EXIF (as libjxl and ImageMagick write it), then eXIf chunks and two iCCP chunks.
        byte[] png = Png(PngTestFiles.Ihdr(1, 1, 8, 2), Zlib([0, 1, 2, 3]),
            ("iCCP", Iccp(icc)), ("iCCP", Iccp(TestIcc.DisplayP3Gamma22())), ("zTXt", RawProfile("exif", first)), ("eXIf", second), ("eXIf", first));
        using var decoded = PngTestFiles.Decode(new MemoryStream(png));
        Assert.Equal(new Rgba32(1, 2, 3), decoded.Pixels.Row32(0)[0]);
        Assert.Equal(first, decoded.Metadata.Exif);
        Assert.Equal(icc, decoded.Metadata.Icc);

        using var metadataOnly = new MemoryStream(png);
        Assert.Equal(first, PngDecoder.ReadMetadata(metadataOnly).Exif);
    }

    [Fact]
    public void Png_RawProfileWithForgedLength_IsIgnored()
    {
        // The declared length (60 MB) is far beyond the hex digits present: no EXIF, and no 60 MB buffer either.
        byte[] text = [.. "Raw profile type exif\0"u8, .. "\nexif\n 60000000\n4d4d002a"u8];
        byte[] png = Png(PngTestFiles.Ihdr(1, 1, 8, 0), Zlib([0, 9]), ("tEXt", text));
        Allocations.Measure(() =>
        {
            using var decoded = PngTestFiles.Decode(new MemoryStream(png));
            Assert.Null(decoded.Metadata.Exif);
        }).AssertSmall();
    }

    [Fact]
    public void Pnm_ForgedDimensions_AreRefusedBeforeAllocating()
    {
        byte[] ppm = [.. "P6\n16384 32768\n255\n"u8, .. new byte[16]];
        byte[] pam = [.. "P7\nWIDTH 16384\nHEIGHT 16384\nDEPTH 4\nMAXVAL 65535\nTUPLTYPE RGB_ALPHA\nENDHDR\n"u8, .. new byte[16]];
        byte[] ascii = [.. "P3\n20000 20000\n255\n1 2 3\n"u8];
        Allocations.Measure(() =>
        {
            foreach (byte[] file in new[] { ppm, pam, ascii })
            {
                var ex = Assert.Throws<InvalidDataException>(() => PnmCodec.Decode(new MemoryStream(file)));
                Assert.Contains("smaller than its header declares", ex.Message);
            }
        }).AssertSmall();

        // The pixel cap applies to any stream.
        Assert.Throws<InvalidDataException>(() => PnmCodec.Decode(new NonSeekableStream(ppm), maxPixels: 1 << 20));

        // A complete raster of the declared size still decodes.
        using var ok = PnmCodec.Decode(new MemoryStream([.. "P6\n2 1\n255\n"u8, 1, 2, 3, 4, 5, 6]));
        Assert.Equal(new Rgba32(4, 5, 6), ok.Pixels.Row32(0)[1]);
    }

    // Signature, IHDR, the given chunks (a uint is a forged length with no payload), IDAT, IEND.
    private static byte[] Png(byte[] ihdr, byte[] idat, params (string Type, object Data)[] chunks)
    {
        using var ms = new MemoryStream();
        ms.Write(PngTestFiles.Signature);
        PngTestFiles.WriteChunk(ms, "IHDR", ihdr);
        var header = new byte[8];
        foreach (var (type, data) in chunks)
        {
            if (data is uint forged)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header, forged);
                Encoding.ASCII.GetBytes(type, header.AsSpan(4));
                ms.Write(header);
                ms.Write(new byte[64]);
            }
            else
            {
                PngTestFiles.WriteChunk(ms, type, (byte[])data);
            }
        }

        PngTestFiles.WriteChunk(ms, "IDAT", idat);
        PngTestFiles.WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] Zlib(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        return ms.ToArray();
    }

    private static byte[] Iccp(byte[] icc) => [.. "icc\0\0"u8, .. Zlib(icc)];

    // ImageMagick's layout: newline, name, newline, length, newline, hex digits.
    private static byte[] RawProfile(string name, byte[] payload)
    {
        string text = $"\n{name}\n{payload.Length,8}\n{Convert.ToHexString(payload).ToLowerInvariant()}\n";
        return [.. Encoding.Latin1.GetBytes($"Raw profile type {name}\0\0"), .. Zlib(Encoding.ASCII.GetBytes(text))];
    }

    /// <summary>A read-only stream that cannot seek or report its length (like a pipe).</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
