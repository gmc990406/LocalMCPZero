using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace ZERO.Services;

public sealed class GovTask
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public long Priority { get; init; }
    public string Scope { get; init; } = "";
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
    public string ClaimedBy { get; init; } = "";
    public DateTime? LeaseUntil { get; init; }
    public DateTime? Heartbeat { get; init; }
    public long Attempts { get; init; }
    public long MaxAttempts { get; init; }
    public string LastError { get; init; } = "";
    public string CreatedBy { get; init; } = "";
    public string Approval { get; init; } = "";
    public string Phase { get; init; } = "";
    public string Plan { get; init; } = "";
    public string Result { get; init; } = "";
    public string Payload { get; init; } = "";
    public long ActivityCalls { get; init; }

    public string KindText => Kind switch { "manual_plan" => "计划任务", "ui_validation" => "界面验证", "memory_review" => "记忆维护", _ => Kind };
    public string StateText => GovernanceStore.StateText(State);
    public string ScopeText => Scope switch { "full" => "全电脑访问", "restricted" => "有限制", "mixed" => "跨模式", _ => "所有模式" };
    public string ApprovalText => Approval switch { "pending" => "待批准", "approved" => "已批准", "rejected" => "已驳回", _ => "" };
    public string Meta => $"{KindText} · {StateText}{(ApprovalText.Length > 0 ? " · " + ApprovalText : "")} · {HistoryStore.When(Updated)}" +
                          (ClaimedBy.Length > 0 ? $" · {ClaimedBy}" : "");
    public string Detail
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"编号 {Id} · {KindText} · {ScopeText} · 优先级 {Priority}");
            sb.AppendLine($"状态：{StateText}{(ApprovalText.Length > 0 ? "（" + ApprovalText + "）" : "")}{(Phase.Length > 0 ? " · 阶段：" + Phase : "")}");
            sb.AppendLine($"创建：{Created:yyyy-MM-dd HH:mm:ss}（{(CreatedBy.Length > 0 ? CreatedBy : "系统")}） · 更新：{Updated:yyyy-MM-dd HH:mm:ss}");
            if (ClaimedBy.Length > 0) sb.AppendLine($"执行者：{ClaimedBy}{(Heartbeat is { } h ? $" · 最近心跳 {h:HH:mm:ss}" : "")}{(LeaseUntil is { } l ? $" · 租约到 {l:HH:mm:ss}" : "")}");
            sb.AppendLine($"尝试次数：{Attempts} / {MaxAttempts} · 服务端记录到的工具调用：{ActivityCalls} 次");
            if (LastError.Length > 0) sb.AppendLine($"最近错误：{LastError}");
            if (Plan.Length > 2 && Plan != "[]") sb.AppendLine($"计划：{Plan}");
            if (Result.Length > 2 && Result != "{}") sb.AppendLine($"结果：{Result}");
            return sb.ToString().TrimEnd();
        }
    }
    public bool CanCancel => State is not ("completed" or "cancelled");
    public bool CanRequeue => State is "failed" or "cancelled" or "deferred" || (State == "review_required" && Kind != "memory_review");
    public bool NeedsApproval => Approval == "pending";
}

public sealed class GovCandidate
{
    public long Id { get; init; }
    public List<long> MemoryIds { get; init; } = new();
    public string Category { get; init; } = "";
    public string Signals { get; init; } = "";
    public string Risk { get; init; } = "";
    public string Status { get; init; } = "";
    public string Scope { get; init; } = "";
    public string TaskId { get; init; } = "";
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
    public string CategoryText => GovernanceStore.CategoryText(Category);
    public string RiskText => Risk switch { "high" => "高风险", "medium" => "中风险", _ => "低风险" };
    public string StatusText => Status switch
    {
        "open" => "待复审", "review_required" => "待你审批", "resolved" => "已处理", "dismissed" => "已忽略", "stale" => "已失效", _ => Status,
    };
    public string Title => $"{CategoryText} · " + string.Join("、", MemoryIds.Select(i => "#" + i));
    public string Meta => $"{StatusText} · {RiskText} · {HistoryStore.When(Updated)}";
    public string Rule
    {
        get
        {
            try
            {
                using var d = JsonDocument.Parse(Signals);
                return d.RootElement.TryGetProperty("rule", out var r) ? r.GetString() ?? "" : Signals;
            }
            catch { return Signals; }
        }
    }
}

