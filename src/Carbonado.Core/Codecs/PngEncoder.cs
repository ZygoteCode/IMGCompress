using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Codecs;

/// <summary>
/// Managed PNG encoder: every colour type and bit depth, palette + tRNS, the five filters plus adaptive strategies,
/// Adam7, and zlib streams written as IDAT chunks while compressing (bounded memory).
/// </summary>
public static class PngEncoder
{
    // Fast encodes of at least this much filtered data are deflated in parallel segments (pigz-style).
    private const long ParallelThreshold = 4L << 20;
    private const int SegmentTargetBytes = 1 << 20;

    /// <summary>Encodes <paramref name="pixels"/> exactly as PNG (no information is ever lost).</summary>
    /// <param name="output">Destination; written sequentially.</param>
    /// <param name="pixels">Source pixels (8- or 16-bit RGBA).</param>
    /// <param name="metadata">ICC/EXIF/XMP/DPI to embed according to <paramref name="options"/>; may be null.</param>
    /// <param name="options">Representation and effort.</param>
    /// <exception cref="ArgumentException">An explicit colour type/bit depth cannot represent the pixels exactly.</exception>
    public static void Encode(Stream output, PixelBuffer pixels, ImageMetadata? metadata, PngEncodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(options);
        bool keepRgbProfile = options.WriteIcc && IccHeader.IsRgb(metadata?.Icc);
        var stats = PngPixelStats.Collect(pixels, collectColors: options.ColorType == PngColorType.Palette);
        var layout = PngLayout.Plan(stats, pixels.Depth, options.ColorType, options.BitDepth, options.Interlace, allowGray: !keepRgbProfile);
        var settings = new PngWriteSettings(options.Filter, ZlibLevel(options.Level), ZLibCompressionStrategy.Default, options.WriteIcc, options.WriteExif);
        Write(output, pixels, layout, metadata, settings);
    }

    /// <summary>
    /// Fast exact encode for intermediates: smallest exact non-palette type, Sub filter, fastest deflate (parallel for
    /// large images). Embeds every piece of <paramref name="metadata"/> (ICC, EXIF, XMP, DPI) so encoders reading the
    /// file see the source metadata.
    /// </summary>
    public static void EncodeFast(Stream output, PixelBuffer pixels, ImageMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(pixels);
        var stats = PngPixelStats.Collect(pixels, collectColors: false);
        var layout = PngLayout.Plan(stats, pixels.Depth, null, null, interlace: false, allowGray: !IccHeader.IsRgb(metadata?.Icc));
        var settings = new PngWriteSettings(PngFilterStrategy.Sub, 1, ZLibCompressionStrategy.Default, WriteIcc: true, WriteExif: true, AllowParallel: true);
        Write(output, pixels, layout, metadata, settings);
    }

    /// <summary>Writes a complete PNG for an already planned layout.</summary>
    internal static void Write(Stream output, PixelBuffer px, PngLayout layout, ImageMetadata? metadata, PngWriteSettings settings)
    {
        output.Write(PngChunks.Signature);
        WriteHeaderChunks(output, px, layout, metadata, settings);
        var idat = new PngIdatStream(output, settings.IdatChunkLimit);
        if (!(settings.AllowParallel && !layout.Interlace && TryCompressParallel(idat, px, layout, settings)))
        {
            using (var z = new ZLibStream(idat, new ZLibCompressionOptions { CompressionLevel = settings.ZlibLevel, CompressionStrategy = settings.Strategy }, leaveOpen: true))
            {
                WriteFilteredRows(z, new PackingRowSource(px, layout), px.Width, px.Height, layout, settings.Filter);
            }
        }

        idat.Complete();
        PngChunks.Write(output, PngChunks.Iend, []);
    }

