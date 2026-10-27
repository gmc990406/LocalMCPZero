using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Condition = System.Windows.Automation.Condition;

namespace ZERO.Services;

/// <summary>桌面窗口信息（物理像素坐标）</summary>
public sealed record WinInfo(long Hwnd, int Pid, string Process, string Title, string Class, int X, int Y, int W, int H, bool Visible, bool Minimized, bool Foreground)
{
    public string Line => $"{(Title.Length > 0 ? Title : "（无标题）")}";
    public string Meta => $"{Process} · PID {Pid} · {W}×{H}{(Minimized ? " · 已最小化" : "")}{(Foreground ? " · 前台" : "")}";
}

/// <summary>Win32：枚举窗口、按窗口截图（PrintWindow，窗口被遮挡时也能截到）、桌面可用性检查、模拟输入</summary>
public static class Win32
{
    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll")] static extern bool SwitchDesktop(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }

    /// <summary>辅助进程使用物理像素坐标（与截图一致）</summary>
    public static void UsePhysicalPixels()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }

    static string ProcName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName + ".exe"; } catch { return "?"; }
    }

    public static int WindowPid(long hwnd)
    {
        GetWindowThreadProcessId(new IntPtr(hwnd), out uint pid);
        return (int)pid;
    }

    public static bool Exists(long hwnd) => hwnd != 0 && IsWindow(new IntPtr(hwnd));

    public static WinInfo? Info(long hwnd)
    {
        var h = new IntPtr(hwnd);
        if (!IsWindow(h)) return null;
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        var cls = new StringBuilder(256);
        GetClassName(h, cls, cls.Capacity);
        GetWindowRect(h, out var r);
        int pid = WindowPid(hwnd);
        return new WinInfo(hwnd, pid, ProcName(pid), sb.ToString(), cls.ToString(), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            IsWindowVisible(h), IsIconic(h), GetForegroundWindow() == h);
    }

    /// <summary>可见的顶层应用窗口（跳过工具窗口、被 DWM 隐藏的 UWP 后台窗口、无标题窗口）</summary>
    public static List<WinInfo> ListWindows(bool includeUntitled = false)
    {
        var list = new List<WinInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (GetWindow(h, 4) != IntPtr.Zero) return true;                    // GW_OWNER：有所有者的弹出窗口算在所有者里
            if ((GetWindowLong(h, -20) & 0x80) != 0) return true;               // WS_EX_TOOLWINDOW
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;   // DWMWA_CLOAKED
            var info = Info(h.ToInt64());
            if (info == null || (!includeUntitled && info.Title.Length == 0) || info.W <= 0 || info.H <= 0) return true;
            list.Add(info);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>桌面是否可交互：锁屏、远程桌面断开、UAC 安全桌面时返回原因</summary>
    public static string? DesktopUnavailable()
    {
        var d = OpenInputDesktop(0, false, 0x0100);   // DESKTOP_SWITCHDESKTOP
        if (d == IntPtr.Zero) return "无法访问输入桌面（电脑可能已锁屏、远程桌面已断开或正在显示 UAC 安全桌面）";
        try { return SwitchDesktop(d) ? null : "当前不是可交互的桌面（可能已锁屏）"; }
        finally { CloseDesktop(d); }
    }

    /// <summary>窗口截图：返回 BGRA 像素（自上而下），PrintWindow 失败或窗口最小化时抛出异常</summary>
    public static (byte[] Bgra, int W, int H) Capture(long hwnd)
    {
        var h = new IntPtr(hwnd);
        if (!IsWindow(h)) throw new InvalidOperationException("窗口不存在或已关闭");
        if (IsIconic(h)) throw new InvalidOperationException("窗口已最小化，无法截图");
        GetWindowRect(h, out var r);
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) throw new InvalidOperationException("窗口大小为 0");
        var sdc = GetDC(IntPtr.Zero);
        var mdc = CreateCompatibleDC(sdc);
        var bmp = CreateCompatibleBitmap(sdc, w, ht);
        var old = SelectObject(mdc, bmp);
        try
        {
            if (!PrintWindow(h, mdc, 2)) throw new InvalidOperationException("PrintWindow 失败（窗口可能不支持或桌面不可用）");   // PW_RENDERFULLCONTENT
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -ht, biPlanes = 1, biBitCount = 32 };
            var buf = new byte[w * ht * 4];
            SelectObject(mdc, old);
            if (GetDIBits(mdc, bmp, 0, (uint)ht, buf, ref bi, 0) == 0) throw new InvalidOperationException("读取截图像素失败");
            for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
            return (buf, w, ht);
        }
        finally
        {
            DeleteObject(bmp); DeleteDC(mdc); ReleaseDC(IntPtr.Zero, sdc);
        }
    }

    public static BitmapSource ToBitmap((byte[] Bgra, int W, int H) img)
    {
        var b = BitmapSource.Create(img.W, img.H, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.W * 4);
        b.Freeze();
        return b;
    }

    public static void Window(long hwnd, string action)
    {
        var h = new IntPtr(hwnd);
        switch (action)
        {
            case "activate":
                if (IsIconic(h)) ShowWindow(h, 9);
                keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);   // 按一下 Alt，允许切换前台窗口
                SetForegroundWindow(h); break;
            case "restore": ShowWindow(h, 9); break;
            case "minimize": ShowWindow(h, 6); break;
            case "maximize": ShowWindow(h, 3); break;
            default: throw new ArgumentException("未知的窗口操作 " + action);
        }
    }

    public static bool IsForeground(long hwnd) => GetForegroundWindow() == new IntPtr(hwnd);

    const uint MOVE = 0x0001, LDOWN = 0x0002, LUP = 0x0004, RDOWN = 0x0008, RUP = 0x0010, ABS = 0x8000, VDESK = 0x4000, WHEEL = 0x0800;

    public static void Click(int x, int y, string button = "left", int count = 1)
    {
        SetCursorPos(x, y);
        Thread.Sleep(30);
        uint down = button == "right" ? RDOWN : LDOWN, up = button == "right" ? RUP : LUP;
        for (int i = 0; i < count; i++)
        {
            Send(Mouse(down)); Send(Mouse(up));
            Thread.Sleep(60);
        }
    }

    public static void Wheel(int x, int y, int delta)
    {
        SetCursorPos(x, y);
        var m = Mouse(WHEEL); m.u.mi.mouseData = unchecked((uint)delta);
        Send(m);
    }

    static INPUT Mouse(uint flags) => new() { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };
    static void Send(params INPUT[] inp)
    {
        if (SendInput((uint)inp.Length, inp, Marshal.SizeOf<INPUT>()) != inp.Length)
            throw new InvalidOperationException("模拟输入被系统拒绝（桌面不可用，或目标窗口权限高于 ZERO）");
    }

    public static void TypeText(string text)
    {
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { KeyCombo("enter"); continue; }
            Send(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 } } },
                 new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 | 0x0002 } } });
            Thread.Sleep(5);
        }
    }

    static readonly Dictionary<string, ushort> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09,
        ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
    };

    /// <summary>组合键，例如 ctrl+s、alt+f4、enter、f5</summary>
    public static void KeyCombo(string combo)
    {
        var vks = new List<ushort>();
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Keys.TryGetValue(part, out var vk)) vks.Add(vk);
            else if (part.Length == 1 && char.IsLetterOrDigit(part[0])) vks.Add(char.ToUpperInvariant(part[0]));
            else if ((part[0] == 'f' || part[0] == 'F') && int.TryParse(part[1..], out int f) && f is >= 1 and <= 24) vks.Add((ushort)(0x70 + f - 1));
            else throw new ArgumentException($"不认识的按键「{part}」");
        }
        if (vks.Contains(0x5B) && vks.Count == 2 && vks[1] == 'L') throw new ArgumentException("不允许发送 Win+L");
        var list = new List<INPUT>();
        foreach (var vk in vks) list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } });
        for (int i = vks.Count - 1; i >= 0; i--) list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vks[i], dwFlags = 0x0002 } } });
        Send(list.ToArray());
    }
}

