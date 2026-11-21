using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Carbonado.Core.Codecs;

/// <summary>
/// CRC-32 (ISO-HDLC / zlib / PNG polynomial 0xEDB88320, reflected), computed with the slice-by-8 algorithm.
/// Values are "finalized" CRCs, compatible with zlib's <c>crc32()</c>: <c>Append(Compute(a), b) == Compute(a ‖ b)</c>.
/// </summary>
public static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;

    // 8 tables of 256 entries: table k maps a byte to its CRC contribution k bytes further down the stream.
    private static readonly uint[] s_tables = CreateTables();

    /// <summary>CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>Continues the CRC-32 <paramref name="crc"/> (of preceding data) over <paramref name="data"/>.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        ref uint t = ref MemoryMarshal.GetArrayDataReference(s_tables);
        ref byte p = ref MemoryMarshal.GetReference(data);
        nint length = data.Length;
        nint i = 0;
        for (; i + 8 <= length; i += 8)
        {
            uint one = ReadLittleEndian(ref Unsafe.Add(ref p, i)) ^ c;
            uint two = ReadLittleEndian(ref Unsafe.Add(ref p, i + 4));
            c = Unsafe.Add(ref t, (7 * 256) + (int)(one & 0xFF))
                ^ Unsafe.Add(ref t, (6 * 256) + (int)((one >> 8) & 0xFF))
                ^ Unsafe.Add(ref t, (5 * 256) + (int)((one >> 16) & 0xFF))
                ^ Unsafe.Add(ref t, (4 * 256) + (int)(one >> 24))
                ^ Unsafe.Add(ref t, (3 * 256) + (int)(two & 0xFF))
                ^ Unsafe.Add(ref t, (2 * 256) + (int)((two >> 8) & 0xFF))
                ^ Unsafe.Add(ref t, 256 + (int)((two >> 16) & 0xFF))
                ^ Unsafe.Add(ref t, (int)(two >> 24));
        }

        for (; i < length; i++)
        {
            c = Unsafe.Add(ref t, (int)((c ^ Unsafe.Add(ref p, i)) & 0xFF)) ^ (c >> 8);
        }

        return ~c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadLittleEndian(ref byte source)
    {
        uint value = Unsafe.ReadUnaligned<uint>(ref source);
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
    }

    private static uint[] CreateTables()
    {
        var tables = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            }

            tables[n] = c;
        }

        for (int k = 1; k < 8; k++)
        {
            for (int n = 0; n < 256; n++)
            {
                uint prev = tables[((k - 1) * 256) + n];
                tables[(k * 256) + n] = (prev >> 8) ^ tables[prev & 0xFF];
            }
        }

        return tables;
    }
}
