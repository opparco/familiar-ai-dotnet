using FamiliarAI.Server;
using FamiliarAI.Server.Agent;
using FamiliarAI.Server.Agent.Backend;
using FamiliarAI.Server.Agent.Tools;

// ---- configuration ----
var agentName     = Environment.GetEnvironmentVariable("AGENT_NAME")     ?? "Familiar";
var companionName = Environment.GetEnvironmentVariable("COMPANION_NAME") ?? "USER";
var apiKey        = Environment.GetEnvironmentVariable("API_KEY")        ?? "";
var platform      = Environment.GetEnvironmentVariable("PLATFORM")       ?? "kimi";
var model         = Environment.GetEnvironmentVariable("MODEL")          ?? "";
var host          = Environment.GetEnvironmentVariable("WEB_HOST")       ?? "0.0.0.0";
var port          = int.TryParse(Environment.GetEnvironmentVariable("WEB_PORT"), out var p) ? p : 5000;

var config = new AgentConfig(agentName, companionName, apiKey, platform, model);

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

    var kimiModel = string.IsNullOrEmpty(model) ? "kimi-k2.5" : model;
    var backend   = new KimiBackend(apiKey, kimiModel, sp.GetRequiredService<ILogger<KimiBackend>>());

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

    return new EmbodiedAgent(config, backend, memory, sp.GetRequiredService<ILogger<EmbodiedAgent>>());
});

builder.Services.AddSingleton<FamiliarServer>();
builder.Services.AddHostedService<AgentLoopService>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

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
Console.WriteLine($"  WebSocket : ws://{host}:{port}/ws");
Console.WriteLine($"  Platform  : {platform}{(string.IsNullOrEmpty(apiKey) ? " (no API key → stub)" : "")}");
Console.WriteLine("  Press Ctrl+C to stop");
Console.WriteLine();

await app.RunAsync();
