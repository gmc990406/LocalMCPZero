using System;
using System.Collections.Generic;
using System.Linq;

namespace ZERO.Services;

/// <summary>一项 AI 权限：对应 server.py 中 PERM_TOOLS 的一个键，控制一组 MCP 工具是否提供给 AI。</summary>
public sealed record PermissionInfo(
    string Key, string Group, string Title, string Description, string Tools,
    bool Risky,          // 有风险：开关显示为红色
    bool FullOnly,       // 只在全电脑访问模式下可用
    bool RestrictedDefault);

public static class Permissions
{
    public static readonly PermissionInfo[] All =
    {
        new("file_read", "文件", "读取文件", "浏览文件夹、读取和搜索文件内容", "list_files · read_file · search_files", false, false, true),
        new("file_write", "文件", "修改文件", "创建、覆盖和编辑文件，新建文件夹", "write_file · edit_file · make_dir", true, false, true),
        new("system", "电脑", "查看系统信息", "系统版本、CPU、内存、磁盘、IP 地址和正在运行的进程（只读）", "system_info · list_processes", false, false, false),
        new("screenshot", "电脑", "截取屏幕", "截取桌面画面，AI 能看到屏幕上显示的所有内容", "screenshot", true, false, false),
        new("command", "命令", "运行命令", "用 PowerShell 执行命令，例如运行 Python 脚本、安装依赖、编译项目，权限等同当前 Windows 账户", "run_command", true, true, false),
        new("jobs", "命令", "后台任务", "在后台运行耗时的脚本或开发服务器，查看输出、停止任务", "start_job · job_status · list_jobs · stop_job", true, true, false),
        new("history", "记忆与记录", "查看操作记录", "查看 AI 在这台电脑上的历史操作（只能看到同一访问模式下的记录）", "history_conversations · history_calls", false, false, true),
        new("memory_read", "记忆与记录", "搜索记忆", "搜索记忆库；连接时会把置顶的记忆直接告诉 AI", "memory_search", false, false, true),
        new("memory_write", "记忆与记录", "保存和修改记忆", "保存新的记忆，修改或删除已有记忆", "memory_save · memory_update · memory_delete", false, false, true),
    };

    public static PermissionInfo Get(string key) => All.First(p => p.Key == key);

    /// <summary>未单独设置过时的默认值（兼容旧版的“允许 AI 查看历史 / 保存记忆”开关）</summary>
    public static bool Default(AppSettings s, bool full, string key)
    {
        var p = Get(key);
        if (!full && p.FullOnly) return false;
        return key switch
        {
            "history" => s.HistoryShare,
            "memory_write" => s.MemoryWrite,
            _ => full || p.RestrictedDefault,
        };
    }

    static Dictionary<string, bool> Map(AppSettings s, bool full) =>
        full ? (s.PermsFull ??= new()) : (s.PermsRestricted ??= new());

    public static bool IsAllowed(AppSettings s, bool full, string key)
    {
        if (!full && Get(key).FullOnly) return false;
        return Map(s, full).TryGetValue(key, out var v) ? v : Default(s, full, key);
    }

    public static void Set(AppSettings s, bool full, string key, bool value) => Map(s, full)[key] = value;

    public static void Reset(AppSettings s, bool full) => Map(s, full).Clear();

    /// <summary>实际生效的权限（考虑依赖关系：写入记忆需要先能搜索记忆）</summary>
    public static bool IsEffective(AppSettings s, bool full, string key) =>
        IsAllowed(s, full, key) && (key != "memory_write" || IsAllowed(s, full, "memory_read"));

    /// <summary>传给 server.py 的 MCP_PERMS</summary>
    public static string ToEnv(AppSettings s, bool full) =>
        string.Join(",", All.Where(p => IsEffective(s, full, p.Key)).Select(p => p.Key));

    /// <summary>用于比较“运行中的服务”和“当前设置”是否一致</summary>
    public static string Signature(AppSettings s, bool full) =>
        ToEnv(s, full) + "|" + s.HistoryEnabled + s.MemoryEnabled;

    public static (int on, int total) Count(AppSettings s, bool full)
    {
        var list = All.Where(p => full || !p.FullOnly).ToList();
        return (list.Count(p => IsEffective(s, full, p.Key)), list.Count);
    }
}
