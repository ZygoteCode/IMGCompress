using System.Text.Json.Serialization;

namespace Carbonado.Core.Persistence;

/// <summary>Source-generated JSON metadata for settings (indented, human-editable). No reflection at startup.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Source-generated JSON metadata for history lines and stats (compact).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSerializable(typeof(StatsSnapshot))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>Persisted aggregate of the history (keeps lifetime stats O(1) at startup).</summary>
internal sealed class StatsSnapshot
{
    public long Files { get; set; }

    public long OriginalBytes { get; set; }

    public long OutputBytes { get; set; }

    public Dictionary<string, int> ByFormat { get; set; } = [];
}
