from PIL import Image, ImageDraw

S = 512
c1 = (43, 108, 246)    # #2B6CF6
c2 = (17, 66, 186)     # 深蓝

base = Image.new('RGB', (S, S))
d = ImageDraw.Draw(base)
for y in range(S):
    t = y / (S - 1)
    d.line([(0, y), (S, y)], fill=tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3)))

mask = Image.new('L', (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.24), fill=255)

img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
img.paste(base, (0, 0), mask)

# 集装箱「箱体」
ov = Image.new('RGBA', (S, S), (0, 0, 0, 0))
od = ImageDraw.Draw(ov)
W = int(S * 0.62)
H = int(S * 0.40)
x0 = (S - W) // 2
y0 = (S - H) // 2 + int(S * 0.02)

od.rounded_rectangle([x0, y0, x0 + W, y0 + H], radius=int(S * 0.05), fill=(255, 255, 255, 255))

# 箱体波纹分隔（用背景蓝制造缝隙感）
gap = max(2, int(S * 0.020))
n = 4
for i in range(1, n):
    xx = x0 + W * i // n
    od.rectangle([xx - gap, y0 + int(H * 0.14), xx + gap, y0 + H - int(H * 0.14)],
                 fill=(26, 79, 214, 255))

# 顶部提手横梁
od.rounded_rectangle([x0 + int(W * 0.16), y0 - int(S * 0.075),
                      x0 + int(W * 0.84), y0 - int(S * 0.030)],
                     radius=int(S * 0.018), fill=(255, 255, 255, 255))

img = Image.alpha_composite(img, ov)

# 左下角小圆点（象征运行指示灯）
dot = Image.new('RGBA', (S, S), (0, 0, 0, 0))
ImageDraw.Draw(dot).ellipse([int(S * 0.18), int(S * 0.74), int(S * 0.30), int(S * 0.86)],
                            fill=(125, 232, 175, 255))
img = Image.alpha_composite(img, dot)

img.save(r"D:\workbuddy\wslc-manager\app.ico",
         sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
img.resize((256, 256), Image.LANCZOS).save(r"D:\workbuddy\wslc-manager\_tools\icon_preview.png")
print("OK")
