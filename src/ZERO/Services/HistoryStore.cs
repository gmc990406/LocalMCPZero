using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace ZERO.Services;

public sealed class ConversationItem
{
    public long Id { get; init; }
    public string Mode { get; init; } = "";
    public string Client { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public DateTime Started { get; init; }
    public DateTime Updated { get; init; }
    public long Calls { get; init; }
    public bool Pinned { get; init; }
    /// <summary>分组来源：session=MCP 会话，activity=按时间推断，explicit=AI 新建，manual=手动拆分，legacy=旧版（推断）</summary>
    public string Kind { get; init; } = "legacy";

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "未命名对话" : Title;
    public bool Untitled => string.IsNullOrWhiteSpace(Title);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool Inferred => Kind is "activity" or "legacy";
    public string KindText => Kind switch
    {
        "chat" => "按聊天", "session" => "MCP 会话", "activity" => "按时间推断", "explicit" => "AI 新建", "manual" => "手动拆分", _ => "旧版分组（推断）",
    };
    public string Meta => $"{Client} · {HistoryStore.When(Updated)} · {(Calls > 0 ? $"{Calls} 次操作" : "无操作")}{(Inferred ? " · 推断" : "")}";
    public string ModeText => Mode == "full" ? "全电脑访问" : "有限制";
    public string Range => Started.Date == Updated.Date
        ? $"{Started:yyyy-MM-dd HH:mm} – {Updated:HH:mm}"
        : $"{Started:yyyy-MM-dd HH:mm} – {Updated:yyyy-MM-dd HH:mm}";
}

public sealed class ToolItem
{
    public string Tool { get; init; } = "";
    public long Count { get; init; }
    public long Failed { get; init; }
    public DateTime Last { get; init; }
    public string Meta => $"{Count} 次{(Failed > 0 ? $" · {Failed} 次失败" : "")} · 最近 {HistoryStore.When(Last)}";
}

public sealed class CallItem
{
    public long Id { get; init; }
    public long ConvId { get; init; }
    public string ConvTitle { get; init; } = "";
    public string Client { get; init; } = "";
    public DateTime Time { get; init; }
    public string Tool { get; init; } = "";
    public string Args { get; init; } = "";
    public string Result { get; init; } = "";
    public bool Ok { get; init; }
    public long Ms { get; init; }
    public string Note { get; init; } = "";

    public string TimeText => Time.ToString("MM-dd HH:mm:ss");
    /// <summary>列表里显示的参数摘要：只取参数值（不显示 JSON 转义），无参数时显示「无参数」</summary>
    public string Preview
    {
        get
        {
            string a;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(Args);
                var vals = new List<string>();
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v && v != ".")
                        vals.Add(v);
                a = vals.Count > 0 ? string.Join("  ·  ", vals) : (doc.RootElement.EnumerateObject().Any() ? Args : "无参数");
            }
            catch { a = Args; }
            a = a.Replace("\r", " ").Replace("\n", " ");
            return a.Length > 160 ? a[..160] + "…" : a;
        }
    }
    public string Context => string.IsNullOrWhiteSpace(ConvTitle) ? $"#{ConvId} · {Client}" : $"#{ConvId} {ConvTitle} · {Client}";
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}

/// <summary>历史记录数据库（%LOCALAPPDATA%\ZERO\history.db）。MCP 服务写入调用记录，界面负责浏览、编辑和清理。</summary>
public static class HistoryStore
{
    public static string DbPath { get; } =
        Environment.GetEnvironmentVariable("ZERO_HISTORY_DB") is { Length: > 0 } p ? p : Path.Combine(AppSettings.DataDir, "history.db");