/// <summary>UI Automation：读取窗口控件树、查找 / 等待元素、对元素执行操作、比较两张图片。
/// 界面（视觉工作台）直接调用；MCP 服务通过 ZERO.exe --uia-helper 辅助进程（每行一个 JSON 请求）调用同一份代码。</summary>
public static class UiaProvider
{
    static readonly CacheRequest Cache = BuildCache();

    static CacheRequest BuildCache()
    {
        var c = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        foreach (var p in new AutomationProperty[]
                 {
                     AutomationElement.NameProperty, AutomationElement.AutomationIdProperty, AutomationElement.ControlTypeProperty,
                     AutomationElement.ClassNameProperty, AutomationElement.BoundingRectangleProperty, AutomationElement.IsEnabledProperty,
                     AutomationElement.IsOffscreenProperty, AutomationElement.IsPasswordProperty, AutomationElement.HasKeyboardFocusProperty,
                     AutomationElement.IsInvokePatternAvailableProperty, AutomationElement.IsValuePatternAvailableProperty,
                     AutomationElement.IsTogglePatternAvailableProperty, AutomationElement.IsSelectionItemPatternAvailableProperty,
                     AutomationElement.IsExpandCollapsePatternAvailableProperty, AutomationElement.IsScrollItemPatternAvailableProperty,
                     ValuePattern.ValueProperty, ValuePattern.IsReadOnlyProperty, TogglePattern.ToggleStateProperty,
                     SelectionItemPattern.IsSelectedProperty, ExpandCollapsePattern.ExpandCollapseStateProperty,
                 })
            c.Add(p);
        return c;
    }

