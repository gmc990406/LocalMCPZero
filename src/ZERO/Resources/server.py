"""
ZERO - 本地 MCP 服务（可接入任意支持 MCP 的 AI 客户端）
MCP_MODE=restricted : 只允许在 MCP_WORKSPACE 内读写文件，禁用命令/后台任务
MCP_MODE=full       : 全电脑访问（文件 + 命令 + 后台任务 + 截屏）
"""
import asyncio, os, re, subprocess, sys, time, uuid
from pathlib import Path

import uvicorn
from mcp.server.fastmcp import FastMCP, Image
from mcp.server.transport_security import TransportSecuritySettings
from mcp.types import ToolAnnotations

TOKEN = os.environ.get("MCP_TOKEN", "").strip()
MODE = os.environ.get("MCP_MODE", "restricted").strip().lower()
FULL = MODE == "full"
WORKSPACE = Path(os.environ.get("MCP_WORKSPACE") or (Path.home() / "ZERO-Workspace")).resolve()
PORT = int(os.environ.get("MCP_PORT", "8766"))
JOB_DIR = Path(os.environ.get("MCP_JOBDIR") or (WORKSPACE / ".mcp_jobs")).resolve()
PROTECT = [Path(p).resolve() for p in os.environ.get("MCP_PROTECT", "").split(";") if p.strip()]
BASE = Path.home().resolve() if FULL else WORKSPACE
MAX_OUTPUT = 20000
IS_WIN = os.name == "nt"
SKIP = {".git", "node_modules", ".venv", "venv", "__pycache__", ".mcp_jobs"}

if len(TOKEN) < 24:
    sys.exit("MCP_TOKEN 未设置或过短")
if not FULL:
    WORKSPACE.mkdir(parents=True, exist_ok=True)
JOB_DIR.mkdir(parents=True, exist_ok=True)


def log(msg: str):
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


mcp = FastMCP(
    "ZERO",
    instructions=(f"操作用户本地电脑（全电脑访问模式），相对路径基于 {BASE}，也可用绝对路径。可用 screenshot 截取桌面，用 run_command 执行 PowerShell 命令。" if FULL
                  else f"只能读写工作目录 {WORKSPACE} 内的文件，命令执行已禁用。"),
    stateless_http=True,
    log_level="WARNING",
    json_response=True,
    transport_security=TransportSecuritySettings(enable_dns_rebinding_protection=False),
)
RO = ToolAnnotations(readOnlyHint=True)
RW = ToolAnnotations(readOnlyHint=False, destructiveHint=True)


def safe_path(p: str) -> Path:
    raw = Path(p)
    target = (raw if raw.is_absolute() else BASE / raw).resolve()
    if not FULL and target != WORKSPACE and WORKSPACE not in target.parents:
        raise ValueError(f"有限制模式：路径超出工作目录 {WORKSPACE}")
    for pr in PROTECT:
        if target == pr or pr in target.parents:
            raise ValueError("该目录受保护（ZERO 配置/Token 所在目录）")
    return target


def disp(f: Path) -> str:
    try:
        return f.relative_to(BASE).as_posix()
    except ValueError:
        return str(f)


