using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ZERO.Services;

/// <summary>Windows Job Object：本程序退出（包括崩溃）时，所有子进程会被系统自动结束。</summary>
static class ChildProcessJob
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct BASIC { public long a, b; public uint LimitFlags; public UIntPtr c, d; public uint e; public UIntPtr f; public uint g, h; }
    [StructLayout(LayoutKind.Sequential)]
    struct IO { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    struct EXT { public BASIC Basic; public IO Io; public UIntPtr p1, p2, p3, p4; }

    static readonly IntPtr Handle = Create();

    static IntPtr Create()
    {
        var h = CreateJobObject(IntPtr.Zero, null);
        var info = new EXT { Basic = new BASIC { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        int len = Marshal.SizeOf<EXT>();
        var ptr = Marshal.AllocHGlobal(len);
        Marshal.StructureToPtr(info, ptr, false);
        SetInformationJobObject(h, 9, ptr, (uint)len);
        Marshal.FreeHGlobal(ptr);
        return h;
    }

    public static void Add(Process p) { try { AssignProcessToJobObject(Handle, p.Handle); } catch { } }
}

public sealed class ManagedProcess
{
    Process? _p;
    bool _stopping;

    public event Action<string>? Output;
    public event Action<int>? Exited;          // 仅非主动停止时触发
    public bool IsRunning { get { try { return _p is { HasExited: false }; } catch { return false; } } }

    public void Start(string file, string args, string workDir, IDictionary<string, string>? env = null)
    {
        Stop();
        _stopping = false;
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir,
        };
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data); };
        p.Exited += (_, _) =>
        {
            int code = -1;
            try { code = p.ExitCode; } catch { }
            if (!_stopping && ReferenceEquals(_p, p)) Exited?.Invoke(code);   // 重启时旧进程的退出不算意外退出
        };
        try { p.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException($"无法启动 {file}：{ex.Message}"); }
        ChildProcessJob.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _p = p;
    }

    public void Stop()
    {
        _stopping = true;
        var p = _p;
        _p = null;
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } } catch { }
        p.Dispose();
    }
}
