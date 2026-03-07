using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FamiliarAI.Server.Agent.Backend;

/// <summary>
/// Dumps backend requests and responses to JSON files for debugging.
///
/// Output directory: env var BACKEND_DUMP_DIR, or ~/.familiar_ai/debug/.
/// Set BACKEND_DUMP_DIR=off to disable entirely.
///
/// File naming: {yyyyMMdd}/{HHmmss}_{seq:D3}_{backend}_{req|res}.json
/// </summary>
internal static class BackendDumper
{
    private static readonly string? DumpDir = ResolveDumpDir();
    private static int _seq = 0;

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string? ResolveDumpDir()
    {
        var env = Environment.GetEnvironmentVariable("BACKEND_DUMP_DIR");
        if (env is not null)
            return env.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : env;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".familiar_ai", "debug");
    }

    [Conditional("DEBUG")]
    public static void DumpRequest(string backend, JsonNode body)
    {
        if (DumpDir is null) return;
        Write(backend, "req", body.ToJsonString(_writeOptions));
    }

    [Conditional("DEBUG")]
    public static void DumpResponse(string backend, JsonNode response)
    {
        if (DumpDir is null) return;
        Write(backend, "res", response.ToJsonString(_writeOptions));
    }

    [Conditional("DEBUG")]
    public static void DumpResponse(string backend, string responseText)
    {
        if (DumpDir is null) return;
        // Wrap plain text in a JSON object for consistency
        var node = new JsonObject { ["content"] = responseText };
        Write(backend, "res", node.ToJsonString(_writeOptions));
    }

    private static void Write(string backend, string kind, string content)
    {
        try
        {
            var now = DateTime.Now;
            var dir = Path.Combine(DumpDir!, now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dir);
            var seq = Interlocked.Increment(ref _seq);
            var filename = $"{now:HHmmss}_{seq:D3}_{backend}_{kind}.json";
            File.WriteAllText(Path.Combine(dir, filename), content);
        }
        catch
        {
            // Never crash the agent due to debug logging
        }
    }
}
