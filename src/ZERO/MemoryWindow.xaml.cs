using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ZERO.Services;

namespace ZERO;

/// <summary>记忆库：查看、搜索、编辑 AI 和用户保存的长期记忆；可置顶（连接时直接告诉 AI）、设置可见范围。</summary>
public partial class MemoryWindow : UserControl, ISizeTierAware
{
    readonly AppSettings _settings;
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly HashSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);
    bool _suppress, _draft;
    MemoryItem? _mem;

    public MemoryWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        BtnDataDir.ToolTip = $"记忆库保存在这台电脑的当前用户目录下，更新 ZERO 或移动程序位置都不会丢失：\n{System.IO.Path.GetFullPath(MemoryStore.DbPath)}";
        EnableSwitch.IsChecked = settings.MemoryEnabled;
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Reload(); };
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded && !Editing) Reload(); };
        Loaded += (_, _) => Reload();
    }

    bool Editing => ContentEdit.IsKeyboardFocusWithin || TagsEdit.IsKeyboardFocusWithin;

    void Reload(long? select = null)
    {
        try
        {
            _suppress = true;
            RefreshTagChrome();
            var list = MemoryStore.List(Search.Text.Trim(), null);
            if (_tags.Count > 0)
            {
                // 多选：任一匹配（OR），不改后端
                list = list.Where(m =>
                {
                    var parts = m.Tags.Split(new[] { ',', '\uFF0C' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    return parts.Any(p => _tags.Contains(p));
                }).ToList();
            }
            MainList.ItemsSource = list;
            var id = select ?? _mem?.Id;
            MainList.SelectedItem = list.FirstOrDefault(m => m.Id == id);
            ListTitle.Text = $"记忆 · {list.Count}";
            ListEmpty.Text = Search.Text.Trim().Length > 0 || _tags.Count > 0
                ? "没有找到匹配的记忆"
                : "还没有记忆\nAI 了解到值得长期记住的信息时会保存在这里，\n你也可以点击「新建记忆」手动添加";
            ListEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _suppress = false;
            ShowSelection();
        }
        catch (Exception ex)
        {
            _suppress = false;
            AppLog.Error("记忆", ex);
            ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("无法读取记忆库", ex.Message, DialogKind.Error, "好", null));
        }
    }

    void RefreshTagChrome()
    {
        var all = MemoryStore.AllTags();
        _tags.RemoveWhere(tag => all.All(t => !string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase)));

        int count = _tags.Count;
        BtnTagFilter.Content = count == 0 ? "标签" : $"标签 · {count}";

        SelectedTagChips.Children.Clear();
        if (count == 0)
        {
            SelectedTagRow.Visibility = Visibility.Collapsed;
            BtnMoreTags.Visibility = Visibility.Collapsed;
            return;
        }
        SelectedTagRow.Visibility = Visibility.Visible;
        const int maxChips = 6;
        int shown = 0;
        foreach (var tag in _tags.OrderBy(x => x))
        {
            if (shown >= maxChips) break;
            var chipTag = tag;
            var label = new TextBlock { Text = tag, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Accent") };
            var close = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 22, Height = 22, Margin = new Thickness(2, 0, 0, 0), ToolTip = "移除",
                Content = new Path { Style = (Style)FindResource("IconPresenter"), Width = 9, Height = 9, Data = (Geometry)FindResource("Icon.Close") }
            };
            close.Click += (_, _) => { _tags.Remove(chipTag); Reload(); };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(label);
            sp.Children.Add(close);
            SelectedTagChips.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF1, 0xFF)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 4, 2),
                Margin = new Thickness(0, 0, 6, 0),
                Child = sp
            });
            shown++;
        }
        int more = count - shown;
        BtnMoreTags.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (more > 0) BtnMoreTags.Content = $"+{more}";
    }

    void TagFilter_Click(object s, RoutedEventArgs e)
    {
        BuildTagPopupList();
        TagPopup.IsOpen = true;
        TagSearchBox.Focus();
    }

    void BuildTagPopupList()
    {
        var filter = TagSearchBox.Text.Trim();
        TagSearchHint.Visibility = filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TagPopupList.Children.Clear();
        foreach (var (tag, n) in MemoryStore.AllTags())
        {
            if (filter.Length > 0 && tag.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var row = new CheckBox
            {
                Content = $"{tag} · {n}",
                IsChecked = _tags.Contains(tag),
                Margin = new Thickness(2, 3, 2, 3),
                FontSize = 12.5,
                Tag = tag,
            };
            row.Checked += TagCheck_Changed;
            row.Unchecked += TagCheck_Changed;
            TagPopupList.Children.Add(row);
        }
    }

    void TagCheck_Changed(object s, RoutedEventArgs e)
    {
        if (s is not CheckBox cb || cb.Tag is not string tag) return;
        if (cb.IsChecked == true) _tags.Add(tag);
        else _tags.Remove(tag);
        Reload();
        BtnTagFilter.Content = _tags.Count == 0 ? "标签" : $"标签 · {_tags.Count}";
    }

    void TagSearch_Changed(object s, TextChangedEventArgs e) => BuildTagPopupList();

    void ClearTags_Click(object s, RoutedEventArgs e)
    {
        _tags.Clear();
        TagPopup.IsOpen = false;
        Reload();
    }

    public void SnapshotSelectTags(IEnumerable<string> tags, bool openPopup = false)
    {
        _tags.Clear();
        foreach (var x in tags)
            if (!string.IsNullOrWhiteSpace(x)) _tags.Add(x.Trim());
        Reload();
        if (openPopup) { BuildTagPopupList(); TagPopup.IsOpen = true; }
    }

    void MainList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _draft = false;
        ShowSelection();
    }

    void ShowSelection()
    {
        if (!_draft) _mem = MainList.SelectedItem as MemoryItem;
        bool any = _mem != null || _draft;
        Detail.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        DetailEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any) return;
        if (_draft)
        {
            DetailTitle.Text = "新建记忆";
            MetaText.Text = "写好内容后点击其他地方即自动保存";
            MemActions.Visibility = Visibility.Collapsed;
            if (!Editing) { ContentEdit.Text = ""; TagsEdit.Text = _tags.Count == 1 ? _tags.First() : ""; }
            SetScopeButtons("all");
        }
        else
        {
            var m = _mem!;
            DetailTitle.Text = $"记忆 #{m.Id}";
            MetaText.Text = m.Detail;
            MemActions.Visibility = Visibility.Visible;
            PinIcon.Stroke = m.Pinned ? (Brush)FindResource("Accent") : (Brush)FindResource("TextSecondary");
            BtnPin.ToolTip = m.Pinned ? "取消置顶" : "置顶：AI 连接时直接告诉它（无需搜索），且 AI 不能删除";
            ProtectIcon.Stroke = m.Protected ? (Brush)FindResource("Accent") : (Brush)FindResource("TextSecondary");
            BtnProtect.ToolTip = m.Protected ? "取消保护" : "受保护：AI 不能删除，记忆维护时不会因为长期未用被列为候选";
            if (!ContentEdit.IsKeyboardFocusWithin) ContentEdit.Text = m.Content;
            if (!TagsEdit.IsKeyboardFocusWithin) TagsEdit.Text = m.Tags;
            SetScopeButtons(m.Scope);
        }
        ContentHint.Visibility = ContentEdit.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetScopeButtons(string scope)
    {
        foreach (RadioButton rb in ScopeGroup.Children) rb.IsChecked = (string)rb.Tag == scope;
    }

    string SelectedScope => ScopeGroup.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "all";

    void New_Click(object s, RoutedEventArgs e)
    {
        _draft = true; _mem = null;
        _suppress = true; MainList.SelectedItem = null; _suppress = false;
        ContentEdit.Text = ""; TagsEdit.Text = _tags.Count == 1 ? _tags.First() : "";
        ShowSelection();
        ContentEdit.Focus();
    }

    void Edit_LostFocus(object s, KeyboardFocusChangedEventArgs e)
    {
        ContentHint.Visibility = ContentEdit.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Editing) return;
        var content = ContentEdit.Text.Trim();
        if (_draft)
        {
            if (content.Length == 0) return;
            long id = 0;
            Try(() => id = MemoryStore.Add(content, TagsEdit.Text, SelectedScope));
            _draft = false;
            FlashSaved();
            Reload(id);
            return;
        }
        if (_mem == null) return;
        if (content.Length == 0)
        {
            ContentEdit.Text = _mem.Content;
            return;
        }
        if (content == _mem.Content && MemoryStore.CleanTags(TagsEdit.Text) == _mem.Tags) return;
        var mid = _mem.Id;
        Try(() => MemoryStore.Update(mid, content, TagsEdit.Text));
        FlashSaved();
        Reload(mid);
    }

    void TagsEdit_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { MainList.Focus(); e.Handled = true; }
    }

    void Scope_Click(object s, RoutedEventArgs e)
    {
        if (_mem == null) return;
        var id = _mem.Id;
        Try(() => MemoryStore.SetScope(id, SelectedScope));
        FlashSaved();
        Reload(id);
    }

    void Pin_Click(object s, RoutedEventArgs e)
    {
        if (_mem == null) return;
        var (id, pin) = (_mem.Id, !_mem.Pinned);
        Try(() => MemoryStore.SetPinned(id, pin));
        Reload(id);
    }

    void Protect_Click(object s, RoutedEventArgs e)
    {
        if (_mem == null) return;
        var (id, on) = (_mem.Id, !_mem.Protected);
        Try(() => MemoryStore.SetProtected(id, on));
        Reload(id);
    }

    void Delete_Click(object s, RoutedEventArgs e)
    {
        if (_mem == null) return;
        if (!ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("把这条记忆移到回收站？", $"「{Short(_mem.FirstLine)}」会移到回收站，AI 以后搜不到它。\n可以在「治理 → 审核与回收站」中恢复或永久删除。",
                DialogKind.Danger, "移到回收站"))) return;
        var id = _mem.Id;
        Try(() => MemoryStore.Delete(id));
        _mem = null;
        Reload();
    }

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    void FlashSaved()
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(120)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(1300)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1700)));
        SavedTip.BeginAnimation(OpacityProperty, a);
    }

    void Search_Changed(object? s, TextChangedEventArgs e)
    {
        if (_suppress || !IsLoaded) return;
        _searchDelay.Stop(); _searchDelay.Start();
    }

    void Search_Go(object? s, EventArgs e)
    {
        _searchDelay.Stop();
        Reload();
    }

    void Refresh_Click(object s, RoutedEventArgs e) => Reload();

    void Setting_Changed(object s, RoutedEventArgs e)
    {
        _settings.MemoryEnabled = EnableSwitch.IsChecked == true;
        _settings.Save();
        SettingHint.Visibility = Visibility.Visible;
        AppLog.Write("设置", $"记忆库：启用={_settings.MemoryEnabled}");
    }

    void DataDir_Click(object s, RoutedEventArgs e)
    {
        var db = System.IO.Path.GetFullPath(MemoryStore.DbPath);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                System.IO.File.Exists(db) ? $"/select,\"{db}\"" : $"\"{System.IO.Path.GetDirectoryName(db)}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Error("记忆", ex); }
    }

    void Try(Action a)
    {
        try { a(); }
        catch (Exception ex)
        {
            AppLog.Error("记忆", ex);
            ZeroDialog.Show(Window.GetWindow(this) ?? Application.Current.MainWindow, new DialogSpec("操作失败", ex.Message, DialogKind.Error, "好", null));
        }
    }

    public void SaveSnapshot(string path)
    {
        var root = (FrameworkElement)Content;
        int w = (int)(root.ActualWidth + root.Margin.Left + root.Margin.Right);
        int h = (int)(root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, w * 2), Math.Max(1, h * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    public void SelectFirstForPreview()
    {
        if (MainList.Items.Count > 0) MainList.SelectedIndex = 0;
    }

    public void ApplySizeTier(string tier, double listCol, double visionList, double shotMin, double gap)
    {
        if (ListCol != null) ListCol.Width = new GridLength(listCol);
        if (ListGap != null) ListGap.Width = new GridLength(gap);
    }
}