using System.Buffers.Binary;

namespace Carbonado.Core.Decoding;

/// <summary>Reads the logical screen and the first image descriptor of a GIF, and whether that image's data is complete.</summary>
internal static class GifStructure
{
    private const int MaxBlocks = 1 << 20;

    /// <param name="FirstImageComplete">The first image's LZW data ends with its block terminator (the file is not truncated inside it).</param>
    public sealed record Geometry(int ScreenWidth, int ScreenHeight, int Left, int Top, bool FirstImageComplete);

    /// <summary>The geometry of the first image, or null when the stream holds no image descriptor.</summary>
    public static Geometry? Read(Stream s)
    {
        s.Position = 0;
        Span<byte> header = stackalloc byte[13];
        if (s.ReadAtLeast(header, 13, throwOnEndOfStream: false) < 13)
        {
            return null;
        }

        int screenW = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]), screenH = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        SkipColorTable(s, header[10]);
        Span<byte> descriptor = stackalloc byte[9];
        for (int i = 0; i < MaxBlocks; i++)
        {
            switch (s.ReadByte())
            {
                case 0x21:
                    if (s.ReadByte() < 0 || !SkipSubBlocks(s))
                    {
                        return null;
                    }

                    break;

                case 0x2C:
                    if (s.ReadAtLeast(descriptor, 9, throwOnEndOfStream: false) < 9)
                    {
                        return null;
                    }

                    var geometry = new Geometry(
                        screenW,
                        screenH,
                        BinaryPrimitives.ReadUInt16LittleEndian(descriptor),
                        BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]),
                        false);
                    SkipColorTable(s, descriptor[8]);
                    bool complete = s.ReadByte() >= 0 && SkipSubBlocks(s);
                    return geometry with { FirstImageComplete = complete };

                default:
                    return null;
            }
        }

        return null;
    }

    private static void SkipColorTable(Stream s, byte flags)
    {
        if ((flags & 0x80) != 0)
        {
            s.Seek(3 << ((flags & 7) + 1), SeekOrigin.Current);
        }
    }

    private static bool SkipSubBlocks(Stream s)
    {
        for (int i = 0; i < MaxBlocks; i++)
        {
            int size = s.ReadByte();
            if (size <= 0)
            {
                return size == 0;
            }

            s.Seek(size, SeekOrigin.Current);
            if (s.Position > s.Length)
            {
                return false;
            }
        }

        return false;
    }
}
