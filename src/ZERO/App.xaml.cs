using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace ZERO;

public partial class App : Application
{
    static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // MCP 服务的界面自动化辅助进程：ZERO.exe --uia-helper（标准输入/输出逐行 JSON，不显示窗口、不占用单实例锁）
        if (Array.IndexOf(e.Args, "--uia-helper") >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = 1;
            try { code = Services.UiaProvider.HelperMain(); }
            catch (Exception ex) { Services.AppLog.Error("界面自动化", ex); }
            Shutdown(code);
            return;
        }
        // 调试用：ZERO.exe --snapshot-dialog=目录  把各个对话框渲染为 PNG 后退出（不占用单实例锁）
        var dlgArg = Array.Find(e.Args, a => a.StartsWith("--snapshot-dialog="));
        if (dlgArg != null)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var dir = dlgArg["--snapshot-dialog=".Length..].Trim('"');
            ZeroDialog.RenderPreview(ZERO.MainWindow.TrustPrompt, System.IO.Path.Combine(dir, "dialog-trust.png"));
            ZeroDialog.RenderPreview(ZERO.MainWindow.RegenPrompt, System.IO.Path.Combine(dir, "dialog-regen.png"));
            ZeroDialog.RenderPreview(ZERO.MainWindow.ExitPrompt, System.IO.Path.Combine(dir, "dialog-exit.png"));
            ZeroDialog.RenderPreview(ZERO.MainWindow.StopFirstPrompt, System.IO.Path.Combine(dir, "dialog-info.png"));
            ZeroDialog.RenderPreview(new DialogSpec("ZERO 出错", "示例错误信息。", DialogKind.Error, "好", null), System.IO.Path.Combine(dir, "dialog-error.png"));
            Shutdown();
            return;
        }
        // 调试用：ZERO.exe --snapshot-history=路径.png  打开历史窗口并截图后退出（可用环境变量 ZERO_HISTORY_DB 指定测试数据库）
        var histArg = Array.Find(e.Args, a => a.StartsWith("--snapshot-history="));
        if (histArg != null)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var path = histArg["--snapshot-history=".Length..].Trim('"');
            var page = new HistoryWindow(Services.AppSettings.Load());
            var w = HostPageSnapshot(page);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            int step = 0;
            t.Tick += (_, _) =>
            {
                if (step++ == 0) { page.SelectFirstForPreview(path.Contains("-tool") ? 0 : 2, path.Contains("-tool")); return; }
                t.Stop();
                page.SaveSnapshot(path);
                Shutdown();
            };
            t.Start();
            return;
        }
        // 调试用：ZERO.exe --snapshot-memory=路径.png（可用环境变量 ZERO_MEMORY_DB 指定测试数据库）
        var memArg = Array.Find(e.Args, a => a.StartsWith("--snapshot-memory="));
        if (memArg != null)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var path = memArg["--snapshot-memory=".Length..].Trim('"');
            var page = new MemoryWindow(Services.AppSettings.Load());
            var w = HostPageSnapshot(page);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            int step = 0;
            t.Tick += (_, _) =>
            {
                if (step++ == 0) { page.SelectFirstForPreview(); return; }
                t.Stop();
                page.SaveSnapshot(path);
                Shutdown();
            };
            t.Start();
            return;
        }
        // 调试用：ZERO.exe --snapshot-permissions=路径.png（文件名含 -full 时显示全电脑访问模式）
        var permArg = Array.Find(e.Args, a => a.StartsWith("--snapshot-permissions="));
        if (permArg != null)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var path = permArg["--snapshot-permissions=".Length..].Trim('"');
            var page = new PermissionWindow(Services.AppSettings.Load(), null);
            page.SelectTab(path.Contains("-full"));
            var w = HostPageSnapshot(page);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            t.Tick += (_, _) => { t.Stop(); page.SaveSnapshot(path); Shutdown(); };
            t.Start();
            return;
        }
        // 调试用：ZERO.exe --snapshot-governance=路径.png（文件名含 -review / -tasks / -audit / -policy 时显示对应页签；
        // 可用环境变量 ZERO_MEMORY_DB、ZERO_GOV_DB 指定测试数据库）
        var govArg = Array.Find(e.Args, a => a.StartsWith("--snapshot-governance="));
        if (govArg != null)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var path = govArg["--snapshot-governance=".Length..].Trim('"');
            var page = new GovernanceWindow(Services.AppSettings.Load());
            var w = HostPageSnapshot(page, 1200, 800);
            var tab = new[] { "review", "tasks", "audit", "policy" }.FirstOrDefault(k => path.Contains("-" + k)) ?? "overview";
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            int step = 0;
            t.Tick += (_, _) =>
            {
                if (step == 0) page.SelectTab(tab);
                else if (step == 1) page.SelectFirstForPreview();
                else { t.Stop(); page.SaveSnapshot(path); Shutdown(); }
                step++;
            };
            t.Start();
            return;
        }
        // 调试用：ZERO.exe --snapshot-vision=路径.png [--select=标题关键字]
        var visArg = Array.Find(e.Args, a => a.StartsWith("--snapshot-vision="));
        if (visArg != null)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var path = visArg["--snapshot-vision=".Length..].Trim('"');
            var sel = Array.Find(e.Args, a => a.StartsWith("--select="))?["--select=".Length..].Trim('"');
            var page = new VisionWindow(Services.AppSettings.Load(), null);
            var w = HostPageSnapshot(page, 1200, 800);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            int step = 0;
            t.Tick += (_, _) =>
            {
                if (step++ == 0) { page.SelectFirstForPreview(sel); return; }
                t.Stop(); page.SaveSnapshot(path); Shutdown();
            };
            t.Start();
            return;
        }

        // 调试用：ZERO.exe --snapshot-main=路径.png [--page=...] [--width=] [--height=]
        // 可选：--gov-tab=overview|review|tasks|audit|policy  --audit-filter=trash|all|log
        //       --memory-tags=tag1,tag2  --memory-tag-popup=1
        var mainArg = Array.Find(e.Args, a => a.StartsWith("--snapshot-main="));
        if (mainArg != null)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var path = mainArg["--snapshot-main=".Length..].Trim('"');
            var page = Array.Find(e.Args, a => a.StartsWith("--page="))?["--page=".Length..].Trim('"') ?? "overview";
            var govTab = Array.Find(e.Args, a => a.StartsWith("--gov-tab="))?["--gov-tab=".Length..].Trim('"');
            var auditFilter = Array.Find(e.Args, a => a.StartsWith("--audit-filter="))?["--audit-filter=".Length..].Trim('"');
            var memTags = Array.Find(e.Args, a => a.StartsWith("--memory-tags="))?["--memory-tags=".Length..].Trim('"');
            bool memPopup = Array.Exists(e.Args, a => a == "--memory-tag-popup=1" || a == "--memory-tag-popup");
            var w = new MainWindow();
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -4000; w.Top = 0; w.ShowActivated = false; w.ShowInTaskbar = false;
            var widthArg = Array.Find(e.Args, a => a.StartsWith("--width="));
            var heightArg = Array.Find(e.Args, a => a.StartsWith("--height="));
            if (widthArg != null && double.TryParse(widthArg["--width=".Length..], out var ww)) w.Width = ww;
            if (heightArg != null && double.TryParse(heightArg["--height=".Length..], out var hh)) w.Height = hh;
            w.Show();
            w.ApplySizeTierPublic();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            int step = 0;
            t.Tick += (_, _) =>
            {
                if (step == 0) { w.Navigate(page); step++; return; }
                if (step == 1)
                {
                    w.ApplySnapshotExtras(govTab, auditFilter, memTags, memPopup);
                    step++;
                    return;
                }
                t.Stop(); w.SaveSnapshot(path); Shutdown();
            };
            t.Start();
            return;
        }
        _mutex = new Mutex(true, "ZERO_SingleInstance_8f3a", out bool isNew);
        if (!isNew)
        {
            ZeroDialog.Show(null, new DialogSpec("ZERO 已在运行", "请在系统托盘中找到它。", DialogKind.Info, "好", null));
            Shutdown();
            return;
        }
        Services.AppLog.SessionHeader();
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Services.AppLog.Write("崩溃", args.ExceptionObject?.ToString() ?? "");
        DispatcherUnhandledException += (_, args) =>
        {
            Services.AppLog.Error("界面", args.Exception);
            ZeroDialog.Show(Current.MainWindow, new DialogSpec("ZERO 出错", args.Exception.Message, DialogKind.Error, "好", null));
            args.Handled = true;
        };
        if (Array.Exists(e.Args, a => a.StartsWith("--snapshot=")))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        new MainWindow().Show();
    }

    static Window HostPageSnapshot(System.Windows.Controls.UserControl page, double width = 1100, double height = 720)
    {
        var w = new Window
        {
            Content = page,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = 0,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            Background = System.Windows.Media.Brushes.White,
            ShowInTaskbar = false,
        };
        w.Show();
        return w;
    }
}
