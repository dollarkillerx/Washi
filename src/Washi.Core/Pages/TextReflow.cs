// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PDFiumCore;
using PdfSharp.Pdf;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>A paragraph as it can be edited: lines in one font and size, one under another at an even spacing.</summary>
public sealed record TextParagraph(int Page, string Text, PdfRect Box, int Lines, double Size, string Font);

public static partial class PdfTextEditor
{
    // ------------------------------------------------------------------ finding paragraphs

    /// <summary>The page's paragraphs: its lines grouped when they share a font and size, start at the same place (the first may be indented) and follow at an even spacing.</summary>
    public static IReadOnlyList<TextParagraph> Paragraphs(string path, int page)
    {
        PdfiumDocument.EnsureInit();
        var doc = PdfEraser.Load(path);
        try
        {
            var list = new List<TextParagraph>();
            PdfEraser.WithPage(doc, page, (p, map) => list.AddRange(Paragraphs(Segments(Chars(p, map), page))));
            return list;
        }
        finally { fpdfview.FPDF_CloseDocument(doc); }
    }

    private static IEnumerable<TextParagraph> Paragraphs(List<TextSegment> lines)
    {
        var block = new List<TextSegment>();
        TextParagraph Close()
        {
            var box = block.Select(l => l.Box).Aggregate(Union);
            var text = new StringBuilder(block[0].Text);
            for (int i = 1; i < block.Count; i++) text.Append(Joiner(block[i - 1].Text, block[i].Text)).Append(block[i].Text);
            var p = new TextParagraph(block[0].Page, text.ToString(), box, block.Count, block[0].Size, block[0].Font);
            block.Clear();
            return p;
        }
        foreach (var l in VisualLines(lines.Where(l => l.Horizontal).ToList()))
        {
            if (block.Count > 0 && !Continues(block, l)) yield return Close();
            block.Add(l);
        }
        if (block.Count > 0) yield return Close();
    }

    /// <summary>
    /// Runs on one baseline, one after another, joined into the line they make (a Japanese line often draws its digits
    /// in another font): the line takes the font and size most of its characters have.
    /// </summary>
    private static List<TextSegment> VisualLines(List<TextSegment> runs)
    {
        var lines = new List<List<TextSegment>>();
        foreach (var r in runs)
        {
            var last = lines.Count > 0 ? lines[^1][^1] : null;
            if (last is not null && Math.Abs(r.Box.Y1 - last.Box.Y1) < 0.3 * Math.Max(r.Size, last.Size)
                && r.Box.X0 >= last.Box.X1 - 1 && r.Box.X0 - last.Box.X1 < 2 * Math.Max(r.Size, last.Size))
                lines[^1].Add(r);
            else lines.Add([r]);
        }
        return [.. lines.Select(l =>
        {
            var main = l.OrderByDescending(r => r.Text.Length).First();
            var text = new StringBuilder();
            foreach (var r in l)
            {
                if (text.Length > 0 && r.Box.X0 - l[l.IndexOf(r) - 1].Box.X1 > 0.25 * r.Size && !IsCjk(text[^1]) && !IsCjk(r.Text[0])) text.Append(' ');
                text.Append(r.Text);
            }
            return main with { Text = text.ToString(), Box = l.Select(r => r.Box).Aggregate(Union), OriginX = l[0].OriginX, OriginY = l[0].OriginY };
        })];
    }

    /// <summary>Whether <paramref name="next"/> is the next line of the paragraph so far.</summary>
    private static bool Continues(List<TextSegment> block, TextSegment next)
    {
        var last = block[^1];
        double size = last.Size;
        if (Math.Abs(next.Size - size) > 0.05 * size) return false;
        double gap = next.Box.Y1 - last.Box.Y1;   // baseline to baseline, near enough
        if (gap < 0.9 * size || gap > 2.4 * size) return false;
        if (block.Count >= 2 && Math.Abs(gap - (block[1].Box.Y1 - block[0].Box.Y1)) > 0.15 * size) return false;
        // a line that stops well short of the column ended its paragraph
        if (last.Box.X1 < block.Max(l => l.Box.X1) - 2 * size) return false;
        // the left edge: the same as the lines after the first; the first may be indented
        double left = block.Count >= 2 ? block[1].Box.X0 : block[0].Box.X0;
        if (block.Count >= 2) return Math.Abs(next.Box.X0 - left) < 0.6 * size;
        return next.Box.X0 > block[0].Box.X0 - 3 * size && next.Box.X0 < block[0].Box.X0 + 0.6 * size;   // the first line indented, or not
    }

