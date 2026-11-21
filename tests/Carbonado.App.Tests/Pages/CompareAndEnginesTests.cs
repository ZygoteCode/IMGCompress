using System.Windows.Media;
using System.Windows.Media.Imaging;
using Carbonado.App.Localization;
using Carbonado.App.Pages.Compare;
using Carbonado.App.Pages.Engines;
using Carbonado.App.Tests.TestSupport;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tools;

namespace Carbonado.App.Tests.Pages;

public sealed class CompareAndEnginesTests : IDisposable
{
    public CompareAndEnginesTests() => Loc.SetLookupOverride(k => k);

    public void Dispose() => Loc.SetLookupOverride(null);

    [Fact]
    public async Task Compare_ShowLoadsImages_HeatmapOnDemand()
    {
        var loader = new FakeLoader();
        var vm = new CompareViewModel(loader);
        Assert.True(vm.IsEmpty);
        var result = new CompressionResult(Guid.NewGuid(), "a.png", "a.avif", JobOutcome.Compressed, 1000, 200, OutputFormat.Avif, "avif.avifenc", 88.2,
            VerificationKind.PerceptualScore, TimeSpan.FromSeconds(1), [], null, 4, 4);

        vm.Show(new ComparisonSource("a.png", "a.avif", result));
        await loader.LastLoad!;

        Assert.False(vm.IsEmpty);
        Assert.NotNull(vm.Before);
        Assert.NotNull(vm.After);
        Assert.Equal(4, vm.PixelWidth);
        Assert.Equal("AVIF", vm.FormatText);
        Assert.Equal("−80%", vm.SavedText);
        Assert.Equal(0, loader.HeatmapCalls);

        vm.Mode = ComparisonMode.Heatmap;
        await loader.LastHeatmap!;
        Assert.Equal(1, loader.HeatmapCalls);
        Assert.NotNull(vm.Heatmap);
        Assert.Equal(87.5, vm.MeasuredScore);

        vm.Show(new ComparisonSource("a.png", "a.avif", result));
        Assert.Single(vm.Sources);
    }

    [Fact]
    public async Task Compare_LoadErrorIsReported()
    {
        var loader = new FakeLoader { Fail = true };
        var vm = new CompareViewModel(loader);
        vm.Show(new ComparisonSource("bad.png", "bad.avif", null));
        try
        {
            await loader.LastLoad!;
        }
        catch (InvalidDataException)
        {
        }

        await Task.Yield();
        Assert.Equal("L.Compare.Error", vm.Error);
        Assert.Null(vm.Before);
    }

    [Fact]
    public void Engines_RowsForEveryToolWithStatus()
    {
        var catalog = new FakeTools
        {
            Tools =
            [
                new ToolInfo(ToolId.Cjxl, @"C:\t\cjxl.exe", "libjxl", "0.13.0", ToolIntegrity.Verified, "aa"),
                new ToolInfo(ToolId.Ffmpeg, @"C:\t\ffmpeg.exe", "legacy", null, ToolIntegrity.Unknown, "bb"),
                new ToolInfo(ToolId.Avifenc, @"C:\t\avifenc.exe", "libavif", "1.4.2", ToolIntegrity.Mismatch, "cc"),
            ],
            AvailableFormats = [OutputFormat.Jxl, OutputFormat.Png],
        };
        var vm = new EnginesViewModel(catalog, new FakeShell(), new ImmediateDispatcher());

        Assert.Equal(Enum.GetValues<ToolId>().Length, vm.Rows.Count);
        Assert.Equal(EngineStatus.Verified, vm.Rows.Single(r => r.Id == ToolId.Cjxl).Status);
        Assert.Equal(EngineStatus.Unverified, vm.Rows.Single(r => r.Id == ToolId.Ffmpeg).Status);
        Assert.Equal(EngineStatus.Modified, vm.Rows.Single(r => r.Id == ToolId.Avifenc).Status);
        Assert.Equal(EngineStatus.Missing, vm.Rows.Single(r => r.Id == ToolId.Cwebp).Status);
        Assert.True(vm.HasProblems);
        Assert.Equal(1, vm.VerifiedCount);
        Assert.True(vm.Formats.Single(f => f.Format == OutputFormat.Jxl).Available);
        Assert.False(vm.Formats.Single(f => f.Format == OutputFormat.Avif).Available);
    }

    private sealed class FakeLoader : IComparisonLoader
    {
        public bool Fail { get; init; }

        public Task<ComparisonImages>? LastLoad { get; private set; }

        public Task<(ImageSource Map, double Score)>? LastHeatmap { get; private set; }

        public int HeatmapCalls { get; private set; }

        public Task<ComparisonImages> LoadAsync(string original, string output, CancellationToken ct)
        {
            LastLoad = Fail
                ? Task.FromException<ComparisonImages>(new InvalidDataException("corrupt"))
                : Task.FromResult(new ComparisonImages(Pixel(), Pixel(), 4, 4));
            return LastLoad;
        }

        public Task<(ImageSource Map, double Score)> HeatmapAsync(string original, string output, CancellationToken ct)
        {
            HeatmapCalls++;
            LastHeatmap = Task.FromResult<(ImageSource, double)>((Pixel(), 87.5));
            return LastHeatmap;
        }

        private static BitmapSource Pixel()
        {
            var bmp = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16);
            bmp.Freeze();
            return bmp;
        }
    }

    private sealed class FakeTools : IToolCatalog
    {
        public IReadOnlyList<ToolInfo> Tools { get; init; } = [];

        public string ToolsFolder => @"C:\t";

        public IReadOnlyCollection<OutputFormat> AvailableFormats { get; init; } = [];

        public event EventHandler? Changed;

        public Task RefreshAsync(CancellationToken ct)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
