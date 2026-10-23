using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ZERO.Services;

namespace ZERO;

/// <summary>主窗口提供给权限窗口的服务状态</summary>
public interface IServiceHost
{
    bool IsServiceRunning { get; }
    bool RunningFull { get; }              // 运行中的服务使用的访问模式
    string? RunningSignature { get; }      // 运行中的服务使用的权限（Permissions.Signature）
    bool SelectedFull { get; }             // 主界面当前选中的访问模式
    Task<bool> RestartServerAsync();       // 只重启 MCP 服务，保留隧道地址
}

/// <summary>权限：按访问模式（有限制 / 全电脑访问）分别设置 AI 可以使用哪些能力。</summary>
public partial class PermissionWindow : Window
{
    readonly AppSettings _settings;
    readonly IServiceHost? _host;
    readonly List<Row> _rows = new();
    bool _full, _suppress, _restarting;
    string? _justApplied;

    sealed class Row
    {
        public PermissionInfo Info = null!;
        public CheckBox Switch = null!;
        public TextBlock Desc = null!, Note = null!;
        public Path NoteIcon = null!;
        public FrameworkElement NoteLine = null!;
    }

    public PermissionWindow(AppSettings settings, IServiceHost? host)
    {
        InitializeComponent();
        _settings = settings;
        _host = host;
        StateChanged += (_, _) => Shell.Margin = WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(12);
        Activated += (_, _) => { if (IsLoaded) Refresh(); };   // 记忆库 / 历史记录窗口里的总开关可能变了
        BuildRows();
        SelectTab(host?.SelectedFull ?? settings.Mode == "full");
    }

    public void SelectTab(bool full)
    {
        _suppress = true;
        TabFull.IsChecked = full;
        TabRestricted.IsChecked = !full;
        _suppress = false;
        _full = full;
        Refresh();
    }

    void Tab_Changed(object s, RoutedEventArgs e)
    {
        if (_suppress || !IsInitialized) return;
        _full = TabFull.IsChecked == true;
        _justApplied = null;
        Refresh();
    }

