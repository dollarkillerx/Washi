// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PDFiumCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Washi.Core.Forms;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>
/// A run of text on one line, as it can be edited: its text, its box on the upright page, and how it is drawn
/// (where its baseline starts in PDF space, the size, the font's name and colour).
/// </summary>
public sealed record TextSegment(int Page, string Text, PdfRect Box, double OriginX, double OriginY, double Size, string Font, bool Serif,
                                 int Color, bool Horizontal);

/// <summary>Replace the text inside <see cref="Box"/> (upright points) with <see cref="Text"/>.</summary>
public sealed record TextEdit(int Page, PdfRect Box, string Text);

/// <param name="KeptFont">The document's own font drew the new text; otherwise a similar font (<see cref="Font"/>) did.</param>
public sealed record TextEditResult(int Page, string Old, string New, bool KeptFont, string Font, bool Wider);

/// <summary>
/// Edits text on the page, a line or part of one at a time: the old glyphs are taken out of the content (the rest of the
/// line stays where it was, as in redaction) and the new text is drawn at the same baseline, size and colour, in the
/// document's own font when it has every character needed, otherwise in a similar standard font. The copy is rendered
/// next to the original and its text read back before it is kept. Horizontal text only. Callers hold
/// <see cref="PdfiumDocument.Gate"/>.
/// </summary>
public static partial class PdfTextEditor
{
    // ------------------------------------------------------------------ reading lines

    /// <param name="Size">The size it shows at on the page (the font size through the text and page matrices).</param>
    /// <param name="FontSize">The size the content gave the font (Tf); <see cref="M"/> scales it to the page.</param>
    /// <param name="UX">Where the baseline starts on the upright page (<see cref="UY"/> likewise).</param>
    /// <param name="Upright">It runs left to right on the page as shown (whatever the page's rotation).</param>
    private sealed record Char(string? Text, PdfRect Box, double X, double Y, double Size, string Font, int Flags, int Color,
                               double FontSize = 0, (double A, double B, double C, double D) M = default,
                               double UX = 0, double UY = 0, bool Upright = false);

    /// <summary>The page's text in segments: characters on one baseline, in the same font and size, close together.</summary>
    public static IReadOnlyList<TextSegment> Segments(string path, int page)
    {
        PdfiumDocument.EnsureInit();
        var doc = PdfEraser.Load(path);
        try
        {
            var list = new List<TextSegment>();
            PdfEraser.WithPage(doc, page, (p, map) => list.AddRange(Segments(Chars(p, map), page)));
            return list;
        }
        finally { fpdfview.FPDF_CloseDocument(doc); }
    }

