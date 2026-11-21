using System.Text;
using Carbonado.Core.Codecs;

namespace Carbonado.Core.Tests.Codecs;

public class Crc32Tests
{
    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0xE8B7BE43u)]
    [InlineData("123456789", 0xCBF43926u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
    public void Crc32_KnownVectors(string text, uint expected) =>
        Assert.Equal(expected, Crc32.Compute(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void Crc32_AppendEqualsWholeAndMatchesBitwiseReference()
    {
        var data = new byte[1000];
        new Random(42).NextBytes(data);
        uint whole = Crc32.Compute(data);
        Assert.Equal(BitwiseCrc(data), whole);
        for (int split = 0; split <= data.Length; split += 37)
        {
            Assert.Equal(whole, Crc32.Append(Crc32.Compute(data.AsSpan(0, split)), data.AsSpan(split)));
        }

        // Every alignment/length combination around the 8-byte main loop.
        for (int start = 0; start < 9; start++)
        {
            for (int len = 0; len < 40; len++)
            {
                Assert.Equal(BitwiseCrc(data.AsSpan(start, len)), Crc32.Compute(data.AsSpan(start, len)));
            }
        }
    }

    [Theory]
    [InlineData("", 0x00000001u)]
    [InlineData("a", 0x00620062u)]
    [InlineData("Wikipedia", 0x11E60398u)]
    [InlineData("123456789", 0x091E01DEu)]
    public void Adler32_KnownVectors(string text, uint expected) =>
        Assert.Equal(expected, Adler32.Compute(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void Adler32_AppendAndCombineEqualWhole()
    {
        // Large enough to exercise the deferred-modulo blocks (NMAX = 5552) with maximal byte values.
        var data = new byte[70_000];
        new Random(3).NextBytes(data);
        data.AsSpan(10_000, 20_000).Fill(0xFF);
        uint whole = Adler32.Compute(data);
        Assert.Equal(BytewiseAdler(data), whole);
        foreach (int split in new[] { 0, 1, 5551, 5552, 5553, 33_333, 69_999, 70_000 })
        {
            uint a = Adler32.Compute(data.AsSpan(0, split));
            uint b = Adler32.Compute(data.AsSpan(split));
            Assert.Equal(whole, Adler32.Append(a, data.AsSpan(split)));
            Assert.Equal(whole, Adler32.Combine(a, b, data.Length - split));
        }
    }

    private static uint BitwiseCrc(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
        }

        return ~c;
    }

    private static uint BytewiseAdler(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }
}
