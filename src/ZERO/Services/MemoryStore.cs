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
    public long Revision { get; init; } = 1;
    public bool Protected { get; init; }
    public DateTime? ArchivedAt { get; init; }
    public DateTime? DeletedAt { get; init; }
    public long Priority { get; init; }
    public long MergedInto { get; init; }

    public string FirstLine
    {
        get
        {
            var s = Content.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            return s.Length == 0 ? "（空）" : s;
        }
    }
    public bool HasTags => !string.IsNullOrWhiteSpace(Tags);
    public bool IsArchived => ArchivedAt != null;
    public string ScopeText => Scope switch { "full" => "仅全电脑访问", "restricted" => "仅有限制模式", _ => "所有模式" };
    public string StateText => DeletedAt != null ? "回收站" : MergedInto > 0 ? $"已合并到 #{MergedInto}" : IsArchived ? "已归档" : Priority < 0 ? "已降权" : "";
    public string Meta => $"{(string.IsNullOrEmpty(Source) ? "手动添加" : Source)} · {HistoryStore.When(Updated)}{(Hits > 0 ? $" · 被查到 {Hits} 次" : "")}" +
                          (StateText.Length > 0 ? " · " + StateText : "") + (Protected ? " · 受保护" : "");
    public string Detail =>
        $"{(string.IsNullOrEmpty(Source) ? "手动添加" : "来自 " + Source)}{(SourceConv > 0 ? $"（历史 #{SourceConv}）" : "")} · " +
        $"创建 {Created:yyyy-MM-dd HH:mm} · 修改 {Updated:yyyy-MM-dd HH:mm} · 版本 {Revision}" +
        (LastUsed is { } u ? $" · AI 最近查到 {HistoryStore.When(u)}（共 {Hits} 次）" : " · AI 尚未查到过") +
        (StateText.Length > 0 ? $" · {StateText}" : "");
}

public sealed class MemoryVersion
{
    public long Revision { get; init; }
    public string Content { get; init; } = "";
    public string Tags { get; init; } = "";
    public string State { get; init; } = "";
    public DateTime ChangedAt { get; init; }
    public string ActorType { get; init; } = "";
    public string Actor { get; init; } = "";
    public string Action { get; init; } = "";
    public string Reason { get; init; } = "";
    public string ActionText => Action switch
    {
        "baseline" => "升级时的原始内容", "create" => "创建", "update" => "修改", "flags" => "修改属性", "delete" => "删除到回收站",
        "restore" => "恢复", "merge" => "合并（保留）", "merged_into" => "被合并", "compress" => "压缩", "correct" => "更正",
        "archive" => "归档", "lower_priority" => "降权", _ => Action,
    };
    public string Header => $"版本 {Revision} · {ChangedAt:yyyy-MM-dd HH:mm} · {ActionText} · {(Actor.Length > 0 ? Actor : ActorType)}" +
                            (State != "active" ? $" · {(State == "deleted" ? "回收站" : "已归档")}" : "");
}

/// <summary>记忆库（%LOCALAPPDATA%\ZERO\memory.db）：AI 和用户都可以写入的长期记忆，不随历史记录清理。
/// v2 起每次修改都保留版本，删除先进入回收站；表结构见 zero_schema.json（与 MCP 服务共用）。</summary>
public static class MemoryStore
{
    public static string DbPath { get; } =
        Environment.GetEnvironmentVariable("ZERO_MEMORY_DB") is { Length: > 0 } p ? p : Path.Combine(AppSettings.DataDir, "memory.db");

    const string Cols = "id, content, tags, scope, source, source_conv, created, updated, pinned, hits, last_used, revision, protected, archived_at, deleted_at, priority, merged_into";
    static bool _ready;

