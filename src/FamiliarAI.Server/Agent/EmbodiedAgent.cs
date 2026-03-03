using System.Text.Json.Nodes;
using FamiliarAI.Server.Agent.Backend;
using FamiliarAI.Server.Agent.Tools;

namespace FamiliarAI.Server.Agent;

/// <summary>
/// Full ReAct agent — .NET port of Python EmbodiedAgent.
///
/// Loop per turn:
///   stream_turn → if tool_use: execute → append to history → continue
///                 if end_turn: save memories → return text
///
/// Memory kinds used: observation, conversation, self_model, curiosity
/// Prompts loaded from external files in prompts/ directory.
/// </summary>
public sealed class EmbodiedAgent : IFamiliarAgent, IDisposable
{
    private const int MaxIterations = 50;

    private readonly ILlmBackend _backend;
    private readonly ObservationMemory _memory;
    private readonly MemoryTool _memoryTool;
    private readonly TomTool _tomTool;
    private readonly CameraTool?   _camera;
    private readonly TtsTool?     _tts;
    private readonly MobilityTool? _mobility;
    private readonly ILogger<EmbodiedAgent> _logger;

    // Prompt templates (loaded once at startup)
    private readonly string _systemTemplate;
    private readonly string _emotionTemplate;
    private readonly string _summaryTemplate;
    private readonly string _selfModelTemplate;
    private readonly string _tapePlanTemplate;
    private readonly string _tapeBlockedTemplate;
    private readonly string _tapeReplanTemplate;

    // Data-driven config (loaded once at startup)
    private readonly InteroceptionConfig _interoceptionConfig;
    private readonly AgentTextConfig _agentText;

    private readonly List<JsonObject> _history = [];
    private readonly object _historyLock = new();

    private int _turnCount;
    private readonly DateTime _startedAt = DateTime.Now;

    public string AgentName    { get; }
    public string CompanionName { get; }

    public EmbodiedAgent(
        AgentConfig config,
        AgentTextConfig agentText,
        ILlmBackend backend,
        ObservationMemory memory,
        ILogger<EmbodiedAgent> logger,
        CameraTool?   camera   = null,
        TtsTool?      tts      = null,
        MobilityTool? mobility = null)
    {
        AgentName    = config.AgentName;
        CompanionName = config.CompanionName;
        _agentText   = agentText;
        _backend     = backend;
        _memory      = memory;
        _logger      = logger;
        _camera      = camera;
        _tts         = tts;
        _mobility    = mobility;
        _memoryTool  = new MemoryTool(memory, agentText);
        _tomTool     = new TomTool(memory, config.CompanionName);

        var promptDir = ResolvePromptDir();
        _systemTemplate     = File.ReadAllText(Path.Combine(promptDir, "system.md"));
        _emotionTemplate    = File.ReadAllText(Path.Combine(promptDir, "emotion.md"));
        _summaryTemplate    = File.ReadAllText(Path.Combine(promptDir, "summary.md"));
        _selfModelTemplate  = File.ReadAllText(Path.Combine(promptDir, "self_model.md"));
        _tapePlanTemplate   = File.ReadAllText(Path.Combine(promptDir, "tape_plan.md"));
        _tapeBlockedTemplate= File.ReadAllText(Path.Combine(promptDir, "tape_blocked.md"));
        _tapeReplanTemplate = File.ReadAllText(Path.Combine(promptDir, "tape_replan.md"));

        var dataDir = ResolveDataDir();
        _interoceptionConfig = InteroceptionConfig.Load(dataDir);

        _logger.LogInformation("EmbodiedAgent ready — prompts from {PromptDir}, data from {DataDir}",
            promptDir, dataDir);
    }

    // ---------------------------------------------------------------
    // IFamiliarAgent
    // ---------------------------------------------------------------

