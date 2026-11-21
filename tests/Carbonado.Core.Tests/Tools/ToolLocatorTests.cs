using System.Text.Json;
using Carbonado.Core.Tests.TestSupport;
using Carbonado.Core.Tools;

namespace Carbonado.Core.Tests.Tools;

public class ToolLocatorTests
{
    [Fact]
    public void Manifest_LoadsRepoManifest()
    {
        string root = RequireRoot();
        var manifest = ToolManifest.Load(Path.Combine(root, "manifest.json"));
        var libjxl = Assert.Single(manifest.Packages, p => p.Name == "libjxl");
        Assert.Equal("0.13.0", libjxl.Version);
        Assert.StartsWith("https://github.com/libjxl/", libjxl.Source);
        Assert.Equal(64, libjxl.ArchiveSha256!.Length);
        Assert.True(libjxl.Files.ContainsKey("libjxl/cjxl.exe"));
        Assert.Equal(64, libjxl.Files["libjxl/cjxl.exe"].Length);
        Assert.Contains(manifest.Packages, p => p.Name == "ffmpeg" && p.ArchiveSha256 is null);
        Assert.Equal(["libjxl", "libavif", "libwebp", "oxipng", "ect", "pngquant", "ffmpeg"], manifest.Packages.Select(p => p.Name));
    }

    [Fact]
    public void Manifest_RejectsMalformedFiles()
    {
        using var dir = new TempDir("manifest");
        File.WriteAllText(dir.File("bad.json"), "{ not json");
        Assert.Throws<InvalidDataException>(() => ToolManifest.Load(dir.File("bad.json")));
        File.WriteAllText(dir.File("shape.json"), """{ "packages": [ { "name": 5 } ] }""");
        Assert.Throws<InvalidDataException>(() => ToolManifest.Load(dir.File("shape.json")));
    }

    [Fact]
    public void Locator_FindsAllRepoTools()
    {
        string root = RequireRoot();
        var locator = new ToolLocator(new ToolLocatorOptions([root], Path.Combine(root, "manifest.json"), SearchPath: false));
        var tools = locator.Discover();
        Assert.Equal(Enum.GetValues<ToolId>(), tools.Select(t => t.Id));
        foreach (var tool in tools)
        {
            Assert.True(tool.Integrity == ToolIntegrity.Verified, $"{tool.Id}: {tool.Integrity}");
            Assert.True(File.Exists(tool.Path));
            Assert.Equal(64, tool.Sha256.Length);
            Assert.NotNull(tool.Version);
            Assert.True(locator.Has(tool.Id));
            Assert.Same(tool, locator.Get(tool.Id));
        }

        Assert.Equal("libjxl", locator.Get(ToolId.Cjxl)!.Package);
        Assert.Equal("0.13.0", locator.Get(ToolId.Cjxl)!.Version);
    }

    [Fact]
    public void Locator_DefaultOptions_FindRepoToolsFromTheBuildOutput()
    {
        RequireRoot();
        var options = ToolLocator.DefaultOptions(AppContext.BaseDirectory);
        Assert.NotNull(options.ManifestPath);
        Assert.Contains(options.SearchRoots, r => File.Exists(Path.Combine(r, "manifest.json")));
        Assert.True(new ToolLocator(options with { VerifyHashes = false }).Has(ToolId.Cjxl));
    }

