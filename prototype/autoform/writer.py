# SPDX-License-Identifier: AGPL-3.0-or-later
"""Step 4: write detected fields as a real AcroForm, plus a review overlay.

Fonts: text fields use KozMinPr6N-Regular *without embedding* (the same
trick the 国税庁 forms use) so Acrobat / Preview / Chrome / PDF.js render
typed Japanese with a local font. Checkboxes use ZapfDingbats.
"""
import pymupdf as fitz

KOZ_DICT = ("<</Type/Font/Subtype/Type0/BaseFont/KozMinPr6N-Regular/Encoding/UniJIS-UCS2-HW-H"
            "/DescendantFonts[<</Type/Font/Subtype/CIDFontType0/BaseFont/KozMinPr6N-Regular"
            "/CIDSystemInfo<</Registry(Adobe)/Ordering(Japan1)/Supplement 6>>"
            "/FontDescriptor<</Type/FontDescriptor/FontName/KozMinPr6N-Regular/Flags 6"
            "/FontBBox[-437 -340 1147 1317]/ItalicAngle 0/Ascent 880/Descent -120"
            "/CapHeight 742/StemV 80>>/DW 1000/W[231 632 500]>>]>>")


def _u16(s: str) -> str:
    return "<FEFF" + s.encode("utf-16-be").hex().upper() + ">"


def write_acroform(doc: fitz.Document, fields) -> None:
    koz = doc.get_new_xref(); doc.update_object(koz, KOZ_DICT)
    zadb = doc.get_new_xref(); doc.update_object(zadb, "<</Type/Font/Subtype/Type1/BaseFont/ZapfDingbats>>")
    helv = doc.get_new_xref(); doc.update_object(helv, "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>")

    def form_xobj(content: str, w: float, h: float, res: str) -> int:
        x = doc.get_new_xref()
        doc.update_object(x, f"<</Type/XObject/Subtype/Form/BBox[0 0 {w:.2f} {h:.2f}]/Resources {res}>>")
        doc.update_stream(x, content.encode())
        return x

    names, per_page, all_x = set(), {}, []
    for f in fields:
        page = doc[f.page]
        H = page.rect.height
        x0, y0, x1, y1 = f.rect
        w, h = x1 - x0, y1 - y0
        name = f.name if f.name not in names else f"{f.name}_p{f.page + 1}"
        names.add(name)
        rect = f"[{x0:.2f} {H - y1:.2f} {x1:.2f} {H - y0:.2f}]"
        if f.kind == "checkbox":
            fs = h * 0.9
            yes = form_xobj(f"q BT /ZaDb {fs:.2f} Tf 0 g {(w - fs * 0.85) / 2:.2f} {(h - fs * 0.75) / 2:.2f} Td (4) Tj ET Q",
                            w, h, f"<</Font<</ZaDb {zadb} 0 R>>>>")
            off = form_xobj("", w, h, "<<>>")
            obj = (f"<</Type/Annot/Subtype/Widget/FT/Btn/T{_u16(name)}/F 4/P {page.xref} 0 R/Rect{rect}"
                   f"/DA(/ZaDb 0 Tf 0 g)/MK<</CA(4)>>/V/Off/AS/Off/AP<</N<</Yes {yes} 0 R/Off {off} 0 R>>>>>>")
        else:
            fs = round(max(5.0, min(10.0, h * 0.6)), 1)
            ap = form_xobj("/Tx BMC EMC", w, h, "<<>>")
            comb = f"/Ff {(1 << 24) | (1 << 23)}/MaxLen {f.maxlen}" if f.kind == "comb" and f.maxlen else ""
            multi = "/Ff 4096" if f.kind == "text" and h > 24 else ""
            tu = f"/TU{_u16(f.label)}" if f.label else ""
            obj = (f"<</Type/Annot/Subtype/Widget/FT/Tx/T{_u16(name)}{tu}/F 4/P {page.xref} 0 R/Rect{rect}"
                   f"/DA(/KozMinPr6N-Regular {fs} Tf 0 g)/Q {f.align}{comb or multi}/MK<<>>/V<FEFF>"
                   f"/AP<</N {ap} 0 R>>>>")
        x = doc.get_new_xref()
        doc.update_object(x, obj)
        per_page.setdefault(page.xref, []).append(x)
        all_x.append(x)

    for pxref, xs in per_page.items():
        doc.xref_set_key(pxref, "Annots", "[" + " ".join(f"{x} 0 R" for x in xs) + "]")
    doc.xref_set_key(doc.pdf_catalog(), "AcroForm",
                     "<</Fields[" + " ".join(f"{x} 0 R" for x in all_x) + "]"
                     f"/DR<</Font<</KozMinPr6N-Regular {koz} 0 R/ZaDb {zadb} 0 R/Helv {helv} 0 R>>>>"
                     "/DA(/Helv 0 Tf 0 g)>>")


COLORS = {"text": (0.1, 0.35, 0.95), "amount": (0.55, 0.2, 0.85), "comb": (0.95, 0.5, 0.05),
          "checkbox": (0.05, 0.65, 0.25)}


def draw_overlay(doc: fitz.Document, fields, cells_by_page, truth=None) -> None:
    """Colour-coded review layer drawn on top of the page content."""
    for pno, cells in cells_by_page.items():
        page = doc[pno]
        sh = page.new_shape()
        for c in cells:
            if c.kind in ("label",):
                sh.draw_rect(c.rect); sh.finish(color=(0.6, 0.6, 0.6), width=0.3, dashes="[1 1] 0")
            elif c.kind == "shaded":
                sh.draw_rect(c.rect); sh.finish(color=None, fill=(0.5, 0.5, 0.5), fill_opacity=0.25)
        for gt in (truth or {}).get(pno, []):
            sh.draw_rect(gt); sh.finish(color=(1, 0, 0), width=0.6)
        sh.commit()
        for f in (x for x in fields if x.page == pno):
            col = COLORS[f.kind]
            r = fitz.Rect(f.rect)
            sh = page.new_shape()
            sh.draw_rect(r); sh.finish(color=col, fill=col, fill_opacity=0.18, width=0.5)
            if f.kind == "comb":
                cw = r.width / f.maxlen
                for i in range(1, f.maxlen):
                    sh.draw_line((r.x0 + i * cw, r.y1 - 3), (r.x0 + i * cw, r.y1)); sh.finish(color=col, width=0.4)
            if f.prefilled:
                sh.draw_rect(r + (-0.8, -0.8, 0.8, 0.8)); sh.finish(color=(0.9, 0.1, 0.1), width=0.5, dashes="[2 1] 0")
            sh.commit()
            tag = f.name[:14] if f.kind != "checkbox" else ""
            if tag and r.height >= 7:
                page.insert_text((r.x0 + 1, r.y0 + 4.2), tag, fontsize=3.6, fontname="japan", color=col)
