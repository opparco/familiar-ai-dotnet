using System.Text.Json.Nodes;
using FamiliarAI.Server.Agent.Backend;

namespace FamiliarAI.Server.Agent.Tools;

/// <summary>
/// Agent-callable memory tools: remember + recall.
/// Mirrors Python MemoryTool — same tool names, same parameter schema.
/// </summary>
public sealed class MemoryTool
{
    private readonly ObservationMemory _store;

    public MemoryTool(ObservationMemory store) => _store = store;

    public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
    [
        new ToolDefinition(
            "remember",
            "Save something to long-term memory. Use this to remember important things: " +
            "what you saw, what happened, how you felt, conversations.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["type"]        = "string",
                        ["description"] = "What to remember (1-3 sentences).",
                    },
                    ["emotion"] = new JsonObject
                    {
                        ["type"]        = "string",
                        ["enum"]        = new JsonArray("neutral", "happy", "sad", "curious", "excited", "moved"),
                        ["description"] = "Emotional tone of this memory.",
                    },
                },
                ["required"] = new JsonArray("content"),
            }),

        new ToolDefinition(
            "recall",
            "Search long-term memory for things related to a topic. " +
            "Use this to remember past observations, conversations, or feelings.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"]        = "string",
                        ["description"] = "What to search for.",
                    },
                    ["n"] = new JsonObject
                    {
                        ["type"]        = "integer",
                        ["description"] = "Number of memories to return (default 3).",
                    },
                },
                ["required"] = new JsonArray("query"),
            }),
    ];

    public async Task<(string Text, string? Image)> CallAsync(string toolName, JsonObject input)
    {
        if (toolName == "remember")
        {
            var content = input["content"]?.GetValue<string>() ?? "";
            var emotion = input["emotion"]?.GetValue<string>() ?? "neutral";
            await _store.SaveAsync(content, kind: "observation", emotion: emotion);
            return ($"Remembered: {content[..Math.Min(60, content.Length)]}", null);
        }

        if (toolName == "recall")
        {
            var query = input["query"]?.GetValue<string>() ?? "";
            var n     = input["n"]?.GetValue<int>() ?? 3;
            var mems  = await _store.RecallAsync(query, n);
            if (mems.Count == 0) return ("No relevant memories found.", null);

            var lines = mems.Select(m =>
            {
                var score   = m.Score.HasValue ? $" ({m.Score:F2})" : "";
                var emotion = m.Emotion is not ("neutral" or "") ? $" [{m.Emotion}]" : "";
                return $"- {m.Date} {m.Time}{score}{emotion}: {m.Content[..Math.Min(120, m.Content.Length)]}";
            });
            return (string.Join('\n', lines), null);
        }

        return ($"Unknown memory tool: {toolName}", null);
    }
}
