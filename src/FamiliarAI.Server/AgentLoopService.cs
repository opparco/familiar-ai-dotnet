using FamiliarAI.Server.Agent;
using FamiliarAI.Server.Models;

namespace FamiliarAI.Server;

/// <summary>
/// Background service that consumes the input queue and fires desire turns
/// when idle — mirrors aio_server.py _run_agent_loop().
///
/// Loop:
///   1. Wait up to IdleCheckInterval for a user message.
///   2. If message arrives → RunUserTurnAsync.
///   3. If timeout → check idle duration vs DesireCooldown.
///      If cooldown elapsed → ask DesireSystem for dominant prompt.
///      If prompt exists → Satisfy the desire, then RunDesireTurnAsync.
/// </summary>
public sealed class AgentLoopService : BackgroundService
{
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DesireCooldown    = TimeSpan.FromSeconds(90);

    private readonly FamiliarServer _server;
    private readonly DesireSystem   _desires;
    private readonly ChatLogger     _chatLogger;
    private readonly ILogger<AgentLoopService> _logger;

    public AgentLoopService(
        FamiliarServer server,
        DesireSystem desires,
        ChatLogger chatLogger,
        ILogger<AgentLoopService> logger)
    {
        _server     = server;
        _desires    = desires;
        _chatLogger = chatLogger;
        _logger     = logger;
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
                await _server.RunUserTurnAsync(userInput, stoppingToken);
                continue;
            }

            // ── Idle path: desire check ────────────────────────────────
            var idleFor = DateTimeOffset.UtcNow - _server.LastInteractionAt;
            if (idleFor < DesireCooldown)
                continue;

            var prompt = _desires.DominantAsPrompt();
            if (prompt is null)
            {
                _logger.LogDebug("Idle {Secs:F0}s — no desire above threshold", idleFor.TotalSeconds);
                continue;
            }

            var dominant = _desires.GetDominant();
            var name     = dominant?.name ?? "unknown";

            // Echo murmur to stdout/log and broadcast to clients
            var murmur = GetMurmur(name);
            _chatLogger.LogStatus(murmur);
            await _server.BroadcastAsync("status", new StatusData(murmur));

            // Check once more — a user message may have arrived while we were deciding
            if (_server.InputChannel.Reader.TryRead(out var intercepted))
            {
                _logger.LogDebug("Desire pre-empted by user message");
                await _server.RunUserTurnAsync(intercepted, stoppingToken);
                continue;
            }

            // Satisfy before running so a long turn doesn't re-trigger the same desire
            _desires.Satisfy(name);

            _logger.LogInformation(
                "Desire fired: {Name} (idle {Secs:F0}s)",
                name, idleFor.TotalSeconds);

            await _server.RunDesireTurnAsync(name, prompt, stoppingToken);
        }

        _logger.LogInformation("Agent loop stopped");
    }

    // ---------------------------------------------------------------
    // Murmur strings (shown to clients before the desire turn runs)
    // ---------------------------------------------------------------

    private static string GetMurmur(string desireName) => desireName switch
    {
        "look_around"      => "なんか外が気になってきた…",
        "explore"          => "ちょっと動きたくなってきたな…",
        "greet_companion"  => "誰かいるかな…",
        "rest"             => "少し休憩しよかな…",
        _                  => "ちょっと気になることがあって…",
    };
}
