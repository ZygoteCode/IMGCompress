using System.Runtime.InteropServices;

namespace Carbonado.Core.Metrics;

/// <summary>
/// A set of equally sized float planes in unmanaged memory, 64-byte aligned, with rows padded to a multiple of 16 floats
/// so 256-bit accesses never split cache lines. Contents start undefined: producers write every row before it is
/// read, and the padding columns are never read. Three planes of linear R, G, B serve as the
/// <see cref="ILinearSource"/> of a downsampled scale.
/// </summary>
internal sealed unsafe class FloatPlanes : IDisposable, ILinearSource
{
    private float* _data;

    public FloatPlanes(int count, int width, int height)
    {
        Width = width;
        Height = height;
        Stride = (width + 15) & ~15;
        PlaneLength = (long)Stride * height;
        ByteLength = PlaneLength * count * sizeof(float);
        _data = (float*)NativeMemory.AlignedAlloc((nuint)ByteLength, 64);
        GC.AddMemoryPressure(ByteLength);
    }

    ~FloatPlanes() => Free();

    public int Width { get; }

    public int Height { get; }

    /// <summary>Floats per row (≥ <see cref="Width"/>, multiple of 16).</summary>
    public int Stride { get; }

    public long PlaneLength { get; }

    public long ByteLength { get; }

    /// <summary>Start of row <paramref name="y"/> of plane <paramref name="plane"/>. Valid until disposed.</summary>
    public float* Row(int plane, int y) => _data + (plane * PlaneLength) + ((long)y * Stride);

    /// <summary>Copies row <paramref name="y"/> of planes 0, 1 and 2.</summary>
    public void Read(int y, float* r, float* g, float* b)
    {
        long bytes = (long)Width * sizeof(float);
        Buffer.MemoryCopy(Row(0, y), r, bytes, bytes);
        Buffer.MemoryCopy(Row(1, y), g, bytes, bytes);
        Buffer.MemoryCopy(Row(2, y), b, bytes, bytes);
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private void Free()
    {
        float* p = _data;
        _data = null;
        if (p != null)
        {
            NativeMemory.AlignedFree(p);
            GC.RemoveMemoryPressure(ByteLength);
        }
    }
}
