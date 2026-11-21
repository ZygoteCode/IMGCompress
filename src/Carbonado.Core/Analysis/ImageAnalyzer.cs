using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Analysis;

/// <summary>Computes the <see cref="ImageAnalysis"/> content facts that drive encoder planning.</summary>
public static class ImageAnalyzer
{
    private const int UniqueColorCap = 65536;
    private const int RowsPerChunk = 64;

    /// <summary>
    /// Analyses <paramref name="pixels"/>: alpha usage, grayscale (R = G = B everywhere), distinct RGBA values (exact up
    /// to 65 536, else -1), palette fit (≤ 256 values and no meaningful 16-bit precision), meaningful 16-bit precision,
    /// edge density (share of pixels whose alpha-premultiplied luma gradient |∇| exceeds 32 on the 8-bit scale, forward
    /// differences), flat ratio (share of pixels equal to their left neighbour, over pixels that have one) and the content
    /// kind derived from them: Graphic when ≤ 4096 colors, or flat ratio ≥ 0.55 with at most 65 536 colors (a smooth
    /// render with more colors is never Graphic); otherwise Photo when more than 20 000 colors and flat ratio &lt; 0.35;
    /// Mixed otherwise.
    /// </summary>
    public static ImageAnalysis Analyze(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        int w = pixels.Width, h = pixels.Height;
        int chunks = (h + RowsPerChunk - 1) / RowsPerChunk;
        var parts = new ChunkStats[chunks];
        Parallel.For(0, chunks, i => parts[i] = AnalyzeRows(pixels, i * RowsPerChunk, Math.Min(h, (i + 1) * RowsPerChunk)));

        var total = default(ChunkStats);
        foreach (var p in parts)
        {
            total = total.Merge(p);
        }

        int unique = pixels.Depth == SampleDepth.Eight ? CountUnique<uint>(pixels) : CountUnique<ulong>(pixels);
        AlphaUsage alpha = total.AnyPartialAlpha ? AlphaUsage.Translucent : total.AnyTransparent ? AlphaUsage.Binary : AlphaUsage.Opaque;
        long withLeft = (long)(w - 1) * h;
        double flatRatio = withLeft > 0 ? total.Flat / (double)withLeft : 0;
        double edgeDensity = total.Edges / (double)pixels.PixelCount;
        return new ImageAnalysis(
            w,
            h,
            alpha,
            !total.AnyColor,
            unique,
            FitsPalette: unique is >= 0 and <= 256 && !total.Has16BitPrecision,
            total.Has16BitPrecision,
            Classify(unique, flatRatio),
            edgeDensity,
            flatRatio);
    }

