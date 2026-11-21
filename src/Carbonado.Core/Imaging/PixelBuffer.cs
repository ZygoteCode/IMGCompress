using System.Runtime.InteropServices;

namespace Carbonado.Core.Imaging;

/// <summary>
/// An RGBA image held in unmanaged, 64-byte aligned memory: interleaved R,G,B,A samples, non-premultiplied alpha,
/// 8 or 16 bits per sample (16-bit samples are native little-endian), rows tightly packed (no padding).
/// Unmanaged storage keeps multi-hundred-megabyte images off the GC heap and frees them deterministically.
/// </summary>
public sealed unsafe class PixelBuffer : IDisposable
{
    private byte* _data;
    private int _disposed;

    private PixelBuffer(int width, int height, SampleDepth depth, bool clear)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (depth is not (SampleDepth.Eight or SampleDepth.Sixteen))
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "Only 8 and 16 bit samples are supported.");
        }

        Width = width;
        Height = height;
        Depth = depth;
        BytesPerPixel = depth == SampleDepth.Eight ? 4 : 8;
        Stride = (long)width * BytesPerPixel;
        if (Stride > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Image row exceeds 2 GiB.");
        }

        ByteLength = checked(Stride * height);
        _data = (byte*)NativeMemory.AlignedAlloc((nuint)ByteLength, 64);
        if (clear)
        {
            NativeMemory.Clear(_data, (nuint)ByteLength);
        }

        GC.AddMemoryPressure(ByteLength);
    }

    ~PixelBuffer() => Free();

    public int Width { get; }

    public int Height { get; }

    public SampleDepth Depth { get; }

    /// <summary>4 for 8-bit buffers, 8 for 16-bit buffers.</summary>
    public int BytesPerPixel { get; }

    /// <summary>Bytes per row (Width × BytesPerPixel).</summary>
    public long Stride { get; }

    public long ByteLength { get; }

    public long PixelCount => (long)Width * Height;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Start of the pixel data. Valid until <see cref="Dispose"/>.</summary>
    public nint Pointer
    {
        get
        {
            ThrowIfDisposed();
            return (nint)_data;
        }
    }

    public static PixelBuffer Create(int width, int height, SampleDepth depth, bool clear = true) =>
        new(width, height, depth, clear);

    /// <summary>The raw bytes of row <paramref name="y"/>.</summary>
    public Span<byte> RowBytes(int y)
    {
        ThrowIfDisposed();
        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, null);
        }

        return new Span<byte>(_data + (y * Stride), (int)Stride);
    }

    /// <summary>Row <paramref name="y"/> as 8-bit pixels. Only valid for <see cref="SampleDepth.Eight"/>.</summary>
    public Span<Rgba32> Row32(int y)
    {
        if (Depth != SampleDepth.Eight)
        {
            throw new InvalidOperationException("Row32 requires an 8-bit buffer.");
        }

        return MemoryMarshal.Cast<byte, Rgba32>(RowBytes(y));
    }

    /// <summary>Row <paramref name="y"/> as 16-bit pixels. Only valid for <see cref="SampleDepth.Sixteen"/>.</summary>
    public Span<Rgba64> Row64(int y)
    {
        if (Depth != SampleDepth.Sixteen)
        {
            throw new InvalidOperationException("Row64 requires a 16-bit buffer.");
        }

        return MemoryMarshal.Cast<byte, Rgba64>(RowBytes(y));
    }

    /// <summary>The whole buffer as one span; throws when the buffer is larger than 2 GiB (use rows instead).</summary>
    public Span<byte> AsSpan()
    {
        ThrowIfDisposed();
        if (ByteLength > int.MaxValue)
        {
            throw new InvalidOperationException("Buffer exceeds 2 GiB; iterate rows instead.");
        }

        return new Span<byte>(_data, (int)ByteLength);
    }

    public PixelBuffer Clone()
    {
        ThrowIfDisposed();
        var copy = new PixelBuffer(Width, Height, Depth, clear: false);
        Buffer.MemoryCopy(_data, copy._data, ByteLength, ByteLength);
        return copy;
    }

    /// <summary>
    /// Converts to another sample depth. 8→16 is exact (v·257); 16→8 rounds to nearest ((v·255 + 32895) &gt;&gt; 16).
    /// Returns a clone when the depth already matches.
    /// </summary>
    public PixelBuffer ToDepth(SampleDepth depth)
    {
        ThrowIfDisposed();
        if (depth == Depth)
        {
            return Clone();
        }

        var result = new PixelBuffer(Width, Height, depth, clear: false);
        int samples = Width * 4;
        if (depth == SampleDepth.Sixteen)
        {
            for (int y = 0; y < Height; y++)
            {
                byte* src = _data + (y * Stride);
                ushort* dst = (ushort*)(result._data + (y * result.Stride));
                for (int i = 0; i < samples; i++)
                {
                    dst[i] = (ushort)(src[i] * 257);
                }
            }
        }
        else
        {
            for (int y = 0; y < Height; y++)
            {
                ushort* src = (ushort*)(_data + (y * Stride));
                byte* dst = result._data + (y * result.Stride);
                for (int i = 0; i < samples; i++)
                {
                    dst[i] = (byte)(((src[i] * 255) + 32895) >> 16);
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private void Free()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_data != null)
        {
            NativeMemory.AlignedFree(_data);
            _data = null;
            GC.RemoveMemoryPressure(ByteLength);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
