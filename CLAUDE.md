# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

ASP.NET Core 9 WebSocket server — a .NET port of the Python `familiar_agent` stack. Runs a ReAct + TAPE planning loop powered by Kimi (Moonshot AI), Anthropic, or any OpenAI-compatible backend, with SQLite + ruri-v3 ONNX vector memory, ONVIF/RTSP camera vision, VOICEVOX/ElevenLabs TTS, Tuya Cloud mobility, and an autonomous desire system.

## Build & run commands

```bash
dotnet build src/FamiliarAI.Server
dotnet run --project src/FamiliarAI.Server

# Publish single-file (Windows x64)
dotnet publish src/FamiliarAI.Server -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o publish/
```

There are no tests in this repository.

## Git workflow

**Always cut a feature branch. Never commit directly to `main`.** Commit messages in English, Conventional Commits format (`feat:`, `fix:`, `docs:`, `chore:`).

## Configuration

All config is via environment variables. A `.env` file in the working directory is loaded automatically at startup (existing env vars take precedence). Key variables:

| Variable | Default | Notes |
|---|---|---|
| `API_KEY` | *(required)* | API key for the chosen platform. Without it → `StubAgent`. |
| `PLATFORM` | `kimi` | `kimi` — Moonshot AI; `anthropic` — Anthropic Messages API; `openai` — OpenAI-compatible (e.g. Ollama, vLLM, LM Studio). |
| `BASE_URL` | *(see below)* | Base URL for the LLM API. Ignored for `anthropic`. Default: `https://api.moonshot.ai/v1` for kimi; `http://localhost:11434/v1` for Ollama, `http://localhost:8000/v1` for vLLM. |
| `MODEL` | *(per platform)* | Model name. Defaults: `kimi-k2.5` (kimi), `claude-sonnet-4-6` (anthropic), `local-model` (openai). |
| `AGENT_NAME` | `Familiar` | |
| `COMPANION_NAME` | `USER` | |
| `WEB_HOST` / `WEB_PORT` | `0.0.0.0` / `5000` | |
| `CAMERA_HOST` | *(unset)* | Leave unset to disable camera. |
| `TTS_ENGINE` | `voicevox` | `voicevox` or `elevenlabs` |
| `TUYA_API_KEY` | *(unset)* | Leave unset to disable mobility. |

`ME.md` (working dir or `~/.familiar_ai/ME.md`) is injected at the top of every system prompt as persona definition. It is gitignored — never commit it.

## Architecture

```
Program.cs (DI composition root)
  └── ASP.NET Core / Kestrel /ws
        └── FamiliarServer           — WebSocket session manager; broadcast hub; InputChannel queue
              └── AgentLoopService   — BackgroundService; consumes InputChannel; fires desire turns
                    └── EmbodiedAgent — ReAct loop (max 50 iter), TAPE planning, post-turn memory
                          ├── KimiBackend        — Moonshot AI SSE streaming + CompleteAsync (reasoning_content round-trip)
                          ├── AnthropicBackend   — Anthropic Messages API native SSE streaming
                          ├── OpenAICompatibleBackend — any OpenAI-compatible endpoint
                          ├── ObservationMemory  — SQLite + ruri-v3 vectors
                          ├── MemoryTool         — remember / recall
                          ├── TomTool            — Theory of Mind scaffold
                          ├── CameraTool?        — ONVIF PTZ + RTSP frame capture (optional)
                          ├── TtsTool?           — VOICEVOX / ElevenLabs (optional)
                          └── MobilityTool?      — Tuya Cloud API robot vacuum (optional)
              └── DesireSystem       — autonomous motivations; grows while idle; persisted to JSON
```

### EmbodiedAgent turn lifecycle

