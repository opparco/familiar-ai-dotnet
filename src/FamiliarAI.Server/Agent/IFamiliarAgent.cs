namespace FamiliarAI.Server.Agent;

public record AgentConfig(
    string AgentName,
    string CompanionName,
    string ApiKey,
    string Platform,
    string Model
);

/// <summary>Callbacks fired during a single agent turn.</summary>
public record TurnCallbacks(
    Action<string, Dictionary<string, object?>>? OnAction = null,
    Action<string>? OnText = null
);

public interface IFamiliarAgent
{
    string AgentName { get; }
    string CompanionName { get; }

    /// <summary>Run one agent turn. Pass empty string for desire-driven turns.</summary>
    Task RunAsync(
        string userInput,
        TurnCallbacks callbacks,
        string? innerVoice = null,
        CancellationToken ct = default
    );

    void ClearHistory();
}
