using System.Text.Json.Nodes;
using FamiliarAI.Server.Agent.Backend;

namespace FamiliarAI.Server.Agent;

/// <summary>
/// Agent backed by the Kimi (Moonshot AI) LLM.
/// Maintains conversation history across turns, including round-tripping
/// reasoning_content so the API stays happy on tool-call turns.
/// </summary>
public sealed class KimiAgent : IFamiliarAgent, IDisposable
{
    private readonly KimiBackend _backend;
    private readonly string _systemPrompt;
    private readonly List<JsonObject> _history = [];
    private readonly ILogger<KimiAgent> _logger;
    private readonly object _lock = new();

    public string AgentName    { get; }
    public string CompanionName { get; }

    public KimiAgent(AgentConfig config, ILogger<KimiAgent> logger, ILogger<KimiBackend> backendLogger)
    {
        AgentName     = config.AgentName;
        CompanionName = config.CompanionName;
        _logger       = logger;

        var model = !string.IsNullOrEmpty(config.Model) ? config.Model : "kimi-k2.5";
        _backend = new KimiBackend(config.ApiKey, model, backendLogger);

        _systemPrompt = BuildSystemPrompt(config);
        _logger.LogInformation("KimiAgent ready (model={Model})", model);
    }

    // ---------------------------------------------------------------
    // IFamiliarAgent
    // ---------------------------------------------------------------

    public async Task RunAsync(
        string userInput,
        TurnCallbacks callbacks,
        string? innerVoice = null,
        CancellationToken ct = default)
    {
        // Build effective user content
        string effective = userInput;
        if (!string.IsNullOrEmpty(innerVoice))
            effective = string.IsNullOrEmpty(userInput)
                ? $"[{innerVoice}]"
                : $"[{innerVoice}]\n{userInput}";

        List<JsonObject> snapshot;
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(effective))
                _history.Add(KimiBackend.MakeUserMessage(effective));
            snapshot = [.. _history];
        }

        _logger.LogDebug("RunAsync: history={Count} messages", snapshot.Count);

        var (result, rawAssistant) = await _backend.StreamTurnAsync(
            _systemPrompt, snapshot, maxTokens: 4096, callbacks.OnText, ct);

        lock (_lock)
        {
            _history.Add(rawAssistant);
        }

        _logger.LogDebug(
            "Turn complete: stop={Stop} text_len={Len}",
            result.StopReason, result.Text.Length);
    }

    public void ClearHistory()
    {
        lock (_lock) { _history.Clear(); }
        _logger.LogInformation("History cleared");
    }

    public void Dispose() => _backend.Dispose();

    // ---------------------------------------------------------------
    // System prompt
    // ---------------------------------------------------------------

    private static string BuildSystemPrompt(AgentConfig config)
    {
        var name     = config.AgentName;
        var companion = config.CompanionName;
        var date      = DateTime.Now.ToString("yyyy-MM-dd");

        // Load ME.md if present (persona file, gitignored)
        var meFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".familiar_ai", "ME.md");
        var persona = File.Exists(meFile) ? File.ReadAllText(meFile) : "";

        if (!string.IsNullOrWhiteSpace(persona))
            return $"{persona.Trim()}\n\nToday is {date}.";

        return $"""
            You are {name}, a warm and curious AI companion.
            You speak naturally and with genuine interest.
            Your companion's name is {companion}.
            Today is {date}.
            """;
    }
}