    static object? P(AutomationElement e, AutomationProperty p)
    {
        try
        {
            var v = e.GetCachedPropertyValue(p, true);
            return v == AutomationElement.NotSupported ? null : v;
        }
        catch { return null; }
    }

    static AutomationElement Root(long hwnd)
    {
        if (!Win32.Exists(hwnd)) throw new UiaError("WINDOW_NOT_FOUND", "窗口不存在或已关闭");
        using (Cache.Activate())
            return AutomationElement.FromHandle(new IntPtr(hwnd));
    }

    static AutomationElement Refresh(AutomationElement e)
    {
        using (Cache.Activate()) return e.GetUpdatedCache(Cache);
    }

    /// <summary>元素的 JSON 描述；密码框不读取内容</summary>
    public static JsonObject Describe(AutomationElement e, string path, int depth, long hwnd) => Describe(e, path, depth, Origin(hwnd));

    static (int X, int Y) Origin(long hwnd) => Win32.Info(hwnd) is { } wi ? (wi.X, wi.Y) : (0, 0);

    static JsonObject Describe(AutomationElement e, string path, int depth, (int X, int Y) origin)
    {
        var o = new JsonObject { ["path"] = path, ["depth"] = depth };
        var ct = P(e, AutomationElement.ControlTypeProperty) as ControlType;
        o["type"] = ct?.ProgrammaticName.Replace("ControlType.", "") ?? "";
        if (P(e, AutomationElement.NameProperty) is string n && n.Length > 0) o["name"] = n.Length > 200 ? n[..200] + "…" : n;
        if (P(e, AutomationElement.AutomationIdProperty) is string id && id.Length > 0) o["id"] = id;
        if (P(e, AutomationElement.ClassNameProperty) is string cls && cls.Length > 0) o["class"] = cls;
        if (P(e, AutomationElement.BoundingRectangleProperty) is Rect r && !r.IsEmpty)
            o["rect"] = new JsonArray((int)r.X - origin.X, (int)r.Y - origin.Y, (int)r.Width, (int)r.Height);   // 相对窗口左上角（物理像素）
        if (P(e, AutomationElement.IsEnabledProperty) is false) o["enabled"] = false;
        if (P(e, AutomationElement.IsOffscreenProperty) is true) o["offscreen"] = true;
        if (P(e, AutomationElement.HasKeyboardFocusProperty) is true) o["focused"] = true;
        bool pwd = P(e, AutomationElement.IsPasswordProperty) is true;
        if (pwd) o["password"] = true;
        var acts = new JsonArray();
        if (P(e, AutomationElement.IsInvokePatternAvailableProperty) is true) acts.Add("invoke");
        if (P(e, AutomationElement.IsValuePatternAvailableProperty) is true)
        {
            if (P(e, ValuePattern.IsReadOnlyProperty) is not true) acts.Add("set_value");
            if (P(e, ValuePattern.ValueProperty) is string v && v.Length > 0)
                o["value"] = pwd ? "••••••（密码框，内容不读取）" : v.Length > 300 ? v[..300] + "…" : v;
        }
        if (P(e, AutomationElement.IsTogglePatternAvailableProperty) is true)
        {
            acts.Add("toggle");
            if (P(e, TogglePattern.ToggleStateProperty) is ToggleState ts) o["toggle"] = ts.ToString().ToLowerInvariant();
        }
        if (P(e, AutomationElement.IsSelectionItemPatternAvailableProperty) is true)
        {
            acts.Add("select");
            if (P(e, SelectionItemPattern.IsSelectedProperty) is true) o["selected"] = true;
        }
        if (P(e, AutomationElement.IsExpandCollapsePatternAvailableProperty) is true)
        {
            acts.Add("expand"); acts.Add("collapse");
            if (P(e, ExpandCollapsePattern.ExpandCollapseStateProperty) is ExpandCollapseState es) o["expand_state"] = es.ToString().ToLowerInvariant();
        }
        if (acts.Count > 0) o["actions"] = acts;
        return o;
    }