def clip(s: str, n: int = MAX_OUTPUT) -> str:
    return s if len(s) <= n else s[:n // 2] + f"\n...（省略 {len(s) - n} 字符）...\n" + s[-n // 2:]


def shell_args(command: str) -> list:
    if IS_WIN:
        prefix = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; "
        return ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", prefix + command]
    return ["bash", "-lc", command]


# ---------------- 文件工具（两种模式都有） ----------------
@mcp.tool(annotations=RO)
def list_files(path: str = ".", recursive: bool = False, max_items: int = 500) -> str:
    """列出目录内容。recursive=True 时递归（跳过 .git、node_modules、.venv 等）。"""
    base = safe_path(path)
    log(f"list_files {disp(base)}")
    out = []
    it = base.rglob("*") if recursive else base.iterdir()
    for f in it:
        if any(part in SKIP for part in f.parts):
            continue
        try:
            out.append(disp(f) + "/" if f.is_dir() else f"{disp(f)}  ({f.stat().st_size} B)")
        except OSError:
            continue
        if len(out) >= max_items:
            out.append("...（已截断）")
            break
    return "\n".join(sorted(out)) or "（空目录）"


@mcp.tool(annotations=RO)
def read_file(path: str, start_line: int = 1, end_line: int = 0) -> str:
    """读取文本文件，可指定行范围（end_line=0 表示到结尾），返回带行号的内容。"""
    f = safe_path(path)
    log(f"read_file {disp(f)}")
    lines = f.read_text(encoding="utf-8", errors="replace").splitlines()
    end = len(lines) if end_line <= 0 else min(end_line, len(lines))
    body = "\n".join(f"{i}: {lines[i - 1]}" for i in range(max(1, start_line), end + 1))
    return clip(f"[{disp(f)} 共 {len(lines)} 行]\n" + body)


@mcp.tool(annotations=RW)
def write_file(path: str, content: str) -> str:
    """创建或覆盖文本文件（自动创建父目录）。"""
    f = safe_path(path)
    log(f"write_file {disp(f)}")
    f.parent.mkdir(parents=True, exist_ok=True)
    f.write_text(content, encoding="utf-8")
    return f"已写入 {disp(f)}（{len(content)} 字符）"


@mcp.tool(annotations=RW)
def edit_file(path: str, old_text: str, new_text: str, replace_all: bool = False) -> str:
    """把文件中的 old_text 替换为 new_text。old_text 必须精确匹配；不唯一时需 replace_all=True。"""
    f = safe_path(path)
    log(f"edit_file {disp(f)}")
    text = f.read_text(encoding="utf-8")
    n = text.count(old_text)
    if n == 0:
        return "未找到 old_text，未修改。"
    if n > 1 and not replace_all:
        return f"old_text 出现了 {n} 次，请提供更长的上下文或设置 replace_all=True。"
    f.write_text(text.replace(old_text, new_text) if replace_all else text.replace(old_text, new_text, 1), encoding="utf-8")
    return f"已修改 {disp(f)}（替换 {n if replace_all else 1} 处）"


@mcp.tool(annotations=RW)
def make_dir(path: str) -> str:
    """创建目录。"""
    f = safe_path(path)
    log(f"make_dir {disp(f)}")
    f.mkdir(parents=True, exist_ok=True)
    return f"已创建 {disp(f)}"


@mcp.tool(annotations=RO)
def search_files(pattern: str, path: str = ".", glob: str = "*", max_results: int = 200) -> str:
    """在文件中按正则搜索文本，返回 文件:行号: 内容（最多扫描 20000 个文件）。"""
    base = safe_path(path)
    log(f"search_files '{pattern}' in {disp(base)}")
    rx = re.compile(pattern)
    hits, scanned = [], 0
    for f in base.rglob(glob):
        if any(p in SKIP for p in f.parts) or not f.is_file():
            continue
        scanned += 1
        if scanned > 20000:
            hits.append("...（扫描文件过多，已停止）")
            break
        try:
            for i, line in enumerate(f.read_text(encoding="utf-8", errors="ignore").splitlines(), 1):
                if rx.search(line):
                    hits.append(f"{disp(f)}:{i}: {line.strip()[:300]}")
                    if len(hits) >= max_results:
                        return "\n".join(hits) + "\n...（已截断）"
        except OSError:
            pass
    return "\n".join(hits) or "无匹配"


# ---------------- 命令工具（仅全电脑模式） ----------------
async def run_command(command: str, cwd: str = ".", timeout_seconds: int = 60) -> str:
    """前台运行命令并返回输出（Windows 用 PowerShell）。适合 2 分钟内结束的命令；耗时任务请用 start_job。"""
    timeout_seconds = max(1, min(timeout_seconds, 120))
    log(f"run_command {command[:120]}")
    def _run():
        try:
            r = subprocess.run(shell_args(command), cwd=safe_path(cwd), capture_output=True, timeout=timeout_seconds)
            out = (r.stdout + b"\n" + r.stderr).decode("utf-8", errors="replace").strip()
            return f"[退出码 {r.returncode}]\n{clip(out)}"
        except subprocess.TimeoutExpired:
            return f"超时（{timeout_seconds} 秒）。耗时任务请改用 start_job。"
    return await asyncio.to_thread(_run)


JOBS: dict = {}


def start_job(command: str, cwd: str = ".") -> str:
    """在后台启动命令（长时间脚本、开发服务器等），立即返回 job_id，用 job_status 查看输出。"""
    job_id = uuid.uuid4().hex[:8]
    logf = JOB_DIR / f"{job_id}.log"
    fh = open(logf, "wb")
    flags = (subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.CREATE_NO_WINDOW) if IS_WIN else 0
    p = subprocess.Popen(shell_args(command), cwd=safe_path(cwd), stdout=fh, stderr=subprocess.STDOUT,
                         stdin=subprocess.DEVNULL, creationflags=flags, start_new_session=not IS_WIN)
    JOBS[job_id] = {"proc": p, "cmd": command, "log": logf, "started": time.strftime("%H:%M:%S"), "fh": fh}
    log(f"start_job {job_id}: {command[:120]}")
    return f"已启动后台任务 job_id={job_id} PID={p.pid}"


def job_status(job_id: str, tail_lines: int = 50) -> str:
    """查看后台任务状态和最后若干行输出。"""
    j = JOBS.get(job_id)
    if not j:
        return "找不到该任务（服务重启后任务列表会清空）"
    code = j["proc"].poll()
    state = "运行中" if code is None else f"已结束，退出码 {code}"
    lines = j["log"].read_bytes().decode("utf-8", errors="replace").splitlines()
    return clip(f"[{job_id}] {state} | 启动于 {j['started']} | {j['cmd']}\n" + "\n".join(lines[-tail_lines:]))


def list_jobs() -> str:
    """列出所有后台任务。"""
    if not JOBS:
        return "没有后台任务"
    return "\n".join(f"{k}  {'运行中' if v['proc'].poll() is None else '已结束(' + str(v['proc'].poll()) + ')'}  {v['cmd']}"
                     for k, v in JOBS.items())


def stop_job(job_id: str) -> str:
    """停止后台任务（含子进程）。"""
    j = JOBS.get(job_id)
    if not j:
        return "找不到该任务"
    p = j["proc"]
    if p.poll() is None:
        if IS_WIN:
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(p.pid)], capture_output=True)
        else:
            import signal
            os.killpg(p.pid, signal.SIGTERM)
    log(f"stop_job {job_id}")
    return f"已停止 {job_id}"

