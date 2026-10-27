using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZERO.Services;

namespace ZERO;

/// <summary>历史记录：按对话 / 按工具浏览 AI 的操作，可编辑标题、摘要、备注，可删除、拆分、置顶。</summary>
public partial class HistoryWindow : UserControl, ISizeTierAware
{
    readonly AppSettings _settings;
    bool _toolView, _suppress;
    ConversationItem? _conv;
    ToolItem? _tool;
    CallItem? _call;

    public HistoryWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        BtnDataDir.ToolTip = $"历史记录保存在这台电脑的当前用户目录下，更新 ZERO 或移动程序位置都不会丢失：\n{System.IO.Path.GetFullPath(HistoryStore.DbPath)}";
        RecordSwitch.IsChecked = settings.HistoryEnabled;
        foreach (RadioButton rb in KeepGroup.Children)
            rb.IsChecked = (string)rb.Tag == settings.HistoryDays.ToString();
        if (KeepGroup.Children.OfType<RadioButton>().All(r => r.IsChecked != true))
            ((RadioButton)KeepGroup.Children[1]).IsChecked = true;

        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded && !Editing) Reload(); };
        Loaded += (_, _) => Reload();
    }

    /// <summary>正在编辑标题/摘要/备注时不自动刷新，避免覆盖输入内容</summary>
    bool Editing => TitleEdit.IsKeyboardFocusWithin || SummaryEdit.IsKeyboardFocusWithin || NoteEdit.IsKeyboardFocusWithin;

    string Query => Search.Text.Trim();

    // ====================== 列表 ======================
    void Reload(long? selectConv = null, long? selectCall = null)
    {
        try
        {
            var keepCall = selectCall ?? _call?.Id;
            _suppress = true;
            if (_toolView)
            {
                var tools = HistoryStore.Tools(Query);
                MainList.ItemsSource = tools;
                MainList.SelectedItem = tools.FirstOrDefault(t => t.Tool == _tool?.Tool);
                ListTitle.Text = $"工具 · {tools.Count}";
                ListEmpty.Visibility = tools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                var convs = HistoryStore.Conversations(Query);
                MainList.ItemsSource = convs;
                var id = selectConv ?? _conv?.Id;
                MainList.SelectedItem = convs.FirstOrDefault(c => c.Id == id);
                ListTitle.Text = $"对话 · {convs.Count}";
                ListEmpty.Visibility = convs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            ListEmpty.Text = string.IsNullOrEmpty(Query) ? "还没有历史记录\nAI 操作这台电脑后会自动记录在这里" : "没有找到匹配的记录";
            _suppress = false;
            ShowSelection(keepCall);
        }
        catch (Exception ex)
        {
            _suppress = false;
            AppLog.Error("历史", ex);
            ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("无法读取历史记录", ex.Message, DialogKind.Error, "好", null));
        }
    }

    void MainList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        ShowSelection(null);
    }

    void ShowSelection(long? selectCall)
    {
        _conv = MainList.SelectedItem as ConversationItem;
        _tool = MainList.SelectedItem as ToolItem;
        bool any = _conv != null || _tool != null;
        Detail.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        DetailEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any) { _call = null; return; }

        List<CallItem> calls;
        if (_conv != null)
        {
            TitleEdit.Visibility = Visibility.Visible; ToolTitle.Visibility = Visibility.Collapsed;
            SummaryPanel.Visibility = Visibility.Visible; ConvActions.Visibility = Visibility.Visible;
            if (!TitleEdit.IsKeyboardFocusWithin) TitleEdit.Text = _conv.Title;
            if (!SummaryEdit.IsKeyboardFocusWithin) SummaryEdit.Text = _conv.Summary;
            MetaText.Text = $"#{_conv.Id} · {_conv.Client} · {_conv.Range} · {_conv.ModeText} · {_conv.KindText}";
            TitleEdit.ToolTip = _conv.AutoTitle ? "标题由 ZERO 根据操作自动生成（AI 写标题后会替换），点击可修改" : "点击编辑标题";
            if (_conv.AutoTitle) MetaText.Text += " · 自动标题";
            MetaText.ToolTip = _conv.Inferred
                ? "这一组是按「同一客户端 + 时间相近」推断出来的，可能包含多个聊天，也可能把一个聊天拆成多组。可用「拆分」或「合并到上一组」调整。"
                : _conv.Kind == "chat" ? "这一组对应 AI 客户端里的一个聊天（由客户端提供的聊天标识确定，例如 ChatGPT）。"
                : _conv.Kind == "session" ? "这一组来自同一个 MCP 会话（由 AI 客户端回传的会话标识确定）。" : null;
            PinIcon.Stroke = _conv.Pinned ? (Brush)FindResource("Accent") : (Brush)FindResource("TextSecondary");
            BtnPin.ToolTip = _conv.Pinned ? "取消置顶" : "置顶（不会被自动清理）";
            calls = HistoryStore.Calls(_conv.Id, null);
            CallList.ItemTemplate = (DataTemplate)FindResource("CallTemplate");
        }
        else
        {
            TitleEdit.Visibility = Visibility.Collapsed; ToolTitle.Visibility = Visibility.Visible;
            SummaryPanel.Visibility = Visibility.Collapsed; ConvActions.Visibility = Visibility.Collapsed;
            ToolTitle.Text = _tool!.Tool;
            MetaText.Text = _tool.Meta;
            MetaText.Margin = new Thickness(0, 4, 0, 0);
            calls = HistoryStore.Calls(null, _tool.Tool, Query);
            CallList.ItemTemplate = (DataTemplate)FindResource("CallToolTemplate");
        }
        if (_conv != null) MetaText.Margin = new Thickness(10, 2, 0, 0);
        CallsTitle.Text = $"操作记录 · {calls.Count}";
        _suppress = true;
        CallList.ItemsSource = calls;
        CallList.SelectedItem = calls.FirstOrDefault(c => c.Id == selectCall);
        _suppress = false;
        ShowCall();
    }

    void CallList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        ShowCall();
    }

    void ShowCall()
    {
        _call = CallList.SelectedItem as CallItem;
        CallDetail.Visibility = _call == null ? Visibility.Collapsed : Visibility.Visible;
        if (_call == null) return;
        CallDot.Fill = (Brush)FindResource(_call.Ok ? "Green" : "Red");
        CallHead.Text = $"{_call.Tool} · {_call.Time:yyyy-MM-dd HH:mm:ss} · {_call.Ms} ms · {(_call.Ok ? "成功" : "失败")}";
        ArgsBox.Text = PrettyArgs(_call.Args);
        ResultBox.Text = _call.Result;
        if (!NoteEdit.IsKeyboardFocusWithin) NoteEdit.Text = _call.Note;
        BtnGoConv.Visibility = _toolView ? Visibility.Visible : Visibility.Collapsed;
        BtnSplit.Visibility = _toolView ? Visibility.Collapsed : Visibility.Visible;
        CallList.ScrollIntoView(_call);
    }

    /// <summary>把 JSON 参数显示为「名称: 值」，便于阅读</summary>
    static string PrettyArgs(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return json;
            return string.Join("\n", doc.RootElement.EnumerateObject().Select(p =>
                $"{p.Name}: {(p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}"));
        }
        catch { return json; }
    }

    // ====================== 编辑 ======================
    void ConvEdit_LostFocus(object s, KeyboardFocusChangedEventArgs e)
    {
        if (_conv == null || (TitleEdit.Text.Trim() == _conv.Title && SummaryEdit.Text.Trim() == _conv.Summary)) return;
        Try(() => HistoryStore.UpdateConversation(_conv.Id, TitleEdit.Text, SummaryEdit.Text));
        FlashSaved();
        Reload();
    }

    void TitleEdit_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SummaryEdit.Focus(); e.Handled = true; }
        if (e.Key == Key.Escape && _conv != null) { TitleEdit.Text = _conv.Title; MainList.Focus(); }
    }

    void NoteEdit_LostFocus(object s, KeyboardFocusChangedEventArgs e)
    {
        if (_call == null || NoteEdit.Text.Trim() == _call.Note) return;
        Try(() => HistoryStore.UpdateNote(_call.Id, NoteEdit.Text));
        FlashSaved();
        Reload();
    }

    void NoteEdit_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CallList.Focus(); e.Handled = true; }
        if (e.Key == Key.Escape && _call != null) { NoteEdit.Text = _call.Note; CallList.Focus(); }
    }

    void FlashSaved()
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(120)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(1300)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1700)));
        SavedTip.BeginAnimation(OpacityProperty, a);
    }

    void Pin_Click(object s, RoutedEventArgs e)
    {
        if (_conv == null) return;
        Try(() => HistoryStore.SetPinned(_conv.Id, !_conv.Pinned));
        Reload();
    }

    void DeleteConv_Click(object s, RoutedEventArgs e)
    {
        if (_conv == null) return;
        if (!ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("删除这个对话？",
                $"「{_conv.DisplayTitle}」及其 {_conv.Calls} 条操作记录将被永久删除。", DialogKind.Danger, "删除"))) return;
        Try(() => HistoryStore.DeleteConversation(_conv.Id));
        _conv = null; _call = null;
        Reload();
    }

    void DeleteCall_Click(object s, RoutedEventArgs e)
    {
        if (_call == null) return;
        if (!ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("删除这条记录？", $"{_call.Tool} · {_call.Time:MM-dd HH:mm:ss} 将被永久删除。",
                DialogKind.Danger, "删除"))) return;
        Try(() => HistoryStore.DeleteCall(_call.Id));
        _call = null;
        Reload();
    }

    void Split_Click(object s, RoutedEventArgs e)
    {
        if (_call == null) return;
        if (!ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("拆分为新对话？",
                "把这条记录及之后的记录移到一个新对话中。适用于同一个 AI 在短时间内做了两件无关的事。", DialogKind.Question, "拆分"))) return;
        long id = 0;
        var callId = _call.Id;
        Try(() => id = HistoryStore.SplitFrom(callId));
        if (id > 0) Reload(id, callId);
    }

    void Merge_Click(object s, RoutedEventArgs e)
    {
        if (_conv == null) return;
        ConversationItem? prev = null;
        Try(() => prev = HistoryStore.Previous(_conv));
        if (prev == null)
        {
            ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("没有可合并的上一组", $"{_conv.Client} 在这一组之前没有其他记录。", DialogKind.Info, "好", null));
            return;
        }
        if (!ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("合并到上一组？",
                $"把这一组的 {_conv.Calls} 条记录并入 #{prev.Id}「{prev.DisplayTitle}」。适用于同一个聊天被分成了多组。", DialogKind.Question, "合并"))) return;
        long from = _conv.Id, into = prev.Id;
        Try(() => HistoryStore.Merge(from, into));
        _conv = null; _call = null;
        Reload(into);
    }

    void DataDir_Click(object s, RoutedEventArgs e)
    {
        var db = System.IO.Path.GetFullPath(HistoryStore.DbPath);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                System.IO.File.Exists(db) ? $"/select,\"{db}\"" : $"\"{System.IO.Path.GetDirectoryName(db)}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Error("历史", ex); }
    }

    void GoConv_Click(object s, RoutedEventArgs e)
    {
        if (_call == null) return;
        long conv = _call.ConvId, call = _call.Id;
        _suppress = true;
        Search.Clear(raise: false);
        ViewConv.IsChecked = true;
        _toolView = false;
        _suppress = false;
        Reload(conv, call);
    }

    // ====================== 工具栏与设置 ======================
    void View_Changed(object s, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        _toolView = ViewTool.IsChecked == true;
        _call = null;
        MainList.ItemTemplate = (DataTemplate)FindResource(_toolView ? "ToolTemplate" : "ConvTemplate");
        Reload();
    }

    void Search_Go(object? s, EventArgs e)
    {
        if (_suppress || !IsLoaded) return;
        Reload();
    }

    void Refresh_Click(object s, RoutedEventArgs e) => Reload();

    void Setting_Changed(object s, RoutedEventArgs e)
    {
        _settings.HistoryEnabled = RecordSwitch.IsChecked == true;
        _settings.Save();
        SettingHint.Visibility = Visibility.Visible;
        AppLog.Write("设置", $"历史记录：记录={_settings.HistoryEnabled}");
    }

    void Keep_Click(object s, RoutedEventArgs e)
    {
        var days = int.Parse((string)((RadioButton)s).Tag);
        if (days == _settings.HistoryDays) return;
        if (days > 0 && (days < _settings.HistoryDays || _settings.HistoryDays == 0) && !ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec(
                $"只保留 {days} 天？", $"超过 {days} 天的对话会被立即删除（置顶的对话除外）。", DialogKind.Warning, "确定")))
        {
            foreach (RadioButton rb in KeepGroup.Children) rb.IsChecked = (string)rb.Tag == _settings.HistoryDays.ToString();
            return;
        }
        _settings.HistoryDays = days;
        _settings.Save();
        int n = 0;
        Try(() => n = HistoryStore.Cleanup(days));
        AppLog.Write("设置", $"历史保留天数：{(days == 0 ? "永久" : days + " 天")}，已清理 {n} 个对话");
        Reload();
    }

    void ClearAll_Click(object s, RoutedEventArgs e)
    {
        if (!ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("清空历史记录？", "所有对话和操作记录都将被永久删除，无法恢复。",
                DialogKind.Danger, "清空", "取消", "置顶的对话会保留。"))) return;
        Try(() => HistoryStore.ClearAll(keepPinned: true));
        AppLog.Write("历史", "已清空历史记录（保留置顶）");
        _conv = null; _call = null;
        Reload();
    }

    void Try(Action a)
    {
        try { a(); }
        catch (Exception ex)
        {
            AppLog.Error("历史", ex);
            ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("操作失败", ex.Message, DialogKind.Error, "好", null));
        }
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

    /// <summary>调试用：选中第一项及其中一条记录（截图时展示详情）</summary>
    public void SelectFirstForPreview(int callIndex, bool toolView)
    {
        if (toolView) ViewTool.IsChecked = true;
        if (MainList.Items.Count > 0) MainList.SelectedIndex = 0;
        if (CallList.Items.Count > callIndex) CallList.SelectedIndex = callIndex;
    }

    public void ApplySizeTier(string tier, double listCol, double visionList, double shotMin, double gap)
    {
        if (ListCol != null) ListCol.Width = new GridLength(listCol);
        if (ListGap != null) ListGap.Width = new GridLength(gap);
    
    }
}
