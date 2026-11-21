using System.Runtime.InteropServices;

namespace Carbonado.Core.Imaging;

/// <summary>8-bit per channel RGBA pixel, non-premultiplied, memory order R,G,B,A.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rgba32 : IEquatable<Rgba32>
{
    public byte R;
    public byte G;
    public byte B;
    public byte A;

    public Rgba32(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public readonly bool Equals(Rgba32 other) => R == other.R && G == other.G && B == other.B && A == other.A;

    public override readonly bool Equals(object? obj) => obj is Rgba32 other && Equals(other);

    public override readonly int GetHashCode() => (R << 24) | (G << 16) | (B << 8) | A;

    public static bool operator ==(Rgba32 left, Rgba32 right) => left.Equals(right);

    public static bool operator !=(Rgba32 left, Rgba32 right) => !left.Equals(right);

    public override readonly string ToString() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}