public sealed class GovDecision
{
    public long Id { get; init; }
    public long CandidateId { get; init; }
    public string TaskId { get; init; } = "";
    public string Action { get; init; } = "";
    public string Payload { get; init; } = "";
    public string Reason { get; init; } = "";
    public double Confidence { get; init; }
    public string Evidence { get; init; } = "";
    public string Impact { get; init; } = "";
    public string ActorType { get; init; } = "";
    public string Actor { get; init; } = "";
    public string Status { get; init; } = "";
    public string Risk { get; init; } = "";
    public DateTime Created { get; init; }
    public DateTime? Decided { get; init; }
    public string DecidedBy { get; init; } = "";
    public string Result { get; init; } = "";
    public string ActionText => GovernanceStore.ActionText(Action);
    public string StatusText => Status switch
    {
        "proposed" => "待审批", "applied" => "已生效", "rejected" => "已驳回", "conflict" => "版本冲突", "failed" => "失败", _ => Status,
    };
    public string Title => $"{ActionText} · 候选 {CandidateId}";
    public string Meta => $"{StatusText} · {(ActorType == "ai" ? "AI " + Actor : ActorType == "user" ? "用户" : Actor)} · {HistoryStore.When(Created)} · 置信度 {Confidence:0.##}";
    public string NewContent
    {
        get
        {
            try
            {
                using var d = JsonDocument.Parse(Payload);
                return d.RootElement.TryGetProperty("new_content", out var r) ? r.GetString() ?? "" : "";
            }
            catch { return ""; }
        }
    }
    public long TargetId
    {
        get
        {
            try
            {
                using var d = JsonDocument.Parse(Payload);
                return d.RootElement.TryGetProperty("target_id", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : 0;
            }
            catch { return 0; }
        }
    }
}

public sealed class GovEvent
{
    public DateTime Time { get; init; }
    public string Text { get; init; } = "";
    public string Actor { get; init; } = "";
    public long MemoryId { get; init; }
    public string ActionLabel { get; init; } = "";
    public long BeforeRevision { get; init; }
    public long AfterRevision { get; init; }
    public string TimeText => Time.ToString("MM-dd HH:mm:ss");
    public string MemoryRef => MemoryId > 0 ? $"#{MemoryId}" : "";
    public string VersionChange => AfterRevision > 0 ? $"{BeforeRevision} → {AfterRevision}" : "";
    public string Line => $"{TimeText}  {Actor}  {Text}";
}

public sealed record GovOverview(long Memories, long Archived, long Trash, long Pinned, long CandidatesOpen, long DecisionsPending,
    long TasksQueued, long TasksActive, long TasksReview, long TasksFailed, DateTime? LastScan, bool LastScanOk, string LastScanText);

/// <summary>治理库（%LOCALAPPDATA%\ZERO\governance.db）：任务队列、策略设置、窗口授权、操作日志；记忆候选与结论在 memory.db 中。
/// 简单的读取和设置在这里直接完成；涉及记忆内容变更的操作（扫描、应用结论、用户处理候选）调用 runtime\zero_gov.py，
/// 保证界面和 MCP 服务使用同一套规则。</summary>
public static class GovernanceStore
{
    public static string DbPath { get; } =
        Environment.GetEnvironmentVariable("ZERO_GOV_DB") is { Length: > 0 } p ? p : Path.Combine(AppSettings.DataDir, "governance.db");

    static bool _ready;

    public static string StateText(string s) => s switch
    {
        "draft" => "草稿", "queued" => "待领取", "leased" => "已领取", "running" => "进行中", "review_required" => "待审核",
        "completed" => "已完成", "deferred" => "已搁置", "failed" => "失败", "cancelled" => "已取消", _ => s,
    };
    public static string CategoryText(string c) => c switch
    {
        "duplicate" => "完全重复", "near_duplicate" => "近似重复", "redundant" => "内容冗余", "too_long" => "内容过长", "inactive" => "长期未被检索",
        "stale" => "长期未更新", "expired" => "已过期", "conflict" => "可能矛盾", _ => c,
    };
    public static string ActionText(string a) => a switch
    {
        "keep" => "保留", "merge" => "合并", "compress" => "压缩", "correct" => "更正", "lower_priority" => "降权", "archive" => "归档",
        "delete_proposal" => "删除到回收站", "needs_user_review" => "交给用户判断", _ => a,
    };