def _capture(monitor: int, max_width: int) -> tuple:
    """用 Windows GDI 截屏（纯 Python ctypes，不依赖第三方库），返回 (png_bytes, 说明)。"""
    import ctypes, struct, zlib
    from ctypes import wintypes as wt
    u32, g32 = ctypes.windll.user32, ctypes.windll.gdi32
    for fn in (lambda: u32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)),
               lambda: ctypes.windll.shcore.SetProcessDpiAwareness(2),
               lambda: u32.SetProcessDPIAware()):
        try:
            if fn():
                break
        except Exception:
            pass
    mons = []
    PROC = ctypes.WINFUNCTYPE(ctypes.c_int, wt.HMONITOR, wt.HDC, ctypes.POINTER(wt.RECT), wt.LPARAM)
    def _cb(h, dc, r, lp):
        mons.append((r.contents.left, r.contents.top, r.contents.right - r.contents.left, r.contents.bottom - r.contents.top))
        return 1
    u32.EnumDisplayMonitors(None, None, PROC(_cb), 0)
    if monitor <= 0:
        x, y, w, h = (u32.GetSystemMetrics(76), u32.GetSystemMetrics(77), u32.GetSystemMetrics(78), u32.GetSystemMetrics(79))
    elif monitor <= len(mons):
        x, y, w, h = mons[monitor - 1]
    else:
        raise ValueError(f"只有 {len(mons)} 个显示器")
    if w <= 0 or h <= 0:
        raise RuntimeError("没有可用的桌面画面")
    ow = min(max_width, w); oh = max(1, round(h * ow / w))
    for f, res, args in ((g32.CreateCompatibleDC, wt.HDC, [wt.HDC]), (g32.CreateCompatibleBitmap, wt.HBITMAP, [wt.HDC, ctypes.c_int, ctypes.c_int]),
                         (g32.SelectObject, wt.HGDIOBJ, [wt.HDC, wt.HGDIOBJ]), (u32.GetDC, wt.HDC, [wt.HWND]),
                         (g32.DeleteObject, wt.BOOL, [wt.HGDIOBJ]), (g32.DeleteDC, wt.BOOL, [wt.HDC]), (u32.ReleaseDC, ctypes.c_int, [wt.HWND, wt.HDC]),
                         (g32.SetStretchBltMode, ctypes.c_int, [wt.HDC, ctypes.c_int]),
                         (g32.StretchBlt, wt.BOOL, [wt.HDC] + [ctypes.c_int] * 4 + [wt.HDC] + [ctypes.c_int] * 4 + [wt.DWORD]),
                         (g32.GetDIBits, ctypes.c_int, [wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT, ctypes.c_void_p, ctypes.c_void_p, wt.UINT])):
        f.restype, f.argtypes = res, args
    sdc = u32.GetDC(None)
    if not sdc:
        raise RuntimeError("无法获取桌面（可能已锁屏、远程桌面已断开或未登录）")
    mdc = g32.CreateCompatibleDC(sdc); bmp = g32.CreateCompatibleBitmap(sdc, ow, oh); old = g32.SelectObject(mdc, bmp)
    try:
        g32.SetStretchBltMode(mdc, 4)  # HALFTONE，缩放更清晰
        ok = g32.StretchBlt(mdc, 0, 0, ow, oh, sdc, x, y, w, h, 0x00CC0020 | 0x40000000)  # SRCCOPY | CAPTUREBLT
        if not ok:
            raise RuntimeError("截屏失败（可能已锁屏、远程桌面已断开或未登录）")
        hdr = struct.pack("<IiiHHIIiiII", 40, ow, -oh, 1, 32, 0, 0, 0, 0, 0, 0)
        bi = ctypes.create_string_buffer(hdr + b"\0" * 16)
        buf = ctypes.create_string_buffer(ow * oh * 4)
        if g32.GetDIBits(mdc, bmp, 0, oh, buf, bi, 0) != oh:
            raise RuntimeError("读取截图像素失败")
    finally:
        g32.SelectObject(mdc, old); g32.DeleteObject(bmp); g32.DeleteDC(mdc); u32.ReleaseDC(None, sdc)
    src = buf.raw
    rgb = bytearray(ow * oh * 3)
    rgb[0::3], rgb[1::3], rgb[2::3] = src[2::4], src[1::4], src[0::4]
    stride = ow * 3
    raw = b"".join(b"\0" + bytes(rgb[i:i + stride]) for i in range(0, len(rgb), stride))
    chunk = lambda t, d: struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", ow, oh, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))
    return png, f"{w}x{h} -> {ow}x{oh}，显示器数 {len(mons)}"


