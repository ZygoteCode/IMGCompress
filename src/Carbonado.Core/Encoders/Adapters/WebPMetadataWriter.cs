using System.Buffers.Binary;

namespace Carbonado.Core.Encoders.Adapters;

/// <summary>
/// Sets the metadata chunks (ICCP, EXIF, XMP) of a WebP file, converting a simple file to the extended (VP8X)
/// layout when needed. The Windows build of cwebp can only copy ICC profiles, so metadata is applied here instead.
/// </summary>
internal static class WebPMetadataWriter
{
    private const byte IccFlag = 0x20, AlphaFlag = 0x10, ExifFlag = 0x08, XmpFlag = 0x04, AnimationFlag = 0x02;

    /// <summary>A copy of <paramref name="webp"/> whose metadata is exactly the given chunks (null = none).</summary>
    /// <param name="width">Canvas width, used when the input has no VP8X chunk.</param>
    /// <param name="height">Canvas height, used when the input has no VP8X chunk.</param>
    /// <exception cref="InvalidDataException">Not a WebP file.</exception>
    public static byte[] Apply(ReadOnlySpan<byte> webp, byte[]? icc, byte[]? exif, byte[]? xmp, int width, int height)
    {
        if (webp.Length < 20 || !webp.StartsWith("RIFF"u8) || !webp.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            throw new InvalidDataException("Not a WebP file.");
        }

        var image = new List<(int Offset, int Length)>();
        bool alpha = false, animation = false, hadVp8x = false;
        int canvasW = width, canvasH = height;
        int pos = 12;
        while (pos + 8 <= webp.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(webp[(pos + 4)..]);
            long total = 8L + size + (size & 1);
            if (pos + 8 + size > webp.Length)
            {
                throw new InvalidDataException("WebP: truncated chunk.");
            }

            ReadOnlySpan<byte> id = webp.Slice(pos, 4);
            ReadOnlySpan<byte> payload = webp.Slice(pos + 8, (int)size);
            if (id.SequenceEqual("VP8X"u8) && size >= 10)
            {
                hadVp8x = true;
                alpha |= (payload[0] & AlphaFlag) != 0;
                animation |= (payload[0] & AnimationFlag) != 0;
                canvasW = 1 + (payload[4] | (payload[5] << 8) | (payload[6] << 16));
                canvasH = 1 + (payload[7] | (payload[8] << 8) | (payload[9] << 16));
            }
            else if (!(id.SequenceEqual("ICCP"u8) || id.SequenceEqual("EXIF"u8) || id.SequenceEqual("XMP "u8)))
            {
                alpha |= id.SequenceEqual("ALPH"u8) || (id.SequenceEqual("VP8L"u8) && size >= 5 && ((payload[4] >> 4) & 1) != 0);
                animation |= id.SequenceEqual("ANIM"u8);
                image.Add((pos, (int)Math.Min(total, webp.Length - pos)));
            }

            pos += (int)Math.Min(total, webp.Length - pos);
        }

        bool hasMetadata = icc is { Length: > 0 } || exif is { Length: > 0 } || xmp is { Length: > 0 };
        if (!hasMetadata && !hadVp8x)
        {
            return webp.ToArray();
        }

        using var output = new MemoryStream(webp.Length + (icc?.Length ?? 0) + (exif?.Length ?? 0) + (xmp?.Length ?? 0) + 64);
        output.Write("RIFF\0\0\0\0WEBP"u8);
        if (hasMetadata || alpha || animation)
        {
            byte flags = (byte)((icc is { Length: > 0 } ? IccFlag : 0) | (alpha ? AlphaFlag : 0) | (exif is { Length: > 0 } ? ExifFlag : 0)
                | (xmp is { Length: > 0 } ? XmpFlag : 0) | (animation ? AnimationFlag : 0));
            Span<byte> vp8x = stackalloc byte[10];
            vp8x.Clear();
            vp8x[0] = flags;
            Write24(vp8x[4..], canvasW - 1);
            Write24(vp8x[7..], canvasH - 1);
            WriteChunk(output, "VP8X"u8, vp8x);
        }

        if (icc is { Length: > 0 })
        {
            WriteChunk(output, "ICCP"u8, icc);
        }

        foreach (var (offset, length) in image)
        {
            output.Write(webp.Slice(offset, length));
        }

        if (exif is { Length: > 0 })
        {
            WriteChunk(output, "EXIF"u8, exif);
        }

        if (xmp is { Length: > 0 })
        {
            WriteChunk(output, "XMP "u8, xmp);
        }

        byte[] result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8));
        return result;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> id, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        id.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)payload.Length);
        output.Write(header);
        output.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            output.WriteByte(0);
        }
    }

    private static void Write24(Span<byte> d, int v)
    {
        d[0] = (byte)v;
        d[1] = (byte)(v >> 8);
        d[2] = (byte)(v >> 16);
    }
}
