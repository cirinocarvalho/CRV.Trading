using System.Text.Json;

namespace CRV.Core.Models;

/// <summary>
/// Reads and writes basket JSON with the same options <see cref="StrategyConfig.ToSetupConfigs"/>
/// parses it with. A basket that fails to parse makes the engine fall back to the legacy A–D
/// setups without any error, so every writer goes through here.
/// </summary>
public static class BasketCodec
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new LenientIntConverter(), new LenientTimeOnlyConverter() },
    };

    /// <summary>The entries in <paramref name="json"/>; empty for null/blank. Throws on malformed JSON
    /// so a caller never overwrites a basket it couldn't read.</summary>
    public static List<BasketEntry> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<List<BasketEntry>>(json, ReadOptions) ?? new();

    /// <summary>JSON for <paramref name="entries"/>; empty string when there are none (what the editor stored before).</summary>
    public static string Serialize(IReadOnlyCollection<BasketEntry> entries) =>
        entries.Count == 0 ? "" : JsonSerializer.Serialize(entries);
}
