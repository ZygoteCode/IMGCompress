using System.Buffers.Binary;

namespace Carbonado.Core.Codecs;

/// <summary>
/// JPEG marker-segment access without decoding: reads EXIF, XMP and ICC from APPn segments, and rewrites the
/// metadata segments of a JPEG while copying its entropy-coded data byte for byte.
/// </summary>
internal static class JpegSegments
{
    private const byte Soi = 0xD8, Eoi = 0xD9, Sos = 0xDA, App0 = 0xE0, App1 = 0xE1, App2 = 0xE2, App14 = 0xEE;

    // Largest payloads that fit one segment (length field 65535 minus itself and the identifiers).
    private const int MaxExif = 65535 - 2 - 6;
    private const int MaxXmp = 65535 - 2 - 29;
    private const int IccChunk = 65535 - 2 - 14;

    private static ReadOnlySpan<byte> ExifId => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> XmpId => "http://ns.adobe.com/xap/1.0/\0"u8;

    private static ReadOnlySpan<byte> IccId => "ICC_PROFILE\0"u8;

    /// <summary>Metadata found in a JPEG's APPn segments.</summary>
    /// <param name="HasOtherMetadata">Comments or APPn segments other than JFIF, Adobe, EXIF, XMP and ICC (IPTC, MPF, …) are present.</param>
    public sealed record Metadata(byte[]? Exif, byte[]? Xmp, byte[]? Icc, bool HasOtherMetadata = false);

    /// <summary>Reads the metadata segments preceding the first scan. Returns empty metadata for non-JPEG data.</summary>
    public static Metadata Read(Stream stream)
    {
        byte[]? exif = null, xmp = null;
        var icc = new SortedDictionary<int, byte[]>();
        int iccCount = 0;
        bool other = false;
        Span<byte> header = stackalloc byte[4];
        if (stream.ReadAtLeast(header[..2], 2, throwOnEndOfStream: false) < 2 || header[0] != 0xFF || header[1] != Soi)
        {
            return new Metadata(null, null, null);
        }

        for (int segments = 0; segments < 4096; segments++)
        {
            int marker = NextMarker(stream);
            if (marker is < 0 or Sos or Eoi)
            {
                break;
            }

            if (IsStandalone(marker))
            {
                continue;
            }

            if (stream.ReadAtLeast(header[..2], 2, throwOnEndOfStream: false) < 2)
            {
                break;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(header) - 2;
            if (length < 0)
            {
                break;
            }

            if (marker is App1 or App2)
            {
                byte[] payload = new byte[length];
                if (stream.ReadAtLeast(payload, length, throwOnEndOfStream: false) < length)
                {
                    break;
                }

                ReadOnlySpan<byte> p = payload;
                if (marker == App1 && exif is null && p.StartsWith(ExifId))
                {
                    exif = p[ExifId.Length..].ToArray();
                }
                else if (marker == App1 && xmp is null && p.StartsWith(XmpId))
                {
                    xmp = p[XmpId.Length..].ToArray();
                }
                else if (marker == App2 && p.Length > IccId.Length + 2 && p.StartsWith(IccId))
                {
                    int sequence = p[IccId.Length], count = p[IccId.Length + 1];
                    iccCount = Math.Max(iccCount, count);
                    icc.TryAdd(sequence, p[(IccId.Length + 2)..].ToArray());
                }
                else
                {
                    other = true;
                }
            }
            else
            {
                other |= (marker is > App0 and <= 0xEF && marker != App14) || marker == 0xFE;
                stream.Seek(length, SeekOrigin.Current);
            }
        }

        return new Metadata(exif, xmp, AssembleIcc(icc, iccCount), other);
    }

    /// <summary>
    /// True when the image data reaches an EOI marker, walking every scan and inter-scan segment (progressive JPEGs
    /// included). A missing EOI is the signature of a truncated (half-downloaded) file, which WIC would decode silently.
    /// </summary>
    public static bool IsComplete(Stream stream)
    {
        stream.Position = 0;
        Span<byte> two = stackalloc byte[2];
        if (stream.ReadAtLeast(two, 2, throwOnEndOfStream: false) < 2 || two[0] != 0xFF || two[1] != Soi)
        {
            return false;
        }

        // Header segments up to the first SOS.
        for (int segments = 0; ; segments++)
        {
            int marker = NextMarker(stream);
            if (marker < 0 || marker == Eoi || segments > 4096)
            {
                return false;
            }

            if (IsStandalone(marker))
            {
                continue;
            }

            if (!SkipSegment(stream, two))
            {
                return false;
            }

            if (marker == Sos)
            {
                break;
            }
        }

        // Entropy-coded data: 0xFF 0x00 is a stuffed byte, RSTn and fill bytes continue the scan, EOI ends the image,
        // anything else is a segment between scans (DHT, DQT, DRI, SOS, …) that is skipped.
        var buffer = new byte[64 * 1024];
        bool afterFf = false;
        while (true)
        {
            long chunkStart = stream.Position;
            int n = stream.Read(buffer);
            if (n == 0)
            {
                return false;
            }

            for (int i = 0; i < n; i++)
            {
                byte b = buffer[i];
                if (!afterFf)
                {
                    afterFf = b == 0xFF;
                    continue;
                }

                afterFf = b == 0xFF;
                if (b is 0x00 or 0xFF or (>= 0xD0 and <= 0xD7))
                {
                    continue;
                }

                if (b == Eoi)
                {
                    return true;
                }

                stream.Position = chunkStart + i + 1;
                if (!SkipSegment(stream, two))
                {
                    return false;
                }

                break;
            }
        }
    }

    // Skips a length-prefixed segment payload; the stream is positioned at its length field.
    private static bool SkipSegment(Stream stream, Span<byte> scratch)
    {
        if (stream.ReadAtLeast(scratch, 2, throwOnEndOfStream: false) < 2)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(scratch);
        if (length < 2 || stream.Position + length - 2 > stream.Length)
        {
            return false;
        }

        stream.Seek(length - 2, SeekOrigin.Current);
        return true;
    }

    /// <summary>
    /// A copy of <paramref name="jpeg"/> whose EXIF/XMP/ICC segments are exactly the given ones (null = none). When
    /// <paramref name="keepOtherMetadata"/> is false, comments and every other APPn segment (except JFIF and Adobe,
    /// which affect decoding) are dropped, as is data after EOI. Entropy-coded data is copied unchanged.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a structurally valid JPEG.</exception>
    public static byte[] Rewrite(ReadOnlySpan<byte> jpeg, byte[]? exif, byte[]? xmp, byte[]? icc, bool keepOtherMetadata)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != Soi)
        {
            throw new InvalidDataException("Not a JPEG file.");
        }

