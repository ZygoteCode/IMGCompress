using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Carbonado.Core.Imaging;
using Carbonado.Core.Metrics;
using Carbonado.Core.Tests.Imaging;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Metrics;

public class Ssimulacra2Tests(ITestOutputHelper output)
{
    /// <summary>|ours − ssimulacra2.exe| per compared pair, for the running max/mean printed by each case.</summary>
    private static readonly ConcurrentDictionary<string, double> s_deltas = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> ExeCases =>
    [
        "photo0-jpeg30", "photo0-jpeg60", "photo0-jpeg90", "photo0-jxl0.5", "photo0-jxl2", "photo0-jxl6",
        "photo2-jpeg30", "photo2-jpeg60", "photo2-jpeg90", "photo2-jxl0.5", "photo2-jxl2", "photo2-jxl6",
        "photo4-full-jxl1",
        "graphic-jpeg60", "graphic-jxl2", "palette-jpeg90", "gradient-jpeg30", "gray-jxl3",
        "alpha-jxl1", "alpha-jxl4", "alpha-quantized", "alpha-invisible-rgb", "opaque-vs-translucent",
        "deep16-noise", "p3-jpeg60", "tiny-8x8-jpeg30", "odd-9x13-jpeg30", "odd-37x23-jxl2", "odd-257x129-jpeg60",
    ];

    [Fact]
    public void Identical_Is100()
    {
        using var photo = TestImages.PhotoLike(203, 117);
        Assert.Equal(100.0, Ssimulacra2.Compute(photo, photo, ct: Ct));
        using var alpha = TestImages.WithAlpha(64, 64);
        using var alphaCopy = alpha.Clone();
        Assert.Equal(100.0, Ssimulacra2.Compute(alpha, alphaCopy, ct: Ct));
        using var deep = TestImages.Deep16();
        Assert.Equal(100.0, Ssimulacra2.Compute(deep, deep, ct: Ct));
        using var reference = new Ssimulacra2Reference(photo);
        Assert.Equal(100.0, reference.Score(photo, Ct));
        using var smallest = TestImages.Gradient(8, 8);
        Assert.Equal(100.0, Ssimulacra2.Compute(smallest, smallest, ct: Ct));
    }

    [Theory]
    [MemberData(nameof(ExeCases))]
    public void MatchesReferenceExe(string name)
    {
        string exe = TestTools.Require(ToolId.Ssimulacra2);
        using var dir = new TempDir("ssim2");
        using var pair = CreatePair(name, dir);
        double expected = RunExe(exe, pair.ReferencePath, pair.DistortedPath);
        double ours = Ssimulacra2.Compute(pair.Reference, pair.Distorted, pair.Icc, ct: Ct);
        using (var cached = new Ssimulacra2Reference(pair.Reference, pair.Icc))
        {
            Assert.Equal(ours, cached.Score(pair.Distorted, Ct));
        }

        double delta = ours - expected;
        if (pair.Icc is not null)
        {
            // The tool converts through skcms (not bit-exact with our matrix/TRC math), but reading the pixels as sRGB
            // instead must be clearly further off: proves both sides applied the embedded profile.
            double asSrgb = Ssimulacra2.Compute(pair.Reference, pair.Distorted, ct: Ct);
            output.WriteLine(FormattableString.Invariant($"{name}: same pixels read as sRGB score {asSrgb:F8}"));
            Assert.True(Math.Abs(asSrgb - expected) > 5 * Math.Abs(delta));
        }

        s_deltas[name] = Math.Abs(delta);
        var all = s_deltas.Values.ToArray();
        output.WriteLine(FormattableString.Invariant(
            $"{name} {pair.Reference.Width}x{pair.Reference.Height}: ours {ours:F8} exe {expected:F8} delta {delta:E2} | {all.Length} pairs so far: max |delta| {all.Max():E2}, mean {all.Average():E2}"));
        Assert.True(Math.Abs(delta) <= 0.5, $"{name}: ours {ours:F8} vs ssimulacra2.exe {expected:F8}");
    }

    [Fact]
    public void MonotoneWithNoise()
    {
        using var reference = TestImages.PhotoLike(256, 192, seed: 3);
        using var cached = new Ssimulacra2Reference(reference);
        double previous = double.PositiveInfinity;
        foreach (int amplitude in new[] { 0, 1, 3, 6, 12, 24, 48 })
        {
            using var noisy = AddNoise(reference, amplitude, seed: 11);
            double score = cached.Score(noisy, Ct);
            output.WriteLine($"noise ±{amplitude}: {score:F4}");
            Assert.True(score < previous, $"noise ±{amplitude} scored {score} after {previous}");
            previous = score;
        }

        Assert.True(previous < 50);
    }

