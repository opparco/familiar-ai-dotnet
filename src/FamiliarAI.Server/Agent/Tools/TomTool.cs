using System.Text.Json.Nodes;
using FamiliarAI.Server.Agent.Backend;

namespace FamiliarAI.Server.Agent.Tools;

/// <summary>
/// Theory of Mind tool — perspective-taking before responding.
/// Mirrors Python ToMTool.
///
/// The tool:
///   1. Recalls memories about the named person from ObservationMemory.
///   2. Returns a structured Japanese prompt scaffold that guides the agent
///      through tone analysis → projection → substitution → response policy.
///
/// The agent is expected to fill in the scaffold in its own reply — the tool
/// output is a reasoning framework, not a final answer.
/// </summary>
public sealed class TomTool
{
    private readonly ObservationMemory _memory;
    private readonly string _defaultPerson;
    private readonly string _template;

    public TomTool(ObservationMemory memory, string template, string defaultPerson = "Alex")
    {
        _memory = memory;
        _template = template;
        _defaultPerson = defaultPerson;
    }

    // ---------------------------------------------------------------
    // Tool definition
    // ---------------------------------------------------------------

    public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
    [
        new ToolDefinition(
            "tom",
            "Theory of Mind: perspective-taking tool. " +
            "Call this BEFORE responding to understand what the other person is feeling and wanting. " +
            "Projects your simulated emotions onto them, then swaps perspectives.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["situation"] = new JsonObject
                    {
                        ["type"]        = "string",
                        ["description"] = "What the other person said or did (their message/action).",
                    },
                    ["person"] = new JsonObject
                    {
                        ["type"]        = "string",
                        ["description"] = $"Who you are talking to (default: {_defaultPerson}).",
                    },
                },
                ["required"] = new JsonArray("situation"),
            }),
    ];

    // ---------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------

    public async Task<(string text, string? image)> CallAsync(
        string toolName, JsonObject input, CancellationToken ct = default)
    {
        if (toolName != "tom")
            return ($"Unknown tool: {toolName}", null);

        var situation = input["situation"]?.GetValue<string>() ?? "";
        var person = input["person"]?.GetValue<string>() ?? _defaultPerson;

        // Recall memories relevant to this person and situation
        var query = $"{person} コミュニケーション 性格 会話パターン {situation}";
        var memories = await _memory.RecallAsync(query, n: 5);

        var memoryContext = "";
        if (memories.Count > 0)
        {
            var lines = memories.Select(m =>
            {
                var em = m.Emotion is not ("neutral" or "") ? $"[{m.Emotion}] " : "";
                return $"- {em}{m.Content[..Math.Min(100, m.Content.Length)]}";
            });
            memoryContext = $"\n## {person}に関する記憶\n" + string.Join('\n', lines);
        }

        var output = _template
            .Replace("{person}", person)
            .Replace("{situation}", situation)
            .Replace("{memory_context}", memoryContext);

        return (output, null);
    }
}