        var headers = new List<(byte Marker, int Offset, int Length)>();
        int pos = 2;
        int scanStart = -1;
        while (pos < jpeg.Length)
        {
            int markerAt = pos;
            int marker = NextMarker(jpeg, ref pos);
            if (marker < 0)
            {
                throw new InvalidDataException("JPEG: the file ends before the image data.");
            }

            if (marker == Sos)
            {
                scanStart = markerAt;
                break;
            }

            if (IsStandalone(marker))
            {
                continue;
            }

            if (marker == Eoi || pos + 2 > jpeg.Length)
            {
                throw new InvalidDataException("JPEG: no image data.");
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[pos..]);
            if (length < 2 || pos + length > jpeg.Length)
            {
                throw new InvalidDataException("JPEG: malformed segment length.");
            }

            headers.Add(((byte)marker, markerAt, pos + length - markerAt));
            pos += length;
        }

        if (scanStart < 0)
        {
            throw new InvalidDataException("JPEG: no image data.");
        }

        int end = keepOtherMetadata ? jpeg.Length : FindEndOfImage(jpeg, scanStart);
        using var output = new MemoryStream(end + (exif?.Length ?? 0) + (xmp?.Length ?? 0) + (icc?.Length ?? 0) + 256);
        output.Write([0xFF, Soi]);

        // JFIF must stay first.
        foreach (var h in headers)
        {
            if (h.Marker == App0 && IsJfif(jpeg, h))
            {
                output.Write(jpeg.Slice(h.Offset, h.Length));
            }
        }

        if (exif is { Length: > 0 and <= MaxExif })
        {
            WriteSegment(output, App1, ExifId, exif);
        }

        if (xmp is { Length: > 0 and <= MaxXmp })
        {
            WriteSegment(output, App1, XmpId, xmp);
        }

