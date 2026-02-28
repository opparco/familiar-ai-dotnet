namespace FamiliarAI.Server;

/// <summary>
/// Writes conversation events to stdout and ~/.familiar_ai/chat.log.
///
/// Format:
///   [HH:mm:ss] USER    ▶ hello there
///   [HH:mm:ss]   ⚙️  recall(query=hello)
///   [HH:mm:ss] Familiar ▶ こんにちは！
///   [HH:mm:ss] ※ なんか外が気になってきた…
///
/// Opened once at startup; each call appends a line to the file.
/// Thread-safe via a simple lock.
/// </summary>
public sealed class ChatLogger
{
    private static readonly string LogPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".familiar_ai", "chat.log");

    private readonly object _lock = new();

    public ChatLogger()
    {
        var sep = new string('─', 60);
        var header = $"\n{sep}\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] session start\n";
        lock (_lock)
        {
            Console.Write(header);
            AppendFile(header);
        }
    }

    // ---------------------------------------------------------------
    // Public log methods
    // ---------------------------------------------------------------

    /// <summary>User message received from a client.</summary>
    public void LogUserMessage(string sender, string text)
        => WriteLine($"[{Now}] {sender} ▶ {text}");

    /// <summary>Full agent response text (called once per turn on completion).</summary>
    public void LogAgentText(string agentName, string text)
    {
        if (string.IsNullOrEmpty(text) || text == "(no response)") return;
        WriteLine($"[{Now}] {agentName} ▶ {text}");
    }

    /// <summary>Tool call executed during a turn.</summary>
    public void LogAction(string icon, string label)
        => WriteLine($"[{Now}]   {icon} {label}");

    /// <summary>System/murmur status message.</summary>
    public void LogStatus(string message)
        => WriteLine($"[{Now}] ※ {message}");

    // ---------------------------------------------------------------
    // Internal helpers
    // ---------------------------------------------------------------

    private static string Now => DateTime.Now.ToString("HH:mm:ss");

    private void WriteLine(string line)
    {
        lock (_lock)
        {
            Console.WriteLine(line);
            AppendFile(line + "\n");
        }
    }

    private static void AppendFile(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, text, System.Text.Encoding.UTF8);
        }
        catch { /* never crash on log write */ }
    }
}
