using Carbonado.Core.Analysis;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Engine;

public sealed class CandidatePlannerTests : IDisposable
{
    private static readonly ImageAnalysis Photo = new(640, 480, AlphaUsage.Opaque, false, -1, false, false, ContentKind.Photo, 0.1, 0.05);
    private static readonly ImageAnalysis Graphic = new(320, 200, AlphaUsage.Opaque, false, 7, true, false, ContentKind.Graphic, 0.05, 0.9);
    private static readonly ImageAnalysis Mixed = new(320, 200, AlphaUsage.Opaque, false, 9000, false, false, ContentKind.Mixed, 0.1, 0.4);
    private static readonly ImageAnalysis AlphaPhoto = Photo with { Alpha = AlphaUsage.Translucent };
    private static readonly ImageAnalysis GrayPhoto = Photo with { IsGrayscale = true };
    private static readonly ImageAnalysis Deep = Photo with { Has16BitPrecision = true };
    private static readonly ImageAnalysis Tiny = new(6, 5, AlphaUsage.Opaque, false, 30, true, false, ContentKind.Graphic, 0, 0.5);

    private readonly TempDir _dir = new("planner");
    private readonly EncoderRegistry _all;

    public CandidatePlannerTests()
    {
        _all = Registry(Enum.GetValues<ToolId>());
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Planner_Lossless_PlansJxlWebPAndPngLosslessOnly()
    {
        var plans = Plan(BuiltInProfiles.Flawless, Photo, ImageFormat.Bmp);

        Assert.Equal(["jxl.cjxl Lossless", "webp.cwebp Lossless", "png.oxipng Lossless", "png.ect Lossless"], Labels(plans));
    }

    [Fact]
    public void Planner_Lossless_PngSourceIsOptimizedInPlaceOfReencoded()
    {
        var plans = Plan(BuiltInProfiles.Flawless, Graphic, ImageFormat.Png);

        Assert.Equal(["jxl.cjxl Lossless", "webp.cwebp Lossless", "png.oxipng Optimize", "png.ect Optimize"], Labels(plans));
    }

    [Fact]
    public void Planner_Lossless_FiltersByAllowedFormats()
    {
        var profile = BuiltInProfiles.Flawless with { Formats = [OutputFormat.WebP, OutputFormat.Png] };

        var plans = Plan(profile, Photo, ImageFormat.Tiff);

        Assert.Equal(["webp.cwebp Lossless", "png.oxipng Lossless", "png.ect Lossless"], Labels(plans));
    }

    [Fact]
    public void Planner_Lossless_16BitImagesSkipEightBitOnlyFormats()
    {
        var plans = Plan(BuiltInProfiles.Flawless, Deep, ImageFormat.Png);

        Assert.DoesNotContain(plans, p => p.Format == OutputFormat.WebP);
        Assert.Contains(plans, p => p.Encoder.Id == "jxl.cjxl" && p.Kind == EncodeKind.Lossless);
    }

    [Fact]
    public void Planner_JpegSource_AddsTranscodeAndDropsPixelReencodes()
    {
        var plans = Plan(BuiltInProfiles.Flawless, Photo, ImageFormat.Jpeg);

        // A reversible transcode always beats re-encoding the decoded JPEG pixels losslessly.
        Assert.Equal(["jxl.cjxl JpegTranscode"], Labels(plans));
    }

    [Fact]
    public void Planner_JpegSource_TranscodeAlsoRacesLossyCandidates()
    {
        var plans = Plan(BuiltInProfiles.Vs, Photo, ImageFormat.Jpeg);

        Assert.Contains(plans, p => p.Kind == EncodeKind.JpegTranscode && p.Format == OutputFormat.Jxl);
        Assert.Contains(plans, p => p.Kind == EncodeKind.Optimize && p.Format == OutputFormat.Jpeg);
        Assert.Contains(plans, p => p.Kind == EncodeKind.Lossy && p.Format == OutputFormat.Jxl);
    }

    [Fact]
    public void Planner_JpegSource_NoTranscodeWhenDisallowedOrJxlNotAllowed()
    {
        var noTranscode = Plan(BuiltInProfiles.Vs with { AllowJpegTranscode = false }, Photo, ImageFormat.Jpeg);
        var noJxl = Plan(BuiltInProfiles.Web, Photo, ImageFormat.Jpeg);

        Assert.DoesNotContain(noTranscode, p => p.Kind == EncodeKind.JpegTranscode);
        Assert.DoesNotContain(noJxl, p => p.Kind == EncodeKind.JpegTranscode || p.Format == OutputFormat.Jxl);
    }

    [Fact]
    public void Planner_JpegArchive_IsTranscodeOnly()
    {
        var plans = Plan(BuiltInProfiles.JpegArchive, Photo, ImageFormat.Jpeg);

        Assert.Equal(["jxl.cjxl JpegTranscode"], Labels(plans));
    }

    [Fact]
    public void Planner_Photo_RacesLossyFormatsOnly()
    {
        var plans = Plan(BuiltInProfiles.Vs, Photo, ImageFormat.Png);

        // No lossless candidates and no palette PNG for photos in a race: they cannot win.
        Assert.Equal(["jxl.cjxl Lossy", "avif.avifenc Lossy", "webp.cwebp Lossy", "jpeg.sjpeg Lossy"], Labels(plans));
        Assert.All(plans, p => Assert.Equal(ChromaMode.Auto, p.Chroma));
    }

    [Fact]
    public void Planner_Graphic_AddsLosslessCandidatesInLossyModes()
    {
        var plans = Plan(BuiltInProfiles.Vs, Graphic, ImageFormat.Png);

        Assert.Equal(
            ["jxl.cjxl Lossy", "jxl.cjxl Lossless", "avif.avifenc Lossy", "webp.cwebp Lossy", "webp.cwebp Lossless", "jpeg.sjpeg Lossy",
             "png.pngquant Lossy", "png.oxipng Optimize", "png.ect Optimize"],
            Labels(plans));
    }

    [Fact]
    public void Planner_Mixed_GetsPalettePngButNoLossless()
    {
        var plans = Plan(BuiltInProfiles.Vs, Mixed, ImageFormat.Png);
        var smooth = Plan(BuiltInProfiles.Vs, Mixed with { UniqueColors = -1 }, ImageFormat.Png);

        Assert.Contains(plans, p => p.Encoder.Id == "png.pngquant");
        Assert.DoesNotContain(plans, p => p.Kind is EncodeKind.Lossless or EncodeKind.Optimize);

        // Beyond 65 536 colours a palette cannot reach high scores: not raced.
        Assert.DoesNotContain(smooth, p => p.Encoder.Id == "png.pngquant");
    }

    [Fact]
    public void Planner_TargetSize_PlansLossyCandidatesOfTheAllowedFormats()
    {
        var plans = Plan(BuiltInProfiles.Email, Photo, ImageFormat.Jpeg);

        Assert.Equal(new[] { "jpeg.sjpeg Lossy", "jpeg.sjpeg Optimize", "webp.cwebp Lossy" }.Order(), Labels(plans).Order());
        Assert.DoesNotContain(plans, p => p.Kind == EncodeKind.JpegTranscode);
    }

    [Fact]
    public void Planner_Alpha_ExcludesJpeg()
    {
        var plans = Plan(BuiltInProfiles.Vs, AlphaPhoto, ImageFormat.Png);

        Assert.DoesNotContain(plans, p => p.Format == OutputFormat.Jpeg);
        Assert.Contains(plans, p => p.Format == OutputFormat.Avif);
    }

    [Fact]
    public void Planner_Fixed_TakesTheFirstFormatThatCanRepresentTheImage()
    {
        var profile = BuiltInProfiles.Vs with { Strategy = FormatStrategy.Fixed, Formats = [OutputFormat.Jpeg, OutputFormat.WebP, OutputFormat.Avif] };

        var opaque = Plan(profile, Photo, ImageFormat.Png);
        var alpha = Plan(profile, AlphaPhoto, ImageFormat.Png);

        Assert.All(opaque, p => Assert.Equal(OutputFormat.Jpeg, p.Format));
        Assert.All(alpha, p => Assert.Equal(OutputFormat.WebP, p.Format));
        Assert.NotEmpty(alpha);
    }

    [Fact]
    public void Planner_Fixed_PngAlwaysOffersLosslessEvenForPhotos()
    {
        var profile = BuiltInProfiles.Vs with { Strategy = FormatStrategy.Fixed, Formats = [OutputFormat.Png] };

        var plans = Plan(profile, Photo, ImageFormat.Bmp);

        Assert.Equal(["png.pngquant Lossy", "png.oxipng Lossless", "png.ect Lossless"], Labels(plans));
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg, new[] { "jpeg.sjpeg Lossy", "jpeg.sjpeg Optimize" })]
    [InlineData(ImageFormat.Png, new[] { "png.pngquant Lossy", "png.oxipng Optimize", "png.ect Optimize" })]
    [InlineData(ImageFormat.WebP, new[] { "webp.cwebp Lossy" })]
    [InlineData(ImageFormat.Avif, new[] { "avif.avifenc Lossy" })]
    [InlineData(ImageFormat.Heic, new[] { "avif.avifenc Lossy" })]
    [InlineData(ImageFormat.Jxl, new[] { "jxl.cjxl Lossy" })]
    [InlineData(ImageFormat.Bmp, new[] { "png.pngquant Lossy", "png.oxipng Lossless", "png.ect Lossless" })]
    [InlineData(ImageFormat.Tiff, new[] { "png.pngquant Lossy", "png.oxipng Lossless", "png.ect Lossless" })]
    [InlineData(ImageFormat.Gif, new string[0])]
    public void Planner_KeepSource_SameFormatOptimizeAndLossy(ImageFormat source, string[] expected)
    {
        var plans = Plan(BuiltInProfiles.Keep, Photo, source);

        Assert.Equal(expected.Order(), Labels(plans).Order());
    }

