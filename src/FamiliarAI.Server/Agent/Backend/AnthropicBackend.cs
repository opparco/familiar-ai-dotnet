using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FamiliarAI.Server.Agent.Backend;

/// <summary>
/// Anthropic Messages API backend (native streaming).
///
/// History management note:
/// EmbodiedAgent stores tool results in OpenAI format:
///   {"role":"tool","tool_call_id":"...","content":"..."}
/// and assistant messages either in OpenAI format (tool_calls array) or
/// Anthropic format (content array with tool_use blocks).
/// <see cref="TranslateHistory"/> converts both to Anthropic format before sending.
/// </summary>
public sealed class AnthropicBackend : ILlmBackend, IDisposable
{
    private const string BaseUrl = "https://api.anthropic.com/";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<AnthropicBackend> _logger;

    public AnthropicBackend(string apiKey, string model, ILogger<AnthropicBackend> logger)
    {
        _model = model;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);
    }

    // ---------------------------------------------------------------
    // Message factories
    // ---------------------------------------------------------------

    public JsonObject MakeUserMessage(string content) =>
        new() { ["role"] = "user", ["content"] = content };

    // ---------------------------------------------------------------
    // Streaming turn
    // ---------------------------------------------------------------

    /// <summary>
    /// Stream one LLM turn using the Anthropic Messages API.
    /// Returns <see cref="TurnResult"/> and a raw assistant <see cref="JsonObject"/>
    /// in Anthropic format (content blocks) ready to be stored in history.
    /// </summary>
    public async Task<(TurnResult Result, JsonObject RawAssistant)> StreamTurnAsync(
        string system,
        IReadOnlyList<JsonObject> history,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokens,
        Action<string>? onText,
        CancellationToken ct = default)
    {
        var messages = new JsonArray();
        foreach (var msg in TranslateHistory(history))
            messages.Add(msg);

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["messages"] = messages,
            ["stream"] = true,
        };

        if (tools is { Count: > 0 })
        {
            var toolsArr = new JsonArray();
            foreach (var t in tools)
                toolsArr.Add(ToAnthropicFormat(t));
            body["tools"] = toolsArr;
        }

        var bodyJson = body.ToJsonString(_jsonOptions);
        _logger.LogDebug("Anthropic request: {Body}", bodyJson);
        BackendDumper.DumpRequest("anthropic", body);

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);

        // ── Parse SSE ──────────────────────────────────────────────────
        // Anthropic SSE: each event has "data: <json>" where json["type"] identifies the event.
        // Content blocks are indexed; text and tool_use blocks arrive in parallel by index.
        var textByIndex = new SortedDictionary<int, StringBuilder>();
        var toolByIndex = new SortedDictionary<int, ToolUseAccum>();
        string? stopReason = null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        bool done = false;
        while (!done && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (string.IsNullOrWhiteSpace(data)) continue;

            JsonElement root;
            try { root = JsonDocument.Parse(data).RootElement; }
            catch (JsonException) { continue; }

            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            switch (type)
            {
                case "content_block_start":
                    {
                        if (!root.TryGetProperty("index", out var idxEl)) break;
                        var idx = idxEl.GetInt32();
                        if (!root.TryGetProperty("content_block", out var cb)) break;
                        var cbType = cb.TryGetProperty("type", out var cbt) ? cbt.GetString() : null;
                        if (cbType == "text")
                            textByIndex[idx] = new StringBuilder();
                        else if (cbType == "tool_use")
                            toolByIndex[idx] = new ToolUseAccum
                            {
                                Id = cb.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                                Name = cb.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                            };
                        break;
                    }
                case "content_block_delta":
                    {
                        if (!root.TryGetProperty("index", out var idxEl)) break;
                        var idx = idxEl.GetInt32();
                        if (!root.TryGetProperty("delta", out var delta)) break;
                        var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;

                        if (deltaType == "text_delta" && textByIndex.TryGetValue(idx, out var sb))
                        {
                            var chunk = delta.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
                            sb.Append(chunk);
                            onText?.Invoke(chunk);
                        }
                        else if (deltaType == "input_json_delta" && toolByIndex.TryGetValue(idx, out var tua))
                        {
                            tua.PartialJson += delta.TryGetProperty("partial_json", out var pj) ? pj.GetString() ?? "" : "";
                        }
                        break;
                    }
                case "message_delta":
                    if (root.TryGetProperty("delta", out var msgDelta) &&
                        msgDelta.TryGetProperty("stop_reason", out var sr) &&
                        sr.ValueKind != JsonValueKind.Null)
                        stopReason = sr.GetString();
                    break;

                case "message_stop":
                    done = true;
                    break;
            }
        }

        // ── Build TurnResult ───────────────────────────────────────────
        // Build text strings once; reuse for both fullText and rawAssistant content blocks.
        var textStrings = textByIndex.Values.Select(sb => sb.ToString()).ToList();
        var fullText = string.Concat(textStrings);

        // Parse tool inputs once; reuse for both toolCalls list and rawAssistant content blocks.
        var toolCalls = new List<ToolCall>();
        var toolUseBlocks = new List<(ToolUseAccum Tua, JsonObject Input)>();
        foreach (var tua in toolByIndex.Values)
        {
            JsonObject input;
            try { input = (JsonObject)JsonNode.Parse(tua.PartialJson)!; }
            catch { input = new JsonObject(); }
            toolCalls.Add(new ToolCall(tua.Id, tua.Name, input));
            toolUseBlocks.Add((tua, (JsonObject)input.DeepClone()));
        }

        var stop = stopReason == "tool_use" ? StopReason.ToolUse : StopReason.EndTurn;

        // ── Build rawAssistant in Anthropic format ─────────────────────
        var contentArr = new JsonArray();
        foreach (var text in textStrings)
            if (text.Length > 0)
                contentArr.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var (tua, input) in toolUseBlocks)
            contentArr.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = tua.Id,
                ["name"] = tua.Name,
                ["input"] = input,
            });

        JsonNode content = contentArr.Count > 0
            ? contentArr
            : (JsonNode)JsonValue.Create(fullText)!;

        var rawAssistant = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content,
        };

        BackendDumper.DumpResponse("anthropic", rawAssistant);
        return (new TurnResult(stop, fullText, toolCalls), rawAssistant);
    }

    // ---------------------------------------------------------------
    // Simple completion (no streaming, no tools)
    // ---------------------------------------------------------------

    public async Task<string> CompleteAsync(string prompt, int maxTokens, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = maxTokens,
            ["messages"] = new JsonArray { MakeUserMessage(prompt) },
        };

        BackendDumper.DumpRequest("anthropic", body);

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
        request.Content = new StringContent(body.ToJsonString(_jsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        BackendDumper.DumpResponse("anthropic", JsonNode.Parse(json)!);
        var doc = JsonDocument.Parse(json);
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                return block.GetProperty("text").GetString() ?? "";
        }
        return "";
    }

    public void Dispose() => _http.Dispose();

    // ── Private helpers ────────────────────────────────────────────────

    /// <summary>
    /// Translate EmbodiedAgent history (OpenAI or Anthropic format) to Anthropic format.
    ///
    /// Handles three cases:
    /// 1. role="tool" (OpenAI tool result) → user message with tool_result content blocks.
    /// 2. role="assistant" with "tool_calls" (OpenAI) → content blocks with tool_use.
    /// 3. Everything else → cloned as-is (already Anthropic format or plain user message).
    /// </summary>
    private static IEnumerable<JsonObject> TranslateHistory(IReadOnlyList<JsonObject> history)
    {
        int i = 0;
        while (i < history.Count)
        {
            var msg = history[i];
            var role = msg["role"]?.GetValue<string>() ?? "";

            if (role == "tool")
            {
                // Collect consecutive tool messages → single Anthropic user message
                var blocks = new JsonArray();
                while (i < history.Count && history[i]["role"]?.GetValue<string>() == "tool")
                {
                    var t = history[i];
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = t["tool_call_id"]?.GetValue<string>(),
                        ["content"] = t["content"]?.GetValue<string>(),
                    });
                    i++;
                }
                yield return new JsonObject { ["role"] = "user", ["content"] = blocks };
            }
            else if (role == "assistant" && msg["tool_calls"] is JsonArray openAiTcs)
            {
                // OpenAI assistant with tool_calls → Anthropic content blocks
                var blocks = new JsonArray();
                var textContent = msg["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(textContent))
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = textContent });
                foreach (var tc in openAiTcs.OfType<JsonObject>())
                {
                    var fn = tc["function"]?.AsObject();
                    var argsStr = fn?["arguments"]?.GetValue<string>() ?? "{}";
                    JsonObject input;
                    try { input = (JsonObject)JsonNode.Parse(argsStr)!; }
                    catch { input = new JsonObject(); }
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = tc["id"]?.GetValue<string>(),
                        ["name"] = fn?["name"]?.GetValue<string>(),
                        ["input"] = input,
                    });
                }
                yield return new JsonObject { ["role"] = "assistant", ["content"] = blocks };
                i++;
            }
            else
            {
                yield return (JsonObject)msg.DeepClone();
                i++;
            }
        }
    }

    private static JsonObject ToAnthropicFormat(ToolDefinition t) => new()
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["input_schema"] = t.InputSchema.DeepClone(),
    };

    private sealed class ToolUseAccum
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string PartialJson { get; set; } = "";
    }
}
