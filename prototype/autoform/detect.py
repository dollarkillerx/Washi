# SPDX-License-Identifier: AGPL-3.0-or-later
"""Steps 2-3: find table cells and decide which ones are input fields.

Pipeline per page (all coordinates are PyMuPDF top-down points):

  graphics raster   render the page with all text removed (lines may be
                    vector paths OR tiny images - rasterising covers both)
  ink mask          dark pixels, dilated 1px to close hairline gaps
  cells             connected components of the *background*; a component
                    that is (almost) a full rectangle is a table cell
  text              chars from the text layer, split into "label" colour
                    (the dominant form colour) and "data" colour (prefilled)
  classify          label / text / amount / comb / checkbox / shaded / skip

Everything here is plain array + geometry code so it can be ported 1:1 to
.NET (PDFium render + a flood-fill / OpenCvSharp connected components).
"""
from __future__ import annotations
from dataclasses import dataclass, field, asdict
import unicodedata
import numpy as np
import pymupdf as fitz
from scipy import ndimage

SCALE = 4.0            # raster pixels per PDF point
INK_LUM = 210          # pixel darker than this = ink (graphics raster)
SHADED_LUM = 238       # cell interior mean darker than this = shaded, not input


@dataclass
class Char:
    c: str
    rect: fitz.Rect
    color: int


@dataclass
class Field:
    page: int
    kind: str                      # text | amount | comb | checkbox
    rect: tuple                    # x0, y0, x1, y1 (points, top-down)
    name: str = ""
    maxlen: int = 0
    align: int = 0                 # 0 left, 1 center, 2 right
    prefilled: str = ""            # data-coloured text already printed inside
    label: str = ""
    notes: list = field(default_factory=list)

    def to_json(self):
        d = asdict(self)
        d["rect"] = [round(v, 2) for v in self.rect]
        return d


@dataclass
class Cell:
    rect: fitz.Rect
    label_chars: list
    data_chars: list
    shaded: bool
    kind: str = "?"
    ticks: int = 0
    text: str = ""


# ------------------------------------------------------------------ rasters
def _rgb_arr(pix: fitz.Pixmap) -> np.ndarray:
    return np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width, pix.n)[..., :3]


def _gray(pix: fitz.Pixmap) -> np.ndarray:
    return (_rgb_arr(pix) @ np.array([0.299, 0.587, 0.114])).astype(np.uint8)


def graphics_raster(doc: fitz.Document, pno: int) -> tuple[np.ndarray, np.ndarray]:
    """Render page pno with every text object removed (lines/images kept). Returns (gray, rgb)."""
    tmp = fitz.open()
    tmp.insert_pdf(doc, from_page=pno, to_page=pno, annots=False)
    p = tmp[0]
    p.add_redact_annot(p.rect, fill=False)
    p.apply_redactions(images=fitz.PDF_REDACT_IMAGE_NONE,
                       graphics=fitz.PDF_REDACT_LINE_ART_NONE,
                       text=fitz.PDF_REDACT_TEXT_REMOVE)
    pix = p.get_pixmap(matrix=fitz.Matrix(SCALE, SCALE), annots=False, alpha=False)
    return _gray(pix), _rgb_arr(pix).copy()


def full_raster(page: fitz.Page) -> np.ndarray:
    return _gray(page.get_pixmap(matrix=fitz.Matrix(SCALE, SCALE), annots=False, alpha=False))


# ------------------------------------------------------------------ text
_OK_BLOCKS = [(0x20, 0x7E), (0x3000, 0x30FF), (0x4E00, 0x9FFF), (0xFF00, 0xFFEF),
              (0x2010, 0x2BFF), (0x3400, 0x4DBF), (0xF900, 0xFAFF), (0xA0, 0x24F)]


def readable(text: str) -> bool:
    cs = [c for c in text if not c.isspace()]
    if not cs:
        return False
    ok = sum(any(a <= ord(c) <= b for a, b in _OK_BLOCKS) for c in cs)
    return ok / len(cs) > 0.9


def page_chars(page: fitz.Page) -> list[Char]:
    out = []
    for b in page.get_text("rawdict")["blocks"]:
        for l in b.get("lines", []):
            for s in l["spans"]:
                for ch in s["chars"]:
                    if ch["c"].isspace():
                        continue
                    r = fitz.Rect(ch["bbox"])
                    if r.is_empty:
                        continue
                    out.append(Char(ch["c"], r, s["color"]))
    return out


def _rgb(c: int):
    return ((c >> 16) & 255) / 255, ((c >> 8) & 255) / 255, (c & 255) / 255