    [Fact]
    public void Planner_KeepSource_LosslessModeKeepsOnlyExactCandidates()
    {
        var profile = BuiltInProfiles.Keep with { Mode = QualityMode.Lossless };

        Assert.Equal(["jpeg.sjpeg Optimize"], Labels(Plan(profile, Photo, ImageFormat.Jpeg)));
        Assert.Equal(["png.oxipng Optimize", "png.ect Optimize"], Labels(Plan(profile, Photo, ImageFormat.Png)));
        Assert.Equal(["webp.cwebp Lossless"], Labels(Plan(profile, Photo, ImageFormat.WebP)));

        // AVIF lossless is planned only when AVIF itself was asked for.
        Assert.Equal(["avif.avifenc Lossless"], Labels(Plan(profile, Photo, ImageFormat.Avif)));
        Assert.DoesNotContain(Plan(BuiltInProfiles.Flawless with { Formats = [OutputFormat.Avif, OutputFormat.Jxl] }, Photo, ImageFormat.Png), p => p.Format == OutputFormat.Avif);
    }

    [Fact]
    public void Planner_Animated_OnlyAnimationPreservingLosslessPaths()
    {
        var gif = Plan(BuiltInProfiles.Vs, Graphic, ImageFormat.Gif, animated: true);
        var apng = Plan(BuiltInProfiles.Vs, Graphic, ImageFormat.Apng, animated: true);
        var keepApng = Plan(BuiltInProfiles.Keep, Graphic, ImageFormat.Apng, animated: true);
        var webp = Plan(BuiltInProfiles.Vs, Graphic, ImageFormat.WebP, animated: true);
        var jpegOnly = Plan(BuiltInProfiles.Vs with { Formats = [OutputFormat.Jpeg, OutputFormat.Avif] }, Graphic, ImageFormat.Gif, animated: true);

        Assert.Equal(["jxl.cjxl Lossless", "webp.gif2webp Lossless"], Labels(gif));
        Assert.Equal(["jxl.cjxl Lossless", "png.oxipng Optimize"], Labels(apng));
        Assert.Equal(["png.oxipng Optimize"], Labels(keepApng));
        Assert.Empty(webp);
        Assert.Empty(jpegOnly);
    }