Each call to `RunAsync()`:
1. **Morning reconstruction** (first turn only) — loads self-model, curiosity, feelings from memory.
2. **Memory + feelings context** — semantic recall + recent feelings appended to the user message.
3. **TAPE planning** — `CompleteAsync` generates a 2–4 step plan injected into the system prompt.
4. **ReAct loop** — `StreamTurnAsync → tool dispatch → append to history → repeat` until `end_turn` or max iterations.
5. **Adaptive replanning** — after each tool result, checks if the plan is blocked; if so, calls `CompleteAsync` for a revised step appended to the tool result text.
6. **Post-turn memory saves** (fire-and-forget) — infer emotion, summarise exchange, update self-model on emotional turns.

### Backend quirks

**KimiBackend:** Kimi returns `reasoning_content` (thinking tokens) in assistant messages. This field **must be round-tripped** back on subsequent turns that include tool calls or the API rejects the request with `"reasoning_content is missing"`. `StreamTurnAsync` captures it and includes it in the returned `rawAssistant` `JsonObject`.

**AnthropicBackend:** Uses the native Anthropic Messages API (`https://api.anthropic.com/v1/messages`, `anthropic-version: 2023-06-01`). `TranslateHistory()` converts the agent's OpenAI-format history (tool results as `role:"tool"`, tool calls as `tool_calls` array) to Anthropic format (tool results as user messages with `tool_result` content blocks, tool calls as `tool_use` content blocks) before each request. `BASE_URL` is ignored — the endpoint is hardcoded.

### Tool interface pattern

All tools follow this pattern:
```csharp
class SomeTool
{
    public IEnumerable<ToolDefinition> GetToolDefinitions() { ... }
    public Task<(string text, string? base64Image)> CallAsync(string name, JsonObject input, CancellationToken ct) { ... }
}
```

`ToolDefinition` uses Anthropic-style schema; `ToOpenAIFormat()` converts it to OpenAI function-calling format before sending to the API.

### Prompt templates

All prompts live in `src/FamiliarAI.Server/prompts/` and are copied to the output directory at build time. `EmbodiedAgent` resolves them relative to the executable (deployed) or working directory (dev). Templates use `{placeholder}` substitution (no Razor).

### Memory

`ObservationMemory` uses a single SQLite DB at `~/.familiar_ai/observations.db` with WAL mode. Recall strategy: vector cosine similarity → LIKE keyword fallback → recency fallback. `RuriEmbedding` wraps `model.onnx` + `tokenizer.model` via `Microsoft.ML.OnnxRuntime`. If model files are missing at startup, the server falls back to `StubAgent`.

Model files must be placed at `~/.familiar_ai/models/ruri-v3/` or `./models/ruri-v3/` relative to the working directory.

Memory kinds: `observation`, `conversation`, `feeling`, `self_model`, `curiosity`.

### Desire system

`DesireSystem` grows desire levels in real time via `Tick()`. `AgentLoopService` polls every 10 s; after 90 s idle it checks `DominantAsPrompt()` and fires a desire turn if a desire exceeds 0.6. State persisted to `~/.familiar_ai/desires.json`.

### Adding a new tool

1. Implement in `src/FamiliarAI.Server/Agent/Tools/<Name>Tool.cs` using `GetToolDefinitions()` + `CallAsync()`.
2. Register in `Program.cs` (construct + pass to `EmbodiedAgent`).
3. Add dispatch in `EmbodiedAgent.DispatchToolAsync()`.
4. Add tool name to `prompts/system.md`.

## Persistent files

| Path | Contents |
|---|---|
| `~/.familiar_ai/observations.db` | SQLite memory |
| `~/.familiar_ai/desires.json` | Desire levels |
| `~/.familiar_ai/chat.log` | Conversation log |
| `~/.familiar_ai/captures/` | RTSP snapshots |
| `~/.familiar_ai/models/ruri-v3/` | ONNX model files |
| `~/.familiar_ai/ME.md` | Persona (optional) |

## WebSocket protocol

Connect to `ws://{host}:{port}/ws`.

**Client → server:** `{ "type": "chat", "data": { "message": "..." } }` or `{ "type": "clear_history", "data": {} }`

**Server → client types:** `connected`, `user_message`, `text_chunk`, `action`, `response_complete`, `status`, `error`, `history_cleared`.
