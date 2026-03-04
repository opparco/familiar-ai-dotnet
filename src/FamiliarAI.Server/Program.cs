using System.Text.Json;
using FamiliarAI.Server;
using FamiliarAI.Server.Agent;
using FamiliarAI.Server.Agent.Backend;
using FamiliarAI.Server.Agent.Tools;

// ---- load .env (if present) ----
DotNetEnv.Env.Load();

// ---- configuration ----
var agentName = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "Familiar";
var companionName = Environment.GetEnvironmentVariable("COMPANION_NAME") ?? "USER";
var apiKey = Environment.GetEnvironmentVariable("API_KEY") ?? "";
var platform = Environment.GetEnvironmentVariable("PLATFORM") ?? "kimi";
var model = Environment.GetEnvironmentVariable("MODEL") ?? "";
var baseUrl = Environment.GetEnvironmentVariable("BASE_URL") ?? "";
var host = Environment.GetEnvironmentVariable("WEB_HOST") ?? "0.0.0.0";
var port = int.TryParse(Environment.GetEnvironmentVariable("WEB_PORT"), out var p) ? p : 5000;

var cameraHost = Environment.GetEnvironmentVariable("CAMERA_HOST") ?? "";
var cameraUser = Environment.GetEnvironmentVariable("CAMERA_USERNAME") ?? "admin";
var cameraPass = Environment.GetEnvironmentVariable("CAMERA_PASSWORD") ?? "";
var cameraPort = int.TryParse(Environment.GetEnvironmentVariable("CAMERA_PORT"), out var cp) ? cp : 2020;

var tuyaRegion = Environment.GetEnvironmentVariable("TUYA_REGION") ?? "eu";
var tuyaApiKey = Environment.GetEnvironmentVariable("TUYA_API_KEY") ?? "";
var tuyaApiSecret = Environment.GetEnvironmentVariable("TUYA_API_SECRET") ?? "";
var tuyaDeviceId = Environment.GetEnvironmentVariable("TUYA_DEVICE_ID") ?? "";

var ttsEngine = Environment.GetEnvironmentVariable("TTS_ENGINE") ?? "voicevox";
var voicevoxUrl = Environment.GetEnvironmentVariable("VOICEVOX_URL") ?? "http://localhost:50021";
var voicevoxSpk = int.TryParse(Environment.GetEnvironmentVariable("VOICEVOX_SPEAKER"), out var vs) ? vs : 3;
var elevenApiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") ?? "";
var elevenVoiceId = Environment.GetEnvironmentVariable("ELEVENLABS_VOICE_ID") ?? "";

var config = new AgentConfig(agentName, companionName, apiKey, platform, model);
var agentTextConfig = AgentTextConfig.LoadDefault();

// ---- build ----
var builder = WebApplication.CreateBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddConsole();

builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(port));

