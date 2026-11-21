# Masume prototype (Python reference implementation)

Turn a **flat PDF form** (no fields) into a **fillable AcroForm** automatically.
This prototype validates the Masume detection algorithm. The C# port in
`src/Washi.Masume` must reach the same scores on the same forms, and its
`fields.json` output uses the same shape (see `tests/Washi.Masume.Tests`).

`samples/` and `out/` are git-ignored: real forms often carry personal data.
For the eval, download the 国税庁 年末調整 "入力用" PDFs from nta.go.jp into `samples/`.

```bash
cd prototype
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt

# flat PDF -> fillable PDF + review overlay + fields.json
.venv/bin/python -m autoform detect samples/your_form.pdf -o out

# PDF that already has fields: strip them, re-detect, score against the real ones
.venv/bin/python -m autoform eval samples/nta_2027bun_01.pdf -o out
```

Outputs, using `out/` as the output folder:

| file | what |
|---|---|
| `*_fillable.pdf` | upright pages with real form fields: text, right-aligned amount, comb (`MaxLen`), checkbox |
| `*_overlay.pdf` / `*_overlay_pN.png` | review layer. Blue = text, purple = amount, orange = comb, green = checkbox, red dashed = cell already has printed data, grey = shaded (not for input). Eval overlays also draw the real fields as red outlines |
| `*_fields.json` | one record per field: `kind`, `rect`, `maxlen`, `align`, `prefilled`, `label`. This is the hand-off format for an editor UI where a human confirms the fields |

## Results

The three 国税庁 年末調整 forms already contain fields made by hand, so they
work as ground truth. `eval` deletes those fields and runs the detector on
the flat page.

| form | real fields | detected | recall | precision |
|---|---|---|---|---|
| 扶養控除等(異動)申告書 R9 | 193 | 130 | 78% | 100% |
| 保険料控除申告書 R8 | 143 | 130 | 84% | 90% |
| 基礎・配偶者・特定親族・所得金額調整 R8 | 83 | 77 | 83% | 90% |

- **Recall**: the share of real fields whose centre is covered by a detected field.
- **Precision**: the share of detected fields that sit on a real field.

Recall is a little optimistic. One detected birth-date cell covers four real
fields (元号 + 年/月/日), and that counts as four hits.

On a company-made DocuWorks PDF of the same three forms (rotated pages, no
ToUnicode, text partly drawn as graphics), it finds 331 fields, 22 of them
flagged as already holding printed data.

## Algorithm

All steps are plain geometry and array code. Nothing depends on PyMuPDF-only magic.

1. **Make pages upright** (`normalize.py`). Wrap the content in a `cm`
   matrix, then set `/Rotate 0` and swap the MediaBox. From here on,
   coordinates match what the user sees.
2. **Render the graphics only** (`detect.graphics_raster`). Redact all text
   from a copy of the page, then render it at 4 px/pt. Rendering, instead of
   reading vector paths, also catches lines stored as tiny images. This
   DocuWorks file draws its lines that way.
3. **Find cells**. Take the dark pixels, dilate them by 1 px, and label the
   connected components of the background. A component counts as a cell when
   its bounding box is at least 85% filled after `fill_holes`. That rule
   drops struck-out triangle cells, but keeps cells with glyphs drawn as
   graphics inside them.
4. **Classify the text**. Get characters with their boxes from the text
   layer. The dominant colour is the form's label colour. A clearly different
   colour means prefilled data. This works even when the text layer decodes
   to garbage (no ToUnicode), because only boxes and colours are used.
5. **Classify each cell**:
   - **Shaded** (median interior brightness < 238) → not for input.
   - **Ink drawn in the label colour** (row numbers, headings) → label.
   - **No labels inside**:
     - square, 12 pt or smaller → checkbox
     - evenly spaced short ticks inside → comb, `MaxLen = ticks + 1`
     - otherwise → text
   - **Runs of 4 or more equal boxes in a row** (法人番号 style) → one comb field.
   - **Labels inside the cell**. Each rule finds the free writing area:
     - **A**: ≤ 3 characters at the right end (円, 人) → amount field, right-aligned, stops left of the mark.
     - **B**: a note along the top → field below it.
     - **C**: a note along the bottom → field above it.
     - **D**: labels hug the right end with a blank to their left (＿年＿月＿日, ＿税務署長) → whole cell.
     - **E**: ≤ 6 scattered marks, e.g. `(a) …… 円` → largest empty rectangle, if it is ≥ 50% of the cell.
     - Otherwise → label cell.
   - **□ glyphs** → checkbox. If the text has no Unicode mapping, the
     glyph's pixels are checked for a hollow square.
6. **Name the fields**. When the text is readable, use the nearest label
   cell to the left or above. For checkboxes, use the words right after the
   box. Otherwise use `p{page}_{kind}_{n}`.
7. **Write the AcroForm** (`writer.py`). Widgets are written as raw PDF
   objects. Text fields use non-embedded `KozMinPr6N-Regular` with
   `UniJIS-UCS2-HW-H`, the same approach as the 国税庁 forms, so viewers draw
   typed Japanese with a local font. Checkboxes use ZapfDingbats.

## Known gaps

- **Scanned PDFs.** Lines come out ragged and there is no text layer. You
  need deskewing, morphological line extraction (OpenCV), and OCR for labels.
- **Field names on garbled-text PDFs** fall back to positions. OCR on the
  label cells (macOS Vision, Windows.Media.Ocr, Tesseract `jpn`) would fix it.
- **Splitting a cell into sub-fields** (元号 / 年 / 月 / 日, 郵便番号 + 住所)
  is not done. One cell becomes one field.
- **口 (kanji) vs □.** Both are hollow squares. When the text is unreadable,
  a 口 can become a false checkbox.
- **Prefilled data is only flagged.** It is not removed. Removing it needs
  char-level content-stream rewriting, which is the hard part described below.

## Porting to Avalonia / .NET

| step | Python here | .NET |
|---|---|---|
| upright pages | PyMuPDF xref edits | PDFsharp: edit `/Rotate`, `/MediaBox`, prepend a content stream |
| graphics-only render | redact text + render | PDFium (`PDFiumCore`): remove the text page objects (`FPDFPageObj_GetType == TEXT`) from a copy, then `FPDF_RenderPageBitmap` |
| full render, colours | PyMuPDF pixmap | PDFium bitmap; **SkiaSharp** for pixel access (Avalonia already ships Skia) |
| connected components, fill holes, dilation | scipy.ndimage | **OpenCvSharp4**: `ConnectedComponentsWithStats`, `FloodFill`, `Dilate`; or a hand-written BFS (about 60 lines) |
| chars + boxes + colours | PyMuPDF `rawdict` | PdfPig `page.Letters` (has `GlyphRectangle` and `Color`), or PDFium `FPDFText_GetCharBox` |
| write AcroForm | raw xref objects | PDFsharp low-level `PdfDictionary` / `PdfArray`. It has no "create field" API, so do it the same way as here |
| review UI | overlay PNG | Avalonia `Canvas` over the page bitmap, with each field a draggable/resizable `Border`, editing `fields.json` |

Licences: PDFium is BSD; PdfPig, PDFsharp and OpenCvSharp are MIT/Apache.
This prototype uses PyMuPDF (AGPL-3.0), which is compatible with Washi's
AGPL-3.0-or-later licence.