    [Fact]
    public void Planner_StillGif_NeverUsesGif2webp()
    {
        var plans = Plan(BuiltInProfiles.Vs, Graphic, ImageFormat.Gif);

        Assert.DoesNotContain(plans, p => p.Encoder.Id == "webp.gif2webp");
        Assert.Contains(plans, p => p.Encoder.Id == "webp.cwebp");
    }

    [Fact]
    public void Planner_Insane_AddsAvifChromaVariant()
    {
        var insane = BuiltInProfiles.Vs with { Effort = EffortLevel.Insane };

        var plans = Plan(insane, Photo, ImageFormat.Png);
        var gray = Plan(insane, GrayPhoto, ImageFormat.Png);

        Assert.Equal([ChromaMode.Yuv444, ChromaMode.Yuv420], plans.Where(p => p.Format == OutputFormat.Avif).Select(p => p.Chroma));
        Assert.Equal([ChromaMode.Auto], gray.Where(p => p.Format == OutputFormat.Avif).Select(p => p.Chroma));
        Assert.Single(Plan(BuiltInProfiles.Vs, Photo, ImageFormat.Png), p => p.Format == OutputFormat.Avif);
    }

    [Fact]
    public void Planner_TinyImages_GetLosslessCandidatesOnly()
    {
        // SSIMULACRA2 needs 8×8: lossy candidates could not be verified.
        var plans = Plan(BuiltInProfiles.Vs, Tiny, ImageFormat.Png);

        Assert.NotEmpty(plans);
        Assert.All(plans, p => Assert.NotEqual(EncodeKind.Lossy, p.Kind));
    }