    /// <summary>Filters every scanline (all Adam7 passes when interlaced) and writes filter byte + residuals.</summary>
    internal static void WriteFilteredRows(Stream z, IPngRowSource source, int width, int height, PngLayout layout, PngFilterStrategy strategy)
    {
        int bpp = layout.FilterDistance;
        int maxRowBytes = layout.RowBytes(width);
        int candidates = strategy is PngFilterStrategy.MinSum or PngFilterStrategy.Entropy ? 5 : 1;
        byte[] cur = ArrayPool<byte>.Shared.Rent(maxRowBytes);
        byte[] prev = ArrayPool<byte>.Shared.Rent(maxRowBytes);
        var outputs = new byte[candidates][];
        for (int i = 0; i < candidates; i++)
        {
            outputs[i] = ArrayPool<byte>.Shared.Rent(maxRowBytes + 1);
        }

        int[] histogram = new int[256];
        try
        {
            foreach (var pass in Adam7.Passes(width, height, layout.Interlace))
            {
                int rowBytes = layout.RowBytes(pass.Width);
                Array.Clear(prev, 0, rowBytes);
                for (int r = 0; r < pass.Height; r++)
                {
                    Span<byte> raw = cur.AsSpan(0, rowBytes);
                    source.GetRow(pass, r, raw);
                    ReadOnlySpan<byte> above = prev.AsSpan(0, rowBytes);
                    byte[] chosen = candidates == 1
                        ? FilterRow(FixedFilter(strategy), raw, above, outputs[0], bpp)
                        : ChooseFilter(strategy, raw, above, outputs, bpp, histogram);
                    z.Write(chosen, 0, rowBytes + 1);
                    (cur, prev) = (prev, cur);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cur);
            ArrayPool<byte>.Shared.Return(prev);
            foreach (byte[] o in outputs)
            {
                ArrayPool<byte>.Shared.Return(o);
            }
        }
    }

    internal static int ZlibLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => 0,
        CompressionLevel.Fastest => 1,
        CompressionLevel.Optimal => 6,
        CompressionLevel.SmallestSize => 9,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    private static int FixedFilter(PngFilterStrategy s) => s switch
    {
        PngFilterStrategy.None => PngFilters.None,
        PngFilterStrategy.Sub => PngFilters.Sub,
        PngFilterStrategy.Up => PngFilters.Up,
        PngFilterStrategy.Average => PngFilters.Average,
        PngFilterStrategy.Paeth => PngFilters.Paeth,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    private static byte[] FilterRow(int type, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> above, byte[] output, int bpp)
    {
        output[0] = (byte)type;
        PngFilters.Apply(type, raw, above, output.AsSpan(1, raw.Length), bpp);
        return output;
    }

    private static byte[] ChooseFilter(PngFilterStrategy strategy, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> above, byte[][] outputs, int bpp, int[] histogram)
    {
        int best = 0;
        double bestCost = double.MaxValue;
        for (int type = 0; type < 5; type++)
        {
            FilterRow(type, raw, above, outputs[type], bpp);
            ReadOnlySpan<byte> residuals = outputs[type].AsSpan(1, raw.Length);
            double cost = strategy == PngFilterStrategy.MinSum ? PngFilters.AbsSum(residuals) : PngFilters.EntropyBits(residuals, histogram);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = type;
            }
        }

        return outputs[best];
    }

    internal static void WriteHeaderChunks(Stream output, PixelBuffer px, PngLayout layout, ImageMetadata? metadata, PngWriteSettings settings)
    {
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)px.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)px.Height);
        ihdr[8] = (byte)layout.BitDepth;
        ihdr[9] = (byte)layout.ColorType;
        ihdr[12] = (byte)(layout.Interlace ? 1 : 0);
        PngChunks.Write(output, PngChunks.Ihdr, ihdr);

        WriteColorAndDensity(output, metadata, layout.IsGrayType, settings.WriteIcc);
        if (layout.Palette is { } palette)
        {
            var plte = new byte[palette.Length * 3];
            int translucent = 0;
            for (int i = 0; i < palette.Length; i++)
            {
                plte[i * 3] = palette[i].R;
                plte[(i * 3) + 1] = palette[i].G;
                plte[(i * 3) + 2] = palette[i].B;
                if (palette[i].A != 255)
                {
                    translucent = i + 1;
                }
            }

            PngChunks.Write(output, PngChunks.Plte, plte);
            if (translucent > 0)
            {
                var trns = new byte[translucent];
                for (int i = 0; i < translucent; i++)
                {
                    trns[i] = palette[i].A;
                }

                PngChunks.Write(output, PngChunks.Trns, trns);
            }
        }
        else if (layout.TransparentKey is { } key)
        {
            Span<byte> trns = stackalloc byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(trns, key.R);
            BinaryPrimitives.WriteUInt16BigEndian(trns[2..], key.G);
            BinaryPrimitives.WriteUInt16BigEndian(trns[4..], key.B);
            PngChunks.Write(output, PngChunks.Trns, layout.ColorType == PngColorType.Gray ? trns[..2] : trns);
        }

        if (settings.WriteExif)
        {
            WriteDescriptive(output, metadata);
        }
    }

    /// <summary>
    /// A copy of an existing PNG whose metadata is exactly <paramref name="metadata"/>: colour chunks (iCCP, sRGB,
    /// gAMA, cHRM, cICP), pHYs, eXIf and text chunks are removed, then the given ICC (when valid for the colour type),
    /// DPI, EXIF and XMP are inserted after IHDR. Image data and every other chunk are copied unchanged, unless
    /// <paramref name="dropUnknownChunks"/> also removes the ancillary chunks that do not describe the image
    /// (everything but tRNS, bKGD, sBIT, hIST and the APNG chunks): files from elsewhere can hide metadata there.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a PNG.</exception>
    internal static byte[] ReplaceMetadata(ReadOnlySpan<byte> png, ImageMetadata? metadata, bool dropUnknownChunks = false)
    {
        if (png.Length < 33 || !png.StartsWith(PngChunks.Signature) || BinaryPrimitives.ReadUInt32BigEndian(png[12..]) != PngChunks.Ihdr)
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        bool gray = (PngColorType)png[25] is PngColorType.Gray or PngColorType.GrayAlpha;
        using var output = new MemoryStream(png.Length + (metadata?.Icc?.Length ?? 0) + (metadata?.Exif?.Length ?? 0) + (metadata?.Xmp?.Length ?? 0) + 256);
        output.Write(png[..33]);
        WriteColorAndDensity(output, metadata, gray, writeIcc: true);
        WriteDescriptive(output, metadata);
        int pos = 33;
        while (pos + 12 <= png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png[pos..]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(png[(pos + 4)..]);
            long size = 12L + length;
            if (pos + size > png.Length)
            {
                throw new InvalidDataException("PNG: truncated chunk.");
            }

            if (!IsMetadataChunk(type) && (!dropUnknownChunks || IsImageChunk(type)))
            {
                output.Write(png.Slice(pos, (int)size));
            }

            pos += (int)size;
            if (type == PngChunks.Iend)
            {
                break;
            }
        }

        return output.ToArray();
    }

    // Critical chunks, transparency, background, significant bits, histogram, and animation control/frames.
    private static bool IsImageChunk(uint type) => PngChunks.IsCritical(type) || type is PngChunks.Trns or PngChunks.Sbit or PngChunks.Actl
        or 0x624B4744 /* bKGD */ or 0x68495354 /* hIST */ or 0x6663544C /* fcTL */ or 0x66644154 /* fdAT */;

    private static bool IsMetadataChunk(uint type) => type is PngChunks.Iccp or PngChunks.Phys or PngChunks.Exif or PngChunks.Itxt
        or PngChunks.Text or PngChunks.Ztxt or 0x73524742 /* sRGB */ or 0x67414D41 /* gAMA */ or 0x6348524D /* cHRM */
        or 0x63494350 /* cICP */ or 0x74494D45 /* tIME */;

    // iCCP (only when the profile matches gray/colour data) and pHYs (non-default DPI).
    private static void WriteColorAndDensity(Stream output, ImageMetadata? metadata, bool grayData, bool writeIcc)
    {
        if (writeIcc && metadata?.Icc is { Length: > 0 } icc && (grayData ? IccHeader.IsGray(icc) : IccHeader.IsRgb(icc)))
        {
            WriteIccp(output, icc);
        }

        if (metadata is { } m && double.IsFinite(m.DpiX) && double.IsFinite(m.DpiY) && m.DpiX > 0 && m.DpiY > 0 && (m.DpiX != 96 || m.DpiY != 96))
        {
            Span<byte> phys = stackalloc byte[9];
            BinaryPrimitives.WriteUInt32BigEndian(phys, (uint)Math.Round(m.DpiX / 0.0254));
            BinaryPrimitives.WriteUInt32BigEndian(phys[4..], (uint)Math.Round(m.DpiY / 0.0254));
            phys[8] = 1;
            PngChunks.Write(output, PngChunks.Phys, phys);
        }
    }

    // eXIf (valid TIFF only) and XMP as an uncompressed iTXt.
    private static void WriteDescriptive(Stream output, ImageMetadata? metadata)
    {
        if (metadata?.Exif is { Length: >= 8 } exif && (exif.AsSpan().StartsWith("II*\0"u8) || exif.AsSpan().StartsWith("MM\0*"u8)))
        {
            PngChunks.Write(output, PngChunks.Exif, exif);
        }

        if (metadata?.Xmp is { Length: > 0 } xmp)
        {
            // iTXt: keyword, NUL, compression flag 0, method 0, empty language tag + NUL, empty translation + NUL, text.
            byte[] keyword = Encoding.Latin1.GetBytes(PngChunks.XmpKeyword);
            var itxt = new byte[keyword.Length + 5 + xmp.Length];
            keyword.CopyTo(itxt, 0);
            xmp.CopyTo(itxt, keyword.Length + 5);
            PngChunks.Write(output, PngChunks.Itxt, itxt);
        }
    }

    private static void WriteIccp(Stream output, byte[] icc)
    {
        var data = new MemoryStream(icc.Length / 2);
        data.Write("ICC Profile\0\0"u8);
        using (var z = new ZLibStream(data, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(icc);
        }

        PngChunks.Write(output, PngChunks.Iccp, data.GetBuffer().AsSpan(0, (int)data.Length));
    }

    /// <summary>
    /// Parallel deflate: rows are split into ~1 MiB segments filtered with Sub (no dependency on the row above),
    /// each segment is deflated independently and ended with a sync flush (byte-aligned, not final), and the raw
    /// deflate streams are concatenated inside one zlib wrapper whose Adler-32 is combined from the segments.
    /// </summary>
    internal static bool TryCompressParallel(PngIdatStream idat, PixelBuffer px, PngLayout layout, PngWriteSettings settings)
    {
        if (settings.Filter != PngFilterStrategy.Sub || Environment.ProcessorCount < 2)
        {
            return false;
        }

        int rowBytes = layout.RowBytes(px.Width);
        long total = (long)(rowBytes + 1) * px.Height;
        if (total < ParallelThreshold)
        {
            return false;
        }

        int rowsPerSegment = Math.Max(1, SegmentTargetBytes / (rowBytes + 1));
        int segments = (px.Height + rowsPerSegment - 1) / rowsPerSegment;
        var compressed = new byte[segments][];
        var adlers = new uint[segments];
        var lengths = new long[segments];
        int failed = 0;
        var options = new ZLibCompressionOptions { CompressionLevel = settings.ZlibLevel, CompressionStrategy = settings.Strategy };
        Parallel.For(0, segments, () => new PackingRowSource(px, layout), (s, _, source) =>
        {
            int y0 = s * rowsPerSegment, y1 = Math.Min(px.Height, y0 + rowsPerSegment);
            int size = (y1 - y0) * (rowBytes + 1);
            byte[] filtered = ArrayPool<byte>.Shared.Rent(size);
            byte[] raw = ArrayPool<byte>.Shared.Rent(rowBytes);
            try
            {
                var pass = new Adam7.Pass(0, 0, 1, 1, px.Width, px.Height);
                for (int y = y0, o = 0; y < y1; y++, o += rowBytes + 1)
                {
                    source.GetRow(pass, y, raw.AsSpan(0, rowBytes));
                    filtered[o] = PngFilters.Sub;
                    PngFilters.Apply(PngFilters.Sub, raw.AsSpan(0, rowBytes), default, filtered.AsSpan(o + 1, rowBytes), layout.FilterDistance);
                }

                adlers[s] = Adler32.Compute(filtered.AsSpan(0, size));
                lengths[s] = size;
                var ms = new MemoryStream(size / 2);
                var deflate = new DeflateStream(ms, options, leaveOpen: true);
                deflate.Write(filtered, 0, size);
                if (s < segments - 1)
                {
                    deflate.Flush();
                    byte[] bytes = ms.ToArray();
                    if (!bytes.AsSpan().EndsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFF, 0xFF]))
                    {
                        Interlocked.Exchange(ref failed, 1);
                    }

                    compressed[s] = bytes;
                    deflate.Dispose();
                }
                else
                {
                    deflate.Dispose();
                    compressed[s] = ms.ToArray();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(filtered);
                ArrayPool<byte>.Shared.Return(raw);
            }

            return source;
        }, _ => { });

        if (failed != 0)
        {
            return false;
        }

        uint adler = adlers[0];
        for (int s = 1; s < segments; s++)
        {
            adler = Adler32.Combine(adler, adlers[s], lengths[s]);
        }

        // CMF 0x78 (deflate, 32 KiB window), FLG 0x01 (fastest, no dictionary, check bits).
        idat.Write([0x78, 0x01]);
        foreach (byte[] segment in compressed)
        {
            idat.Write(segment);
        }

        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, adler);
        idat.Write(trailer);
        return true;
    }
}

