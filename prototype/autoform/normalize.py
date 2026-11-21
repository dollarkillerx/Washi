# SPDX-License-Identifier: AGPL-3.0-or-later
"""Step 1: make every page upright (/Rotate 0, MediaBox at origin).

The page content is wrapped in a `cm` matrix so that what you see is what the
coordinates say. After this, all later steps work in one coordinate system
(PyMuPDF top-down points == what the user sees).
"""
import pymupdf as fitz


def _matrix(rot: int, a: float, b: float, c: float, d: float) -> str:
    # (u, v) in old PDF space -> (X, Y) in new PDF space
    return {
        0:   f"1 0 0 1 {-a:.4f} {-b:.4f}",
        90:  f"0 -1 1 0 {-b:.4f} {c:.4f}",
        180: f"-1 0 0 -1 {c:.4f} {d:.4f}",
        270: f"0 1 -1 0 {d:.4f} {-a:.4f}",
    }[rot]


def bake_rotation(doc: fitz.Document) -> list[str]:
    """Rewrite pages in place. Returns warnings."""
    warns = []
    for page in doc:
        rot = page.rotation % 360
        mb = page.mediabox  # PyMuPDF gives MediaBox as stored (x0,y0,x1,y1)
        raw = doc.xref_get_key(page.xref, "MediaBox")[1].strip("[] ").split()
        a, b, c, d = map(float, raw) if len(raw) == 4 else (mb.x0, mb.y0, mb.x1, mb.y1)
        if rot == 0 and a == 0 and b == 0:
            continue
        if page.first_annot:
            warns.append(f"page {page.number + 1}: has annotations; they are not rotated")
        w, h = c - a, d - b
        nw, nh = (h, w) if rot in (90, 270) else (w, h)
        content = page.read_contents()
        new = f"q {_matrix(rot, a, b, c, d)} cm\n".encode() + content + b"\nQ\n"
        x = doc.get_new_xref()
        doc.update_object(x, "<<>>")
        doc.update_stream(x, new)
        doc.xref_set_key(page.xref, "Contents", f"{x} 0 R")
        page.set_rotation(0)
        box = f"[0 0 {nw:.4f} {nh:.4f}]"
        doc.xref_set_key(page.xref, "MediaBox", box)
        for k in ("CropBox", "TrimBox", "BleedBox", "ArtBox"):
            if doc.xref_get_key(page.xref, k)[0] != "null":
                doc.xref_set_key(page.xref, k, box)
    return warns
