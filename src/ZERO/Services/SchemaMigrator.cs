using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ZERO.Services;

/// <summary>
/// 数据库结构升级。表结构和策略默认值定义在嵌入资源 zero_schema.json 中，与 Python 服务（zero_gov.py）共用同一份，
/// 两端按同样的规则升级：先备份（&lt;库&gt;.bak-v旧版本，只备份一次），再在 BEGIN IMMEDIATE 事务中执行，失败整体回滚。
/// </summary>
public static class SchemaMigrator
{
    static readonly JsonDocument Doc = Load();

    static JsonDocument Load()
    {
        using var s = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("zero_schema.json")
                      ?? throw new InvalidOperationException("缺少内置的 zero_schema.json");
        return JsonDocument.Parse(s);
    }

    public static int TargetVersion(string which) => Doc.RootElement.GetProperty(which).GetProperty("version").GetInt32();

    /// <summary>策略默认值（governance.db settings 表中没有设置过的项）</summary>
    public static IReadOnlyDictionary<string, string> PolicyDefaults { get; } =
        Doc.RootElement.GetProperty("policy_defaults").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

    public static int Version(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    static void Exec(SqliteConnection c, string sql, SqliteTransaction? t = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>把 c 升级到最新版本；失败时抛出异常（调用方必须提示错误，不能当成空库继续使用）</summary>
    public static void Migrate(SqliteConnection c, string dbPath, string which)
    {
        var spec = Doc.RootElement.GetProperty(which);
        int target = spec.GetProperty("version").GetInt32();
        Exec(c, "PRAGMA journal_mode=WAL;");
        int v = Version(c);
        if (v >= target) return;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0)
            {
                var bak = $"{dbPath}.bak-v{v}";
                if (!File.Exists(bak))
                {
                    using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = bak, Pooling = false }.ToString());
                    dest.Open();
                    c.BackupDatabase(dest);
                    AppLog.Write("数据", $"{Path.GetFileName(dbPath)} 升级前已备份：{Path.GetFileName(bak)}");
                }
            }
        }
        using var t = c.BeginTransaction();      // Microsoft.Data.Sqlite：BEGIN IMMEDIATE
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = t;
            cmd.CommandText = "PRAGMA user_version";
            v = Convert.ToInt32(cmd.ExecuteScalar());      // 拿到写锁后再确认（MCP 服务可能刚升级完）
        }
        if (v < target)
        {
            foreach (var sql in spec.GetProperty("base").EnumerateArray()) Exec(c, sql.GetString()!, t);
            foreach (var step in spec.GetProperty("steps").EnumerateArray())
            {
                if (step.GetProperty("v").GetInt32() <= v) continue;
                foreach (var sql in step.GetProperty("sql").EnumerateArray())
                {
                    try { Exec(c, sql.GetString()!, t); }
                    catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
                }
            }
            Exec(c, $"PRAGMA user_version={target}", t);
        }
        t.Commit();
        if (v < target) AppLog.Write("数据", $"{Path.GetFileName(dbPath)} 已升级到 v{target}");
    }
}