/// <summary>Encoder settings beyond the layout.</summary>
/// <param name="IdatChunkLimit">Largest IDAT payload; the compressed stream is split into chunks of at most this size.</param>
internal sealed record PngWriteSettings(
    PngFilterStrategy Filter,
    int ZlibLevel,
    ZLibCompressionStrategy Strategy,
    bool WriteIcc,
    bool WriteExif,
    bool AllowParallel = false,
    int IdatChunkLimit = 1 << 22);

/// <summary>Supplies raw (unfiltered) scanlines.</summary>
internal interface IPngRowSource
{
    /// <summary>Writes row <paramref name="row"/> of <paramref name="pass"/> into <paramref name="dst"/>.</summary>
    void GetRow(Adam7.Pass pass, int row, Span<byte> dst);
}

/// <summary>Packs rows from a <see cref="PixelBuffer"/> on demand.</summary>
internal sealed class PackingRowSource(PixelBuffer px, PngLayout layout) : IPngRowSource
{
    private readonly PngRowPacker _packer = new(px, layout);

    public void GetRow(Adam7.Pass pass, int row, Span<byte> dst) =>
        _packer.Pack(pass.YStart + (row * pass.YStep), pass.XStart, pass.XStep, pass.Width, dst);
}

/// <summary>Adam7 interlacing geometry.</summary>
internal static class Adam7
{
    /// <summary>A reduced image: pixels (XStart + i·XStep, YStart + j·YStep).</summary>
    public readonly record struct Pass(int XStart, int YStart, int XStep, int YStep, int Width, int Height);

