// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PDFiumCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>Where a match was found.</summary>
public enum RedactSource { Text, Field, Comment }

/// <summary>A match on a page: its text, a little of the text around it, and its boxes (upright top-down points,
/// one per line it spans).</summary>
public sealed record RedactMatch(int Page, string Text, string Context, IReadOnlyList<PdfRect> Boxes, RedactSource Source);

/// <summary>An area to redact (0-based page, upright top-down points). <see cref="Cover"/>: remove what is under it and
/// draw a black box; otherwise only the annotation there goes (a comment that mentions a match).</summary>
public sealed record RedactArea(int Page, PdfRect Rect, bool Cover = true);

/// <param name="Glyphs">Characters removed from the page text.</param>
/// <param name="Images">Images whose pixels under the areas were blanked (a copy of each), or dropped.</param>
/// <param name="Paths">Drawings that lay entirely inside an area.</param>
/// <param name="Annotations">Comments, links and form fields removed.</param>
/// <param name="Strings">Pieces of text outside the pages masked with ■ (document properties, bookmarks, tags).</param>
/// <param name="Boxes">The black boxes drawn (they can be larger than the areas: whole image pixels, a run that had to go whole).</param>
public sealed record RedactReport(int Areas, int Pages, int Glyphs, int Images, int Paths, int Annotations, int Strings,
                                  IReadOnlyList<RedactArea> Boxes)
{
    public int Removed => Glyphs + Images + Paths + Annotations + Strings;
}

/// <summary>
/// Redaction: finds words and kinds of data, then removes for real what lies under each area and covers it with a black
/// box. Text is taken out glyph by glyph (the rest of the line stays where it was), pixels of images under the areas
/// are blanked, drawings inside them dropped, and comments and form fields there removed. The same words are masked
/// in document properties, bookmarks and tags. Before the copy is kept it is checked: the words are searched for again,
/// no character is left under a box, and a render of every page shows no change outside the boxes.
/// <para>Callers hold <see cref="PdfiumDocument.Gate"/>.</para>
/// </summary>
public static class PdfRedactor
{
    private const int MaxRounds = 4;

    // ------------------------------------------------------------------ finding

    public static IReadOnlyList<RedactMatch> Find(string input, RedactSearch search)
    {
        if (search.IsEmpty) return [];
        var matcher = new Matcher(search);
        var list = new List<RedactMatch>();
        PdfiumDocument.EnsureInit();
        var doc = PdfEraser.Load(input);
        try
        {
            int pages = fpdfview.FPDF_GetPageCount(doc);
            for (int p = 0; p < pages; p++)
                PdfEraser.WithPage(doc, p, (page, map) => list.AddRange(FindOnPage(page, map, p, matcher)));
        }
        finally { fpdfview.FPDF_CloseDocument(doc); }
        list.AddRange(FindInAnnotations(input, matcher));
        return [.. list.OrderBy(m => m.Page).ThenBy(m => m.Boxes.Count > 0 ? m.Boxes[0].Y0 : 0).ThenBy(m => m.Boxes.Count > 0 ? m.Boxes[0].X0 : 0)];
    }

    /// <summary>The areas that redact <paramref name="matches"/>.</summary>
    public static IReadOnlyList<RedactArea> Areas(IEnumerable<RedactMatch> matches) =>
        [.. matches.SelectMany(m => m.Boxes.Select(b => new RedactArea(m.Page, b, m.Source != RedactSource.Comment)))];

    private sealed record PageChar(string? Text, PdfRect Box);

    private static List<PageChar> Chars(FpdfPageT page, PdfiumDocument.PageMapper map)
    {
        var list = new List<PageChar>();
        var tp = fpdf_text.FPDFTextLoadPage(page);
        try
        {
            int n = fpdf_text.FPDFTextCountChars(tp);
            var box = new FS_RECTF_();
            for (int i = 0; i < n; i++)
            {
                uint u = fpdf_text.FPDFTextGetUnicode(tp, i);
                string? s = u is 0 or 0xFFFE or 0xFFFF or > 0x10FFFF || u is >= 0xD800 and <= 0xDFFF ? null : char.ConvertFromUtf32((int)u);
                if (s is not null && (string.IsNullOrWhiteSpace(s) || char.IsControl(s, 0))) s = null;
                bool generated = fpdf_text.FPDFTextIsGenerated(tp, i) == 1;
                PdfRect r = default;
                if (s is not null && !generated && fpdf_text.FPDFTextGetLooseCharBox(tp, i, box) != 0)
                    r = map.ToDevice(box.Left, box.Top, box.Right, box.Bottom);
                list.Add(new PageChar(generated || r.Width <= 0 && r.Height <= 0 ? null : s, r));
            }
        }
        finally { fpdf_text.FPDFTextClosePage(tp); }
        return list;
    }