    public async Task RunAsync(
        string userInput,
        TurnCallbacks callbacks,
        string? innerVoice = null,
        CancellationToken ct = default)
    {
        _turnCount++;
        var isDesireTurn = !string.IsNullOrEmpty(innerVoice) && string.IsNullOrEmpty(userInput);

        // ── Morning reconstruction (first turn only) ──────────────────
        string morningCtx = "";
        if (_turnCount == 1)
            morningCtx = await BuildMorningContextAsync();

        // ── Memory + feelings context ─────────────────────────────────
        string feelingsCtx = "";
        string effectiveInput = userInput;

        if (!isDesireTurn)
        {
            var (memories, feelings) = await FetchContextAsync(userInput);
            var memParts = new List<string>();
            if (memories.Count > 0) memParts.Add(ObservationMemory.FormatForContext(memories, _agentText.MemoryLabels.Memories));
            if (feelings.Count > 0) memParts.Add(ObservationMemory.FormatFeelingsForContext(feelings, _agentText.MemoryLabels.Feelings));
            if (memParts.Count > 0)
                effectiveInput = userInput + "\n\n" + string.Join("\n\n", memParts);
            feelingsCtx = feelings.Count > 0
                ? ObservationMemory.FormatFeelingsForContext(feelings, _agentText.MemoryLabels.Feelings) : "";
        }
        else
        {
            effectiveInput = _agentText.InnerVoice.DesireTurnInput;
        }

        // ── Append user message ────────────────────────────────────────
        List<JsonObject> snapshot;
        lock (_historyLock)
        {
            _history.Add(_backend.MakeUserMessage(effectiveInput));
            snapshot = [.. _history];
        }

        // ── TAPE: upfront plan ────────────────────────────────────────
        string planCtx = "";
        if (!isDesireTurn && !string.IsNullOrWhiteSpace(userInput))
            planCtx = await GeneratePlanAsync(userInput, ct);

        // ── Build tool list ───────────────────────────────────────────
        var tools = _memoryTool.GetToolDefinitions().ToList();
        tools.AddRange(_tomTool.GetToolDefinitions());
        if (_camera   is not null) tools.AddRange(_camera.GetToolDefinitions());
        if (_tts      is not null) tools.AddRange(_tts.GetToolDefinitions());
        if (_mobility is not null) tools.AddRange(_mobility.GetToolDefinitions());

        // ── ReAct loop ────────────────────────────────────────────────
        bool cameraUsed   = false;
        bool sayUsed      = false;
        int  nonSayStreak = 0;
        string finalText  = _agentText.NoResponse;

        for (int i = 0; i < MaxIterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogDebug("Agent iteration {I}", i + 1);

            var system = BuildSystemPrompt(feelingsCtx, morningCtx, innerVoice ?? "", planCtx);

            var (result, rawAssistant) = await _backend.StreamTurnAsync(
                system, snapshot, tools, maxTokens: 4096, callbacks.OnText, ct);

            if (result.StopReason == StopReason.EndTurn)
            {
                lock (_historyLock) { _history.Add(rawAssistant); snapshot = [.. _history]; }
                finalText = string.IsNullOrEmpty(result.Text) ? _agentText.NoResponse : result.Text;

                // Post-turn memory saves (fire and forget — don't block response)
                _ = SavePostTurnMemoriesAsync(userInput, finalText, cameraUsed);
                return;
            }

            if (result.StopReason == StopReason.ToolUse && result.ToolCalls.Count > 0)
            {
                // Execute each tool call
                var toolResults = new List<(string text, string? image)>();
                foreach (var tc in result.ToolCalls)
                {
                    if (tc.Name == "see")  cameraUsed = true;
                    if (tc.Name == "say")  { sayUsed = true; nonSayStreak = 0; }
                    else                   nonSayStreak++;

                    callbacks.OnAction?.Invoke(tc.Name, tc.Input
                        .ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.ToString()));

                    string toolText;
                    string? toolImage;
                    try
                    {
                        (toolText, toolImage) = await DispatchToolAsync(tc.Name, tc.Input, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Tool {Name} failed: {Ex}", tc.Name, ex.Message);
                        (toolText, toolImage) = ($"Tool error: {ex.Message}", null);
                    }

                    // TAPE adaptive replanning
                    if (!string.IsNullOrEmpty(planCtx))
                    {
                        var inputSummary = string.Join(", ", tc.Input.Take(3).Select(kv => $"{kv.Key}={kv.Value}"));
                        var blocked = await CheckPlanBlockedAsync(planCtx, tc.Name, inputSummary, toolText, ct);
                        if (blocked)
                        {
                            var replan = await GenerateReplanAsync(planCtx, tc.Name, inputSummary, toolText, ct);
                            if (!string.IsNullOrEmpty(replan))
                            {
                                toolText = $"{toolText}\n\n{_agentText.AdaptiveReplanLabel}{replan}";
                                _logger.LogInformation("TAPE replan: {Replan}", replan[..Math.Min(80, replan.Length)]);
                            }
                        }
                    }

                    _logger.LogDebug("Tool {Name} result: {Text}", tc.Name, toolText[..Math.Min(100, toolText.Length)]);
                    toolResults.Add((toolText, toolImage));
                }

                // Append assistant + tool results atomically
                lock (_historyLock)
                {
                    _history.Add(rawAssistant);
                    foreach (var (tc, (text, _)) in result.ToolCalls.Zip(toolResults))
                    {
                        _history.Add(new JsonObject
                        {
                            ["role"]         = "tool",
                            ["tool_call_id"] = tc.Id,
                            ["content"]      = text,
                        });
                    }
                    snapshot = [.. _history];
                }

                // Nudge: 2+ tool calls without say()
                if (nonSayStreak >= 2 && !sayUsed)
                {
                    var nudge = _backend.MakeUserMessage(_agentText.Nudges.SilentReminder);
                    lock (_historyLock) { _history.Add(nudge); snapshot = [.. _history]; }
                    nonSayStreak = 0;
                }
                else if (sayUsed && nonSayStreak >= 2)
                {
                    var nudge = _backend.MakeUserMessage(_agentText.Nudges.StopExploring);
                    lock (_historyLock) { _history.Add(nudge); snapshot = [.. _history]; }
                    nonSayStreak = 0;
                }

                continue;
            }

            _logger.LogWarning("Unexpected stop_reason, breaking loop");
            break;
        }

        // Max iterations — force final response
        _logger.LogWarning("Reached max iterations ({Max}). Forcing final response.", MaxIterations);
        var forceMsg = _backend.MakeUserMessage(_agentText.Nudges.ForceEnd);
        lock (_historyLock) { _history.Add(forceMsg); snapshot = [.. _history]; }

        var (forceResult, _) = await _backend.StreamTurnAsync(
            BuildSystemPrompt("", morningCtx, "", planCtx),
            snapshot, [], maxTokens: 4096, callbacks.OnText, ct);
        finalText = forceResult.Text;
    }

