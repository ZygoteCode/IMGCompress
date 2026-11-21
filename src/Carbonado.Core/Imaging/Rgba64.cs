using System.Runtime.InteropServices;

namespace Carbonado.Core.Imaging;

/// <summary>16-bit per channel RGBA pixel, non-premultiplied, native little-endian, memory order R,G,B,A.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct Rgba64 : IEquatable<Rgba64>
{
    public ushort R;
    public ushort G;
    public ushort B;
    public ushort A;

    public Rgba64(ushort r, ushort g, ushort b, ushort a = ushort.MaxValue)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public readonly bool Equals(Rgba64 other) => R == other.R && G == other.G && B == other.B && A == other.A;

    public override readonly bool Equals(object? obj) => obj is Rgba64 other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(R, G, B, A);

    public static bool operator ==(Rgba64 left, Rgba64 right) => left.Equals(right);

    public static bool operator !=(Rgba64 left, Rgba64 right) => !left.Equals(right);

    public override readonly string ToString() => $"#{R:X4}{G:X4}{B:X4}{A:X4}";
}