    [Fact]
    public void Planner_FiltersByInstalledTools()
    {
        var registry = Registry([ToolId.Cjxl, ToolId.Djxl, ToolId.Cwebp, ToolId.Dwebp]);

        var plans = CandidatePlanner.Plan(BuiltInProfiles.Vs, Graphic, ImageFormat.Png, false, registry);

        Assert.DoesNotContain(plans, p => p.Format is OutputFormat.Avif or OutputFormat.Jpeg);

        // Without oxipng and ECT the built-in optimizer covers PNG.
        Assert.Contains(plans, p => p.Encoder.Id == "png.builtin");
        Assert.Contains(plans, p => p.Encoder.Id == "jxl.cjxl" && p.Kind == EncodeKind.Lossy);
    }

    [Fact]
    public void Planner_NoDuplicates()
    {
        foreach (var profile in BuiltInProfiles.All)
        {
            foreach (var source in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Gif, ImageFormat.WebP })
            {
                var plans = Plan(profile, Graphic, source);
                Assert.Equal(plans.Count, plans.Select(p => (p.Encoder.Id, p.Kind, p.Chroma)).Distinct().Count());
            }
        }
    }

    private static string[] Labels(IEnumerable<CandidatePlan> plans) => plans.Select(p => p.ToString()).ToArray();

    private IReadOnlyList<CandidatePlan> Plan(CompressionProfile profile, ImageAnalysis analysis, ImageFormat source, bool animated = false) =>
        CandidatePlanner.Plan(profile, analysis, source, animated, _all);

    // A registry over stub executables: the planner never runs them.
    private EncoderRegistry Registry(IEnumerable<ToolId> installed)
    {
        string root = _dir.Sub("tools-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (ToolId id in installed)
        {
            var (package, file) = ToolLocator.Layout(id);
            Directory.CreateDirectory(Path.Combine(root, package));
            File.WriteAllBytes(Path.Combine(root, package, file), []);
        }

        var locator = new ToolLocator(new ToolLocatorOptions([root], VerifyHashes: false, SearchPath: false));
        return new EncoderRegistry(locator, ProcessRunner.Shared);
    }
}
