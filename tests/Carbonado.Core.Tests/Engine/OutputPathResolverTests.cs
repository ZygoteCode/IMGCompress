using System.Globalization;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Tests.Engine;

public class OutputPathResolverTests
{
    private const string Input = @"C:\Photos\Trip\beach.JPG";

    [Fact]
    public void Resolver_DefaultsToCarbonadoSubfolderNextToTheSource()
    {
        string path = OutputPathResolver.Resolve(Input, null, new OutputPlan(), OutputFormat.Avif, "vs", 86.9);

        Assert.Equal(@"C:\Photos\Trip\Carbonado\beach.avif", path);
    }

    [Fact]
    public void Resolver_SameFolder()
    {
        string path = OutputPathResolver.Resolve(Input, null, new OutputPlan(OutputLocation.SameFolder), OutputFormat.Jxl, "vs", null);

        Assert.Equal(@"C:\Photos\Trip\beach.jxl", path);
    }

    [Fact]
    public void Resolver_CustomFolder_MirrorsTheTreeUnderTheInputRoot()
    {
        var plan = new OutputPlan(OutputLocation.CustomFolder, CustomFolder: @"D:\Out", MirrorTree: true);

        Assert.Equal(@"D:\Out\Trip\beach.webp", OutputPathResolver.Resolve(Input, @"C:\Photos", plan, OutputFormat.WebP, "vs", null));
        Assert.Equal(@"D:\Out\beach.webp", OutputPathResolver.Resolve(Input, @"C:\Photos\Trip", plan, OutputFormat.WebP, "vs", null));

        // Outside the root, or without one, the tree is not mirrored.
        Assert.Equal(@"D:\Out\beach.webp", OutputPathResolver.Resolve(Input, @"C:\Other", plan, OutputFormat.WebP, "vs", null));
        Assert.Equal(@"D:\Out\beach.webp", OutputPathResolver.Resolve(Input, null, plan, OutputFormat.WebP, "vs", null));
        Assert.Equal(@"D:\Out\beach.webp", OutputPathResolver.Resolve(Input, @"C:\Photos", plan with { MirrorTree = false }, OutputFormat.WebP, "vs", null));
    }

    [Fact]
    public void Resolver_CustomFolderIsRequired()
    {
        Assert.Throws<ArgumentException>(() => OutputPathResolver.Resolve(Input, null, new OutputPlan(OutputLocation.CustomFolder), OutputFormat.Png, "vs", null));
    }

    [Fact]
    public void Resolver_ExpandsEveryToken()
    {
        var plan = new OutputPlan(OutputLocation.SameFolder, NamePattern: "{name}-{ext}-{format}-{profile}-{score}-{date}");
        string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string lossy = OutputPathResolver.Resolve(Input, null, plan, OutputFormat.Jpeg, "web", 86.94);
        string lossless = OutputPathResolver.Resolve(Input, null, plan, OutputFormat.Png, "flawless", null);

        Assert.Equal($@"C:\Photos\Trip\beach-jpg-jpeg-web-86.9-{date}.jpg", lossy);
        Assert.Equal($@"C:\Photos\Trip\beach-jpg-png-flawless-lossless-{date}.png", lossless);
    }

    [Fact]
    public void Resolver_TokensAreCaseInsensitiveAndExpandedOnce()
    {
        var plan = new OutputPlan(OutputLocation.SameFolder, NamePattern: "{NAME}_{Format}_{unknown}");

        // A file literally named after a token is not expanded again.
        string path = OutputPathResolver.Resolve(@"C:\x\{date}.png", null, plan, OutputFormat.Avif, "vs", null);

        Assert.Equal(@"C:\x\{date}_avif_{unknown}.avif", path);
    }

    [Fact]
    public void Resolver_ScoreUsesTheInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
            string path = OutputPathResolver.Resolve(Input, null, new OutputPlan(NamePattern: "{name}@{score}"), OutputFormat.Avif, "vs", 85.26);
            Assert.EndsWith(@"\beach@85.3.avif", path);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("a:b*c?d\"e<f>g|h", "a_b_c_d_e_f_g_h")]
    [InlineData("  name.  ", "name")]
    [InlineData("...", "beach")]
    [InlineData("", "beach")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.backup", "_nul.backup")]
    [InlineData("sub/dir\\x", "sub_dir_x")]
    public void Resolver_ReplacesInvalidCharacters(string pattern, string expected)
    {
        string path = OutputPathResolver.Resolve(Input, null, new OutputPlan(OutputLocation.SameFolder, NamePattern: pattern), OutputFormat.Png, "vs", null);

        Assert.Equal(@"C:\Photos\Trip\" + expected + ".png", path);
    }

    [Fact]
    public void Resolver_SanitizesTheSubfolderName()
    {
        string path = OutputPathResolver.Resolve(Input, null, new OutputPlan(SubfolderName: "out:put?"), OutputFormat.Png, "vs", null);
        string fallback = OutputPathResolver.Resolve(Input, null, new OutputPlan(SubfolderName: "  "), OutputFormat.Png, "vs", null);

        Assert.Equal(@"C:\Photos\Trip\out_put_\beach.png", path);
        Assert.Equal(@"C:\Photos\Trip\Carbonado\beach.png", fallback);
    }

    [Fact]
    public void Resolver_ReplaceOriginals_GoesNextToTheOriginalWithTheNewExtension()
    {
        var plan = new OutputPlan(OutputLocation.CustomFolder, CustomFolder: @"D:\Out", NamePattern: "{name}-small", ReplaceOriginals: true);

        Assert.Equal(@"C:\Photos\Trip\beach.avif", OutputPathResolver.Resolve(Input, @"C:\Photos", plan, OutputFormat.Avif, "vs", 90));
        Assert.Equal(@"C:\Photos\Trip", OutputPathResolver.ResolveFolder(Input, @"C:\Photos", plan));
    }

    [Fact]
    public void Resolver_RelativeInputsAreMadeAbsolute()
    {
        string path = OutputPathResolver.Resolve("pic.png", null, new OutputPlan(), OutputFormat.Jxl, "vs", null);

        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "Carbonado", "pic.jxl"), path);
    }
}
