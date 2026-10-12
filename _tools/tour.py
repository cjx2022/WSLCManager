"""启动 → 依次切换左侧导航 → 每页截图 → 收尾。
用 SendMessage 投递鼠标消息（不依赖窗口是否在前台），坐标取自像素扫描结果。"""
import os, sys, time, subprocess, ctypes
from ctypes import wintypes

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import shot as S

user32 = ctypes.WinDLL("user32", use_last_error=True)
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

WM_MOUSEMOVE = 0x0200
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202

ROOT = os.path.dirname(HERE)          # 仓库根目录 = _tools/ 的上一级
EXE = os.path.join(ROOT, "bin", "Release", "net8.0-windows", "WSLCManager.exe")
TITLE = "WSLC 容器管理器"
OUTDIR = os.path.abspath(os.path.join(HERE, "..", "_shots"))

# 实测导航项中心（客户区物理像素）
NAV = [
    ("env",       150, 152),
    ("container", 150, 226),
    ("image",     150, 299),
    ("stats",     150, 371),
    ("settings",  150, 445),
]

wait = float(sys.argv[1]) if len(sys.argv) > 1 else 8.0
os.makedirs(OUTDIR, exist_ok=True)

p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE))
print("pid =", p.pid)
time.sleep(wait)

wins = S.find_window(TITLE, 400, 300)
if not wins:
    print("NOT_FOUND")
    p.terminate()
    sys.exit(1)
hwnd, rect = wins[0]
print("hwnd =", hwnd, "size =", rect.right - rect.left, rect.bottom - rect.top)


def click(x, y, real=False):
    lp = (y << 16) | (x & 0xFFFF)
    user32.SendMessageW(hwnd, WM_MOUSEMOVE, 0, lp)
    time.sleep(0.05)
    user32.SendMessageW(hwnd, WM_LBUTTONDOWN, 1, lp)
    time.sleep(0.06)
    user32.SendMessageW(hwnd, WM_LBUTTONUP, 0, lp)


def grab(name):
    hwnd2, rect2 = S.find_window(TITLE, 400, 300)[0]
    out = os.path.join(OUTDIR, f"page_{name}.png")
    ok, w, h, white, black = S.grab(hwnd2, rect2, out)
    print(f"{name:10} -> {os.path.basename(out)}  {w}x{h} white={white:.4f}")
    return out


# 目标：切换后高亮应变化，用文件哈希粗验
import hashlib
def sig(path):
    return hashlib.md5(open(path, "rb").read()).hexdigest()[:10]

base = grab("_base")
print("base sig:", sig(base))

seen = {}
for name, x, y in NAV:
    click(x, y)
    time.sleep(1.4)
    f = grab(name)
    s = sig(f)
    print(f"   sig={s}")
    seen[name] = s

time.sleep(0.3)
p.terminate()
try:
    p.wait(timeout=5)
except Exception:
    p.kill()
print("done; distinct sigs:", len(set(seen.values())), "/", len(seen))
