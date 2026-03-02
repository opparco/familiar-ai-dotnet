using System.Text.Json.Nodes;

namespace FamiliarAI.Server.Agent.Backend;

/// <summary>
/// Common interface for all LLM backends (Kimi, OpenAI-compatible, etc.).
/// </summary>
public interface ILlmBackend
{
    /// <summary>Stream one LLM turn. Returns the TurnResult and the raw assistant JsonObject to store in history.</summary>
    Task<(TurnResult Result, JsonObject RawAssistant)> StreamTurnAsync(
        string system,
        IReadOnlyList<JsonObject> history,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokens,
        Action<string>? onText,
        CancellationToken ct = default);

    /// <summary>Simple non-streaming completion (no tools). Used for TAPE planning, emotion inference, etc.</summary>
    Task<string> CompleteAsync(string prompt, int maxTokens, CancellationToken ct = default);

    /// <summary>Create a user-role message in the format expected by this backend.</summary>
    JsonObject MakeUserMessage(string content);
}