    private static readonly (int X0, int Y0, int Dx, int Dy)[] s_grid =
        [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    /// <summary>The non-empty passes (a single full pass when not interlaced).</summary>
    public static IEnumerable<Pass> Passes(int width, int height, bool interlace)
    {
        if (!interlace)
        {
            yield return new Pass(0, 0, 1, 1, width, height);
            yield break;
        }

        foreach (var (x0, y0, dx, dy) in s_grid)
        {
            int w = (width - x0 + dx - 1) / dx, h = (height - y0 + dy - 1) / dy;
            if (w > 0 && h > 0)
            {
                yield return new Pass(x0, y0, dx, dy, w, h);
            }
        }
    }
}

/// <summary>
/// Write-only stream that frames everything written to it as IDAT chunks of at most a given payload size.
/// <see cref="Flush"/> is a no-op so that deflate flushes do not create tiny chunks; call <see cref="Complete"/> at the end.
/// </summary>
internal sealed class PngIdatStream(Stream output, int chunkLimit) : Stream
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(chunkLimit, 64 * 1024));
    private int _count;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int capacity = Math.Min(_buffer.Length, chunkLimit);
            if (_count == capacity)
            {
                if (capacity < chunkLimit)
                {
                    byte[] bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(chunkLimit, _buffer.Length * 2L));
                    _buffer.AsSpan(0, _count).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = bigger;
                }
                else
                {
                    EmitChunk();
                }

                continue;
            }

            int n = Math.Min(data.Length, capacity - _count);
            data[..n].CopyTo(_buffer.AsSpan(_count));
            _count += n;
            data = data[n..];
        }
    }

    /// <summary>Writes the buffered remainder as the last IDAT chunk and releases the buffer.</summary>
    public void Complete()
    {
        if (_count > 0)
        {
            EmitChunk();
        }

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void EmitChunk()
    {
        PngChunks.Write(output, PngChunks.Idat, _buffer.AsSpan(0, _count));
        _count = 0;
    }
}