        if (icc is { Length: > 0 })
        {
            int chunks = (icc.Length + IccChunk - 1) / IccChunk;
            if (chunks <= 255)
            {
                for (int i = 0; i < chunks; i++)
                {
                    ReadOnlySpan<byte> part = icc.AsSpan(i * IccChunk, Math.Min(IccChunk, icc.Length - (i * IccChunk)));
                    WriteSegment(output, App2, [.. IccId, (byte)(i + 1), (byte)chunks], part);
                }
            }
        }

        foreach (var h in headers)
        {
            ReadOnlySpan<byte> segment = jpeg.Slice(h.Offset, h.Length);
            bool replaced = (h.Marker == App1 && (Payload(segment).StartsWith(ExifId) || Payload(segment).StartsWith(XmpId)))
                || (h.Marker == App2 && Payload(segment).StartsWith(IccId))
                || (h.Marker == App0 && IsJfif(jpeg, h));
            bool metadata = (h.Marker is >= App0 and <= 0xEF && h.Marker != App14 && h.Marker != App0) || h.Marker == 0xFE;
            if (!replaced && (keepOtherMetadata || !metadata))
            {
                output.Write(segment);
            }
        }

        output.Write(jpeg[scanStart..end]);
        return output.ToArray();
    }

    private static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> segment) => segment[4..];

    private static bool IsJfif(ReadOnlySpan<byte> jpeg, (byte Marker, int Offset, int Length) h) =>
        h.Length >= 9 && (jpeg.Slice(h.Offset + 4, 5).SequenceEqual("JFIF\0"u8) || jpeg.Slice(h.Offset + 4, 5).SequenceEqual("JFXX\0"u8));

    private static void WriteSegment(Stream output, byte marker, ReadOnlySpan<byte> id, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[4];
        header[0] = 0xFF;
        header[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(2 + id.Length + data.Length));
        output.Write(header);
        output.Write(id);
        output.Write(data);
    }

    private static bool IsStandalone(int marker) => marker is (>= 0xD0 and <= 0xD7) or 0x01;

    private static byte[]? AssembleIcc(SortedDictionary<int, byte[]> chunks, int count)
    {
        if (chunks.Count == 0 || chunks.Count != count)
        {
            return null;
        }

        int expected = 1;
        foreach (int key in chunks.Keys)
        {
            if (key != expected++)
            {
                return null;
            }
        }

        return chunks.Values.SelectMany(c => c).ToArray();
    }

    // Next marker code from a stream positioned at a marker (fill bytes 0xFF are skipped); -1 at end of data.
    private static int NextMarker(Stream stream)
    {
        int b = stream.ReadByte();
        if (b != 0xFF)
        {
            return -1;
        }

        while ((b = stream.ReadByte()) == 0xFF)
        {
        }

        return b is < 0 or 0 ? -1 : b;
    }

    private static int NextMarker(ReadOnlySpan<byte> jpeg, ref int pos)
    {
        if (pos >= jpeg.Length || jpeg[pos] != 0xFF)
        {
            return -1;
        }

        while (pos < jpeg.Length && jpeg[pos] == 0xFF)
        {
            pos++;
        }

        return pos < jpeg.Length && jpeg[pos] != 0 ? jpeg[pos++] : -1;
    }

    /// <summary>Offset just past EOI, walking scans and inter-scan segments (progressive JPEGs); the data end when EOI is missing.</summary>
    private static int FindEndOfImage(ReadOnlySpan<byte> jpeg, int scanStart)
    {
        int pos = scanStart + 2;
        while (pos + 1 < jpeg.Length)
        {
            if (pos + 2 > jpeg.Length)
            {
                break;
            }

            // Segment header (SOS or an inter-scan marker such as DHT/DQT/DRI).
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[pos..]);
            pos += length;

            // Entropy-coded data: 0xFF is followed by 0x00 (stuffing) or RSTn inside a scan.
            while (pos + 1 < jpeg.Length)
            {
                if (jpeg[pos] != 0xFF)
                {
                    pos++;
                    continue;
                }

                byte next = jpeg[pos + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7 || next == 0xFF)
                {
                    pos += next == 0xFF ? 1 : 2;
                    continue;
                }

                if (next == Eoi)
                {
                    return pos + 2;
                }

                pos += 2;
                break;
            }
        }

        return jpeg.Length;
    }
}