    internal static IEnumerable<RedactMatch> FindOnPage(FpdfPageT page, PdfiumDocument.PageMapper map, int pageIndex, Matcher matcher)
    {
        var chars = Chars(page, map);
        foreach (var hit in matcher.Find([.. chars.Select(c => c.Text)]))
        {
            var boxes = Lines(hit.Select(i => chars[i].Box));
            var text = string.Concat(hit.Select(i => chars[i].Text));
            int from = Math.Max(0, hit[0] - 16), to = Math.Min(chars.Count - 1, hit[^1] + 16);
            var context = new StringBuilder();
            for (int i = from; i <= to; i++)
                if (chars[i].Text is { } t) context.Append(t);
                else if (context.Length > 0 && context[^1] != ' ') context.Append(' ');
            yield return new RedactMatch(pageIndex, text, context.ToString().Trim(), boxes, RedactSource.Text);
        }
    }

    /// <summary>Character boxes joined into one box per line (or column, for vertical text), slightly padded.</summary>
    private static List<PdfRect> Lines(IEnumerable<PdfRect> chars)
    {
        var boxes = new List<PdfRect>();
        foreach (var c in chars)
        {
            if (boxes.Count > 0 && SameLine(boxes[^1], c))
            {
                var g = boxes[^1];
                boxes[^1] = new PdfRect(Math.Min(g.X0, c.X0), Math.Min(g.Y0, c.Y0), Math.Max(g.X1, c.X1), Math.Max(g.Y1, c.Y1));
            }
            else boxes.Add(c);
        }
        return [.. boxes.Select(b => new PdfRect(b.X0 - 0.3, b.Y0 - 0.3, b.X1 + 0.3, b.Y1 + 0.3))];

        static bool SameLine(PdfRect g, PdfRect c)
        {
            double v = Math.Min(g.Y1, c.Y1) - Math.Max(g.Y0, c.Y0), h = Math.Min(g.X1, c.X1) - Math.Max(g.X0, c.X0);
            double size = Math.Max(c.Width, c.Height);
            bool across = v > 0.5 * Math.Min(g.Height, c.Height) && c.X0 - g.X1 < 1.5 * size && g.X0 - c.X1 < 1.5 * size;
            bool down = h > 0.5 * Math.Min(g.Width, c.Width) && c.Y0 - g.Y1 < 1.5 * size && g.Y0 - c.Y1 < 1.5 * size;
            return across || down;
        }
    }

    /// <summary>Form fields whose value matches, and comments whose text does.</summary>
    private static List<RedactMatch> FindInAnnotations(string input, Matcher matcher)
    {
        var list = new List<RedactMatch>();
        using var d = Open(input, PdfDocumentOpenMode.Import);
        for (int p = 0; p < d.PageCount; p++)
        {
            var page = d.Pages[p];
            var toUpright = Upright(page).Inverse() ?? Mx.Identity;
            if (Deref(page.Elements["/Annots"]) is not PdfArray annots) continue;
            foreach (var item in annots.Elements)
            {
                if (Deref(item) is not PdfDictionary a || RectOf(a) is not { } rect) continue;
                var subtype = a.Elements.GetName("/Subtype");
                var box = ToUpright(toUpright, rect);
                if (subtype == "/Widget")
                {
                    var value = FieldValue(a);
                    if (value.Length > 0 && matcher.Matches(value))
                        list.Add(new RedactMatch(p, value, value, [box], RedactSource.Field));
                }
                else if (subtype is not ("/Popup" or "/Link"))
                {
                    var text = TextOf(a.Elements["/Contents"]) + " " + StripTags(TextOf(a.Elements["/RC"]));
                    if (matcher.Matches(text))
                        list.Add(new RedactMatch(p, text.Trim(), text.Trim(), [box], RedactSource.Comment));
                }
            }
        }
        return list;
    }

