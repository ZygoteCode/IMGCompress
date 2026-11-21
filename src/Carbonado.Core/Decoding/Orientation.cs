using Carbonado.Core.Imaging;

namespace Carbonado.Core.Decoding;

/// <summary>Applies EXIF orientations (1..8) to pixels, producing the upright image.</summary>
public static class Orientation
{
    private const int Tile = 64;

    /// <summary>
    /// The upright version of <paramref name="src"/> stored with EXIF <paramref name="orientation"/>.
    /// Returns <paramref name="src"/> itself for 1 (and for invalid values); otherwise a new buffer the caller owns.
    /// </summary>
    /// <remarks>
    /// 2 mirror horizontally · 3 rotate 180° · 4 mirror vertically · 5 transpose · 6 rotate 90° clockwise ·
    /// 7 transverse · 8 rotate 90° counter-clockwise. 5–8 swap width and height. Tiled and parallel.
    /// </remarks>
    public static PixelBuffer Apply(PixelBuffer src, ushort orientation)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (orientation is < 2 or > 8)
        {
            return src;
        }

        bool swap = orientation >= 5;
        var dst = PixelBuffer.Create(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.Depth, clear: false);
        if (src.Depth == SampleDepth.Eight)
        {
            Transform<uint>(src, dst, orientation);
        }
        else
        {
            Transform<ulong>(src, dst, orientation);
        }

        return dst;
    }

    private static unsafe void Transform<T>(PixelBuffer src, PixelBuffer dst, ushort orientation)
        where T : unmanaged
    {
        nint sp = src.Pointer, dp = dst.Pointer;
        int sw = src.Width, sh = src.Height, dw = dst.Width, dh = dst.Height;
        if (orientation <= 4)
        {
            Parallel.For(0, dh, y =>
            {
                int sy = orientation == 2 ? y : sh - 1 - y;
                var srcRow = new ReadOnlySpan<T>((T*)sp + ((long)sy * sw), sw);
                var dstRow = new Span<T>((T*)dp + ((long)y * dw), dw);
                srcRow.CopyTo(dstRow);
                if (orientation != 4)
                {
                    dstRow.Reverse();
                }
            });
            return;
        }

        int tilesY = (dh + Tile - 1) / Tile;
        Parallel.For(0, tilesY, ty =>
        {
            T* s = (T*)sp;
            T* d = (T*)dp;
            int y0 = ty * Tile, y1 = Math.Min(dh, y0 + Tile);
            for (int x0 = 0; x0 < dw; x0 += Tile)
            {
                int x1 = Math.Min(dw, x0 + Tile);
                for (int y = y0; y < y1; y++)
                {
                    T* row = d + ((long)y * dw);
                    for (int x = x0; x < x1; x++)
                    {
                        // Destination (x, y) → source (sx, sy).
                        int sx, sy;
                        switch (orientation)
                        {
                            case 5:
                                sx = y;
                                sy = x;
                                break;
                            case 6:
                                sx = y;
                                sy = sh - 1 - x;
                                break;
                            case 7:
                                sx = sw - 1 - y;
                                sy = sh - 1 - x;
                                break;
                            default:
                                sx = sw - 1 - y;
                                sy = x;
                                break;
                        }

                        row[x] = s[((long)sy * sw) + sx];
                    }
                }
            }
        });
    }
}
