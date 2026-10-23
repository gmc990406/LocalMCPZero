using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ZERO.Services;

public sealed class AppSettings
{
    public string Mode { get; set; } = "restricted";          // restricted | full
    public string Workspace { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ZERO-Workspace");
    public string Token { get; set; } = "";
    public int Port { get; set; } = 8766;
    public bool Trust { get; set; }                            // 免确认模式（仅全电脑访问有效，默认关闭）
    public bool TrayHintShown { get; set; }
    public bool HistoryEnabled { get; set; } = true;          // 记录 AI 的操作历史
    public bool HistoryShare { get; set; } = true;            // 旧版开关：允许 AI 查看历史（现在只作为「权限」的默认值）
    public int HistoryDays { get; set; } = 30;                // 历史保留天数，0 = 永久
    public int LogKeepMinutes { get; set; } = 30;             // 主界面日志面板自动清理：只保留最近 N 分钟
    public bool MemoryEnabled { get; set; } = true;           // 记忆库：AI 可以搜索
    public bool MemoryWrite { get; set; } = true;             // 旧版开关：允许 AI 保存/修改/删除记忆（现在只作为「权限」的默认值）
    // AI 权限（「权限」窗口），有限制 / 全电脑访问两种模式分开保存；没有设置过的项使用默认值，见 Permissions.Default
    public Dictionary<string, bool>? PermsRestricted { get; set; }
    public Dictionary<string, bool>? PermsFull { get; set; }

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZERO");
    static string FilePath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Load()
    {
        MigrateFromOldVersions();
        AppSettings s;
        try { s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { s = new(); }
        if (string.IsNullOrWhiteSpace(s.Token) || s.Token.Length < 24) { s.Token = NewToken(); s.Save(); }
        return s;
    }

    /// <summary>从旧版（LocalMCP / Notion Bridge）的配置目录迁移设置，保留 Token 与模式。</summary>
    static void MigrateFromOldVersions()
    {
        try
        {
            if (File.Exists(FilePath)) return;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var name in new[] { "LocalMCP", "NotionBridge" })
            {
                var old = Path.Combine(local, name, "settings.json");
                if (!File.Exists(old)) continue;
                Directory.CreateDirectory(DataDir);
                File.Copy(old, FilePath);
                return;
            }
        }
        catch { }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
