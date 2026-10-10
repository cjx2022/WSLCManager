"""从截图中扫描左侧导航栏的文本行位置，输出各项中心坐标（物理像素）。"""
import sys
from PIL import Image

path = sys.argv[1]
x0 = int(sys.argv[2]) if len(sys.argv) > 2 else 40
x1 = int(sys.argv[3]) if len(sys.argv) > 3 else 300

im = Image.open(path).convert("RGB")
W, H = im.size
px = im.load()
print("image size:", W, H)

rows = []
for y in range(H):
    cnt = 0
    for x in range(x0, min(x1, W)):
        r, g, b = px[x, y]
        lum = 0.299 * r + 0.587 * g + 0.114 * b
        if lum < 175:
            cnt += 1
    rows.append(cnt)

# 找连续非零段（允许 2 行空隙）
segs = []
start = None
gap = 0
for y, c in enumerate(rows):
    if c > 0:
        if start is None:
            start = y
        gap = 0
    else:
        if start is not None:
            gap += 1
            if gap > 3:
                segs.append((start, y - gap))
                start = None
                gap = 0
if start is not None:
    segs.append((start, len(rows) - 1))

print(f"found {len(segs)} text segments in x[{x0},{x1}]:")
for i, (a, b) in enumerate(segs):
    print(f"  #{i}: y {a}..{b}  center={((a+b)//2)}  h={b-a+1}")