    /// <param name="unique">Distinct colors, or -1 when more than 65 536.</param>
    /// <param name="flatRatio">Share of pixels equal to their left neighbour.</param>
    private static ContentKind Classify(int unique, double flatRatio)
    {
        bool capped = unique < 0;
        if (unique is >= 0 and <= 4096 || (flatRatio >= 0.55 && !capped))
        {
            return ContentKind.Graphic;
        }

        return (capped || unique > 20_000) && flatRatio < 0.35 ? ContentKind.Photo : ContentKind.Mixed;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ChunkStats AnalyzeRows(PixelBuffer px, int y0, int y1)
    {
        int w = px.Width;
        var stats = default(ChunkStats);
        int[] luma = new int[w], below = new int[w];
        Luma(px, y0, luma);
        for (int y = y0; y < y1; y++)
        {
            bool hasBelow = y + 1 < px.Height;
            if (hasBelow)
            {
                Luma(px, y + 1, below);
            }

            if (px.Depth == SampleDepth.Eight)
            {
                stats.AddRow(px.Row32(y));
            }
            else
            {
                stats.AddRow(px.Row64(y));
            }

            for (int x = 0; x < w; x++)
            {
                int gx = x + 1 < w ? luma[x + 1] - luma[x] : 0;
                int gy = hasBelow ? below[x] - luma[x] : 0;
                if ((gx * gx) + (gy * gy) > 32 * 32)
                {
                    stats.Edges++;
                }
            }

            (luma, below) = (below, luma);
        }

        return stats;
    }

    /// <summary>Alpha-premultiplied BT.601 luma of row <paramref name="y"/> on the 8-bit scale.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Luma(PixelBuffer px, int y, int[] dst)
    {
        if (px.Depth == SampleDepth.Eight)
        {
            ReadOnlySpan<Rgba32> row = px.Row32(y);
            for (int x = 0; x < row.Length; x++)
            {
                Rgba32 p = row[x];
                dst[x] = Premultiplied(p.R, p.G, p.B, p.A);
            }

            return;
        }

        ReadOnlySpan<Rgba64> deep = px.Row64(y);
        for (int x = 0; x < deep.Length; x++)
        {
            Rgba64 p = deep[x];
            dst[x] = Premultiplied(To8(p.R), To8(p.G), To8(p.B), To8(p.A));
        }
    }

    private static int Premultiplied(int r, int g, int b, int a) => (((((77 * r) + (150 * g) + (29 * b) + 128) >> 8) * a) + 127) / 255;

    private static int To8(ushort v) => ((v * 255) + 32895) >> 16;

    /// <summary>Distinct pixel values, or -1 as soon as there are more than <see cref="UniqueColorCap"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int CountUnique<T>(PixelBuffer px)
        where T : unmanaged, IBinaryInteger<T>
    {
        var set = new ColorSet<T>();
        T previous = default;
        bool started = false;
        for (int y = 0; y < px.Height; y++)
        {
            ReadOnlySpan<T> row = MemoryMarshal.Cast<byte, T>(px.RowBytes(y));
            foreach (T c in row)
            {
                if (started && c == previous)
                {
                    continue;
                }

                started = true;
                previous = c;
                if (set.Add(c) && set.Count > UniqueColorCap)
                {
                    return -1;
                }
            }
        }

        return set.Count;
    }

    /// <summary>Open-addressing hash set sized for <see cref="UniqueColorCap"/> + 1 values (load factor ≤ 0.5).</summary>
    private sealed class ColorSet<T>
        where T : unmanaged, IBinaryInteger<T>
    {
        private const int Bits = 17;
        private readonly T[] _slots = new T[1 << Bits]; // zero marks an empty slot; the value zero is tracked separately
        private bool _hasZero;

        public int Count { get; private set; }

        public bool Add(T value)
        {
            if (value == T.Zero)
            {
                if (_hasZero)
                {
                    return false;
                }

                _hasZero = true;
                Count++;
                return true;
            }

            int mask = _slots.Length - 1;
            int i = Slot(value);
            while (true)
            {
                T s = _slots[i];
                if (s == value)
                {
                    return false;
                }

                if (s == T.Zero)
                {
                    _slots[i] = value;
                    Count++;
                    return true;
                }

                i = (i + 1) & mask;
            }
        }

        private static int Slot(T value)
        {
            ulong v = ulong.CreateTruncating(value);
            return (int)((v * 0x9E3779B97F4A7C15ul) >> (64 - Bits));
        }
    }

    private struct ChunkStats
    {
        public bool AnyTransparent; // some alpha below maximum
        public bool AnyPartialAlpha; // some alpha strictly between 0 and maximum
        public bool AnyColor; // some pixel with R, G, B not all equal
        public bool Has16BitPrecision;
        public long Flat;
        public long Edges;

        public readonly ChunkStats Merge(ChunkStats o) => new()
        {
            AnyTransparent = AnyTransparent || o.AnyTransparent,
            AnyPartialAlpha = AnyPartialAlpha || o.AnyPartialAlpha,
            AnyColor = AnyColor || o.AnyColor,
            Has16BitPrecision = Has16BitPrecision || o.Has16BitPrecision,
            Flat = Flat + o.Flat,
            Edges = Edges + o.Edges,
        };

        public void AddRow(ReadOnlySpan<Rgba32> row)
        {
            for (int x = 0; x < row.Length; x++)
            {
                Rgba32 p = row[x];
                AnyTransparent |= p.A != 255;
                AnyPartialAlpha |= p.A is not (0 or 255);
                AnyColor |= p.R != p.G || p.G != p.B;
                if (x > 0 && p == row[x - 1])
                {
                    Flat++;
                }
            }
        }

        public void AddRow(ReadOnlySpan<Rgba64> row)
        {
            for (int x = 0; x < row.Length; x++)
            {
                Rgba64 p = row[x];
                AnyTransparent |= p.A != 65535;
                AnyPartialAlpha |= p.A is not (0 or 65535);
                AnyColor |= p.R != p.G || p.G != p.B;
                Has16BitPrecision |= p.R % 257 != 0 || p.G % 257 != 0 || p.B % 257 != 0 || p.A % 257 != 0;
                if (x > 0 && p == row[x - 1])
                {
                    Flat++;
                }
            }
        }
    }
}
