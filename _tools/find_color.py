"""扫描截图中指定颜色的连通区域，输出 bbox（用于定位按钮）。"""
import sys
from PIL import Image

path = sys.argv[1]
tr = int(sys.argv[2]); tg = int(sys.argv[3]); tb = int(sys.argv[4])
tol = int(sys.argv[5]) if len(sys.argv) > 5 else 26

im = Image.open(path).convert("RGB")
W, H = im.size
px = im.load()

mask = [[False] * W for _ in range(H)]
for y in range(H):
    for x in range(W):
        r, g, b = px[x, y]
        if abs(r - tr) <= tol and abs(g - tg) <= tol and abs(b - tb) <= tol:
            mask[y][x] = True

# 简单连通域（4 邻域，栈式）
seen = [[False] * W for _ in range(H)]
boxes = []
for y in range(H):
    for x in range(W):
        if mask[y][x] and not seen[y][x]:
            stack = [(x, y)]
            seen[y][x] = True
            minx = maxx = x
            miny = maxy = y
            n = 0
            while stack:
                cx, cy = stack.pop()
                n += 1
                if cx < minx: minx = cx
                if cx > maxx: maxx = cx
                if cy < miny: miny = cy
                if cy > maxy: maxy = cy
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = cx + dx, cy + dy
                    if 0 <= nx < W and 0 <= ny < H and mask[ny][nx] and not seen[ny][nx]:
                        seen[ny][nx] = True
                        stack.append((nx, ny))
            if n > 60:
                boxes.append((minx, miny, maxx, maxy, n))

boxes.sort(key=lambda b: -b[4])
print(f"image {W}x{H}  color=({tr},{tg},{tb})  regions={len(boxes)}")
for b in boxes[:12]:
    print(f"  bbox=({b[0]},{b[1]})-({b[2]},{b[3]})  w={b[2]-b[0]+1} h={b[3]-b[1]+1} px={b[4]}  center=({(b[0]+b[2])//2},{(b[1]+b[3])//2})")
