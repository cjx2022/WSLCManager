"""真实鼠标点击测试：强制前置 + 命中校验。用法: ui_click.py <x物理> <y物理> [截图名]"""
import os, sys, time, subprocess, ctypes
from ctypes import wintypes

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import shot as S

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

ROOT = os.path.dirname(HERE)          # 仓库根目录 = _tools/ 的上一级
EXE = os.path.join(ROOT, "bin", "Release", "net8.0-windows", "WSLCManager.exe")
TITLE = "WSLC 容器管理器"
OUTDIR = os.path.abspath(os.path.join(HERE, "..", "_shots"))


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG),
                ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class _INPUTUNION(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("u", _INPUTUNION)]


MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004


def _mkinput(flag):
    a = (INPUT * 1)()
    a[0].type = 0
    a[0].u.mi.dx = 0
    a[0].u.mi.dy = 0
    a[0].u.mi.mouseData = 0
    a[0].u.mi.dwFlags = flag
    a[0].u.mi.time = 0
    a[0].u.mi.dwExtraInfo = None
    return a


def send_click(gap=0.14):
    n1 = user32.SendInput(1, _mkinput(MOUSEEVENTF_LEFTDOWN), ctypes.sizeof(INPUT))
    time.sleep(gap)
    n2 = user32.SendInput(1, _mkinput(MOUSEEVENTF_LEFTUP), ctypes.sizeof(INPUT))
    return n1 + n2


def force_foreground(hwnd):
    HWND_TOPMOST = -1
    SWP_NOSIZE = 0x0001
    SWP_NOMOVE = 0x0002
    SWP_SHOWWINDOW = 0x0040

    fg = user32.GetForegroundWindow()
    tid_fg = user32.GetWindowThreadProcessId(fg, None)
    tid_me = kernel32.GetCurrentThreadId()
    user32.AttachThreadInput(tid_me, tid_fg, True)
    user32.ShowWindow(hwnd, 9)          # SW_RESTORE
    # 置顶，避免被其它窗口（如 WorkBuddy 主窗口）遮挡而吃掉点击
    user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW)
    user32.BringWindowToTop(hwnd)
    user32.SetForegroundWindow(hwnd)
    user32.AttachThreadInput(tid_me, tid_fg, False)
    time.sleep(0.45)
    return user32.GetForegroundWindow() == hwnd


def assert_topmost(hwnd, x, y):
    """点击前校验：该屏幕坐标命中的顶层窗口是否属于目标。"""
    pt = wintypes.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    sx, sy = pt.x + x, pt.y + y
    hit = user32.WindowFromPoint(wintypes.POINT(sx, sy))
    root = user32.GetAncestor(hit, 2)
    return sx, sy, root == hwnd, root


def real_click(hwnd, x, y):
    sx = sy = 0
    ok = False
    for attempt in range(4):
        sx, sy, ok, root = assert_topmost(hwnd, x, y)
        if ok:
            break
        # 被遮挡：重新置顶再试
        user32.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040)
        user32.BringWindowToTop(hwnd)
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.35)
    user32.SetCursorPos(sx, sy)
    time.sleep(0.45)
    n = send_click()
    print("   SendInput ->", n)
    time.sleep(0.1)
    return sx, sy, ok


page = sys.argv[1] if len(sys.argv) > 1 else "container"
clicks = []
i = 2
while i + 1 < len(sys.argv) + 1 and i + 1 <= len(sys.argv):
    try:
        clicks.append((int(sys.argv[i]), int(sys.argv[i + 1])))
    except Exception:
        break
    i += 2

shots = [a for a in sys.argv if a.startswith("shot=")]
outname = shots[-1].split("=", 1)[1] if shots else "interact"

p = subprocess.Popen([EXE, f"--page={page}"], cwd=os.path.dirname(EXE))
time.sleep(7.0)
wins = S.find_window(TITLE, 400, 300)
if not wins:
    print("NOT_FOUND")
    p.terminate()
    sys.exit(1)
hwnd, rect = wins[0]

fg = force_foreground(hwnd)
print("foreground =", fg, " hwnd =", hwnd)

# 第一次点击只用于激活
user32.SetCursorPos(rect.left + 700, rect.top + 40)
time.sleep(0.18)
send_click()
time.sleep(0.6)

for (x, y) in clicks:
    sx, sy, ok = real_click(hwnd, x, y)
    print(f"click ({x},{y}) -> screen({sx},{sy}) hit_ok={ok}")
    time.sleep(1.6)

out = os.path.join(OUTDIR, f"{outname}.png")
target_title = os.environ.get("SHOT_TITLE", TITLE)
w2 = S.find_window(target_title, 300, 200)
if not w2:
    print("TARGET_WINDOW_NOT_FOUND:", target_title)
    print("--- 本进程(pid=%d)的所有窗口 ---" % p.pid)
    found_any = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def _cb(h, lp):
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        if pid.value == p.pid:
            n = user32.GetWindowTextLengthW(h)
            buf = ctypes.create_unicode_buffer(n + 2)
            user32.GetWindowTextW(h, buf, n + 2)
            r = wintypes.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            vis = bool(user32.IsWindowVisible(h))
            print(f"   hwnd={h} visible={vis} title={buf.value!r} rect=({r.left},{r.top})-({r.right},{r.bottom})")
            found_any.append((h, r))
        return True

    user32.EnumWindows(_cb, 0)
    if not found_any:
        print("   (无)")
    w2 = S.find_window(TITLE, 400, 300)
if w2:
    S.grab(w2[0][0], w2[0][1], out)
    print("saved", out, "window:", target_title)
else:
    print("WINDOW_GONE")

time.sleep(0.3)
p.terminate()
try:
    p.wait(timeout=5)
except Exception:
    p.kill()
