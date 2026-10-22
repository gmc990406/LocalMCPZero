using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace ZERO.Services;

/// <summary>写入程序目录 logs\ZERO.log（不可写时改用 %LOCALAPPDATA%\ZERO\logs）。</summary>
public static class AppLog
{
    static readonly object Lock = new();
    public static string FilePath { get; }

    static AppLog()
    {
        string Try(string dir)
        {
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, "ZERO.log");
            File.AppendAllText(f, "");
            return f;
        }
        try { FilePath = Try(Path.Combine(AppContext.BaseDirectory, "logs")); }
        catch
        {
            FilePath = Try(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZERO", "logs"));
        }
        try   // 超过 5 MB 时轮换
        {
            var fi = new FileInfo(FilePath);
            if (fi.Length > 5 * 1024 * 1024) { File.Copy(FilePath, FilePath + ".old", true); File.WriteAllText(FilePath, ""); }
        }
        catch { }
    }

    public static void Write(string category, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message.Replace("\r", "")}{Environment.NewLine}";
            lock (Lock) File.AppendAllText(FilePath, line, Encoding.UTF8);
        }
        catch { }
    }

    public static void Error(string category, Exception ex) => Write(category, "异常：" + ex);

    public static void SessionHeader()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("==================================================================");
        sb.AppendLine($"ZERO 启动  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  版本 {typeof(AppLog).Assembly.GetName().Version}");
        sb.AppendLine($"程序目录：{AppContext.BaseDirectory}");
        sb.AppendLine($"系统：{RuntimeInformation.OSDescription}  架构 {RuntimeInformation.OSArchitecture}  .NET {Environment.Version}");
        sb.AppendLine($"用户：{Environment.UserName}  语言：{System.Globalization.CultureInfo.CurrentUICulture.Name}");
        try
        {
            var test = new Uri("https://pypi.org/");
            var proxy = HttpClient.DefaultProxy.GetProxy(test);
            sb.AppendLine($"系统代理：{(proxy == null || proxy == test ? "无" : proxy.ToString())}");
        }
        catch (Exception ex) { sb.AppendLine("系统代理：读取失败 " + ex.Message); }
        foreach (var v in new[] { "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "PIP_INDEX_URL", "PYTHONHOME", "PYTHONPATH" })
        {
            var val = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(val)) sb.AppendLine($"环境变量 {v}={val}");
        }
        sb.Append("==================================================================");
        Write("启动", sb.ToString());
    }

    public static void OpenFolder()
    {
        try { Process.Start("explorer.exe", $"/select,\"{FilePath}\""); } catch { }
    }
}
