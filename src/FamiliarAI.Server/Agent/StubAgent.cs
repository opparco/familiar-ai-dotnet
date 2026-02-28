namespace FamiliarAI.Server.Agent;

/// <summary>
/// Placeholder agent that echoes the user input.
/// Replace with a real LLM-backed agent implementation.
/// </summary>
public sealed class StubAgent : IFamiliarAgent
{
    public string AgentName { get; }
    public string CompanionName { get; }

    public StubAgent(AgentConfig config)
    {
        AgentName = config.AgentName;
        CompanionName = config.CompanionName;
    }

    public async Task RunAsync(
        string userInput,
        TurnCallbacks callbacks,
        string? innerVoice = null,
        CancellationToken ct = default
    )
    {
        await Task.Delay(300, ct);

        var reply = string.IsNullOrEmpty(userInput)
            ? $"[{AgentName} is looking around...]"
            : $"(stub) You said: {userInput}";

        foreach (var ch in reply)
        {
            ct.ThrowIfCancellationRequested();
            callbacks.OnText?.Invoke(ch.ToString());
            await Task.Delay(20, ct);
        }
    }

    public void ClearHistory() { }
}
