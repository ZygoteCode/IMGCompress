using System.Buffers.Binary;

namespace Carbonado.Core.Decoding;

/// <summary>
/// Minimal, defensive EXIF (TIFF structure) reader/editor: orientation and GPS removal. Edits never move existing
/// data, so every offset inside the blob stays valid.
/// </summary>
public static class ExifReader
{
    private const ushort OrientationTag = 0x0112;
    private const ushort GpsPointerTag = 0x8825;
    private const int EntrySize = 12;

    /// <summary>The orientation (1..8) stored in IFD0; 1 when absent, invalid or the data is not TIFF.</summary>
    public static ushort ReadOrientation(ReadOnlySpan<byte> tiff)
    {
        if (!Tiff.TryOpen(tiff, out var t) || !t.TryFindEntry(t.Ifd0, OrientationTag, out int entry))
        {
            return 1;
        }

        uint value = t.ReadScalar(entry);
        return value is >= 1 and <= 8 ? (ushort)value : (ushort)1;
    }

    /// <summary>True when IFD0 references a GPS IFD.</summary>
    public static bool HasGps(ReadOnlySpan<byte> tiff) =>
        Tiff.TryOpen(tiff, out var t) && t.TryFindEntry(t.Ifd0, GpsPointerTag, out _);

    /// <summary>
    /// A copy of <paramref name="tiff"/> without location data: the GPS IFD and all its out-of-line values are zeroed
    /// and the pointer to it is removed from IFD0. The size and every other offset are unchanged.
    /// </summary>
    /// <exception cref="InvalidDataException">The structure is too damaged to guarantee the GPS data is gone (drop the EXIF instead).</exception>
    public static byte[] StripGps(byte[] tiff)
    {
        ArgumentNullException.ThrowIfNull(tiff);
        byte[] copy = tiff.ToArray();
        if (!Tiff.TryOpen(copy, out var t))
        {
            throw new InvalidDataException("EXIF: not a valid TIFF structure.");
        }

        if (!t.TryFindEntry(t.Ifd0, GpsPointerTag, out int pointer))
        {
            return copy;
        }

        uint gps = t.ReadScalar(pointer);
        if (!t.TryIfdBounds(gps, out int gpsCount))
        {
            throw new InvalidDataException("EXIF: the GPS IFD pointer is invalid.");
        }

        for (int i = 0; i < gpsCount; i++)
        {
            int entry = (int)gps + 2 + (i * EntrySize);
            if (t.OutOfLineRange(entry) is { } range)
            {
                copy.AsSpan(range.Offset, range.Length).Clear();
            }
        }

        copy.AsSpan((int)gps, 2 + (gpsCount * EntrySize) + 4).Clear();
        t.RemoveEntry(copy, t.Ifd0, pointer);
        return copy;
    }

    /// <summary>A copy of <paramref name="tiff"/> whose IFD0 orientation is <paramref name="value"/> (inserting the tag if needed).</summary>
    /// <exception cref="InvalidDataException">The data is not a valid TIFF structure.</exception>
    public static byte[] WithOrientation(byte[] tiff, ushort value)
    {
        ArgumentNullException.ThrowIfNull(tiff);
        ArgumentOutOfRangeException.ThrowIfLessThan(value, (ushort)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, (ushort)8);
        byte[] copy = tiff.ToArray();
        if (!Tiff.TryOpen(copy, out var t))
        {
            throw new InvalidDataException("EXIF: not a valid TIFF structure.");
        }

        if (t.TryFindEntry(t.Ifd0, OrientationTag, out int entry))
        {
            t.WriteShortValue(copy, entry, value);
            return copy;
        }

        return value == 1 ? copy : t.AppendIfd0WithShort(copy, OrientationTag, value);
    }

