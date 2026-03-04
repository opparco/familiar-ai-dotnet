using Microsoft.Data.Sqlite;

namespace FamiliarAI.Server.Agent.Tools;

/// <summary>
/// SQLite + ruri-v3 ONNX vector memory store.
/// Mirrors Python ObservationMemory: two tables (observations + obs_embeddings),
/// hybrid recall (vector similarity → LIKE keyword → recency fallback).
///
/// Memory kinds: observation, conversation, feeling, self_model, curiosity
/// </summary>
public sealed class ObservationMemory : IDisposable
{
    private static readonly string DefaultDbPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".familiar_ai", "observations.db");

    private readonly string _dbPath;
    private readonly RuriEmbedding _embedder;
    private readonly ILogger<ObservationMemory> _logger;
    private SqliteConnection? _conn;
    private readonly object _lock = new();

    public ObservationMemory(
        RuriEmbedding embedder,
        ILogger<ObservationMemory> logger,
        string? dbPath = null)
    {
        _embedder = embedder;
        _logger = logger;
        _dbPath = dbPath ?? DefaultDbPath;
    }

    // ---------------------------------------------------------------
    // DDL
    // ---------------------------------------------------------------

    private SqliteConnection EnsureConnected()
    {
        if (_conn is not null) return _conn;

        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();

        using var pragma = _conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        using var ddl = _conn.CreateCommand();
        ddl.CommandText = """
            CREATE TABLE IF NOT EXISTS observations (
                id        TEXT PRIMARY KEY,
                content   TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                date      TEXT NOT NULL,
                time      TEXT NOT NULL,
                direction TEXT NOT NULL DEFAULT 'unknown',
                kind      TEXT NOT NULL DEFAULT 'observation',
                emotion   TEXT NOT NULL DEFAULT 'neutral',
                image_data TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_obs_timestamp ON observations(timestamp);
            CREATE INDEX IF NOT EXISTS idx_obs_kind      ON observations(kind);

            CREATE TABLE IF NOT EXISTS obs_embeddings (
                obs_id TEXT PRIMARY KEY REFERENCES observations(id) ON DELETE CASCADE,
                vector BLOB NOT NULL
            );
            """;
        ddl.ExecuteNonQuery();

        return _conn;
    }

    // ---------------------------------------------------------------
    // Save
    // ---------------------------------------------------------------

    public void Save(
        string content,
        string direction = "unknown",
        string kind = "observation",
        string emotion = "neutral")
    {
        try
        {
            lock (_lock)
            {
                var db = EnsureConnected();
                var now = DateTime.Now;
                var id = Guid.NewGuid().ToString();
                var vec = _embedder.EncodeDocument(content);

                using var ins = db.CreateCommand();
                ins.CommandText = """
                    INSERT INTO observations (id, content, timestamp, date, time, direction, kind, emotion)
                    VALUES ($id, $content, $ts, $date, $time, $dir, $kind, $emotion)
                    """;
                ins.Parameters.AddWithValue("$id", id);
                ins.Parameters.AddWithValue("$content", content);
                ins.Parameters.AddWithValue("$ts", now.ToString("o"));
                ins.Parameters.AddWithValue("$date", now.ToString("yyyy-MM-dd"));
                ins.Parameters.AddWithValue("$time", now.ToString("HH:mm"));
                ins.Parameters.AddWithValue("$dir", direction);
                ins.Parameters.AddWithValue("$kind", kind);
                ins.Parameters.AddWithValue("$emotion", emotion);
                ins.ExecuteNonQuery();

                using var emb = db.CreateCommand();
                emb.CommandText = "INSERT INTO obs_embeddings (obs_id, vector) VALUES ($id, $vec)";
                emb.Parameters.AddWithValue("$id", id);
                emb.Parameters.AddWithValue("$vec", VectorToBytes(vec));
                emb.ExecuteNonQuery();

                _logger.LogDebug("Saved {Kind}/{Emotion}: {Content}", kind, emotion, content[..Math.Min(60, content.Length)]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to save memory: {Ex}", ex.Message);
        }
    }

    public Task SaveAsync(
        string content,
        string direction = "unknown",
        string kind = "observation",
        string emotion = "neutral")
        => Task.Run(() => Save(content, direction, kind, emotion));

    // ---------------------------------------------------------------
    // Recall (vector similarity → LIKE → recency fallback)
    // ---------------------------------------------------------------

    public List<MemoryRecord> Recall(string query, int n = 3, string? kind = null)
    {
        try
        {
            lock (_lock)
            {
                var db = EnsureConnected();

                // -- vector path --
                using var countCmd = db.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(*) FROM obs_embeddings";
                var count = (long)countCmd.ExecuteScalar()!;

                if (count > 0)
                {
                    var qVec = _embedder.EncodeQuery(query);
                    var rows = LoadAllWithVectors(db, kind);
                    if (rows.Count == 0) return [];

                    var vecs = rows.Select(r => r.Vector).ToArray();
                    var scores = RuriEmbedding.CosineSimilarity(qVec, vecs);

                    return rows
                        .Select((r, i) => (r, score: scores[i]))
                        .OrderByDescending(x => x.score)
                        .Take(n)
                        .Select(x => x.r.Record with { Score = x.score })
                        .ToList();
                }

                // -- LIKE keyword fallback --
                var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                    .Where(w => w.Length > 1).Take(4).ToList();
                if (keywords.Count > 0)
                {
                    var likeResult = LikeSearch(db, keywords, kind, n);
                    if (likeResult.Count > 0) return likeResult;
                }

                // -- recency fallback --
                return RecentRows(db, kind, n);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Recall failed: {Ex}", ex.Message);
            return [];
        }
    }

    public Task<List<MemoryRecord>> RecallAsync(string query, int n = 3, string? kind = null)
        => Task.Run(() => Recall(query, n, kind));

    // ---------------------------------------------------------------
    // Specialised queries
    // ---------------------------------------------------------------

    public List<MemoryRecord> RecentFeelings(int n = 5)
    {
        try
        {
            lock (_lock)
            {
                var db = EnsureConnected();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    SELECT content, date, time, kind, emotion
                    FROM observations
                    WHERE kind IN ('feeling', 'conversation')
                    ORDER BY timestamp DESC LIMIT $n
                    """;
                cmd.Parameters.AddWithValue("$n", n);
                return ReadRecords(cmd);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("RecentFeelings failed: {Ex}", ex.Message);
            return [];
        }
    }

    public Task<List<MemoryRecord>> RecentFeelingsAsync(int n = 5)
        => Task.Run(() => RecentFeelings(n));

    public List<MemoryRecord> RecallByKind(string kind, int n = 5)
    {
        try
        {
            lock (_lock)
            {
                var db = EnsureConnected();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    SELECT content, date, time, kind, emotion
                    FROM observations WHERE kind = $kind
                    ORDER BY timestamp DESC LIMIT $n
                    """;
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$n", n);
                return ReadRecords(cmd);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("RecallByKind failed: {Ex}", ex.Message);
            return [];
        }
    }

    public Task<List<MemoryRecord>> RecallByKindAsync(string kind, int n = 5)
        => Task.Run(() => RecallByKind(kind, n));

    // ---------------------------------------------------------------
    // Context formatting  (mirrors Python format_* methods)
    // ---------------------------------------------------------------

    public static string FormatForContext(IReadOnlyList<MemoryRecord> memories, string header)
    {
        if (memories.Count == 0) return "";
        var lines = new List<string> { header };
        foreach (var m in memories)
        {
            var score = m.Score.HasValue ? $" (類似度:{m.Score:F2})" : "";
            var emotion = m.Emotion is not ("neutral" or "") ? $" [{m.Emotion}]" : "";
            lines.Add($"- {m.Date} {m.Time}{score}{emotion}: {m.Content[..Math.Min(120, m.Content.Length)]}");
        }
        return string.Join('\n', lines);
    }

    public static string FormatFeelingsForContext(IReadOnlyList<MemoryRecord> feelings, string header)
    {
        if (feelings.Count == 0) return "";
        var lines = new List<string> { header };
        foreach (var f in feelings)
        {
            var emotion = f.Emotion is not ("neutral" or "") ? $"[{f.Emotion}] " : "";
            lines.Add($"- {f.Date} {f.Time} {emotion}{f.Content[..Math.Min(120, f.Content.Length)]}");
        }
        return string.Join('\n', lines);
    }

    public static string FormatSelfModelForContext(IReadOnlyList<MemoryRecord> selfModel, string header)
    {
        if (selfModel.Count == 0) return "";
        var lines = new List<string> { header };
        foreach (var m in selfModel)
            lines.Add($"- {m.Content[..Math.Min(120, m.Content.Length)]}");
        return string.Join('\n', lines);
    }

    public static string FormatCuriositiesForContext(IReadOnlyList<MemoryRecord> curiosities, string header)
    {
        if (curiosities.Count == 0) return "";
        var lines = new List<string> { header };
        foreach (var c in curiosities)
            lines.Add($"- {c.Date} {c.Time}: {c.Content[..Math.Min(120, c.Content.Length)]}");
        return string.Join('\n', lines);
    }

    // ---------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------

    private record VectorRow(MemoryRecord Record, float[] Vector);

    private static List<VectorRow> LoadAllWithVectors(SqliteConnection db, string? kind)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = kind is null
            ? "SELECT o.content, o.date, o.time, o.kind, o.emotion, e.vector FROM observations o JOIN obs_embeddings e ON o.id = e.obs_id"
            : "SELECT o.content, o.date, o.time, o.kind, o.emotion, e.vector FROM observations o JOIN obs_embeddings e ON o.id = e.obs_id WHERE o.kind = $kind";
        if (kind is not null) cmd.Parameters.AddWithValue("$kind", kind);

        using var reader = cmd.ExecuteReader();
        var rows = new List<VectorRow>();
        while (reader.Read())
        {
            var rec = new MemoryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4));
            var blob = (byte[])reader["vector"];
            rows.Add(new VectorRow(rec, BytesToVector(blob)));
        }
        return rows;
    }

    private static List<MemoryRecord> LikeSearch(
        SqliteConnection db, List<string> keywords, string? kind, int n)
    {
        var conditions = string.Join(" OR ", keywords.Select(_ => "content LIKE ?"));
        var sql = kind is null
            ? $"SELECT content, date, time, kind, emotion FROM observations WHERE ({conditions}) ORDER BY timestamp DESC LIMIT ?"
            : $"SELECT content, date, time, kind, emotion FROM observations WHERE ({conditions}) AND kind = ? ORDER BY timestamp DESC LIMIT ?";

        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var kw in keywords)
            cmd.Parameters.AddWithValue("?", $"%{kw}%");
        if (kind is not null)
            cmd.Parameters.AddWithValue("?", kind);
        cmd.Parameters.AddWithValue("?", n);
        return ReadRecords(cmd);
    }

    private static List<MemoryRecord> RecentRows(SqliteConnection db, string? kind, int n)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = kind is null
            ? "SELECT content, date, time, kind, emotion FROM observations ORDER BY timestamp DESC LIMIT $n"
            : "SELECT content, date, time, kind, emotion FROM observations WHERE kind = $kind ORDER BY timestamp DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", n);
        if (kind is not null) cmd.Parameters.AddWithValue("$kind", kind);
        return ReadRecords(cmd);
    }

    private static List<MemoryRecord> ReadRecords(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<MemoryRecord>();
        while (reader.Read())
            list.Add(new MemoryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        return list;
    }

    private static byte[] VectorToBytes(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToVector(byte[] b)
    {
        var v = new float[b.Length / sizeof(float)];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }

    public void Dispose()
    {
        _conn?.Close();
        _conn?.Dispose();
    }
}

/// <summary>A single memory row returned from recall.</summary>
public sealed record MemoryRecord(
    string Content,
    string Date,
    string Time,
    string Kind,
    string Emotion)
{
    public float? Score { get; init; }
}
