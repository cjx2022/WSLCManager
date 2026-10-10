"""逐个页面启动截图：用 --page= 参数直接打开目标页。"""
import os, sys, time, subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import shot as S

EXE = r"D:\workbuddy\wslc-manager\bin\Release\net8.0-windows\WSLCManager.exe"
TITLE = "WSLC 容器管理器"
OUTDIR = os.path.abspath(os.path.join(HERE, "..", "_shots"))
os.makedirs(OUTDIR, exist_ok=True)

pages = sys.argv[1].split(",") if len(sys.argv) > 1 else ["env", "container", "image", "settings"]
wait = float(sys.argv[2]) if len(sys.argv) > 2 else 7.0
EXTRA = os.environ.get("EXTRA_ARGS", "").split() if os.environ.get("EXTRA_ARGS") else []

for pg in pages:
    p = subprocess.Popen([EXE, f"--page={pg}"] + EXTRA, cwd=os.path.dirname(EXE))
    time.sleep(wait)
    if p.poll() is not None:
        print(f"{pg}: PROCESS_EXITED {p.returncode}")
        continue
    wins = S.find_window(TITLE, 400, 300)
    if not wins:
        print(f"{pg}: NOT_FOUND")
        p.terminate()
        continue
    hwnd, rect = wins[0]
    out = os.path.join(OUTDIR, f"page_{pg}.png")
    ok, w, h, white, black = S.grab(hwnd, rect, out)
    print(f"{pg:10} {w}x{h} white={white:.4f} -> {os.path.basename(out)}")
    p.terminate()
    try:
        p.wait(timeout=5)
    except Exception:
        p.kill()
    time.sleep(0.6)
print("done")