    static SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DbPath))!);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 10,
        }.ToString());
        c.Open();
        Exec(c, null, "PRAGMA busy_timeout=10000;");
        if (!_ready)
        {
            SchemaMigrator.Migrate(c, DbPath, "memory");
            _ready = true;
        }
        return c;
    }

    static void Exec(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    internal static DateTime ToLocal(double ts) => DateTimeOffset.FromUnixTimeMilliseconds((long)(ts * 1000)).LocalDateTime;

    /// <summary>规范化标签：按逗号/空格等拆分、去重，最多 10 个</summary>
    public static string CleanTags(string tags) =>
        string.Join(", ", tags.Split(new[] { ',', '，', ';', '；', '、', ' ', '\t', '\n', '\r', '/', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).Select(t => t.Length > 30 ? t[..30] : t).Distinct().Take(10));

    static MemoryItem Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Content = r.GetString(1), Tags = r.GetString(2), Scope = r.GetString(3), Source = r.GetString(4),
        SourceConv = r.GetInt64(5), Created = ToLocal(r.GetDouble(6)), Updated = ToLocal(r.GetDouble(7)), Pinned = r.GetInt64(8) != 0,
        Hits = r.GetInt64(9), LastUsed = r.GetDouble(10) > 0 ? ToLocal(r.GetDouble(10)) : null,
        Revision = r.GetInt64(11), Protected = r.GetInt64(12) != 0,
        ArchivedAt = r.GetDouble(13) > 0 ? ToLocal(r.GetDouble(13)) : null, DeletedAt = r.GetDouble(14) > 0 ? ToLocal(r.GetDouble(14)) : null,
        Priority = r.GetInt64(15), MergedInto = r.GetInt64(16),
    };

    /// <summary>view：active = 未删除（含已归档）；trash = 回收站</summary>
    public static List<MemoryItem> List(string query, string? tag = null, string view = "active")
    {
        var list = new List<MemoryItem>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string> { view == "trash" ? "deleted_at>0" : "deleted_at=0" };
        var terms = query.Split(new[] { ' ', '\t', ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Take(10).ToList();
        for (int i = 0; i < terms.Count; i++)
        {
            where.Add($"(content LIKE $q{i} OR tags LIKE $q{i} OR source LIKE $q{i})");
            cmd.Parameters.AddWithValue($"$q{i}", $"%{terms[i]}%");
        }
        if (!string.IsNullOrEmpty(tag)) { where.Add("(', ' || tags || ', ') LIKE $tag"); cmd.Parameters.AddWithValue("$tag", $"%, {tag}, %"); }
        cmd.CommandText = $"SELECT {Cols} FROM memories WHERE " + string.Join(" AND ", where) +
                          (view == "trash" ? " ORDER BY deleted_at DESC LIMIT 2000" : " ORDER BY pinned DESC, (archived_at>0), updated DESC LIMIT 2000");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public static MemoryItem? Get(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM memories WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>所有标签及使用次数（按次数排序，不含回收站）</summary>
    public static List<(string Tag, int Count)> AllTags()
    {
        var counts = new Dictionary<string, int>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT tags FROM memories WHERE tags != '' AND deleted_at=0";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            foreach (var t in r.GetString(0).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                counts[t] = counts.GetValueOrDefault(t) + 1;
        return counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Select(k => (k.Key, k.Value)).ToList();
    }

    // ---- 写操作：每次都在一个事务里完成“修改 + 保存版本 + 审计事件” ----
    static void Snapshot(SqliteConnection c, SqliteTransaction t, long id, string action, string reason = "") =>
        Exec(c, t, "INSERT OR REPLACE INTO memory_versions(memory_id, revision, content, tags, scope, pinned, protected, state, changed_at, actor_type, actor, action, reason) " +
                   "SELECT id, revision, content, tags, scope, pinned, protected, CASE WHEN deleted_at>0 THEN 'deleted' WHEN archived_at>0 THEN 'archived' ELSE 'active' END, " +
                   "$n, 'user', '用户', $a, $r FROM memories WHERE id=$id", ("$n", Now), ("$a", action), ("$r", reason), ("$id", id));

    static void Event(SqliteConnection c, SqliteTransaction t, long id, string type, string detail = "") =>
        Exec(c, t, "INSERT INTO memory_events(memory_id, event_type, actor_type, actor_display, before_revision, after_revision, detail, created_at) " +
                   "SELECT id, $e, 'user', '用户', revision-1, revision, $d, $n FROM memories WHERE id=$id", ("$e", type), ("$d", detail), ("$n", Now), ("$id", id));

    static void Change(long id, string setSql, string action, string reason, params (string, object)[] ps)
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        Exec(c, t, $"UPDATE memories SET {setSql}, revision=revision+1 WHERE id=$id", ps.Append(("$id", (object)id)).ToArray());
        Snapshot(c, t, id, action, reason);
        Event(c, t, id, action, reason);
        t.Commit();
    }

    public static long Add(string content, string tags, string scope)
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "INSERT INTO memories(content, tags, scope, created, updated) VALUES($c, $t, $s, $n, $n); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$c", content.Trim());
        cmd.Parameters.AddWithValue("$t", CleanTags(tags));
        cmd.Parameters.AddWithValue("$s", scope);
        cmd.Parameters.AddWithValue("$n", Now);
        var id = (long)(cmd.ExecuteScalar() ?? 0L);
        Snapshot(c, t, id, "create");
        Exec(c, t, "INSERT INTO memory_events(memory_id, event_type, actor_type, actor_display, after_revision, created_at) VALUES($id, 'create', 'user', '用户', 1, $n)",
            ("$id", id), ("$n", Now));
        t.Commit();
        return id;
    }

    public static void Update(long id, string content, string tags) =>
        Change(id, "content=$c, tags=$t, updated=$n", "update", "", ("$c", content.Trim()), ("$t", CleanTags(tags)), ("$n", Now));

    public static void SetScope(long id, string scope) => Change(id, "scope=$s", "flags", $"可见范围 → {scope}", ("$s", scope));
    public static void SetPinned(long id, bool pinned) => Change(id, "pinned=$p", "flags", pinned ? "置顶" : "取消置顶", ("$p", pinned ? 1 : 0));
    public static void SetProtected(long id, bool on) => Change(id, "protected=$p", "flags", on ? "标记为受保护" : "取消保护", ("$p", on ? 1 : 0));

    /// <summary>删除到回收站（可恢复）</summary>
    public static void Delete(long id) => Change(id, "deleted_at=$n", "delete", "用户在 ZERO 中删除", ("$n", Now));

    /// <summary>从回收站 / 归档中恢复；revision &gt; 0 时同时把内容恢复到该版本（作为新版本保存）</summary>
    public static void Restore(long id, long revision = 0)
    {
        if (revision <= 0) { Change(id, "deleted_at=0, archived_at=0, merged_into=0, updated=$n", "restore", "从回收站/归档恢复", ("$n", Now)); return; }
        using var c = Open();
        using var t = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = t;
            cmd.CommandText = "UPDATE memories SET content=(SELECT content FROM memory_versions WHERE memory_id=$id AND revision=$r), " +
                              "tags=(SELECT tags FROM memory_versions WHERE memory_id=$id AND revision=$r), deleted_at=0, archived_at=0, merged_into=0, " +
                              "updated=$n, revision=revision+1 WHERE id=$id AND EXISTS(SELECT 1 FROM memory_versions WHERE memory_id=$id AND revision=$r)";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$r", revision); cmd.Parameters.AddWithValue("$n", Now);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"记忆 #{id} 没有版本 {revision}");
        }
        Snapshot(c, t, id, "restore", $"恢复到版本 {revision}");
        Event(c, t, id, "restore", $"revision={revision}");
        t.Commit();
    }

    /// <summary>永久删除（只能对回收站中的记忆操作）：清除正文和全部版本，只保留一条不含正文的审计事件</summary>
    public static void Purge(long id)
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = t;
            cmd.CommandText = "DELETE FROM memories WHERE id=$id AND deleted_at>0";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("只能永久删除回收站中的记忆");
        }
        Exec(c, t, "DELETE FROM memory_versions WHERE memory_id=$id", ("$id", id));
        Exec(c, t, "INSERT INTO memory_events(memory_id, event_type, actor_type, actor_display, detail, created_at) VALUES($id, 'purge', 'user', '用户', '永久删除（正文与版本已清除）', $n)",
            ("$id", id), ("$n", Now));
        t.Commit();
    }

    public static List<MemoryVersion> Versions(long id)
    {
        var list = new List<MemoryVersion>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT revision, content, tags, state, changed_at, actor_type, actor, action, reason FROM memory_versions WHERE memory_id=$id ORDER BY revision DESC LIMIT 200";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new MemoryVersion
            {
                Revision = r.GetInt64(0), Content = r.GetString(1), Tags = r.GetString(2), State = r.GetString(3), ChangedAt = ToLocal(r.GetDouble(4)),
                ActorType = r.GetString(5), Actor = r.GetString(6), Action = r.GetString(7), Reason = r.GetString(8),
            });
        return list;
    }

    /// <summary>给治理界面用：执行只读查询</summary>
    internal static T Query<T>(Func<SqliteConnection, T> f)
    {
        using var c = Open();
        return f(c);
    }
}