    public void ClearHistory()
    {
        lock (_historyLock) { _history.Clear(); }
        _logger.LogInformation("History cleared");
    }

    // ---------------------------------------------------------------
    // Tool dispatch
    // ---------------------------------------------------------------

    private Task<(string, string?)> DispatchToolAsync(string name, JsonObject input, CancellationToken ct)
    {
        if (name is "remember" or "recall")
            return _memoryTool.CallAsync(name, input);

        if (name == "tom")
            return _tomTool.CallAsync(name, input, ct);

        if (name is "see" or "look" && _camera is not null)
            return _camera.CallAsync(name, input, ct);

        if (name == "say" && _tts is not null)
            return _tts.CallAsync(name, input, ct);

        if (name == "walk" && _mobility is not null)
            return _mobility.CallAsync(name, input, ct);

        return Task.FromResult<(string, string?)>(
            ($"Tool '{name}' not available in this configuration.", null));
    }

    // ---------------------------------------------------------------
    // System prompt assembly
    // ---------------------------------------------------------------

    private string BuildSystemPrompt(
        string feelingsCtx,
        string morningCtx,
        string innerVoice,
        string planCtx)
    {
        var me      = LoadMeMd();
        var system = _systemTemplate.Replace("{max_steps}", MaxIterations.ToString());
        var intero  = BuildInteroception();

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(me)) parts.Add(me);
        parts.Add(system);
        parts.Add(intero);
        if (!string.IsNullOrEmpty(morningCtx))    parts.Add(morningCtx);
        else if (!string.IsNullOrEmpty(feelingsCtx)) parts.Add(feelingsCtx);
        if (!string.IsNullOrEmpty(innerVoice))
            parts.Add(_agentText.InnerVoice.Header + "\n" + innerVoice);
        if (!string.IsNullOrEmpty(planCtx))
            parts.Add(_agentText.PlanHeader + "\n" + planCtx);

