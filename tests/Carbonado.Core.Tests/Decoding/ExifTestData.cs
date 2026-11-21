using System.Buffers.Binary;

namespace Carbonado.Core.Tests.Decoding;

/// <summary>Builds small TIFF/EXIF blobs for tests: IFD0 (Make, Orientation, GPS pointer) + a GPS IFD.</summary>
public static class ExifTestData
{
    /// <summary>A minimal well-formed XMP packet (WIC rejects packets without the xpacket wrapper and RDF body).</summary>
    public static readonly byte[] Xmp = System.Text.Encoding.UTF8.GetBytes(
        "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:format>image/jpeg</dc:format></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");

    /// <summary>The GPSLatitude rational payload (45/1, 27/1, 3000/100), easy to search for.</summary>
    public static readonly byte[] Latitude = Rationals(false, (45, 1), (27, 1), (3000, 100));

    public static byte[] Build(bool bigEndian, ushort? orientation, bool withGps)
    {
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>
        {
            (0x010F, 2, 6, "Canon\0"u8.ToArray()),
        };
        if (orientation is { } o)
        {
            entries.Add((0x0112, 3, 1, U16(bigEndian, o)));
        }

        if (withGps)
        {
            entries.Add((0x8825, 4, 1, new byte[4]));
        }

        byte[] ifd0 = Ifd(bigEndian, entries, 8, 0);
        if (withGps)
        {
            uint gpsOffset = (uint)(8 + ifd0.Length);
            entries[^1] = (0x8825, 4, 1, U32(bigEndian, gpsOffset));
            ifd0 = Ifd(bigEndian, entries, 8, 0);
            byte[] lat = Rationals(bigEndian, (45, 1), (27, 1), (3000, 100));
            byte[] gps = Ifd(bigEndian, [(0x0001, 2, 2, "N\0"u8.ToArray()), (0x0002, 5, 3, lat), (0x0003, 2, 2, "E\0"u8.ToArray()), (0x0004, 5, 3, lat)], gpsOffset, 0);
            return [.. Header(bigEndian), .. ifd0, .. gps];
        }

        return [.. Header(bigEndian), .. ifd0];
    }

    private static byte[] Header(bool be) => be ? [0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8] : [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0];

    private static byte[] Ifd(bool be, List<(ushort Tag, ushort Type, uint Count, byte[] Value)> entries, uint offset, uint next)
    {
        var head = new List<byte>(U16(be, (ushort)entries.Count));
        var data = new List<byte>();
        uint dataStart = offset + 2 + ((uint)entries.Count * 12) + 4;
        foreach (var (tag, type, count, value) in entries)
        {
            head.AddRange(U16(be, tag));
            head.AddRange(U16(be, type));
            head.AddRange(U32(be, count));
            if (value.Length <= 4)
            {
                head.AddRange(value.Concat(new byte[4 - value.Length]));
            }
            else
            {
                head.AddRange(U32(be, dataStart + (uint)data.Count));
                data.AddRange(value);
                if (data.Count % 2 == 1)
                {
                    data.Add(0);
                }
            }
        }

        head.AddRange(U32(be, next));
        return [.. head, .. data];
    }

    private static byte[] Rationals(bool be, params (uint N, uint D)[] values) =>
        values.SelectMany(v => U32(be, v.N).Concat(U32(be, v.D))).ToArray();

    private static byte[] U16(bool be, ushort v)
    {
        var b = new byte[2];
        if (be)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        }

        return b;
    }

    private static byte[] U32(bool be, uint v)
    {
        var b = new byte[4];
        if (be)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        }

        return b;
    }
}
