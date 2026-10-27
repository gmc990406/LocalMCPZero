using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ZERO.Services;

/// <summary>检测并安装运行环境：便携 Python、MCP 依赖、cloudflared。全部放在程序目录 runtime\ 下，不改动系统。</summary>
public sealed class EnvironmentSetup
{
    public string RuntimeDir { get; }
    public string PythonDir => Path.Combine(RuntimeDir, "python");
    public string PythonExe
    {
        get
        {
            var direct = Path.Combine(PythonDir, "python.exe");
            var venv = Path.Combine(PythonDir, "Scripts", "python.exe");
            return File.Exists(direct) || !File.Exists(venv) ? direct : venv;
        }
    }
    public string ServerPy => Path.Combine(RuntimeDir, "server.py");
    public string? CloudflaredExe { get; private set; }

    public event Action<string>? Log;
    public event Action<double?>? Progress;   // null = 不确定进度

    const string PyVersion = "3.12.10";
    // 方案一：NuGet 上的完整版便携 Python（自带 pip）
    static readonly string[] PythonNupkgUrls =
    {
        $"https://api.nuget.org/v3-flatcontainer/python/{PyVersion}/python.{PyVersion}.nupkg",
        $"https://www.nuget.org/api/v2/package/python/{PyVersion}",
    };
    // 方案二：官方嵌入版 Python（国内镜像优先）+ get-pip
    static readonly string[] PythonEmbedUrls =
    {
        $"https://registry.npmmirror.com/-/binary/python/{PyVersion}/python-{PyVersion}-embed-amd64.zip",
        $"https://mirrors.huaweicloud.com/python/{PyVersion}/python-{PyVersion}-embed-amd64.zip",
        $"https://www.python.org/ftp/python/{PyVersion}/python-{PyVersion}-embed-amd64.zip",
    };
    static readonly string[] PipWheelIndexes =
    {
        "https://mirrors.aliyun.com/pypi/simple/pip/",
        "https://pypi.tuna.tsinghua.edu.cn/simple/pip/",
        "https://pypi.org/simple/pip/",
    };
    static readonly string[] PipIndexes = { "", "https://pypi.tuna.tsinghua.edu.cn/simple", "https://mirrors.aliyun.com/pypi/simple/" };
    static readonly string[] CloudflaredUrls =
    {
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe",
    };

    void Say(string m)
    {
        if (!m.StartsWith("\r")) AppLog.Write("环境", m);
        Log?.Invoke(m);
    }
    static void Trace(string m) => AppLog.Write("环境", m);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public EnvironmentSetup()
    {
        RuntimeDir = Path.Combine(AppContext.BaseDirectory, "runtime");
        Directory.CreateDirectory(RuntimeDir);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ZERO/1.0");
    }

    /// <summary>每次启动都用内置的最新脚本覆盖（server.py 及其模块：治理、视觉工作台、数据库结构）。</summary>
    public void WriteServerScript() => WriteScripts();

    public static readonly string[] Scripts = { "server.py", "zero_gov.py", "zero_ui.py", "zero_schema.json" };

    public void WriteScripts()
    {
        foreach (var name in Scripts)
        {
            using var s = typeof(EnvironmentSetup).Assembly.GetManifestResourceStream(name);
            if (s == null) { Trace($"缺少内置资源 {name}"); continue; }
            var path = Path.Combine(RuntimeDir, name);
            var tmp = path + ".tmp";
            using (var f = File.Create(tmp)) s.CopyTo(f);
            File.Move(tmp, path, true);   // 先写临时文件再替换，避免服务读取到写了一半的脚本
        }
    }

    // ---------------- Python ----------------
    public async Task InstallPythonAsync()
    {
        var errors = new List<string>();
        Trace($"开始安装 Python {PyVersion}，目标目录 {PythonDir}");

        // 方案一：NuGet 完整版
        if (Environment.GetEnvironmentVariable("NB_SKIP_NUGET") != "1")
        {
            try { await InstallFromNupkgAsync(); return; }
            catch (Exception ex) { Trace("方案一（NuGet）失败：" + ex); errors.Add("NuGet：" + ex.Message); Say("NuGet 不可用，改用镜像下载嵌入版 Python…"); }
        }

        // 方案二：嵌入版 + get-pip
        try { await InstallFromEmbedAsync(); return; }
        catch (Exception ex) { Trace("方案二（嵌入版）失败：" + ex); errors.Add("嵌入版：" + ex.Message); }

        // 方案三：电脑上已安装的 Python（3.10+）创建独立环境
        try
        {
            if (Environment.GetEnvironmentVariable("NB_SKIP_SYSTEM") == "1") throw new Exception("已跳过（测试）");
            await InstallFromSystemPythonAsync(); return;
        }
        catch (Exception ex) { Trace("方案三（系统 Python）失败：" + ex); errors.Add("系统 Python：" + ex.Message); }

        throw new Exception("Python 安装失败。" + string.Join("；", errors));
    }

