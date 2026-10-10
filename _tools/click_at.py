"""驱动 WPF 窗口：置顶 → 按客户区物理坐标点击 → 截图。
用法: click_at.py <窗口标题> <x物理> <y物理> [点击后截图标题] [截图输出]
"""
import ctypes, os, sys, time, zlib, struct
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

PW_RENDERFULLCONTENT = 0x00000002
SWP_NOSIZE = 0x0001
SWP_SHOWWINDOW = 0x0040
HWND_TOPMOST = -1


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", ctypes.c_long),
                ("biHeight", ctypes.c_long), ("biPlanes", wintypes.WORD),
                ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wintypes.DWORD),
                ("biClrImportant", wintypes.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


def find_window(title, min_w=300, min_h=200):
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, lp):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        if n == 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        if buf.value == title:
            r = RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(r))
            if (r.right - r.left) >= min_w and (r.bottom - r.top) >= min_h:
                found.append((hwnd, r))
        return True

    user32.EnumWindows(cb, 0)
    return found


def grab(hwnd, rect, path):
    w = rect.right - rect.left
    h = rect.bottom - rect.top
    hdc_win = user32.GetWindowDC(hwnd)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_win)
    hbmp = gdi32.CreateCompatibleBitmap(hdc_win, w, h)
    gdi32.SelectObject(hdc_mem, hbmp)
    user32.PrintWindow(hwnd, hdc_mem, PW_RENDERFULLCONTENT)
    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(hwnd, hdc_win)
    data = bytearray(buf.raw)
    rows = []
    for y in range(h):
        row = bytearray()
        off = y * w * 4
        for x in range(w):
            i = off + x * 4
            row += bytes((data[i + 2], data[i + 1], data[i]))
        rows.append(bytes(row))
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, d):
        return struct.pack(">I", len(d)) + tag + d + struct.pack(">I", zlib.crc32(tag + d) & 0xFFFFFFFF)

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(png)


def do_click(hwnd, x, y):
    pt = wintypes.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    sx, sy = pt.x + x, pt.y + y
    user32.SetCursorPos(sx, sy)
    time.sleep(0.14)
    user32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.07)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    return sx, sy


def main():
    title = sys.argv[1]
    x = int(sys.argv[2])
    y = int(sys.argv[3])
    shot_title = sys.argv[4] if len(sys.argv) > 4 else ""
    shot_out = sys.argv[5] if len(sys.argv) > 5 else ""

    wins = find_window(title)
    if not wins:
        print("NOT_FOUND", title)
        sys.exit(1)
    hwnd, r = wins[0]

    user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.4)

    # 先点标题栏空白把窗口真正激活（不在前台时首次点击会被吃掉）
    # SKIP_ACT=1 时跳过，避免抢走表格里正在编辑的焦点
    if os.environ.get("SKIP_ACT") != "1":
        tx, ty = do_click(hwnd, 600, 40)
        time.sleep(0.5)
        print(f"activate click at ({tx},{ty}) foreground={user32.GetForegroundWindow() == hwnd}")

    sx, sy = do_click(hwnd, x, y)
    hit = user32.WindowFromPoint(wintypes.POINT(sx, sy))
    root = user32.GetAncestor(hit, 2)
    print(f"click at ({sx},{sy}) hit={hit} root={root} target={hwnd} match={root == hwnd}")
    time.sleep(1.2)

    if not shot_title or not shot_out:
        return

    wins2 = find_window(shot_title, 200, 150)
    if not wins2:
        print("retry click (first one likely eaten by activation)")
        do_click(hwnd, x, y)
        time.sleep(1.6)
        wins2 = find_window(shot_title, 200, 150)

    if not wins2:
        print("AFTER_SHOT_NOT_FOUND", shot_title)
        return

    h2, r2 = wins2[0]
    grab(h2, r2, shot_out)
    print(f"saved {shot_out} ({r2.right - r2.left}x{r2.bottom - r2.top})")


main()

