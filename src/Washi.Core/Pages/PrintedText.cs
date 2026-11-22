// SPDX-License-Identifier: AGPL-3.0-or-later
using Washi.Core.Ocr;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>A form cell whose printed content should become the field's editable value.</summary>
/// <param name="Key">The caller's id (the field name).</param>
/// <param name="Comb">One character per box: the value keeps no spaces.</param>
public sealed record PrintedCell(string Key, int Page, PdfRect Rect, bool Comb = false);

/// <param name="Text">What was printed in the cell (empty when nothing could be read). Only cells that held
/// printed content are returned.</param>
/// <param name="FromOcr">Read by OCR (check it) rather than taken from the text layer.</param>
public sealed record PrintedValue(string Key, string Text, bool FromOcr);

public sealed record PrintedResult(IReadOnlyList<PrintedValue> Values, EraseReport Erase, string Output);

/// <summary>
/// Turns data printed on a form into editable values: the printed content under each cell is erased for real
/// (<see cref="PdfEraser"/>, so it cannot show through or be extracted later), and its text is read so the caller can
/// put it into the field. Readable text is taken from the PDF itself; otherwise only the ink that was erased (the
/// original render minus the erased one) is read by OCR, so the form's own labels inside a cell never end up in the
/// value. All cells go into one OCR image, one call per document.
/// </summary>
public static class PrintedText
{
    private const int Gap = 48;            // white pixels between cells in the OCR sheet
    private const int Pad = 6;

