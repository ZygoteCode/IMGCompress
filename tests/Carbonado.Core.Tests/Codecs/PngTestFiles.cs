using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Carbonado.Core.Codecs;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Tests.Codecs;

/// <summary>
/// Hand-assembled PNG/APNG files for tests (independent of Carbonado's PNG encoder): RGBA 8-bit, filter None.
/// </summary>
public static class PngTestFiles
{
    public static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decodes with Carbonado's decoder, honouring test cancellation.</summary>
    public static DecodedImage Decode(Stream png) => PngDecoder.Decode(png, TestContext.Current.CancellationToken);

    /// <summary>Decodes a PNG file (the file is closed afterwards).</summary>
    public static DecodedImage DecodeFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Decode(fs);
    }

    /// <summary>Decodes a Netpbm file (the file is closed afterwards).</summary>
    public static DecodedImage DecodePamFile(string path)
    {
        using var fs = File.OpenRead(path);
        return PnmCodec.Decode(fs);
    }

    /// <summary>A still RGBA PNG holding <paramref name="px"/> (8-bit).</summary>
    public static byte[] Still(PixelBuffer px)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        WriteChunk(ms, "IHDR", Ihdr(px.Width, px.Height, bitDepth: 8, colorType: 6));
        WriteChunk(ms, "IDAT", Compress(px));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>
    /// An APNG whose default image is the first frame; every frame is full-size, so the file decodes as
    /// <paramref name="frames"/>[0] by non-APNG decoders.
    /// </summary>
    public static byte[] Apng(params PixelBuffer[] frames)
    {
        int w = frames[0].Width, h = frames[0].Height;
        using var ms = new MemoryStream();
        ms.Write(Signature);
        WriteChunk(ms, "IHDR", Ihdr(w, h, bitDepth: 8, colorType: 6));
        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames.Length);
        WriteChunk(ms, "acTL", actl);
        uint sequence = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            WriteChunk(ms, "fcTL", Fctl(sequence++, w, h));
            byte[] data = Compress(frames[i]);
            if (i == 0)
            {
                WriteChunk(ms, "IDAT", data);
            }
            else
            {
                var fdat = new byte[4 + data.Length];
                BinaryPrimitives.WriteUInt32BigEndian(fdat, sequence++);
                data.CopyTo(fdat, 4);
                WriteChunk(ms, "fdAT", fdat);
            }
        }

        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    public static byte[] Ihdr(int width, int height, byte bitDepth, byte colorType, byte interlace = 0)
    {
        var d = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), (uint)height);
        d[8] = bitDepth;
        d[9] = colorType;
        d[12] = interlace;
        return d;
    }

    public static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        s.Write(header);
        s.Write(data);
        uint crc = Crc32.Append(Crc32.Compute(header[4..]), data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    /// <summary>Byte offsets of every chunk (start of its length field) in a PNG file, with its type.</summary>
    public static List<(int Offset, string Type, int Length)> Chunks(byte[] png)
    {
        var result = new List<(int, string, int)>();
        int o = 8;
        while (o + 8 <= png.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(o));
            result.Add((o, Encoding.ASCII.GetString(png, o + 4, 4), len));
            o += 12 + len;
        }

        return result;
    }

    private static byte[] Fctl(uint sequence, int w, int h)
    {
        var d = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(d, sequence);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), (uint)h);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(20), 1); // delay 1/10 s
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(22), 10);
        return d;
    }

    private static byte[] Compress(PixelBuffer px)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < px.Height; y++)
        {
            raw.WriteByte(0);
            raw.Write(px.RowBytes(y));
        }

        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(z);
        }

        return ms.ToArray();
    }
}
