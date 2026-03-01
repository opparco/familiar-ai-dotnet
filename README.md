# familiar-ai — .NET Server

ASP.NET Core 9 WebSocket server for the familiar-ai embodied agent.
A port of the Python `src/familiar_agent/` stack.

## What it does

- Runs a **ReAct loop** (THINK → ACT → OBSERVE) powered by Kimi (Moonshot AI) LLM
- **Sees** the world through an ONVIF/RTSP Wi-Fi camera (Tapo C220 or compatible)
- **Speaks** via VOICEVOX (local, Japanese-optimised) or ElevenLabs (cloud)
- **Remembers** using SQLite + ruri-v3 ONNX vector embeddings
- **Feels** autonomous desires that fire self-initiated turns when idle
- Exposes a single `/ws` WebSocket endpoint consumed by any frontend

---

## Requirements

| Dependency | Purpose | Notes |
|---|---|---|
| [.NET 9 SDK](https://dotnet.microsoft.com/download) | Runtime | |
| [ruri-v3 ONNX model](https://huggingface.co/keisuke-miyako/ruri-v3-30m-onnx-fp32) | Vector memory | `model.onnx` + `tokenizer.model` |
| [VOICEVOX Engine](https://voicevox.hiroshiba.jp/) | TTS (default) | Local HTTP API on port 50021 |
| [ffmpeg](https://ffmpeg.org/) | RTSP frame capture + audio playback | Must be in `PATH` |
| mpv *(optional)* | Audio playback | Falls back to `ffplay` if absent |
| ONVIF camera *(optional)* | Vision | Any PTZ-capable model; tested on Tapo C220 |

---

## Quick start

```bash
# 1. Place ruri-v3 model files
mkdir -p ~/.familiar_ai/models/ruri-v3
# copy model.onnx and tokenizer.model there

# 2. Set environment variables (see Configuration below)
export API_KEY=sk-...          # Kimi (Moonshot AI) API key
export AGENT_NAME=Familiar
export COMPANION_NAME=Alex

# 3. Run
cd dotnet
dotnet run --project src/FamiliarAI.Server
```

The server prints a banner and starts listening:

```
  familiar-ai  [Familiar]
  WebSocket : ws://0.0.0.0:5000/ws
  Platform  : kimi
  Camera    : not configured
  TTS       : voicevox (speaker 3, http://localhost:50021)
  Chat log  : C:\Users\yourname\.familiar_ai\chat.log
  Press Ctrl+C to stop
```

---

## Web UI

The server ships a browser-based chat interface at `http://{WEB_HOST}:{WEB_PORT}/` (default: `http://localhost:5000/`).

Open the URL in any modern browser after starting the server — no build step required.

### Features

- **WebSocket chat** — sends messages and streams agent replies character by character with a typewriter effect.
- **Avatar panel** — displays a character sprite with:
  - **Lip-sync animation** driven by real-time phoneme analysis of the streamed text (detects Japanese vowels, long vowels, sokuon, hatsuon, etc.).
  - **Blink animation** at random 2–6 s intervals.
- **Tool action notifications** — tool calls (camera, memory, TTS, etc.) appear as labelled action rows in the chat.
- **Auto-reconnect** — reconnects to the WebSocket every 5 s if the connection drops.
- **Clear button / `/clear` command** — sends `clear_history` to reset the conversation context.

### Character images

The avatar switches between four PNG images based on speaking state and blink timing.
Place them in `wwwroot/character-images/`:

| Filename | State |
|---|---|
| `open_close.png` | Eyes open, mouth closed *(idle default)* |
| `open_open.png` | Eyes open, mouth open |
| `close_close.png` | Eyes closed, mouth closed *(blinking)* |
| `close_open.png` | Eyes closed, mouth open |

### Server-injected config

The server exposes a `/config.js` endpoint that injects `appConfig` (agent name, companion name, typewriter delay) as a global variable into the page — no bundler or build pipeline required.

---

## Configuration

All configuration is via environment variables.
A `.env` file in the working directory (or alongside the executable) is loaded automatically at startup — variables already set in the environment take precedence.

```ini
# .env  (gitignored)
API_KEY=sk-...
AGENT_NAME=Familiar
COMPANION_NAME=Alex
CAMERA_HOST=192.168.1.100
CAMERA_PASSWORD=secret
TTS_ENGINE=voicevox
```

### Core

| Variable | Default | Description |
|---|---|---|
| `API_KEY` | *(required)* | Kimi (Moonshot AI) API key. Without this the server runs as `StubAgent` (echoes text, no LLM). |
| `AGENT_NAME` | `Familiar` | Agent's display name |
| `COMPANION_NAME` | `USER` | Human's display name |
| `MODEL` | `kimi-k2.5` | Kimi model ID |
| `WEB_HOST` | `0.0.0.0` | Bind address |
| `WEB_PORT` | `5000` | Bind port |

### Camera (optional)

| Variable | Default | Description |
|---|---|---|
| `CAMERA_HOST` | *(unset)* | IP address of ONVIF camera. Leave unset to disable. |
| `CAMERA_USERNAME` | `admin` | ONVIF username |
| `CAMERA_PASSWORD` | *(unset)* | ONVIF password |
| `CAMERA_PORT` | `2020` | ONVIF port (Tapo uses 2020) |

Camera uses RTSP `rtsp://{user}:{pass}@{host}:554/stream1` for frame capture.

### TTS

| Variable | Default | Description |
|---|---|---|
| `TTS_ENGINE` | `voicevox` | `voicevox` or `elevenlabs` |
| `VOICEVOX_URL` | `http://localhost:50021` | VOICEVOX Engine HTTP endpoint |
| `VOICEVOX_SPEAKER` | `3` | VOICEVOX speaker ID |
| `ELEVENLABS_API_KEY` | *(unset)* | ElevenLabs API key |
| `ELEVENLABS_VOICE_ID` | *(unset)* | ElevenLabs voice ID |

### Mobility (optional)

| Variable | Default | Description |
|---|---|---|
| `TUYA_API_KEY` | *(unset)* | Tuya Cloud API key. Leave unset to disable. |
| `TUYA_API_SECRET` | *(unset)* | Tuya Cloud API secret |
| `TUYA_DEVICE_ID` | *(unset)* | Robot vacuum device ID |
| `TUYA_REGION` | `eu` | API region: `eu` `us` `cn` `in` |

### Persona

Create `ME.md` in the working directory or `~/.familiar_ai/ME.md`.
This file is injected at the top of every system prompt and defines personality, dialect, and speaking style. It is gitignored — never commit it.

---

## ruri-v3 model setup

Download from [keisuke-miyako/ruri-v3-30m-onnx-fp32](https://huggingface.co/keisuke-miyako/ruri-v3-30m-onnx-fp32):

```bash
# Using huggingface-cli (pip install huggingface_hub)
hf download keisuke-miyako/ruri-v3-30m-onnx-fp32 --local-dir ~/.familiar_ai/models/ruri-v3
```

Or manually place the files:

```
~/.familiar_ai/models/ruri-v3/
  model.onnx
  tokenizer.model
```

Alternatively place them in `./models/ruri-v3/` relative to the working directory.

If the model files are missing, the server logs a warning and falls back to `StubAgent`.

---

## Persistent files

| Path | Contents |
|---|---|
| `~/.familiar_ai/observations.db` | SQLite memory store (observations + embeddings) |
| `~/.familiar_ai/desires.json` | Desire levels (persisted across restarts) |
| `~/.familiar_ai/chat.log` | Conversation log (appended each session) |
| `~/.familiar_ai/captures/` | RTSP frame snapshots (`capture_YYYYMMDD_HHmmss.jpg`) |
| `~/.familiar_ai/models/ruri-v3/` | ruri-v3 ONNX model files |
| `~/.familiar_ai/ME.md` | Persona file (optional) |

---

## WebSocket protocol

Connect to `ws://{host}:{port}/ws`.

### Client → server

```jsonc
// Send a chat message
{ "type": "chat",          "data": { "message": "こんにちは" } }

// Clear conversation history
{ "type": "clear_history", "data": {} }
```

### Server → client

| `type` | `data` fields | Description |
|---|---|---|
| `connected` | `status`, `agent_name` | Sent immediately on connect |
| `user_message` | `sender`, `message` | Echo of incoming chat |
| `text_chunk` | `chunk` | Streaming LLM output fragment |
| `action` | `name`, `icon`, `label`, `input` | Tool call notification |
| `response_complete` | `full_text`, `actions` | Turn finished |
| `status` | `message` | System message / desire murmur |
| `error` | `message` | Error notification |
| `history_cleared` | `{}` | History was cleared |

---

## Tools

| Tool | Always available | Description |
|---|---|---|
| `remember` | ✓ | Save text to long-term memory |
| `recall` | ✓ | Semantic search over past memories |
| `tom` | ✓ | Theory of Mind: perspective-taking scaffold |
| `see` | With camera | Capture current RTSP frame → base64 JPEG |
| `look` | With camera | ONVIF PTZ pan/tilt |
| `say` | With TTS | Speak text via VOICEVOX or ElevenLabs |
| `walk` | With mobility | Tuya robot vacuum: direction control |

---

## Architecture

```
Program.cs
  └── ASP.NET Core / Kestrel
        └── /ws  ──→  FamiliarServer          (WebSocket session manager)
                           │
                     AgentLoopService          (background loop)
                           │  user turn / desire turn
                     EmbodiedAgent             (ReAct loop, TAPE planning)
                        ├── KimiBackend        (Moonshot AI SSE streaming)
                        ├── ObservationMemory  (SQLite + ruri-v3 vectors)
                        ├── MemoryTool         (remember / recall)
                        ├── TomTool            (Theory of Mind)
                        ├── CameraTool         (ONVIF + RTSP, optional)
                        ├── TtsTool            (VOICEVOX / ElevenLabs, optional)
                        └── MobilityTool       (Tuya Cloud API, optional)
                     DesireSystem              (autonomous motivations)
                     ChatLogger                (stdout + chat.log)
```

### ReAct + TAPE loop

Each turn:
1. **Morning reconstruction** (first turn only) — loads self-model, curiosity targets, and recent feelings from memory.
2. **Memory context** — recalls semantically relevant memories and recent feelings, appended to the user message.
3. **TAPE planning** — generates a 2–4 step plan injected into the system prompt.
4. **ReAct loop** (max 50 iterations) — `stream_turn → tool_calls → observe → repeat` until `end_turn`.
5. **Adaptive replanning** — after each tool result, checks if the result blocks the plan. If so, generates a revised step appended to the tool result.
6. **Post-turn memory saves** (fire-and-forget) — infers emotion, summarises the exchange, updates self-model on emotional turns.

### Desire system

Desires grow at fixed rates per second of idle time:

| Desire | Growth | Fires after |
|---|---|---|
| `look_around` | 0.012/s | ~40 s |
| `explore` | 0.005/s | ~2 min |
| `greet_companion` | 0 | manual boost only |
| `rest` | 0 | manual boost only |

When a desire exceeds 0.6 and the agent has been idle for 90 s, a murmur is broadcast to clients and a desire-driven inner-voice turn fires.

---

## Building

```bash
cd dotnet
dotnet build src/FamiliarAI.Server
dotnet run  --project src/FamiliarAI.Server
```

Publish single-file (Windows x64):

```bash
dotnet publish src/FamiliarAI.Server \
  -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -o publish/
```

---

## Relation to the Python version

This server is a standalone .NET port of the Python `src/familiar_agent/` stack.
It shares the same WebSocket protocol, memory database schema, and prompt files,
so a frontend written for the Python server works with this one unchanged.

Key differences:
- LLM backend: **Kimi only** (Python supports Anthropic / Gemini / OpenAI / Kimi)
- No TUI or REPL — WebSocket server only
- Embedding model: **ruri-v3 ONNX** (Python uses `multilingual-e5-small` via torch)
- Mobility backend: **Tuya Cloud API** (Python uses `tinytuya` library directly)