    const int SchemaVersion = 2;
    // 与 server.py 中的 SCHEMA / MIGRATE_V2 保持一致
    const string Schema = """
        CREATE TABLE IF NOT EXISTS conversations(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          mode TEXT NOT NULL, client TEXT NOT NULL, client_key TEXT NOT NULL,
          title TEXT NOT NULL DEFAULT '', summary TEXT NOT NULL DEFAULT '',
          started REAL NOT NULL, updated REAL NOT NULL,
          calls INTEGER NOT NULL DEFAULT 0, pinned INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS calls(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          conv_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
          ts REAL NOT NULL, tool TEXT NOT NULL, args TEXT NOT NULL, result TEXT NOT NULL,
          ok INTEGER NOT NULL, ms INTEGER NOT NULL, note TEXT NOT NULL DEFAULT '');
        CREATE INDEX IF NOT EXISTS ix_calls_conv ON calls(conv_id, ts);
        CREATE INDEX IF NOT EXISTS ix_calls_tool ON calls(tool, ts);
        CREATE INDEX IF NOT EXISTS ix_conv_updated ON conversations(updated);
        """;
    static readonly string[] MigrateV2 =
    {
        "ALTER TABLE conversations ADD COLUMN kind TEXT NOT NULL DEFAULT 'legacy'",
        "ALTER TABLE conversations ADD COLUMN session_id TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE conversations ADD COLUMN client_ua TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE conversations ADD COLUMN upstream_id TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE calls ADD COLUMN session_id TEXT NOT NULL DEFAULT ''",
    };

    static bool _ready;

