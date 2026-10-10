"""启动 WSLCManager → 等窗口 → PrintWindow 截图 → 收尾（同一条命令内完成）。"""
import os, sys, time, subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import shot

EXE = r"D:\workbuddy\wslc-manager\bin\Release\net8.0-windows\WSLCManager.exe"
TITLE = "WSLC 容器管理器"

out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "_shots", "main.png")
wait = float(sys.argv[2]) if len(sys.argv) > 2 else 7.0
keep = (len(sys.argv) > 3 and sys.argv[3] == "keep")

os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)

p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE))
print("pid =", p.pid)
time.sleep(wait)

if p.poll() is not None:
    print("PROCESS_EXITED code =", p.returncode)
    sys.exit(2)

wins = shot.find_window(TITLE, min_w=400, min_h=300)
if not wins:
    print("NOT_FOUND:", TITLE)
    print("--- 当前可见窗口 ---")
    subprocess.run([sys.executable, os.path.join(HERE, "wins.py")])
    p.terminate()
    sys.exit(1)

hwnd, rect = wins[0]
ok, w, h, white, black = shot.grab(hwnd, rect, out)
print(f"hwnd={hwnd} size={w}x{h} printwindow={ok} white={white:.3f} black={black:.3f}")
print("saved:", os.path.abspath(out))

if not keep:
    time.sleep(0.5)
    p.terminate()
    try:
        p.wait(timeout=5)
    except Exception:
        p.kill()
    print("terminated")
