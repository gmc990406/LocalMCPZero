using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace ZERO.Services;

public sealed class MemoryItem
{
    public long Id { get; init; }
    public string Content { get; init; } = "";
    public string Tags { get; init; } = "";
    public string Scope { get; init; } = "all";
    public string Source { get; init; } = "";
    public long SourceConv { get; init; }
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
    public bool Pinned { get; init; }
    public long Hits { get; init; }
    public DateTime? LastUsed { get; init; }

    public string FirstLine
    {
        get
        {
            var s = Content.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            return s.Length == 0 ? "（空）" : s;
        }
    }
    public bool HasTags => !string.IsNullOrWhiteSpace(Tags);
    public string ScopeText => Scope switch { "full" => "仅全电脑访问", "restricted" => "仅有限制模式", _ => "所有模式" };
    public string Meta => $"{(string.IsNullOrEmpty(Source) ? "手动添加" : Source)} · {HistoryStore.When(Updated)}{(Hits > 0 ? $" · 被查到 {Hits} 次" : "")}";
    public string Detail =>
        $"{(string.IsNullOrEmpty(Source) ? "手动添加" : "来自 " + Source)}{(SourceConv > 0 ? $"（历史 #{SourceConv}）" : "")} · " +
        $"创建 {Created:yyyy-MM-dd HH:mm} · 修改 {Updated:yyyy-MM-dd HH:mm}" +
        (LastUsed is { } u ? $" · AI 最近查到 {HistoryStore.When(u)}（共 {Hits} 次）" : " · AI 尚未查到过");
}

/// <summary>记忆库（%LOCALAPPDATA%\ZERO\memory.db）：AI 和用户都可以写入的长期记忆，不随历史记录清理。</summary>
public static class MemoryStore
{
    public static string DbPath { get; } =
        Environment.GetEnvironmentVariable("ZERO_MEMORY_DB") is { Length: > 0 } p ? p : Path.Combine(AppSettings.DataDir, "memory.db");

    // 与 server.py 中的 MEM_SCHEMA 保持一致
    const string Schema = """
        CREATE TABLE IF NOT EXISTS memories(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          content TEXT NOT NULL, tags TEXT NOT NULL DEFAULT '',
          scope TEXT NOT NULL DEFAULT 'all',
          source TEXT NOT NULL DEFAULT '', source_conv INTEGER NOT NULL DEFAULT 0,
          created REAL NOT NULL, updated REAL NOT NULL,
          pinned INTEGER NOT NULL DEFAULT 0, hits INTEGER NOT NULL DEFAULT 0, last_used REAL NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_mem_updated ON memories(updated);
        """;

    static bool _ready;

    static SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DbPath))!);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5,
        }.ToString());
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000;");
        if (!_ready)
        {
            Exec(c, "PRAGMA journal_mode=WAL;");
            Exec(c, Schema);
            Exec(c, "PRAGMA user_version=1;");
            _ready = true;
        }
        return c;
    }

    static void Exec(SqliteConnection c, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    static DateTime ToLocal(double ts) => DateTimeOffset.FromUnixTimeMilliseconds((long)(ts * 1000)).LocalDateTime;

    /// <summary>规范化标签：按逗号/空格等拆分、去重，最多 10 个</summary>
    public static string CleanTags(string tags) =>
        string.Join(", ", tags.Split(new[] { ',', '，', ';', '；', '、', ' ', '\t', '\n', '\r', '/', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).Select(t => t.Length > 30 ? t[..30] : t).Distinct().Take(10));

    public static List<MemoryItem> List(string query, string? tag = null)
    {
        var list = new List<MemoryItem>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        var terms = query.Split(new[] { ' ', '\t', ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Take(10).ToList();
        for (int i = 0; i < terms.Count; i++)
        {
            where.Add($"(content LIKE $q{i} OR tags LIKE $q{i} OR source LIKE $q{i})");
            cmd.Parameters.AddWithValue($"$q{i}", $"%{terms[i]}%");
        }
        if (!string.IsNullOrEmpty(tag)) { where.Add("(', ' || tags || ', ') LIKE $tag"); cmd.Parameters.AddWithValue("$tag", $"%, {tag}, %"); }
        cmd.CommandText = "SELECT id, content, tags, scope, source, source_conv, created, updated, pinned, hits, last_used FROM memories" +
                          (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                          " ORDER BY pinned DESC, updated DESC LIMIT 2000";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new MemoryItem
            {
                Id = r.GetInt64(0), Content = r.GetString(1), Tags = r.GetString(2), Scope = r.GetString(3), Source = r.GetString(4),
                SourceConv = r.GetInt64(5), Created = ToLocal(r.GetDouble(6)), Updated = ToLocal(r.GetDouble(7)), Pinned = r.GetInt64(8) != 0,
                Hits = r.GetInt64(9), LastUsed = r.GetDouble(10) > 0 ? ToLocal(r.GetDouble(10)) : null,
            });
        return list;
    }

    /// <summary>所有标签及使用次数（按次数排序）</summary>
    public static List<(string Tag, int Count)> AllTags()
    {
        var counts = new Dictionary<string, int>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT tags FROM memories WHERE tags != ''";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            foreach (var t in r.GetString(0).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                counts[t] = counts.GetValueOrDefault(t) + 1;
        return counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Select(k => (k.Key, k.Value)).ToList();
    }

    public static long Add(string content, string tags, string scope)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO memories(content, tags, scope, created, updated) VALUES($c, $t, $s, $n, $n); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$c", content.Trim());
        cmd.Parameters.AddWithValue("$t", CleanTags(tags));
        cmd.Parameters.AddWithValue("$s", scope);
        cmd.Parameters.AddWithValue("$n", Now);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public static void Update(long id, string content, string tags)
    {
        using var c = Open();
        Exec(c, "UPDATE memories SET content=$c, tags=$t, updated=$n WHERE id=$id",
            ("$c", content.Trim()), ("$t", CleanTags(tags)), ("$n", Now), ("$id", id));
    }

    public static void SetScope(long id, string scope)
    {
        using var c = Open();
        Exec(c, "UPDATE memories SET scope=$s WHERE id=$id", ("$s", scope), ("$id", id));
    }

    public static void SetPinned(long id, bool pinned)
    {
        using var c = Open();
        Exec(c, "UPDATE memories SET pinned=$p WHERE id=$id", ("$p", pinned ? 1 : 0), ("$id", id));
    }

    public static void Delete(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM memories WHERE id=$id", ("$id", id));
    }
}