using System.IO.Compression;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Codecs;

/// <summary>Options for <see cref="PngOptimizer.Optimize"/>.</summary>
/// <param name="CleanInvisiblePixels">Allow setting the RGB of fully transparent pixels to 0 (visible pixels never change).</param>
/// <param name="KeepIcc">Embed the ICC profile (and only use colour types the profile is valid for).</param>
/// <param name="KeepExif">Embed EXIF and XMP.</param>
/// <param name="Effort">1..3: how many representations × filter strategies × deflate strategies are tried.</param>
public sealed record PngOptimizeOptions(bool CleanInvisiblePixels = true, bool KeepIcc = true, bool KeepExif = false, int Effort = 2);

/// <summary>
/// Built-in lossless PNG optimizer (the fallback when oxipng/ECT are unavailable). Applies exact reductions only —
/// 16→8 bits when every sample is v·257, alpha removal when opaque or expressible as a tRNS colour key, gray when
/// R = G = B, palette (+ tRNS) for ≤ 256 colours, 1/2/4-bit packing — then tries several filter and deflate
/// strategies at maximum zlib effort, in parallel, and keeps the smallest result.
/// </summary>
public static class PngOptimizer
{
    /// <summary>Returns the smallest PNG found for <paramref name="pixels"/>; decoding it yields the same visible pixels.</summary>
    /// <remarks>
    /// When <see cref="PngOptimizeOptions.CleanInvisiblePixels"/> is false every sample, invisible RGB included, is
    /// preserved exactly. <paramref name="pixels"/> is never modified.
    /// </remarks>
    public static byte[] Optimize(PixelBuffer pixels, ImageMetadata? metadata, PngOptimizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Effort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Effort, 3);

