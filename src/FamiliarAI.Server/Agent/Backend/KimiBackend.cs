using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FamiliarAI.Server.Agent.Backend;

/// <summary>
/// Moonshot AI Kimi backend.
///
/// Kimi uses an OpenAI-compatible streaming API but includes a
/// <c>reasoning_content</c> field in assistant messages (thinking tokens).
/// That field MUST be round-tripped back in subsequent turns containing
/// tool calls, otherwise the API returns:
///   "thinking is enabled but reasoning_content is missing in assistant tool call message"
///
/// This backend captures <c>reasoning_content</c> from the SSE stream and
/// preserves it in the returned <paramref name="rawAssistant"/> JsonObject.
/// </summary>
public sealed class KimiBackend : IDisposable
{
    private const string BaseUrl = "https://api.moonshot.ai/v1/";

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<KimiBackend> _logger;

    public KimiBackend(string apiKey, string model, ILogger<KimiBackend> logger)
    {
        _model = model;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
    }

    // ---------------------------------------------------------------
    // Message factories
    // ---------------------------------------------------------------

    public static JsonObject MakeUserMessage(string content) =>
        new() { ["role"] = "user", ["content"] = content };

    public static JsonObject MakeSystemMessage(string content) =>
        new() { ["role"] = "system", ["content"] = content };

    // ---------------------------------------------------------------
    // Streaming turn
    // ---------------------------------------------------------------

    /// <summary>
    /// Stream one LLM turn.
    /// Returns the parsed TurnResult and the raw assistant JsonObject
    /// (including reasoning_content) that should be stored in history.
    /// </summary>
    public async Task<(TurnResult Result, JsonObject RawAssistant)> StreamTurnAsync(
        string system,
        IReadOnlyList<JsonObject> history,
        int maxTokens,
        Action<string>? onText,
        CancellationToken ct = default)
    {
        // Flatten: system message first, then history
        var messages = new JsonArray();
        messages.Add(MakeSystemMessage(system));
        foreach (var msg in history)
            messages.Add(msg.DeepClone()); // must clone to add to a new array

        var body = new JsonObject
        {
            ["model"]      = _model,
            ["max_tokens"] = maxTokens,
            ["messages"]   = messages,
            ["stream"]     = true,
        };

        _logger.LogDebug("Kimi request: {Messages}", body.ToJsonString());

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);

        // ── Parse SSE ────────────────────────────────────────────────
        // Simple line-based parser: each SSE event starts with "data: "
        var textChunks      = new List<string>();
        var reasoningChunks = new List<string>();
        // tool call delta accumulator: index → {id, name, arguments}
        var rawTcs          = new Dictionary<int, RawTcAccum>();
        string? finishReason = null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") break;
            if (string.IsNullOrWhiteSpace(data)) continue;

            JsonElement root;
            try { root = JsonDocument.Parse(data).RootElement; }
            catch (JsonException) { continue; }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null)
                finishReason = fr.GetString();

            if (!choice.TryGetProperty("delta", out var delta)) continue;

            // reasoning_content (Kimi-specific thinking tokens)
            if (delta.TryGetProperty("reasoning_content", out var rc) &&
                rc.ValueKind == JsonValueKind.String)
            {
                reasoningChunks.Add(rc.GetString()!);
            }

            // content
            if (delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var chunk = content.GetString()!;
                textChunks.Add(chunk);
                onText?.Invoke(chunk);
            }

            // tool_calls
            if (delta.TryGetProperty("tool_calls", out var tcs))
            {
                foreach (var tcDelta in tcs.EnumerateArray())
                {
                    var idx = tcDelta.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;
                    if (!rawTcs.TryGetValue(idx, out var accum))
                        rawTcs[idx] = accum = new RawTcAccum();

                    if (tcDelta.TryGetProperty("id", out var id))
                        accum.Id = id.GetString() ?? accum.Id;

                    if (tcDelta.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var name))
                            accum.Name = name.GetString() ?? accum.Name;
                        if (fn.TryGetProperty("arguments", out var args))
                            accum.Arguments += args.GetString();
                    }
                }
            }
        }

        // ── Build result ─────────────────────────────────────────────
        var text = string.Concat(textChunks);

        var toolCalls = new List<ToolCall>();
        foreach (var (_, accum) in rawTcs.OrderBy(x => x.Key))
        {
            JsonObject input;
            try { input = (JsonObject)JsonNode.Parse(accum.Arguments)!; }
            catch { input = new JsonObject(); }
            toolCalls.Add(new ToolCall(accum.Id, accum.Name, input));
        }

        var stop = finishReason == "tool_calls" ? StopReason.ToolUse : StopReason.EndTurn;

        // Build raw assistant — include reasoning_content so Kimi accepts it next turn
        var rawAssistant = new JsonObject
        {
            ["role"]    = "assistant",
            ["content"] = text.Length > 0 ? JsonValue.Create(text) : null,
        };
        if (reasoningChunks.Count > 0)
            rawAssistant["reasoning_content"] = string.Concat(reasoningChunks);
        if (toolCalls.Count > 0)
        {
            var tcsArr = new JsonArray();
            foreach (var tc in toolCalls)
            {
                tcsArr.Add(new JsonObject
                {
                    ["id"]   = tc.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"]      = tc.Name,
                        ["arguments"] = tc.Input.ToJsonString(),
                    },
                });
            }
            rawAssistant["tool_calls"] = tcsArr;
        }

        return (new TurnResult(stop, text, toolCalls), rawAssistant);
    }

    // ---------------------------------------------------------------
    // Simple completion (no streaming, no tools)
    // ---------------------------------------------------------------

    public async Task<string> CompleteAsync(string prompt, int maxTokens, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"]      = _model,
            ["max_tokens"] = maxTokens,
            ["messages"]   = new JsonArray { MakeUserMessage(prompt) },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc  = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    public void Dispose() => _http.Dispose();

    // ── Private helpers ───────────────────────────────────────────────

    private sealed class RawTcAccum
    {
        public string Id        { get; set; } = "";
        public string Name      { get; set; } = "";
        public string Arguments { get; set; } = "";
    }
}
