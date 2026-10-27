using System;
using System.Collections.Generic;
using System.Linq;

namespace ZERO.Services;

/// <summary>一项 AI 权限：对应 server.py 中 PERM_TOOLS 的一个键，控制一组 MCP 工具是否提供给 AI。</summary>
public sealed record PermissionInfo(
    string Key, string Group, string Title, string Description, string Tools,
    bool Risky,          // 有风险：开关显示为红色
    bool FullOnly,       // 只在全电脑访问模式下可用
    bool RestrictedDefault,
    bool OffByDefault = false);   // 修改类交互：两种模式下都默认关闭，需要用户主动开启

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
        new("memory_read", "记忆与记录", "搜索记忆", "搜索记忆库、查看记忆的历史版本；连接时会把置顶的记忆直接告诉 AI", "memory_search · memory_versions", false, false, true),
        new("memory_write", "记忆与记录", "保存和修改记忆", "保存新的记忆，修改记忆，把记忆移到回收站（可在记忆库中恢复）", "memory_save · memory_update · memory_delete", false, false, true),
        new("tasks", "治理", "登记任务计划", "AI 可以登记多步任务的计划和进度；严格模式下修改类操作需要先登记任务（在「治理中心」查看）", "task_create · task_update · task_get · task_list · task_complete", false, false, true),
        new("governance", "治理", "处理记忆维护任务", "AI 可以领取记忆库的维护候选（重复、冲突、过期等），提交处理建议；高风险建议需要你在「治理中心」批准", "governance_status · governance_claim · governance_heartbeat · governance_submit · governance_release", false, false, true),
        new("ui_read", "视觉工作台", "查看窗口和控件", "列出桌面上的窗口，读取窗口的控件结构（密码框内容不会读取），等待界面变化", "ui_list_windows · ui_inspect · ui_wait · ui_compare · ui_request_access", false, false, false),
        new("ui_capture", "视觉工作台", "窗口截图和录制", "截取指定窗口的画面，或录制几秒钟的画面帧序列，AI 能看到窗口里显示的内容", "ui_screenshot · ui_record", true, false, false),
        new("ui_control", "视觉工作台", "操作窗口控件", "点击、输入、选择窗口里的控件。每个窗口还需要你在「视觉工作台」单独授权，可随时紧急停止", "ui_action", true, false, false, true),
        new("browser_read", "视觉工作台", "打开和查看网页", "用独立的浏览器配置打开允许列表中的网址（默认只允许本机），查看页面结构和截图", "browser_open · browser_inspect · browser_close", false, false, false),
        new("browser_control", "视觉工作台", "操作网页", "在 AI 打开的网页里点击、输入、执行脚本", "browser_action", true, false, false, true),
    };

    /// <summary>依赖关系：开启前一项需要先开启后一项（与 server.py 的 DEPENDS 一致）</summary>
    public static readonly Dictionary<string, string> Depends = new()
    {
        ["memory_write"] = "memory_read",
        ["governance"] = "memory_write",
        ["ui_control"] = "ui_read",
        ["browser_control"] = "browser_read",
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
            "governance" => s.MemoryWrite,
            _ when p.OffByDefault => false,
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

    /// <summary>实际生效的权限（考虑依赖关系，例如写入记忆需要先能搜索记忆，可以多级）</summary>
    public static bool IsEffective(AppSettings s, bool full, string key)
    {
        for (int i = 0; i < 8 && key != null; i++)
        {
            if (!IsAllowed(s, full, key)) return false;
            if (!Depends.TryGetValue(key, out var need)) return true;
            key = need;
        }
        return true;
    }

    /// <summary>没有开启的前置权限（用于界面提示），没有则返回 null</summary>
    public static PermissionInfo? MissingDependency(AppSettings s, bool full, string key)
    {
        while (Depends.TryGetValue(key, out var need))
        {
            if (!IsAllowed(s, full, need)) return Get(need);
            key = need;
        }
        return null;
    }

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
