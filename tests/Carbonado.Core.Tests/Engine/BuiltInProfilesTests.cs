using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Tests.Engine;

public class BuiltInProfilesTests
{
    [Fact]
    public void Profiles_AllValid()
    {
        var all = BuiltInProfiles.All;

        Assert.Equal(all.Count, all.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, p =>
        {
            Assert.True(p.BuiltIn, p.Id);
            Assert.False(string.IsNullOrWhiteSpace(p.Id));
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.NameIt));
            Assert.NotEmpty(p.Formats);
            Assert.DoesNotContain(OutputFormat.Gif, p.Formats);
            switch (p.Mode)
            {
                case QualityMode.TargetScore:
                    Assert.InRange(p.TargetScore, 30, 99);
                    break;
                case QualityMode.TargetSize:
                    Assert.True(p.TargetBytes > 0, p.Id);
                    break;
                case QualityMode.Lossless:
                    break;
            }

            if (p.Strategy == FormatStrategy.Fixed)
            {
                Assert.NotEmpty(p.Formats);
            }

            if (p.Resize is { } r)
            {
                Assert.True(r.MaxWidth > 0 || r.MaxHeight > 0, p.Id);
            }
        });
    }

    [Fact]
    public void Profiles_MatchTheSpecTable()
    {
        Assert.Equal(["flawless", "vvs", "vs", "si", "i", "web", "keep", "jpeg-archive", "social", "email", "legacy-1", "legacy-2", "legacy-3", "legacy-4"], BuiltInProfiles.All.Select(p => p.Id));
        Assert.Equal([90.0, 85, 78, 65], new[] { "vvs", "vs", "si", "i" }.Select(id => BuiltInProfiles.Find(id)!.TargetScore));
        Assert.Equal(QualityMode.Lossless, BuiltInProfiles.Flawless.Mode);
        Assert.Equal([OutputFormat.Jxl, OutputFormat.WebP, OutputFormat.Png], BuiltInProfiles.Flawless.Formats);
        Assert.Equal([OutputFormat.Avif, OutputFormat.WebP, OutputFormat.Jpeg, OutputFormat.Png], BuiltInProfiles.Web.Formats);
        Assert.Equal(FormatStrategy.KeepSource, BuiltInProfiles.Keep.Strategy);
        Assert.Equal([OutputFormat.Jxl], BuiltInProfiles.JpegArchive.Formats);
        Assert.Equal(new ResizeSpec(2048, 2048), BuiltInProfiles.Social.Resize);
        Assert.Equal(80, BuiltInProfiles.Social.TargetScore);
        Assert.Equal(300 * 1024, BuiltInProfiles.Email.TargetBytes);
        Assert.Equal(new ResizeSpec(1600, 1600), BuiltInProfiles.Email.Resize);
        Assert.Equal([OutputFormat.Jpeg, OutputFormat.WebP], BuiltInProfiles.Email.Formats);
    }

    [Fact]
    public void Profiles_DefaultFindAndLegacy()
    {
        Assert.Same(BuiltInProfiles.Vs, BuiltInProfiles.Default);
        Assert.Same(BuiltInProfiles.JpegArchive, BuiltInProfiles.Find("JPEG-ARCHIVE"));
        Assert.Null(BuiltInProfiles.Find("nope"));

        var formats = new[] { OutputFormat.Jpeg, OutputFormat.WebP, OutputFormat.Avif, OutputFormat.Jxl };
        for (int level = 1; level <= 4; level++)
        {
            var legacy = BuiltInProfiles.Legacy(level);
            Assert.Equal($"legacy-{level}", legacy.Id);
            Assert.Equal(80, legacy.TargetScore);
            Assert.Equal(formats[level - 1], legacy.Formats[0]);
            Assert.Same(legacy, BuiltInProfiles.Find(legacy.Id));
        }
    }
}