    /// <summary>控件树（ControlView），深度优先，按 path（如 0.2.1）标识元素</summary>
    public static JsonObject Tree(long hwnd, int maxDepth = 6, int maxNodes = 400, string? start = null)
    {
        var root = start is { Length: > 0 } ? Find(hwnd, start) : Root(hwnd);
        string rootPath = start is { Length: > 0 } && start.All(ch => char.IsDigit(ch) || ch == '.') ? start : "0";
        var nodes = new JsonArray();
        var origin = Origin(hwnd);
        bool truncated = false;
        var walker = TreeWalker.ControlViewWalker;
        void Walk(AutomationElement e, string path, int depth)
        {
            if (nodes.Count >= maxNodes) { truncated = true; return; }
            nodes.Add(Describe(e, path, depth, origin));
            if (depth >= maxDepth) return;
            AutomationElement? child;
            using (Cache.Activate())
            {
                try { child = walker.GetFirstChild(e, Cache); } catch { child = null; }
            }
            int i = 0;
            while (child != null)
            {
                if (nodes.Count >= maxNodes) { truncated = true; return; }
                Walk(child, $"{path}.{i}", depth + 1);
                i++;
                using (Cache.Activate())
                {
                    try { child = walker.GetNextSibling(child, Cache); } catch { child = null; }
                }
            }
        }
        Walk(root, rootPath, 0);
        return new JsonObject { ["ok"] = true, ["nodes"] = nodes, ["truncated"] = truncated, ["count"] = nodes.Count };
    }

