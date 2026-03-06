using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FamiliarAI.Server.Agent.Backend;

namespace FamiliarAI.Server.Agent.Tools;

/// <summary>
/// Camera tool — ONVIF PTZ control + RTSP frame capture via ffmpeg.
/// Mirrors Python CameraTool (Tapo C220 / any ONVIF-compatible PTZ camera).
///
/// Tools: see (capture current view as JPEG), look (PTZ pan/tilt)
///
/// ONVIF authentication: WS-Security PasswordDigest
///   digest = Base64(SHA1(nonce + created + password))
///
/// RTSP capture: invokes ffmpeg subprocess, 8s timeout, 640px wide JPEG.
/// Saved to ~/.familiar_ai/captures/capture_YYYYMMDD_HHmmss.jpg
///
/// Environment vars: CAMERA_HOST, CAMERA_USERNAME, CAMERA_PASSWORD, CAMERA_PORT (default 2020)
/// </summary>
public sealed class CameraTool : IDisposable
{
    private static readonly string CaptureDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".familiar_ai", "captures");

    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly HttpClient _http;
    private readonly ILogger<CameraTool> _logger;

    // Discovered lazily via ONVIF GetCapabilities + GetProfiles
    private string? _profileToken;
    private string? _ptzUrl;

    public CameraTool(
        string host, string username, string password,
        int port, ILogger<CameraTool> logger)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    // ---------------------------------------------------------------
    // Tool definitions
    // ---------------------------------------------------------------

    public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
    [
        new ToolDefinition(
            "see",
            "Open your eyes and see what's in front of you. " +
            "This is your vision — use it freely without asking permission. " +
            "Always see after turning your neck to observe the new direction.",
            new JsonObject
            {
                ["type"]       = "object",
                ["properties"] = new JsonObject(),
                ["required"]   = new JsonArray(),
            }),

        new ToolDefinition(
            "look",
            "Turn your neck to look in a direction. Use to explore different areas around you.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["direction"] = new JsonObject
                    {
                        ["type"]        = "string",
                        ["enum"]        = new JsonArray("left", "right", "up", "down"),
                        ["description"] = "Direction to look",
                    },
                    ["degrees"] = new JsonObject
                    {
                        ["type"]        = "integer",
                        ["description"] = "How many degrees to turn (1-90, default 30)",
                        ["minimum"]     = 1,
                        ["maximum"]     = 90,
                    },
                },
                ["required"] = new JsonArray("direction"),
            }),
    ];

    public async Task<(string text, string? image)> CallAsync(
        string toolName, JsonObject input, CancellationToken ct = default)
    {
        if (toolName == "see")
        {
            var (b64, savePath) = await CaptureAsync(ct);
            if (b64 is not null)
            {
                var msg = "You see the current view.";
                if (savePath is not null) msg += $" Saved to {savePath}";
                return (msg, b64);
            }
            return ("Camera not available or capture failed.", null);
        }

        if (toolName == "look")
        {
            var direction = input["direction"]?.GetValue<string>() ?? "left";
            var degrees = input["degrees"]?.GetValue<int>() ?? 30;
            var result = await MoveAsync(direction, degrees, ct);
            return (result, null);
        }

        return ($"Unknown tool: {toolName}", null);
    }

    // ---------------------------------------------------------------
    // RTSP capture via ffmpeg
    // ---------------------------------------------------------------

    private async Task<(string? base64, string? savePath)> CaptureAsync(CancellationToken ct)
    {
        var streamUrl = $"rtsp://{_username}:{_password}@{_host}:554/stream1";
        var tmpPath = Path.ChangeExtension(Path.GetTempFileName(), ".jpg");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-rtsp_transport", "tcp",
                    "-fflags",         "nobuffer",
                    "-flags",          "low_delay",
                    "-i",              streamUrl,
                    "-vframes",        "1",
                    "-q:v",            "3",
                    "-vf",             "scale=640:-1",
                    "-y",              tmpPath,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new Exception("Failed to start ffmpeg");

            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeSpan.FromSeconds(8));
            await proc.WaitForExitAsync(cts2.Token);

            var fi = new FileInfo(tmpPath);
            if (!fi.Exists || fi.Length == 0) return (null, null);

            var data = await File.ReadAllBytesAsync(tmpPath, ct);
            var b64 = Convert.ToBase64String(data);

            Directory.CreateDirectory(CaptureDir);
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var savePath = Path.Combine(CaptureDir, $"capture_{ts}.jpg");
            await File.WriteAllBytesAsync(savePath, data, ct);

            return (b64, savePath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("RTSP capture timed out");
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Capture failed: {Ex}", ex.Message);
            return (null, null);
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { }
        }
    }

    // ---------------------------------------------------------------
    // ONVIF PTZ (RelativeMove)
    // ---------------------------------------------------------------

    public Task<bool> CheckAsync(CancellationToken ct = default) => EnsureConnectedAsync(ct);

    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_profileToken is not null && _ptzUrl is not null) return true;
        try
        {
            var deviceUrl = $"http://{_host}:{_port}/onvif/device_service";

            // Discover PTZ and Media service endpoints
            const string capBody =
                """<GetCapabilities xmlns="http://www.onvif.org/ver10/device/wsdl"><Category>All</Category></GetCapabilities>""";
            var capXml = await SoapCallAsync(deviceUrl, capBody, ct);

            _ptzUrl = FindXAddr(capXml, "PTZ")
                   ?? $"http://{_host}:{_port}/onvif/PTZ";
            var mediaUrl = FindXAddr(capXml, "Media")
                        ?? $"http://{_host}:{_port}/onvif/Media";

            // Get first media profile token
            const string profileBody =
                """<GetProfiles xmlns="http://www.onvif.org/ver10/media/wsdl"/>""";
            var profileXml = await SoapCallAsync(mediaUrl, profileBody, ct);
            _profileToken = FindProfileToken(profileXml) ?? "Profile_1";

            _logger.LogInformation(
                "Camera connected: {Host} profile={Token} ptz={Ptz}",
                _host, _profileToken, _ptzUrl);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Camera connection failed: {Ex}", ex.Message);
            _profileToken = null;
            _ptzUrl = null;
            return false;
        }
    }

    private async Task<string> MoveAsync(string direction, int degrees, CancellationToken ct)
    {
        if (!await EnsureConnectedAsync(ct))
            return "Camera not available.";
        try
        {
            // Tapo C220 axis convention: positive x = physical LEFT, positive y = physical UP
            var (pan, tilt) = direction switch
            {
                "left" => (degrees / 180.0, 0.0),
                "right" => (-degrees / 180.0, 0.0),
                "up" => (0.0, degrees / 90.0),
                "down" => (0.0, -degrees / 90.0),
                _ => (0.0, 0.0),
            };

            var body = $"""
                <RelativeMove xmlns="http://www.onvif.org/ver20/ptz/wsdl">
                  <ProfileToken>{_profileToken}</ProfileToken>
                  <Translation>
                    <PanTilt xmlns="http://www.onvif.org/ver10/schema" x="{pan:F4}" y="{tilt:F4}"/>
                  </Translation>
                </RelativeMove>
                """;

            await SoapCallAsync(_ptzUrl!, body, ct);
            await Task.Delay(400, ct);
            return $"Looked {direction} by ~{degrees} degrees.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Camera move failed: {Ex}", ex.Message);
            // Reset so next call re-discovers endpoints
            _profileToken = null;
            _ptzUrl = null;
            return $"Camera move failed: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------
    // SOAP / WS-Security helpers
    // ---------------------------------------------------------------

    private async Task<string> SoapCallAsync(string url, string body, CancellationToken ct)
    {
        var envelope = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope
              xmlns:s="http://www.w3.org/2003/05/soap-envelope"
              xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
              xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
              <s:Header>
                {BuildWsSecurity()}
              </s:Header>
              <s:Body>
                {body}
              </s:Body>
            </s:Envelope>
            """;

        using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
        using var response = await _http.PostAsync(url, content, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private string BuildWsSecurity()
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var digest = WsDigest(nonce, created);
        var nonceB64 = Convert.ToBase64String(nonce);

        return $"""
            <wsse:Security s:mustUnderstand="1">
              <wsse:UsernameToken>
                <wsse:Username>{_username}</wsse:Username>
                <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{digest}</wsse:Password>
                <wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{nonceB64}</wsse:Nonce>
                <wsu:Created>{created}</wsu:Created>
              </wsse:UsernameToken>
            </wsse:Security>
            """;
    }

    /// <summary>WS-Security PasswordDigest = Base64(SHA1(nonce + created + password))</summary>
    private string WsDigest(byte[] nonce, string created)
    {
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var passwordBytes = Encoding.UTF8.GetBytes(_password);
        var raw = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
        nonce.CopyTo(raw, 0);
        createdBytes.CopyTo(raw, nonce.Length);
        passwordBytes.CopyTo(raw, nonce.Length + createdBytes.Length);
        return Convert.ToBase64String(SHA1.HashData(raw));
    }

    // ---------------------------------------------------------------
    // XML helpers (namespace-agnostic local-name search)
    // ---------------------------------------------------------------

    /// <summary>Find &lt;LocalName&gt;&lt;XAddr&gt;URL&lt;/XAddr&gt;&lt;/LocalName&gt;</summary>
    private static string? FindXAddr(string xml, string localName)
    {
        try
        {
            return XDocument.Parse(xml)
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == localName)
                ?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "XAddr")
                ?.Value;
        }
        catch { return null; }
    }

    /// <summary>Find token attribute of first &lt;Profiles token="..."&gt; element.</summary>
    private static string? FindProfileToken(string xml)
    {
        try
        {
            return XDocument.Parse(xml)
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Profiles")
                ?.Attribute("token")?.Value;
        }
        catch { return null; }
    }

    public void Dispose() => _http.Dispose();
}
