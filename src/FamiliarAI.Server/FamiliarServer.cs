using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FamiliarAI.Server.Agent;
using FamiliarAI.Server.Models;

namespace FamiliarAI.Server;

/// <summary>
/// Manages connected WebSocket clients, the shared input queue, and
/// broadcasts — mirrors aio_server.py FamiliarServer.
/// </summary>
public sealed class FamiliarServer
{
    private readonly IFamiliarAgent _agent;
    private readonly ChatLogger _chatLogger;
    private readonly ILogger<FamiliarServer> _logger;

    // All connected clients: id → socket
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();

    // Single shared queue: WS handlers enqueue messages; AgentLoopService consumes them.
    // Unbounded channel so enqueue never blocks (matches asyncio.Queue default).
    public Channel<string> InputChannel { get; } =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public string AgentName => _agent.AgentName;
    public string CompanionName => _agent.CompanionName;

    // Updated whenever a user message arrives (for desire cooldown check)
    public DateTimeOffset LastInteractionAt { get; private set; } = DateTimeOffset.UtcNow;

    public FamiliarServer(IFamiliarAgent agent, ChatLogger chatLogger, ILogger<FamiliarServer> logger)
    {
        _agent      = agent;
        _chatLogger = chatLogger;
        _logger     = logger;
    }

    // ---------------------------------------------------------------
    // WebSocket connection handler  (called from Program.cs MapGet)
    // ---------------------------------------------------------------

    public async Task HandleClientAsync(HttpContext ctx, WebSocket ws)
    {
        var id = Guid.NewGuid();
        _clients[id] = ws;
        _logger.LogInformation("Client connected: {Id} from {Remote}", id, ctx.Connection.RemoteIpAddress);

        try
        {
            // Send connected confirmation
            await SendAsync(ws, "connected", new ConnectedData("ok", AgentName));

            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await HandleMessageAsync(ws, json);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            // Abrupt client disconnect — expected, no log noise
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket error for client {Id}", id);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _logger.LogInformation("Client disconnected: {Id}", id);
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------
    // Incoming message dispatch
    // ---------------------------------------------------------------

    private async Task HandleMessageAsync(WebSocket ws, string json)
    {
        IncomingEnvelope? env;
        try
        {
            env = JsonSerializer.Deserialize<IncomingEnvelope>(json);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Invalid JSON: {Json}", json);
            await SendAsync(ws, "error", new ErrorData("Invalid JSON"));
            return;
        }

        if (env is null) return;

        switch (env.Type)
        {
            case "chat":
            {
                var message = env.Data?.TryGetProperty("message", out var m) == true
                    ? m.GetString()?.Trim()
                    : null;

                if (!string.IsNullOrEmpty(message))
                {
                    _chatLogger.LogUserMessage(CompanionName, message);

                    // Broadcast user message to all clients immediately
                    await BroadcastAsync("user_message", new UserMessageData(CompanionName, message));

                    // Enqueue for agent loop (also acts as mid-turn interrupt)
                    await InputChannel.Writer.WriteAsync(message);
                    LastInteractionAt = DateTimeOffset.UtcNow;
                }
                break;
            }
            case "clear_history":
                _agent.ClearHistory();
                await BroadcastAsync("history_cleared", new { });
                _logger.LogInformation("History cleared");
                break;

            default:
                _logger.LogWarning("Unknown message type: {Type}", env.Type);
                break;
        }
    }

    // ---------------------------------------------------------------
    // Agent turn helpers  (called by AgentLoopService)
    // ---------------------------------------------------------------

    public async Task RunUserTurnAsync(string userInput, CancellationToken ct)
    {
        var actionsLog = new List<ActionData>();
        var textBuffer = new List<string>();

        var callbacks = new TurnCallbacks(
            OnAction: (name, input) =>
            {
                var icon  = GetActionIcon(name);
                var label = FormatAction(name, input);
                var data  = new ActionData(name, icon, label, input);
                actionsLog.Add(data);
                _chatLogger.LogAction(icon, label);
                _ = BroadcastAsync("action", data);
            },
            OnText: chunk =>
            {
                textBuffer.Add(chunk);
                _ = BroadcastAsync("text_chunk", new TextChunkData(chunk));
            }
        );

        try
        {
            await _agent.RunAsync(userInput, callbacks, ct: ct);
            var fullText = string.Concat(textBuffer);
            _chatLogger.LogAgentText(AgentName, fullText);
            await BroadcastAsync("response_complete",
                new ResponseCompleteData(fullText, actionsLog));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent error during user turn");
            await BroadcastAsync("error", new ErrorData(ex.Message));
        }
    }

    public async Task RunDesireTurnAsync(string desireName, string prompt, CancellationToken ct)
    {
        var textBuffer = new List<string>();

        var callbacks = new TurnCallbacks(
            OnAction: (name, input) =>
            {
                var icon  = GetActionIcon(name);
                var label = FormatAction(name, input);
                _chatLogger.LogAction(icon, label);
                _ = BroadcastAsync("action", new ActionData(name, icon, label, input));
            },
            OnText: chunk =>
            {
                textBuffer.Add(chunk);
                _ = BroadcastAsync("text_chunk", new TextChunkData(chunk));
            }
        );

        try
        {
            await _agent.RunAsync("", callbacks, innerVoice: prompt, ct: ct);
            var fullText = string.Concat(textBuffer);
            _chatLogger.LogAgentText(AgentName, fullText);
            await BroadcastAsync("response_complete",
                new ResponseCompleteData(fullText, []));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent error during desire turn ({Desire})", desireName);
            await BroadcastAsync("error", new ErrorData(ex.Message));
        }
    }

    // ---------------------------------------------------------------
    // Broadcast / send helpers
    // ---------------------------------------------------------------

    public async Task BroadcastAsync(string type, object? data)
    {
        if (_clients.IsEmpty) return;

        var json = JsonSerializer.Serialize(new WsEnvelope(type, data));
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        var dead = new List<Guid>();
        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open)
            {
                dead.Add(id);
                continue;
            }
            try
            {
                await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Broadcast failed for {Id}: {Msg}", id, ex.Message);
                dead.Add(id);
            }
        }

        foreach (var id in dead)
            _clients.TryRemove(id, out _);
    }

    private static async Task SendAsync(WebSocket ws, string type, object? data)
    {
        var json = JsonSerializer.Serialize(new WsEnvelope(type, data));
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // ---------------------------------------------------------------
    // Action formatting  (mirrors aio_server.py)
    // ---------------------------------------------------------------

    private static string GetActionIcon(string name) => name switch
    {
        "see"        => "👀",
        "look_left"  => "◀️",
        "look_right" => "▶️",
        "look_up"    => "🔼",
        "look_down"  => "🔽",
        "look_around"=> "🔄",
        "walk"       => "🚶",
        "say"        => "💬",
        "recall"     => "🧠",
        "remember"   => "📝",
        _            => "⚙️",
    };

    private static string FormatAction(string name, Dictionary<string, object?> input)
    {
        if (name is "look_left" or "look_right" or "look_up" or "look_down")
            return $"{name}({input.GetValueOrDefault("degrees")}°)";
        if (name == "say")
        {
            var text = input.GetValueOrDefault("text")?.ToString() ?? "";
            return $"「{text[..Math.Min(50, text.Length)]}…」";
        }
        if (name == "walk")
        {
            var dir = input.GetValueOrDefault("direction") ?? "?";
            var dur = input.GetValueOrDefault("duration");
            return dur is not null ? $"{dir} {dur}s" : $"{dir}";
        }
        if (name == "see") return "looking...";
        return name;
    }
}