    [Fact]
    public void Alpha_UsesWorstOfTwoBackgrounds()
    {
        using var reference = TestImages.WithAlpha(160, 120);

        // Darken the translucent pixels: the change reads differently over the dark and the bright background.
        using var distorted = reference.Clone();
        for (int y = 0; y < distorted.Height; y++)
        {
            foreach (ref var p in distorted.Row32(y))
            {
                if (p.A is > 0 and < 255)
                {
                    p = new Rgba32((byte)(p.R / 2), (byte)(p.G / 2), (byte)(p.B / 2), p.A);
                }
            }
        }

        double dark = Ssimulacra2.ComputeOn(reference, distorted, null, 0.1f, Ct);
        double bright = Ssimulacra2.ComputeOn(reference, distorted, null, 0.9f, Ct);
        Assert.NotEqual(dark, bright, 3);
        double expected = Math.Min(dark, bright);
        Assert.Equal(expected, Ssimulacra2.Compute(reference, distorted, ct: Ct));
        using (var cached = new Ssimulacra2Reference(reference))
        {
            Assert.Equal(expected, cached.Score(distorted, Ct));
        }

        // RGB under fully transparent pixels is invisible on both backgrounds.
        using var hidden = reference.Clone();
        for (int y = 0; y < hidden.Height; y++)
        {
            foreach (ref var p in hidden.Row32(y))
            {
                if (p.A == 0)
                {
                    p = new Rgba32(255, 0, 255, 0);
                }
            }
        }

        Assert.Equal(100.0, Ssimulacra2.Compute(reference, hidden, ct: Ct));

        // An opaque reference against a distorted image that gained transparency is also scored on both backgrounds.
        using var opaque = TestImages.PhotoLike(160, 120);
        using var faded = opaque.Clone();
        for (int y = 0; y < 60; y++)
        {
            foreach (ref var p in faded.Row32(y))
            {
                p.A = 128;
            }
        }

        double fadedScore = Ssimulacra2.Compute(opaque, faded, ct: Ct);
        Assert.Equal(Math.Min(Ssimulacra2.ComputeOn(opaque, faded, null, 0.1f, Ct), Ssimulacra2.ComputeOn(opaque, faded, null, 0.9f, Ct)), fadedScore);
        using var cachedOpaque = new Ssimulacra2Reference(opaque);
        Assert.Equal(fadedScore, cachedOpaque.Score(faded, Ct));
    }

    [Fact]
    public void Reference_CachedEqualsUncached()
    {
        foreach (var reference in new[] { TestImages.PhotoLike(301, 203, seed: 5), TestImages.WithAlpha(150, 91), TestImages.Deep16(130, 97) })
        {
            using var _ = reference;
            var distorted = Enumerable.Range(0, 6).Select(i => AddNoise(reference, 2 + (5 * i), seed: i)).ToArray();
            try
            {
                double[] uncached = distorted.Select(d => Ssimulacra2.Compute(reference, d, ct: Ct)).ToArray();
                using var cached = new Ssimulacra2Reference(reference);
                Assert.Equal(uncached, distorted.Select(d => cached.Score(d, Ct)).ToArray());

                // Concurrent scoring against one shared reference gives the same results.
                var concurrent = new double[distorted.Length * 4];
                Parallel.For(0, concurrent.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => concurrent[i] = cached.Score(distorted[i % distorted.Length], Ct));
                for (int i = 0; i < concurrent.Length; i++)
                {
                    Assert.Equal(uncached[i % distorted.Length], concurrent[i]);
                }

                var map = cached.ScoreWithMap(distorted[2], Ct);
                Assert.Equal(uncached[2], map.Score);
            }
            finally
            {
                foreach (var d in distorted)
                {
                    d.Dispose();
                }
            }
        }
    }

