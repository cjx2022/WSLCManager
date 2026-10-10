"""由 Assets/logo.png 自身派生全部品牌图形（自包含，不依赖任何外部源图）：

  1) Assets/logo.png        —— 收紧到主体 + 居中，主体占画布宽度 SUBJECT_FRAC
  2) app.ico                —— 白色圆角底多尺寸图标（应用/任务栏用）
  3) _tools/logo_preview.png —— 浅色底 / 深色底预览，供人工目视确认

为什么这么做：原始线稿四边留白很大（主体只占画布宽约 85%、高约 55%），
放到标题栏 28px 的方框里时，鲸鱼实际只有十几像素高，显得很小。
这里把画布收紧到「主体 / SUBJECT_FRAC」，主体占比随之上调。

脚本是**幂等**的：以自身 alpha 边界为输入，重复运行结果不变。

用法：python _tools/make_logo.py
"""
import os

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "Assets")
LOGO = os.path.join(ASSETS, "logo.png")

# 主体（鲸鱼 + Windows 徽标）占正方形画布宽度的比例。越大越"满"，
# 但要给线稿边缘留一点余量，避免贴边。
SUBJECT_FRAC = 0.92
# 圆角底图标里，四边留白占图标的比例。
ICON_PAD_FRAC = 0.06
ICON_RADIUS_FRAC = 0.22
ICON_SIZES = [16, 24, 32, 48, 64, 128, 256]


def load_trimmed():
    """读入 logo.png，按 alpha 边界裁到主体，居中放进正方形画布。"""
    im = Image.open(LOGO).convert("RGBA")
    bbox = im.split()[-1].getbbox()
    if bbox is None:
        raise SystemExit("logo.png 全透明，无法取主体边界")
    content = im.crop(bbox)
    cw, ch = content.size

    side = int(round(max(cw, ch) / SUBJECT_FRAC))
    out = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    out.alpha_composite(content, ((side - cw) // 2, (side - ch) // 2))
    print(f"主体 {cw}x{ch} → 画布 {side}x{side}"
          f"（主体占宽 {cw / side:.1%}、占高 {ch / side:.1%}）")
    return out


def make_icon(logo, size):
    """白色圆角底 + logo，用于文件/任务栏（深色任务栏上也能看清）。"""
    pad = max(1, int(round(size * ICON_PAD_FRAC)))
    tile = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(tile).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=int(size * ICON_RADIUS_FRAC),
        fill=(255, 255, 255, 255))
    inner = size - pad * 2
    tile.alpha_composite(logo.resize((inner, inner), Image.LANCZOS), (pad, pad))
    return tile


def main():
    logo = load_trimmed()
    logo.save(LOGO)
    print("saved", os.path.relpath(LOGO, ROOT))

    frames = [make_icon(logo, s) for s in ICON_SIZES]
    frames[-1].save(os.path.join(ROOT, "app.ico"), format="ICO",
                    sizes=[(s, s) for s in ICON_SIZES])
    print("saved app.ico")

    # 预览：左半浅色底看图标，右半深色底看透明版
    prev = Image.new("RGB", (820, 300), (247, 248, 251))
    for s, pos in ((256, (20, 22)), (64, (300, 120)), (32, (390, 140)), (16, (450, 150))):
        ic = make_icon(logo, s)
        prev.paste(ic, pos, ic)
    dark = Image.new("RGB", (330, 256), (30, 34, 42))
    prev.paste(dark, (480, 22))
    big = logo.resize((256, 256), Image.LANCZOS)
    prev.paste(big, (517, 22), big)
    prev.save(os.path.join(ROOT, "_tools", "logo_preview.png"))
    print("saved _tools/logo_preview.png")


if __name__ == "__main__":
    main()
