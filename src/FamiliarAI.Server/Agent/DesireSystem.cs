using System.Text.Json;

namespace FamiliarAI.Server.Agent;

/// <summary>
/// Autonomous desire system — mirrors Python DesireSystem.
///
/// Desires grow at fixed rates per second of inactivity.
/// When a desire exceeds TriggerThreshold (0.6), it fires as an inner-voice
/// prompt injected into the agent loop as a desire turn.
/// State is persisted to ~/.familiar_ai/desires.json across restarts.
///
/// Desires and growth rates:
///   look_around  0.012/s  — fires after ~40s idle
///   explore      0.005/s  — fires after ~2min idle
///   greet_companion 0.0   — manual boost only
///   rest         0.0      — manual boost only
/// </summary>
public sealed class DesireSystem
{
    private static readonly string DefaultStatePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".familiar_ai", "desires.json");

    private static readonly IReadOnlyDictionary<string, float> DefaultDesires =
        new Dictionary<string, float>
        {
            ["look_around"] = 0.1f,
            ["explore"] = 0.1f,
            ["greet_companion"] = 0.0f,
            ["rest"] = 0.0f,
        };

    // Growth per second of inactivity
    private static readonly IReadOnlyDictionary<string, float> GrowthRates =
        new Dictionary<string, float>
        {
            ["look_around"] = 0.012f,   // reaches 0.6 after ~40s
            ["explore"] = 0.005f,   // reaches 0.6 after ~2min
            ["greet_companion"] = 0.0f,
            ["rest"] = 0.0f,
        };

    private const float TriggerThreshold = 0.6f;

    private readonly string _statePath;
    private readonly ILogger<DesireSystem> _logger;
    private Dictionary<string, float> _desires;
    private long _lastTickMs;  // Environment.TickCount64

    /// <summary>
    /// What the agent wants to investigate next (per-session).
    /// Set by EmbodiedAgent when a curiosity memory is saved.
    /// Used to specialise the look_around / explore prompt.
    /// </summary>
    public string? CuriosityTarget { get; set; }

    private readonly AgentTextConfig _textConfig;

    public DesireSystem(AgentTextConfig textConfig, ILogger<DesireSystem> logger, string? statePath = null)
    {
        _textConfig = textConfig;
        _statePath = statePath ?? DefaultStatePath;
        _logger = logger;
        _desires = new Dictionary<string, float>(DefaultDesires);
        _lastTickMs = Environment.TickCount64;
        Load();
    }

    // ---------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var json = File.ReadAllText(_statePath);
            _desires = JsonSerializer.Deserialize<Dictionary<string, float>>(json)
                       ?? new(DefaultDesires);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load desires: {Ex}", ex.Message);
            _desires = new(DefaultDesires);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath,
                JsonSerializer.Serialize(_desires,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not save desires: {Ex}", ex.Message);
        }
    }

    // ---------------------------------------------------------------
    // Core operations
    // ---------------------------------------------------------------

    /// <summary>Advance desire levels by elapsed wall-clock time.</summary>
    public void Tick()
    {
        var now = Environment.TickCount64;
        var dt = (now - _lastTickMs) / 1000.0f;   // ms → seconds
        _lastTickMs = now;

        foreach (var (name, rate) in GrowthRates)
        {
            if (rate <= 0f) continue;
            var current = _desires.GetValueOrDefault(name, 0f);
            _desires[name] = Math.Min(1.0f, current + rate * dt);
        }
        Save();
    }

    /// <summary>Reset a desire to its default level after it fires.</summary>
    public void Satisfy(string desireName)
    {
        if (!_desires.ContainsKey(desireName)) return;
        _desires[desireName] = DefaultDesires.GetValueOrDefault(desireName, 0f);
        Save();
        _logger.LogDebug("Desire satisfied: {Name}", desireName);
    }

    /// <summary>Boost a desire (e.g., dopamine response to novelty).</summary>
    public void Boost(string desireName, float amount = 0.2f)
    {
        var current = _desires.GetValueOrDefault(desireName, 0f);
        _desires[desireName] = Math.Min(1.0f, current + amount);
        Save();
    }

    /// <summary>
    /// Tick, then return the strongest desire at or above TriggerThreshold.
    /// Returns null if no desire is ready to fire.
    /// </summary>
    public (string name, float level)? GetDominant()
    {
        Tick();
        (string name, float level)? best = null;
        foreach (var (name, level) in _desires)
        {
            if (level >= TriggerThreshold && (best is null || level > best.Value.level))
                best = (name, level);
        }
        return best;
    }

    /// <summary>
    /// Return the natural-language inner-voice prompt for the dominant desire, or null.
    /// Calls GetDominant() internally (which calls Tick()).
    /// </summary>
    public string? DominantAsPrompt()
    {
        var result = GetDominant();
        if (result is null) return null;

        var (name, _) = result.Value;

        if (!_textConfig.Desires.TryGetValue(name, out var dt)) return null;

        // Use curiosity-target variant when available
        if (CuriosityTarget is not null && dt.CuriosityPrompt is not null)
            return dt.CuriosityPrompt.Replace("{target}", CuriosityTarget);

        return dt.Prompt;
    }

    /// <summary>Returns the client-facing murmur for the given desire name.</summary>
    public string GetMurmur(string desireName) =>
        _textConfig.Desires.TryGetValue(desireName, out var dt)
            ? dt.Murmur
            : _textConfig.MurmurDefault;

    /// <summary>Current desire levels snapshot (for diagnostics).</summary>
    public IReadOnlyDictionary<string, float> Levels =>
        new Dictionary<string, float>(_desires);
}