def split_label_data(chars: list[Char]):
    """Dominant colour = form labels. Clearly different dark colour = prefilled data."""
    if not chars:
        return [], [], (0, 0, 0)
    vals, counts = np.unique([c.color for c in chars], return_counts=True)
    label_col = _rgb(int(vals[counts.argmax()]))
    labels, data = [], []
    for c in chars:
        dist = sum((x - y) ** 2 for x, y in zip(_rgb(c.color), label_col)) ** 0.5
        (data if dist > 0.35 else labels).append(c)
    return labels, data, label_col


def _cdist(a, b):
    return sum((x - y) ** 2 for x, y in zip(a, b)) ** 0.5


# ------------------------------------------------------------------ cells
def find_cells(g: np.ndarray, rgb: np.ndarray) -> list[tuple[fitz.Rect, float, tuple]]:
    """Returns (cell rect, fraction covered by non-text ink inside it, mean colour of that ink)."""
    ink = ndimage.binary_dilation(g < INK_LUM, iterations=1)
    lab, n = ndimage.label(~ink)          # 4-connected background regions
    H, W = ink.shape
    cells = []
    for i, sl in enumerate(ndimage.find_objects(lab), start=1):
        if sl is None:
            continue
        ys, xs = sl
        if ys.start == 0 or xs.start == 0 or ys.stop == H or xs.stop == W:
            continue                       # outside of the form
        h, w = ys.stop - ys.start, xs.stop - xs.start
        if w < 3 * SCALE or h < 3 * SCALE:
            continue
        if w * h > 0.25 * H * W:
            continue
        comp = lab[sl] == i
        # glyphs drawn as graphics (not text) leave holes inside the cell: fill them
        solid = ndimage.binary_fill_holes(comp)
        if solid.sum() / (w * h) < 0.85:   # triangles (struck-out cells), rounded blobs
            continue
        holes = solid & ~comp
        inner_ink = holes.sum() / solid.sum()
        px = rgb[sl][holes & (g[sl] < 150)]  # core pixels only: anti-aliased edges wash the colour out
        px = px if len(px) else rgb[sl][holes & (g[sl] < INK_LUM)]
        ink_col = tuple(px.mean(axis=0) / 255) if len(px) else None
        # undo the 1px dilation
        cells.append((fitz.Rect((xs.start - 1) / SCALE, (ys.start - 1) / SCALE,
                                (xs.stop + 1) / SCALE, (ys.stop + 1) / SCALE), inner_ink, ink_col))
    return cells


def count_ticks(g: np.ndarray, r: fitz.Rect) -> int:
    """Short vertical marks inside a cell that do not reach both walls (comb dividers)."""
    x0, y0, x1, y1 = (int(v * SCALE) for v in (r.x0, r.y0, r.x1, r.y1))
    sub = g[y0 + 2:y1 - 2, x0 + 3:x1 - 3] < INK_LUM
    if sub.size == 0:
        return 0
    colfrac = sub.mean(axis=0)
    on = (colfrac > 0.12) & (colfrac < 0.97)
    xs, cur = [], []
    for i, v in enumerate(on):
        if v:
            cur.append(i)
        elif cur:
            xs.append(np.mean(cur)); cur = []
    if cur:
        xs.append(np.mean(cur))
    if len(xs) < 3:
        return 0
    gaps = np.diff([0] + xs + [sub.shape[1]])
    if gaps.std() / gaps.mean() > 0.2:
        return 0
    return len(xs)


