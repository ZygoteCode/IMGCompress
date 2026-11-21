using System.Buffers.Binary;
using System.Text;

namespace Carbonado.Core.Tests.Codecs;

/// <summary>Builds real (parseable) ICC v2 matrix/TRC display profiles for tests.</summary>
public static class TestIcc
{
    /// <summary>Display P3 primaries (D50-adapted) with a pure 2.2 gamma: a valid RGB profile that is not sRGB.</summary>
    public static byte[] DisplayP3Gamma22() => MatrixTrc(
        "Carbonado test P3 gamma 2.2",
        (0.5151, 0.2412, -0.0011),
        (0.2920, 0.6922, 0.0419),
        (0.1571, 0.0666, 0.7841),
        2.2);

    private static byte[] MatrixTrc(string description, (double X, double Y, double Z) r, (double X, double Y, double Z) g, (double X, double Y, double Z) b, double gamma)
    {
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", Desc(description)),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("rXYZ", Xyz(r.X, r.Y, r.Z)),
            ("gXYZ", Xyz(g.X, g.Y, g.Z)),
            ("bXYZ", Xyz(b.X, b.Y, b.Z)),
            ("rTRC", Curve(gamma)),
            ("gTRC", Curve(gamma)),
            ("bTRC", Curve(gamma)),
            ("cprt", Text("No copyright, test data")),
        };

        int offset = 128 + 4 + (12 * tags.Count);
        using var data = new MemoryStream();
        var table = new List<(string Signature, int Offset, int Length)>();
        foreach (var (signature, bytes) in tags)
        {
            table.Add((signature, offset + (int)data.Length, bytes.Length));
            data.Write(bytes);
            while (data.Length % 4 != 0)
            {
                data.WriteByte(0);
            }
        }

        var profile = new byte[offset + data.Length];
        var span = profile.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x02100000);
        Ascii("mntr").CopyTo(span[12..]);
        Ascii("RGB ").CopyTo(span[16..]);
        Ascii("XYZ ").CopyTo(span[20..]);
        BinaryPrimitives.WriteUInt16BigEndian(span[24..], 2026);
        BinaryPrimitives.WriteUInt16BigEndian(span[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[28..], 1);
        Ascii("acsp").CopyTo(span[36..]);
        Xyz(0.9642, 1.0, 0.8249).AsSpan(8).CopyTo(span[68..]);
        BinaryPrimitives.WriteUInt32BigEndian(span[128..], (uint)tags.Count);
        for (int i = 0; i < table.Count; i++)
        {
            int e = 132 + (i * 12);
            Ascii(table[i].Signature).CopyTo(span[e..]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(e + 4)..], (uint)table[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(span[(e + 8)..], (uint)table[i].Length);
        }

        data.ToArray().CopyTo(profile, offset);
        return profile;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var d = new byte[20];
        Ascii("XYZ ").CopyTo(d, 0);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8), S15Fixed16(x));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12), S15Fixed16(y));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16), S15Fixed16(z));
        return d;
    }

    private static byte[] Curve(double gamma)
    {
        var d = new byte[14];
        Ascii("curv").CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(12), (ushort)Math.Round(gamma * 256));
        return d;
    }

    // textDescriptionType: ASCII part, empty Unicode part, empty ScriptCode part (67 bytes).
    private static byte[] Desc(string text)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(text + "\0");
        var d = new byte[12 + ascii.Length + 4 + 4 + 2 + 1 + 67];
        Ascii("desc").CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), (uint)ascii.Length);
        ascii.CopyTo(d, 12);
        return d;
    }

    private static byte[] Text(string text)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(text + "\0");
        var d = new byte[8 + ascii.Length];
        Ascii("text").CopyTo(d, 0);
        ascii.CopyTo(d, 8);
        return d;
    }

    private static int S15Fixed16(double v) => (int)Math.Round(v * 65536);

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
}
