"""用程序自身截图。GRAB=1 时连 DWM 非客户区一起抓（用于验证窗口外框）。"""
import os, sys, subprocess, time

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = r"D:\workbuddy\wslc-manager\bin\Release\net8.0-windows\WSLCManager.exe"
OUT = os.path.abspath(os.path.join(HERE, "..", "_shots"))
os.makedirs(OUT, exist_ok=True)

pages = sys.argv[1].split(",") if len(sys.argv) > 1 else ["container"]
grab = os.environ.get("GRAB", "1") == "1"

for pg in pages:
    out = os.path.join(OUT, f"ui_{pg}.png")
    for f in (out, out + ".err.txt"):
        if os.path.exists(f):
            try: os.remove(f)
            except Exception: pass

    args = [EXE, f"--page={pg}", f"--screenshot={out}", "--exit-after-shot"]
    if grab:
        args.append("--grab")
    subprocess.Popen(args, cwd=os.path.dirname(EXE))

    for _ in range(40):
        time.sleep(0.5)
        if os.path.exists(out) and os.path.getsize(out) > 2000:
            break

    if os.path.exists(out):
        print(f"{pg:10} OK {os.path.getsize(out)} bytes")
    else:
        print(f"{pg:10} FAILED")
        errf = out + ".err.txt"
        if os.path.exists(errf):
            print(open(errf, encoding="utf-8", errors="replace").read()[:400])
    time.sleep(1.0)
print("done")
