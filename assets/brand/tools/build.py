# SPDX-License-Identifier: AGPL-3.0-or-later
"""Rebuild every Washi brand asset from code.

The mark ("Fold grid") is pure geometry on an 8-unit grid. Lettering is IBM Plex
Sans / IBM Plex Sans JP (SIL OFL 1.1), shaped with HarfBuzz and outlined to SVG
paths, so the SVGs need no installed fonts.

Requirements (macOS: Quick Look renders the SVGs to PNG):
    python3 -m venv .venv && .venv/bin/pip install fonttools brotli uharfbuzz pillow
    npm pack @ibm/plex-sans @ibm/plex-sans-jp
    mkdir -p $PLEX_DIR/sans $PLEX_DIR/jp
    tar -xzf ibm-plex-sans-*.tgz -C $PLEX_DIR/sans && tar -xzf ibm-plex-sans-jp-*.tgz -C $PLEX_DIR/jp
    PLEX_DIR=... .venv/bin/python assets/brand/tools/build.py
"""
import io, os, subprocess, tempfile
import uharfbuzz as hb
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from PIL import Image

BRAND = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLEX = os.environ["PLEX_DIR"]
SANS_SEMI = f"{PLEX}/sans/package/fonts/complete/woff2/IBMPlexSans-SemiBold.woff2"
SANS_MED = f"{PLEX}/sans/package/fonts/complete/woff2/IBMPlexSans-Medium.woff2"
SANS_REG = f"{PLEX}/sans/package/fonts/complete/woff2/IBMPlexSans-Regular.woff2"
JP_REG = f"{PLEX}/jp/package/fonts/complete/woff2/hinted/IBMPlexSansJP-Regular.woff2"

# palette
INK = "#161616"
BLUE = "#1f49e0"          # Washi blue (藍)
BLUE_ON_DARK = "#4f7bff"
ACCENT = "#e0452b"        # vermilion (朱)
PAPER = "#ffffff"
MIST = "#f4f4f4"
GRAY = "#6f6f6f"
GRAY_ON_DARK = "#a8a8a8"
RULE = "#e3e3e3"

# ------------------------------------------------------------------ geometry
# 512 x 512 artboard, 8-unit grid: two 168 squares, 16 gutter, 80 margin.
S, G, M = 168, 16, 80
B = M + S + G                 # second column / row
SPAN = 2 * S + G              # 352: the drawn part of the artboard


def mark(blue=BLUE, accent=ACCENT, ox=0.0, oy=0.0, k=1.0):
    """Fold grid: four squares. The top-right one is folded over (only its lower-left
    half remains); the bottom-right one is the field being filled."""
    def p(x, y):
        return f"{ox + x * k:.2f} {oy + y * k:.2f}"

    def sq(x, y, fill):
        return f'<path d="M{p(x, y)}H{ox + (x + S) * k:.2f}V{oy + (y + S) * k:.2f}H{ox + x * k:.2f}Z" fill="{fill}"/>'

    return (sq(M, M, blue)
            + f'<path d="M{p(B, M)}L{p(B + S, M + S)}H{ox + B * k:.2f}Z" fill="{blue}"/>'
            + sq(M, B, blue)
            + sq(B, B, accent))


def mark_mono():
    """Single colour (currentColor): the active field becomes an outlined box."""
    w = 16
    return (f'<path d="M{M} {M}h{S}v{S}h-{S}Z"/>'
            f'<path d="M{B} {M}L{B + S} {M + S}H{B}Z"/>'
            f'<path d="M{M} {B}h{S}v{S}h-{S}Z"/>'
            f'<path d="M{B} {B}h{S}v{S}h-{S}Z M{B + w} {B + w}v{S - 2 * w}h{S - 2 * w}v-{S - 2 * w}Z" '
            f'fill-rule="evenodd"/>')


def placed_mark(x, y, size, **kw):
    """Mark whose squares cover exactly size x size at (x, y)."""
    k = size / SPAN
    return mark(ox=x - M * k, oy=y - M * k, k=k, **kw)


