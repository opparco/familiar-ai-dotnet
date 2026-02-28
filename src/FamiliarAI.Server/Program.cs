using FamiliarAI.Server;
using FamiliarAI.Server.Agent;
using FamiliarAI.Server.Agent.Backend;

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

// Suppress default ASP.NET banner; we'll print our own
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddConsole();

builder.WebHost.ConfigureKestrel(opts =>
{
    opts.ListenAnyIP(port);
});

// Agent: KimiAgent is default; fall back to StubAgent if no API key
builder.Services.AddSingleton<IFamiliarAgent>(sp =>
{
    if (!string.IsNullOrEmpty(apiKey) && platform == "kimi")
    {
        return new KimiAgent(
            config,
            sp.GetRequiredService<ILogger<KimiAgent>>(),
            sp.GetRequiredService<ILogger<KimiBackend>>());
    }
    sp.GetRequiredService<ILogger<StubAgent>>()
      .LogWarning("API_KEY not set — using StubAgent (no real LLM calls)");
    return new StubAgent(config);
});

builder.Services.AddSingleton<FamiliarServer>();
builder.Services.AddHostedService<AgentLoopService>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});

// WebSocket endpoint — the only route (CLI mode: no static files, no HTML serving)
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
