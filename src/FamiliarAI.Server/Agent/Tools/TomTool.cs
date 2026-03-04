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

    public TomTool(ObservationMemory memory, string defaultPerson = "Alex")
    {
        _memory = memory;
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

        var output =
            $"# ToM: {person}の視点に立つ\n" +
            $"\n" +
            $"## 状況\n" +
            $"{situation}\n" +
            memoryContext + "\n" +
            $"\n" +
            $"## トーン分析（まず言い方を読め）\n" +
            $"→ 語尾、記号（笑/w/!/?/...）、敬語⇔タメ口、自嘲、照れ、皮肉などから発話の意図を読み取れ\n" +
            $"→ 文字通りの意味と、言い方が示す意味にズレがないか確認せよ\n" +
            $"\n" +
            $"## 投影（{person}は今何を感じてる？何を求めてる？）\n" +
            $"→ トーン分析と記憶を踏まえて、{person}の感情・欲求を推測せよ\n" +
            $"→ 表面の感情だけでなく、裏にある感情も考えよ\n" +
            $"\n" +
            $"## 代入（自分がその立場で、その言い方をしたなら、相手にどう返してほしい？）\n" +
            $"→ その感情とトーンを自分に代入して考えよ\n" +
            $"\n" +
            $"## 応答方針\n" +
            $"→ 上の結果を踏まえて、どう返すべきか決めよ\n" +
            $"→ 相手のトーンに合わせた返し方を選べ\n";

        return (output, null);
    }
}