    /// <summary>The smallest EXIF blob carrying only an orientation (little-endian TIFF, one IFD0 entry).</summary>
    public static byte[] CreateMinimal(ushort orientation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(orientation, (ushort)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(orientation, (ushort)8);
        var tiff = new byte[8 + 2 + EntrySize + 4];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), OrientationTag);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        return tiff;
    }

    /// <summary>A bounds-checked view of a TIFF blob.</summary>
    private readonly ref struct Tiff
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly bool _bigEndian;

        private Tiff(ReadOnlySpan<byte> data, bool bigEndian, uint ifd0)
        {
            _data = data;
            _bigEndian = bigEndian;
            Ifd0 = ifd0;
        }

        public uint Ifd0 { get; }

        public static bool TryOpen(ReadOnlySpan<byte> data, out Tiff tiff)
        {
            tiff = default;
            if (data.Length < 8)
            {
                return false;
            }

            bool be;
            if (data.StartsWith("II*\0"u8))
            {
                be = false;
            }
            else if (data.StartsWith("MM\0*"u8))
            {
                be = true;
            }
            else
            {
                return false;
            }

            uint ifd0 = be ? BinaryPrimitives.ReadUInt32BigEndian(data[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            var candidate = new Tiff(data, be, ifd0);
            if (!candidate.TryIfdBounds(ifd0, out _))
            {
                return false;
            }

            tiff = candidate;
            return true;
        }

        /// <summary>Validates that an IFD (count, entries, next pointer) lies inside the blob.</summary>
        public bool TryIfdBounds(uint offset, out int count)
        {
            count = 0;
            if (offset < 8 || offset > (uint)_data.Length - 2)
            {
                return false;
            }

            count = U16((int)offset);
            return (long)offset + 2 + ((long)count * EntrySize) + 4 <= _data.Length;
        }

        public bool TryFindEntry(uint ifd, ushort tag, out int entry)
        {
            entry = -1;
            if (!TryIfdBounds(ifd, out int count))
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                int e = (int)ifd + 2 + (i * EntrySize);
                if (U16(e) == tag)
                {
                    entry = e;
                    return true;
                }
            }

            return false;
        }

        /// <summary>The first value of a SHORT or LONG entry (count ≥ 1).</summary>
        public uint ReadScalar(int entry) => U16(entry + 2) switch
        {
            3 => U16(entry + 8),
            4 or 13 => U32(entry + 8),
            _ => 0,
        };

        /// <summary>Where an entry's values live when they do not fit in the 4-byte value field; null when inline or invalid.</summary>
        public (int Offset, int Length)? OutOfLineRange(int entry)
        {
            int size = U16(entry + 2) switch
            {
                1 or 2 or 6 or 7 => 1,
                3 or 8 => 2,
                4 or 9 or 11 or 13 => 4,
                5 or 10 or 12 => 8,
                _ => 0,
            };
            long bytes = (long)size * U32(entry + 4);
            if (bytes <= 4)
            {
                return null;
            }

            long offset = U32(entry + 8);
            return offset + bytes <= _data.Length ? ((int)offset, (int)bytes) : null;
        }

        public void WriteShortValue(byte[] target, int entry, ushort value)
        {
            ushort type = U16(entry + 2);
            Span<byte> field = target.AsSpan(entry + 8, 4);
            field.Clear();
            if (type == 4)
            {
                WriteU32(field, value);
            }
            else
            {
                WriteU16(target.AsSpan(entry + 2, 2), 3);
                WriteU16(field, value);
            }
        }

        /// <summary>Removes an entry from an IFD in place by shifting the following entries (and the next-IFD pointer) up.</summary>
        public void RemoveEntry(byte[] target, uint ifd, int entry)
        {
            int count = U16((int)ifd);
            int end = (int)ifd + 2 + (count * EntrySize) + 4;
            target.AsSpan(entry + EntrySize, end - entry - EntrySize).CopyTo(target.AsSpan(entry));
            target.AsSpan(end - EntrySize, EntrySize).Clear();
            WriteU16(target.AsSpan((int)ifd, 2), (ushort)(count - 1));
        }

        /// <summary>Appends a copy of IFD0 with one more SHORT entry (tags kept sorted) and points the header at it.</summary>
        public byte[] AppendIfd0WithShort(byte[] source, ushort tag, ushort value)
        {
            int count = U16((int)Ifd0);
            int start = source.Length + (source.Length & 1);
            var result = new byte[start + 2 + ((count + 1) * EntrySize) + 4];
            source.CopyTo(result, 0);
            var span = result.AsSpan();
            WriteU16(span[start..], (ushort)(count + 1));
            int written = 0;
            bool inserted = false;
            for (int i = 0; i < count; i++)
            {
                int e = (int)Ifd0 + 2 + (i * EntrySize);
                if (!inserted && U16(e) > tag)
                {
                    WriteShortEntry(span.Slice(start + 2 + (written++ * EntrySize), EntrySize), tag, value);
                    inserted = true;
                }

                source.AsSpan(e, EntrySize).CopyTo(span[(start + 2 + (written++ * EntrySize))..]);
            }

            if (!inserted)
            {
                WriteShortEntry(span.Slice(start + 2 + (written++ * EntrySize), EntrySize), tag, value);
            }

            source.AsSpan((int)Ifd0 + 2 + (count * EntrySize), 4).CopyTo(span[(start + 2 + (written * EntrySize))..]);
            WriteU32(span[4..], (uint)start);
            return result;
        }

        private void WriteShortEntry(Span<byte> e, ushort tag, ushort value)
        {
            WriteU16(e, tag);
            WriteU16(e[2..], 3);
            WriteU32(e[4..], 1);
            e[8..].Clear();
            WriteU16(e[8..], value);
        }

        private ushort U16(int offset) =>
            _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(_data[offset..]) : BinaryPrimitives.ReadUInt16LittleEndian(_data[offset..]);

        private uint U32(int offset) =>
            _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(_data[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(_data[offset..]);

        private void WriteU16(Span<byte> d, ushort v)
        {
            if (_bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(d, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(d, v);
            }
        }

        private void WriteU32(Span<byte> d, uint v)
        {
            if (_bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(d, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(d, v);
            }
        }
    }
}
