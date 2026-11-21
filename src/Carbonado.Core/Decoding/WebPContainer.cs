using System.Buffers.Binary;

namespace Carbonado.Core.Decoding;

/// <summary>
/// Reads the RIFF structure of a WebP file: metadata chunks (ICCP/EXIF/XMP), the canvas, and — for animations —
/// the first frame repackaged as a still WebP that dwebp can decode (dwebp and ffmpeg reject animations).
/// </summary>
internal static class WebPContainer
{
    private const int MaxChunks = 1 << 16;
    private const int MaxMetadata = 64 << 20;

    /// <summary>The first animation frame as a standalone still WebP, and where it sits on the canvas.</summary>
    public sealed record Frame(int X, int Y, int Width, int Height, byte[] StillWebP);

    /// <param name="Frames">Number of ANMF frames (0 for a still image).</param>
    public sealed record Info(int Frames, int CanvasWidth, int CanvasHeight, byte[]? Icc, byte[]? Exif, byte[]? Xmp, Frame? FirstFrame);

    /// <exception cref="InvalidDataException">Not a RIFF/WEBP file.</exception>
    public static Info Read(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 14);
        Span<byte> header = stackalloc byte[12];
        if (s.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12 || !header.StartsWith("RIFF"u8) || !header[8..].SequenceEqual("WEBP"u8))
        {
            throw new InvalidDataException("Not a WebP file.");
        }

        int frames = 0, canvasW = 0, canvasH = 0;
        byte[]? icc = null, exif = null, xmp = null;
        Frame? first = null;
        Span<byte> chunk = stackalloc byte[8];
        for (int i = 0; i < MaxChunks && s.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8; i++)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long next = s.Position + size + (size & 1);
            ReadOnlySpan<byte> id = chunk[..4];
            if (size <= MaxMetadata)
            {
                if (id.SequenceEqual("VP8X"u8) && size >= 10)
                {
                    byte[] vp8x = ReadPayload(s, size);
                    canvasW = 1 + (vp8x[4] | (vp8x[5] << 8) | (vp8x[6] << 16));
                    canvasH = 1 + (vp8x[7] | (vp8x[8] << 8) | (vp8x[9] << 16));
                }
                else if (id.SequenceEqual("ICCP"u8))
                {
                    icc ??= ReadPayload(s, size);
                }
                else if (id.SequenceEqual("EXIF"u8))
                {
                    byte[] e = ReadPayload(s, size);
                    exif ??= e.AsSpan().StartsWith("Exif\0\0"u8) ? e[6..] : e;
                }
                else if (id.SequenceEqual("XMP "u8))
                {
                    xmp ??= ReadPayload(s, size);
                }
                else if (id.SequenceEqual("ANMF"u8) && frames++ == 0 && size >= 16)
                {
                    first = ParseFrame(ReadPayload(s, size));
                }
            }
            else if (id.SequenceEqual("ANMF"u8))
            {
                frames++;
            }

            s.Position = next;
        }

        return new Info(frames, canvasW, canvasH, icc, exif, xmp, first);
    }

    private static byte[] ReadPayload(Stream s, uint size)
    {
        // A chunk size beyond the end of the file is a truncation: refused before allocating.
        if (s.Length - s.Position < size)
        {
            throw new InvalidDataException("WebP: the file is truncated.");
        }

        var data = new byte[size];
        if (s.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) < data.Length)
        {
            throw new InvalidDataException("WebP: the file is truncated.");
        }

        return data;
    }

    // ANMF payload: X/2, Y/2, width−1, height−1, duration (24-bit LE each), flags, then the frame's chunks.
    private static Frame? ParseFrame(byte[] anmf)
    {
        int x = 2 * U24(anmf, 0), y = 2 * U24(anmf, 3), w = 1 + U24(anmf, 6), h = 1 + U24(anmf, 9);
        ReadOnlySpan<byte> alpha = default, vp8 = default, vp8l = default;
        int pos = 16;
        while (pos + 8 <= anmf.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(anmf.AsSpan(pos + 4));
            if (pos + 8 + size > anmf.Length)
            {
                break;
            }

            ReadOnlySpan<byte> whole = anmf.AsSpan(pos, 8 + (int)size);
            ReadOnlySpan<byte> id = whole[..4];
            if (id.SequenceEqual("ALPH"u8))
            {
                alpha = whole;
            }
            else if (id.SequenceEqual("VP8 "u8))
            {
                vp8 = whole;
            }
            else if (id.SequenceEqual("VP8L"u8))
            {
                vp8l = whole;
            }

            pos += 8 + (int)size + (int)(size & 1);
        }

        byte[]? still = !vp8l.IsEmpty ? Riff(vp8l)
            : vp8.IsEmpty ? null
            : alpha.IsEmpty ? Riff(vp8)
            : Riff(Vp8x(w, h), alpha, vp8);
        return still is null ? null : new Frame(x, y, w, h, still);
    }

    private static byte[] Vp8x(int width, int height)
    {
        var chunk = new byte[18];
        "VP8X"u8.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), 10);
        chunk[8] = 0x10; // alpha present
        Write24(chunk, 12, width - 1);
        Write24(chunk, 15, height - 1);
        return chunk;
    }

    private static byte[] Riff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b = default, ReadOnlySpan<byte> c = default)
    {
        int body = 4 + Padded(a) + Padded(b) + Padded(c);
        var file = new byte[8 + body];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)body);
        "WEBP"u8.CopyTo(file.AsSpan(8));
        int pos = 12;
        a.CopyTo(file.AsSpan(pos));
        pos += Padded(a);
        b.CopyTo(file.AsSpan(pos));
        pos += Padded(b);
        c.CopyTo(file.AsSpan(pos));
        return file;

        static int Padded(ReadOnlySpan<byte> chunk) => chunk.Length + (chunk.Length & 1);
    }

    private static int U24(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16);

    private static void Write24(byte[] d, int o, int v)
    {
        d[o] = (byte)v;
        d[o + 1] = (byte)(v >> 8);
        d[o + 2] = (byte)(v >> 16);
    }
}