# ------------------------------------------------------------------ type
_fonts = {}


def _font(path):
    if path not in _fonts:
        tt = TTFont(path)
        tt.flavor = None
        buf = io.BytesIO()
        tt.save(buf)
        data = buf.getvalue()
        _fonts[path] = (TTFont(io.BytesIO(data)), hb.Font(hb.Face(data)))
    return _fonts[path]


def text(s, path, size, x, y, fill, tracking=0.0):
    """Outlined, kerned text. (x, y) is the baseline start. Returns (svg, advance)."""
    tt, font = _font(path)
    upm = tt["head"].unitsPerEm
    buf = hb.Buffer()
    buf.add_str(s)
    buf.guess_segment_properties()
    hb.shape(font, buf, {"kern": True, "liga": True})
    gs, order, k = tt.getGlyphSet(), tt.getGlyphOrder(), size / upm
    pen, adv = SVGPathPen(gs), 0.0
    for info, pos in zip(buf.glyph_infos, buf.glyph_positions):
        tp = TransformPen(pen, (k, 0, 0, -k, x + (adv + pos.x_offset) * k, y - pos.y_offset * k))
        gs[order[info.codepoint]].draw(tp)
        adv += pos.x_advance + tracking * upm
    return f'<path d="{pen.getCommands()}" fill="{fill}"/>', adv * k


def cap(path, size):
    tt, _ = _font(path)
    return tt["OS/2"].sCapHeight * size / tt["head"].unitsPerEm


# ------------------------------------------------------------------ compositions
def svg(w, h, body, bg=None, title="Washi", extra=""):
    b = f'<rect width="{w}" height="{h}" fill="{bg}"/>' if bg else ""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" '
            f'role="img" aria-label="{title}"{extra}><title>{title}</title>{b}{body}</svg>\n')


def lockup(dark=False, jp=True):
    """Mark (96) + 'Washi' with cap height = half the mark (+ divider + 和紙)."""
    h = 96
    size = (h * 0.5) / (cap(SANS_SEMI, 1000) / 1000)
    blue, ink, gray = (BLUE_ON_DARK, PAPER, GRAY_ON_DARK) if dark else (BLUE, INK, GRAY)
    body = placed_mark(0, 0, h, blue=blue)
    ch = cap(SANS_SEMI, size)
    base = h / 2 + ch / 2
    tx = h + h * 0.36                                       # gap after the mark
    t, adv = text("Washi", SANS_SEMI, size, tx, base, ink, tracking=-0.005)
    body += t
    x = tx + adv
    if jp:
        gap = h * 0.22
        body += f'<rect x="{x + gap:.2f}" y="{base - ch:.2f}" width="2" height="{ch:.2f}" fill="{gray}"/>'
        t2, adv2 = text("和紙", JP_REG, size * 0.5, x + 2 * gap + 2, base, gray)
        body += t2
        x += 2 * gap + 2 + adv2
    return body, x, h


def social():
    W, H = 1280, 640
    body = []
    for gx in range(0, W + 1, 32):                       # the マス目 under everything
        body.append(f'<rect x="{gx}" y="0" width="1" height="{H}" fill="{RULE}"/>')
    for gy in range(0, H + 1, 32):
        body.append(f'<rect x="0" y="{gy}" width="{W}" height="1" fill="{RULE}"/>')
    body.append(f'<rect x="96" y="128" width="384" height="384" fill="{PAPER}"/>')
    body.append(placed_mark(160, 192, 256))
    t, adv = text("Washi", SANS_SEMI, 128, 544, 320, INK, tracking=-0.01)
    body.append(t)
    t, _ = text("和紙", JP_REG, 40, 544 + adv + 32, 320, GRAY)
    body.append(t)
    t, _ = text("Flat PDF forms → fillable. With AI and MCP.", SANS_REG, 32, 548, 392, INK)
    body.append(t)
    t, _ = text("Open-source PDF toolkit  ·  Desktop / CLI / MCP / AI", SANS_MED, 20, 548, 448, GRAY)
    body.append(t)
    return svg(W, H, "".join(body), bg=MIST, title="Washi - open-source PDF toolkit")