    private static string FieldValue(PdfDictionary widget)
    {
        for (var f = widget; f is not null; f = Deref(f.Elements["/Parent"]) as PdfDictionary)
            switch (Deref(f.Elements["/V"]))
            {
                case PdfString s: return s.Value;
                case PdfArray a: return string.Join(" ", a.Elements.Select(e => (Deref(e) as PdfString)?.Value ?? ""));
                case null: continue;
                default: return "";
            }
        return "";
    }

    private static string TextOf(PdfItem? i) => Deref(i) switch
    {
        PdfString s => s.Value,
        PdfDictionary { Stream: not null } d => Encoding.UTF8.GetString(d.Stream.UnfilteredValue),
        _ => "",
    };

    private static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<[^>]*>", " ");

    // ------------------------------------------------------------------ redacting

    /// <summary>
    /// Writes <paramref name="output"/> (a new file) with everything under <paramref name="areas"/> removed and covered.
    /// <paramref name="search"/> (the words the areas came from) is also masked outside the pages and searched for again
    /// in the result.
    /// </summary>
    /// <exception cref="RedactException">Nothing could be redacted safely; no file is left behind.</exception>
    public static RedactReport Redact(string input, string output, IReadOnlyList<RedactArea> areas, RedactSearch? search = null)
    {
        if (areas.Count == 0) throw new RedactException("nothing to redact", "Err_RedactNothing");
        if (File.Exists(output)) throw new RedactException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        var matcher = search is null || search.IsEmpty ? null : new Matcher(search);
        PdfiumDocument.EnsureInit();
        int pageCount;
        using (var probe = Open(input, PdfDocumentOpenMode.Import)) pageCount = probe.PageCount;
        foreach (var a in areas)
            if (a.Page < 0 || a.Page >= pageCount)
                throw new RedactException($"page {a.Page + 1} does not exist (the PDF has {pageCount})", "Err_NoPage", a.Page + 1, pageCount);

        var work = areas.ToList();
        for (int round = 1; ; round++)
        {
            try
            {
                var pass = Pass(input, output, work, matcher);
                var (grow, problem) = Verify(input, output, pass, matcher);
                if (problem is not null) throw problem;
                if (grow.Count == 0)
                    return new RedactReport(areas.Count, pass.Boxes.Select(b => b.Page).Distinct().Count(), pass.Stats.Glyphs,
                                            pass.Stats.Images + pass.Stats.ImagesRemoved, pass.Stats.Paths, pass.Annotations, pass.Strings, pass.Boxes);
                if (round >= MaxRounds)
                    throw new RedactException("the redacted copy kept changing; it was not kept", "Err_RedactUnstable");
                work.AddRange(grow);
                File.Delete(output);
            }
            catch
            {
                if (File.Exists(output)) File.Delete(output);
                throw;
            }
        }
    }

    private sealed record PassResult(RedactStats Stats, int Annotations, int Strings, List<RedactArea> Boxes, List<RedactArea> Allowed);

