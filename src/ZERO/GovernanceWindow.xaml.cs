using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZERO.Services;

namespace ZERO;

/// <summary>治理中心：记忆库的维护候选与 AI 结论、任务队列、审计与回收站、策略设置。</summary>
public partial class GovernanceWindow : Window
{
    readonly AppSettings _settings;
    string _tab = "overview", _candFilter = "open", _taskFilter = "active", _auditFilter = "trash";
    bool _busy;
    long _candId;
    string? _taskId;
    long _auditMemId;
    readonly Dictionary<string, FrameworkElement> _policyInputs = new();

    public GovernanceWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        StateChanged += (_, _) => Shell.Margin = WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(12);
        Activated += (_, _) => { if (IsLoaded && !_busy && _tab != "policy") Reload(); };
        Loaded += (_, _) => Reload();
    }

    // ====================== 页签 ======================
    public void SelectTab(string tag)
    {
        foreach (RadioButton rb in TabBar.Children) rb.IsChecked = (string)rb.Tag == tag;
        _tab = tag;
        Reload();
    }

    void Tab_Click(object s, RoutedEventArgs e) { _tab = (string)((RadioButton)s).Tag; Reload(); }
    void CandFilter_Click(object s, RoutedEventArgs e) { _candFilter = (string)((RadioButton)s).Tag; _candId = 0; Reload(); }
    void TaskFilter_Click(object s, RoutedEventArgs e) { _taskFilter = (string)((RadioButton)s).Tag; Reload(); }
    void AuditFilter_Click(object s, RoutedEventArgs e) { _auditFilter = (string)((RadioButton)s).Tag; _auditMemId = 0; Reload(); }
    void Refresh_Click(object s, RoutedEventArgs e) => Reload();

    void Reload()
    {
        PanelOverview.Visibility = _tab == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PanelReview.Visibility = _tab == "review" ? Visibility.Visible : Visibility.Collapsed;
        PanelTasks.Visibility = _tab == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        PanelAudit.Visibility = _tab == "audit" ? Visibility.Visible : Visibility.Collapsed;
        PanelPolicy.Visibility = _tab == "policy" ? Visibility.Visible : Visibility.Collapsed;
        PolicyButtons.Visibility = _tab == "policy" ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            switch (_tab)
            {
                case "overview": LoadOverview(); break;
                case "review": LoadCandidates(); break;
                case "tasks": LoadTasks(); break;
                case "audit": LoadAudit(); break;
                case "policy": LoadPolicy(); break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("治理", ex);
            FooterHint.Text = "读取失败：" + ex.Message;
            FooterHint.Foreground = (Brush)FindResource("Red");
        }
    }

    void Hint(string text, bool warn = false)
    {
        FooterHint.Text = text;
        FooterHint.Foreground = (Brush)FindResource(warn ? "Orange" : "TextTertiary");
    }

    // ====================== 小部件 ======================
    Button Btn(string text, Action onClick, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Style = (Style)FindResource(primary ? "PrimaryButton" : "SecondaryButton"), Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 6, 14, 6),
            Content = new TextBlock { Text = text, Foreground = primary ? Brushes.White : danger ? (Brush)FindResource("Red") : (Brush)FindResource("TextPrimary"), FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal },
        };
        if (danger && primary) b.Background = (Brush)FindResource("Red");
        b.Click += (_, _) => onClick();
        return b;
    }

    TextBlock Txt(string text, double size = 12.5, string? brush = null, bool bold = false, Thickness? margin = null) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.55,
        Foreground = (Brush)FindResource(brush ?? "TextPrimary"), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = margin ?? new Thickness(0, 0, 0, 4),
    };

    TextBlock Caption(string text, double top = 14) => new() { Text = text, Style = (Style)FindResource("Caption"), Margin = new Thickness(2, top, 0, 6) };

    TextBox Code(string text, double maxH = 180) => new()
    {
        Text = text, Style = (Style)FindResource("CodeBox"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = maxH,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(10, 8, 10, 8),
        Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF8)), BorderThickness = new Thickness(0),
    };

    Border Box(UIElement child, string? bg = null) => new()
    {
        Background = new SolidColorBrush(bg == null ? Color.FromRgb(0xFA, 0xFA, 0xFC) : (Color)ColorConverter.ConvertFromString(bg)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEB)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12, 14, 10), Margin = new Thickness(0, 0, 0, 10), Child = child,
    };

    bool Confirm(string title, string msg, string ok, bool danger = false) =>
        ZeroDialog.Show(this, new DialogSpec(title, msg, danger ? DialogKind.Danger : DialogKind.Question, ok));

    /// <summary>调用 zero_gov.py 完成需要规则判断的操作，完成后刷新界面</summary>
    async Task Run(string what, Func<Task<GovernanceStore.CliResult>> op)
    {
        if (_busy) return;
        _busy = true;
        Hint($"正在{what}…");
        try
        {
            var r = await op();
            AppLog.Write("治理", $"{what}：{(r.Ok ? "成功" : "失败")} {r.Message}");
            if (!r.Ok) ZeroDialog.Show(this, new DialogSpec($"{what}失败", r.Message, DialogKind.Error, "好", null));
            Hint(r.Ok ? $"{what}：{r.Message}" : $"{what}失败：{r.Message}", !r.Ok);
        }
        catch (Exception ex)
        {
            AppLog.Error("治理", ex);
            ZeroDialog.Show(this, new DialogSpec($"{what}失败", ex.Message, DialogKind.Error, "好", null));
        }
        finally { _busy = false; }
        Reload();
    }

    void Local(string what, Action op)
    {
        try { op(); AppLog.Write("治理", what); Hint(what + " · 已完成"); }
        catch (Exception ex)
        {
            AppLog.Error("治理", ex);
            ZeroDialog.Show(this, new DialogSpec($"{what}失败", ex.Message, DialogKind.Error, "好", null));
        }
        Reload();
    }

    // ====================== 概览 ======================
    void LoadOverview()
    {
        var o = GovernanceStore.Overview();
        StatCards.Children.Clear();
        void Stat(string label, long n, string? tab = null, bool warn = false, string? filter = null)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = n.ToString(), FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(warn && n > 0 ? "Orange" : "TextPrimary") });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 2, 0, 0) });
            var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 12, 18, 12), Margin = new Thickness(0, 0, 12, 12), Width = 160, Child = sp };
            if (tab != null)
            {
                card.Cursor = System.Windows.Input.Cursors.Hand;
                card.ToolTip = "点击查看";
                card.MouseLeftButtonUp += (_, _) =>
                {
                    if (tab == "review" && filter != null) { _candFilter = filter; foreach (RadioButton rb in CandFilter.Children) rb.IsChecked = (string)rb.Tag == filter; }
                    if (tab == "tasks" && filter != null) { _taskFilter = filter; foreach (RadioButton rb in TaskFilter.Children) rb.IsChecked = (string)rb.Tag == filter; }
                    SelectTab(tab);
                };
            }
            StatCards.Children.Add(card);
        }
        Stat("有效记忆", o.Memories, "audit");
        Stat("置顶", o.Pinned);
        Stat("已归档", o.Archived, "audit");
        Stat("回收站", o.Trash, "audit");
        Stat("待复审候选", o.CandidatesOpen, "review", true, "open");
        Stat("待你审批的结论", o.DecisionsPending, "review", true, "decisions");
        Stat("排队中的任务", o.TasksQueued, "tasks", false, "active");
        Stat("执行中的任务", o.TasksActive, "tasks", false, "active");
        Stat("待审核/待批准", o.TasksReview, "tasks", true, "attention");
        Stat("失败的任务", o.TasksFailed, "tasks", true, "attention");

        var pol = GovernanceStore.Policy();
        bool enabled = pol.GetValueOrDefault("review.enabled") is "1" or "true";
        int cycle = GovernanceStore.GetInt("review.cycle_days");
        var last = GovernanceStore.LastScan();
        string next = !enabled ? "定时检查已关闭（可在「策略设置」中开启）"
            : last == null ? "ZERO 运行时会尽快进行第一次检查"
            : $"下次定时检查：{last.Value.AddDays(cycle):yyyy-MM-dd HH:mm} 之后（每 {cycle} 天）";
        ScanInfo.Text = $"最近一次：{(o.LastScan is { } t ? t.ToString("yyyy-MM-dd HH:mm") + " · " : "")}{o.LastScanText}\n{next}\n" +
                        "检查只使用本地规则（不调用 AI 模型），不会修改记忆，只生成待复审的候选。";
        ScanInfo.Foreground = (Brush)FindResource(o.LastScanOk ? "TextPrimary" : "Red");
        string level = pol.GetValueOrDefault("task.level") switch { "strict" => "严格（修改类操作必须先登记任务）", "light" => "轻量（不提示登记任务）", _ => "标准（连续修改时提示登记任务，不强制）" };
        PolicyInfo.Text = $"任务模式：{level}{(pol.GetValueOrDefault("task.require_approval") is "1" ? "，任务需要你批准后才能执行" : "")}\n" +
                          $"自动生效的维护结论：{pol.GetValueOrDefault("review.auto_apply")}（其余结论需要你审批）\n" +
                          $"空闲提示：{(pol.GetValueOrDefault("review.passive_hint") is "1" ? "有待处理的维护任务时，偶尔在工具结果末尾提示 AI（每个客户端最多每 " + pol.GetValueOrDefault("hint.minutes") + " 分钟一次）" : "关闭")}\n" +
                          $"视觉工作台：{(pol.GetValueOrDefault("ui.stop") is "1" ? "已紧急停止" : "正常")} · 浏览器允许列表：{pol.GetValueOrDefault("browser.allow")}";
        Hint("策略修改后立即生效（MCP 服务每次调用都会读取，约 2 秒内），不需要重启服务。");
    }

    async void Scan_Click(object s, RoutedEventArgs e)
    {
        BtnScan.IsEnabled = false;
        ScanText.Text = "正在检查…";
        await Run("检查记忆库", async () =>
        {
            var r = await GovernanceStore.ScanAsync("manual");
            if (r.Ok && r.Data is { } d && d.TryGetProperty("stats", out var st))
            {
                int I(string k) => st.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : 0;
                return r with { Message = $"检查了 {I("memories")} 条记忆，新候选 {I("new_candidates")} 个，重新打开 {I("reopened")} 个，失效 {I("stale_candidates")} 个" };
            }
            return r;
        });
        BtnScan.IsEnabled = true;
        ScanText.Text = "立即检查记忆库";
    }

    // ====================== 记忆治理 ======================
    void LoadCandidates()
    {
        if (_candFilter == "decisions")
        {
            var ds = GovernanceStore.Decisions("pending");
            CandList.ItemsSource = ds;
            CandList.SelectedItem = ds.FirstOrDefault(d => d.CandidateId == _candId);
            CandEmpty.Text = "没有等待审批的结论";
            CandEmpty.Visibility = ds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            var cs = GovernanceStore.Candidates(_candFilter);
            CandList.ItemsSource = cs;
            CandList.SelectedItem = cs.FirstOrDefault(c => c.Id == _candId);
            CandEmpty.Text = _candFilter == "open" ? "没有待处理的候选\n点击右上角「立即检查记忆库」重新检查" : "没有记录";
            CandEmpty.Visibility = cs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        ShowCandidate();
        Hint("候选只是“可能需要整理”，不代表一定有问题；你可以直接处理，也可以等 AI 提交结论后审批。所有修改都保留历史版本。");
    }

    void CandList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        _candId = CandList.SelectedItem switch { GovCandidate c => c.Id, GovDecision d => d.CandidateId, _ => _candId };
        if (CandList.SelectedItem != null) ShowCandidate();
    }

    void ShowCandidate()
    {
        var c = _candId > 0 ? GovernanceStore.Candidates("all").FirstOrDefault(x => x.Id == _candId) : null;
        CandDetailScroll.Visibility = c != null ? Visibility.Visible : Visibility.Collapsed;
        CandDetailEmpty.Visibility = c != null ? Visibility.Collapsed : Visibility.Visible;
        CandDetail.Children.Clear();
        if (c == null) return;
        var p = CandDetail;
        p.Children.Add(Txt(c.Title, 16, bold: true));
        p.Children.Add(Txt($"{c.StatusText} · {c.RiskText} · 可见范围：{(c.Scope == "mixed" ? "跨模式（只能由你处理）" : c.Scope == "all" ? "所有模式" : c.Scope == "full" ? "全电脑访问" : "有限制")} · " +
                           $"发现于 {c.Created:yyyy-MM-dd HH:mm}{(c.TaskId.Length > 0 ? " · 任务 " + c.TaskId : "")}", 11.5, "TextSecondary"));
        p.Children.Add(Caption("规则依据"));
        p.Children.Add(Txt(c.Rule.Length > 0 ? c.Rule : c.Signals));

        p.Children.Add(Caption($"涉及的记忆（{c.MemoryIds.Count}）"));
        var mems = new List<MemoryItem>();
        foreach (var id in c.MemoryIds)
        {
            var m = MemoryStore.Get(id);
            if (m == null) { p.Children.Add(Box(Txt($"#{id} 已被永久删除", 12, "TextTertiary"))); continue; }
            mems.Add(m);
            var sp = new StackPanel();
            sp.Children.Add(Txt($"#{m.Id} · 版本 {m.Revision}{(m.StateText.Length > 0 ? " · " + m.StateText : "")}{(m.Pinned ? " · 置顶" : "")}{(m.Protected ? " · 受保护" : "")} · {m.ScopeText}", 11.5, "TextSecondary", true));
            sp.Children.Add(Code(m.Content));
            sp.Children.Add(Txt((m.HasTags ? "标签：" + m.Tags + " · " : "") + m.Meta, 11.5, "TextTertiary"));
            p.Children.Add(Box(sp));
        }

        var decisions = GovernanceStore.Decisions("", c.Id);
        if (decisions.Count > 0)
        {
            p.Children.Add(Caption("AI / 用户的结论"));
            foreach (var d in decisions)
            {
                var sp = new StackPanel();
                sp.Children.Add(Txt($"{d.ActionText}{(d.TargetId > 0 ? $"（保留 #{d.TargetId}）" : "")} · {d.StatusText} · {(d.ActorType == "ai" ? "AI " + d.Actor : d.ActorType == "user" ? "用户" : d.Actor)} · " +
                                    $"{d.Created:MM-dd HH:mm} · 置信度 {d.Confidence:0.##} · {(d.Risk == "user" ? "" : d.Risk == "high" ? "高风险" : d.Risk == "medium" ? "中风险" : "低风险")}", 12, bold: true));
                if (d.Reason.Length > 0) sp.Children.Add(Txt("理由：" + d.Reason, 12));
                if (d.Evidence.Length > 0) sp.Children.Add(Txt("依据：" + d.Evidence, 12, "TextSecondary"));
                if (d.Impact.Length > 0) sp.Children.Add(Txt("影响：" + d.Impact, 12, "TextSecondary"));
                if (d.NewContent.Length > 0) { sp.Children.Add(Txt("建议的新内容：", 11.5, "TextSecondary")); sp.Children.Add(Code(d.NewContent, 140)); }
                if (d.Status != "proposed" && d.Result.Length > 2) sp.Children.Add(Txt("结果：" + d.Result, 11.5, "TextTertiary"));
                if (d.Status == "proposed")
                {
                    var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                    var did = d.Id;
                    row.Children.Add(Btn("批准并应用", () => _ = Run("应用结论", () => GovernanceStore.ApplyAsync(did)), true));
                    row.Children.Add(Btn("驳回", () => _ = Run("驳回结论", () => GovernanceStore.RejectAsync(did, "用户驳回"))));
                    sp.Children.Add(row);
                }
                p.Children.Add(Box(sp, d.Status == "proposed" ? "#FFF8EC" : null));
            }
        }

        if (c.Status is not ("open" or "review_required")) return;
        p.Children.Add(Caption("直接处理（立即生效，旧内容保留为历史版本）", 18));
        var acts = new WrapPanel();
        var cid = c.Id;
        acts.Children.Add(Btn("保留（标记为已复审）", () => _ = Run("保留", () => GovernanceStore.UserDecideAsync(cid, "keep"))));
        acts.Children.Add(Btn("降权", () => _ = Run("降权", () => GovernanceStore.UserDecideAsync(cid, "lower_priority"))));
        acts.Children.Add(Btn("归档", () =>
        {
            if (Confirm("归档这些记忆？", "归档后 AI 默认搜索不到它们（可在「审核与回收站 → 全部记忆」中恢复）。", "归档"))
                _ = Run("归档", () => GovernanceStore.UserDecideAsync(cid, "archive"));
        }));
        acts.Children.Add(Btn("删除到回收站", () =>
        {
            if (Confirm("删除到回收站？", "这些记忆会移到回收站，AI 搜索不到；可以在「审核与回收站」中恢复或永久删除。", "删除", true))
                _ = Run("删除", () => GovernanceStore.UserDecideAsync(cid, "delete"));
        }, danger: true));
        acts.Children.Add(Btn("忽略此候选", () => _ = Run("忽略候选", () => GovernanceStore.UserDecideAsync(cid, "dismiss"))));
        p.Children.Add(acts);

        if (mems.Count == 0) return;
        bool multi = mems.Count > 1;
        p.Children.Add(Caption(multi ? "合并为一条（保留所选记忆，其余归档并标记“已合并”）" : "更正 / 压缩内容（作为新版本保存）", 14));
        ComboBox? target = null;
        var edit = new TextBox
        {
            Style = (Style)FindResource("EditBox"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = mems[0].Content, MaxLength = 2000,
        };
        if (multi)
        {
            target = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), FontSize = 12.5 };
            foreach (var m in mems) target.Items.Add(new ComboBoxItem { Content = $"保留 #{m.Id}：{Short(m.FirstLine, 24)}", Tag = m });
            target.SelectedIndex = 0;
            target.SelectionChanged += (_, _) => { if (target.SelectedItem is ComboBoxItem { Tag: MemoryItem m }) edit.Text = m.Content; };
            p.Children.Add(target);
        }
        p.Children.Add(edit);
        var row2 = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        row2.Children.Add(Btn(multi ? "合并" : "保存为新内容", () =>
        {
            var text = edit.Text.Trim();
            if (text.Length == 0) return;
            long tid = target?.SelectedItem is ComboBoxItem { Tag: MemoryItem tm } ? tm.Id : mems[0].Id;
            if (multi && !Confirm("合并这些记忆？", $"#{tid} 会更新为编辑框中的内容，其余 {mems.Count - 1} 条归档并标记为已合并（都可以恢复）。", "合并")) return;
            _ = Run(multi ? "合并" : "更正", () => GovernanceStore.UserDecideAsync(cid, multi ? "merge" : "correct", tid, text));
        }, true));
        p.Children.Add(row2);
    }

    static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    // ====================== 任务中心 ======================
    void LoadTasks()
    {
        var ts = GovernanceStore.Tasks(_taskFilter);
        TaskList.ItemsSource = ts;
        TaskList.SelectedItem = ts.FirstOrDefault(t => t.Id == _taskId);
        TaskEmpty.Text = _taskFilter switch { "active" => "没有进行中的任务", "attention" => "没有需要关注的任务", _ => "没有任务" };
        TaskEmpty.Visibility = ts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowTask();
        Hint("任务由 AI 登记（task_create）或由记忆库检查生成；同一任务同一时刻只会被一个 AI 领取，租约过期自动回到队列。");
    }

    void TaskList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is GovTask t) { _taskId = t.Id; ShowTask(); }
    }

    void ShowTask()
    {
        var t = TaskList.SelectedItem as GovTask;
        TaskDetail.Visibility = t != null ? Visibility.Visible : Visibility.Collapsed;
        TaskDetailEmpty.Visibility = t != null ? Visibility.Collapsed : Visibility.Visible;
        if (t == null) return;
        TaskTitle.Text = t.Title;
        TaskInfo.Text = t.Detail;
        TaskButtons.Children.Clear();
        var id = t.Id;
        if (t.NeedsApproval)
        {
            TaskButtons.Children.Add(Btn("批准执行", () => _ = Run("批准任务", () => GovernanceStore.TaskActionAsync(id, "approve")), true));
            TaskButtons.Children.Add(Btn("驳回", () => _ = Run("驳回任务", () => GovernanceStore.TaskActionAsync(id, "reject")), danger: true));
        }
        if (t.CanCancel)
            TaskButtons.Children.Add(Btn("取消任务", () =>
            {
                if (Confirm("取消这个任务？", "AI 之后对这个任务的提交会被拒绝。已经完成的修改不会撤销（可以在记忆的历史版本中恢复）。", "取消任务"))
                    _ = Run("取消任务", () => GovernanceStore.TaskActionAsync(id, "cancel"));
            }));
        if (t.CanRequeue)
            TaskButtons.Children.Add(Btn("重新排队", () => _ = Run("重新排队", () => GovernanceStore.TaskActionAsync(id, "requeue"))));
        TaskEvents.ItemsSource = GovernanceStore.TaskEvents(t.Id);
    }

    // ====================== 审核与回收站 ======================
    void LoadAudit()
    {
        bool log = _auditFilter == "log";
        AuditLog.Visibility = log ? Visibility.Visible : Visibility.Collapsed;
        if (log)
        {
            AuditList.ItemsSource = null;
            AuditEmpty.Text = "审计日志显示在右侧\n（记忆的每次创建、修改、合并、归档、删除、恢复）";
            AuditEmpty.Visibility = Visibility.Visible;
            AuditDetailScroll.Visibility = AuditDetailEmpty.Visibility = Visibility.Collapsed;
            AuditLog.ItemsSource = GovernanceStore.MemoryEvents(500);
            Hint("审计日志只追加不修改；永久删除的记忆只保留一条不含正文的记录。");
            return;
        }
        var list = MemoryStore.List("", null, _auditFilter == "trash" ? "trash" : "active");
        AuditList.ItemsSource = list;
        AuditList.SelectedItem = list.FirstOrDefault(m => m.Id == _auditMemId);
        AuditEmpty.Text = _auditFilter == "trash" ? "回收站是空的" : "还没有记忆";
        AuditEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowAuditMemory();
        Hint(_auditFilter == "trash" ? "回收站中的记忆 AI 搜索不到；恢复后回到记忆库，永久删除后无法找回。" : "选择一条记忆查看全部历史版本，可以恢复到任意版本（作为新版本保存）。");
    }

    void AuditList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (AuditList.SelectedItem is MemoryItem m) { _auditMemId = m.Id; ShowAuditMemory(); }
    }

    void ShowAuditMemory()
    {
        var m = AuditList.SelectedItem as MemoryItem;
        AuditDetailScroll.Visibility = m != null ? Visibility.Visible : Visibility.Collapsed;
        AuditDetailEmpty.Visibility = m != null ? Visibility.Collapsed : Visibility.Visible;
        AuditDetail.Children.Clear();
        if (m == null) return;
        var p = AuditDetail;
        p.Children.Add(Txt($"记忆 #{m.Id}", 16, bold: true));
        p.Children.Add(Txt(m.Detail, 11.5, "TextSecondary"));
        p.Children.Add(Code(m.Content, 200));
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var id = m.Id;
        if (m.DeletedAt != null || m.IsArchived)
            row.Children.Add(Btn(m.DeletedAt != null ? "恢复到记忆库" : "取消归档", () => Local($"恢复记忆 #{id}", () => MemoryStore.Restore(id)), true));
        if (m.DeletedAt != null)
            row.Children.Add(Btn("永久删除", () =>
            {
                if (Confirm("永久删除这条记忆？", "正文和全部历史版本都会被清除，无法恢复。审计日志中只保留一条不含正文的记录。", "永久删除", true))
                    Local($"永久删除记忆 #{id}", () => MemoryStore.Purge(id));
            }, true, true));
        if (m.DeletedAt == null)
            row.Children.Add(Btn(m.Protected ? "取消保护" : "标记为受保护", () => Local(m.Protected ? $"取消保护 #{id}" : $"保护 #{id}", () => MemoryStore.SetProtected(id, !m.Protected))));
        p.Children.Add(row);
        var vs = MemoryStore.Versions(m.Id);
        p.Children.Add(Caption($"历史版本（{vs.Count}，新→旧）"));
        foreach (var v in vs)
        {
            var sp = new StackPanel();
            sp.Children.Add(Txt(v.Header + (v.Reason.Length > 0 ? " · " + v.Reason : ""), 11.5, "TextSecondary", true));
            sp.Children.Add(Code(v.Content, 120));
            if (v.Revision != m.Revision && v.Content != m.Content)
            {
                var rev = v.Revision;
                sp.Children.Add(Btn($"恢复到版本 {rev}", () =>
                {
                    if (Confirm($"恢复到版本 {rev}？", "当前内容会保存为历史版本，然后用该版本的内容生成一个新版本。", "恢复"))
                        Local($"记忆 #{id} 恢复到版本 {rev}", () => MemoryStore.Restore(id, rev));
                }));
            }
            p.Children.Add(Box(sp));
        }
    }

    // ====================== 策略设置 ======================
    sealed record Field(string Key, string Label, string Desc, string Kind, string[]? Options = null, string[]? OptionText = null);

    static readonly (string Group, Field[] Fields)[] PolicySpec =
    {
        ("任务", new Field[]
        {
            new("task.level", "任务模式", "轻量：不提示；标准：连续修改时提示 AI 登记任务；严格：下面列出的工具必须先 task_create 登记任务", "choice",
                new[] { "light", "standard", "strict" }, new[] { "轻量", "标准", "严格" }),
            new("task.require_approval", "任务需要批准", "严格模式下，AI 登记的任务需要你在「任务中心」批准后才能执行修改类工具", "bool"),
            new("task.strict_tools", "严格模式管控的工具", "逗号分隔", "text"),
            new("task.lease_minutes", "任务无活动超时（分钟）", "AI 登记的任务超过这么久没有活动会被搁置", "int"),
            new("task.hint_writes", "提示登记任务的连续修改次数", "标准模式下，没有登记任务时连续修改达到这个次数提示一次", "int"),
        }),
        ("记忆维护", new Field[]
        {
            new("review.enabled", "定时检查记忆库", "ZERO 运行时按周期用本地规则检查（不调用模型）", "bool"),
            new("review.cycle_days", "检查周期（天）", "", "int"),
            new("review.inactive_days", "长期未被检索（天）", "超过这么久 AI 没有搜到过的记忆列为候选", "int"),
            new("review.stale_days", "长期未更新（天）", "", "int"),
            new("review.max_len", "内容过长（字）", "超过这个长度建议压缩", "int"),
            new("review.similarity", "近似重复阈值（0~1）", "越大越严格", "float"),
            new("review.batch_limit", "每次最多生成候选数", "", "int"),
            new("review.recheck_days", "处理后多久再检查（天）", "保留或忽略的候选在这段时间内不会再次出现", "int"),
            new("review.protected_tags", "受保护的标签", "带这些标签的记忆不会因“长期未用”被列为候选，且不能被 AI 删除（逗号分隔）", "text"),
            new("review.auto_apply", "自动生效的结论", "AI 提交的这些低风险结论直接生效（可选：keep, lower_priority）；其余都需要你审批", "text"),
            new("review.passive_hint", "空闲时提示 AI", "有待处理的维护任务时，偶尔在工具结果末尾提醒 AI 可以领取（可选，不强制）", "bool"),
            new("hint.minutes", "提示间隔（分钟）", "同一 AI 客户端两次提示之间的最短间隔", "int"),
            new("review.lease_minutes", "维护任务租约（分钟）", "AI 领取后多久没有提交或续期，任务回到队列", "int"),
            new("review.max_attempts", "最多尝试次数", "超过后任务标记为失败，等待你处理", "int"),
            new("review.scan_max_items", "每次检查的记忆上限", "", "int"),
        }),
        ("视觉工作台与浏览器", new Field[]
        {
            new("ui.grant_minutes", "窗口授权默认时长（分钟）", "", "int"),
            new("ui.max_actions_per_min", "每分钟最多操作次数", "ui_action / browser_action", "int"),
            new("ui.max_captures_per_min", "每分钟最多截图次数", "", "int"),
            new("ui.record_max_seconds", "单次录制最长（秒）", "", "int"),
            new("ui.capture_keep", "保留的截图数量", "", "int"),
            new("ui.lock_seconds", "窗口操作锁（秒）", "一个 AI 操作窗口后，其他 AI 在这段时间内不能操作同一窗口", "int"),
            new("browser.allow", "浏览器允许的主机", "逗号分隔，例如 localhost,127.0.0.1,*.example.com；其他地址的请求都会被拦截", "text"),
            new("browser.headless", "浏览器后台运行", "关闭后浏览器窗口会显示在桌面上（需要已登录的桌面）", "bool"),
        }),
    };

    void LoadPolicy()
    {
        var pol = GovernanceStore.Policy();
        PolicyHost.Children.Clear();
        _policyInputs.Clear();
        foreach (var (group, fields) in PolicySpec)
        {
            PolicyHost.Children.Add(new TextBlock { Text = group, Style = (Style)FindResource("Caption"), Margin = new Thickness(4, PolicyHost.Children.Count == 0 ? 0 : 18, 0, 8) });
            var list = new StackPanel();
            foreach (var f in fields)
            {
                if (list.Children.Count > 0)
                    list.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF0)), Margin = new Thickness(0, 10, 0, 10) });
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                left.Children.Add(new TextBlock { Text = f.Label, FontSize = 13, FontWeight = FontWeights.SemiBold });
                if (f.Desc.Length > 0) left.Children.Add(new TextBlock { Text = f.Desc, FontSize = 11.5, Foreground = (Brush)FindResource("TextSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
                g.Children.Add(left);
                var v = pol.GetValueOrDefault(f.Key, "");
                FrameworkElement input;
                switch (f.Kind)
                {
                    case "bool":
                        input = new CheckBox { Style = (Style)FindResource("SwitchSafe"), IsChecked = v is "1" or "true", VerticalAlignment = VerticalAlignment.Center };
                        break;
                    case "choice":
                    {
                        var sp = new StackPanel { Orientation = Orientation.Horizontal };
                        for (int i = 0; i < f.Options!.Length; i++)
                            sp.Children.Add(new RadioButton { Style = (Style)FindResource("Segment"), GroupName = f.Key, Content = f.OptionText![i], Tag = f.Options[i], IsChecked = v == f.Options[i] });
                        input = new Border { Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xE9, 0xEE)), CornerRadius = new CornerRadius(8), Child = sp, Tag = sp };
                        break;
                    }
                    default:
                        input = new TextBox { Style = (Style)FindResource("EditBox"), Text = v, Width = f.Kind == "text" ? 320 : 120, VerticalAlignment = VerticalAlignment.Center };
                        break;
                }
                input.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(input, 1);
                g.Children.Add(input);
                _policyInputs[f.Key] = input;
                list.Children.Add(g);
            }
            PolicyHost.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 14, 16, 14), Child = list });
        }
        Hint("保存后立即生效（MCP 服务约 2 秒内读到新策略），不需要重启服务。");
    }

    void PolicySave_Click(object s, RoutedEventArgs e)
    {
        var pol = GovernanceStore.Policy();
        var changes = new Dictionary<string, string>();
        foreach (var (_, fields) in PolicySpec)
            foreach (var f in fields)
            {
                if (!_policyInputs.TryGetValue(f.Key, out var input)) continue;
                string v;
                switch (f.Kind)
                {
                    case "bool": v = ((CheckBox)input).IsChecked == true ? "1" : "0"; break;
                    case "choice": v = ((StackPanel)((Border)input).Tag).Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? ""; break;
                    default: v = ((TextBox)input).Text.Trim(); break;
                }
                if (f.Kind == "int" && (!int.TryParse(v, out var n) || n < 0 || n > 100000))
                { ZeroDialog.Show(this, new DialogSpec("无法保存", $"「{f.Label}」需要 0~100000 之间的整数", DialogKind.Warning, "好", null)); return; }
                if (f.Kind == "float" && (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0 || d > 1))
                { ZeroDialog.Show(this, new DialogSpec("无法保存", $"「{f.Label}」需要 0~1 之间的小数", DialogKind.Warning, "好", null)); return; }
                if (f.Key == "review.auto_apply" && v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(a => a is not ("keep" or "lower_priority")))
                { ZeroDialog.Show(this, new DialogSpec("无法保存", "自动生效的结论只能是 keep、lower_priority（合并、删除等必须由你审批）", DialogKind.Warning, "好", null)); return; }
                if (pol.GetValueOrDefault(f.Key, "") != v) changes[f.Key] = v;
            }
        if (changes.Count == 0) { Hint("没有修改"); return; }
        try
        {
            foreach (var (k, v) in changes) GovernanceStore.Set(k, v);
            AppLog.Write("治理", "策略已修改：" + string.Join("，", changes.Select(c => $"{c.Key}={c.Value}")));
            Hint($"已保存 {changes.Count} 项，立即生效。");
        }
        catch (Exception ex)
        {
            AppLog.Error("治理", ex);
            ZeroDialog.Show(this, new DialogSpec("保存失败", ex.Message, DialogKind.Error, "好", null));
        }
    }

    void PolicyReset_Click(object s, RoutedEventArgs e)
    {
        if (!Confirm("恢复默认策略？", "任务模式、记忆维护、视觉工作台的所有策略都会恢复为默认值（不影响已有的记忆和任务）。", "恢复默认")) return;
        try { GovernanceStore.ResetPolicy(); AppLog.Write("治理", "策略已恢复默认"); }
        catch (Exception ex) { ZeroDialog.Show(this, new DialogSpec("恢复失败", ex.Message, DialogKind.Error, "好", null)); }
        LoadPolicy();
        Hint("已恢复默认策略。");
    }

    // ====================== 窗口按钮 / 调试截图 ======================
    void BtnClose_Click(object s, RoutedEventArgs e) => Close();
    void BtnMin_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void BtnMax_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>调试截图：选中第一项（候选 / 任务 / 记忆）</summary>
    public void SelectFirstForPreview()
    {
        if (_tab == "review" && CandList.Items.Count > 0) CandList.SelectedIndex = 0;
        if (_tab == "tasks" && TaskList.Items.Count > 0) TaskList.SelectedIndex = 0;
        if (_tab == "audit" && AuditList.Items.Count > 0) AuditList.SelectedIndex = 0;
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
