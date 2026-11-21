using System.Text.RegularExpressions;
using System.Xml.Linq;
using Carbonado.Core.Engine;
using Carbonado.Core.Tools;

namespace Carbonado.App.Tests.Localization;

public partial class LocalizationTests
{
    private static readonly string Root = FindRepo();

    private static Dictionary<string, string> Table(string lang)
    {
        var doc = XDocument.Load(Path.Combine(Root, "src", "Carbonado.App", "Localization", $"Strings.{lang}.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return doc.Root!.Elements().ToDictionary(e => (string)e.Attribute(x + "Key")!, e => e.Value);
    }

    [Fact]
    public void BothLanguages_HaveTheSameKeys()
    {
        var it = Table("it");
        var en = Table("en");
        Assert.Empty(it.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(it.Keys));
        Assert.All(it, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
        Assert.All(en, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
    }

    [Fact]
    public void Placeholders_MatchBetweenLanguages()
    {
        var it = Table("it");
        var en = Table("en");
        foreach (var (key, itValue) in it)
        {
            var a = Placeholder().Matches(itValue).Select(m => m.Value).Distinct().Order().ToList();
            var b = Placeholder().Matches(en[key]).Select(m => m.Value).Distinct().Order().ToList();
            Assert.True(a.SequenceEqual(b), $"{key}: it={string.Join(",", a)} en={string.Join(",", b)}");
        }
    }

    [Fact]
    public void EveryKeyUsedInSources_Exists()
    {
        var keys = Table("en").Keys.ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(Root, "src", "Carbonado.App"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                                 && !f.Contains(Path.Combine("Localization", "Strings."), StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match m in KeyUsage().Matches(File.ReadAllText(file)))
            {
                string key = m.Groups["key"].Value.TrimEnd('.');
                if (!key.EndsWith("Role", StringComparison.Ordinal) && !IsPrefixOnly(key))
                {
                    used.Add(key);
                }
            }
        }

        var missing = used.Where(k => !keys.Contains(k)).Order().ToList();
        Assert.True(missing.Count == 0, "Missing keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void DynamicKeys_Exist()
    {
        var keys = Table("en").Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var stage in Enum.GetValues<JobStage>())
        {
            Assert.Contains($"L.Stage.{stage}", keys);
        }

        foreach (string grade in new[] { "FL", "VVS", "VS", "SI", "I" })
        {
            Assert.Contains($"L.Grade.{grade}", keys);
        }

        foreach (var tool in Enum.GetValues<ToolId>())
        {
            Assert.Contains($"L.Engines.Role.{tool}", keys);
        }

        foreach (var mode in Enum.GetValues<QualityMode>())
        {
            Assert.Contains($"L.Mode.{mode}", keys);
        }

        foreach (var s in Enum.GetValues<FormatStrategy>())
        {
            Assert.Contains($"L.Strategy.{s}", keys);
        }

        foreach (var m in Enum.GetValues<Carbonado.Core.Encoders.MetadataPolicy>())
        {
            Assert.Contains($"L.Metadata.{m}", keys);
        }

        foreach (var e in Enum.GetValues<Carbonado.Core.Encoders.EffortLevel>())
        {
            Assert.Contains($"L.Effort.{e}", keys);
        }
    }

    private static bool IsPrefixOnly(string key) =>
        key is "L.Stage" or "L.Grade" or "L.Engines.Role" or "L.Mode" or "L.Strategy" or "L.Metadata" or "L.Effort" or "L.Nav";

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<key>L\.[A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)*\.?)")]
    private static partial Regex KeyUsage();

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Carbonado.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