    /// <summary>查找元素。selector：path（0.2.1，来自 ui_inspect）或 “id=xxx;name=确定;type=Button;class=…;index=0”，name~= 表示包含</summary>
    public static AutomationElement Find(long hwnd, string selector)
    {
        var root = Root(hwnd);
        selector = selector.Trim();
        if (selector.Length > 0 && selector.All(ch => char.IsDigit(ch) || ch == '.'))
        {
            var parts = selector.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] != "0") throw new UiaError("BAD_SELECTOR", "path 必须以 0 开头（0 表示窗口本身）");
            var e = root;
            var walker = TreeWalker.ControlViewWalker;
            foreach (var p in parts.Skip(1))
            {
                int idx = int.Parse(p);
                AutomationElement? c;
                using (Cache.Activate()) c = walker.GetFirstChild(e, Cache);
                for (int i = 0; i < idx && c != null; i++)
                    using (Cache.Activate()) c = walker.GetNextSibling(c, Cache);
                e = c ?? throw new UiaError("ELEMENT_NOT_FOUND", $"找不到元素 {selector}（界面可能已变化，请重新 ui_inspect）");
            }
            return e;
        }
        string? id = null, name = null, nameHas = null, type = null, cls = null;
        int index = 0;
        foreach (var kv in selector.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = kv.IndexOf('=');
            if (eq <= 0) throw new UiaError("BAD_SELECTOR", $"选择器格式错误：{kv}（应为 key=value）");
            var k = kv[..eq].Trim().ToLowerInvariant(); var v = kv[(eq + 1)..].Trim();
            switch (k)
            {
                case "id": id = v; break;
                case "name": name = v; break;
                case "name~": nameHas = v; break;
                case "type": type = v; break;
                case "class": cls = v; break;
                case "index": index = int.TryParse(v, out var ix) ? ix : 0; break;
                default: throw new UiaError("BAD_SELECTOR", $"不支持的选择器字段 {k}（可用 id、name、name~、type、class、index）");
            }
        }
        var conds = new List<Condition>();
        if (id != null) conds.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        if (name != null) conds.Add(new PropertyCondition(AutomationElement.NameProperty, name));
        if (cls != null) conds.Add(new PropertyCondition(AutomationElement.ClassNameProperty, cls));
        if (type != null)
        {
            var ct = ControlTypeByName(type) ?? throw new UiaError("BAD_SELECTOR", $"不认识的控件类型 {type}（例如 Button、Edit、CheckBox、ComboBox、ListItem、MenuItem、TabItem）");
            conds.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
        }
        if (conds.Count == 0 && nameHas == null) throw new UiaError("BAD_SELECTOR", "选择器为空");
        Condition cond = conds.Count switch { 0 => Condition.TrueCondition, 1 => conds[0], _ => new AndCondition(conds.ToArray()) };
        AutomationElementCollection all;
        using (Cache.Activate()) all = root.FindAll(TreeScope.Descendants, cond);
        var matches = all.Cast<AutomationElement>()
            .Where(e => nameHas == null || (P(e, AutomationElement.NameProperty) as string ?? "").Contains(nameHas, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count <= index) throw new UiaError("ELEMENT_NOT_FOUND", $"找不到元素 {selector}（找到 {matches.Count} 个匹配）");
        return matches[index];
    }

    static ControlType? ControlTypeByName(string n)
    {
        foreach (var f in typeof(ControlType).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return f.GetValue(null) as ControlType;
        return null;
    }

    /// <summary>等待元素出现 / 消失 / 可用</summary>
    public static JsonObject Wait(long hwnd, string selector, string state, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        string last = "";
        while (true)
        {
            try
            {
                var e = Find(hwnd, selector);
                if (state == "appear" || (state == "enabled" && P(e, AutomationElement.IsEnabledProperty) is true))
                    return new JsonObject { ["ok"] = true, ["state"] = state, ["elapsed_ms"] = sw.ElapsedMilliseconds, ["element"] = Describe(e, selector, 0, hwnd) };
                last = state == "enabled" ? "元素存在但不可用" : "元素仍然存在";
            }
            catch (UiaError ex) when (ex.Code == "ELEMENT_NOT_FOUND")
            {
                if (state == "disappear") return new JsonObject { ["ok"] = true, ["state"] = state, ["elapsed_ms"] = sw.ElapsedMilliseconds };
                last = ex.Message;
            }
            if (sw.ElapsedMilliseconds >= timeoutMs)
                return new JsonObject { ["ok"] = false, ["code"] = "TIMEOUT", ["message"] = $"等待 {timeoutMs} 毫秒后仍未满足条件：{last}" };
            Thread.Sleep(250);
        }
    }

    /// <summary>执行操作。优先使用 UIA 模式（不需要前台、不移动鼠标）；click 类操作使用真实鼠标输入，需要桌面可用</summary>
    public static JsonObject Act(long hwnd, string selector, string action, string? value)
    {
        if (action is "activate" or "restore" or "minimize" or "maximize")
        {
            Win32.Window(hwnd, action);
            Thread.Sleep(200);
            return new JsonObject { ["ok"] = true, ["action"] = action };
        }
        if (action == "keys" || (action == "type" && selector.Length == 0) || (action is "click" or "double_click" or "right_click" or "scroll" && selector.Length == 0))
        {
            EnsureInput(hwnd);
            switch (action)
            {
                case "keys": Win32.KeyCombo(value ?? ""); break;
                case "type": Win32.TypeText(value ?? ""); break;
                default:
                    var (x, y) = ParsePoint(hwnd, value);
                    if (action == "scroll") Win32.Wheel(x, y, -120 * 3);
                    else Win32.Click(x, y, action == "right_click" ? "right" : "left", action == "double_click" ? 2 : 1);
                    break;
            }
            return new JsonObject { ["ok"] = true, ["action"] = action };
        }
        var e = Find(hwnd, selector);
        bool pwd = P(e, AutomationElement.IsPasswordProperty) is true;
        if (pwd && action is "set_value" or "type")
            throw new UiaError("SENSITIVE_FIELD", "这是密码框：ZERO 不允许 AI 输入或读取密码，请让用户自己输入");
        if (P(e, AutomationElement.IsEnabledProperty) is false && action != "focus")
            throw new UiaError("ELEMENT_DISABLED", "元素当前不可用（灰色）");
        switch (action)
        {
            case "invoke":
                if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) ((InvokePattern)ip).Invoke();
                else if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp0)) ((TogglePattern)tp0).Toggle();
                else if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp0)) ((SelectionItemPattern)sp0).Select();
                else if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep0)) ((ExpandCollapsePattern)ep0).Expand();
                else throw new UiaError("PATTERN_UNSUPPORTED", "元素不支持 invoke，可以改用 click（真实鼠标点击）");
                break;
            case "set_value":
                if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) throw new UiaError("PATTERN_UNSUPPORTED", "元素不支持 set_value，可以先 click 再 type");
                if (((ValuePattern)vp).Current.IsReadOnly) throw new UiaError("READ_ONLY", "元素是只读的（或不允许直接写值），不能 set_value，可以先 click 再 type");
                ((ValuePattern)vp).SetValue(value ?? "");
                break;
            case "toggle":
                if (!e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) throw new UiaError("PATTERN_UNSUPPORTED", "元素不支持 toggle");
                ((TogglePattern)tp).Toggle();
                break;
            case "select":
                if (!e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) throw new UiaError("PATTERN_UNSUPPORTED", "元素不支持 select");
                ((SelectionItemPattern)sp).Select();
                break;
            case "expand":
            case "collapse":
                if (!e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep)) throw new UiaError("PATTERN_UNSUPPORTED", $"元素不支持 {action}");
                if (action == "expand") ((ExpandCollapsePattern)ep).Expand(); else ((ExpandCollapsePattern)ep).Collapse();
                break;
            case "focus":
                e.SetFocus();
                break;
            case "scroll_into_view":
                if (!e.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var si)) throw new UiaError("PATTERN_UNSUPPORTED", "元素不支持 scroll_into_view");
                ((ScrollItemPattern)si).ScrollIntoView();
                break;
            case "type":
                EnsureInput(hwnd);
                try { e.SetFocus(); } catch { }
                Thread.Sleep(80);
                Win32.TypeText(value ?? "");
                break;
            case "click":
            case "double_click":
            case "right_click":
            case "scroll":
            {
                EnsureInput(hwnd);
                var cur = Refresh(e);
                if (P(cur, AutomationElement.BoundingRectangleProperty) is not Rect r || r.IsEmpty || r.Width < 1)
                    throw new UiaError("ELEMENT_NOT_VISIBLE", "元素没有可见区域（可能在屏幕外或被折叠）");
                int x = (int)(r.X + r.Width / 2), y = (int)(r.Y + r.Height / 2);
                if (action == "scroll") Win32.Wheel(x, y, (value == "up" ? 120 : -120) * 3);
                else Win32.Click(x, y, action == "right_click" ? "right" : "left", action == "double_click" ? 2 : 1);
                break;
            }
            default:
                throw new UiaError("BAD_ACTION", $"不支持的操作 {action}");
        }
        Thread.Sleep(150);
        JsonObject? after = null;
        try { after = Describe(Refresh(e), selector, 0, hwnd); } catch { }
        return new JsonObject { ["ok"] = true, ["action"] = action, ["element"] = after };
    }

    static (int, int) ParsePoint(long hwnd, string? value)
    {
        var wi = Win32.Info(hwnd) ?? throw new UiaError("WINDOW_NOT_FOUND", "窗口不存在或已关闭");
        var parts = (value ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y))
            throw new UiaError("BAD_ARGUMENT", "没有指定元素时需要坐标 value=\"x,y\"（相对窗口左上角，物理像素，与 ui_screenshot 原图一致）");
        if (x < 0 || y < 0 || x >= wi.W || y >= wi.H) throw new UiaError("BAD_ARGUMENT", $"坐标超出窗口范围（窗口 {wi.W}×{wi.H}）");
        return (wi.X + x, wi.Y + y);
    }

    /// <summary>真实输入前：桌面必须可用，目标窗口必须在前台（自动激活一次）</summary>
    static void EnsureInput(long hwnd)
    {
        if (Win32.DesktopUnavailable() is { } why) throw new UiaError("DESKTOP_UNAVAILABLE", why);
        if (!Win32.IsForeground(hwnd))
        {
            Win32.Window(hwnd, "activate");
            Thread.Sleep(250);
            if (!Win32.IsForeground(hwnd)) throw new UiaError("NOT_FOREGROUND", "无法把目标窗口切换到前台，模拟输入可能发到其他窗口，已取消");
        }
    }

    // ====================== 图片比较 ======================
    static (byte[] px, int w, int h) LoadBgra(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var src = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }

    /// <summary>逐像素比较（容差 tolerance：单个通道差值），返回差异比例、差异区域，并输出差异图（红色标出变化）</summary>
    public static JsonObject Compare(string a, string b, string? outPath, int tolerance = 16)
    {
        var (pa, wa, ha) = LoadBgra(a);
        var (pb, wb, hb) = LoadBgra(b);
        int w = Math.Min(wa, wb), h = Math.Min(ha, hb);
        long changed = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var diff = outPath != null ? new byte[w * h * 4] : null;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int ia = (y * wa + x) * 4, ib = (y * wb + x) * 4;
                int d = Math.Max(Math.Abs(pa[ia] - pb[ib]), Math.Max(Math.Abs(pa[ia + 1] - pb[ib + 1]), Math.Abs(pa[ia + 2] - pb[ib + 2])));
                bool ch = d > tolerance;
                if (ch)
                {
                    changed++;
                    if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
                }
                if (diff != null)
                {
                    int o = (y * w + x) * 4;
                    byte g = (byte)((pb[ib] + pb[ib + 1] + pb[ib + 2]) / 3 / 2 + 110);
                    if (ch) { diff[o] = 40; diff[o + 1] = 40; diff[o + 2] = 235; }
                    else { diff[o] = g; diff[o + 1] = g; diff[o + 2] = g; }
                    diff[o + 3] = 255;
                }
            }
        long total = (long)w * h;
        if (diff != null && outPath != null)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, diff, w * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(outPath);
            enc.Save(fs);
        }
        var r = new JsonObject
        {
            ["ok"] = true, ["size_a"] = new JsonArray(wa, ha), ["size_b"] = new JsonArray(wb, hb), ["same_size"] = wa == wb && ha == hb,
            ["changed_pixels"] = changed, ["changed_ratio"] = total > 0 ? Math.Round((double)changed / total, 6) : 0,
        };
        if (maxX >= 0) r["changed_box"] = new JsonArray(minX, minY, maxX - minX + 1, maxY - minY + 1);
        if (outPath != null) r["diff_image"] = outPath;
        return r;
    }

    // ====================== 辅助进程：ZERO.exe --uia-helper ======================
    /// <summary>从标准输入逐行读取 JSON 请求，逐行输出 JSON 结果；标准输入关闭后退出</summary>
    public static int HelperMain()
    {
        Win32.UsePhysicalPixels();
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        int exit = 0;
        var t = new Thread(() =>
        {
            string? line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                JsonObject res;
                try { res = Handle(JsonNode.Parse(line)!.AsObject()); }
                catch (UiaError ex) { res = new JsonObject { ["ok"] = false, ["code"] = ex.Code, ["message"] = ex.Message }; }
                catch (ElementNotAvailableException) { res = new JsonObject { ["ok"] = false, ["code"] = "ELEMENT_GONE", ["message"] = "元素已不存在（界面已变化）" }; }
                catch (Exception ex) { res = new JsonObject { ["ok"] = false, ["code"] = "HELPER_ERROR", ["message"] = ex.GetType().Name + ": " + ex.Message }; }
                stdout.WriteLine(res.ToJsonString());
            }
        });
        t.SetApartmentState(ApartmentState.MTA);   // UIA 客户端建议在 MTA 线程中调用
        t.IsBackground = false;
        t.Start();
        t.Join();
        return exit;
    }

    static JsonObject Handle(JsonObject q)
    {
        string cmd = q["cmd"]?.GetValue<string>() ?? "";
        long hwnd = q["hwnd"]?.GetValue<long>() ?? 0;
        string sel = q["selector"]?.GetValue<string>() ?? "";
        switch (cmd)
        {
            case "ping": return new JsonObject { ["ok"] = true, ["pid"] = Environment.ProcessId };
            case "tree": return Tree(hwnd, q["depth"]?.GetValue<int>() ?? 6, q["max"]?.GetValue<int>() ?? 400, sel);
            case "find": return new JsonObject { ["ok"] = true, ["element"] = Describe(Find(hwnd, sel), sel, 0, hwnd) };
            case "wait": return Wait(hwnd, sel, q["state"]?.GetValue<string>() ?? "appear", q["timeout_ms"]?.GetValue<int>() ?? 5000);
            case "act": return Act(hwnd, sel, q["action"]?.GetValue<string>() ?? "", q["value"]?.GetValue<string>());
            case "compare": return Compare(q["a"]!.GetValue<string>(), q["b"]!.GetValue<string>(), q["out"]?.GetValue<string>(), q["tolerance"]?.GetValue<int>() ?? 16);
            default: throw new UiaError("BAD_COMMAND", "未知命令 " + cmd);
        }
    }
}

public sealed class UiaError : Exception
{
    public string Code { get; }
    public UiaError(string code, string message) : base(message) { Code = code; }
}
