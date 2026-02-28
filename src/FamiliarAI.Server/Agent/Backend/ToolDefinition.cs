using System.Text.Json.Nodes;

namespace FamiliarAI.Server.Agent.Backend;

/// <summary>
/// Tool definition in Anthropic-style schema (name / description / input_schema).
/// KimiBackend converts this to OpenAI function-calling format before sending.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonObject InputSchema)
{
    /// <summary>Convert to OpenAI/Kimi function-calling format.</summary>
    public JsonObject ToKimiFormat() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"]        = Name,
            ["description"] = Description,
            ["parameters"]  = InputSchema.DeepClone(),
        },
    };
}