    /// <exception cref="EraseException">Nothing could be erased (see the message).</exception>
    public static async Task<PrintedResult> MoveIntoFieldsAsync(string input, string output, IReadOnlyList<PrintedCell> cells,
        Func<IOcrEngine> ocr, IReadOnlyList<string> languages, EraseOptions? options = null, CancellationToken ct = default)
    {
        if (cells.Count == 0) throw new EraseException("no field holds printed content", "Err_EraseNothing");
        var boxes = cells.Select(c => new EraseRegion(c.Page, c.Rect)).ToList();

        // 1. erase (with text runs that start in a cell but run a little past its edge)
        EraseReport report;
        IReadOnlyList<EraseTarget> targets;
        var readablePages = new HashSet<int>();
        await PdfiumDocument.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var doc = PdfiumDocument.Open(input);
                foreach (var page in cells.Select(c => c.Page).Distinct())
                    if (page >= 0 && page < doc.PageCount && TextQuality.Readable(doc.GetPageText(page))) readablePages.Add(page);
            }, ct).ConfigureAwait(false);
            (targets, var kept) = await Task.Run(() => PdfEraser.Plan(input, boxes, options), ct).ConfigureAwait(false);
            foreach (var k in kept.Where(k => k.Kind == EraseKind.Text && k.Why is EraseKeptWhy.RunsOutside or EraseKeptWhy.Chained))
            {
                var (x, y) = k.Rect.Center;
                if (cells.Any(c => c.Page == k.Page && c.Rect.Contains(x, y)))
                    boxes.Add(new EraseRegion(k.Page, new PdfRect(k.Rect.X0 - 1, k.Rect.Y0 - 1, k.Rect.X1 + 1, k.Rect.Y1 + 1)));
            }
            (targets, _) = await Task.Run(() => PdfEraser.Plan(input, boxes, options), ct).ConfigureAwait(false);
            report = await Task.Run(() => PdfEraser.Erase(input, output, boxes, options), ct).ConfigureAwait(false);
        }
        finally { PdfiumDocument.Gate.Release(); }

        // 2. readable text layer: the erased text objects themselves
        var values = new Dictionary<string, PrintedValue>();
        var needOcr = new List<PrintedCell>();
        foreach (var cell in cells)
        {
            // everything erased that reaches into the cell: one printed run may span several cells (＿年＿月＿日)
            var inCell = targets.Where(t => t.Page == cell.Page && t.Rect.Intersect(cell.Rect) is { Width: >= 2, Height: >= 2 }).ToList();
            var texts = inCell.Where(t => t.Kind == EraseKind.Text).ToList();
            // the PDF's own text can be used only when each run belongs to this cell alone; a shared run is read by OCR
            bool onlyText = inCell.Count > 0 && texts.Count == inCell.Count
                            && texts.All(t => t.Rect.Intersect(cell.Rect).Area >= 0.8 * t.Rect.Area);
            var joined = string.Join(" ", texts.OrderBy(t => Math.Round(t.Rect.Y0 / 4)).ThenBy(t => t.Rect.X0).Select(t => (t.Text ?? "").Trim()));
            // a garbled text layer can still look like CJK text: trust it only on pages whose text reads well
            if (inCell.Count == 0) continue;
            if (onlyText && readablePages.Contains(cell.Page) && TextQuality.Readable(joined))
                values[cell.Key] = new(cell.Key, Clean(joined, cell.Comb), false);
            else needOcr.Add(cell);
        }

        // 3. the rest by OCR, on the erased ink only. If reading fails, the erased copy is not kept: print that was
        //    erased but not captured would be lost to the user.
        try
        {
        if (needOcr.Count > 0)
        {
            var engine = ocr();
            double scale = Math.Min(engine.Dpi, 300) / 72.0;
            var crops = new List<(PrintedCell Cell, RgbImage Image)>();
            await PdfiumDocument.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    using var before = PdfiumDocument.Open(input);
                    using var after = PdfiumDocument.Open(output);
                    foreach (var page in needOcr.GroupBy(c => c.Page))
                    {
                        var a = before.RenderPage(page.Key, scale);
                        var b = after.RenderPage(page.Key, scale);
                        foreach (var cell in page) crops.Add((cell, ErasedInk(a, b, cell.Rect, scale)));
                    }
                }, ct).ConfigureAwait(false);
            }
            finally { PdfiumDocument.Gate.Release(); }

            var (sheet, rows) = Sheet(crops.Select(c => c.Image).ToList());
            using var ms = new MemoryStream();
            PngWriter.Write(sheet, ms);
            var lines = await engine.RecognizeAsync(new OcrImage(ms.ToArray(), sheet.Width, sheet.Height), languages, ct);
            for (int i = 0; i < crops.Count; i++)
            {
                var (top, bottom) = rows[i];
                var mine = lines.Where(l => (l.Box.Y0 + l.Box.Y1) / 2 is var cy && cy >= top && cy < bottom)
                                .OrderBy(l => l.Box.Y0).ThenBy(l => l.Box.X0).Select(l => l.Text.Trim());
                var cell = crops[i].Cell;
                values[cell.Key] = new(cell.Key, Clean(string.Join(" ", mine), cell.Comb), true);
            }
        }
        }
        catch
        {
            File.Delete(output);
            throw;
        }
        return new PrintedResult([.. cells.Where(c => values.ContainsKey(c.Key)).Select(c => values[c.Key])], report, output);
    }

    private static string Clean(string s, bool comb)
    {
        s = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return comb ? new string([.. s.Where(c => !char.IsWhiteSpace(c))]) : s;
    }

    /// <summary>The cell from <paramref name="before"/>, keeping only pixels that changed in <paramref name="after"/>.</summary>
    private static RgbImage ErasedInk(RgbImage before, RgbImage after, PdfRect cell, double scale)
    {
        int x0 = Math.Clamp((int)Math.Floor(cell.X0 * scale) - Pad, 0, before.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(cell.Y0 * scale) - Pad, 0, before.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(cell.X1 * scale) + Pad, x0 + 1, before.Width);
        int y1 = Math.Clamp((int)Math.Ceiling(cell.Y1 * scale) + Pad, y0 + 1, before.Height);
        int w = x1 - x0, h = y1 - y0;
        var px = new byte[w * h * 3];
        Array.Fill(px, (byte)255);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = ((y0 + y) * before.Width + x0 + x) * 3, d = (y * w + x) * 3;
                int diff = Math.Abs(before.Pixels[s] - after.Pixels[s]) + Math.Abs(before.Pixels[s + 1] - after.Pixels[s + 1])
                         + Math.Abs(before.Pixels[s + 2] - after.Pixels[s + 2]);
                if (diff <= 24) continue;
                px[d] = before.Pixels[s]; px[d + 1] = before.Pixels[s + 1]; px[d + 2] = before.Pixels[s + 2];
            }
        return new RgbImage(w, h, px);
    }

    /// <summary>Crops stacked top to bottom with white gaps; returns each crop's y range in the sheet.</summary>
    private static (RgbImage Sheet, List<(double Top, double Bottom)> Rows) Sheet(IReadOnlyList<RgbImage> crops)
    {
        int width = crops.Max(c => c.Width) + 2 * Gap;
        int height = crops.Sum(c => c.Height) + Gap * (crops.Count + 1);
        var px = new byte[width * height * 3];
        Array.Fill(px, (byte)255);
        var rows = new List<(double, double)>();
        int top = Gap;
        foreach (var c in crops)
        {
            for (int y = 0; y < c.Height; y++)
                Buffer.BlockCopy(c.Pixels, y * c.Width * 3, px, ((top + y) * width + Gap) * 3, c.Width * 3);
            rows.Add((top - Gap / 2.0, top + c.Height + Gap / 2.0));
            top += c.Height + Gap;
        }
        return (new RgbImage(width, height, px), rows);
    }
}