def hollow_square(full: np.ndarray, r: fitz.Rect) -> fitz.Rect | None:
    """Is this glyph a □ ? (works even when the font has no Unicode mapping)."""
    if not (3 <= r.width <= 14 and 3 <= r.height <= 14):
        return None
    x0, y0, x1, y1 = (int(v * SCALE) for v in (r.x0, r.y0, r.x1, r.y1))
    ink = full[y0:y1, x0:x1] < 160
    if ink.sum() < 8:
        return None
    ys, xs = np.nonzero(ink)
    a, b, c, d = ys.min(), ys.max(), xs.min(), xs.max()
    h, w = b - a + 1, d - c + 1
    if h < 0.5 * ink.shape[0] or not (0.75 < w / h < 1.33):
        return None
    box = ink[a:b + 1, c:d + 1]
    edge = min(box[0].mean(), box[-1].mean(), box[:, 0].mean(), box[:, -1].mean())
    inner = box[h // 4: h - h // 4, w // 4: w - w // 4]
    if edge > 0.7 and (inner.size == 0 or inner.mean() < 0.08):
        return fitz.Rect((x0 + c) / SCALE, (y0 + a) / SCALE, (x0 + d + 1) / SCALE, (y0 + b + 1) / SCALE)
    return None


def largest_empty(r: fitz.Rect, obstacles: list[fitz.Rect]) -> fitz.Rect | None:
    """Largest axis-aligned rectangle inside r that touches none of the obstacles (brute force, n is tiny)."""
    obs = [o + (-0.5, -0.5, 0.5, 0.5) for o in obstacles]
    xs = sorted({r.x0, r.x1, *(min(max(v, r.x0), r.x1) for o in obs for v in (o.x0, o.x1))})
    ys = sorted({r.y0, r.y1, *(min(max(v, r.y0), r.y1) for o in obs for v in (o.y0, o.y1))})
    best, area = None, 0.0
    for i, x0 in enumerate(xs):
        for x1 in xs[i + 1:]:
            for j, y0 in enumerate(ys):
                for y1 in ys[j + 1:]:
                    a = (x1 - x0) * (y1 - y0)
                    if a <= area:
                        continue
                    cand = fitz.Rect(x0, y0, x1, y1)
                    if any(cand.intersects(o) and not (cand & o).is_empty for o in obs):
                        continue
                    best, area = cand, a
    return best


def _inside(c: Char, r: fitz.Rect) -> bool:
    m = fitz.Point((c.rect.x0 + c.rect.x1) / 2, (c.rect.y0 + c.rect.y1) / 2)
    return r.contains(m)


def _text(chars: list[Char]) -> str:
    chars = sorted(chars, key=lambda c: (round(c.rect.y0 / 4), c.rect.x0))
    return unicodedata.normalize("NFKC", "".join(c.c for c in chars))


# ------------------------------------------------------------------ main
def detect_page(doc: fitz.Document, pno: int) -> tuple[list[Field], list[Cell]]:
    page = doc[pno]
    g, rgb = graphics_raster(doc, pno)
    full = full_raster(page)
    chars = page_chars(page)
    labels, data, label_col = split_label_data(chars)
    is_readable = readable("".join(c.c for c in chars))

    # □ glyphs -> checkboxes, removed from the label set
    boxes, rest = [], []
    for c in labels:
        sq = None
        if c.c in "□☐" or not is_readable:
            sq = hollow_square(full, c.rect)
        (boxes.append(sq) if sq else rest.append(c))
    labels = rest

    cells = []
    for r, inner_ink, ink_col in find_cells(g, rgb):
        x0, y0, x1, y1 = (int(v * SCALE) for v in (r.x0, r.y0, r.x1, r.y1))
        interior = g[y0 + 3:y1 - 3, x0 + 3:x1 - 3]
        shaded = interior.size > 0 and float(np.median(interior)) < SHADED_LUM
        cell = Cell(r, [c for c in labels if _inside(c, r)], [c for c in data if _inside(c, r)], shaded)
        cell.boxes = [b for b in boxes if r.contains((b.tl + b.br) / 2)]
        cell.graphic_fill = inner_ink > 0.02
        # drawn glyphs in the form's own colour are labels (row numbers, headings)
        cell.graphic_label = cell.graphic_fill and ink_col is not None and _cdist(ink_col, label_col) < 0.3
        cells.append(cell)

    fields: list[Field] = []

    # runs of equal boxes in a row (法人番号 style digit boxes) -> one comb
    used = set()
    rows: dict = {}
    for i, c in enumerate(cells):
        if not c.label_chars and c.rect.width < 20 and c.rect.height >= 6:
            rows.setdefault((round(c.rect.y0), round(c.rect.y1)), []).append(i)
    for idx in rows.values():
        idx.sort(key=lambda i: cells[i].rect.x0)
        run = [idx[0]]
        for i in idx[1:] + [None]:
            if i is not None:
                a, b = cells[run[-1]].rect, cells[i].rect
                if b.x0 - a.x1 < 2.5 and abs(b.width - a.width) < 0.2 * a.width:
                    run.append(i); continue
            if len(run) >= 4:
                r = fitz.Rect(cells[run[0]].rect.tl, cells[run[-1]].rect.br)
                pre = _text([ch for j in run for ch in cells[j].data_chars])
                fields.append(Field(pno, "comb", tuple(r), maxlen=len(run), align=1, prefilled=pre))
                used.update(run)
                for j in run:
                    cells[j].kind = "comb-part"
            run = [i] if i is not None else []

    for i, c in enumerate(cells):
        if i in used:
            continue
        r = fitz.Rect(c.rect)
        pre = _text(c.data_chars) or ("(drawn, not text)" if c.graphic_fill and not count_ticks(g, r) else "")
        if c.shaded:
            c.kind = "shaded"; continue
        if c.graphic_label and not c.data_chars and not count_ticks(g, r):
            c.kind = "label"; continue
        if c.label_chars:
            lab_box = fitz.Rect()
            for ch in c.label_chars:
                lab_box |= ch.rect
            w, h = r.width, r.height
            small = lab_box.get_area() < 0.2 * r.get_area() and len(c.label_chars) <= 3
            sub = None
            if small and lab_box.x0 > r.x0 + 0.6 * w and h >= 7:
                # A: unit mark such as 円 in the right/bottom corner -> amount field
                c.kind = "amount"
                fields.append(Field(pno, "amount", (r.x0, r.y0, lab_box.x0 - 0.5, r.y1),
                                    align=2, prefilled=pre, label=_text(c.label_chars)))
                continue
            if lab_box.y1 <= r.y0 + 0.45 * h and r.y1 - lab_box.y1 >= 8:
                # B: small note printed along the top, writing space below it
                sub = fitz.Rect(r.x0, lab_box.y1 + 0.5, r.x1, r.y1)
            elif lab_box.y0 >= r.y0 + 0.55 * h and lab_box.y0 - r.y0 >= 8:
                # C: note along the bottom, e.g. （左のA～Cを記載）
                sub = fitz.Rect(r.x0, r.y0, r.x1, lab_box.y0 - 0.5)
            elif (not c.boxes and lab_box.x0 - r.x0 >= 0.25 * w and r.x1 - lab_box.x1 <= 0.2 * w and h >= 7):
                # D: fill-in-the-blank, labels hug the right end: ＿年＿月＿日 / ＿税務署長
                sub = fitz.Rect(r)
            elif not c.boxes and len(c.label_chars) <= 6 and h >= 7:
                # E: a few marks scattered in the cell, e.g. "(a) ........ 円"
                e = largest_empty(r, [ch.rect for ch in c.label_chars])
                if e and e.get_area() >= 0.5 * r.get_area() and e.height >= 7 and e.width >= 12:
                    sub = e
            if sub is None:
                c.kind = "label"
                c.text = _text(c.label_chars)
                continue
            n = count_ticks(g, sub)
            kind = "comb" if n else "text"
            c.kind = kind
            fields.append(Field(pno, kind, tuple(sub), maxlen=n + 1 if n else 0, align=1 if n else 0,
                                prefilled=pre, label=_text(c.label_chars)[:24], notes=["label inside cell"]))
            continue
        if r.width < 6 or r.height < 5:
            c.kind = "skip"; continue
        if r.width <= 12 and r.height <= 12 and 0.75 < r.width / r.height < 1.33:
            c.kind = "checkbox"
            fields.append(Field(pno, "checkbox", tuple(r)))
            continue
        n = count_ticks(g, r)
        if n:
            c.kind, c.ticks = "comb", n
            fields.append(Field(pno, "comb", tuple(r), maxlen=n + 1, align=1, prefilled=pre))
            continue
        c.kind = "text"
        fields.append(Field(pno, "text", tuple(r), prefilled=pre))

    for b in boxes:
        fields.append(Field(pno, "checkbox", tuple(b), notes=["□ glyph"]))

    name_fields(fields, cells, labels, is_readable, pno)
    return fields, cells


def name_fields(fields, cells, labels, is_readable, pno):
    lab_cells = [c for c in cells if c.kind == "label" and c.text]
    seen = {}
    for k, f in enumerate(sorted(fields, key=lambda f: (round(f.rect[1] / 6), f.rect[0]))):
        r = fitz.Rect(f.rect)
        name = ""
        if is_readable:
            if f.kind == "checkbox":
                # the words printed right after the box
                right = [c for c in labels if abs((c.rect.y0 + c.rect.y1) / 2 - (r.y0 + r.y1) / 2) < r.height
                         and r.x1 - 1 < c.rect.x0 < r.x1 + 80]
                name = _text(sorted(right, key=lambda c: c.rect.x0)[:12])
            else:
                best = None
                for c in lab_cells:
                    cr = c.rect
                    vo = min(cr.y1, r.y1) - max(cr.y0, r.y0)
                    ho = min(cr.x1, r.x1) - max(cr.x0, r.x0)
                    if cr.x1 <= r.x0 + 2 and vo > 0.5 * min(cr.height, r.height):
                        d = r.x0 - cr.x1
                    elif cr.y1 <= r.y0 + 2 and ho > 0.5 * min(cr.width, r.width):
                        d = (r.y0 - cr.y1) * 1.5
                    else:
                        continue
                    if best is None or d < best[0]:
                        best = (d, c.text)
                name = best[1] if best else ""
            name = "".join(name.split())[:24]
        f.label = f.label or name
        base = name or f"p{pno + 1}_{f.kind}_{k + 1:03d}"
        seen[base] = seen.get(base, 0) + 1
        f.name = base if seen[base] == 1 else f"{base}_{seen[base]}"
