using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ZERO.Services;

namespace ZERO;

/// <summary>记忆库：查看、搜索、编辑 AI 和用户保存的长期记忆；可置顶（连接时直接告诉 AI）、设置可见范围。</summary>
public partial class MemoryWindow : Window
{
    readonly AppSettings _settings;
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    bool _suppress, _draft;
    string? _tag;
    MemoryItem? _mem;

    public MemoryWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        BtnDataDir.ToolTip = $"记忆库保存在这台电脑的当前用户目录下，更新 ZERO 或移动程序位置都不会丢失：\n{System.IO.Path.GetFullPath(MemoryStore.DbPath)}";
        EnableSwitch.IsChecked = settings.MemoryEnabled;
        WriteSwitch.IsChecked = settings.MemoryWrite;
        WriteSwitch.IsEnabled = settings.MemoryEnabled;
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Reload(); };
        StateChanged += (_, _) => Shell.Margin = WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(12);
        Activated += (_, _) => { if (IsLoaded && !Editing) Reload(); };
        Loaded += (_, _) => Reload();
    }

    bool Editing => ContentEdit.IsKeyboardFocusWithin || TagsEdit.IsKeyboardFocusWithin;

    void Reload(long? select = null)
    {
        try
        {
            _suppress = true;
            BuildTagBar();
            var list = MemoryStore.List(SearchBox.Text.Trim(), _tag);
            MainList.ItemsSource = list;
            var id = select ?? _mem?.Id;
            MainList.SelectedItem = list.FirstOrDefault(m => m.Id == id);
            ListTitle.Text = $"记忆 · {list.Count}";
            ListEmpty.Text = SearchBox.Text.Trim().Length > 0 || _tag != null
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
            ZeroDialog.Show(this, new DialogSpec("无法读取记忆库", ex.Message, DialogKind.Error, "好", null));
        }
    }

    void BuildTagBar()
    {
        var tags = MemoryStore.AllTags();
        if (_tag != null && tags.All(t => t.Tag != _tag)) _tag = null;
        TagBar.Children.Clear();
        TagBar.Visibility = tags.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        void Add(string text, string? tag)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "Tag", Content = text, Tag = tag,
                IsChecked = _tag == tag, Margin = new Thickness(0, 0, 6, 6),
            };
            rb.Click += (_, _) => { _tag = (string?)rb.Tag; Reload(); };
            TagBar.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xE9, 0xEE)), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 0), Child = rb });
            rb.Margin = new Thickness(0);
        }
        Add("全部标签", null);
        foreach (var (t, n) in tags.Take(20)) Add($"{t} · {n}", t);
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
            if (!Editing) { ContentEdit.Text = ""; TagsEdit.Text = _tag ?? ""; }
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

    // ====================== 编辑 ======================
    void New_Click(object s, RoutedEventArgs e)
    {
        _draft = true; _mem = null;
        _suppress = true; MainList.SelectedItem = null; _suppress = false;
        ContentEdit.Text = ""; TagsEdit.Text = _tag ?? "";
        ShowSelection();
        ContentEdit.Focus();
    }

    void Edit_LostFocus(object s, KeyboardFocusChangedEventArgs e)
    {
        ContentHint.Visibility = ContentEdit.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Editing) return;    // 焦点在内容和标签之间切换时先不保存
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
            ContentEdit.Text = _mem.Content;    // 不允许清空；要删除请用删除按钮
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

    void Delete_Click(object s, RoutedEventArgs e)
    {
        if (_mem == null) return;
        if (!ZeroDialog.Show(this, new DialogSpec("删除这条记忆？", $"「{Short(_mem.FirstLine)}」将被永久删除，AI 以后也搜不到它。",
                DialogKind.Danger, "删除"))) return;
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

    // ====================== 工具栏与设置 ======================
    void Search_Changed(object s, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_suppress || !IsLoaded) return;
        _searchDelay.Stop(); _searchDelay.Start();
    }

    void Refresh_Click(object s, RoutedEventArgs e) => Reload();

    void Setting_Changed(object s, RoutedEventArgs e)
    {
        _settings.MemoryEnabled = EnableSwitch.IsChecked == true;
        _settings.MemoryWrite = WriteSwitch.IsChecked == true;
        WriteSwitch.IsEnabled = _settings.MemoryEnabled;
        _settings.Save();
        SettingHint.Visibility = Visibility.Visible;
        AppLog.Write("设置", $"记忆库：启用={_settings.MemoryEnabled} AI 可写入={_settings.MemoryWrite}");
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
            ZeroDialog.Show(this, new DialogSpec("操作失败", ex.Message, DialogKind.Error, "好", null));
        }
    }

    // ====================== 窗口按钮 ======================
    void BtnClose_Click(object s, RoutedEventArgs e) => Close();
    void BtnMin_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void BtnMax_Click(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // 调试用：渲染当前窗口为 PNG（--snapshot-memory）
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

    public void SelectFirstForPreview()
    {
        if (MainList.Items.Count > 0) MainList.SelectedIndex = 0;
    }
}
