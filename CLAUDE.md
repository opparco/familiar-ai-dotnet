# CLAUDE.md

## Project overview

ASP.NET Core 9 WebSocket server — a .NET port of `familiar_agent`. ReAct + TAPE planning loop with Kimi / Anthropic / OpenAI-compatible backends, SQLite + ruri-v3 ONNX memory, ONVIF/RTSP camera, VOICEVOX/ElevenLabs TTS, Tuya mobility, and an autonomous desire system.

## Build & run

```bash
dotnet build src/FamiliarAI.Server
dotnet run --project src/FamiliarAI.Server
```

No tests exist in this repository.

## Git workflow

**Always cut a feature branch. Never commit directly to `main`.** Conventional Commits (`feat:`, `fix:`, `docs:`, `chore:`).

## Configuration

All config via env vars; `.env` in the working directory is auto-loaded. Key vars: `API_KEY` (required — missing → StubAgent), `PLATFORM` (`kimi`/`anthropic`/`openai`), `MODEL`, `BASE_URL`, `CAMERA_HOST`, `TTS_ENGINE`, `TUYA_API_KEY`. `ME.md` (working dir or `~/.familiar_ai/ME.md`) is injected as persona — gitignored, never commit.

## Architecture

```
Program.cs (DI root)
  └── FamiliarServer        — WebSocket session manager; InputChannel queue
        └── AgentLoopService — BackgroundService; fires desire turns after 90s idle
              └── EmbodiedAgent — ReAct loop (max 50 iter) + TAPE planning
                    ├── KimiBackend / AnthropicBackend / OpenAICompatibleBackend
                    ├── ObservationMemory  — SQLite + ruri-v3 vectors
                    ├── MemoryTool / TomTool
                    ├── CameraTool?  — ONVIF PTZ + RTSP (optional)
                    ├── TtsTool?     — VOICEVOX / ElevenLabs (optional)
                    └── MobilityTool? — Tuya Cloud (optional)
        └── DesireSystem     — autonomous motivations; persisted to JSON
```

### EmbodiedAgent turn lifecycle (`RunAsync`)

1. **Morning reconstruction** (first turn only) — loads self-model, curiosity, feelings from memory.
2. **Memory context** — semantic recall + recent feelings appended to the user message.
3. **TAPE planning** — `CompleteAsync` generates a 2–4 step plan injected into the system prompt.
4. **ReAct loop** — `StreamTurnAsync → tool dispatch → append history → repeat` until `end_turn` or max iter.
5. **Adaptive replanning** — if a tool result blocks the plan, `CompleteAsync` revises the current step.
6. **Post-turn memory** (fire-and-forget) — infer emotion, summarise exchange, update self-model.

### Backend quirks

**KimiBackend:** `reasoning_content` (thinking tokens) **must be round-tripped** in every subsequent assistant message that includes tool calls, or the API rejects with `"reasoning_content is missing"`.

**AnthropicBackend:** `TranslateHistory()` converts OpenAI-format history to Anthropic format (tool results → user messages with `tool_result` blocks; tool calls → `tool_use` blocks). `BASE_URL` is ignored — endpoint is hardcoded.

### Tool interface

```csharp
class SomeTool
{
    public IEnumerable<ToolDefinition> GetToolDefinitions() { ... }
    public Task<(string text, string? base64Image)> CallAsync(string name, JsonObject input, CancellationToken ct) { ... }
}
```

`ToolDefinition.ToOpenAIFormat()` converts Anthropic-style schema to OpenAI function-calling format.

### Prompts

Templates in `src/FamiliarAI.Server/prompts/` (copied to output dir at build). `{placeholder}` substitution, no Razor.

### Memory

SQLite at `~/.familiar_ai/observations.db` (WAL). Recall: vector cosine → LIKE keyword → recency. ruri-v3 model files at `~/.familiar_ai/models/ruri-v3/` (or `./models/ruri-v3/`); missing → StubAgent. Kinds: `observation`, `conversation`, `feeling`, `self_model`, `curiosity`.

### Adding a new tool

1. Implement `src/FamiliarAI.Server/Agent/Tools/<Name>Tool.cs` with `GetToolDefinitions()` + `CallAsync()`.
2. Register in `Program.cs`.
3. Add dispatch in `EmbodiedAgent.DispatchToolAsync()`.
4. Add tool name to `prompts/system.md`.

## Persistent files

`~/.familiar_ai/`: `observations.db`, `desires.json`, `chat.log`, `captures/`, `models/ruri-v3/`, `ME.md`.
