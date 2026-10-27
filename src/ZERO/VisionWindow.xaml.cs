using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ZERO.Services;

namespace ZERO;

/// <summary>视觉工作台：查看桌面窗口和控件树、授权 AI 操作指定窗口、批准 AI 的操作请求、查看操作日志、紧急停止。</summary>
public partial class VisionWindow : Window
{
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        { "consent.exe", "LogonUI.exe", "CredentialUIBroker.exe", "LockApp.exe", "SecureHealthSystray.exe" };

    /// <summary>系统安全窗口和 ZERO 主程序自己的窗口（含授权、审批界面）不能授权给 AI</summary>
    static bool IsProtected(string process, long pid) => Protected.Contains(process) || pid == Environment.ProcessId;

    readonly AppSettings _settings;
    readonly IServiceHost? _host;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    List<WinInfo> _all = new();
    WinInfo? _win;
    string _view = "shot";
    int _lastRequests = -1;

    public VisionWindow(AppSettings settings, IServiceHost? host)
    {
        InitializeComponent();
        _settings = settings;
        _host = host;
        StateChanged += (_, _) => Shell.Margin = WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(12);
        _timer.Tick += (_, _) => { if (IsActive || _lastRequests != 0) RefreshStatus(); };
        Loaded += (_, _) => { LoadWindows(); RefreshStatus(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
        Activated += (_, _) => { if (IsLoaded) RefreshStatus(); };
    }

    /// <summary>授权记录对应的访问模式：服务运行中用运行的模式，否则用主界面选中的模式</summary>
    string Mode => _host == null ? (_settings.Mode == "full" ? "full" : "restricted")
        : (_host.IsServiceRunning ? _host.RunningFull : _host.SelectedFull) ? "full" : "restricted";
    string ModeText => Mode == "full" ? "全电脑访问" : "有限制";

    Button Btn(string text, Action onClick, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Style = (Style)FindResource(primary ? "PrimaryButton" : "SecondaryButton"), Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 5, 12, 5),
            Content = new TextBlock { Text = text, Foreground = primary ? Brushes.White : danger ? (Brush)FindResource("Red") : (Brush)FindResource("TextPrimary"), FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal },
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    void Try(string what, Action a)
    {
        try { a(); AppLog.Write("视觉", what); }
        catch (Exception ex)
        {
            AppLog.Error("视觉", ex);
            ZeroDialog.Show(this, new DialogSpec($"{what}失败", ex.Message, DialogKind.Error, "好", null));
        }
    }

    // ====================== 状态、请求 ======================
    void RefreshStatus()
    {
        try
        {
            bool stop = GovernanceStore.GetBool("ui.stop");
            StopText.Text = stop ? "解除紧急停止" : "紧急停止";
            BtnStop.Background = (Brush)FindResource(stop ? "Green" : "Red");
            var desk = Win32.DesktopUnavailable();
            var grants = GovernanceStore.Grants(true);
            StatusText.Text = (stop ? "⛔ 已紧急停止：AI 的所有界面和网页操作都会被拒绝。 " : "") +
                              $"桌面：{(desk == null ? "可用" : desk)} · 有效授权 {grants.Count} 个 · 新授权用于「{ModeText}」模式";
            StatusText.Foreground = (Brush)FindResource(stop ? "Red" : "TextSecondary");
            var reqs = GovernanceStore.Requests();
            if (reqs.Count != _lastRequests) BuildRequests(reqs);
            _lastRequests = reqs.Count;
            if (_view == "grants") LoadGrants();
            if (_view == "log") LogList.ItemsSource = GovernanceStore.UiActions(300);
        }
        catch (Exception ex) { StatusText.Text = "读取状态失败：" + ex.Message; }
    }

    void BuildRequests(List<GovernanceStore.UiRequest> reqs)
    {
        RequestsHost.Children.Clear();
        int minutes = Math.Max(1, GovernanceStore.GetInt("ui.grant_minutes"));
        foreach (var r in reqs)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var alive = Win32.Exists(r.Hwnd) && Win32.WindowPid(r.Hwnd) == r.Pid;
            g.Children.Add(new TextBlock
            {
                Text = $"AI 请求操作窗口：{r.Line}{(alive ? "" : "（窗口已关闭）")}\n授权模式：{(r.Mode == "full" ? "全电脑访问" : "有限制")}，批准后有效 {minutes} 分钟，可随时撤销。",
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20, VerticalAlignment = VerticalAlignment.Center,
            });
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var req = r;
            if (alive && !IsProtected(r.Process, r.Pid))
                btns.Children.Add(Btn("批准", () => { Try($"批准请求 {req.Id}", () => GovernanceStore.DecideRequest(req, true, minutes)); _lastRequests = -1; RefreshStatus(); }, true));
            btns.Children.Add(Btn("拒绝", () => { Try($"拒绝请求 {req.Id}", () => GovernanceStore.DecideRequest(req, false, 0)); _lastRequests = -1; RefreshStatus(); }));
            Grid.SetColumn(btns, 1);
            g.Children.Add(btns);
            RequestsHost.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5)), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 10, 10, 6),
                Margin = new Thickness(0, 0, 0, 10), Child = g,
            });
        }
    }

    void Stop_Click(object s, RoutedEventArgs e)
    {
        bool stop = !GovernanceStore.GetBool("ui.stop");
        if (!stop && !ZeroDialog.Show(this, new DialogSpec("解除紧急停止？", "解除后 AI 可以再次申请操作窗口（之前的授权已全部撤销，需要重新授权）。", DialogKind.Question, "解除"))) return;
        Try(stop ? "紧急停止" : "解除紧急停止", () => GovernanceStore.EmergencyStop(stop));
        _lastRequests = -1;
        RefreshStatus();
        ShowWindow();
    }

    // ====================== 窗口列表 ======================
    void LoadWindows()
    {
        try
        {
            var me = Environment.ProcessId;
            _all = Win32.ListWindows().OrderBy(w => w.Pid == me).ThenBy(w => w.Process).ToList();
            ApplyFilter();
        }
        catch (Exception ex) { AppLog.Error("视觉", ex); }
    }

    void ApplyFilter()
    {
        var f = FilterBox.Text.Trim();
        var list = f.Length == 0 ? _all : _all.Where(w => w.Title.Contains(f, StringComparison.OrdinalIgnoreCase) || w.Process.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        var keep = _win?.Hwnd;
        WinList.ItemsSource = list;
        WinList.SelectedItem = list.FirstOrDefault(w => w.Hwnd == keep);
        if (WinList.SelectedItem == null) { _win = null; ShowWindow(); }
    }

    void Filter_Changed(object s, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) ApplyFilter();
    }

    void Refresh_Click(object s, RoutedEventArgs e) { LoadWindows(); RefreshStatus(); ShowWindow(); }

    void WinList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (WinList.SelectedItem is WinInfo w) { _win = w; ShowWindow(); }
    }

    void View_Click(object s, RoutedEventArgs e) { _view = (string)((RadioButton)s).Tag; ShowWindow(); }

    void ShowWindow()
    {
        ShotBox.Visibility = _view == "shot" ? Visibility.Visible : Visibility.Collapsed;
        TreeList.Visibility = _view == "tree" ? Visibility.Visible : Visibility.Collapsed;
        GrantsView.Visibility = _view == "grants" ? Visibility.Visible : Visibility.Collapsed;
        LogList.Visibility = _view == "log" ? Visibility.Visible : Visibility.Collapsed;
        WinButtons.Children.Clear();
        if (_view == "grants") LoadGrants();
        if (_view == "log") LogList.ItemsSource = GovernanceStore.UiActions(300);
        var w = _win;
        if (w == null)
        {
            WinTitle.Text = "选择左侧的窗口";
            WinMeta.Text = "查看窗口画面和控件结构，或授权 AI 操作这个窗口";
            Shot.Source = null;
            ShotHint.Text = "";
            TreeList.ItemsSource = null;
            return;
        }
        var info = Win32.Info(w.Hwnd);
        bool alive = info != null && info.Pid == w.Pid;
        var grant = GovernanceStore.Grants(true).FirstOrDefault(g => g.Hwnd == w.Hwnd && g.Pid == w.Pid && g.Mode == Mode);
        bool prot = IsProtected(w.Process, w.Pid);
        WinTitle.Text = w.Title.Length > 0 ? w.Title : "（无标题）";
        WinMeta.Text = $"{w.Process} · PID {w.Pid} · hwnd {w.Hwnd} · 类名 {w.Class} · {(info ?? w).W}×{(info ?? w).H}" +
                       (!alive ? " · 窗口已关闭" : "") +
                       (prot ? " · 受保护的窗口（AI 不能观察或操作）" : grant != null ? $" · 已授权 AI 操作（{ModeText}，到 {grant.Expires:HH:mm}）" : " · 未授权 AI 操作");
        WinMeta.Foreground = (Brush)FindResource(grant != null ? "Orange" : "TextSecondary");
        WinButtons.Children.Add(Btn("刷新画面", Capture));
        WinButtons.Children.Add(Btn("读取控件树", () => _ = LoadTree()));
        if (alive && !prot && !GovernanceStore.GetBool("ui.stop"))
        {
            if (grant == null)
            {
                foreach (var m in new[] { 15, 30, 60 })
                {
                    var mins = m;
                    WinButtons.Children.Add(Btn($"授权 AI 操作 {mins} 分钟", () =>
                    {
                        if (!ZeroDialog.Show(this, new DialogSpec("授权 AI 操作这个窗口？",
                                $"在接下来 {mins} 分钟内，连接的 AI（{ModeText}模式）可以点击、输入、操作「{WinTitle.Text}」中的控件。\n可以随时在这里撤销或紧急停止。",
                                DialogKind.Warning, "授权"))) return;
                        Try($"授权窗口 {w.Hwnd}", () => GovernanceStore.AddGrant(Mode, w.Hwnd, w.Pid, w.Process, w.Title, mins));
                        RefreshStatus(); ShowWindow();
                    }, mins == 30));
                }
            }
            else
                WinButtons.Children.Add(Btn("撤销授权", () => { Try($"撤销授权 {grant.Id}", () => GovernanceStore.RevokeGrant(grant.Id)); RefreshStatus(); ShowWindow(); }, danger: true));
        }
        if (_view == "shot") Capture();
    }

    void Capture()
    {
        var w = _win;
        if (w == null) return;
        try
        {
            var img = Win32.Capture(w.Hwnd);
            Shot.Source = Win32.ToBitmap(img);
            ShotHint.Text = "";
        }
        catch (Exception ex)
        {
            Shot.Source = null;
            ShotHint.Text = "无法截取画面：" + ex.Message + (Win32.DesktopUnavailable() is { } d ? "\n" + d : "");
        }
    }

    async Task LoadTree()
    {
        var w = _win;
        if (w == null) return;
        if (_view != "tree") { _view = "tree"; foreach (RadioButton rb in ViewBar.Children) rb.IsChecked = (string)rb.Tag == "tree"; ShowWindow(); }
        TreeList.ItemsSource = new[] { "正在读取控件树…" };
        try
        {
            var tree = await Task.Run(() => UiaProvider.Tree(w.Hwnd, 8, 800));
            var lines = new List<string>();
            foreach (var n in tree["nodes"]!.AsArray())
            {
                var o = n!.AsObject();
                int depth = o["depth"]!.GetValue<int>();
                string s = new string(' ', depth * 2) + $"{o["path"]}  {o["type"]}" +
                           (o["name"] is { } nm ? $"  \"{nm.GetValue<string>()}\"" : "") + (o["id"] is { } id ? $"  #{id.GetValue<string>()}" : "") +
                           (o["value"] is { } v ? $"  = {v.GetValue<string>()}" : "") + (o["actions"] is { } a ? $"  [{string.Join(",", a.AsArray().Select(x => x!.GetValue<string>()))}]" : "") +
                           (o["enabled"] is { } en && !en.GetValue<bool>() ? "  (不可用)" : "");
                lines.Add(s);
            }
            if (tree["truncated"]?.GetValue<bool>() == true) lines.Add("…（已截断，只显示前 800 个元素）");
            TreeList.ItemsSource = lines;
        }
        catch (Exception ex) { TreeList.ItemsSource = new[] { "读取失败：" + ex.Message }; }
    }

    void LoadGrants()
    {
        GrantsHost.Children.Clear();
        var list = GovernanceStore.Grants(false);
        if (list.Count == 0)
            GrantsHost.Children.Add(new TextBlock { Text = "还没有授权记录", FontSize = 12.5, Foreground = (Brush)FindResource("TextTertiary"), Margin = new Thickness(4) });
        foreach (var g in list)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = g.Line, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                                             Foreground = (Brush)FindResource(g.Active ? "TextPrimary" : "TextTertiary") });
            if (g.Active)
            {
                var id = g.Id;
                var b = Btn("撤销", () => { Try($"撤销授权 {id}", () => GovernanceStore.RevokeGrant(id)); RefreshStatus(); ShowWindow(); }, danger: true);
                Grid.SetColumn(b, 1);
                row.Children.Add(b);
            }
            GrantsHost.Children.Add(row);
        }
    }

    // ====================== 窗口按钮 / 调试截图 ======================
    void BtnClose_Click(object s, RoutedEventArgs e) => Close();
    void BtnMin_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void BtnMax_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    public void SelectFirstForPreview(string? titleContains = null)
    {
        var w = WinList.Items.OfType<WinInfo>().FirstOrDefault(x => titleContains == null || x.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase));
        if (w != null) WinList.SelectedItem = w;
    }

    public void SaveSnapshot(string path)
    {
        var root = (FrameworkElement)Content;
        int w = (int)(root.ActualWidth + root.Margin.Left + root.Margin.Right);
        int h = (int)(root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }
}
