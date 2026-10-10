"""真实屏幕抓取（含 ClearType 亚像素条纹）。

为什么不用程序自带的 --screenshot：
  WPF 的 RenderTargetBitmap 渲染路径**必然退化成灰度抗锯齿**（MSDN: ClearType
  在非不透明表面上不可用），所以它拍出来的字看不出 ClearType 有没有生效。
  这里用 GDI 截屏（PIL ImageGrab → BitBlt 屏幕 DC），拿到的是屏幕上真实像素。

用法：
  python _tools/screengrab.py <exe路径> <输出png> [--page=container] [--wait=3.5]
"""
import ctypes
import subprocess
import sys
import time
from ctypes import wintypes

from PIL import ImageGrab

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.FindWindowW.restype = wintypes.HWND
u32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
u32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                             ctypes.c_int, ctypes.c_int, ctypes.c_uint]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
u32.GetForegroundWindow.restype = wintypes.HWND
u32.SetForegroundWindow.argtypes = [wintypes.HWND]
u32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]

HWND_TOPMOST = -1
SWP_NOMOVE, SWP_NOSIZE, SWP_SHOWWINDOW = 0x0002, 0x0001, 0x0040
TITLE = "WSLC 容器管理器"


def find_hwnd(timeout=15.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        h = u32.FindWindowW(None, TITLE)
        if h:
            return h
        time.sleep(0.25)
    return 0


def main():
    args = sys.argv[1:]
    exe, out = args[0], args[1]
    page = next((a for a in args if a.startswith("--page=")), "--page=container")
    wait = float(next((a.split("=")[1] for a in args if a.startswith("--wait=")), 4.0))

    p = subprocess.Popen([exe, page])
    try:
        hwnd = find_hwnd()
        if not hwnd:
            print("!! 找不到窗口", TITLE)
            return 2
        print("hwnd =", hwnd)

        u32.ShowWindow(hwnd, 5)                       # SW_SHOW
        u32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW)
        u32.SetForegroundWindow(hwnd)
        time.sleep(wait)

        fg = u32.GetForegroundWindow()
        print("foreground =", fg, " 是本窗口 ?", fg == hwnd)

        r = wintypes.RECT()
        if not u32.GetWindowRect(hwnd, ctypes.byref(r)):
            print("!! GetWindowRect 失败")
            return 3
        bbox = (r.left, r.top, r.right, r.bottom)
        print("rect =", bbox)

        img = ImageGrab.grab(bbox=bbox, all_screens=True).convert("RGB")
        img.save(out)
        print("saved", out, img.size)
        return 0 if fg == hwnd else 1
    finally:
        p.terminate()
        try:
            p.wait(timeout=5)
        except Exception:
            p.kill()


if __name__ == "__main__":
    sys.exit(main())
