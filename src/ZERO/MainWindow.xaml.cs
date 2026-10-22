using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ZERO.Services;
using Forms = System.Windows.Forms;

namespace ZERO;

public partial class MainWindow : Window
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly EnvironmentSetup _env = new();
    bool _autoStarted;
    readonly ManagedProcess _server = new();
    readonly ManagedProcess _tunnel = new();
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _trayToggle;

    enum State { Stopped, Starting, Running, Error }
    State _state = State.Stopped;
    string? _publicUrl;
    int _port;
    bool _serverReady, _showUrl, _showToken, _showMcp, _exiting, _loadingUi;

    static readonly Regex TunnelUrlRx = new(@"https://(?!api\.)[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);
    static readonly Regex AnsiRx = new(@"\x1B\[[0-9;]*[A-Za-z]");

    public MainWindow()
    {
        InitializeComponent();
        _loadingUi = true;
        ModeRestricted.IsChecked = _settings.Mode != "full";
        ModeFull.IsChecked = _settings.Mode == "full";
        WorkspaceBox.Text = _settings.Workspace;
        TrustSwitch.IsChecked = _settings.Trust;
        _loadingUi = false;
        UpdateModeUi();
        RefreshConnectionFields();
        SetState(State.Stopped);

        _server.Output += OnServerLine;
        _tunnel.Output += OnTunnelLine;
        _server.Exited += code => Dispatcher.BeginInvoke(() => OnUnexpectedExit("MCP 服务", code));
        _tunnel.Exited += code => Dispatcher.BeginInvoke(() => OnUnexpectedExit("Cloudflare 隧道", code));

        StateChanged += (_, _) =>
        {
            Shell.Margin = WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(12);
            if (WindowState == WindowState.Minimized) HideToTray();
        };
        Loaded += async (_, _) => { InitTray(); StartMaintenanceTimers(); StartSnapshotIfRequested(); await RunSetupAsync(); };
    }

    // 调试用：ZERO.exe --snapshot=路径  每 2 秒把窗口画面保存为图片（锁屏时也可用）
    // 路径以 .png 结尾时输出 2 倍分辨率透明背景 PNG（用于 README 截图），否则输出 JPG
    void StartSnapshotIfRequested()
    {
        var arg = Array.Find(Environment.GetCommandLineArgs(), a => a.StartsWith("--snapshot="));
        if (arg == null) return;
        var path = arg["--snapshot=".Length..].Trim('"');
        bool png = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        double scale = png ? 2 : 1;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            try
            {
                var root = (FrameworkElement)Content;
                // 加上外边距（阴影区域），否则右侧和底部会被裁掉
                int w = (int)(root.ActualWidth + root.Margin.Left + root.Margin.Right);
                int h = (int)(root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
                if (w <= 0 || h <= 0) return;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                if (!png)
                {
                    var bg = new DrawingVisual();
                    using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB0)), null, new Rect(0, 0, w, h));
                    rtb.Render(bg);
                }
                rtb.Render(root);
                System.Windows.Media.Imaging.BitmapEncoder enc = png
                    ? new System.Windows.Media.Imaging.PngBitmapEncoder()
                    : new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 82 };
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = File.Create(path);
                enc.Save(fs);
            }
            catch { }
        };
        timer.Start();
    }

    // ====================== 图标 ======================
    System.Windows.Shapes.Path MakeIcon(string key, double size, Brush? stroke = null)
    {
        var p = new System.Windows.Shapes.Path { Style = (Style)FindResource("IconPath"), Data = (Geometry)FindResource(key), Width = size, Height = size };
        if (stroke != null) p.Stroke = stroke;
        return p;
    }

    // ====================== 环境准备（等待框） ======================
    sealed class StepRow { public System.Windows.Shapes.Path Icon = null!; public TextBlock Text = null!, Note = null!; }
    readonly Dictionary<string, StepRow> _steps = new();
    Storyboard? _spin;

    void AddStep(string key, string title)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = MakeIcon("IcoCircle", 13, (Brush)FindResource("Gray"));
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        var text = new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var note = new TextBlock
        {
            FontSize = 12, Foreground = (Brush)FindResource("TextTertiary"), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1); Grid.SetColumn(note, 2);
        g.Children.Add(icon); g.Children.Add(text); g.Children.Add(note);
        StepList.Children.Add(g);
        _steps[key] = new StepRow { Icon = icon, Text = text, Note = note };
    }

    void MarkStep(string key, string state, string note = "")
    {
        var s = _steps[key];
        s.Note.Text = note;
        switch (state)
        {
            case "run":
                s.Icon.Data = (Geometry)FindResource("IcoRing"); s.Icon.Stroke = (Brush)FindResource("Accent");
                s.Text.FontWeight = FontWeights.SemiBold; break;
            case "ok":
                s.Icon.Data = (Geometry)FindResource("IcoCheck"); s.Icon.Stroke = (Brush)FindResource("Green");
                s.Text.FontWeight = FontWeights.Normal; break;
            case "fail":
                s.Icon.Data = (Geometry)FindResource("IcoClose"); s.Icon.Stroke = (Brush)FindResource("Red");
                s.Text.FontWeight = FontWeights.SemiBold; break;
        }
    }

    void SetProgress(double? v)
    {
        var track = ((Grid)ProgressFill.Parent).ActualWidth;
        if (v is null) { ProgressFill.Width = track * 0.3; ProgressFill.Opacity = 0.55; return; }
        ProgressFill.Opacity = 1;
        ProgressFill.BeginAnimation(WidthProperty,
            new DoubleAnimation(Math.Clamp(v.Value, 0, 1) * track, TimeSpan.FromMilliseconds(180)));
    }

    void ShowOverlay(bool show)
    {
        SetupOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            _spin ??= new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            if (_spin.Children.Count == 0)
            {
                var a = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9));
                Storyboard.SetTarget(a, Spinner);
                Storyboard.SetTargetProperty(a, new PropertyPath("RenderTransform.Angle"));
                _spin.Children.Add(a);
            }
            _spin.Begin();
        }
        else _spin?.Stop();
    }

    async Task RunSetupAsync()
    {
        StepList.Children.Clear(); _steps.Clear();
        AddStep("py", "Python 运行时（便携版）");
        AddStep("deps", "MCP 依赖（mcp、uvicorn）");
        AddStep("cf", "Cloudflare 隧道（cloudflared）");
        SetupButtons.Visibility = Visibility.Collapsed;
        SetupDoneIcon.Visibility = Visibility.Collapsed;
        Spinner.Visibility = Visibility.Visible;
        SetupTitle.Text = "正在检测运行环境";
        SetupDetail.Text = "";
        SetupDetail.TextWrapping = TextWrapping.NoWrap;
        SetupDetail.TextTrimming = TextTrimming.CharacterEllipsis;
        BtnToggle.IsEnabled = false;

        void OnLog(string m) => Dispatcher.BeginInvoke(() =>
        {
            var t = m.TrimStart('\r').Trim();
            if (t.Length > 0) SetupDetail.Text = t;
        });
        void OnProg(double? v) => Dispatcher.BeginInvoke(() => SetProgress(v));
        _env.Log += OnLog; _env.Progress += OnProg;
        AppLog.Write("环境", "开始检测运行环境");

        string current = "py";
        bool needUi = false;
        try
        {
            _env.WriteServerScript();
            needUi = !_env.HasPython() || !_env.FindCloudflared();
            if (!needUi) needUi = !await _env.HasDepsAsync();
            if (needUi) ShowOverlay(true);

            current = "py";
            if (_env.HasPython()) MarkStep("py", "ok", "已就绪");
            else
            {
                SetupTitle.Text = "正在准备运行环境";
                MarkStep("py", "run", "下载中"); SetProgress(0);
                await _env.InstallPythonAsync();
                MarkStep("py", "ok", "已安装");
            }

            current = "deps";
            if (await _env.HasDepsAsync()) MarkStep("deps", "ok", "已就绪");
            else
            {
                SetupTitle.Text = "正在准备运行环境";
                MarkStep("deps", "run", "安装中"); SetProgress(null);
                await _env.InstallDepsAsync();
                MarkStep("deps", "ok", "已安装");
            }

            current = "cf";
            if (_env.FindCloudflared()) MarkStep("cf", "ok", "已就绪");
            else
            {
                SetupTitle.Text = "正在准备运行环境";
                MarkStep("cf", "run", "下载中"); SetProgress(0);
                await _env.InstallCloudflaredAsync();
                MarkStep("cf", "ok", "已安装");
            }

            SetProgress(1);
            SetupTitle.Text = "环境已就绪";
            if (needUi) await Task.Delay(700);
            ShowOverlay(false);
            BtnToggle.IsEnabled = true;
            // 命令行参数 --start：环境就绪后自动启动服务（可用于开机自启）
            if (!_autoStarted && Array.Exists(Environment.GetCommandLineArgs(), a => a.Equals("--start", StringComparison.OrdinalIgnoreCase)))
            {
                _autoStarted = true;
                BtnToggle_Click(this, new RoutedEventArgs());
            }
            AppLog.Write("环境", $"环境就绪 · Python：{_env.PythonExe} · cloudflared：{_env.CloudflaredExe}");
            AppendLog(ServerLog, $"环境就绪 · Python：{_env.PythonDir}");
            AppendLog(TunnelLog, $"环境就绪 · cloudflared：{_env.CloudflaredExe}");
        }
        catch (Exception ex)
        {
            AppLog.Write("环境", $"步骤 {current} 失败：{ex}");
            ShowOverlay(true);
            _spin?.Stop();
            MarkStep(current, "fail", "失败");
            Spinner.Visibility = Visibility.Collapsed;
            SetupDoneIcon.Visibility = Visibility.Visible;
            SetupTitle.Text = "环境准备失败";
            SetupDetail.TextWrapping = TextWrapping.Wrap;
            SetupDetail.TextTrimming = TextTrimming.None;
            SetupDetail.Text = ex.Message;
            SetupDetail.ToolTip = ex.Message;
            SetupButtons.Visibility = Visibility.Visible;
        }
        finally { _env.Log -= OnLog; _env.Progress -= OnProg; }
    }

    async void SetupRetry_Click(object s, RoutedEventArgs e) => await RunSetupAsync();
    void SetupExit_Click(object s, RoutedEventArgs e) => ExitApp();
    void SetupOpenLog_Click(object s, RoutedEventArgs e) => AppLog.OpenFolder();

    // ====================== 启动 / 停止 ======================
    async void BtnToggle_Click(object s, RoutedEventArgs e)
    {
        if (_state is State.Running or State.Starting) StopAll("已手动停止");
        else await StartAllAsync();
    }

    static int FindFreePort(int preferred)
    {
        for (int p = preferred; p < preferred + 50; p++)
        {
            try { var l = new TcpListener(IPAddress.Loopback, p); l.Start(); l.Stop(); return p; }
            catch (SocketException) { }
        }
        var any = new TcpListener(IPAddress.Loopback, 0); any.Start();
        int port = ((IPEndPoint)any.LocalEndpoint).Port; any.Stop(); return port;
    }

    async Task StartAllAsync()
    {
        bool full = ModeFull.IsChecked == true;
        var ws = WorkspaceBox.Text.Trim();
        if (!full)
        {
            if (ws.Length == 0) { Toast("请先选择工作空间"); return; }
            try { Directory.CreateDirectory(ws); }
            catch (Exception ex) { Toast($"工作空间不可用：{ex.Message}"); return; }
        }
        _settings.Mode = full ? "full" : "restricted";
        _settings.Workspace = ws;
        _settings.Save();

        _publicUrl = null; _serverReady = false;
        RefreshConnectionFields();
        ClearLog(ServerLog); ClearLog(TunnelLog);
        SetState(State.Starting, "正在启动 MCP 服务…");

        _port = FindFreePort(_settings.Port);
        var jobDir = System.IO.Path.Combine(AppSettings.DataDir, "jobs");
        var env = new Dictionary<string, string>
        {
            ["MCP_TOKEN"] = _settings.Token,
            ["MCP_MODE"] = _settings.Mode,
            ["MCP_TRUST"] = full && _settings.Trust ? "1" : "0",
            ["MCP_WORKSPACE"] = ws,
            ["MCP_PORT"] = _port.ToString(),
            ["MCP_JOBDIR"] = jobDir,
            ["MCP_PROTECT"] = AppSettings.DataDir,
            ["MCP_HISTORY_DB"] = _settings.HistoryEnabled ? HistoryStore.DbPath : "",
            ["MCP_HISTORY_SHARE"] = _settings.HistoryEnabled && _settings.HistoryShare ? "1" : "0",
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
            ["PYTHONUNBUFFERED"] = "1",
        };
        try
        {
            _server.Start(_env.PythonExe, $"-u \"{_env.ServerPy}\"", _env.RuntimeDir, env);
            SetDot(ServerDot, ServerState, "Orange", "启动中");

            // 等待端口就绪
            for (int i = 0; i < 60 && !_serverReady; i++)
            {
                await Task.Delay(250);
                if (!_server.IsRunning) throw new Exception("MCP 服务启动失败，请查看左侧日志");
                if (IsPortOpen(_port)) _serverReady = true;
            }
            if (!_serverReady) throw new Exception("MCP 服务启动超时");
            SetDot(ServerDot, ServerState, "Green", $"运行中 · 127.0.0.1:{_port}");

            SetState(State.Starting, "正在建立 Cloudflare 隧道…");
            SetDot(TunnelDot, TunnelState, "Orange", "连接中");
            _tunnel.Start(_env.CloudflaredExe!, $"tunnel --no-autoupdate --url http://127.0.0.1:{_port}", _env.RuntimeDir);

            for (int i = 0; i < 240 && _publicUrl == null; i++)
            {
                await Task.Delay(250);
                if (!_tunnel.IsRunning) throw new Exception("隧道启动失败，请查看右侧日志");
            }
            if (_publicUrl == null) throw new Exception("等待隧道地址超时（60 秒）");
            SetState(State.Running);
        }
        catch (Exception ex)
        {
            StopAll(null);
            SetState(State.Error, ex.Message);
        }
    }

    static bool IsPortOpen(int port)
    {
        try { using var c = new TcpClient(); return c.ConnectAsync(IPAddress.Loopback, port).Wait(200) && c.Connected; }
        catch { return false; }
    }

    void StopAll(string? reason)
    {
        _tunnel.Stop();
        _server.Stop();
        _publicUrl = null; _serverReady = false;
        SetDot(ServerDot, ServerState, "Off", "未运行");
        SetDot(TunnelDot, TunnelState, "Off", "未运行");
        RefreshConnectionFields();
        if (reason != null)
        {
            AppendLog(ServerLog, "■ " + reason);
            AppendLog(TunnelLog, "■ " + reason);
            SetState(State.Stopped);
        }
    }

    void OnUnexpectedExit(string name, int code)
    {
        if (_exiting || _state is State.Stopped or State.Error) return;
        if (_state == State.Starting) return; // 启动流程自己会报错
        StopAll(null);
        AppLog.Write("运行", $"{name} 意外退出，代码 {code}");
        SetState(State.Error, $"{name} 意外退出（代码 {code}），请查看日志后重新启动");
        ShowBalloon($"{name} 意外退出，连接已断开");
    }

    // ====================== 日志 ======================
    void OnServerLine(string line)
    {
        var masked = MaskToken(line);
        AppLog.Write("MCP", masked);
        Dispatcher.BeginInvoke(() => AppendLog(ServerLog, masked));
    }

    void OnTunnelLine(string raw)
    {
        var line = AnsiRx.Replace(raw, "");
        AppLog.Write("隧道", line);
        var m = TunnelUrlRx.Match(line);
        Dispatcher.BeginInvoke(() =>
        {
            AppendLog(TunnelLog, line);
            if (m.Success && _publicUrl == null)
            {
                _publicUrl = m.Value;
                SetDot(TunnelDot, TunnelState, "Green", "已连接");
                RefreshConnectionFields();
            }
        });
    }

    string MaskToken(string s) => s.Replace(_settings.Token, "••••••");

    // 日志面板：每行记下时间，定时清理超过 LogKeepMinutes 分钟的内容；最多保留 MaxLogLines 行
    const int MaxLogLines = 1000;
    readonly Dictionary<TextBox, List<(DateTime Time, string Line)>> _logLines = new();

    void AppendLog(TextBox box, string line)
    {
        if (!_logLines.TryGetValue(box, out var lines)) _logLines[box] = lines = new();
        lines.Add((DateTime.Now, line));
        if (lines.Count > MaxLogLines)
        {
            lines.RemoveRange(0, lines.Count - MaxLogLines * 4 / 5);
            RebuildLog(box, lines);
            return;
        }
        bool atBottom = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 4;
        box.AppendText((box.Text.Length > 0 ? Environment.NewLine : "") + line);
        if (atBottom) box.ScrollToEnd();
    }

    static void RebuildLog(TextBox box, List<(DateTime Time, string Line)> lines)
    {
        bool atBottom = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 4;
        box.Text = string.Join(Environment.NewLine, lines.Select(l => l.Line));
        if (atBottom) box.ScrollToEnd();
    }

    void ClearLog(TextBox box)
    {
        box.Clear();
        if (_logLines.TryGetValue(box, out var lines)) lines.Clear();
    }

    void PruneLogs()
    {
        var cutoff = DateTime.Now.AddMinutes(-Math.Max(1, _settings.LogKeepMinutes));
        foreach (var (box, lines) in _logLines)
        {
            int n = lines.FindIndex(l => l.Time >= cutoff);
            if (n == 0 || lines.Count == 0) continue;
            if (n < 0) n = lines.Count;
            lines.RemoveRange(0, n);
            RebuildLog(box, lines);
        }
    }

    void ClearServerLog_Click(object s, RoutedEventArgs e) => ClearLog(ServerLog);
    void ClearTunnelLog_Click(object s, RoutedEventArgs e) => ClearLog(TunnelLog);

    // ====================== 历史记录 ======================
    HistoryWindow? _history;

    void BtnHistory_Click(object s, RoutedEventArgs e)
    {
        if (_history == null)
        {
            _history = new HistoryWindow(_settings);
            _history.Closed += (_, _) => _history = null;
            _history.Show();
        }
        else
        {
            if (_history.WindowState == WindowState.Minimized) _history.WindowState = WindowState.Normal;
            _history.Activate();
        }
    }

    void CleanupHistory()
    {
        try
        {
            int n = HistoryStore.Cleanup(_settings.HistoryDays);
            if (n > 0) AppLog.Write("历史", $"已自动清理 {n} 个超过 {_settings.HistoryDays} 天的对话");
        }
        catch (Exception ex) { AppLog.Error("历史", ex); }
    }

    void StartMaintenanceTimers()
    {
        var tip = $"清空（超过 {_settings.LogKeepMinutes} 分钟的日志会自动清理）";
        ClearServerBtn.ToolTip = tip; ClearTunnelBtn.ToolTip = tip;
        CleanupHistory();
        var logTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        logTimer.Tick += (_, _) => PruneLogs();
        logTimer.Start();
        var histTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        histTimer.Tick += (_, _) => CleanupHistory();
        histTimer.Start();
    }

    // ====================== 状态与界面 ======================
    void SetState(State st, string? sub = null)
    {
        AppLog.Write("状态", sub == null ? st.ToString() : $"{st}：{sub}");
        _state = st;
        bool busy = st is State.Running or State.Starting;
        ModeRestricted.IsEnabled = ModeFull.IsEnabled = BtnBrowse.IsEnabled = TrustSwitch.IsEnabled = !busy;
        string color; string title;
        switch (st)
        {
            case State.Starting:
                color = "Orange"; title = "正在启动"; sub ??= "";
                ToggleText.Text = "停止"; SetToggleIcon("IcoStop", true);
                BtnToggle.Background = (Brush)FindResource("Red"); break;
            case State.Running:
                color = "Green"; title = "运行中";
                sub = (ModeFull.IsChecked == true ? (_settings.Trust ? "全电脑访问 · 免确认" : "全电脑访问") : $"有限制 · {WorkspaceBox.Text}")
                      + " · 复制 MCP 地址到 AI 客户端即可连接";
                ToggleText.Text = "停止"; SetToggleIcon("IcoStop", true);
                BtnToggle.Background = (Brush)FindResource("Red"); break;
            case State.Error:
                color = "Red"; title = "出错了"; sub ??= "";
                ToggleText.Text = "重新启动"; SetToggleIcon("IcoRefresh", false);
                BtnToggle.Background = (Brush)FindResource("Accent"); break;
            default:
                color = "Gray"; title = "已停止"; sub ??= "选择访问模式后点击「启动」";
                ToggleText.Text = "启动"; SetToggleIcon("IcoPlay", true);
                BtnToggle.Background = (Brush)FindResource("Accent"); break;
        }
        var brush = (Brush)FindResource(color);
        StatusArc.Stroke = brush; StatusDots.Fill = brush; StatusHalo.Fill = brush;
        StatusTitle.Text = title; StatusSub.Text = sub; StatusSub.ToolTip = sub;

        StatusHalo.BeginAnimation(OpacityProperty, null);
        if (st is State.Running or State.Starting)
        {
            StatusHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.12, 0.38, TimeSpan.FromSeconds(1.2))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else StatusHalo.Opacity = 0.22;

        if (_trayToggle != null) _trayToggle.Text = busy ? "停止" : "启动";
        if (_tray != null) _tray.Text = $"ZERO · {title}";
    }

    void SetToggleIcon(string key, bool filled)
    {
        ToggleIcon.Data = (Geometry)FindResource(key);
        ToggleIcon.Fill = filled ? Brushes.White : Brushes.Transparent;
        ToggleIcon.StrokeThickness = filled ? 1.2 : 2.2;
    }

    void SetDot(System.Windows.Shapes.Ellipse dot, TextBlock label, string color, string text)
    {
        dot.Fill = color == "Off" ? new SolidColorBrush(Color.FromRgb(0x63, 0x63, 0x66)) : (Brush)FindResource(color);
        label.Text = text;
    }

    void Mode_Changed(object s, RoutedEventArgs e) { if (!_loadingUi) UpdateModeUi(); }

    void UpdateModeUi()
    {
        bool full = ModeFull.IsChecked == true;
        WorkspacePanel.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        TrustPanel.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        UpdateTrustUi();
        if (full)
        {
            ModeHintBox.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5));
            ModeHintIcon.Data = (Geometry)FindResource("IcoWarn"); ModeHintIcon.Stroke = (Brush)FindResource("Orange");
            ModeHint.Text = "可读写整台电脑的文件，并可运行命令、后台脚本。权限等同当前 Windows 账户，请勿泄露 Token。";
        }
        else
        {
            ModeHintBox.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF5, 0xFF));
            ModeHintIcon.Data = (Geometry)FindResource("IcoInfo"); ModeHintIcon.Stroke = (Brush)FindResource("Accent");
            ModeHint.Text = "只能读写下方工作空间里的文件，命令执行和后台任务全部禁用。";
        }
    }

    void UpdateTrustUi()
    {
        bool on = TrustSwitch.IsChecked == true;
        TrustPanel.Background = new SolidColorBrush(on ? Color.FromRgb(0xFF, 0xEE, 0xED) : Color.FromRgb(0xF5, 0xF5, 0xF7));
        TrustHint.Foreground = on ? (Brush)FindResource("Red") : (Brush)FindResource("TextSecondary");
        TrustHint.Text = on
            ? "已开启：AI 执行命令、修改文件时将不再请求你确认（取决于 AI 客户端）。请只在你信任的对话中使用。"
            : "开启后，支持 MCP 注解的 AI 客户端执行命令、修改文件时不再弹出确认框。";
    }

    // ---- 对话框文案（--snapshot-dialog 调试参数也会用到） ----
    internal static readonly DialogSpec TrustPrompt = new(
        "开启免确认模式？",
        "ZERO 会把所有工具（包括执行命令、写入和删除文件）声明为「只读」，支持该声明的 AI 客户端将不再弹出确认框，直接执行操作。",
        DialogKind.Danger, "仍要开启", "取消",
        "如果 AI 读到网页、文件或邮件中隐藏的恶意指令，可能在你不知情时删除文件或执行危险命令。请只在你信任的对话中开启。");
    internal static readonly DialogSpec StopFirstPrompt = new(
        "请先停止服务", "服务运行中无法重新生成 Token，停止服务后再试。", DialogKind.Info, "好", null);
    internal static readonly DialogSpec RegenPrompt = new(
        "重新生成 Token？", "旧的 MCP 地址会立即失效，需要在 AI 客户端中更新连接。", DialogKind.Warning, "重新生成");
    internal static readonly DialogSpec ExitPrompt = new(
        "退出 ZERO？", "MCP 服务和隧道都会停止，AI 将无法再访问这台电脑。", DialogKind.Question, "退出");

    void Trust_Changed(object s, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        bool on = TrustSwitch.IsChecked == true;
        if (on)
        {
            if (!ZeroDialog.Show(this, TrustPrompt))
            {
                _loadingUi = true; TrustSwitch.IsChecked = false; _loadingUi = false;
                return;
            }
        }
        _settings.Trust = on;
        _settings.Save();
        AppLog.Write("设置", on ? "免确认模式：开启" : "免确认模式：关闭");
        UpdateTrustUi();
    }

    void BtnBrowse_Click(object s, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog
        {
            Description = "选择工作空间文件夹", UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(WorkspaceBox.Text) ? WorkspaceBox.Text : "",
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
        {
            WorkspaceBox.Text = dlg.SelectedPath;
            _settings.Workspace = dlg.SelectedPath;
            _settings.Save();
        }
    }

    // ====================== 地址 / Token 显示 ======================
    string? McpUrl => _publicUrl == null ? null : $"{_publicUrl}/{_settings.Token}/mcp";

    static string Mask(string v) => new string('•', Math.Min(v.Length, 28));

    static string MaskUrl(string url)
    {
        // https://random-words-xxxx.trycloudflare.com -> https://••••••••.trycloudflare.com
        var m = Regex.Match(url, @"^(https://)([^.]+)(\..*)$");
        return m.Success ? m.Groups[1].Value + new string('•', 10) + m.Groups[3].Value : Mask(url);
    }

    void RefreshConnectionFields()
    {
        const string waiting = "启动后自动生成";
        UrlBox.Text = _publicUrl == null ? waiting : (_showUrl ? _publicUrl : MaskUrl(_publicUrl));
        TokenBox.Text = _showToken ? _settings.Token : Mask(_settings.Token);
        var mcp = McpUrl;
        McpBox.Text = mcp == null ? waiting : (_showMcp ? mcp : MaskUrl(_publicUrl!) + "/" + new string('•', 10) + "/mcp");

        var dim = (Brush)FindResource("TextTertiary");
        var normal = (Brush)FindResource("TextPrimary");
        UrlBox.Foreground = _publicUrl == null ? dim : normal;
        McpBox.Foreground = mcp == null ? dim : normal;
        UrlBox.FontFamily = McpBox.FontFamily = (FontFamily)FindResource(_publicUrl == null ? "UiFont" : "MonoFont");

        CopyUrl.IsEnabled = EyeUrl.IsEnabled = _publicUrl != null;
        CopyMcp.IsEnabled = EyeMcp.IsEnabled = mcp != null;
        EyeUrl.Content = MakeIcon(_showUrl ? "IcoEyeOff" : "IcoEye", 15);
        EyeToken.Content = MakeIcon(_showToken ? "IcoEyeOff" : "IcoEye", 15);
        EyeMcp.Content = MakeIcon(_showMcp ? "IcoEyeOff" : "IcoEye", 15);
    }

    void EyeUrl_Click(object s, RoutedEventArgs e) { _showUrl = !_showUrl; RefreshConnectionFields(); }
    void EyeToken_Click(object s, RoutedEventArgs e) { _showToken = !_showToken; RefreshConnectionFields(); }
    void EyeMcp_Click(object s, RoutedEventArgs e) { _showMcp = !_showMcp; RefreshConnectionFields(); }
    void CopyUrl_Click(object s, RoutedEventArgs e) => Copy(_publicUrl);
    void CopyToken_Click(object s, RoutedEventArgs e) => Copy(_settings.Token);

    void RegenToken_Click(object s, RoutedEventArgs e)
    {
        if (_state is State.Running or State.Starting)
        {
            ZeroDialog.Show(this, StopFirstPrompt);
            return;
        }
        if (!ZeroDialog.Show(this, RegenPrompt)) return;
        _settings.Token = AppSettings.NewToken();
        _settings.Save();
        AppLog.Write("设置", "Token 已重新生成");
        RefreshConnectionFields();
    }
    void CopyMcp_Click(object s, RoutedEventArgs e) => Copy(McpUrl);

    void Copy(string? v)
    {
        if (string.IsNullOrEmpty(v)) return;
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(v); break; } catch { System.Threading.Thread.Sleep(60); }
        }
        Toast("已复制");
    }

    void Toast(string msg)
    {
        CopiedTip.Text = msg;
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2200))));
        CopiedTip.BeginAnimation(OpacityProperty, a);
    }

    // ====================== 托盘与窗口 ======================
    void InitTray()
    {
        System.Drawing.Icon icon;
        try
        {
            var sri = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/app.ico"));
            icon = new System.Drawing.Icon(sri!.Stream);
        }
        catch { icon = System.Drawing.SystemIcons.Application; }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _trayToggle = new Forms.ToolStripMenuItem("启动", null, (_, _) => Dispatcher.Invoke(() => BtnToggle_Click(this, new RoutedEventArgs())));
        menu.Items.Add(_trayToggle);
        menu.Items.Add("复制 MCP 地址", null, (_, _) => Dispatcher.Invoke(() => Copy(McpUrl)));
        menu.Items.Add("打开日志", null, (_, _) => AppLog.OpenFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApp));

        _tray = new Forms.NotifyIcon { Icon = icon, Text = "ZERO", Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowFromTray); };
    }

    void HideToTray()
    {
        Hide();
        if (!_settings.TrayHintShown)
        {
            ShowBalloon("ZERO 仍在后台运行，点击托盘图标可重新打开。");
            _settings.TrayHintShown = true;
            _settings.Save();
        }
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    void ShowBalloon(string text)
    {
        if (_tray == null) return;
        _tray.BalloonTipTitle = "ZERO";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(3000);
    }

    void BtnClose_Click(object s, RoutedEventArgs e) => HideToTray();
    void BtnMin_Click(object s, RoutedEventArgs e) => HideToTray();
    void BtnMax_Click(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting) { e.Cancel = true; HideToTray(); return; }
        base.OnClosing(e);
    }

    void ExitApp()
    {
        if (_state is State.Running or State.Starting)
        {
            ShowFromTray();
            if (!ZeroDialog.Show(this, ExitPrompt)) return;
        }
        _exiting = true;
        StopAll(null);
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Close();
        Application.Current.Shutdown();
    }
}
