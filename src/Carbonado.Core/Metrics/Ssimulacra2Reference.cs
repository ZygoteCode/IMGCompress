using Carbonado.Core.Imaging;

namespace Carbonado.Core.Metrics;

/// <summary>
/// A reference image prepared once for scoring many candidates with SSIMULACRA2 (e.g. during a quality search): its
/// XYB planes and blurred statistics are cached for every scale, so each score only processes the distorted image —
/// about 40 % less work than <see cref="Ssimulacra2.Compute"/>, with bit-identical results. The cache takes about
/// 48 bytes per reference pixel (twice that when the reference has transparency: one per background) in unmanaged
/// memory, released by <see cref="Dispose"/>.
/// <para>
/// Thread safety: <see cref="Score"/> and <see cref="ScoreWithMap"/> may run concurrently from any number of threads;
/// each call allocates its own O(width) working buffers and only reads the cache. <see cref="Dispose"/> may be called
/// while scores are running: the memory is released when the last of them returns, and later calls throw
/// <see cref="ObjectDisposedException"/>.
/// </para>
/// </summary>
public sealed class Ssimulacra2Reference : IDisposable
{
    private readonly ColorSpace.IccLinearizer? _icc;
    private readonly bool _referenceHasAlpha;
    private readonly List<(int Width, int Height)> _sizes;
    private readonly FloatPlanes[][] _caches; // one per background: [unblended] or [dark, bright]; then per scale
    private readonly Lazy<LinearDecoder> _plain, _dark, _bright;
    private int _leases = 1; // the owner's lease plus one per running score
    private int _disposed;

    /// <summary>Prepares <paramref name="reference"/> (at least 8×8; 8 or 16 bit) in the color space of <paramref name="icc"/> (null = sRGB).</summary>
    /// <exception cref="ArgumentException">The image is smaller than <see cref="Ssimulacra2.MinimumSize"/>.</exception>
    public Ssimulacra2Reference(PixelBuffer reference, byte[]? icc = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        Ssimulacra2.ValidateSize(reference);
        Width = reference.Width;
        Height = reference.Height;
        _icc = ColorSpace.IccLinearizer.TryCreate(icc);
        _plain = new(() => LinearDecoder.Get(_icc, null));
        _dark = new(() => LinearDecoder.Get(_icc, Ssimulacra2.DarkBackground));
        _bright = new(() => LinearDecoder.Get(_icc, Ssimulacra2.BrightBackground));
        _referenceHasAlpha = !AlphaOps.IsOpaque(reference);
        _sizes = Ssimulacra2.ScaleSizes(Width, Height);
        float?[] backgrounds = _referenceHasAlpha ? [Ssimulacra2.DarkBackground, Ssimulacra2.BrightBackground] : [null];
        _caches = new FloatPlanes[backgrounds.Length][];
        try
        {
            for (int i = 0; i < backgrounds.Length; i++)
            {
                _caches[i] = Build(reference, backgrounds[i]);
            }
        }
        catch
        {
            Free();
            throw;
        }
    }

    /// <summary>Width of the reference (and of every image scored against it).</summary>
    public int Width { get; }

    /// <summary>Height of the reference (and of every image scored against it).</summary>
    public int Height { get; }

    /// <summary>Scores <paramref name="distorted"/> (same size as the reference) against the cached reference.</summary>
    /// <exception cref="ArgumentException">The size differs from the reference.</exception>
    /// <exception cref="ObjectDisposedException">The reference was disposed.</exception>
    public double Score(PixelBuffer distorted, CancellationToken ct = default) => Run(distorted, null, ct);

    /// <summary>Like <see cref="Score"/>, also returning the full-resolution error heat-map.</summary>
    public Ssimulacra2Map ScoreWithMap(PixelBuffer distorted, CancellationToken ct = default)
    {
        var error = new float[(long)Width * Height];
        double score = Run(distorted, error, ct);
        return new Ssimulacra2Map(Width, Height, error, score);
    }

    /// <summary>Releases the cache (once every running score has returned).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Release();
        }
    }

    private double Run(PixelBuffer distorted, float[]? map, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(distorted);
        if (distorted.Width != Width || distorted.Height != Height)
        {
            throw new ArgumentException($"Image size mismatch: {distorted.Width}×{distorted.Height} vs reference {Width}×{Height}.", nameof(distorted));
        }

        Acquire();
        try
        {
            if (!_referenceHasAlpha && AlphaOps.IsOpaque(distorted))
            {
                return ScoreOn(_caches[0], distorted, _plain.Value, map, mapTakesMax: false, ct);
            }

            // An opaque reference is the same on both backgrounds, so its single cache serves both.
            double dark = ScoreOn(_caches[0], distorted, _dark.Value, map, mapTakesMax: false, ct);
            double bright = ScoreOn(_caches[^1], distorted, _bright.Value, map, mapTakesMax: true, ct);
            return Math.Min(dark, bright);
        }
        finally
        {
            Release();
        }
    }

    private FloatPlanes[] Build(PixelBuffer reference, float? background)
    {
        var planes = new List<FloatPlanes>(_sizes.Count);
        try
        {
            foreach (var (w, h) in _sizes)
            {
                planes.Add(new FloatPlanes(Ssimulacra2Pass.ReferencePlanes, w, h));
            }

            var decoder = background switch { null => _plain.Value, Ssimulacra2.DarkBackground => _dark.Value, _ => _bright.Value };
            Ssimulacra2Pass.RunPyramid(PassMode.BuildReference, _sizes, new PixelLinearSource(reference, decoder), null, planes, null, false, CancellationToken.None);
            return [.. planes];
        }
        catch
        {
            planes.ForEach(p => p.Dispose());
            throw;
        }
    }

    private double ScoreOn(FloatPlanes[] cache, PixelBuffer distorted, LinearDecoder decoder, float[]? map, bool mapTakesMax, CancellationToken ct)
    {
        var norms = Ssimulacra2Pass.RunPyramid(PassMode.ScoreCached, _sizes, null, new PixelLinearSource(distorted, decoder), cache, map, mapTakesMax, ct);
        return Ssimulacra2.FromNorms(Array.ConvertAll(norms, n => n!));
    }

    private void Acquire()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        while (true)
        {
            int leases = Volatile.Read(ref _leases);
            ObjectDisposedException.ThrowIf(leases == 0, this);
            if (Interlocked.CompareExchange(ref _leases, leases + 1, leases) == leases)
            {
                return;
            }
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _leases) == 0)
        {
            Free();
        }
    }

    private void Free()
    {
        foreach (var cache in _caches)
        {
            foreach (var planes in cache ?? [])
            {
                planes.Dispose();
            }
        }
    }
}