    private static List<Char> Chars(FpdfPageT page, PdfiumDocument.PageMapper map)
    {
        var list = new List<Char>();
        var tp = fpdf_text.FPDFTextLoadPage(page);
        try
        {
            int n = fpdf_text.FPDFTextCountChars(tp);
            var box = new FS_RECTF_();
            var matrix = new FS_MATRIX_();
            var nameBuf = Marshal.AllocHGlobal(256);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    uint u = fpdf_text.FPDFTextGetUnicode(tp, i);
                    bool generated = fpdf_text.FPDFTextIsGenerated(tp, i) == 1;
                    string? s = u is 0 or 0xFFFE or 0xFFFF or > 0x10FFFF || u is >= 0xD800 and <= 0xDFFF ? null : char.ConvertFromUtf32((int)u);
                    if (generated || s is null || s is "\r" or "\n") { list.Add(new Char(s is " " ? " " : null, default, 0, 0, 0, "", 0, 0)); continue; }
                    PdfRect r = default;
                    if (fpdf_text.FPDFTextGetLooseCharBox(tp, i, box) != 0) r = map.ToDevice(box.Left, box.Top, box.Right, box.Bottom);
                    double x = 0, y = 0;
                    fpdf_text.FPDFTextGetCharOrigin(tp, i, ref x, ref y);
                    int flags = 0;
                    var len = (int)fpdf_text.FPDFTextGetFontInfo(tp, i, nameBuf, 256, ref flags);
                    var font = len > 1 ? Marshal.PtrToStringUTF8(nameBuf, len - 1) ?? "" : "";
                    uint cr = 0, cg = 0, cb = 0, ca = 0;
                    fpdf_text.FPDFTextGetFillColor(tp, i, ref cr, ref cg, ref cb, ref ca);
                    double fontSize = fpdf_text.FPDFTextGetFontSize(tp, i);
                    (double A, double B, double C, double D) m = (1, 0, 0, 1);
                    if (fpdf_text.FPDFTextGetMatrix(tp, i, matrix) != 0) m = (matrix.A, matrix.B, matrix.C, matrix.D);
                    double size = fontSize * Math.Sqrt(m.C * m.C + m.D * m.D);
                    // the direction of the text on the upright page
                    var (ux, uy) = map.ToDevice(x, y);
                    double unit = Math.Sqrt(m.A * m.A + m.B * m.B);
                    var (ex, ey) = unit > 0 ? map.ToDevice(x + m.A / unit * 100, y + m.B / unit * 100) : (ux, uy);
                    bool upright = ex - ux > 50 && Math.Abs(ey - uy) < 0.02 * (ex - ux);
                    list.Add(new Char(s, r, x, y, size, font, flags, (int)(cr << 16 | cg << 8 | cb), fontSize, m, ux, uy, upright));
                }
            }
            finally { Marshal.FreeHGlobal(nameBuf); }
        }
        finally { fpdf_text.FPDFTextClosePage(tp); }
        return list;
    }

    private static List<TextSegment> Segments(List<Char> chars, int page)
    {
        var segments = new List<TextSegment>();
        var run = new List<Char>();
        void Close()
        {
            var real = run.Where(c => c.Size > 0).ToList();
            if (real.Count > 0 && real.Any(c => !string.IsNullOrWhiteSpace(c.Text)))
            {
                var first = real[0];
                var text = string.Concat(run.Select(c => c.Text ?? "")).Trim();
                var box = real.Select(c => c.Box).Aggregate((a, b) => new PdfRect(Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1)));
                bool horizontal = real.All(c => c.Upright);
                segments.Add(new TextSegment(page, text, box, first.X, first.Y, Math.Round(first.Size, 2), first.Font, Serif(first), first.Color, horizontal));
            }
            run.Clear();
        }
        Char? last = null;
        foreach (var c in chars)
        {
            if (c.Size <= 0)
            {
                // a gap the text layer added: a space within the line, a break at its end
                if (c.Text == " " && run.Count > 0) run.Add(c);
                else { Close(); last = null; }
                continue;
            }
            if (last is { } l && !(l.Font == c.Font && Math.Abs(l.Size - c.Size) < 0.05 * l.Size && l.Upright == c.Upright
                                    && Math.Abs(l.UY - c.UY) < 0.25 * l.Size && c.UX - l.Box.X1 < 1.2 * l.Size && c.UX >= l.UX - 0.5))
                Close();
            run.Add(c);
            last = c;
        }
        Close();
        return segments;
    }

    /// <summary>Whether the font has serifs: by its name first (fonts often set the flag wrongly), then by its flags.</summary>
    private static bool Serif(Char c) =>
        !Regex.IsMatch(c.Font, "Sans|Gothic|Goth|Meiryo|Arial|Helvetica|Verdana|Hiragino.?Kaku|\\bUD|Yu.?Go|ゴシック|黑|黒|Hei|Dotum|Gulim|Malgun", RegexOptions.IgnoreCase)
        && (Regex.IsMatch(c.Font, "Times|Serif|Mincho|Ming|Song|Myeongjo|Batang|Georgia|Garamond|Century|明朝|宋", RegexOptions.IgnoreCase) || (c.Flags & 2) != 0);

    // ------------------------------------------------------------------ editing

    public static IReadOnlyList<TextEditResult> Apply(string input, string output, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0) throw new PageAssemblyException("no text to change", "Err_EditNothing");
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        PdfiumDocument.EnsureInit();

        // 1. what each edit replaces, read from the page as PDFium sees it
        var plans = new List<(TextEdit Edit, List<Char> Old, double Room)>();
        var pdfium = PdfEraser.Load(input);
        try
        {
            foreach (var group in edits.GroupBy(e => e.Page))
                PdfEraser.WithPage(pdfium, group.Key, (page, map) =>
                {
                    var chars = Chars(page, map);
                    foreach (var e in group)
                    {
                        var inside = chars.Where(c => c.Size > 0 && c.Text is not null && e.Box.Contains(c.Box.Center.X, c.Box.Center.Y)).ToList();
                        if (inside.Count == 0) throw new PageAssemblyException($"there is no text in that place on page {e.Page + 1}", "Err_EditNoText", e.Page + 1);
                        if (inside.Any(c => !c.Upright))
                            throw new PageAssemblyException("only horizontal text can be edited", "Err_EditNotHorizontal");
                        if (inside.Max(c => c.UY) - inside.Min(c => c.UY) > 0.5 * inside.Max(c => c.Size))
                            throw new PageAssemblyException("the place holds more than one line; edit one line at a time", "Err_EditOneLine");
                        // how far right the new text may reach: up to the next character on the same baseline
                        var start = inside.Min(c => c.Box.X0);
                        var end = inside.Max(c => c.Box.X1);
                        double room = chars.Where(c => c.Size > 0 && c.Text is not null && !inside.Contains(c) && c.Box.X0 >= end - 0.5
                                                       && Math.Abs(c.UY - inside[0].UY) < 0.5 * inside[0].Size)
                                           .Select(c => c.Box.X0).DefaultIfEmpty(double.PositiveInfinity).Min();
                        plans.Add((e, inside, room));
                    }
                });
        }
        finally { fpdfview.FPDF_CloseDocument(pdfium); }

        // 2. the old glyphs out, the new text in
        var results = new List<TextEditResult>();
        var allowed = new List<(int Page, PdfRect Rect)>();
        using (var doc = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            foreach (var pageGroup in plans.GroupBy(p => p.Edit.Page))
            {
                var page = doc.Pages[pageGroup.Key];
                var up = PdfRedactor.Upright(page);
                var down = up.Inverse() ?? Mx.Identity;
                var boxes = pageGroup.Select(p => up.Bounds(new Box(p.Edit.Box.X0, p.Edit.Box.Y0, p.Edit.Box.X1, p.Edit.Box.Y1))).ToList();
                // the page's resources, our own copy, to add fonts to
                var resources = PdfEraser.Resources(page) is { } pr ? (PdfDictionary)pr.Clone() : new PdfDictionary(doc);
                var fontsDict = Deref(resources.Elements["/Font"]) is PdfDictionary fd0 ? (PdfDictionary)fd0.Clone() : new PdfDictionary(doc);
                resources.Elements["/Font"] = fontsDict;
                page.Elements["/Resources"] = resources;

                // the new text of each edit, in page space
                var inserts = new List<TextInsert>();
                int k = 0;
                foreach (var ((edit, old, room), box) in pageGroup.Zip(boxes))
                {
                    var first = old[0];
                    var oldText = string.Concat(old.Select(c => c.Text));
                    var newText = edit.Text.Replace("\r", "").Replace("\n", " ");
                    var (fontRef, encoded, width, kept, fontName) = Encode(doc, resources, first, newText);
                    string key;
                    do key = "/WashiT" + (++k).ToString(CultureInfo.InvariantCulture); while (fontsDict.Elements.ContainsKey(key));
                    double size = first.Size;
                    var (ma, mb, mc, md) = first.M;
                    double oldWidth = old[^1].Box.X1 - first.Box.X0;
                    double newWidth = width * first.FontSize * Math.Sqrt(ma * ma + mb * mb);
                    bool wider = newWidth > oldWidth + 1;
                    // a longer text must not run into what follows on the line
                    if (wider && first.Box.X0 + newWidth > room + 0.5)
                        throw new PageAssemblyException($"“{newText}” is longer than “{oldText}” and would run into the text after it on the line; change the whole line instead",
                                                        "Err_EditTooLong", newText, oldText);
                    var (r, g, b) = ((first.Color >> 16 & 255) / 255.0, (first.Color >> 8 & 255) / 255.0, (first.Color & 255) / 255.0);
                    var draw = "";
                    if (newText.Length > 0)
                    {
                        fontsDict.Elements[key] = fontRef;
                        draw = string.Create(CultureInfo.InvariantCulture,
                            $"{N(r)} {N(g)} {N(b)} rg BT {key} {N(first.FontSize)} Tf 0 Tr 0 Tc 0 Tw 100 Tz 0 Ts {N(ma)} {N(mb)} {N(mc)} {N(md)} {N(first.X)} {N(first.Y)} Tm {encoded} Tj ET");
                    }
                    inserts.Add(new TextInsert(box, draw));
                    results.Add(new TextEditResult(edit.Page, oldText, newText, kept, fontName, wider));
                    // where change is expected: the old glyphs and the new text, on the upright page
                    allowed.Add((edit.Page, Union(edit.Box, ToUpright(down, first, width))));
                }

                // the old glyphs out, the new text in their place in the content (so the text reads in order)
                var ctx = new RedactContext(doc, boxes, null) { TextOnly = true, Inserts = inserts.Where(i => i.Draw.Length > 0).ToList() };
                var res = new ResourceScope(doc, resources, r => page.Elements["/Resources"] = r);
                var content = PdfEraser.ReadContents(page);
                var bytes = new ContentRedactor(ctx).Run(content, res, Mx.Identity) ?? content;
                res.DropUnused(bytes);
                // glyphs inside a form: the new text goes on top of the page
                var rest = ctx.Inserts.Where(i => !i.Placed).Select(i => "q " + i.Draw + " Q\n").ToList();
                if (rest.Count > 0)
                    bytes = [.. "q\n"u8.ToArray(), .. bytes, .. "\nQ\n"u8.ToArray(), .. Encoding.Latin1.GetBytes(string.Concat(rest))];
                PdfEraser.WriteContents(doc, page, bytes);
            }
            doc.Save(output);
        }

        // 3. proof: nothing changed elsewhere, and the new text reads back
        try
        {
            Verify(input, output, plans, allowed);
        }
        catch
        {
            File.Delete(output);
            throw;
        }
        return results;
    }

    /// <summary>
    /// Find and replace: every place <paramref name="find"/> appears (on <paramref name="page"/>, or every page) becomes
    /// <paramref name="with"/>. A match that runs over two lines is refused, as is one that would run into the text after it.
    /// </summary>
    public static IReadOnlyList<TextEditResult> Replace(string input, string output, string find, string with, int? page = null,
                                                        bool matchCase = true, bool wholeWords = false)
    {
        var hits = PdfSearch.Find(input, find, matchCase, wholeWords).Where(h => page is null || h.Page == page).ToList();
        if (hits.Count == 0) throw new PageAssemblyException($"“{find}” is not in the document", "Err_EditNotFound", find);
        var edits = hits.Select(h => new TextEdit(h.Page, h.Boxes.Aggregate(Union), with)).ToList();
        return Apply(input, output, edits);
    }

    /// <summary>Where the new text (<paramref name="em"/> wide) lands on the upright page: from its baseline start, from below the descenders to above the capitals.</summary>
    private static PdfRect ToUpright(Mx down, Char first, double em)
    {
        var (a, b, c, d) = first.M;
        var s = first.FontSize;
        var b0 = new Mx(a * s, b * s, c * s, d * s, first.X, first.Y).Then(down).Bounds(new Box(0, -0.3, em, 1.0));
        return new PdfRect(b0.X0, b0.Y0, b0.X1, b0.Y1);
    }

    private static PdfRect Union(PdfRect a, PdfRect b) =>
        new(Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1));

    private static void Verify(string before, string after, List<(TextEdit Edit, List<Char> Old, double Room)> plans, List<(int Page, PdfRect Rect)> allowed)
    {
        OnlyChangedIn(before, after, plans.Select(p => p.Edit.Page).Distinct(), allowed);
        ReadBack(after, plans, allowed);
    }

    /// <summary>The two renderings of each page differ only inside <paramref name="allowed"/> (sub-pixel shifts aside).</summary>
    private static void OnlyChangedIn(string before, string after, IEnumerable<int> pages, List<(int Page, PdfRect Rect)> allowed)
    {
        using var a = PdfiumDocument.Open(before);
        using var b = PdfiumDocument.Open(after);
        foreach (var page in pages)
        {
            const double scale = 2;   // at 1:1, glyphs snap to whole pixels and text in a new text object can land one off
            var ia = a.RenderPage(page, scale);
            var ib = b.RenderPage(page, scale);
            var ok = new bool[ia.Width * ia.Height];
            foreach (var r in allowed.Where(x => x.Page == page).Select(x => x.Rect))
            {
                int x0 = Math.Max(0, (int)Math.Floor(r.X0 * scale) - 3), x1 = Math.Min(ia.Width - 1, (int)Math.Ceiling(r.X1 * scale) + 3);
                int y0 = Math.Max(0, (int)Math.Floor(r.Y0 * scale) - 3), y1 = Math.Min(ia.Height - 1, (int)Math.Ceiling(r.Y1 * scale) + 3);
                for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) ok[y * ia.Width + x] = true;
            }
            int changed = 0, cx0 = int.MaxValue, cy0 = int.MaxValue, cx1 = 0, cy1 = 0;
            // a pixel counts as changed when nothing within a pixel of it in the other rendering looks like it: text after
            // the edit that moved into a new text object may be anti-aliased a little differently, and that is not a change
            for (int i = 0; i < ok.Length; i++)
                if (!ok[i] && Diff(ia, ib, i, i) > 24 && !(Near(ia, ib, i) && Near(ib, ia, i)))
                {
                    changed++;
                    int px = i % ia.Width, py = i / ia.Width;
                    cx0 = Math.Min(cx0, px); cy0 = Math.Min(cy0, py); cx1 = Math.Max(cx1, px); cy1 = Math.Max(cy1, py);
                }
            if (changed > 0)
                throw new PageAssemblyException($"the edited copy changed {changed} pixels outside the edited text (in {cx0 / scale:0},{cy0 / scale:0}–{cx1 / scale:0},{cy1 / scale:0} pt on page {page + 1}); it was not kept",
                                                "Err_EditChanged", changed);
        }
    }

    /// <summary>The new text of each line edit reads back where it was drawn, at the old size.</summary>
    private static void ReadBack(string after, List<(TextEdit Edit, List<Char> Old, double Room)> plans, List<(int Page, PdfRect Rect)> allowed)
    {
        var pdfium = PdfEraser.Load(after);
        try
        {
            foreach (var group in plans.GroupBy(p => p.Edit.Page))
                PdfEraser.WithPage(pdfium, group.Key, (page, map) =>
                {
                    var chars = Chars(page, map);
                    foreach (var (edit, old, _) in group)
                    {
                        var area = allowed.Where(x => x.Page == edit.Page).Select(x => x.Rect).First(r => r.Intersect(edit.Box).Area > 0);
                        var drawn = chars.Where(c => c.Size > 0 && c.Text is not null && area.Contains(c.Box.Center.X, c.Box.Center.Y)
                                                     && Math.Abs(c.UY - old[0].UY) < 0.3 * old[0].Size).ToList();
                        var got = string.Concat(drawn.Select(c => c.Text));
                        if (Norm(edit.Text).Length == 0) continue;
                        // the characters that spell the new text, one after another, at the old size: in the text layer's
                        // order, or as they sit on the page (the text layer may reorder a line it takes for right to left)
                        bool found = Spells(drawn, edit.Text, old[0].Size) || Spells(drawn.OrderBy(c => c.UX).ToList(), edit.Text, old[0].Size);
                        if (!found)
                            throw new PageAssemblyException($"the new text did not read back (got “{got}”); the copy was not kept", "Err_EditCheck", got);
                    }
                });
        }
        finally { fpdfview.FPDF_CloseDocument(pdfium); }
    }

    private static bool Spells(List<Char> chars, string text, double size)
    {
        var want = Norm(text);
        for (int i = 0; i < chars.Count; i++)
        {
            var run = "";
            for (int j = i; j < chars.Count && run.Length < want.Length; j++)
            {
                run += Norm(chars[j].Text!);
                if (run == want && chars.Skip(i).Take(j - i + 1).All(c => Math.Abs(c.Size - size) <= 0.1 * size)) return true;
            }
        }
        return false;
    }

    private static string Norm(string s) => Regex.Replace(s.Normalize(NormalizationForm.FormKC), @"\s+", "");

    private static int Diff(RgbImage a, RgbImage b, int i, int j) =>
        Math.Abs(a.Pixels[i * 3] - b.Pixels[j * 3]) + Math.Abs(a.Pixels[i * 3 + 1] - b.Pixels[j * 3 + 1]) + Math.Abs(a.Pixels[i * 3 + 2] - b.Pixels[j * 3 + 2]);

    /// <summary>
    /// Pixel <paramref name="i"/> of <paramref name="a"/> lies, channel by channel, within what the pixels around it in
    /// <paramref name="b"/> span: text that moved by less than a pixel, not text that came or went.
    /// </summary>
    private static bool Near(RgbImage a, RgbImage b, int i)
    {
        int x = i % a.Width, y = i / a.Width;
        for (int ch = 0; ch < 3; ch++)
        {
            int lo = 255, hi = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= b.Width || ny >= b.Height) continue;
                    int v = b.Pixels[(ny * b.Width + nx) * 3 + ch];
                    lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                }
            int p = a.Pixels[i * 3 + ch];
            if (p < lo - 8 || p > hi + 8) return false;
        }
        return true;
    }

    private static string N(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
