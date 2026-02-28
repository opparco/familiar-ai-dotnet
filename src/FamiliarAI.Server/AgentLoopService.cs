using FamiliarAI.Server.Models;

namespace FamiliarAI.Server;

/// <summary>
/// Background service that consumes the input queue and fires desire turns
/// when idle — mirrors aio_server.py _run_agent_loop().
/// </summary>
public sealed class AgentLoopService : BackgroundService
{
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DesireCooldown = TimeSpan.FromSeconds(90);

    private readonly FamiliarServer _server;
    private readonly ILogger<AgentLoopService> _logger;

    public AgentLoopService(FamiliarServer server, ILogger<AgentLoopService> logger)
    {
        _server = server;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent loop started");

        while (!stoppingToken.IsCancellationRequested)
        {
            string? userInput = null;

            // Wait up to IdleCheckInterval for a user message
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(IdleCheckInterval);

            try
            {
                userInput = await _server.InputChannel.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Timeout — no message arrived; go idle path
            }
            catch (OperationCanceledException)
            {
                break; // Server shutting down
            }

            if (userInput is not null)
            {
                _logger.LogDebug("Processing user input: {Input}", userInput);
                await _server.RunUserTurnAsync(userInput, stoppingToken);
            }
            else
            {
                // Idle path: check if desire cooldown has elapsed
                var idleFor = DateTimeOffset.UtcNow - _server.LastInteractionAt;
                if (idleFor < DesireCooldown)
                    continue;

                // TODO: replace with real DesireSystem once ported
                // For now just log that we'd fire a desire turn
                _logger.LogDebug("Idle {Seconds:F0}s — desire check (stub: no desires configured)", idleFor.TotalSeconds);
            }
        }

        _logger.LogInformation("Agent loop stopped");
    }
}