        PixelBuffer? owned = null;
        try
        {
            PixelBuffer work = pixels;
            if (options.CleanInvisiblePixels && HasDirtyInvisiblePixels(pixels))
            {
                owned = pixels.Clone();
                CleanInvisible(owned);
                work = owned;
            }

            if (work.Depth == SampleDepth.Sixteen && FitsEightBits(work))
            {
                var eight = work.ToDepth(SampleDepth.Eight); // exact: every sample is v·257
                owned?.Dispose();
                owned = eight;
                work = eight;
            }

            var kept = new ImageMetadata
            {
                Icc = options.KeepIcc ? metadata?.Icc : null,
                Exif = options.KeepExif ? metadata?.Exif : null,
                Xmp = options.KeepExif ? metadata?.Xmp : null,
                DpiX = metadata?.DpiX ?? 96,
                DpiY = metadata?.DpiY ?? 96,
            };
            var stats = PngPixelStats.Collect(work, collectColors: true);
            var layouts = CandidateLayouts(stats, work.Depth, kept.Icc, options.Effort);
            var (layout, idat) = FindSmallest(work, layouts, options.Effort);

            var output = new MemoryStream(idat.Length + 1024);
            output.Write(PngChunks.Signature);
            PngEncoder.WriteHeaderChunks(output, work, layout, kept, new PngWriteSettings(PngFilterStrategy.None, 9, ZLibCompressionStrategy.Default, WriteIcc: true, WriteExif: true));
            PngChunks.Write(output, PngChunks.Idat, idat);
            PngChunks.Write(output, PngChunks.Iend, []);
            return output.ToArray();
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// The exact representations worth trying, most promising first (gray, palette, truecolour); effort 1 keeps only
    /// the first, and EncodeFast's representation is always added. Colour profiles restrict the choice so a kept
    /// profile stays valid (RGB profiles exclude gray types; gray profiles keep gray images gray).
    /// </summary>
    private static List<PngLayout> CandidateLayouts(PngPixelStats s, SampleDepth depth, byte[]? icc, int effort)
    {
        bool grayAllowed = s.IsGray && !IccHeader.IsRgb(icc);
        bool colorAllowed = !(s.IsGray && IccHeader.IsGray(icc));
        bool wide = depth == SampleDepth.Sixteen;
        var candidates = new List<PngLayout>();

        if (grayAllowed)
        {
            bool keyed = !s.IsOpaque && s.TransparentKey is not null;
            if (s.IsOpaque || keyed)
            {
                int bits = wide ? 16 : s.GrayBitsNeeded;
                candidates.Add(Plan(s, depth, PngColorType.Gray, bits));
            }
            else
            {
                candidates.Add(Plan(s, depth, PngColorType.GrayAlpha, wide ? 16 : 8));
            }
        }

        if (s.Colors is { } colors && colorAllowed)
        {
            candidates.Add(Plan(s, depth, PngColorType.Palette, PngLayout.PaletteDepth(colors.Length)));
        }

        if (colorAllowed && (!grayAllowed || effort >= 3))
        {
            bool keyed = !s.IsOpaque && s.TransparentKey is not null;
            candidates.Add(Plan(s, depth, s.IsOpaque || keyed ? PngColorType.Rgb : PngColorType.Rgba, wide ? 16 : 8));
        }

        // Never empty here: a gray image always admits a gray type unless an RGB profile forbids it, and then colour types are allowed.
        if (effort == 1)
        {
            candidates.RemoveRange(1, candidates.Count - 1);
        }

        // EncodeFast's representation (alpha channel instead of a key, 8 bits for gray) is always tried with its Sub
        // filter, so the result is never larger than the fast encode.
        var fast = PngLayout.Plan(s, depth, null, null, interlace: false, allowGray: !IccHeader.IsRgb(icc));
        if (!candidates.Exists(c => c.ColorType == fast.ColorType && c.BitDepth == fast.BitDepth && c.TransparentKey is null))
        {
            candidates.Add(fast);
        }

        return candidates;
    }

    private static PngLayout Plan(PngPixelStats s, SampleDepth depth, PngColorType type, int bits) =>
        PngLayout.Plan(s, depth, type, bits, interlace: false, allowGray: true);

    private static (PngLayout Layout, byte[] Idat) FindSmallest(PixelBuffer px, List<PngLayout> layouts, int effort)
    {
        PngFilterStrategy[] filters = effort switch
        {
            1 => [PngFilterStrategy.None, PngFilterStrategy.Sub, PngFilterStrategy.MinSum],
            2 => [PngFilterStrategy.None, PngFilterStrategy.Sub, PngFilterStrategy.MinSum, PngFilterStrategy.Entropy, PngFilterStrategy.Paeth],
            _ => Enum.GetValues<PngFilterStrategy>(),
        };
        ZLibCompressionStrategy[] strategies = effort switch
        {
            1 => [ZLibCompressionStrategy.Default],
            2 => [ZLibCompressionStrategy.Default, ZLibCompressionStrategy.Filtered],
            _ => [ZLibCompressionStrategy.Default, ZLibCompressionStrategy.Filtered, ZLibCompressionStrategy.RunLengthEncoding],
        };

        var rasters = layouts.Select(l => new RasterRowSource(px, l)).ToArray();
        var trials = (from layoutIndex in Enumerable.Range(0, layouts.Count)
                      from filter in filters
                      from strategy in strategies
                      select (layoutIndex, filter, strategy)).ToArray();

        // Only the best result is retained while trials run; ties go to the earlier trial (deterministic output).
        var gate = new object();
        int bestIndex = -1;
        byte[] bestData = [];
        Parallel.For(0, trials.Length, i =>
        {
            var (layoutIndex, filter, strategy) = trials[i];
            var buffer = new MemoryStream();
            using (var z = new ZLibStream(buffer, new ZLibCompressionOptions { CompressionLevel = 9, CompressionStrategy = strategy }, leaveOpen: true))
            {
                PngEncoder.WriteFilteredRows(z, rasters[layoutIndex], px.Width, px.Height, layouts[layoutIndex], filter);
            }

            lock (gate)
            {
                if (bestIndex < 0 || buffer.Length < bestData.Length || (buffer.Length == bestData.Length && i < bestIndex))
                {
                    bestIndex = i;
                    bestData = buffer.ToArray();
                }
            }
        });

        return (layouts[trials[bestIndex].layoutIndex], bestData);
    }

    private static bool HasDirtyInvisiblePixels(PixelBuffer px)
    {
        for (int y = 0; y < px.Height; y++)
        {
            if (px.Depth == SampleDepth.Eight)
            {
                foreach (Rgba32 p in px.Row32(y))
                {
                    if (p.A == 0 && (p.R | p.G | p.B) != 0)
                    {
                        return true;
                    }
                }
            }
            else
            {
                foreach (Rgba64 p in px.Row64(y))
                {
                    if (p.A == 0 && (p.R | p.G | p.B) != 0)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void CleanInvisible(PixelBuffer px)
    {
        for (int y = 0; y < px.Height; y++)
        {
            if (px.Depth == SampleDepth.Eight)
            {
                foreach (ref Rgba32 p in px.Row32(y))
                {
                    if (p.A == 0)
                    {
                        p = default;
                    }
                }
            }
            else
            {
                foreach (ref Rgba64 p in px.Row64(y))
                {
                    if (p.A == 0)
                    {
                        p = default;
                    }
                }
            }
        }
    }

    private static bool FitsEightBits(PixelBuffer px)
    {
        for (int y = 0; y < px.Height; y++)
        {
            foreach (ushort v in System.Runtime.InteropServices.MemoryMarshal.Cast<Rgba64, ushort>(px.Row64(y)))
            {
                if ((v >> 8) != (v & 0xFF))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Raw scanlines packed once, shared read-only by all trials of one layout.</summary>
    private sealed class RasterRowSource : IPngRowSource
    {
        private readonly byte[] _raster;
        private readonly int _rowBytes;

        public RasterRowSource(PixelBuffer px, PngLayout layout)
        {
            _rowBytes = layout.RowBytes(px.Width);
            _raster = GC.AllocateUninitializedArray<byte>(checked(_rowBytes * px.Height));
            var packer = new PngRowPacker(px, layout);
            for (int y = 0; y < px.Height; y++)
            {
                packer.Pack(y, 0, 1, px.Width, _raster.AsSpan(y * _rowBytes, _rowBytes));
            }
        }

        public void GetRow(Adam7.Pass pass, int row, Span<byte> dst) => _raster.AsSpan(row * _rowBytes, _rowBytes).CopyTo(dst);
    }
}
