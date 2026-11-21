using System.Buffers.Binary;
using System.Text;

namespace Carbonado.Core.Tests.Imaging;

/// <summary>Builds minimal, valid ICC v4 matrix/TRC profiles for tests.</summary>
internal static class IccTestProfiles
{
    /// <summary>The sRGB tone curve as an ICC parametric curve (type 3).</summary>
    public static byte[] SrgbCurve => Para(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045);

    /// <summary>Display P3: P3 primaries (D50-adapted colorants of Apple's profile) with the sRGB tone curve.</summary>
    public static byte[] DisplayP3 => Rgb([0.515121, 0.241196, -0.001053], [0.291977, 0.692245, 0.041885], [0.157104, 0.066574, 0.784073], SrgbCurve);

    /// <summary>sRGB IEC 61966-2.1 colorants with the sRGB tone curve.</summary>
    public static byte[] Srgb => Rgb([0.436066, 0.222488, 0.013916], [0.385147, 0.716873, 0.097076], [0.143066, 0.060608, 0.714096], SrgbCurve);

    /// <summary>A 'para' curve tag of the given function type and parameters.</summary>
    public static byte[] Para(int type, params double[] parameters)
    {
        var tag = new byte[12 + (4 * parameters.Length)];
        Encoding.ASCII.GetBytes("para").CopyTo(tag, 0);
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(8), (ushort)type);
        for (int i = 0; i < parameters.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(12 + (4 * i)), Fixed(parameters[i]));
        }

        return tag;
    }

    /// <summary>A 'curv' tag: one entry is a u8Fixed8 gamma, more entries are a sampled table.</summary>
    public static byte[] Curv(params ushort[] entries)
    {
        var tag = new byte[12 + (2 * entries.Length)];
        Encoding.ASCII.GetBytes("curv").CopyTo(tag, 0);
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(8), (uint)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(12 + (2 * i)), entries[i]);
        }

        return tag;
    }

    /// <summary>An RGB display profile with the given D50 colorants and one tone curve shared by the three channels.</summary>
    public static byte[] Rgb(double[] red, double[] green, double[] blue, byte[] curve) => Build(
        "RGB ",
        [("wtpt", Xyz([0.9642, 1.0, 0.8249])), ("rXYZ", Xyz(red)), ("gXYZ", Xyz(green)), ("bXYZ", Xyz(blue)), ("rTRC", curve), ("gTRC", curve), ("bTRC", curve)]);

    /// <summary>A gray display profile with the given tone curve.</summary>
    public static byte[] Gray(byte[] curve) => Build("GRAY", [("wtpt", Xyz([0.9642, 1.0, 0.8249])), ("kTRC", curve)]);

    private static byte[] Xyz(double[] v)
    {
        var tag = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(tag, 0);
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(8 + (4 * i)), Fixed(v[i]));
        }

        return tag;
    }

    private static int Fixed(double v) => (int)Math.Round(v * 65536);

    private static byte[] Build(string space, (string Sig, byte[] Data)[] tags)
    {
        int tableEnd = 132 + (12 * tags.Length);
        var offsets = new int[tags.Length];
        int pos = tableEnd;
        for (int i = 0; i < tags.Length; i++)
        {
            offsets[i] = pos;
            pos += (tags[i].Data.Length + 3) & ~3;
        }

        var p = new byte[pos];
        BinaryPrimitives.WriteUInt32BigEndian(p, (uint)pos);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), 0x04300000); // version 4.3
        Encoding.ASCII.GetBytes("mntr").CopyTo(p, 12);
        Encoding.ASCII.GetBytes(space).CopyTo(p, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(p, 20);
        Encoding.ASCII.GetBytes("acsp").CopyTo(p, 36);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(68), Fixed(0.9642));
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(72), Fixed(1.0));
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(76), Fixed(0.8249));
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(128), (uint)tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            int e = 132 + (12 * i);
            Encoding.ASCII.GetBytes(tags[i].Sig).CopyTo(p, e);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(e + 4), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(e + 8), (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(p, offsets[i]);
        }

        return p;
    }
}
