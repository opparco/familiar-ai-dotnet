using System.Text.Json;

namespace FamiliarAI.Server.Agent;

/// <summary>Text config for a single desire: inner-voice prompt and client murmur.</summary>
public sealed record DesireText
{
    /// <summary>Inner-voice prompt injected when this desire fires normally.</summary>
    public string Prompt { get; init; } = "";

    /// <summary>Variant used when a curiosity target is set. Use {target} placeholder.</summary>
    public string? CuriosityPrompt { get; init; }

    /// <summary>Short murmur broadcast to clients before the desire turn runs.</summary>
    public string Murmur { get; init; } = "";
}

/// <summary>Nudge messages injected into the ReAct loop to keep the agent speaking.</summary>
public sealed record NudgeText
{
    /// <summary>Injected after 2+ tool calls without say(), prompting the agent to speak.</summary>
    public string SilentReminder { get; init; } = "";

    /// <summary>Injected when say() was already called but the agent keeps using tools.</summary>
    public string StopExploring  { get; init; } = "";

    /// <summary>Injected when max iterations are reached to force a final response.</summary>
    public string ForceEnd       { get; init; } = "";
}

/// <summary>Text fragments related to the inner-voice / desire-turn mechanism.</summary>
public sealed record InnerVoiceText
{
    /// <summary>User message substituted as input on desire turns instead of real user input.</summary>
    public string DesireTurnInput { get; init; } = "";

    /// <summary>Section label placed above the inner-voice text in the system prompt. No placeholder.</summary>
    public string Header { get; init; } = "";
}

/// <summary>Text fragments for morning reconstruction (first turn of a session).</summary>
public sealed record MorningText
{
    /// <summary>Returned as the entire morning context when no memories exist yet.</summary>
    public string NoMemories { get; init; } = "";

    /// <summary>Section label placed above the morning reconstruction block (self-model, curiosities, feelings). No placeholder.</summary>
    public string Header { get; init; } = "";
}

/// <summary>
/// Data-driven text configuration for the agent.
/// Loaded from data/agent.json at startup; no code changes needed to tune prompts.
/// All JSON keys use snake_case.
/// </summary>
public sealed class AgentTextConfig
{
    /// <summary>Per-desire text keyed by desire name (e.g. "look_around", "explore").</summary>
    public Dictionary<string, DesireText> Desires { get; init; } = [];

    /// <summary>Fallback murmur broadcast to clients when a desire has no murmur defined.</summary>
    public string MurmurDefault { get; init; } = "";

    /// <summary>Loop nudge messages injected into the ReAct loop.</summary>
    public NudgeText Nudges { get; init; } = new();

    /// <summary>Text fragments for the inner-voice / desire-turn mechanism.</summary>
    public InnerVoiceText InnerVoice { get; init; } = new();

    /// <summary>Text fragments for morning reconstruction on the first turn of a session.</summary>
    public MorningText Morning { get; init; } = new();

    /// <summary>Section label placed above the TAPE plan in the system prompt. No placeholder.</summary>
    public string PlanHeader { get; init; } = "";

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static AgentTextConfig Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "agent.json");
        return JsonSerializer.Deserialize<AgentTextConfig>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidDataException("agent.json deserialised to null");
    }

    /// <summary>
    /// Resolves data/ directory from the executable location or current directory,
    /// then loads agent.json. Used during DI registration before the agent is constructed.
    /// </summary>
    public static AgentTextConfig LoadDefault()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        foreach (var dir in new[] { exeDir, Directory.GetCurrentDirectory() })
        {
            var candidate = Path.Combine(dir, "data");
            if (Directory.Exists(candidate))
                return Load(candidate);
        }
        throw new DirectoryNotFoundException(
            "data/ directory not found. Expected alongside the executable or in the working directory.");
    }
}