    [Fact]
    public void Reference_DisposedThrows()
    {
        using var px = TestImages.PhotoLike(64, 48);
        var reference = new Ssimulacra2Reference(px);
        reference.Dispose();
        reference.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reference.Score(px, Ct));
    }

    [Fact]
    public async Task Reference_DisposeWhileScoring_IsSafe()
    {
        using var px = TestImages.PhotoLike(1024, 768);
        using var noisy = AddNoise(px, 5, seed: 1);
        var reference = new Ssimulacra2Reference(px);
        double expected = reference.Score(noisy, Ct);
        var running = Enumerable.Range(0, 4).Select(_ => Task.Run(() => reference.Score(noisy, Ct), Ct)).ToArray();
        reference.Dispose();
        foreach (var task in running)
        {
            // Scores already running finish on the still-alive cache; ones that start after Dispose are refused.
            try
            {
                Assert.Equal(expected, await task);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        Assert.Throws<ObjectDisposedException>(() => reference.Score(noisy, Ct));
    }

    [Fact]
    public void Score_HonoursCancellation()
    {
        using var px = TestImages.PhotoLike(256, 192);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Ssimulacra2.Compute(px, px, ct: cancelled.Token));
        using var reference = new Ssimulacra2Reference(px);
        Assert.ThrowsAny<OperationCanceledException>(() => reference.Score(px, cancelled.Token));
        Assert.Equal(100.0, reference.Score(px, Ct));
    }

    [Fact]
    public void RejectsSmallerThan8x8()
    {
        using var ok = TestImages.Gradient(8, 8);
        using var narrow = TestImages.Gradient(7, 8);
        using var low = TestImages.Gradient(8, 7);
        using var other = TestImages.Gradient(9, 8);
        Assert.Throws<ArgumentException>(() => Ssimulacra2.Compute(narrow, narrow, ct: Ct));
        Assert.Throws<ArgumentException>(() => Ssimulacra2.Compute(low, low, ct: Ct));
        Assert.Throws<ArgumentException>(() => new Ssimulacra2Reference(narrow));
        Assert.Throws<ArgumentException>(() => Ssimulacra2.Compute(ok, other, ct: Ct));
        using var reference = new Ssimulacra2Reference(ok);
        Assert.Throws<ArgumentException>(() => reference.Score(other, Ct));
        Assert.Equal(100.0, reference.Score(ok, Ct));
    }

    [Fact]
    public void Map_HighErrorWhereDistorted()
    {
        using var reference = TestImages.PhotoLike(256, 192, seed: 9);
        using var distorted = reference.Clone();
        var rng = new Random(4);
        for (int y = 64; y < 128; y++)
        {
            var row = distorted.Row32(y);
            for (int x = 96; x < 160; x++)
            {
                byte v = (byte)rng.Next(256);
                row[x] = new Rgba32(v, (byte)(255 - v), row[x].B);
            }
        }

        using var cached = new Ssimulacra2Reference(reference);
        var map = cached.ScoreWithMap(distorted, Ct);
        Assert.Equal((256, 192, 256 * 192), (map.Width, map.Height, map.Error.Length));
        Assert.Equal(cached.Score(distorted, Ct), map.Score);
        Assert.All(map.Error, e => Assert.InRange(e, 0f, 1f));
        double inside = 0, outside = 0;
        int nIn = 0, nOut = 0;
        for (int y = 0; y < 192; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                bool near = y >= 60 && y < 132 && x >= 92 && x < 164;
                bool core = y >= 64 && y < 128 && x >= 96 && x < 160;
                if (core)
                {
                    inside += map.Error[(y * 256) + x];
                    nIn++;
                }
                else if (!near)
                {
                    outside += map.Error[(y * 256) + x];
                    nOut++;
                }
            }
        }

        Assert.True(inside / nIn > 0.1 && inside / nIn > 20 * (outside / nOut), $"inside {inside / nIn}, outside {outside / nOut}");
        Assert.All(cached.ScoreWithMap(reference, Ct).Error, e => Assert.Equal(0f, e));
    }

    [Fact]
    public void IccProfile_ChangesTheLinearization()
    {
        // The same pixel values read as Display P3 are more saturated, so a color shift scores differently.
        using var reference = TestImages.PhotoLike(128, 96, seed: 2);
        using var distorted = AddNoise(reference, 6, seed: 1);
        double srgb = Ssimulacra2.Compute(reference, distorted, ct: Ct);
        Assert.Equal(srgb, Ssimulacra2.Compute(reference, distorted, IccTestProfiles.Srgb, ct: Ct));
        double p3 = Ssimulacra2.Compute(reference, distorted, IccTestProfiles.DisplayP3, ct: Ct);
        Assert.NotEqual(srgb, p3, 6);
        using var cached = new Ssimulacra2Reference(reference, IccTestProfiles.DisplayP3);
        Assert.Equal(p3, cached.Score(distorted, Ct));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Performance_12MP_Under1500ms()
    {
#if DEBUG
        Assert.Skip("Performance is measured in Release builds only.");
#endif
        using var reference = TestImages.PhotoLike(4000, 3000, seed: 8);
        using var distorted = AddNoise(reference, 4, seed: 2);

        // The first call includes JIT compilation of the kernels (the first in the process for this test class order).
        var sw = Stopwatch.StartNew();
        double uncached = Ssimulacra2.Compute(reference, distorted, ct: Ct);
        long firstMs = sw.ElapsedMilliseconds;

        sw.Restart();
        Assert.Equal(uncached, Ssimulacra2.Compute(reference, distorted, ct: Ct));
        long computeMs = sw.ElapsedMilliseconds;

        sw.Restart();
        using var cached = new Ssimulacra2Reference(reference);
        long buildMs = sw.ElapsedMilliseconds;

        long best = long.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            sw.Restart();
            Assert.Equal(uncached, cached.Score(distorted, Ct));
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }

        output.WriteLine($"12 MP: first Compute {firstMs} ms, Compute {computeMs} ms, reference build {buildMs} ms, cached Score {best} ms (best of 3), {Environment.ProcessorCount} logical cores");
        Assert.True(firstMs < 1500, $"first Compute took {firstMs} ms");
        Assert.True(buildMs + best < 1500, $"reference + score took {buildMs + best} ms");
    }

    private static PixelBuffer AddNoise(PixelBuffer src, int amplitude, int seed)
    {
        var rng = new Random(seed);
        var px = src.Clone();
        for (int y = 0; y < px.Height; y++)
        {
            if (px.Depth == SampleDepth.Eight)
            {
                foreach (ref var p in px.Row32(y))
                {
                    p = new Rgba32(Noisy(p.R, amplitude, rng), Noisy(p.G, amplitude, rng), Noisy(p.B, amplitude, rng), p.A);
                }
            }
            else
            {
                foreach (ref var p in px.Row64(y))
                {
                    int a = amplitude * 257;
                    p = new Rgba64(Noisy16(p.R, a, rng), Noisy16(p.G, a, rng), Noisy16(p.B, a, rng), p.A);
                }
            }
        }

        return px;
    }

    private static byte Noisy(byte v, int amplitude, Random rng) => (byte)Math.Clamp(v + rng.Next(-amplitude, amplitude + 1), 0, 255);

    private static ushort Noisy16(ushort v, int amplitude, Random rng) => (ushort)Math.Clamp(v + rng.Next(-amplitude, amplitude + 1), 0, 65535);

    private static double RunExe(string exe, string reference, string distorted)
    {
        string stdout = RunTool(exe, reference, distorted);
        return double.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
    }

    private static string RunTool(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(120_000))
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail($"{Path.GetFileName(exe)} timed out");
        }

        Assert.True(p.ExitCode == 0, $"{Path.GetFileName(exe)} failed ({p.ExitCode}): {stderr.Result}");
        return stdout;
    }

    /// <summary>Builds a named reference/distorted pair, saved as WIC PNGs so the exe decodes exactly our pixels.</summary>
    private static Pair CreatePair(string name, TempDir dir)
    {
        string[] parts = name.Split('-');
        PixelBuffer reference = parts[0] switch
        {
            "photo0" or "photo2" or "photo4" => Photo(int.Parse(parts[0][5..], CultureInfo.InvariantCulture), full: parts[1] == "full"),
            "graphic" => TestImages.Graphic(),
            "palette" => TestImages.Palette(160, 120, colors: 24),
            "gradient" => TestImages.Gradient(320, 240),
            "gray" => TestImages.Gray(200, 150),
            "alpha" => TestImages.WithAlpha(256, 192),
            "opaque" => TestImages.PhotoLike(200, 150, seed: 4),
            "deep16" => TestImages.Deep16(160, 120),
            "p3" => TestImages.PhotoLike(320, 240, seed: 6),
            "tiny" => Crop(TestImages.PhotoLike(), 8, 8),
            "odd" => Crop(TestImages.PhotoLike(), int.Parse(parts[1].Split('x')[0], CultureInfo.InvariantCulture), int.Parse(parts[1].Split('x')[1], CultureInfo.InvariantCulture)),
            _ => throw new ArgumentException(name),
        };

        var pair = new Pair(reference, dir.File("reference.png"), dir.File("distorted.png")) { Icc = parts[0] == "p3" ? IccTestProfiles.DisplayP3 : null };
        SavePng(reference, pair.ReferencePath, pair.Icc);
        string distortion = parts[^1];
        if (distortion.StartsWith("jpeg", StringComparison.Ordinal))
        {
            string jpeg = dir.File("distorted.jpg");
            TestImages.SaveJpegWithWic(reference, jpeg, int.Parse(distortion[4..], CultureInfo.InvariantCulture));
            pair.Distorted = TestImages.LoadWithWic(jpeg);
        }
        else if (distortion.StartsWith("jxl", StringComparison.Ordinal))
        {
            string jxl = dir.File("distorted.jxl"), decoded = dir.File("decoded.png");
            RunTool(TestTools.Require(ToolId.Cjxl), pair.ReferencePath, jxl, "-d", distortion[3..], "-e", "7", "--quiet");
            RunTool(TestTools.Require(ToolId.Djxl), jxl, decoded, "--quiet");
            pair.Distorted = TestImages.LoadWithWic(decoded);
        }
        else
        {
            pair.Distorted = distortion switch
            {
                "quantized" => QuantizeAlpha(reference),
                "rgb" => HideInvisibleRgb(reference),
                "translucent" => Translucent(reference),
                "noise" => AddNoise(reference, 300, seed: 6),
                _ => throw new ArgumentException(name),
            };
        }

        SavePng(pair.Distorted, pair.DistortedPath, pair.Icc);
        return pair;
    }

    /// <summary>
    /// Saves a PNG with WIC; with <paramref name="icc"/>, its sRGB/gAMA/cHRM chunks are replaced by an iCCP chunk
    /// holding the profile, so decoders read the pixels in that color space.
    /// </summary>
    private static void SavePng(PixelBuffer px, string path, byte[]? icc)
    {
        TestImages.SavePngWithWic(px, path);
        if (icc is null)
        {
            return;
        }

        byte[] png = File.ReadAllBytes(path);
        using var compressed = new MemoryStream();
        compressed.Write("Test\0\0"u8); // profile name, NUL, compression method 0
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(icc);
        }

        using var result = new MemoryStream();
        result.Write(png, 0, 8);
        for (int o = 8; o < png.Length;)
        {
            int total = 12 + BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(o));
            string type = Encoding.ASCII.GetString(png, o + 4, 4);
            if (type is not ("sRGB" or "gAMA" or "cHRM"))
            {
                result.Write(png, o, total);
            }

            if (type == "IHDR")
            {
                WriteChunk(result, "iCCP"u8, compressed.ToArray());
            }

            o += total;
        }

        File.WriteAllBytes(path, result.ToArray());
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        s.Write(length);
        s.Write(type);
        s.Write(data);
        uint crc = Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        s.Write(length);
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc;
    }

    /// <summary>A center crop (1024 × 768) of a Windows wallpaper photo, or the whole photo.</summary>
    private static PixelBuffer Photo(int index, bool full)
    {
        var photos = TestImages.SystemPhotos(6);
        if (photos.Count <= index)
        {
            Assert.Skip("Not enough system photos under C:\\Windows\\Web.");
        }

        var px = TestImages.LoadWithWic(photos[index]);
        if (full)
        {
            return px;
        }

        using (px)
        {
            return Crop(px, 1024, 768);
        }
    }

    private static PixelBuffer Crop(PixelBuffer src, int w, int h)
    {
        w = Math.Min(w, src.Width);
        h = Math.Min(h, src.Height);
        int x0 = (src.Width - w) / 2, y0 = (src.Height - h) / 2;
        var px = PixelBuffer.Create(w, h, src.Depth);
        for (int y = 0; y < h; y++)
        {
            src.RowBytes(y0 + y).Slice(x0 * src.BytesPerPixel, w * src.BytesPerPixel).CopyTo(px.RowBytes(y));
        }

        return px;
    }

    private static PixelBuffer QuantizeAlpha(PixelBuffer src)
    {
        var px = AddNoise(src, 6, seed: 3);
        for (int y = 0; y < px.Height; y++)
        {
            foreach (ref var p in px.Row32(y))
            {
                p.A = (byte)(p.A / 16 * 17);
            }
        }

        return px;
    }

    private static PixelBuffer HideInvisibleRgb(PixelBuffer src)
    {
        var px = src.Clone();
        for (int y = 0; y < px.Height; y++)
        {
            foreach (ref var p in px.Row32(y))
            {
                if (p.A == 0)
                {
                    p = new Rgba32((byte)(255 - p.R), 17, 230, 0);
                }
            }
        }

        return px;
    }

    private static PixelBuffer Translucent(PixelBuffer src)
    {
        var px = src.Clone();
        for (int y = 0; y < px.Height; y++)
        {
            var row = px.Row32(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x].A = (byte)(255 - ((x + y) % 64));
            }
        }

        return px;
    }

    private sealed class Pair(PixelBuffer reference, string referencePath, string distortedPath) : IDisposable
    {
        public PixelBuffer Reference { get; } = reference;

        public PixelBuffer Distorted { get; set; } = null!;

        public string ReferencePath { get; } = referencePath;

        public string DistortedPath { get; } = distortedPath;

        public byte[]? Icc { get; init; }

        public void Dispose()
        {
            Reference.Dispose();
            Distorted?.Dispose();
        }
    }
}
