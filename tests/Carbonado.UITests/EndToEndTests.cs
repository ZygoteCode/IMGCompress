using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Carbonado.UITests;

/// <summary>
/// End-to-end tests on the real executable. They share the desktop, so they run sequentially (see AssemblyInfo).
/// Budgets are generous in Debug and strict in Release (the shipped configuration, ReadyToRun when published).
/// </summary>
public class EndToEndTests
{
    private static bool IsRelease => AppSession.ExecutablePath.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase)
                                     || AppSession.ExecutablePath.Contains(@"\artifacts\", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Startup_FirstFrameAndInteractiveWithinBudget()
    {
        // Warm-up launch so disk caches are hot (the budget is for warm starts).
        using (AppSession.Launch(["--intro", "off"]))
        {
        }

        using var s = AppSession.Launch(["--intro", "off"]);
        var marks = s.StartupMarks();
        Assert.True(marks.ContainsKey("first-frame"), "first-frame mark missing");
        // Ledger ruling: the WPF floor on this hardware is ~550 ms (runtime, Application, render-engine boot, first
        // layout and present); 900 ms leaves room for a loaded machine without hiding a regression.
        double budget = IsRelease ? 900 : 2000;
        Assert.True(marks["first-frame"] < budget, $"first frame at {marks["first-frame"]:F0} ms (budget {budget} ms)");
        Assert.True(marks.GetValueOrDefault("interactive", double.MaxValue) < budget + 400, "interactive too late");
    }

    [Fact]
    public void Intro_Short_PlaysAndHandsOff()
    {
        using var s = AppSession.Launch(["--intro", "short"]);
        Assert.NotNull(s.Find("Nav.Forge", TimeSpan.FromSeconds(5)));
        Assert.True(s.WaitGone("Intro.Overlay", TimeSpan.FromSeconds(4)), "intro did not finish");
    }

    [Fact]
    public void Intro_Cinematic_SkipsWithEscape()
    {
        using var s = AppSession.Launch(["--intro", "cinematic"]);
        Assert.NotNull(s.Find("Intro.Overlay", TimeSpan.FromSeconds(3)));
        s.Main.Focus();
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        var sw = Stopwatch.StartNew();
        Assert.True(s.WaitGone("Intro.Overlay", TimeSpan.FromSeconds(2)), "Esc did not skip the intro");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"skip took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Navigate_AllPages()
    {
        using var s = AppSession.Launch(["--intro", "off"]);
        foreach (string page in new[] { "Compare", "Profiles", "Archive", "Engines", "Settings", "About", "Forge" })
        {
            s.Click("Nav." + page);
            Assert.NotNull(s.Find("Page." + page, TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public void Compress_FilesFromArguments_WritesVerifiedOutputs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "carbonado-uitests-input", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "gradient.png");
            string b = Path.Combine(dir, "pattern.png");
            WritePng(a, (x, y) => Color.FromRgb((byte)x, (byte)y, (byte)((x + y) / 2)));
            WritePng(b, (x, y) => ((x / 16) + (y / 16)) % 2 == 0 ? Colors.White : Color.FromRgb(56, 225, 255));

            using var s = AppSession.Launch(["--intro", "off", "--profile", "vs", "--start", a, b]);
            string outDir = Path.Combine(dir, "Carbonado");
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(90) && (!Directory.Exists(outDir) || Directory.GetFiles(outDir).Length < 2))
            {
                Thread.Sleep(200);
            }

            var outputs = Directory.Exists(outDir) ? Directory.GetFiles(outDir) : [];
            Assert.Equal(2, outputs.Length);
            Assert.All(outputs, o => Assert.True(new FileInfo(o).Length > 0));
            Assert.True(outputs.Sum(o => new FileInfo(o).Length) < new FileInfo(a).Length + new FileInfo(b).Length);
            Assert.NotNull(s.Find("Forge.Queue"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Close_WithRunningJobs_IsFast_AndLeavesNoEncoderProcesses()
    {
        string dir = Path.Combine(Path.GetTempPath(), "carbonado-uitests-close", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            string f = Path.Combine(dir, $"big{i}.png");
            WritePng(f, (x, y) => Color.FromRgb((byte)((x * 7) + i), (byte)((y * 3) ^ x), (byte)(x * y)), 2400, 1600);
            files.Add(f);
        }

        try
        {
            var before = EncoderPids();
            using var s = AppSession.Launch(["--intro", "off", "--profile", "vvs", "--start", .. files]);
            Thread.Sleep(2500);
            s.Main.Close();
            var dialogButton = s.Find("Dialog.Primary", TimeSpan.FromSeconds(3));
            var sw = Stopwatch.StartNew();
            dialogButton?.Click();
            Assert.True(s.WaitForExit(TimeSpan.FromSeconds(5)), "app did not exit");
            double budget = IsRelease ? 400 : 1500;
            Assert.True(sw.ElapsedMilliseconds < budget, $"shutdown took {sw.ElapsedMilliseconds} ms");
            Thread.Sleep(300);
            var leaked = EncoderPids().Except(before).ToList();
            Assert.True(leaked.Count == 0, "orphan encoder processes: " + string.Join(",", leaked));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static HashSet<int> EncoderPids() =>
        Process.GetProcesses()
            .Where(p => p.ProcessName is "cjxl" or "djxl" or "avifenc" or "avifdec" or "cwebp" or "dwebp" or "oxipng" or "ect" or "pngquant" or "ffmpeg" or "gif2webp")
            .Select(p => p.Id)
            .ToHashSet();

    private static void WritePng(string path, Func<int, int, Color> pixel, int width = 256, int height = 256)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = pixel(x, y);
                int o = ((y * width) + x) * 4;
                bytes[o] = c.B;
                bytes[o + 1] = c.G;
                bytes[o + 2] = c.R;
                bytes[o + 3] = 255;
            }
        }

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