    async Task InstallFromNupkgAsync()
    {
        var zip = Path.Combine(RuntimeDir, "python.nupkg");
        await DownloadAnyAsync(PythonNupkgUrls, zip, "Python 运行时");
        Say("正在解压 Python…");
        Progress?.Invoke(null);
        var tmp = Path.Combine(RuntimeDir, "python_tmp");
        ResetDir(tmp);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, tmp));
        ResetDir(PythonDir, create: false);
        Directory.Move(Path.Combine(tmp, "tools"), PythonDir);
        Directory.Delete(tmp, true);
        File.Delete(zip);
        if (!HasPython()) throw new Exception("解压后未找到 python.exe");
        Say($"Python {PyVersion} 已安装到 {PythonDir}");
    }

    async Task InstallFromEmbedAsync()
    {
        var zip = Path.Combine(RuntimeDir, "python-embed.zip");
        await DownloadAnyAsync(PythonEmbedUrls, zip, "Python 嵌入版");
        Say("正在解压 Python…");
        Progress?.Invoke(null);
        ResetDir(PythonDir);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, PythonDir));
        File.Delete(zip);

        // 打开 site 支持，否则 pip 安装的包无法导入
        foreach (var pth in Directory.GetFiles(PythonDir, "python*._pth"))
        {
            var lines = File.ReadAllLines(pth).Select(l => l.Trim() == "#import site" ? "import site" : l).ToList();
            if (!lines.Contains("Lib\\site-packages")) lines.Insert(Math.Max(0, lines.Count - 1), "Lib\\site-packages");
            File.WriteAllLines(pth, lines);
        }

        // 用 pip 的 wheel 包自举安装 pip（不依赖 get-pip.py）
        var wheel = await DownloadPipWheelAsync();
        Say("正在安装 pip…");
        Progress?.Invoke(null);
        var (code, _) = await RunAsync(PythonExe,
            $"\"{wheel}\\pip\" install --no-index --no-warn-script-location --disable-pip-version-check \"{wheel}\"");
        File.Delete(wheel);
        if (code != 0) throw new Exception("pip 安装失败");
        Say($"Python {PyVersion}（嵌入版）已安装到 {PythonDir}");
    }

    async Task<string> DownloadPipWheelAsync()
    {
        var fails = new List<string>();
        foreach (var index in PipWheelIndexes)
        {
            try
            {
                Say($"查找 pip 安装包：{new Uri(index).Host}");
                Trace($"GET {index}");
                var html = await Http.GetStringAsync(index);
                Trace($"  返回 {html.Length} 字符");
                var best = Regex.Matches(html, @"href=""([^""]*?pip-(\d+(?:\.\d+)+)-py3-none-any\.whl)[^""]*""")
                    .Select(m => (url: m.Groups[1].Value, ver: Version.Parse(m.Groups[2].Value)))
                    .OrderByDescending(x => x.ver).FirstOrDefault();
                if (best.url == null) throw new Exception("页面里没有 pip 安装包");
                var url = new Uri(new Uri(index), System.Net.WebUtility.HtmlDecode(best.url)).ToString();
                var target = Path.Combine(RuntimeDir, $"pip-{best.ver}-py3-none-any.whl");
                await DownloadAnyAsync(new[] { url }, target, $"pip {best.ver}");
                return target;
            }
            catch (Exception ex) { Trace($"pip 索引 {index} 失败：{ex}"); fails.Add($"{new Uri(index).Host}（{ex.Message}）"); }
        }
        throw new Exception("pip 下载失败：" + string.Join("、", fails));
    }

    async Task InstallFromSystemPythonAsync()
    {
        string? sys = null;
        foreach (var cand in new[] { "py -3", "python" })
        {
            var parts = cand.Split(' ', 2);
            try
            {
                var (c, o) = await RunAsync(parts[0], (parts.Length > 1 ? parts[1] + " " : "") +
                    "-c \"import sys;print(sys.executable if sys.version_info>=(3,10) else '')\"", echo: false);
                var exe = o.Trim();
                if (c == 0 && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !exe.Contains("WindowsApps")) { sys = exe; break; }
            }
            catch { }
        }
        if (sys == null) throw new Exception("未找到 3.10 以上版本");
        Say($"使用电脑上的 Python 创建独立环境：{sys}");
        ResetDir(PythonDir, create: false);
        var (code, _) = await RunAsync(sys, $"-m venv \"{PythonDir}\"");
        if (code != 0 || !HasPython()) throw new Exception("创建虚拟环境失败");
    }

    static void ResetDir(string dir, bool create = true)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (create) Directory.CreateDirectory(dir);
    }

    public bool HasPython() => File.Exists(PythonExe);

    // ---------------- 依赖 ----------------
    public async Task<bool> HasDepsAsync()
    {
        if (!HasPython()) return false;
        var (code, _) = await RunAsync(PythonExe, "-c \"import mcp.server.fastmcp, uvicorn\"", echo: false);
        return code == 0;
    }

    public async Task InstallDepsAsync()
    {
        Progress?.Invoke(null);
        var (pipCode, _) = await RunAsync(PythonExe, "-m pip --version", echo: false);
        if (pipCode != 0)
        {
            Say("初始化 pip…");
            await RunAsync(PythonExe, "-m ensurepip --upgrade");
        }
        foreach (var index in PipIndexes)
        {
            Say(index == "" ? "从 PyPI 安装 mcp、uvicorn…" : $"改用镜像 {index} 重试…");
            var args = "-m pip install \"mcp<2\" uvicorn --disable-pip-version-check --no-warn-script-location --progress-bar off"
                       + (index == "" ? "" : $" -i {index}");
            var (code, _) = await RunAsync(PythonExe, args);
            if (code == 0 && await HasDepsAsync()) { Say("依赖安装完成"); return; }
        }
        throw new Exception("依赖安装失败，请检查网络后重试");
    }

    // ---------------- cloudflared ----------------
    public bool FindCloudflared()
    {
        var candidates = new List<string> { Path.Combine(RuntimeDir, "cloudflared.exe") };
        // 重新读取注册表里的 PATH（软件启动后用 winget 装的也能找到）
        var path = string.Join(";",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH"));
        candidates.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => { try { return Path.Combine(d.Trim().Trim('"'), "cloudflared.exe"); } catch { return ""; } }));
        candidates.Add(@"C:\Program Files (x86)\cloudflared\cloudflared.exe");
        candidates.Add(@"C:\Program Files\cloudflared\cloudflared.exe");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Links\cloudflared.exe"));
        CloudflaredExe = candidates.FirstOrDefault(c => c != "" && File.Exists(c));
        return CloudflaredExe != null;
    }

    public async Task InstallCloudflaredAsync()
    {
        var target = Path.Combine(RuntimeDir, "cloudflared.exe");
        try
        {
            await DownloadAnyAsync(CloudflaredUrls, target, "cloudflared");
            CloudflaredExe = target;
            Say($"cloudflared 已安装到 {target}");
            return;
        }
        catch (Exception ex)
        {
            Say("直接下载失败，改用 winget 安装…");
            Progress?.Invoke(null);
            try
            {
                await RunAsync("winget", "install --id Cloudflare.cloudflared -e --silent --accept-source-agreements --accept-package-agreements");
            }
            catch (Exception wex) { Trace("winget 不可用：" + wex.Message); }
            if (FindCloudflared()) { Say($"cloudflared 已通过 winget 安装：{CloudflaredExe}"); return; }
            throw new Exception(ex.Message + "；winget 安装也失败了");
        }
    }

    // ---------------- 工具方法 ----------------
    async Task DownloadAnyAsync(IEnumerable<string> urls, string target, string name)
    {
        var fails = new List<string>();
        foreach (var url in urls)
        {
            try
            {
                Say($"下载 {name}：{new Uri(url).Host}");
                Trace($"GET {url}");
                var sw = Stopwatch.StartNew();
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                var total = resp.Content.Headers.ContentLength;
                Trace($"  HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}  最终地址 {resp.RequestMessage?.RequestUri}  大小 {total?.ToString() ?? "未知"}  耗时 {sw.ElapsedMilliseconds} ms");
                resp.EnsureSuccessStatusCode();
                var tmp = target + ".part";
                await using (var src = await resp.Content.ReadAsStreamAsync())
                await using (var dst = File.Create(tmp))
                {
                    var buf = new byte[81920];
                    long read = 0; int n; var lastReport = DateTime.MinValue;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        read += n;
                        if ((DateTime.Now - lastReport).TotalMilliseconds > 150)
                        {
                            lastReport = DateTime.Now;
                            Progress?.Invoke(total > 0 ? (double)read / total.Value : null);
                            if (total > 0) Say($"\r{name}：{read / 1048576.0:0.0} / {total.Value / 1048576.0:0.0} MB");
                        }
                    }
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                Progress?.Invoke(1);
                Trace($"  下载完成 {new FileInfo(target).Length} 字节，用时 {sw.Elapsed.TotalSeconds:0.0} 秒");
                return;
            }
            catch (Exception ex)
            {
                var host = new Uri(url).Host;
                var msg = ex is HttpRequestException h && h.StatusCode != null ? $"HTTP {(int)h.StatusCode.Value}" : ex.Message;
                fails.Add($"{host}（{msg}）");
                Trace($"  {url} 失败：{ex}");
                Say($"{host} 下载失败：{msg}");
            }
        }
        throw new Exception($"{name} 下载失败：{string.Join("、", fails)}");
    }

    async Task<(int code, string output)> RunAsync(string file, string args, bool echo = true)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = RuntimeDir,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        var sb = new StringBuilder();
        Trace($"运行：{file} {args}");
        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); if (echo && e.Data.Trim() != "") Say(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); if (echo && e.Data.Trim() != "") Say(e.Data); } };
        try { p.Start(); }
        catch (Exception ex) { Trace($"  无法启动 {file}：{ex.Message}"); throw; }
        ChildProcessJob.Add(p);
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        var outText = sb.ToString().TrimEnd();
        if (outText.Length > 20000) outText = "…" + outText[^20000..];
        Trace($"  退出码 {p.ExitCode}，用时 {sw.Elapsed.TotalSeconds:0.0} 秒" + (outText.Length > 0 ? "，输出：" + Environment.NewLine + outText : ""));
        return (p.ExitCode, sb.ToString());
    }
}
