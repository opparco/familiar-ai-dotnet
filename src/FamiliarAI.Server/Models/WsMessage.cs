using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamiliarAI.Server.Models;

/// <summary>Outgoing envelope: { "type": "...", "data": { ... } }</summary>
public record WsEnvelope(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("data")] object? Data
);

/// <summary>Incoming envelope from clients.</summary>
public record IncomingEnvelope(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("data")] JsonElement? Data
);

// ---- outgoing data payloads ----

public record ConnectedData(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("agent_name")] string AgentName
);

public record UserMessageData(
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("message")] string Message
);

public record TextChunkData(
    [property: JsonPropertyName("chunk")] string Chunk
);

public record ActionData(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] string Icon,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("input")] object Input
);

public record ResponseCompleteData(
    [property: JsonPropertyName("full_text")] string FullText,
    [property: JsonPropertyName("actions")] IReadOnlyList<ActionData> Actions
);

public record StatusData(
    [property: JsonPropertyName("message")] string Message
);

public record ErrorData(
    [property: JsonPropertyName("message")] string Message
);