    /// <summary>What goes between two lines when they are read as one text: nothing between CJK, a space otherwise (none after a hyphen).</summary>
    private static string Joiner(string a, string b) =>
        a.Length == 0 || b.Length == 0 || a.EndsWith('-') || IsCjk(a[^1]) || IsCjk(b[0]) ? "" : " ";

    private static bool IsCjk(char c) => c is >= '　' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '가' and <= '힯'
                                         or >= '豈' and <= '﫿' or >= '＀' and <= '￯' || char.IsSurrogate(c);

    /// <summary>The paragraph in which <paramref name="text"/> first appears (on <paramref name="page"/>, or any page).</summary>
    public static TextParagraph ParagraphContaining(string path, string text, int? page = null)
    {
        var hit = PdfSearch.Find(path, text, matchCase: true).FirstOrDefault(h => page is null || h.Page == page)
                  ?? throw new PageAssemblyException($"“{text}” is not in the document", "Err_EditNotFound", text);
        return Paragraphs(path, hit.Page).FirstOrDefault(p => hit.Boxes.Any(b => p.Box.Intersect(b).Area > 0))
               ?? throw new PageAssemblyException($"“{text}” is not in a paragraph that can be set again", "Err_ParagraphNone", text);
    }

    // ------------------------------------------------------------------ editing a paragraph

    /// <summary>
    /// Replaces the paragraph inside each edit's box with its new text, set again in the paragraph's width, font, size and
    /// line spacing (the first line's indent kept): Latin text breaks between words, CJK between characters (closing
    /// marks may hang past the edge, an opening one never ends a line); a line break in the new text starts a new line.
    /// The paragraph may grow into empty space below it but not over anything; the copy is checked as for line edits.
    /// </summary>
    public static IReadOnlyList<TextEditResult> Reflow(string input, string output, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0) throw new PageAssemblyException("no text to change", "Err_EditNothing");
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        PdfiumDocument.EnsureInit();

