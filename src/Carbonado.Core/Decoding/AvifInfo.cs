using System.Buffers.Binary;

namespace Carbonado.Core.Decoding;

/// <summary>Reads the coded bit depth of an AVIF from its ISOBMFF boxes (<c>pixi</c>, else <c>av1C</c>) without decoding.</summary>
internal static class AvifInfo
{
    private const int MaxBoxes = 4096;

    /// <summary>Bits per channel (8, 10 or 12); 8 when the boxes cannot be found.</summary>
    public static int BitDepth(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 14);
        int budget = MaxBoxes;
        return Find(s, 0, s.Length, depth: 0, ref budget) ?? 8;
    }

    // Walks boxes in [start, end); descends into meta (full box), iprp and ipco.
    private static int? Find(Stream s, long start, long end, int depth, ref int budget)
    {
        Span<byte> header = stackalloc byte[16];
        int? fromAv1C = null;
        long pos = start;
        while (pos + 8 <= end && budget-- > 0)
        {
            s.Position = pos;
            if (s.ReadAtLeast(header[..8], 8, throwOnEndOfStream: false) < 8)
            {
                break;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            ReadOnlySpan<byte> type = header.Slice(4, 4);
            int headerSize = 8;
            if (size == 1)
            {
                if (s.ReadAtLeast(header[8..16], 8, throwOnEndOfStream: false) < 8)
                {
                    break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }

            if (size < headerSize || pos + size > end)
            {
                break;
            }

            long body = pos + headerSize;
            if (type.SequenceEqual("pixi"u8) && size >= headerSize + 5)
            {
                // Full box: version/flags (4), channel count, then bits per channel.
                s.Position = body + 4;
                int channels = s.ReadByte(), max = 0;
                for (int c = 0; c < channels; c++)
                {
                    max = Math.Max(max, s.ReadByte());
                }

                if (max > 0)
                {
                    return max;
                }
            }
            else if (type.SequenceEqual("av1C"u8) && size >= headerSize + 4)
            {
                s.Position = body + 2;
                int flags = s.ReadByte();
                bool high = (flags & 0x40) != 0, twelve = (flags & 0x20) != 0;
                fromAv1C ??= high ? (twelve ? 12 : 10) : 8;
            }
            else if (depth < 4 && (type.SequenceEqual("meta"u8) || type.SequenceEqual("iprp"u8) || type.SequenceEqual("ipco"u8)))
            {
                long childStart = type.SequenceEqual("meta"u8) ? body + 4 : body;
                if (Find(s, childStart, pos + size, depth + 1, ref budget) is { } found)
                {
                    return found;
                }
            }

            pos += size;
        }

        return fromAv1C;
    }
}