async def screenshot(monitor: int = 0, max_width: int = 1600):
    """截取桌面画面并返回图片（PNG）。monitor=0 截全部显示器，1/2… 截指定显示器；max_width 为缩放后的最大宽度。
    需要电脑处于已登录且未锁屏、未断开的桌面会话。"""
    if not IS_WIN:
        return "截屏目前只支持 Windows"
    log(f"screenshot monitor={monitor}")
    try:
        png, info = await asyncio.to_thread(_capture, int(monitor), max(320, min(int(max_width), 3840)))
    except Exception as e:
        return f"截屏失败：{e}"
    shot_dir = JOB_DIR / "screenshots"
    shot_dir.mkdir(parents=True, exist_ok=True)
    path = shot_dir / time.strftime("shot_%Y%m%d_%H%M%S.png")
    path.write_bytes(png)
    for old in sorted(shot_dir.glob("shot_*.png"))[:-20]:   # 只保留最近 20 张
        old.unlink(missing_ok=True)
    return [Image(data=png, format="png"), f"截图已保存：{path}（{info}，{len(png) // 1024} KB）"]


if FULL:
    mcp.tool(annotations=RO)(screenshot)
    mcp.tool(annotations=RW)(run_command)
    mcp.tool(annotations=RW)(start_job)
    mcp.tool(annotations=RO)(job_status)
    mcp.tool(annotations=RO)(list_jobs)
    mcp.tool(annotations=RW)(stop_job)


def with_auth(app):
    """只接受 /<TOKEN>/mcp 路径，或 /mcp + Authorization: Bearer <TOKEN>"""
    secret_prefix = f"/{TOKEN}"
    async def wrapped(scope, receive, send):
        if scope["type"] != "http":
            return await app(scope, receive, send)
        path = scope.get("path", "")
        bearer = dict(scope.get("headers") or []).get(b"authorization", b"").decode()
        if path.startswith(secret_prefix + "/"):
            scope = dict(scope, path=path[len(secret_prefix):])
        elif bearer != f"Bearer {TOKEN}":
            log(f"拒绝未授权请求 {scope.get('method')} {'/'.join(path.split('/')[:2])}/…")
            await send({"type": "http.response.start", "status": 401, "headers": [(b"content-type", b"text/plain")]})
            return await send({"type": "http.response.body", "body": b"unauthorized"})
        return await app(scope, receive, send)
    return wrapped


if __name__ == "__main__":
    log(f"模式：{'全电脑访问' if FULL else '有限制（仅工作目录文件）'}")
    log(f"{'相对路径基准' if FULL else '工作目录'}：{BASE}")
    log(f"已启用工具：{', '.join(sorted(t.name for t in mcp._tool_manager.list_tools()))}")
    log(f"监听 http://127.0.0.1:{PORT}/<token>/mcp")
    uvicorn.run(with_auth(mcp.streamable_http_app()), host="127.0.0.1", port=PORT, log_level="warning", access_log=False)
