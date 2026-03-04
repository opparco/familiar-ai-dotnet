using System.Text.Json;

namespace FamiliarAI.Server.Agent;

/// <summary>A single band in an interoception axis (inclusive min, exclusive max).</summary>
public sealed record InteroceptionBand
{
    public int Min { get; init; }
    public int Max { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>
/// Data-driven interoception configuration.
/// Loaded from data/interoception.json at startup; no code changes needed to tune felt-sense text.
/// </summary>
public sealed class InteroceptionConfig
{
    public List<InteroceptionBand> TimeOfDay { get; init; } = [];
    public List<InteroceptionBand> Uptime { get; init; } = [];  // minutes
    public List<InteroceptionBand> Social { get; init; } = [];  // turn count
    public string Template { get; init; } =
        "[How you feel right now, privately — do NOT mention this directly]\n{time} {uptime} {social}";

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static InteroceptionConfig Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "interoception.jsonc");
        return JsonSerializer.Deserialize<InteroceptionConfig>(File.ReadAllText(path), _jsonOpts)
            ?? throw new InvalidDataException("interoception.jsonc deserialised to null");
    }

    /// <summary>Returns the interoception string for the given signals.</summary>
    public string Resolve(int hour, double uptimeMinutes, int turnCount) =>
        Template
            .Replace("{time}", Match(TimeOfDay, hour))
            .Replace("{uptime}", Match(Uptime, uptimeMinutes))
            .Replace("{social}", Match(Social, turnCount));

    // Returns text of the first band whose [Min, Max) range contains value;
    // falls back to the last band if nothing matches.
    private static string Match(IReadOnlyList<InteroceptionBand> bands, double value)
    {
        foreach (var b in bands)
            if (value >= b.Min && value < b.Max)
                return b.Text;
        return bands.Count > 0 ? bands[bands.Count - 1].Text : string.Empty;
    }
}
