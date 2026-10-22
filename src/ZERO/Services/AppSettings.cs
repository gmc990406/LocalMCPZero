using System;
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
    public bool TrayHintShown { get; set; }

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