    // ====================== 列表 ======================
    void BuildRows()
    {
        RowsHost.Children.Clear();
        _rows.Clear();
        foreach (var group in Permissions.All.GroupBy(p => p.Group))
        {
            RowsHost.Children.Add(new TextBlock
            {
                Text = group.Key, Style = (Style)FindResource("Caption"),
                Margin = new Thickness(4, RowsHost.Children.Count == 0 ? 8 : 18, 0, 8),
            });
            var list = new StackPanel();
            foreach (var p in group)
            {
                if (list.Children.Count > 0)
                    list.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF0)), Margin = new Thickness(0, 12, 0, 12) });
                list.Children.Add(BuildRow(p));
            }
            RowsHost.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 14, 16, 14), Child = list });
        }
    }

    FrameworkElement BuildRow(PermissionInfo p)
    {
        var row = new Row { Info = p };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = p.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        row.Desc = new TextBlock
        {
            FontSize = 12, Foreground = (Brush)FindResource("TextSecondary"), TextWrapping = TextWrapping.Wrap,
            LineHeight = 18, Margin = new Thickness(0, 3, 0, 0),
        };
        left.Children.Add(row.Desc);
        left.Children.Add(new TextBlock
        {
            Text = p.Tools, FontSize = 11, FontFamily = (FontFamily)FindResource("MonoFont"),
            Foreground = (Brush)FindResource("TextTertiary"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        var noteLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.NoteIcon = new Path { Style = (Style)FindResource("IconPath"), Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Note = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
        noteLine.Children.Add(row.NoteIcon);
        noteLine.Children.Add(row.Note);
        row.NoteLine = noteLine;
        left.Children.Add(noteLine);
        g.Children.Add(left);

        row.Switch = new CheckBox
        {
            Style = (Style)FindResource(p.Risky ? "Switch" : "SwitchSafe"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), Tag = p.Key,
        };
        row.Switch.Click += Switch_Click;
        Grid.SetColumn(row.Switch, 1);
        g.Children.Add(row.Switch);
        _rows.Add(row);
        return g;
    }

    void Refresh()
    {
        _suppress = true;
        foreach (var r in _rows)
        {
            var p = r.Info;
            bool allowed = Permissions.IsAllowed(_settings, _full, p.Key);
            string? note = null; bool warn = false, enabled = true;
            if (!_full && p.FullOnly)
            {
                enabled = false;
                note = "有限制模式下不能开启：命令可以访问工作空间以外的任何文件。需要时请在主界面切换到「全电脑访问」。";
            }
            else if (p.Key.StartsWith("memory_") && !_settings.MemoryEnabled)
            {
                enabled = false; note = "记忆库已关闭，可在「记忆库」窗口底部开启。";
            }
            else if (p.Key == "memory_write" && !Permissions.IsAllowed(_settings, _full, "memory_read"))
            {
                enabled = false; note = "需要先开启「搜索记忆」。";
            }
            else if (p.Key == "history" && !_settings.HistoryEnabled)
            {
                enabled = false; note = "没有在记录操作历史，可在「历史记录」窗口底部开启。";
            }
            else if (!_full && allowed && p.Key is "system" or "screenshot")
            {
                warn = true; note = "开启后 AI 能看到工作空间以外的信息。";
            }

            r.Switch.IsChecked = enabled && allowed;
            r.Switch.IsEnabled = enabled;
            r.Desc.Text = p.Description + (!_full && p.Key is "file_read" or "file_write" ? "（仅限工作空间内）" : "");
            r.NoteLine.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
            r.Note.Text = note ?? "";
            var brush = (Brush)FindResource(warn ? "Orange" : "TextSecondary");
            r.Note.Foreground = brush;
            r.NoteIcon.Stroke = brush;
            r.NoteIcon.Data = (Geometry)FindResource(warn ? "IcoWarn" : (!_full && p.FullOnly) ? "IcoLock" : "IcoInfo");
        }
        _suppress = false;

        var (on, total) = Permissions.Count(_settings, _full);
        CountText.Text = $"已开启 {on} / {total} 项";
        if (_full)
        {
            ModeBox.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5));
            ModeIcon.Data = (Geometry)FindResource("IcoWarn"); ModeIcon.Stroke = (Brush)FindResource("Orange");
            ModeText.Text = "全电脑访问：AI 的权限等同当前 Windows 账户。不需要的能力可以在这里关闭，例如只允许读取文件、不允许截图。";
        }
        else
        {
            ModeBox.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF5, 0xFF));
            ModeIcon.Data = (Geometry)FindResource("IcoInfo"); ModeIcon.Stroke = (Brush)FindResource("Accent");
            ModeText.Text = "有限制：文件操作只能在工作空间内进行，运行命令和后台任务始终禁用。";
        }
        RefreshFooter();
    }

    /// <summary>根据服务状态提示修改是否已生效（主窗口启动 / 停止服务时也会调用）</summary>
    public void RefreshFooter()
    {
        string mode = _full ? "全电脑访问" : "有限制";
        bool running = _host?.IsServiceRunning == true;
        bool sameMode = running && _host!.RunningFull == _full;
        bool pending = sameMode && _host!.RunningSignature != Permissions.Signature(_settings, _full);
        BtnApply.Visibility = pending && !_restarting ? Visibility.Visible : Visibility.Collapsed;
        BtnApply.IsEnabled = !_restarting;
        FooterHint.Foreground = (Brush)FindResource(pending ? "Orange" : "TextTertiary");
        FooterHint.Text = _restarting ? "正在重启 MCP 服务…"
            : pending ? "已保存，但服务还在使用修改前的权限，重启服务后生效（公网地址不变）。"
            : sameMode ? (_justApplied ?? "已生效。如果 AI 客户端仍显示旧的工具，请在客户端中刷新连接器或新开对话。")
            : running ? $"修改会自动保存。服务当前以「{(_host!.RunningFull ? "全电脑访问" : "有限制")}」模式运行，这里的设置在以「{mode}」模式启动时生效。"
            : $"修改会自动保存，下次以「{mode}」模式启动服务时生效。";
    }

    void Switch_Click(object s, RoutedEventArgs e)
    {
        if (_suppress) return;
        var sw = (CheckBox)s;
        var key = (string)sw.Tag;
        var on = sw.IsChecked == true;
        var p = Permissions.Get(key);
        Permissions.Set(_settings, _full, key, on);
        _settings.Save();
        _justApplied = null;
        AppLog.Write("权限", $"{(_full ? "全电脑访问" : "有限制")} · {p.Title} = {(on ? "开" : "关")}");
        Refresh();
    }

    void Reset_Click(object s, RoutedEventArgs e)
    {
        Permissions.Reset(_settings, _full);
        _settings.Save();
        _justApplied = null;
        AppLog.Write("权限", $"{(_full ? "全电脑访问" : "有限制")} · 恢复默认");
        Refresh();
    }

    async void Apply_Click(object s, RoutedEventArgs e)
    {
        if (_host == null || _restarting) return;
        _restarting = true;
        RefreshFooter();
        bool ok = await _host.RestartServerAsync();
        _restarting = false;
        _justApplied = ok ? "已重启服务，新的权限已生效。ChatGPT 等客户端需要刷新连接器（或新开对话）才能看到新的工具列表。" : null;
        if (!ok) ZeroDialog.Show(this, new DialogSpec("重启失败", "MCP 服务没有成功重启，请在主界面查看日志后重新启动。", DialogKind.Error, "好", null));
        RefreshFooter();
    }

    // ====================== 调试截图 ======================
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

    // ====================== 窗口按钮 ======================
    void BtnClose_Click(object s, RoutedEventArgs e) => Close();
    void BtnMin_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void BtnMax_Click(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
