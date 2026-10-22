using System;
using System.Threading;
using System.Windows;

namespace ZERO;

public partial class App : Application
{
    static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ZERO_SingleInstance_8f3a", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("ZERO 已经在运行，请在系统托盘中找到它。", "ZERO",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        Services.AppLog.SessionHeader();
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Services.AppLog.Write("崩溃", args.ExceptionObject?.ToString() ?? "");
        DispatcherUnhandledException += (_, args) =>
        {
            Services.AppLog.Error("界面", args.Exception);
            MessageBox.Show(args.Exception.Message, "ZERO 出错", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        if (Array.Exists(e.Args, a => a.StartsWith("--snapshot=")))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