    private static PassResult Pass(string input, string output, IReadOnlyList<RedactArea> areas, Matcher? matcher)
    {
        var stats = new RedactStats();
        var boxes = new List<RedactArea>();
        var allowed = new List<RedactArea>();
        int annotations = 0, strings = 0;
        using (var d = Open(input, PdfDocumentOpenMode.Modify))
        {
            for (int p = 0; p < d.PageCount; p++)
            {
                var mine = areas.Where(a => a.Page == p).ToList();
                if (mine.Count == 0 && matcher is null) continue;
                var page = d.Pages[p];
                var up = Upright(page);
                var down = up.Inverse() ?? Mx.Identity;
                var cover = mine.Where(a => a.Cover).ToList();
                var ctx = new RedactContext(d, [.. cover.Select(a => up.Bounds(new Box(a.Rect.X0, a.Rect.Y0, a.Rect.X1, a.Rect.Y1)))], matcher is null ? null : matcher.Matches);
                var res = new ResourceScope(d, PdfEraser.Resources(page), r => page.Elements["/Resources"] = r);
                var content = PdfEraser.ReadContents(page);
                var bytes = new ContentRedactor(ctx).Run(content, res, Mx.Identity) ?? content;
                res.DropUnused(bytes);
                stats.Glyphs += ctx.Stats.Glyphs; stats.Paths += ctx.Stats.Paths; stats.Images += ctx.Stats.Images;
                stats.ImagesRemoved += ctx.Stats.ImagesRemoved; stats.Tags += ctx.Stats.Tags; stats.Runs += ctx.Stats.Runs;

                var drawn = cover.Select(a => a.Rect).Concat(ctx.Stats.Covered.Select(b => ToUpright(down, b))).ToList();
                boxes.AddRange(drawn.Select(r => new RedactArea(p, r)));
                allowed.AddRange(drawn.Select(r => new RedactArea(p, r)));
                annotations += RemoveAnnotations(d, page, mine, down, allowed, p);

                if (drawn.Count > 0)
                {
                    // the page's own content between q and Q, then the boxes in upright coordinates
                    var sb = new StringBuilder("\nQ\nq ").Append(UprightOps(up)).Append(" cm 0 0 0 rg\n");
                    foreach (var r in drawn)
                        sb.Append(F(r.X0)).Append(' ').Append(F(r.Y0)).Append(' ').Append(F(r.Width)).Append(' ').Append(F(r.Height)).Append(" re\n");
                    sb.Append("f\nQ\n");
                    bytes = [.. "q\n"u8.ToArray(), .. bytes, .. Encoding.ASCII.GetBytes(sb.ToString())];
                }
                if (!ReferenceEquals(bytes, content)) PdfEraser.WriteContents(d, page, bytes);
            }
            if (matcher is not null) strings = ScrubStrings(d, matcher);
            d.Save(output);
        }
        return new PassResult(stats, annotations, strings, boxes, allowed);
    }

