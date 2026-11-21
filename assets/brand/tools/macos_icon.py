# SPDX-License-Identifier: AGPL-3.0-or-later
"""The macOS app icon: the Sheet mark with its blue tile as the rounded body (Apple's 1024
grid: an 824 × 824 body at 100, 100, continuous corners), plus washi.icns via iconutil.

Needs only Pillow:  python3 assets/brand/tools/macos_icon.py
"""
import math, os, subprocess, tempfile
from PIL import Image, ImageDraw

BRAND = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLUE, ACCENT, PAPER, FOLD_TINT = (31, 73, 224), (224, 69, 43), (255, 255, 255), (177, 191, 244)
# the mark on its 512 artboard (see build.py)
PAGE, FOLD = (96, 64, 416, 448), 96
CELLS, CELL_W, CELL_H = [(144, 224), (272, 224), (144, 336), (272, 336)], 96, 80


def squircle(n=5.0, size=824, steps=720):
    """A superellipse |x|^n + |y|^n = 1: the continuous-curvature corner macOS icons use."""
    r = size / 2
    pts = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        c, s = math.cos(t), math.sin(t)
        pts.append((r + r * math.copysign(abs(c) ** (2 / n), c), r + r * math.copysign(abs(s) ** (2 / n), s)))
    return pts


def icon(px):
    k = 4                                   # supersample, then downscale
    W = 1024 * k
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    body = [(100 * k + x * k, 100 * k + y * k) for x, y in squircle()]
    d.polygon(body, fill=BLUE)
    # the page and boxes, at the tile's own proportions inside the body
    scale = 824 / 512 * k

    def P(x, y):
        return (100 * k + x * scale, 100 * k + y * scale)

    x0, y0, x1, y1 = PAGE
    d.polygon([P(x0, y0), P(x1 - FOLD, y0), P(x1, y0 + FOLD), P(x1, y1), P(x0, y1)], fill=PAPER)
    d.polygon([P(x1 - FOLD, y0), P(x1 - FOLD, y0 + FOLD), P(x1, y0 + FOLD)], fill=FOLD_TINT)
    for n, (cx, cy) in enumerate(CELLS):
        d.polygon([P(cx, cy), P(cx + CELL_W, cy), P(cx + CELL_W, cy + CELL_H), P(cx, cy + CELL_H)],
                  fill=ACCENT if n == 3 else BLUE)
    return img.resize((px, px), Image.LANCZOS)


out = os.path.join(BRAND, "png", "macos")
os.makedirs(out, exist_ok=True)
for px in (16, 32, 64, 128, 256, 512, 1024):
    icon(px).save(os.path.join(out, f"icon-macos-{px}.png"))

with tempfile.TemporaryDirectory() as tmp:
    iconset = os.path.join(tmp, "washi.iconset")
    os.makedirs(iconset)
    for base in (16, 32, 128, 256, 512):
        icon(base).save(os.path.join(iconset, f"icon_{base}x{base}.png"))
        icon(base * 2).save(os.path.join(iconset, f"icon_{base}x{base}@2x.png"))
    subprocess.run(["iconutil", "-c", "icns", iconset, "-o", os.path.join(BRAND, "washi.icns")], check=True)
print("wrote", out, "and washi.icns")