# ------------------------------------------------------------------ rendering
def render(svg_text, png_path, w, h, scale=1):
    """Quick Look renders square thumbnails 1:1, so pad to a square and crop."""
    side = max(w, h)
    head = f'viewBox="0 0 {w} {h}" width="{w}" height="{h}"'
    assert head in svg_text
    padded = svg_text.replace(head, f'viewBox="0 0 {side} {side}" width="{side * scale}" height="{side * scale}"', 1)
    with tempfile.TemporaryDirectory() as tmp:
        src = os.path.join(tmp, "in.svg")
        with open(src, "w") as f:
            f.write(padded)
        subprocess.run(["qlmanage", "-t", "-s", str(side * scale), "-o", tmp, src],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        im = Image.open(src + ".png").convert("RGBA")
    if im.size != (side * scale, side * scale):
        im = im.resize((side * scale, side * scale), Image.LANCZOS)
    return im.crop((0, 0, w * scale, h * scale))


# Small icons are drawn on whole pixels instead of downscaled, so edges stay crisp.
# size: (margin, square, gutter) with margin + 2 * square + gutter + margin == size
PIXEL_GRID = {16: (2, 5, 2), 24: (3, 8, 2), 32: (4, 11, 2), 48: (6, 17, 2), 64: (8, 22, 4)}


def pixel_icon(size, scale=4):
    """Hinted small icon: geometry snapped to the pixel grid, triangle anti-aliased."""
    from PIL import ImageDraw
    m, s, g = PIXEL_GRID[size]
    big = Image.new("RGBA", (size * scale, size * scale), PAPER)
    d = ImageDraw.Draw(big)
    def box(x, y, fill):
        d.rectangle([x * scale, y * scale, (x + s) * scale - 1, (y + s) * scale - 1], fill=fill)
    b = m + s + g
    box(m, m, BLUE)
    e = (m + s) * scale - 1                                   # last pixel row of the square
    d.polygon([(b * scale, m * scale), ((b + s) * scale - 1, e), (b * scale, e)], fill=BLUE)
    box(m, b, BLUE)
    box(b, b, ACCENT)
    return big.resize((size, size), Image.BOX)


def write(name, content):
    with open(os.path.join(BRAND, name), "w") as f:
        f.write(content)


def main():
    os.makedirs(f"{BRAND}/png", exist_ok=True)

    write("logo.svg", svg(512, 512, mark()))
    write("logo-on-dark.svg", svg(512, 512, mark(blue=BLUE_ON_DARK)))
    write("logo-mono.svg", svg(512, 512, mark_mono(), extra=' fill="currentColor"'))
    icon = svg(512, 512, mark(), bg=PAPER)
    write("icon.svg", icon)
    write("icon-dark.svg", svg(512, 512, mark(blue=BLUE_ON_DARK), bg=INK))

    for name, kw in (("wordmark.svg", {}), ("wordmark-on-dark.svg", {"dark": True}),
                     ("wordmark-latin.svg", {"jp": False})):
        body, w, h = lockup(**kw)
        write(name, svg(round(w + 2), h, body))

    soc = social()
    write("social-preview.svg", soc)
    render(soc, "", 1280, 640).convert("RGB").save(f"{BRAND}/social-preview.png")

    master = render(icon, "", 512, 512, scale=2)        # 1024 px master
    master.save(f"{BRAND}/png/icon-1024.png")
    for px in (512, 256, 128):
        master.resize((px, px), Image.LANCZOS).save(f"{BRAND}/png/icon-{px}.png")
    small = {px: pixel_icon(px) for px in PIXEL_GRID}
    for px, im in small.items():
        im.save(f"{BRAND}/png/icon-{px}.png")
    small[64].save(f"{BRAND}/favicon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64)],
                   append_images=[small[16], small[24], small[32], small[48]])
    print("brand assets written to", BRAND)


if __name__ == "__main__":
    main()