// ---- services ----
builder.Services.AddSingleton<IFamiliarAgent>(sp =>
{
    if (string.IsNullOrEmpty(apiKey))
    {
        sp.GetRequiredService<ILogger<StubAgent>>()
          .LogWarning("API_KEY not set — using StubAgent (no real LLM calls)");
        return new StubAgent(config);
    }

    ILlmBackend backend = platform.ToLower() switch
    {
        "openai" => new OpenAICompatibleBackend(
            apiKey,
            string.IsNullOrEmpty(model) ? "local-model" : model,
            string.IsNullOrEmpty(baseUrl) ? "http://localhost:1234/v1" : baseUrl,
            sp.GetRequiredService<ILogger<OpenAICompatibleBackend>>()),
        _ => new KimiBackend(
            apiKey,
            string.IsNullOrEmpty(model) ? "kimi-k2.5" : model,
            sp.GetRequiredService<ILogger<KimiBackend>>()),
    };

    // ruri-v3 embedding: lazy attempt, fall back to StubAgent if model files missing
    RuriEmbedding? embedder = null;
    try
    {
        var modelDir = RuriEmbedding.ResolveModelDir();
        embedder = new RuriEmbedding(modelDir);
        sp.GetRequiredService<ILogger<RuriEmbedding>>()
          .LogInformation("ruri-v3 loaded from {Dir}", modelDir);
    }
    catch (Exception ex)
    {
        sp.GetRequiredService<ILogger<RuriEmbedding>>()
          .LogWarning("ruri-v3 not available ({Ex}) — memory tool disabled, using StubAgent", ex.Message);
        return new StubAgent(config);
    }

    var memory = new ObservationMemory(embedder, sp.GetRequiredService<ILogger<ObservationMemory>>());

    // Camera (optional — only constructed when CAMERA_HOST is set)
    CameraTool? camera = null;
    if (!string.IsNullOrEmpty(cameraHost))
    {
        camera = new CameraTool(
            cameraHost, cameraUser, cameraPass, cameraPort,
            sp.GetRequiredService<ILogger<CameraTool>>());
        sp.GetRequiredService<ILogger<CameraTool>>()
          .LogInformation("Camera configured: {Host}:{Port}", cameraHost, cameraPort);
    }

    // TTS
    ITtsEngine ttsEngineImpl = ttsEngine.ToLower() == "elevenlabs" && !string.IsNullOrEmpty(elevenApiKey)
        ? new ElevenLabsEngine(elevenApiKey, elevenVoiceId)
        : new VoicevoxEngine(voicevoxUrl, voicevoxSpk);
    var tts = new TtsTool(ttsEngineImpl, sp.GetRequiredService<ILogger<TtsTool>>());
    sp.GetRequiredService<ILogger<TtsTool>>()
      .LogInformation("TTS engine: {Engine}", tts.EngineName);

    // Mobility (optional — only constructed when TUYA_API_KEY is set)
    MobilityTool? mobility = null;
    if (!string.IsNullOrEmpty(tuyaApiKey) && !string.IsNullOrEmpty(tuyaDeviceId))
    {
        mobility = new MobilityTool(
            tuyaRegion, tuyaApiKey, tuyaApiSecret, tuyaDeviceId,
            sp.GetRequiredService<ILogger<MobilityTool>>());
        sp.GetRequiredService<ILogger<MobilityTool>>()
          .LogInformation("Mobility configured: device={DeviceId} region={Region}", tuyaDeviceId, tuyaRegion);
    }

    return new EmbodiedAgent(config, agentTextConfig, backend, memory, sp.GetRequiredService<ILogger<EmbodiedAgent>>(), camera, tts, mobility);
});

builder.Services.AddSingleton(agentTextConfig);
builder.Services.AddSingleton<ChatLogger>();
builder.Services.AddSingleton<DesireSystem>();
builder.Services.AddSingleton<FamiliarServer>();
builder.Services.AddHostedService<AgentLoopService>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/config.js", () =>
{
    var js = $"const appConfig = {JsonSerializer.Serialize(new
    {
        typewriterDelay = 30,
        agentName,
        companionName,
    })};";
    return Results.Content(js, "application/javascript");
});

app.MapGet("/ws", async (HttpContext ctx, FamiliarServer server) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("WebSocket connection required.");
        return;
    }
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await server.HandleClientAsync(ctx, ws);
});

// ---- banner ----
Console.WriteLine();
Console.WriteLine($"  familiar-ai  [{agentName}]");
Console.WriteLine($"  Web UI    : http://{host}:{port}/");
Console.WriteLine($"  WebSocket : ws://{host}:{port}/ws");
var effectiveBaseUrl = platform.ToLower() is "openai"
    ? (string.IsNullOrEmpty(baseUrl) ? "http://localhost:1234/v1" : baseUrl)
    : "https://api.moonshot.ai/v1";
Console.WriteLine($"  Platform  : {platform}{(string.IsNullOrEmpty(apiKey) ? " (no API key → stub)" : "")} — {effectiveBaseUrl}");
Console.WriteLine($"  Camera    : {(string.IsNullOrEmpty(cameraHost) ? "not configured" : $"{cameraHost}:{cameraPort} (ONVIF)")}");
Console.WriteLine($"  TTS       : {ttsEngine.ToLower()}{(ttsEngine.ToLower() == "voicevox" ? $" (speaker {voicevoxSpk}, {voicevoxUrl})" : "")}");
Console.WriteLine($"  Mobility  : {(string.IsNullOrEmpty(tuyaApiKey) ? "not configured" : $"Tuya {tuyaRegion.ToUpper()} device={tuyaDeviceId[..Math.Min(8, tuyaDeviceId.Length)]}…")}");
var chatLogPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".familiar_ai", "chat.log");
Console.WriteLine($"  Chat log  : {chatLogPath}");
Console.WriteLine("  Press Ctrl+C to stop");
Console.WriteLine();

await app.RunAsync();
