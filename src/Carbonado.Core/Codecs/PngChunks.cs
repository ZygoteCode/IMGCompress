using System.Buffers.Binary;
using System.Text;

namespace Carbonado.Core.Codecs;

/// <summary>PNG signature, chunk type codes (big-endian four-character codes) and chunk framing.</summary>
internal static class PngChunks
{
    public const uint Ihdr = 0x49484452;
    public const uint Plte = 0x504C5445;
    public const uint Idat = 0x49444154;
    public const uint Iend = 0x49454E44;
    public const uint Trns = 0x74524E53;
    public const uint Iccp = 0x69434350;
    public const uint Exif = 0x65584966;
    public const uint Phys = 0x70485973;
    public const uint Sbit = 0x73424954;
    public const uint Itxt = 0x69545874;
    public const uint Text = 0x74455874;
    public const uint Ztxt = 0x7A545874;
    public const uint Actl = 0x6163544C;

    /// <summary>The iTXt keyword that carries an XMP packet.</summary>
    public const string XmpKeyword = "XML:com.adobe.xmp";

    /// <summary>Largest legal chunk payload (2^31 − 1).</summary>
    public const uint MaxLength = int.MaxValue;

    public static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Critical chunks have an upper-case first letter (bit 5 of the first byte clear).</summary>
    public static bool IsCritical(uint type) => (type & 0x2000_0000) == 0;

    /// <summary>The four-character name of a chunk type, for messages.</summary>
    public static string Name(uint type)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, type);
        foreach (byte c in b)
        {
            if (c is < 0x20 or > 0x7E)
            {
                return $"0x{type:X8}";
            }
        }

        return Encoding.ASCII.GetString(b);
    }

    /// <summary>Writes one chunk: length, type, payload and the CRC-32 of type + payload.</summary>
    public static void Write(Stream output, uint type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], type);
        output.Write(header);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Append(Crc32.Compute(header[4..]), data));
        output.Write(crc);
    }
}
