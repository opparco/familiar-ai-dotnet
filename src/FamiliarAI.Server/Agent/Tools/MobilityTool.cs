using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FamiliarAI.Server.Agent.Backend;

namespace FamiliarAI.Server.Agent.Tools;

/// <summary>
/// Robot vacuum mobility tool via Tuya Cloud API.
/// Mirrors Python MobilityTool (tinytuya).
///
/// Tool: walk(direction, duration?) — sends direction_control command.
/// Directions: forward / backward / left / right / stop
///
/// Tuya Cloud API v1.0 authentication:
///   Token request:  HMAC-SHA256(apiKey + t, secret).Upper()
///   API requests:   HMAC-SHA256(apiKey + token + t + stringToSign, secret).Upper()
///   where stringToSign = METHOD\nSHA256HEX(body)\n\npath
///
/// Access token is cached and refreshed on expiry.
///
/// Environment vars:
///   TUYA_REGION    eu | us | cn | in  (default: eu)
///   TUYA_API_KEY
///   TUYA_API_SECRET
///   TUYA_DEVICE_ID
/// </summary>
public sealed class MobilityTool : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> RegionHosts =
        new Dictionary<string, string>
        {
            ["cn"] = "https://openapi.tuyacn.com",
            ["us"] = "https://openapi.tuyaus.com",
            ["eu"] = "https://openapi.tuyaeu.com",
            ["in"] = "https://openapi.tuyain.com",
        };

    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _deviceId;
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly ILogger<MobilityTool> _logger;

    private string? _accessToken;
    private long    _tokenExpiresAt; // Unix seconds

    public MobilityTool(
        string apiRegion, string apiKey, string apiSecret, string deviceId,
        ILogger<MobilityTool> logger)
    {
        _apiKey    = apiKey;
        _apiSecret = apiSecret;
        _deviceId  = deviceId;
        _baseUrl   = RegionHosts.GetValueOrDefault(apiRegion.ToLower(), RegionHosts["eu"]);
        _logger    = logger;
        _http      = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    // ---------------------------------------------------------------
    // Tool definition
    // ---------------------------------------------------------------

    public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
    [
        new ToolDefinition(
            "walk",
            "Walk the robot body. Use to navigate around the room. " +
            "Always stop after walking to avoid collisions.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["direction"] = new JsonObject
                    {
                        ["type"]        = "string",
                        ["enum"]        = new JsonArray("forward", "backward", "left", "right", "stop"),
                        ["description"] = "Direction to move",
                    },
                    ["duration"] = new JsonObject
                    {
                        ["type"]        = "number",
                        ["description"] = "How long to move in seconds (0.1-10). If omitted, moves until stopped.",
                        ["minimum"]     = 0.1,
                        ["maximum"]     = 10.0,
                    },
                },
                ["required"] = new JsonArray("direction"),
            }),
    ];

    public async Task<(string text, string? image)> CallAsync(
        string toolName, JsonObject input, CancellationToken ct = default)
    {
        if (toolName != "walk")
            return ($"Unknown tool: {toolName}", null);

        var direction = input["direction"]?.GetValue<string>() ?? "stop";
        var duration  = input["duration"]?.GetValue<double?>();

        try
        {
            var result = await MoveAsync(direction, duration, ct);
            return (result, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Walk failed: {Ex}", ex.Message);
            return ($"Move failed: {ex.Message}", null);
        }
    }

    // ---------------------------------------------------------------
    // Movement
    // ---------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> TuyaDirections =
        new Dictionary<string, string>
        {
            ["forward"]  = "forward",
            ["backward"] = "backward",
            ["left"]     = "turn_left",
            ["right"]    = "turn_right",
            ["stop"]     = "stop",
        };

    private async Task<string> MoveAsync(string direction, double? duration, CancellationToken ct)
    {
        if (!TuyaDirections.TryGetValue(direction, out var tuyaDir))
            return $"Invalid direction: {direction}";

        await SendCommandAsync(tuyaDir, ct);

        if (duration.HasValue && tuyaDir != "stop")
        {
            await Task.Delay(TimeSpan.FromSeconds(duration.Value), ct);
            await SendCommandAsync("stop", ct);
            return $"Moved {direction} for {duration}s and stopped.";
        }

        return direction == "stop" ? "Stopped." : $"Moving {direction}.";
    }

    // ---------------------------------------------------------------
    // Tuya Cloud API
    // ---------------------------------------------------------------

    private async Task SendCommandAsync(string tuyaDirection, CancellationToken ct)
    {
        var token  = await GetTokenAsync(ct);
        var path   = $"/v1.0/devices/{_deviceId}/commands";
        var body   = JsonSerializer.Serialize(new
        {
            commands = new[] { new { code = "direction_control", value = tuyaDirection } },
        });

        var t    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sign = SignRequest(_apiKey, token, t, "POST", path, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path);
        request.Headers.Add("client_id",    _apiKey);
        request.Headers.Add("access_token", token);
        request.Headers.Add("sign_method",  "HMAC-SHA256");
        request.Headers.Add("t",            t);
        request.Headers.Add("sign",         sign);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var success = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
        if (!success)
        {
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.ToString() : "?";
            var msg  = doc.RootElement.TryGetProperty("msg",  out var m) ? m.GetString() : json;
            throw new Exception($"Tuya API error {code}: {msg}");
        }

        _logger.LogDebug("Walk command sent: {Dir}", tuyaDirection);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_accessToken is not null && now < _tokenExpiresAt - 60)
            return _accessToken;

        var t    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sign = SignTokenRequest(_apiKey, t);
        var path = "/v1.0/token?grant_type=1";

        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
        request.Headers.Add("client_id",   _apiKey);
        request.Headers.Add("sign_method", "HMAC-SHA256");
        request.Headers.Add("t",           t);
        request.Headers.Add("sign",        sign);

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("success", out var s) || !s.GetBoolean())
            throw new Exception($"Tuya token request failed: {json}");

        var result     = doc.RootElement.GetProperty("result");
        _accessToken   = result.GetProperty("access_token").GetString()!;
        var expiresIn  = result.TryGetProperty("expire_time", out var e) ? e.GetInt64() : 7200;
        _tokenExpiresAt = now + expiresIn;

        _logger.LogDebug("Tuya token obtained, expires in {Secs}s", expiresIn);
        return _accessToken;
    }

    // ---------------------------------------------------------------
    // HMAC-SHA256 signing helpers
    // ---------------------------------------------------------------

    /// <summary>Token request signature: HMAC-SHA256(apiKey + t, secret).Upper()</summary>
    private string SignTokenRequest(string apiKey, string t)
    {
        var msg = apiKey + t;
        return HmacSha256Upper(msg);
    }

    /// <summary>
    /// API request signature:
    /// HMAC-SHA256(apiKey + token + t + METHOD\nSHA256HEX(body)\n\npath, secret).Upper()
    /// </summary>
    private string SignRequest(string apiKey, string token, string t,
        string method, string path, string body)
    {
        var bodyHash    = Sha256Hex(body);
        var stringToSign = $"{method}\n{bodyHash}\n\n{path}";
        var msg          = apiKey + token + t + stringToSign;
        return HmacSha256Upper(msg);
    }

    private string HmacSha256Upper(string message)
    {
        var key  = Encoding.UTF8.GetBytes(_apiSecret);
        var data = Encoding.UTF8.GetBytes(message);
        return Convert.ToHexString(HMACSHA256.HashData(key, data));
        // Convert.ToHexString is already uppercase
    }

    private static string Sha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLower();
    }

    public void Dispose() => _http.Dispose();
}
