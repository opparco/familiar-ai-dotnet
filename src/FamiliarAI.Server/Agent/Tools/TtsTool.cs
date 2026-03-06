using System.Text.Json.Nodes;
using FamiliarAI.Server.Agent.Backend;

namespace FamiliarAI.Server.Agent.Tools;

/// <summary>
/// TTS tool — VOICEVOX (default) or ElevenLabs voice synthesis.
/// Tool: say(text) → speaks aloud via local audio player.
///
/// VOICEVOX: 2-step local API (/audio_query → /synthesis) → WAV → player.
/// ElevenLabs: cloud API → MP3 → player.
///
/// Local playback tried in order: mpv → ffplay.
/// Text is truncated to 200 characters before synthesis.
///
/// Environment vars:
///   TTS_ENGINE        voicevox | elevenlabs  (default: voicevox)
///   VOICEVOX_URL      default: http://localhost:50021
///   VOICEVOX_SPEAKER  speaker ID (default: 3)
///   ELEVENLABS_API_KEY, ELEVENLABS_VOICE_ID
/// </summary>
public sealed class TtsTool : IDisposable
{
    private readonly ITtsEngine _engine;
    private readonly ILogger<TtsTool> _logger;

    public string EngineName => _engine.EngineName;

    public Task<bool> CheckAsync(CancellationToken ct = default)
        => Task.FromResult(_engine.IsAvailable());

    public TtsTool(ITtsEngine engine, ILogger<TtsTool> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    // ---------------------------------------------------------------
    // Tool definitions
    // ---------------------------------------------------------------

    public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
    [
        new ToolDefinition(
            "say",
            _engine.EngineName == "voicevox"
                ? "Speak text aloud using VOICEVOX (Japanese optimized). " +
                  "Use this to communicate with people in the room."
                : "Speak text aloud using ElevenLabs (supports audio tags like [cheerful], [warmly]). " +
                  "Use this to communicate with people in the room.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["text"] = new JsonObject
                    {
                        ["type"]        = "string",
                        ["description"] = "Text to speak.",
                    },
                },
                ["required"] = new JsonArray("text"),
            }),
    ];

    public async Task<(string text, string? image)> CallAsync(
        string toolName, JsonObject input, CancellationToken ct = default)
    {
        if (toolName == "say")
        {
            var text = input["text"]?.GetValue<string>() ?? "";
            var result = await SayAsync(text, ct);
            return (result, null);
        }
        return ($"Unknown tool: {toolName}", null);
    }

    // ---------------------------------------------------------------
    // Say implementation
    // ---------------------------------------------------------------

    private async Task<string> SayAsync(string text, CancellationToken ct)
    {
        if (text.Length > 200) text = text[..197] + "...";

        byte[] audio;
        string format;
        try
        {
            (audio, format) = await _engine.SynthesizeAsync(text, ct);
            _logger.LogDebug("TTS synthesized {Bytes} bytes ({Format})", audio.Length, format);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("TTS synthesis failed: {Ex}", ex.Message);
            return $"TTS synthesis failed: {ex.Message}";
        }

        var tmpPath = Path.ChangeExtension(Path.GetTempFileName(), "." + format);
        try
        {
            await File.WriteAllBytesAsync(tmpPath, audio, ct);
            var ok = await PlayLocalAsync(tmpPath, ct);
            return ok
                ? $"Said: {text[..Math.Min(50, text.Length)]}..."
                : "TTS playback failed (mpv and ffplay both failed)";
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { }
        }
    }

    private async Task<bool> PlayLocalAsync(string filePath, CancellationToken ct)
    {
        // mpv first, ffplay (bundled with ffmpeg) as fallback
        var candidates = new[]
        {
            new[] { "mpv", "--no-terminal", "--really-quiet", filePath },
            new[] { "ffplay", "-nodisp", "-autoexit", "-loglevel", "error", filePath },
        };

        foreach (var args in candidates)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(args[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);

            try
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) continue;
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0) return true;
                _logger.LogDebug("{Player} exited {Code}", args[0], proc.ExitCode);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("{Player} not available: {Ex}", args[0], ex.Message);
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_engine is IDisposable d) d.Dispose();
    }
}

// ── TTS engine interface ──────────────────────────────────────────────────────

public interface ITtsEngine
{
    string EngineName { get; }
    bool IsAvailable();
    Task<(byte[] audio, string format)> SynthesizeAsync(string text, CancellationToken ct);
}

// ── VOICEVOX ─────────────────────────────────────────────────────────────────

/// <summary>
/// VOICEVOX local TTS engine.
/// Step 1: POST /audio_query?text=...&amp;speaker=N  → query JSON
/// Step 2: POST /synthesis?speaker=N  (body = query JSON) → WAV bytes
/// </summary>
public sealed class VoicevoxEngine : ITtsEngine, IDisposable
{
    private readonly string _url;
    private readonly int _speaker;
    private readonly HttpClient _http;

    public string EngineName => "voicevox";

    public VoicevoxEngine(string url = "http://localhost:50021", int speaker = 3)
    {
        _url = url.TrimEnd('/');
        _speaker = speaker;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public bool IsAvailable()
    {
        try
        {
            using var resp = _http.GetAsync($"{_url}/version").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<(byte[] audio, string format)> SynthesizeAsync(string text, CancellationToken ct)
    {
        // Step 1: audio_query
        var queryUrl = $"{_url}/audio_query?text={Uri.EscapeDataString(text)}&speaker={_speaker}";
        using var qResp = await _http.PostAsync(queryUrl, null, ct);
        qResp.EnsureSuccessStatusCode();
        var queryJson = await qResp.Content.ReadAsStringAsync(ct);

        // Step 2: synthesis
        var synthUrl = $"{_url}/synthesis?speaker={_speaker}";
        using var body = new StringContent(queryJson, System.Text.Encoding.UTF8, "application/json");
        using var sResp = await _http.PostAsync(synthUrl, body, ct);
        sResp.EnsureSuccessStatusCode();
        var wav = await sResp.Content.ReadAsByteArrayAsync(ct);

        return (wav, "wav");
    }

    public void Dispose() => _http.Dispose();
}

// ── ElevenLabs ───────────────────────────────────────────────────────────────

/// <summary>
/// ElevenLabs cloud TTS engine (eleven_flash_v2_5 model).
/// </summary>
public sealed class ElevenLabsEngine : ITtsEngine, IDisposable
{
    private readonly string _voiceId;
    private readonly HttpClient _http;

    public string EngineName => "elevenlabs";

    public ElevenLabsEngine(string apiKey, string voiceId)
    {
        _voiceId = voiceId;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("xi-api-key", apiKey);
    }

    public bool IsAvailable() => true;

    public async Task<(byte[] audio, string format)> SynthesizeAsync(string text, CancellationToken ct)
    {
        var url = $"https://api.elevenlabs.io/v1/text-to-speech/{_voiceId}";
        var payload = new JsonObject
        {
            ["text"] = text,
            ["model_id"] = "eleven_flash_v2_5",
            ["voice_settings"] = new JsonObject
            {
                ["stability"] = 0.5,
                ["similarity_boost"] = 0.75,
            },
        };

        using var content = new StringContent(
            payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new Exception(
                $"ElevenLabs API error ({response.StatusCode}): {err[..Math.Min(80, err.Length)]}");
        }

        var mp3 = await response.Content.ReadAsByteArrayAsync(ct);
        return (mp3, "mp3");
    }

    public void Dispose() => _http.Dispose();
}