        // 1. each paragraph: its lines, where they start, the spacing, the room below
        var plans = new List<Para>();
        var pdfium = PdfEraser.Load(input);
        try
        {
            foreach (var group in edits.GroupBy(e => e.Page))
                PdfEraser.WithPage(pdfium, group.Key, (page, map) =>
                {
                    var chars = Chars(page, map);
                    var shapes = Shapes(page, map);
                    double pageHeight = map.ToDevice(0, 0, fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page)).Height;
                    foreach (var e in group) plans.Add(Plan(e, chars, shapes, pageHeight));
                });
        }
        finally { fpdfview.FPDF_CloseDocument(pdfium); }

        // 2. the old lines out, the new ones in
        var results = new List<TextEditResult>();
        var allowed = new List<(int Page, PdfRect Rect)>();
        var drawn = new List<(Para Plan, List<string> Lines, PdfRect Area)>();
        using (var doc = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            foreach (var pageGroup in plans.GroupBy(p => p.Edit.Page))
            {
                var page = doc.Pages[pageGroup.Key];
                var up = PdfRedactor.Upright(page);
                var resources = PdfEraser.Resources(page) is { } pr ? (PdfDictionary)pr.Clone() : new PdfDictionary(doc);
                var fontsDict = Deref(resources.Elements["/Font"]) is PdfDictionary fd0 ? (PdfDictionary)fd0.Clone() : new PdfDictionary(doc);
                resources.Elements["/Font"] = fontsDict;
                page.Elements["/Resources"] = resources;

                var boxes = new List<Box>();
                var pathBoxes = new List<Box>();
                var inserts = new List<TextInsert>();
                int k = 0;
                foreach (var plan in pageGroup)
                {
                    var first = plan.First;
                    var text = plan.Edit.Text.Replace("\r", "");
                    var (fontRef, pen, kept, fontName) = PenFor(doc, resources, first, text.Replace("\n", ""));
                    var (ma, mb, mc, md) = first.M;
                    double emToPt = first.FontSize * Math.Sqrt(ma * ma + mb * mb);
                    double spacing = Spacing(resources, plan.LineChars);               // the old character spacing, kept
                    double W(string str) => str.Length == 0 ? 0 : pen(str).Em * emToPt + spacing * str.EnumerateRunes().Count();
                    var lines = Break(text, W, plan.FirstWidth, plan.Width);
                    if (lines.Count > plan.MaxLines)
                        throw new PageAssemblyException($"the new text needs {lines.Count} lines and the paragraph has room for {plan.MaxLines}; shorten it",
                                                        "Err_ParagraphTooLong", lines.Count, plan.MaxLines);
                    string key;
                    do key = "/WashiT" + (++k).ToString(CultureInfo.InvariantCulture); while (fontsDict.Elements.ContainsKey(key));
                    fontsDict.Elements[key] = fontRef;

                    // each line: its own start (the first indented as before), one spacing further down
                    double ul = Math.Sqrt(ma * ma + mb * mb), vl = Math.Sqrt(mc * mc + md * md);
                    double ux = ma / ul, uy = mb / ul, vx = -mc / vl, vy = -md / vl;   // along the line, and down the page, in PDF space
                    var (r, g, b) = ((first.Color >> 16 & 255) / 255.0, (first.Color >> 8 & 255) / 255.0, (first.Color & 255) / 255.0);
                    double tc = spacing / ul;
                    var draw = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
                        $"{N(r)} {N(g)} {N(b)} rg BT {key} {N(first.FontSize)} Tf 0 Tr {N(tc)} Tc 0 Tw 100 Tz 0 Ts "));
                    double widest = 0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        double along = (i == 0 ? plan.FirstStart : plan.Start) - first.UX, down = i * plan.Leading;
                        double x = first.X + along * ux + down * vx, y = first.Y + along * uy + down * vy;
                        var (hex, _) = pen(lines[i]);
                        widest = Math.Max(widest, along + W(lines[i]));
                        if (lines[i].Length > 0)
                            draw.Append(CultureInfo.InvariantCulture, $"{N(ma)} {N(mb)} {N(mc)} {N(md)} {N(x)} {N(y)} Tm {hex} Tj ");
                    }
                    draw.Append("ET");

                    // underlines and markers go with their words, where the words now are
                    var marks = Place(plan, lines, W);
                    var (before, after) = (new StringBuilder(), new StringBuilder());
                    foreach (var (rect, d) in marks)
                    {
                        var target = d.Bottom - d.Top > 0.5 * first.Size ? before : after;   // a marker under the text, an underline over it
                        var (dr, dg, db) = ((d.Color >> 16 & 255) / 255.0, (d.Color >> 8 & 255) / 255.0, (d.Color & 255) / 255.0);
                        target.Append(CultureInfo.InvariantCulture,
                            $"q {N(up.A)} {N(up.B)} {N(up.C)} {N(up.D)} {N(up.E)} {N(up.F)} cm {N(dr)} {N(dg)} {N(db)} rg {N(rect.X0)} {N(rect.Y0)} {N(rect.Width)} {N(rect.Height)} re f Q ");
                        allowed.Add((plan.Edit.Page, rect));
                    }
                    var whole = before + draw.ToString() + (after.Length > 0 ? " " + after.ToString().TrimEnd() : "");
                    foreach (var d in plan.Decor)
                    {
                        pathBoxes.Add(up.Bounds(new Box(d.Box.X0 - 0.5, d.Box.Y0 - 0.5, d.Box.X1 + 0.5, d.Box.Y1 + 0.5)));
                        allowed.Add((plan.Edit.Page, d.Box));
                    }
                    foreach (var line in plan.Old) boxes.Add(up.Bounds(new Box(line.X0, line.Y0, line.X1, line.Y1)));
                    inserts.Add(new TextInsert(up.Bounds(new Box(plan.Old[0].X0, plan.Old[0].Y0, plan.Old[0].X1, plan.Old[0].Y1)), whole));

                    var area = new PdfRect(Math.Min(plan.Start, plan.FirstStart), first.UY - first.Size,
                                           first.UX + widest, first.UY + (lines.Count - 1) * plan.Leading + first.Size * 0.35);
                    allowed.Add((plan.Edit.Page, Union(plan.Box, area)));
                    drawn.Add((plan, lines, area));
                    results.Add(new TextEditResult(plan.Edit.Page, plan.Text, text, kept, fontName, lines.Count > plan.Old.Count));
                }

                var ctx = new RedactContext(doc, boxes, null) { TextOnly = true, Inserts = inserts, PathBoxes = pathBoxes };
                var res = new ResourceScope(doc, resources, rs => page.Elements["/Resources"] = rs);
                var content = PdfEraser.ReadContents(page);
                var bytes = new ContentRedactor(ctx).Run(content, res, Mx.Identity) ?? content;
                res.DropUnused(bytes);
                var rest = ctx.Inserts.Where(i => !i.Placed).Select(i => "q " + i.Draw + " Q\n").ToList();
                if (rest.Count > 0)
                    bytes = [.. "q\n"u8.ToArray(), .. bytes, .. "\nQ\n"u8.ToArray(), .. Encoding.Latin1.GetBytes(string.Concat(rest))];
                PdfEraser.WriteContents(doc, page, bytes);
            }
            Washi.Core.Fonts.SubsetFont.FinishAll(doc);
            doc.Save(output);
        }

        // 3. proof: nothing changed outside the paragraphs, and each one reads back, line by line, at its size
        try
        {
            OnlyChangedIn(input, output, plans.Select(p => p.Edit.Page).Distinct(), allowed);
            ReadBackParagraphs(output, drawn);
        }
        catch
        {
            File.Delete(output);
            throw;
        }
        return results;
    }

    private sealed record Para(TextEdit Edit, Char First, List<PdfRect> Old, string Text, PdfRect Box, double FirstStart, double Start,
                               double FirstWidth, double Width, double Leading, int MaxLines, List<List<Char>> LineChars, List<Decoration> Decor);

    /// <summary>A filled or stroked shape on the page (upright points), and its colour.</summary>
    private sealed record Shape(PdfRect Box, int Color, bool Filled);

    /// <summary>
    /// An underline or a marker under some of the paragraph's words: the words, and the band it covers relative to their
    /// line's baseline (upright points).
    /// </summary>
    private sealed record Decoration(PdfRect Box, string Text, double Top, double Bottom, int Color);

    /// <summary>The page's own shapes (paths), as PDFium sees them.</summary>
    private static List<Shape> Shapes(FpdfPageT page, PdfiumDocument.PageMapper map)
    {
        var list = new List<Shape>();
        int n = fpdf_edit.FPDFPageCountObjects(page);
        for (int i = 0; i < n; i++)
        {
            var obj = fpdf_edit.FPDFPageGetObject(page, i);
            if (fpdf_edit.FPDFPageObjGetType(obj) != 2) continue;   // FPDF_PAGEOBJ_PATH
            float l = 0, b = 0, r = 0, t = 0;
            if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0) continue;
            int fill = 0, stroke = 0;
            fpdf_edit.FPDFPathGetDrawMode(obj, ref fill, ref stroke);
            uint cr = 0, cg = 0, cb = 0, ca = 0;
            if (fill != 0) fpdf_edit.FPDFPageObjGetFillColor(obj, ref cr, ref cg, ref cb, ref ca);
            else fpdf_edit.FPDFPageObjGetStrokeColor(obj, ref cr, ref cg, ref cb, ref ca);
            list.Add(new Shape(map.ToDevice(l, t, r, b), (int)(cr << 16 | cg << 8 | cb), fill != 0));
        }
        return list;
    }

    /// <summary>
    /// The underlines (a thin rule just under a line's baseline) and markers (a fill as tall as the line) under some of the
    /// paragraph's words; one running from the end of a line to the start of the next is one.
    /// </summary>
    private static List<Decoration> Decorations(List<List<Char>> lines, List<Shape> shapes, double size, double left, double right)
    {
        var found = new List<(int Line, int From, int To, Decoration D)>();
        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            double baseline = line[0].UY;
            foreach (var s in shapes)
            {
                var b = s.Box;
                if (b.Width < 0.5 * size || b.X0 < left - 2 || b.X1 > right + 2 || b.Width > right - left + 1) continue;
                bool underline = b.Height <= Math.Max(1.5, 0.15 * size) && b.Y0 >= baseline - 0.1 * size && b.Y1 <= baseline + 0.4 * size;
                bool marker = s.Filled && b.Height >= 0.5 * size && b.Height <= 1.6 * size && (b.Y0 + b.Y1) / 2 > baseline - size && (b.Y0 + b.Y1) / 2 < baseline + 0.2 * size;
                if (!underline && !marker) continue;
                var under = Enumerable.Range(0, line.Count).Where(i => line[i].Box.Center.X >= b.X0 - 0.5 && line[i].Box.Center.X <= b.X1 + 0.5).ToList();
                if (under.Count == 0) continue;
                var text = string.Concat(under.Select(i => line[i].Text));
                found.Add((li, under[0], under[^1], new Decoration(b, text, b.Y0 - baseline, b.Y1 - baseline, s.Color)));
            }
        }
        // joined across a line break
        var merged = new List<Decoration>();
        foreach (var f in found.OrderBy(f => f.Line).ThenBy(f => f.From))
        {
            if (merged.Count > 0 && found.FirstOrDefault(p => p.D == merged[^1]) is var prev && prev.D is not null
                && prev.Line == f.Line - 1 && prev.To == lines[prev.Line].Count - 1 && f.From == 0
                && prev.D.Color == f.D.Color && Math.Abs(prev.D.Bottom - prev.D.Top - (f.D.Bottom - f.D.Top)) < 0.5)
            {
                var joined = prev.D with { Text = prev.D.Text + f.D.Text };
                merged[^1] = joined;
                found[found.IndexOf(prev)] = (prev.Line, prev.From, f.To, joined);
                continue;
            }
            merged.Add(f.D);
        }
        return merged;
    }

    private static Para Plan(TextEdit e, List<Char> chars, List<Shape> shapes, double pageHeight)
    {
        var inside = chars.Where(c => c.Size > 0 && c.Text is not null && e.Box.Contains(c.Box.Center.X, c.Box.Center.Y)).ToList();
        if (inside.Count == 0) throw new PageAssemblyException($"there is no text in that place on page {e.Page + 1}", "Err_EditNoText", e.Page + 1);
        if (inside.Any(c => !c.Upright)) throw new PageAssemblyException("only horizontal text can be edited", "Err_EditNotHorizontal");
        // one size (small marks and digits in another font are fine: the paragraph is set in the font most of it uses)
        var size = inside.GroupBy(c => Math.Round(c.Size, 1)).OrderByDescending(g => g.Count()).First().First().Size;
        if (inside.Count(c => Math.Abs(c.Size - size) > 0.05 * size) > inside.Count / 10)
            throw new PageAssemblyException("the paragraph mixes sizes; edit its lines one at a time", "Err_ParagraphMixed");
        var font = inside.GroupBy(c => c.Font).OrderByDescending(g => g.Count()).First().Key;

        // the lines, top to bottom
        var lines = new List<List<Char>>();
        foreach (var c in inside.OrderBy(c => c.UY))
        {
            if (lines.Count > 0 && Math.Abs(c.UY - lines[^1][0].UY) < 0.5 * size) lines[^1].Add(c);
            else lines.Add([c]);
        }
        foreach (var l in lines) l.Sort((x, y) => x.UX.CompareTo(y.UX));
        // where the paragraph starts, drawn in its main font and size
        var first = lines[0][0] with { Font = font, Size = size };
        var model = inside.First(c => c.Font == font && Math.Abs(c.Size - size) <= 0.05 * size);
        first = first with { FontSize = model.FontSize, M = model.M, Flags = model.Flags, Color = model.Color };
        double firstStart = first.UX, start = lines.Count > 1 ? lines.Skip(1).Min(l => l[0].UX) : firstStart;
        double right = inside.Max(c => c.Box.X1);
        double leading = lines.Count > 1
            ? lines.Zip(lines.Skip(1), (a, b) => b[0].UY - a[0].UY).Order().ElementAt((lines.Count - 1) / 2)
            : size * 1.4;
        var box = inside.Select(c => c.Box).Aggregate(Union);

        // room below: down to the next thing under the paragraph's column, or the foot of the page
        double lastBase = lines[^1][0].UY;
        double floor = chars.Where(c => c.Size > 0 && c.Text is not null && !inside.Contains(c) && c.Box.Y0 > box.Y1 - 0.5
                                        && c.Box.X1 > Math.Min(start, firstStart) && c.Box.X0 < right)
                            .Select(c => c.Box.Y0).DefaultIfEmpty(pageHeight - 18).Min();
        int extra = (int)Math.Floor((floor - (lastBase + size * 0.3)) / leading);
        int maxLines = lines.Count + Math.Max(0, extra);

        var text = new StringBuilder();
        foreach (var l in lines)
        {
            var t = string.Concat(l.Select(c => c.Text));
            if (text.Length > 0) text.Append(Joiner(text.ToString(), t));
            text.Append(t);
        }
        var decor = Decorations(lines, shapes, size, Math.Min(start, firstStart), right);
        return new Para(e, first, [.. lines.Select(l => l.Select(c => c.Box).Aggregate(Union))], text.ToString(), box,
                        firstStart, start, right - firstStart, right - start, leading, maxLines, lines, decor);
    }

    /// <summary>
    /// Where each decoration's words are in the new lines: the first place after the one before where they appear
    /// (spaces aside); a decoration whose words are gone goes with them. A run over a line break gives a piece per line.
    /// </summary>
    private static List<(PdfRect Rect, Decoration D)> Place(Para plan, List<string> lines, Func<string, double> W)
    {
        var at = new List<(int Line, int Index)>();   // the new text's characters, spaces left out
        var flat = new StringBuilder();
        for (int li = 0; li < lines.Count; li++)
            for (int ci = 0; ci < lines[li].Length; ci++)
                if (!char.IsWhiteSpace(lines[li][ci])) { at.Add((li, ci)); flat.Append(lines[li][ci]); }
        var placed = new List<(PdfRect, Decoration)>();
        int from = 0;
        foreach (var d in plan.Decor)
        {
            var want = string.Concat(d.Text.Where(c => !char.IsWhiteSpace(c)));
            int k = want.Length == 0 ? -1 : flat.ToString().IndexOf(want, from, StringComparison.Ordinal);
            if (k < 0) continue;
            from = k + want.Length;
            foreach (var g in Enumerable.Range(k, want.Length).Select(i => at[i]).GroupBy(p => p.Line))
            {
                var line = lines[g.Key];
                double start = g.Key == 0 ? plan.FirstStart : plan.Start, baseline = plan.First.UY + g.Key * plan.Leading;
                int i0 = g.Min(p => p.Index), i1 = g.Max(p => p.Index);
                double x0 = start + W(line[..i0]), x1 = start + W(line[..(i1 + 1)]);
                placed.Add((new PdfRect(x0, baseline + d.Top, x1, baseline + d.Bottom), d));
            }
        }
        return placed;
    }

    /// <summary>Characters that may hang past the right edge rather than start a line, and ones that never end a line.</summary>
    private const string NoStart = "、。，．,.)]}」』）】〕〉》｝！？!?：；:;ー…‥ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ々゛゜";
    private const string NoEnd = "([{「『（【〔〈《｛";

    /// <summary>The text broken into lines that fit (widths in points; the first line's may differ).</summary>
    private static List<string> Break(string text, Func<string, double> W, double firstWidth, double width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        double Fits() => (lines.Count == 0 ? firstWidth : width) * 1.01 + 0.5;   // text set a hair tighter than ours still fits

        // a token on an empty line: as it is, or broken by character when it is longer than the line
        void Start(string token)
        {
            if (W(token) <= Fits() || token.EnumerateRunes().Count() == 1) { line.Append(token); return; }
            foreach (var ch in token.EnumerateRunes().Select(r => r.ToString()))
            {
                if (line.Length > 0 && W(line + ch) > Fits()) { lines.Add(line.ToString()); line.Clear(); }
                line.Append(ch);
            }
        }

        foreach (var part in text.Split('\n'))
        {
            foreach (var token in Tokens(part))
            {
                bool space = string.IsNullOrWhiteSpace(token);
                if (line.Length == 0)
                {
                    if (!space) Start(token);   // no space at the start of a line
                    continue;
                }
                if (space || W((line + token).TrimEnd()) <= Fits() + 0.01 || token.Length == 1 && NoStart.Contains(token[0]))
                {
                    line.Append(token);         // a closing mark hangs past the edge rather than start a line
                    continue;
                }
                // a new line; an opening mark at the end of this one goes with it
                var current = line.ToString().TrimEnd();
                var carry = current.Length > 1 && NoEnd.Contains(current[^1]) ? current[^1..] : "";
                lines.Add(current[..^carry.Length]);
                line.Clear().Append(carry);
                if (line.Length == 0) Start(token); else line.Append(token);
            }
            lines.Add(line.ToString().TrimEnd());
            line.Clear();
        }
        return lines;
    }

    /// <summary>Words, runs of spaces, and single CJK characters (where a line may break).</summary>
    private static IEnumerable<string> Tokens(string s)
    {
        var word = new StringBuilder();
        foreach (var r in s.EnumerateRunes())
        {
            var t = r.ToString();
            bool space = Rune.IsWhiteSpace(r), cjk = IsCjk(t[0]);
            if (space || cjk)
            {
                if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                yield return t;
            }
            else word.Append(t);
        }
        if (word.Length > 0) yield return word.ToString();
    }

    /// <summary>Each new paragraph's lines read back in their place, at the old size.</summary>
    private static void ReadBackParagraphs(string after, List<(Para Plan, List<string> Lines, PdfRect Area)> drawn)
    {
        var pdfium = PdfEraser.Load(after);
        try
        {
            foreach (var group in drawn.GroupBy(d => d.Plan.Edit.Page))
                PdfEraser.WithPage(pdfium, group.Key, (page, map) =>
                {
                    var chars = Chars(page, map);
                    foreach (var (plan, lines, area) in group)
                        for (int i = 0; i < lines.Count; i++)
                        {
                            if (Norm(lines[i]).Length == 0) continue;
                            double baseline = plan.First.UY + i * plan.Leading;
                            var on = chars.Where(c => c.Size > 0 && c.Text is not null && area.Contains(c.Box.Center.X, c.Box.Center.Y)
                                                      && Math.Abs(c.UY - baseline) < 0.3 * plan.First.Size).ToList();
                            if (!Spells(on, lines[i], plan.First.Size) && !Spells(on.OrderBy(c => c.UX).ToList(), lines[i], plan.First.Size))
                                throw new PageAssemblyException($"line {i + 1} of the new paragraph did not read back (got “{string.Concat(on.Select(c => c.Text))}”); the copy was not kept",
                                                                "Err_EditCheck", string.Concat(on.Select(c => c.Text)));
                        }
                });
        }
        finally { fpdfview.FPDF_CloseDocument(pdfium); }
    }
}