        return string.Join("\n\n---\n\n", parts);
    }

    // ---------------------------------------------------------------
    // Interoception (felt sense from objective signals)
    // ---------------------------------------------------------------

    private string BuildInteroception()
    {
        var now    = DateTime.Now;
        var uptime = now - _startedAt;
        return _interoceptionConfig.Resolve(now.Hour, uptime.TotalMinutes, _turnCount);
    }

    // ---------------------------------------------------------------
    // Morning reconstruction (first turn only)
    // ---------------------------------------------------------------

    private async Task<string> BuildMorningContextAsync()
    {
        var (selfModel, curiosities, feelings) = await (
            _memory.RecallByKindAsync("self_model", 5),
            _memory.RecallByKindAsync("curiosity",  3),
            _memory.RecentFeelingsAsync(3)
        ).WhenAll();

        var parts = new List<string>();
        if (selfModel.Count > 0)   parts.Add(ObservationMemory.FormatSelfModelForContext(selfModel, _agentText.MemoryLabels.SelfModel));
        if (curiosities.Count > 0) parts.Add(ObservationMemory.FormatCuriositiesForContext(curiosities, _agentText.MemoryLabels.Curiosities));
        if (feelings.Count > 0)    parts.Add(ObservationMemory.FormatFeelingsForContext(feelings, _agentText.MemoryLabels.Feelings));

        if (parts.Count == 0)
            return _agentText.Morning.NoMemories;

        return _agentText.Morning.Header + "\n\n" + string.Join("\n\n", parts);
    }

    // ---------------------------------------------------------------
    // Post-turn memory saves
    // ---------------------------------------------------------------

    private async Task SavePostTurnMemoriesAsync(string userInput, string agentText, bool cameraUsed)
    {
        try
        {
            if (string.IsNullOrEmpty(agentText) || agentText == _agentText.NoResponse) return;

            if (cameraUsed)
                await _memory.SaveAsync(agentText[..Math.Min(500, agentText.Length)],
                    direction: _agentText.MemoryDirections.Observation, kind: "observation");

            var emotion = await InferEmotionAsync(agentText);
            var summary = await SummarizeExchangeAsync(userInput, agentText);
            await _memory.SaveAsync(summary, direction: _agentText.MemoryDirections.Conversation, kind: "conversation", emotion: emotion);

            if (emotion != "neutral")
                await UpdateSelfModelAsync(agentText, emotion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Post-turn memory save failed: {Ex}", ex.Message);
        }
    }

    // ---------------------------------------------------------------
    // LLM utility calls
    // ---------------------------------------------------------------

    private async Task<string> InferEmotionAsync(string text)
    {
        var prompt = _emotionTemplate
            .Replace("{emotions}", string.Join(" / ", _agentText.Emotions))
            .Replace("{text}", text[..Math.Min(400, text.Length)]);
        var label  = (await _backend.CompleteAsync(prompt, maxTokens: 10)).Trim().ToLower();
        return _agentText.Emotions.Contains(label) ? label : "neutral";
    }

    private async Task<string> SummarizeExchangeAsync(string userInput, string agentText)
    {
        var prompt = _summaryTemplate
            .Replace("{lang}", _agentText.SummaryLang)
            .Replace("{user}", userInput[..Math.Min(200, userInput.Length)])
            .Replace("{agent}", agentText[..Math.Min(200, agentText.Length)]);
        return (await _backend.CompleteAsync(prompt, maxTokens: 80)).Trim()
               is { Length: > 0 } s ? s : agentText[..Math.Min(100, agentText.Length)];
    }

    private async Task UpdateSelfModelAsync(string text, string emotion)
    {
        try
        {
            var prompt  = _selfModelTemplate.Replace("{text}", text[..Math.Min(400, text.Length)]);
            var insight = (await _backend.CompleteAsync(prompt, maxTokens: 80)).Trim();
            if (!string.IsNullOrEmpty(insight) && insight.ToLower() != "nothing")
            {
                await _memory.SaveAsync(insight, direction: _agentText.MemoryDirections.SelfModel, kind: "self_model", emotion: emotion);
                _logger.LogInformation("Self-model updated: {Insight}", insight[..Math.Min(60, insight.Length)]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Self-model update failed: {Ex}", ex.Message);
        }
    }

    // ---------------------------------------------------------------
    // TAPE planning
    // ---------------------------------------------------------------

    private async Task<string> GeneratePlanAsync(string userInput, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return "";
        var allDefs   = _memoryTool.GetToolDefinitions()
            .Concat(_tomTool.GetToolDefinitions())
            .Concat(_camera?.GetToolDefinitions()   ?? [])
            .Concat(_tts?.GetToolDefinitions()      ?? [])
            .Concat(_mobility?.GetToolDefinitions() ?? []);
        var toolNames = string.Join(", ", allDefs.Select(t => t.Name));
        var prompt = _tapePlanTemplate
            .Replace("{tools}",   toolNames)
            .Replace("{request}", userInput[..Math.Min(300, userInput.Length)]);
        try
        {
            return (await _backend.CompleteAsync(prompt, maxTokens: 150, ct)).Trim();
        }
        catch { return ""; }
    }

    private async Task<bool> CheckPlanBlockedAsync(
        string plan, string tool, string argsSummary, string result, CancellationToken ct)
    {
        var prompt = _tapeBlockedTemplate
            .Replace("{plan}",         plan[..Math.Min(400, plan.Length)])
            .Replace("{tool}",         tool)
            .Replace("{args_summary}", argsSummary)
            .Replace("{result_summary}", result[..Math.Min(300, result.Length)]);
        try
        {
            var answer = (await _backend.CompleteAsync(prompt, maxTokens: 5, ct)).Trim().ToLower();
            return answer == "blocked";
        }
        catch { return false; }
    }

    private async Task<string> GenerateReplanAsync(
        string plan, string tool, string argsSummary, string result, CancellationToken ct)
    {
        var prompt = _tapeReplanTemplate
            .Replace("{plan}",         plan[..Math.Min(400, plan.Length)])
            .Replace("{tool}",         tool)
            .Replace("{args_summary}", argsSummary)
            .Replace("{result_summary}", result[..Math.Min(300, result.Length)]);
        try
        {
            return (await _backend.CompleteAsync(prompt, maxTokens: 80, ct)).Trim();
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------
    // Context helpers
    // ---------------------------------------------------------------

    private async Task<(List<MemoryRecord> memories, List<MemoryRecord> feelings)> FetchContextAsync(
        string query)
    {
        var memoriesTask = string.IsNullOrEmpty(query)
            ? Task.FromResult(new List<MemoryRecord>())
            : _memory.RecallAsync(query, n: 3);
        var feelingsTask = _memory.RecentFeelingsAsync(n: 4);
        await Task.WhenAll(memoriesTask, feelingsTask);
        return (memoriesTask.Result, feelingsTask.Result);
    }

    private static string LoadMeMd()
    {
        foreach (var path in new[]
        {
            "ME.md",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".familiar_ai", "ME.md"),
        })
            if (File.Exists(path))
                try { return File.ReadAllText(path, System.Text.Encoding.UTF8).Trim(); }
                catch { }
        return "";
    }

    private static string ResolveDir(string name)
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        var candidate = Path.Combine(exeDir, name);
        if (Directory.Exists(candidate)) return candidate;

        candidate = Path.Combine(Directory.GetCurrentDirectory(), name);
        if (Directory.Exists(candidate)) return candidate;

        throw new DirectoryNotFoundException(
            $"{name}/ directory not found. Expected alongside the executable.");
    }

    private static string ResolvePromptDir() => ResolveDir("prompts");
    private static string ResolveDataDir()   => ResolveDir("data");

    public void Dispose()
    {
        _memory.Dispose();
        _camera?.Dispose();
        _tts?.Dispose();
        _mobility?.Dispose();
    }
}

// ── Extension helpers ─────────────────────────────────────────────────────────

file static class TaskExtensions
{
    public static async Task<(T1, T2, T3)> WhenAll<T1, T2, T3>(
        this (Task<T1> t1, Task<T2> t2, Task<T3> t3) tasks)
    {
        await Task.WhenAll(tasks.t1, tasks.t2, tasks.t3);
        return (tasks.t1.Result, tasks.t2.Result, tasks.t3.Result);
    }
}
