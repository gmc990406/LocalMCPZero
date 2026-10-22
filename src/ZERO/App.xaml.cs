using System;
using System.Threading;
using System.Windows;

namespace ZERO;

public partial class App : Application
{
    static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
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
}
