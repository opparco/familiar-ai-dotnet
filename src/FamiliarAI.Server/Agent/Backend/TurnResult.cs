using System.Text.Json.Nodes;

namespace FamiliarAI.Server.Agent.Backend;

public record ToolCall(string Id, string Name, JsonObject Input);

public enum StopReason { EndTurn, ToolUse }

public record TurnResult(
    StopReason StopReason,
    string Text,
    IReadOnlyList<ToolCall> ToolCalls
);
