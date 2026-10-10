"""启动 exe → PrintWindow 抓图 → 存 PNG。仅用标准库 + ctypes。"""
import ctypes, os, subprocess, sys, time, zlib, struct
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

PW_RENDERFULLCONTENT = 0x00000002
SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0


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


def find_window(title, min_w=600, min_h=400):
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
            w, h = r.right - r.left, r.bottom - r.top
            if w >= min_w and h >= min_h:
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
    ok = user32.PrintWindow(hwnd, hdc_mem, PW_RENDERFULLCONTENT)

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h          # top-down
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = DIB_RGB_COLORS

    bufsize = w * h * 4
    buf = ctypes.create_string_buffer(bufsize)
    lines = gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bi), DIB_RGB_COLORS)

    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(hwnd, hdc_win)

    if lines == 0:
        raise RuntimeError("GetDIBits failed")

    data = bytearray(buf.raw)
    # BGRA -> RGB
    rows = []
    for y in range(h):
        row = bytearray()
        off = y * w * 4
        for x in range(w):
            i = off + x * 4
            row += bytes((data[i + 2], data[i + 1], data[i]))
        rows.append(bytes(row))
    write_png(path, w, h, rows)

    # 统计
    white = black = 0
    total = w * h
    for y in range(0, h, 3):
        off = y * w * 3
        for x in range(0, w, 3):
            i = off + x * 3
            s = rows[y][x * 3] + rows[y][x * 3 + 1] + rows[y][x * 3 + 2]
            if s >= 750:
                white += 1
            if s < 60:
                black += 1
    sampled = len(range(0, h, 3)) * len(range(0, w, 3))
    return ok, w, h, white / max(sampled, 1), black / max(sampled, 1)


def write_png(path, w, h, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 6))
           + chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(png)


if __name__ == "__main__":
    title = sys.argv[1] if len(sys.argv) > 1 else "SSH 免密助手"
    out = sys.argv[2] if len(sys.argv) > 2 else "shot.png"
    wait = float(sys.argv[3]) if len(sys.argv) > 3 else 0

    if wait:
        time.sleep(wait)

    wins = find_window(title)
    if not wins:
        print("NOT_FOUND", title)
        sys.exit(1)

    hwnd, rect = wins[0]
    ok, w, h, white, black = grab(hwnd, rect, out)
    print(f"hwnd={hwnd} size={w}x{h} printwindow={ok} white={white:.3f} black={black:.3f} -> {out}")