    [Fact]
    public void Locator_FlagsHashMismatch()
    {
        string cjxl = TestTools.Require(ToolId.Cjxl);
        using var dir = new TempDir("locator");
        string tools = dir.Sub("tools");
        Directory.CreateDirectory(Path.Combine(tools, "libjxl"));
        File.Copy(cjxl, Path.Combine(tools, "libjxl", "cjxl.exe"));

        WriteManifest(Path.Combine(tools, "manifest.json"), "libjxl", "9.9.9", "libjxl/cjxl.exe", new string('0', 64));
        var bad = new ToolLocator(new ToolLocatorOptions([tools], Path.Combine(tools, "manifest.json"), SearchPath: false));
        var info = bad.Get(ToolId.Cjxl);
        Assert.NotNull(info);
        Assert.Equal(ToolIntegrity.Mismatch, info.Integrity);
        Assert.Null(info.Version);
        Assert.False(bad.Has(ToolId.Djxl));
        Assert.Null(bad.Get(ToolId.Djxl));

        string real = new ToolLocator(new ToolLocatorOptions([RequireRoot()], Path.Combine(RequireRoot(), "manifest.json"), SearchPath: false)).Get(ToolId.Cjxl)!.Sha256;
        WriteManifest(Path.Combine(tools, "manifest.json"), "libjxl", "9.9.9", "libjxl/cjxl.exe", real.ToUpperInvariant());
        var good = new ToolLocator(new ToolLocatorOptions([tools], Path.Combine(tools, "manifest.json"), SearchPath: false));
        Assert.Equal(ToolIntegrity.Verified, good.Get(ToolId.Cjxl)!.Integrity);
        Assert.Equal("9.9.9", good.Get(ToolId.Cjxl)!.Version);

        // Hashing can be switched off.
        var unverified = new ToolLocator(new ToolLocatorOptions([tools], Path.Combine(tools, "manifest.json"), VerifyHashes: false, SearchPath: false));
        Assert.Equal(ToolIntegrity.Unknown, unverified.Get(ToolId.Cjxl)!.Integrity);
        Assert.Equal(string.Empty, unverified.Get(ToolId.Cjxl)!.Sha256);
    }

    [Fact]
    public void Locator_UsesLegacyDirAsFallback()
    {
        string pngquant = TestTools.Require(ToolId.Pngquant);
        using var dir = new TempDir("locator");
        string empty = dir.Sub("tools");
        string legacy = dir.Sub("IMGCompress 6");
        File.Copy(pngquant, Path.Combine(legacy, "pngquant.exe"));

        var withoutManifest = new ToolLocator(new ToolLocatorOptions([empty], LegacyDirs: [legacy], SearchPath: false));
        var info = withoutManifest.Get(ToolId.Pngquant);
        Assert.NotNull(info);
        Assert.Equal(Path.Combine(legacy, "pngquant.exe"), info.Path);
        Assert.Equal("legacy", info.Package);
        Assert.Equal(ToolIntegrity.Unknown, info.Integrity);

        // A legacy copy identical to the pinned binary is still recognised as verified.
        string root = RequireRoot();
        var withManifest = new ToolLocator(new ToolLocatorOptions([empty], Path.Combine(root, "manifest.json"), LegacyDirs: [legacy], SearchPath: false));
        Assert.Equal(ToolIntegrity.Verified, withManifest.Get(ToolId.Pngquant)!.Integrity);

        // Search roots win over legacy folders.
        var both = new ToolLocator(new ToolLocatorOptions([root], Path.Combine(root, "manifest.json"), LegacyDirs: [legacy], SearchPath: false));
        Assert.StartsWith(root, both.Get(ToolId.Pngquant)!.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locator_IgnoresMissingAndInvalidDirectories()
    {
        var locator = new ToolLocator(new ToolLocatorOptions([@"Z:\does\not\exist", "<>|bad"], LegacyDirs: ["", @"\\?\Z:\nope"], SearchPath: false));
        Assert.Empty(locator.Discover());
        Assert.False(locator.Has(ToolId.Ffmpeg));
    }

    private static string RequireRoot()
    {
        TestTools.Require(ToolId.Cjxl);
        return TestTools.Root!;
    }

    private static void WriteManifest(string path, string package, string version, string file, string sha)
    {
        var doc = new
        {
            schema = 1,
            packages = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = package,
                    ["version"] = version,
                    ["source"] = "test",
                    ["archiveSha256"] = null,
                    ["files"] = new Dictionary<string, string> { [file] = sha },
                },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
    }
}