    static SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DbPath))!);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 10,
        }.ToString());
        c.Open();
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "PRAGMA busy_timeout=10000;"; cmd.ExecuteNonQuery(); }
        if (!_ready) { SchemaMigrator.Migrate(c, DbPath, "governance"); _ready = true; }
        return c;
    }

    static void Exec(SqliteConnection c, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection c, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    static DateTime? Ts(double v) => v > 0 ? MemoryStore.ToLocal(v) : null;

    /// <summary>确保数据库已创建并升级（启动时调用；失败抛出异常）</summary>
    public static void EnsureReady() { using var _ = Open(); }

    // ====================== 策略 ======================
    public static Dictionary<string, string> Policy()
    {
        var d = new Dictionary<string, string>(SchemaMigrator.PolicyDefaults);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings";
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public static string Get(string key) => Policy().TryGetValue(key, out var v) ? v : "";
    public static int GetInt(string key) => int.TryParse(Get(key), out var v) ? v : int.TryParse(SchemaMigrator.PolicyDefaults.GetValueOrDefault(key), out var d) ? d : 0;
    public static bool GetBool(string key) => Get(key) is "1" or "true";

    public static void Set(string key, string value)
    {
        using var c = Open();
        Exec(c, "INSERT INTO settings(key, value, updated) VALUES($k, $v, $n) ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated=excluded.updated",
            ("$k", key), ("$v", value), ("$n", Now));
    }

    public static void ResetPolicy()
    {
        using var c = Open();
        Exec(c, "DELETE FROM settings WHERE key NOT IN ('review.last_scan')");
    }

    // ====================== 概览 ======================
    public static GovOverview Overview()
    {
        long Mem(string sql) => MemoryStore.Query(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); });
        using var g = Open();
        DateTime? last = null; bool ok = true; string text = "还没有运行过";
        using (var cmd = g.CreateCommand())
        {
            cmd.CommandText = "SELECT started, ok, stats_json, error, trigger FROM scan_runs ORDER BY id DESC LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                last = Ts(r.GetDouble(0)); ok = r.GetInt64(1) == 1;
                if (!ok) text = "失败：" + r.GetString(3);
                else
                {
                    try
                    {
                        using var d = JsonDocument.Parse(r.GetString(2));
                        var e = d.RootElement;
                        text = $"扫描 {e.GetProperty("memories").GetInt32()} 条，新候选 {e.GetProperty("new_candidates").GetInt32()} 个，" +
                               $"重新打开 {e.GetProperty("reopened").GetInt32()} 个，已有未处理 {e.GetProperty("skipped_existing").GetInt32()} 个";
                    }
                    catch { text = "完成"; }
                }
                text += r.GetString(4) switch { "schedule" => "（定时）", "manual" => "（手动）", _ => "" };
            }
        }
        return new GovOverview(
            Mem("SELECT COUNT(*) FROM memories WHERE deleted_at=0 AND archived_at=0"), Mem("SELECT COUNT(*) FROM memories WHERE deleted_at=0 AND archived_at>0"),
            Mem("SELECT COUNT(*) FROM memories WHERE deleted_at>0"), Mem("SELECT COUNT(*) FROM memories WHERE deleted_at=0 AND pinned=1"),
            Mem("SELECT COUNT(*) FROM memory_review_candidates WHERE status='open'"), Mem("SELECT COUNT(*) FROM memory_review_decisions WHERE status='proposed'"),
            Scalar(g, "SELECT COUNT(*) FROM governance_tasks WHERE state='queued'"), Scalar(g, "SELECT COUNT(*) FROM governance_tasks WHERE state IN ('leased','running')"),
            Scalar(g, "SELECT COUNT(*) FROM governance_tasks WHERE state='review_required' OR approval='pending'"),
            Scalar(g, "SELECT COUNT(*) FROM governance_tasks WHERE state='failed'"), last, ok, text);
    }

    public static DateTime? LastScan()
    {
        var v = Get("review.last_scan");
        return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? Ts(d) : null;
    }

    // ====================== 任务 ======================
    public static List<GovTask> Tasks(string filter)
    {
        var where = filter switch
        {
            "active" => "state IN ('queued','leased','running','review_required','draft') OR approval='pending'",
            "attention" => "state IN ('review_required','failed','deferred') OR approval='pending'",
            "done" => "state IN ('completed','cancelled')",
            _ => "1=1",
        };
        var list = new List<GovTask>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, kind, title, state, priority, scope, created_at, updated_at, claimed_by, lease_until, heartbeat_at, attempts, max_attempts, " +
                          "last_error, created_by, approval, phase, plan_json, result_json, payload_json, activity_calls FROM governance_tasks WHERE " + where +
                          " ORDER BY CASE state WHEN 'running' THEN 0 WHEN 'leased' THEN 1 WHEN 'review_required' THEN 2 WHEN 'queued' THEN 3 ELSE 4 END, updated_at DESC LIMIT 500";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new GovTask
            {
                Id = r.GetString(0), Kind = r.GetString(1), Title = r.GetString(2), State = r.GetString(3), Priority = r.GetInt64(4), Scope = r.GetString(5),
                Created = MemoryStore.ToLocal(r.GetDouble(6)), Updated = MemoryStore.ToLocal(r.GetDouble(7)), ClaimedBy = r.GetString(8),
                LeaseUntil = Ts(r.GetDouble(9)), Heartbeat = Ts(r.GetDouble(10)), Attempts = r.GetInt64(11), MaxAttempts = r.GetInt64(12), LastError = r.GetString(13),
                CreatedBy = r.GetString(14), Approval = r.GetString(15), Phase = r.GetString(16), Plan = r.GetString(17), Result = r.GetString(18),
                Payload = r.GetString(19), ActivityCalls = r.GetInt64(20),
            });
        return list;
    }

    public static List<GovEvent> TaskEvents(string id)
    {
        var list = new List<GovEvent>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, event, actor, detail FROM task_events WHERE task_id=$id ORDER BY id DESC LIMIT 100";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new GovEvent { Time = MemoryStore.ToLocal(r.GetDouble(0)), Actor = r.GetString(2), Text = r.GetString(1) + (r.GetString(3).Length > 0 ? "：" + r.GetString(3) : "") });
        return list;
    }

    // ====================== 记忆候选 / 结论 / 审计 ======================
    public static List<GovCandidate> Candidates(string filter) => MemoryStore.Query(c =>
    {
        var where = filter switch
        {
            "open" => "status IN ('open','review_required')",
            "closed" => "status IN ('resolved','dismissed','stale')",
            _ => "1=1",
        };
        var list = new List<GovCandidate>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT candidate_id, memory_ids_json, category, signals_json, risk_level, status, scope, task_id, created_at, updated_at " +
                          "FROM memory_review_candidates WHERE " + where + " ORDER BY CASE status WHEN 'review_required' THEN 0 WHEN 'open' THEN 1 ELSE 2 END, " +
                          "CASE risk_level WHEN 'high' THEN 0 WHEN 'medium' THEN 1 ELSE 2 END, updated_at DESC LIMIT 500";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            List<long> ids;
            try { ids = JsonSerializer.Deserialize<List<long>>(r.GetString(1)) ?? new(); } catch { ids = new(); }
            list.Add(new GovCandidate
            {
                Id = r.GetInt64(0), MemoryIds = ids, Category = r.GetString(2), Signals = r.GetString(3), Risk = r.GetString(4), Status = r.GetString(5),
                Scope = r.GetString(6), TaskId = r.GetString(7), Created = MemoryStore.ToLocal(r.GetDouble(8)), Updated = MemoryStore.ToLocal(r.GetDouble(9)),
            });
        }
        return list;
    });

    public static List<GovDecision> Decisions(string filter, long candidateId = 0) => MemoryStore.Query(c =>
    {
        var where = candidateId > 0 ? "candidate_id=$c" : filter == "pending" ? "status='proposed'" : "1=1";
        var list = new List<GovDecision>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT decision_id, candidate_id, task_id, action, payload_json, reason, confidence, evidence, impact, actor_type, actor_display, status, " +
                          "risk_level, created_at, decided_at, decided_by, result_json FROM memory_review_decisions WHERE " + where + " ORDER BY decision_id DESC LIMIT 500";
        cmd.Parameters.AddWithValue("$c", candidateId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new GovDecision
            {
                Id = r.GetInt64(0), CandidateId = r.GetInt64(1), TaskId = r.GetString(2), Action = r.GetString(3), Payload = r.GetString(4), Reason = r.GetString(5),
                Confidence = r.GetDouble(6), Evidence = r.GetString(7), Impact = r.GetString(8), ActorType = r.GetString(9), Actor = r.GetString(10),
                Status = r.GetString(11), Risk = r.GetString(12), Created = MemoryStore.ToLocal(r.GetDouble(13)), Decided = Ts(r.GetDouble(14)),
                DecidedBy = r.GetString(15), Result = r.GetString(16),
            });
        return list;
    });

    public static List<GovEvent> MemoryEvents(int limit = 300) => MemoryStore.Query(c =>
    {
        var list = new List<GovEvent>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT created_at, memory_id, event_type, actor_type, actor_display, before_revision, after_revision, detail FROM memory_events ORDER BY event_id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var type = r.GetString(2);
            var t = new MemoryVersion { Action = type }.ActionText;
            if (type == "review_keep") t = "复审：保留"; else if (type == "merge_target") t = "合并（保留）"; else if (type == "purge") t = "永久删除";
            long memId = r.GetInt64(1);
            long before = r.GetInt64(5), after = r.GetInt64(6);
            list.Add(new GovEvent
            {
                Time = MemoryStore.ToLocal(r.GetDouble(0)),
                Actor = r.GetString(3) == "user" ? "用户" : r.GetString(3) == "ai" ? "AI " + r.GetString(4) : r.GetString(4),
                MemoryId = memId,
                ActionLabel = t,
                BeforeRevision = before,
                AfterRevision = after,
                Text = $"#{memId} {t}" + (after > 0 ? $"（版本 {before} → {after}）" : "") + (r.GetString(7).Length > 0 ? "：" + r.GetString(7) : ""),
            });
        }
        return list;
    });

    // ====================== 调用 zero_gov.py（与 MCP 服务共用规则） ======================
    public sealed record CliResult(bool Ok, string Message, JsonElement? Data);

    public static async Task<CliResult> RunCliAsync(params string[] args)
    {
        var env = new EnvironmentSetup();
        if (!File.Exists(env.PythonExe)) return new(false, "Python 运行环境尚未就绪：请先在主界面启动一次 ZERO（会自动安装）", null);
        try { env.WriteScripts(); } catch (Exception ex) { AppLog.Error("治理", ex); }
        var script = Path.Combine(env.RuntimeDir, "zero_gov.py");
        var psi = new ProcessStartInfo(env.PythonExe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = env.RuntimeDir,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        foreach (var a in new[] { "-u", script, "--memory-db", Path.GetFullPath(MemoryStore.DbPath), "--gov-db", Path.GetFullPath(DbPath) }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => p.WaitForExit(120_000)))
        {
            try { p.Kill(true); } catch { }
            return new(false, "处理超时（120 秒）", null);
        }
        var stdout = (await outTask).Trim();
        var stderr = (await errTask).Trim();
        if (stderr.Length > 0) AppLog.Write("治理", stderr);
        try
        {
            using var d = JsonDocument.Parse(stdout.Length > 0 ? stdout : "{}");
            var root = d.RootElement.Clone();
            bool ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return new(ok, msg.Length > 0 ? msg : ok ? "完成" : (stderr.Length > 0 ? stderr : "失败"), root);
        }
        catch { return new(false, stderr.Length > 0 ? stderr : stdout, null); }
    }

    public static Task<CliResult> ScanAsync(string trigger) => RunCliAsync("scan", "--trigger", trigger);
    public static Task<CliResult> ApplyAsync(long decision) => RunCliAsync("apply", "--decision", decision.ToString());
    public static Task<CliResult> RejectAsync(long decision, string reason) => RunCliAsync("reject", "--decision", decision.ToString(), "--reason", reason);
    public static Task<CliResult> TaskActionAsync(string id, string action) => RunCliAsync("task", "--id", id, "--action", action);

    public static async Task<CliResult> UserDecideAsync(long candidate, string action, long target = 0, string? content = null, string reason = "")
    {
        var args = new List<string> { "user-decide", "--candidate", candidate.ToString(), "--action", action, "--target", target.ToString(), "--reason", reason };
        string? tmp = null;
        if (content != null)
        {
            tmp = Path.Combine(Path.GetTempPath(), $"zero-content-{Guid.NewGuid():N}.txt");
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            args.Add("--content-file"); args.Add(tmp);
        }
        try { return await RunCliAsync(args.ToArray()); }
        finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
    }

    // ====================== 视觉工作台：授权、请求、锁、操作日志、紧急停止 ======================
    public sealed record UiGrant(long Id, string Mode, long Hwnd, long Pid, string Process, string Title, bool AllowControl, DateTime Created, DateTime Expires, bool Revoked)
    {
        public bool Active => !Revoked && Expires > DateTime.Now;
        public string Line => $"{(Active ? "有效" : Revoked ? "已撤销" : "已过期")} · {Process} (PID {Pid}) · {Title} · " +
                              $"{(Mode == "full" ? "全电脑访问" : "有限制")} · 到 {Expires:HH:mm}";
    }
    public sealed record UiRequest(long Id, string Mode, long Hwnd, long Pid, string Process, string Title, string Reason, string Client, DateTime Created, string Status)
    {
        public string Line => $"{Created:HH:mm:ss} · {Client} 请求操作 {Process} (PID {Pid})「{Title}」" + (Reason.Length > 0 ? $"：{Reason}" : "");
    }

    public static List<UiGrant> Grants(bool activeOnly)
    {
        var list = new List<UiGrant>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, mode, hwnd, pid, process, title, allow_control, created_at, expires_at, revoked_at FROM ui_grants" +
                          (activeOnly ? " WHERE revoked_at=0 AND expires_at>$n" : "") + " ORDER BY id DESC LIMIT 100";
        cmd.Parameters.AddWithValue("$n", Now);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UiGrant(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetInt64(6) != 0,
                MemoryStore.ToLocal(r.GetDouble(7)), MemoryStore.ToLocal(r.GetDouble(8)), r.GetDouble(9) > 0));
        return list;
    }

    public static void AddGrant(string mode, long hwnd, long pid, string process, string title, int minutes)
    {
        using var c = Open();
        Exec(c, "INSERT INTO ui_grants(mode, hwnd, pid, process, title, allow_control, created_at, expires_at, note) VALUES($m, $h, $p, $pr, $t, 1, $n, $e, '用户在视觉工作台授权')",
            ("$m", mode), ("$h", hwnd), ("$p", pid), ("$pr", process), ("$t", title), ("$n", Now), ("$e", Now + minutes * 60));
    }

    public static void RevokeGrant(long id)
    {
        using var c = Open();
        Exec(c, "UPDATE ui_grants SET revoked_at=$n WHERE id=$id", ("$n", Now), ("$id", id));
    }

    public static List<UiRequest> Requests()
    {
        var list = new List<UiRequest>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, mode, hwnd, pid, process, title, reason, client, created_at, status FROM ui_requests WHERE status='pending' AND created_at>$n ORDER BY id DESC LIMIT 50";
        cmd.Parameters.AddWithValue("$n", Now - 3600);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UiRequest(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                MemoryStore.ToLocal(r.GetDouble(8)), r.GetString(9)));
        return list;
    }

    public static void DecideRequest(UiRequest req, bool approve, int minutes)
    {
        using var c = Open();
        Exec(c, "UPDATE ui_requests SET status=$s, decided_at=$n WHERE id=$id", ("$s", approve ? "approved" : "denied"), ("$n", Now), ("$id", req.Id));
        if (approve)
            Exec(c, "INSERT INTO ui_grants(mode, hwnd, pid, process, title, allow_control, created_at, expires_at, note) VALUES($m, $h, $p, $pr, $t, 1, $n, $e, $note)",
                ("$m", req.Mode), ("$h", req.Hwnd), ("$p", req.Pid), ("$pr", req.Process), ("$t", req.Title), ("$n", Now), ("$e", Now + minutes * 60),
                ("$note", $"批准了 {req.Client} 的请求"));
    }

    public static List<GovEvent> UiActions(int limit = 200)
    {
        var list = new List<GovEvent>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, client, tool, target, action, ok, detail FROM ui_actions ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new GovEvent
            {
                Time = MemoryStore.ToLocal(r.GetDouble(0)), Actor = r.GetString(1),
                Text = $"{(r.GetInt64(5) == 1 ? "✓" : "✗")} {r.GetString(2)} {r.GetString(4)} {r.GetString(3)}" + (r.GetString(6).Length > 0 ? " · " + r.GetString(6) : ""),
            });
        return list;
    }

    /// <summary>紧急停止：立即拒绝所有界面/网页操作，撤销全部窗口授权并释放操作锁（MCP 服务每次操作前都会检查）</summary>
    public static void EmergencyStop(bool stop)
    {
        Set("ui.stop", stop ? "1" : "0");
        if (!stop) return;
        using var c = Open();
        Exec(c, "UPDATE ui_grants SET revoked_at=$n WHERE revoked_at=0", ("$n", Now));
        Exec(c, "DELETE FROM ui_locks");
        Exec(c, "UPDATE ui_requests SET status='denied', decided_at=$n WHERE status='pending'", ("$n", Now));
    }
}
