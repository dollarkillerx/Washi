# SPDX-License-Identifier: AGPL-3.0-or-later
"""
python -m autoform detect IN.pdf [-o out/]   flat PDF -> fillable PDF + overlay + fields.json
python -m autoform eval   IN.pdf [-o out/]   PDF that already has fields: strip them, re-detect,
                                             and score the detection against the real fields
"""
import argparse, json, pathlib, sys
import pymupdf as fitz
from .normalize import bake_rotation
from .detect import detect_page
from .writer import write_acroform, draw_overlay


def run_detect(doc):
    fields, cells = [], {}
    for pno in range(doc.page_count):
        f, c = detect_page(doc, pno)
        fields += f
        cells[pno] = c
    return fields, cells


def summary(fields):
    k = {}
    for f in fields:
        k[f.kind] = k.get(f.kind, 0) + 1
    return ", ".join(f"{v} {n}" for n, v in sorted(k.items()))


def save_overlay(src, fields, cells, path, truth=None):
    ov = fitz.open()
    ov.insert_pdf(src, annots=False)
    draw_overlay(ov, fields, cells, truth)
    ov.save(path, garbage=3, deflate=True)
    for i, p in enumerate(ov):
        p.get_pixmap(dpi=144).save(str(path).replace(".pdf", f"_p{i + 1}.png"))


def cmd_detect(a):
    doc = fitz.open(a.pdf); doc.authenticate("")
    if any(True for p in doc for _ in p.widgets()):
        print("note: input already has form fields; they are kept and new ones are added")
    for w in bake_rotation(doc):
        print("warn:", w)
    fields, cells = run_detect(doc)
    out = pathlib.Path(a.out); out.mkdir(parents=True, exist_ok=True)
    stem = pathlib.Path(a.pdf).stem
    save_overlay(doc, fields, cells, out / f"{stem}_overlay.pdf")
    (out / f"{stem}_fields.json").write_text(json.dumps([f.to_json() for f in fields], ensure_ascii=False, indent=1))
    write_acroform(doc, fields)
    doc.save(out / f"{stem}_fillable.pdf", garbage=3, deflate=True)
    print(f"{stem}: {len(fields)} fields ({summary(fields)}) -> {out}/")
    pre = [f for f in fields if f.prefilled]
    if pre:
        print(f"  {len(pre)} fields already contain printed data (red dashed in overlay)")


def iou(a, b):
    i = fitz.Rect(a) & fitz.Rect(b)
    if i.is_empty:
        return 0.0
    ia = i.get_area()
    return ia / (fitz.Rect(a).get_area() + fitz.Rect(b).get_area() - ia)


def cmd_eval(a):
    doc = fitz.open(a.pdf); doc.authenticate("")
    truth, kinds = {}, {}
    for p in doc:
        for w in p.widgets():
            if w.rect.width < 1:
                continue
            truth.setdefault(p.number, []).append(fitz.Rect(w.rect))
            kinds[(p.number, len(truth[p.number]) - 1)] = w.field_type_string
    # strip every widget so the detector only sees the flat form
    clean = fitz.open()
    clean.insert_pdf(doc, annots=False)
    for p in clean:
        for w in list(p.widgets()):
            p.delete_widget(w)
    fields, cells = run_detect(clean)

    stem = pathlib.Path(a.pdf).stem
    out = pathlib.Path(a.out); out.mkdir(parents=True, exist_ok=True)
    save_overlay(clean, fields, cells, out / f"{stem}_eval.pdf", truth)

    tot_gt = tot_hit = tot_det = tot_good = 0
    for pno, gts in truth.items():
        dets = [f for f in fields if f.page == pno]
        # a real field is "found" when some detected field covers its centre
        hit = sum(any(fitz.Rect(f.rect).contains((g.tl + g.br) / 2) for f in dets) for g in gts)
        # a detected field is "good" when it covers the centre of at least one real field
        good = sum(any(fitz.Rect(f.rect).contains((g.tl + g.br) / 2) for g in gts) for f in dets)
        tot_gt += len(gts); tot_hit += hit; tot_det += len(dets); tot_good += good
    print(f"{stem}: real fields {tot_gt}, detected {tot_det} ({summary(fields)})")
    print(f"  recall    {tot_hit}/{tot_gt} = {tot_hit / max(tot_gt, 1):.0%}   (real fields covered by a detection)")
    print(f"  precision {tot_good}/{tot_det} = {tot_good / max(tot_det, 1):.0%}   (detections that sit on a real field)")


def main():
    ap = argparse.ArgumentParser(prog="autoform")
    sub = ap.add_subparsers(dest="cmd", required=True)
    for n in ("detect", "eval"):
        s = sub.add_parser(n); s.add_argument("pdf"); s.add_argument("-o", "--out", default="out")
    a = ap.parse_args()
    {"detect": cmd_detect, "eval": cmd_eval}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