    static SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DbPath))!);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5,
        }.ToString());
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        if (!_ready)
        {
            Exec(c, "PRAGMA journal_mode=WAL;");
            Migrate(c);
            _ready = true;
        }
        return c;
    }

    static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>升级数据库结构：先把旧库完整备份为 history.db.bak-v1（只备份一次），只加列、不删数据。</summary>
    static void Migrate(SqliteConnection c)
    {
        if (Scalar(c, "PRAGMA user_version") >= SchemaVersion) return;
        var old = Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE name='conversations'") > 0;
        var hasKind = old && Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('conversations') WHERE name='kind'") > 0;
        var bak = DbPath + ".bak-v1";
        if (old && !hasKind && !File.Exists(bak))
        {
            using var b = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = bak, Pooling = false }.ToString());
            b.Open();
            c.BackupDatabase(b);
            AppLog.Write("历史", $"历史库升级前已备份：{bak}");
        }
        Exec(c, Schema);
        foreach (var sql in MigrateV2)
        {
            try { Exec(c, sql); }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
        }
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_conv_session ON conversations(session_id);");
        Exec(c, $"PRAGMA user_version={SchemaVersion};");
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

    public static string When(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} 分钟前";
        if (t.Date == DateTime.Today) return t.ToString("HH:mm");
        if (t.Date == DateTime.Today.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
        return t.Year == DateTime.Now.Year ? t.ToString("MM-dd HH:mm") : t.ToString("yyyy-MM-dd");
    }

    const string SearchCond = "(v.title LIKE $q OR v.summary LIKE $q OR v.client LIKE $q OR EXISTS(SELECT 1 FROM calls k WHERE k.conv_id=v.id AND (k.tool LIKE $q OR k.args LIKE $q OR k.result LIKE $q OR k.note LIKE $q)))";

    public static List<ConversationItem> Conversations(string query)
    {
        var list = new List<ConversationItem>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, mode, client, title, summary, started, updated, calls, pinned, kind FROM conversations v" +
                          (string.IsNullOrWhiteSpace(query) ? "" : " WHERE " + SearchCond) +
                          " ORDER BY pinned DESC, updated DESC LIMIT 500";
        cmd.Parameters.AddWithValue("$q", $"%{query.Trim()}%");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ConversationItem
            {
                Id = r.GetInt64(0), Mode = r.GetString(1), Client = r.GetString(2), Title = r.GetString(3), Summary = r.GetString(4),
                Started = ToLocal(r.GetDouble(5)), Updated = ToLocal(r.GetDouble(6)), Calls = r.GetInt64(7), Pinned = r.GetInt64(8) != 0,
                Kind = r.GetString(9),
            });
        return list;
    }

    public static List<ToolItem> Tools(string query)
    {
        var list = new List<ToolItem>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT tool, COUNT(*), SUM(ok=0), MAX(ts) FROM calls k" +
                          (string.IsNullOrWhiteSpace(query) ? "" : " WHERE k.tool LIKE $q OR k.args LIKE $q OR k.result LIKE $q OR k.note LIKE $q") +
                          " GROUP BY tool ORDER BY MAX(ts) DESC";
        cmd.Parameters.AddWithValue("$q", $"%{query.Trim()}%");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ToolItem { Tool = r.GetString(0), Count = r.GetInt64(1), Failed = r.GetInt64(2), Last = ToLocal(r.GetDouble(3)) });
        return list;
    }

    /// <summary>按对话（convId）或按工具（tool）查询调用记录；按工具时可用 query 过滤。</summary>
    public static List<CallItem> Calls(long? convId, string? tool, string query = "")
    {
        var list = new List<CallItem>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = convId != null ? "k.conv_id=$c" : "k.tool=$t";
        if (convId == null && !string.IsNullOrWhiteSpace(query)) where += " AND (k.args LIKE $q OR k.result LIKE $q OR k.note LIKE $q)";
        cmd.CommandText = "SELECT k.id, k.conv_id, v.title, v.client, k.ts, k.tool, k.args, k.result, k.ok, k.ms, k.note " +
                          $"FROM calls k JOIN conversations v ON v.id=k.conv_id WHERE {where} " +
                          (convId != null ? "ORDER BY k.ts ASC" : "ORDER BY k.ts DESC") + " LIMIT 2000";
        cmd.Parameters.AddWithValue("$c", convId ?? 0);
        cmd.Parameters.AddWithValue("$t", tool ?? "");
        cmd.Parameters.AddWithValue("$q", $"%{query.Trim()}%");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new CallItem
            {
                Id = r.GetInt64(0), ConvId = r.GetInt64(1), ConvTitle = r.GetString(2), Client = r.GetString(3), Time = ToLocal(r.GetDouble(4)),
                Tool = r.GetString(5), Args = r.GetString(6), Result = r.GetString(7), Ok = r.GetInt64(8) != 0, Ms = r.GetInt64(9), Note = r.GetString(10),
            });
        return list;
    }

    public static void UpdateConversation(long id, string title, string summary)
    {
        using var c = Open();
        Exec(c, "UPDATE conversations SET title=$t, summary=$s WHERE id=$id", ("$t", title.Trim()), ("$s", summary.Trim()), ("$id", id));
    }

    public static void SetPinned(long id, bool pinned)
    {
        using var c = Open();
        Exec(c, "UPDATE conversations SET pinned=$p WHERE id=$id", ("$p", pinned ? 1 : 0), ("$id", id));
    }

    public static void UpdateNote(long callId, string note)
    {
        using var c = Open();
        Exec(c, "UPDATE calls SET note=$n WHERE id=$id", ("$n", note.Trim()), ("$id", callId));
    }

    public static void DeleteConversation(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM conversations WHERE id=$id", ("$id", id));
    }

    public static void DeleteCall(long callId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "UPDATE conversations SET calls=MAX(calls-1,0) WHERE id=(SELECT conv_id FROM calls WHERE id=$id)", ("$id", callId));
        Exec(c, "DELETE FROM calls WHERE id=$id", ("$id", callId));
        tx.Commit();
    }

    /// <summary>把某条记录及其之后的记录拆分为一个新对话，返回新对话编号。</summary>
    public static long SplitFrom(long callId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        long newId;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO conversations(mode, client, client_key, title, summary, started, updated, calls, pinned, kind, client_ua)
                SELECT v.mode, v.client, v.client_key, '', '', k.ts, v.updated, 0, 0, 'manual', v.client_ua
                FROM calls k JOIN conversations v ON v.id=k.conv_id WHERE k.id=$id;
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$id", callId);
            newId = (long)(cmd.ExecuteScalar() ?? 0L);
        }
        Exec(c, """
            UPDATE calls SET conv_id=$n WHERE conv_id=(SELECT conv_id FROM calls WHERE id=$id)
              AND (ts > (SELECT ts FROM calls WHERE id=$id) OR (ts = (SELECT ts FROM calls WHERE id=$id) AND id >= $id));
            """, ("$n", newId), ("$id", callId));
        RecountAll(c);
        tx.Commit();
        return newId;
    }

    /// <summary>同一客户端、同一访问模式下时间上紧挨着的上一组（用于合并被误拆开的记录）。</summary>
    public static ConversationItem? Previous(ConversationItem conv)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, title FROM conversations WHERE mode=$m AND client=$cl AND id!=$id AND started<=$s ORDER BY started DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$m", conv.Mode);
        cmd.Parameters.AddWithValue("$cl", conv.Client);
        cmd.Parameters.AddWithValue("$id", conv.Id);
        cmd.Parameters.AddWithValue("$s", conv.Started.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ConversationItem { Id = r.GetInt64(0), Title = r.GetString(1) } : null;
    }

    /// <summary>把 from 组的记录并入 into 组：标题/摘要为空时沿用 from 的，会话标识一并转移，然后删除 from 组。</summary>
    public static void Merge(long from, long into)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "UPDATE calls SET conv_id=$i WHERE conv_id=$f", ("$i", into), ("$f", from));
        Exec(c, """
            UPDATE conversations SET
              title=CASE WHEN title='' THEN (SELECT title FROM conversations WHERE id=$f) ELSE title END,
              summary=CASE WHEN summary='' THEN (SELECT summary FROM conversations WHERE id=$f)
                           WHEN (SELECT summary FROM conversations WHERE id=$f)='' THEN summary
                           ELSE summary || char(10) || (SELECT summary FROM conversations WHERE id=$f) END,
              session_id=CASE WHEN (SELECT session_id FROM conversations WHERE id=$f)!='' THEN (SELECT session_id FROM conversations WHERE id=$f) ELSE session_id END,
              pinned=MAX(pinned, (SELECT pinned FROM conversations WHERE id=$f)),
              started=MIN(started, (SELECT started FROM conversations WHERE id=$f))
            WHERE id=$i;
            """, ("$i", into), ("$f", from));
        Exec(c, "DELETE FROM conversations WHERE id=$f", ("$f", from));
        RecountAll(c);
        tx.Commit();
    }

    static void RecountAll(SqliteConnection c) => Exec(c, """
        UPDATE conversations SET calls=(SELECT COUNT(*) FROM calls k WHERE k.conv_id=conversations.id),
          updated=COALESCE((SELECT MAX(ts) FROM calls k WHERE k.conv_id=conversations.id), updated);
        """);

    /// <summary>删除超过保留天数且未置顶的对话。days ≤ 0 表示永久保留。返回删除的对话数。</summary>
    public static int Cleanup(int days)
    {
        if (days <= 0 || !File.Exists(DbPath)) return 0;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE pinned=0 AND updated < $t";
        cmd.Parameters.AddWithValue("$t", Now - days * 86400.0);
        var n = cmd.ExecuteNonQuery();
        // 只建立了分组、却没有任何操作和标题的空组，一天后清理
        Exec(c, "DELETE FROM conversations WHERE calls=0 AND title='' AND summary='' AND pinned=0 AND updated < $t", ("$t", Now - 86400.0));
        return n;
    }

    public static void ClearAll(bool keepPinned)
    {
        using var c = Open();
        Exec(c, keepPinned ? "DELETE FROM conversations WHERE pinned=0" : "DELETE FROM conversations");
        Exec(c, "DELETE FROM calls WHERE conv_id NOT IN (SELECT id FROM conversations)");
        Exec(c, "VACUUM");
    }
}