    /// <summary>
    /// Removes the annotations under the areas (and their pop-ups and replies); a form field goes with its widget.
    /// Areas that do not cover (a comment that matched) remove only the annotation they were made from.
    /// </summary>
    private static int RemoveAnnotations(PdfDocument d, PdfPage page, List<RedactArea> areas, Mx down, List<RedactArea> allowed, int p)
    {
        if (areas.Count == 0 || Deref(page.Elements["/Annots"]) is not PdfArray annots) return 0;
        var gone = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var item in annots.Elements)
        {
            if (Deref(item) is not PdfDictionary a || RectOf(a) is not { } rect) continue;
            if (a.Elements.GetName("/Subtype") == "/Popup") continue;
            var r = ToUpright(down, rect);
            bool hit = areas.Any(x => x.Cover ? x.Rect.Intersect(r).Area > 0.5 : x.Rect.IoU(r) > 0.9);
            if (hit) gone.Add(a);
        }
        if (gone.Count == 0) return 0;
        // replies to a removed comment, and pop-ups of removed ones
        bool more = true;
        while (more)
        {
            more = false;
            foreach (var item in annots.Elements)
                if (Deref(item) is PdfDictionary a && !gone.Contains(a)
                    && (Deref(a.Elements["/IRT"]) is PdfDictionary irt && gone.Contains(irt) || Deref(a.Elements["/Parent"]) is PdfDictionary par && gone.Contains(par)))
                { gone.Add(a); more = true; }
        }
        for (int i = annots.Elements.Count - 1; i >= 0; i--)
            if (Deref(annots.Elements[i]) is PdfDictionary a && gone.Contains(a))
            {
                annots.Elements.RemoveAt(i);
                if (RectOf(a) is { } rect) allowed.Add(new RedactArea(p, ToUpright(down, rect)));
                if (a.Elements.GetName("/Subtype") == "/Widget") RemoveField(d, a);
            }
        return gone.Count(a => a.Elements.GetName("/Subtype") != "/Popup");
    }

    /// <summary>Takes a widget out of the form: out of its field's kids, and the field out of the form when it has none left.</summary>
    private static void RemoveField(PdfDocument d, PdfDictionary widget)
    {
        var form = Deref(d.Internals.Catalog.Elements["/AcroForm"]) as PdfDictionary;
        var node = widget;
        while (node is not null)
        {
            var parent = Deref(node.Elements["/Parent"]) as PdfDictionary;
            var list = parent is null ? Deref(form?.Elements["/Fields"]) as PdfArray : Deref(parent.Elements["/Kids"]) as PdfArray;
            if (list is not null)
                for (int i = list.Elements.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(Deref(list.Elements[i]), node)) list.Elements.RemoveAt(i);
            if (parent is null || Deref(parent.Elements["/Kids"]) is PdfArray { Elements.Count: > 0 }) break;
            node = parent;   // the field has no widgets left: it goes too
        }
    }

    /// <summary>Masks matches in every text string of the document outside the page content.</summary>
    private static int ScrubStrings(PdfDocument d, Matcher matcher)
    {
        int count = 0;
        var seen = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
        var objects = d.Internals.GetAllObjects().Cast<PdfItem>().Prepend(d.Internals.Catalog);
        objects = objects.Prepend(d.Info);
        foreach (var o in objects) Walk(o, null, 0);
        // the XMP copy of the properties (PDFsharp writes a new one from them)
        foreach (var o in d.Internals.GetAllObjects().OfType<PdfDictionary>().Prepend(d.Internals.Catalog).ToList())
            if (Deref(o.Elements["/Metadata"]) is PdfDictionary { Stream: not null } x && matcher.Matches(Xmp(x.Stream.UnfilteredValue)))
            {
                o.Elements.Remove("/Metadata");
                count++;
            }
        return count;

        void Walk(PdfItem item, PdfDictionary? owner, int depth)
        {
            if (depth > 32 || !seen.Add(item)) return;
            switch (item)
            {
                case PdfDictionary dict:
                    if (dict.Elements.GetName("/Type") == "/Sig") return;   // signature values are binary
                    foreach (var key in dict.Elements.Keys.ToList())
                    {
                        if (Machine(key)) continue;
                        var v = dict.Elements[key];
                        if (v is PdfString s && Masked(s) is { } m) { dict.Elements[key] = m; count++; }
                        else if (v is PdfArray or PdfDictionary) Walk(v, dict, depth + 1);
                    }
                    break;
                case PdfArray arr:
                    for (int i = 0; i < arr.Elements.Count; i++)
                    {
                        var v = arr.Elements[i];
                        if (v is PdfString s && Masked(s) is { } m) { arr.Elements[i] = m; count++; }
                        else if (v is PdfArray or PdfDictionary) Walk(v, owner, depth + 1);
                    }
                    break;
            }
        }

        PdfString? Masked(PdfString s)
        {
            var text = s.Value;
            if (text.Length == 0 || IsDate(text) || !matcher.Matches(text)) return null;
            return new PdfString(matcher.Mask(text), PdfStringEncoding.Unicode);
        }
    }

    // ------------------------------------------------------------------ checking

    /// <summary>
    /// The proof: no match is found again where a box is (else its boxes are returned, to redact in another round; matches
    /// elsewhere are text the user kept), no character is left under a box, and nothing changed outside the boxes except right next to a box on its own line (what a run
    /// removed whole leaves: returned too, to cover).
    /// </summary>
    private static (List<RedactArea> Grow, RedactException? Problem) Verify(string before, string after, PassResult pass, Matcher? matcher)
    {
        var grow = new List<RedactArea>();
        var boxes = pass.Boxes;
        // 1. characters under the boxes, and matches found again
        var doc = PdfEraser.Load(after);
        int leftover = 0;
        try
        {
            int pages = fpdfview.FPDF_GetPageCount(doc);
            for (int p = 0; p < pages; p++)
            {
                var mine = boxes.Where(b => b.Page == p).Select(b => b.Rect).ToList();
                if (mine.Count == 0 && matcher is null) continue;
                PdfEraser.WithPage(doc, p, (page, map) =>
                {
                    var chars = Chars(page, map);
                    foreach (var c in chars)
                        if (c.Text is not null && mine.Any(b => Shrink(b).Contains(c.Box.Center.X, c.Box.Center.Y) && b.Intersect(c.Box).Area > c.Box.Area * 0.5))
                            leftover++;
                    // a match found again where a box is: part of it was left (redact it too); elsewhere it is
                    // text the user chose to keep
                    if (matcher is not null)
                        foreach (var m in FindOnPage(page, map, p, matcher))
                            if (m.Boxes.Any(b => mine.Any(x => x.Intersect(b).Area > 0)))
                                grow.AddRange(m.Boxes.Select(b => new RedactArea(p, b)));
                });
            }
        }
        finally { fpdfview.FPDF_CloseDocument(doc); }
        if (leftover > 0)
            return ([], new RedactException($"{leftover} characters were still found under the black boxes; the copy was not kept", "Err_RedactLeftover", leftover));
        if (matcher is not null)
        {
            var again = FindInAnnotations(after, matcher)
                .Where(m => m.Boxes.Any(b => boxes.Concat(pass.Allowed).Any(x => x.Page == m.Page && x.Rect.Intersect(b).Area > 0))).ToList();
            if (again.Count > 0)
                return ([], new RedactException($"{again.Count} matches were still found in fields or comments; the copy was not kept", "Err_RedactLeftover", again.Count));
            var (strings, where) = StringsLeft(after, matcher);
            if (strings > 0)
                return ([], new RedactException($"{strings} matches were still found in the document's text strings ({where}); the copy was not kept", "Err_RedactLeftover", strings));
        }

        // 2. renders of both versions: nothing may change outside the boxes and removed annotations
        using var a = PdfiumDocument.Open(before);
        using var b = PdfiumDocument.Open(after);
        foreach (var p in pass.Allowed.Select(x => x.Page).Distinct())
        {
            var ia = a.RenderPage(p, 1.0);
            var ib = b.RenderPage(p, 1.0);
            if (ia.Width != ib.Width || ia.Height != ib.Height)
                return ([], new RedactException("a page changed size; the copy was not kept", "Err_RedactChanged", ia.Width * ia.Height));
            var ok = new bool[ia.Width * ia.Height];
            foreach (var r in pass.Allowed.Where(x => x.Page == p).Select(x => x.Rect))
            {
                int x0 = Math.Max(0, (int)Math.Floor(r.X0) - 1), x1 = Math.Min(ia.Width - 1, (int)Math.Ceiling(r.X1) + 1);
                int y0 = Math.Max(0, (int)Math.Floor(r.Y0) - 1), y1 = Math.Min(ia.Height - 1, (int)Math.Ceiling(r.Y1) + 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++) ok[y * ia.Width + x] = true;
            }
            var changed = new bool[ok.Length];
            int count = 0;
            for (int i = 0, q = 0; i < ok.Length; i++, q += 3)
                if (!ok[i] && Math.Abs(ia.Pixels[q] - ib.Pixels[q]) + Math.Abs(ia.Pixels[q + 1] - ib.Pixels[q + 1]) + Math.Abs(ia.Pixels[q + 2] - ib.Pixels[q + 2]) > 24)
                { changed[i] = true; count++; }
            if (count == 0) continue;
            var mine = boxes.Where(x => x.Page == p).Select(x => x.Rect).ToList();
            foreach (var cluster in Clusters(changed, ia.Width, ia.Height))
            {
                // what a run removed whole leaves: on the line of a box (or its column, for vertical text)
                bool onLine = mine.Any(m =>
                    Math.Min(m.Y1, cluster.Y1) - Math.Max(m.Y0, cluster.Y0) > 0.5 * Math.Min(cluster.Height, m.Height) ||
                    Math.Min(m.X1, cluster.X1) - Math.Max(m.X0, cluster.X0) > 0.5 * Math.Min(cluster.Width, m.Width));
                if (!onLine)
                    return ([], new RedactException($"the redacted copy differs from the original outside the black boxes ({count} pixels); it was not kept", "Err_RedactChanged", count));
                grow.Add(new RedactArea(p, new PdfRect(cluster.X0 - 1, cluster.Y0 - 1, cluster.X1 + 1, cluster.Y1 + 1)));
            }
        }
        return (grow, null);

        static PdfRect Shrink(PdfRect r) => new(r.X0 + 0.75, r.Y0 + 0.75, r.X1 - 0.75, r.Y1 - 0.75);
    }

    /// <summary>Bounding boxes of groups of changed pixels (8-connected, with a 3-pixel reach so a glyph is one group).</summary>
    private static List<PdfRect> Clusters(bool[] changed, int w, int h)
    {
        var list = new List<PdfRect>();
        var seen = new bool[changed.Length];
        var queue = new Queue<int>();
        for (int i = 0; i < changed.Length; i++)
        {
            if (!changed[i] || seen[i]) continue;
            int x0 = w, y0 = h, x1 = 0, y1 = 0;
            seen[i] = true;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue(), cx = c % w, cy = c / w;
                x0 = Math.Min(x0, cx); x1 = Math.Max(x1, cx); y0 = Math.Min(y0, cy); y1 = Math.Max(y1, cy);
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int n = ny * w + nx;
                        if (changed[n] && !seen[n]) { seen[n] = true; queue.Enqueue(n); }
                    }
            }
            list.Add(new PdfRect(x0, y0, x1 + 1, y1 + 1));
        }
        return list;
    }

    private static (int Count, string? Where) StringsLeft(string path, Matcher matcher)
    {
        using var d = Open(path, PdfDocumentOpenMode.Import);
        int n = 0;
        string? where = null;
        foreach (var o in d.Internals.GetAllObjects().OfType<PdfDictionary>())
        {
            if (o.Elements.GetName("/Type") == "/Sig") continue;
            foreach (var key in o.Elements.Keys)
                if (!Machine(key) && Deref(o.Elements[key]) is PdfString s && !IsDate(s.Value) && matcher.Matches(s.Value)) { n++; where ??= key; }
            if (o.Elements.GetName("/Subtype") == "/XML" && o.Stream is not null && matcher.Matches(Xmp(o.Stream.UnfilteredValue))) { n++; where ??= "XMP metadata"; }
        }
        return (n, where);
    }

    /// <summary>Keys whose strings are written by programs, not people: binary keys, lengths, dates.</summary>
    private static bool Machine(string key) =>
        key is "/ID" or "/O" or "/U" or "/OE" or "/UE" or "/Perms" or "/Length" or "/CreationDate" or "/ModDate" or "/M";

    /// <summary>A PDF date (D:20261122…): a pattern for long numbers must not take it for one.</summary>
    private static bool IsDate(string s) => s.Length > 3 && s.StartsWith("D:", StringComparison.Ordinal) && char.IsDigit(s[2]);

    /// <summary>XMP text without what programs write into it (dates, document IDs), which patterns could match.</summary>
    private static string Xmp(byte[] xml) =>
        System.Text.RegularExpressions.Regex.Replace(Encoding.UTF8.GetString(xml),
            @"uuid:[0-9A-Fa-f\-]+|\d{4}-\d{2}-\d{2}T[0-9:.+\-Z]+|<\?xpacket[^>]*>|x:xmptk=""[^""]*""", " ");

    // ------------------------------------------------------------------ geometry and plumbing

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        if (mode == PdfDocumentOpenMode.Modify) return PdfSecurity.OpenForChanges(path, PdfPasswords.For(path));
        try { return PdfReader.Open(path, PdfPasswords.For(path) ?? "", mode); }
        catch (PdfReaderException e) when (PdfSecurity.IsPasswordProblem(e))
        {
            throw new RedactException($"{Path.GetFileName(path)} is protected with a password", "Err_NeedsPassword", Path.GetFileName(path));
        }
    }

    /// <summary>Upright top-down page points to PDF space (the mapping PDFium uses for the upright page, inverted).</summary>
    internal static Mx Upright(PdfPage page)
    {
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        double a = box.X1, b = box.Y1, c = box.X2, d = box.Y2;
        return (((page.Rotate % 360) + 360) % 360) switch
        {
            90 => new(0, 1, 1, 0, a, b),
            180 => new(-1, 0, 0, 1, c, b),
            270 => new(0, -1, -1, 0, c, d),
            _ => new(1, 0, 0, -1, a, d),
        };
    }

    private static string UprightOps(Mx m) => $"{F(m.A)} {F(m.B)} {F(m.C)} {F(m.D)} {F(m.E)} {F(m.F)}";

    private static PdfRect ToUpright(Mx down, Box b)
    {
        var r = down.Bounds(b);
        return new PdfRect(r.X0, r.Y0, r.X1, r.Y1);
    }

    private static Box? RectOf(PdfDictionary annot)
    {
        if (Deref(annot.Elements["/Rect"]) is not PdfArray a || a.Elements.Count < 4) return null;
        double x0 = FontMetrics.Num(a.Elements[0]), y0 = FontMetrics.Num(a.Elements[1]), x1 = FontMetrics.Num(a.Elements[2]), y1 = FontMetrics.Num(a.Elements[3]);
        return new Box(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    private static string F(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
