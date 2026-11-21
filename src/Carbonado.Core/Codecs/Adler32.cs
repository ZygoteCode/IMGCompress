using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Carbonado.Core.Codecs;

/// <summary>
/// Adler-32 checksum (RFC 1950, the zlib stream trailer). Values are compatible with zlib's <c>adler32()</c>:
/// <c>Append(Compute(a), b) == Compute(a ‖ b)</c> and <c>Combine(Compute(a), Compute(b), b.Length) == Compute(a ‖ b)</c>.
/// </summary>
public static class Adler32
{
    private const uint Modulus = 65521;

    // Largest n such that 255·n·(n+1)/2 + (n+1)·(Modulus−1) fits in 32 bits: sums may be deferred this long.
    private const int MaxDeferred = 5552;

    /// <summary>Adler-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(1, data);

    /// <summary>Continues the checksum <paramref name="adler"/> (of preceding data) over <paramref name="data"/>.</summary>
    public static uint Append(uint adler, ReadOnlySpan<byte> data)
    {
        uint a = adler & 0xFFFF, b = adler >> 16;
        ref byte p = ref MemoryMarshal.GetReference(data);
        int remaining = data.Length;
        nint i = 0;
        while (remaining > 0)
        {
            int n = Math.Min(remaining, MaxDeferred);
            remaining -= n;
            for (; n >= 8; n -= 8, i += 8)
            {
                a += Unsafe.Add(ref p, i); b += a;
                a += Unsafe.Add(ref p, i + 1); b += a;
                a += Unsafe.Add(ref p, i + 2); b += a;
                a += Unsafe.Add(ref p, i + 3); b += a;
                a += Unsafe.Add(ref p, i + 4); b += a;
                a += Unsafe.Add(ref p, i + 5); b += a;
                a += Unsafe.Add(ref p, i + 6); b += a;
                a += Unsafe.Add(ref p, i + 7); b += a;
            }

            for (; n > 0; n--, i++)
            {
                a += Unsafe.Add(ref p, i);
                b += a;
            }

            a %= Modulus;
            b %= Modulus;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// Adler-32 of the concatenation of two blocks, from the checksum of each and the length of the second
    /// (zlib's <c>adler32_combine</c>). Lets independently checksummed segments be joined without re-reading them.
    /// </summary>
    public static uint Combine(uint adler1, uint adler2, long length2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length2);
        uint rem = (uint)(length2 % Modulus);
        ulong sum1 = adler1 & 0xFFFF;
        ulong sum2 = (rem * sum1) % Modulus;
        sum1 += (adler2 & 0xFFFF) + Modulus - 1;
        sum2 += (adler1 >> 16) + (adler2 >> 16) + Modulus - rem;
        if (sum1 >= Modulus)
        {
            sum1 -= Modulus;
        }

        if (sum1 >= Modulus)
        {
            sum1 -= Modulus;
        }

        if (sum2 >= Modulus << 1)
        {
            sum2 -= Modulus << 1;
        }

        if (sum2 >= Modulus)
        {
            sum2 -= Modulus;
        }

        return (uint)((sum2 << 16) | sum1);
    }
}
