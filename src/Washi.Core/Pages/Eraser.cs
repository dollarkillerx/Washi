// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using PDFiumCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

public sealed class EraseException(string message, string? key = null, params object[] args) : Exception(message), Washi.Core.Localization.ILocalizedError
{
    public string? Key { get; } = key;
    public object[] Args { get; } = args;
}

/// <summary>A box to erase: 0-based page, upright top-down points (the same space as fields).</summary>
public sealed record EraseRegion(int Page, PdfRect Rect);

/// <param name="Text">Text objects.</param>
/// <param name="Images">Image objects (scanned stamps, text drawn as small images).</param>
/// <param name="Drawings">Vector drawings (strokes, marks such as a circled 有/無).</param>
/// <param name="KeepColored">Only erase dark, neutral ink (black and grey). Official forms print their template in
/// colour and the filled-in data in black, so this keeps the form itself. Turn it off for black-printed forms.</param>
/// <param name="Rules">Also erase thin long lines (table rules, comb dividers). Off by default: they are usually the form.</param>
public sealed record EraseOptions(bool Text = true, bool Images = true, bool Drawings = true, bool KeepColored = true, bool Rules = false)
{
    public static EraseOptions Default { get; } = new();
}

public enum EraseKind { Text, Image, Drawing }

/// <summary>One page object that will be (or was) removed, in upright page points.</summary>
public sealed record EraseTarget(int Page, EraseKind Kind, PdfRect Rect, string? Text);

/// <summary>Why something inside a box was left alone.</summary>
public enum EraseKeptWhy { RunsOutside, LargeImage, Grouped, Colored, Chained, Clips }

/// <summary>Something inside a box that was left alone, and why (<see cref="Reason"/> in English, for agents).</summary>
public sealed record EraseKept(int Page, EraseKind Kind, PdfRect Rect, EraseKeptWhy Why)
{
    public string Reason => Why switch
    {
        EraseKeptWhy.RunsOutside => "a line of text that runs outside the box: widen the box to erase it",
        EraseKeptWhy.LargeImage => "a large image (a scan?) covers this area; its pixels are not changed",
        EraseKeptWhy.Grouped => "grouped content (a form XObject) is not erased",
        EraseKeptWhy.Colored => "coloured text (the form's own printing) is kept; turn off 'keep coloured printing' to erase it",
        EraseKeptWhy.Clips => "text that other content is clipped to: erasing it would change what shows through",
        _ => "a text run joined to the next one on its line: widen the box to erase both",
    };
}

/// <param name="RemainingChars">Characters still found inside the boxes after erasing (text extraction of the output).</param>
/// <param name="ChangedPixelsOutside">Pixels that changed outside the boxes when both versions are rendered (should be 0).</param>
/// <param name="ImagesDropped">Image streams that no longer exist in the file (not just unreferenced).</param>
public sealed record EraseReport(
    int TextRemoved, int ImagesRemoved, int DrawingsRemoved, IReadOnlyList<EraseKept> Kept,
    int RemainingChars, long ChangedPixelsOutside, int ImagesDropped)
{
    public int Removed => TextRemoved + ImagesRemoved + DrawingsRemoved;
}

/// <summary>
/// Removes printed content under boxes for real. PDFium decides which page objects lie inside a box (bounds on the
/// upright page, ink colour); each one is matched to the operator that draws it, and only those operators are cut out
/// of the page's content stream; every other byte is copied unchanged. Image streams nothing uses any more are dropped,
/// and PDFsharp saves only reachable objects, so the old content stream is gone too. Nothing is covered with white.
/// <para>Rules: an object goes when at least <see cref="InsideShare"/> of it lies inside a box (text and images) or
/// all of it does (drawings). Coloured ink and thin long rules stay unless the options say otherwise. A text run
/// that only partly lies in a box, or that the next run on its line is chained to, is kept and reported.</para>
/// <para>Every result is checked (text extraction and a render of both versions) before it is kept.
/// Not thread-safe: callers hold <see cref="PdfiumDocument.Gate"/>.</para>
/// </summary>
public static class PdfEraser
{
    /// <summary>Share of an object's area that must be inside a box (text and images).</summary>
    public const double InsideShare = 0.6;
    private const double Tolerance = 1.0;   // points, around each box

    /// <summary>What <see cref="Erase"/> would remove, without writing anything (the editor highlights it).</summary>
    public static (IReadOnlyList<EraseTarget> Targets, IReadOnlyList<EraseKept> Kept) Plan(
        string input, IReadOnlyList<EraseRegion> regions, EraseOptions? options = null)
    {
        options ??= EraseOptions.Default;
        PdfiumDocument.EnsureInit();
        var doc = Load(input);
        try
        {
            var targets = new List<EraseTarget>();
            var kept = new List<EraseKept>();
            foreach (var group in regions.GroupBy(r => r.Page))
                WithPage(doc, group.Key, (page, map) =>
                {
                    foreach (var c in Classify(doc, page, map, group.Key, [.. group.Select(g => g.Rect)], options))
                    {
                        if (c.Target is { } t) targets.Add(t);
                        if (c.Kept is { } k) kept.Add(k);
                    }
                });
            return (targets, kept);
        }
        finally { fpdfview.FPDF_CloseDocument(doc); }
    }

    /// <summary>
    /// Per page (0-based): whether its drawing commands can be matched one to one with the objects PDFium sees, which
    /// is what erasing needs. Pages that do not match are refused by <see cref="Erase"/> (nothing is changed).
    /// </summary>
    public static IReadOnlyList<(int Page, bool Matches, int Objects, int Operators)> Check(string input)
    {
        PdfiumDocument.EnsureInit();
        var types = new List<List<int>>();
        var doc = Load(input);
        try
        {
            int pages = fpdfview.FPDF_GetPageCount(doc);
            for (int i = 0; i < pages; i++)
                WithPage(doc, i, (page, _) => types.Add([.. Enumerable.Range(0, fpdf_edit.FPDFPageCountObjects(page))
                    .Select(k => fpdf_edit.FPDFPageObjGetType(fpdf_edit.FPDFPageGetObject(page, k)))]));
        }
        finally { fpdfview.FPDF_CloseDocument(doc); }
        using var d = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var result = new List<(int, bool, int, int)>();
        for (int i = 0; i < types.Count && i < d.PageCount; i++)
        {
            var painters = Painters(new ContentStream(ReadContents(d.Pages[i])), Resources(d.Pages[i]));
            result.Add((i, painters.Select(p => p.Type).SequenceEqual(types[i]), types[i].Count, painters.Count));
        }
        return result;
    }

    /// <summary>Writes <paramref name="output"/> (a new file) with the content under <paramref name="regions"/> removed.</summary>
    /// <exception cref="EraseException">Nothing could be erased safely; no file is left behind.</exception>
    public static EraseReport Erase(string input, string output, IReadOnlyList<EraseRegion> regions, EraseOptions? options = null)
    {
        options ??= EraseOptions.Default;
        if (regions.Count == 0) throw new EraseException("no area to erase", "Err_EraseNoArea");
        if (File.Exists(output)) throw new EraseException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        CheckPermissions(input);
        PdfiumDocument.EnsureInit();

        // 1. PDFium decides what is inside the boxes (bounds on the upright page, ink colour)
        var perPage = new Dictionary<int, (List<Candidate> Found, List<int> Types)>();
        var doc = Load(input);
        try
        {
            int pages = fpdfview.FPDF_GetPageCount(doc);
            foreach (var r in regions)
                if (r.Page < 0 || r.Page >= pages) throw new EraseException($"page {r.Page + 1} does not exist (the PDF has {pages})", "Err_NoPage", r.Page + 1, pages);
            foreach (var group in regions.GroupBy(r => r.Page))
                WithPage(doc, group.Key, (page, map) =>
                {
                    var types = Enumerable.Range(0, fpdf_edit.FPDFPageCountObjects(page))
                        .Select(i => fpdf_edit.FPDFPageObjGetType(fpdf_edit.FPDFPageGetObject(page, i))).ToList();
                    perPage[group.Key] = (Classify(doc, page, map, group.Key, [.. group.Select(g => g.Rect)], options), types);
                });
        }
        finally { fpdfview.FPDF_CloseDocument(doc); }

        // 2. the matching operators are cut out of the page's content stream; every other byte stays as it was
        var kept = new List<EraseKept>();
        var removed = new List<EraseTarget>();
        var cutXObjects = new List<(PdfPage Page, string Name)>();
        int dropped;
        try
        {
        using (var d = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            int before = CountImages(d);
            foreach (var (pageIndex, (found, types)) in perPage.OrderBy(p => p.Key))
            {
                kept.AddRange(found.Select(c => c.Kept).OfType<EraseKept>());
                var wanted = found.Where(c => c.Target is not null).ToDictionary(c => c.Index);
                if (wanted.Count == 0) continue;
                var page = d.Pages[pageIndex];
                var content = new ContentStream(ReadContents(page));
                var painters = Painters(content, Resources(page));
                if (!painters.Select(p => p.Type).SequenceEqual(types))
                    throw new EraseException($"page {pageIndex + 1}: its drawing commands could not be matched one to one; nothing was changed", "Err_EraseUnmatched", pageIndex + 1);
                var edits = new List<(int Start, int End, string With)>();
                var cut = new HashSet<int>();   // operator indexes removed
                foreach (var (opIndex, _, objIndex) in painters.Select((p, i) => (p.Op, p.Type, i)))
                    if (wanted.ContainsKey(objIndex)) cut.Add(opIndex);
                // a text run whose successor in the same line is placed right after it cannot go alone
                foreach (var opIndex in cut.Where(o => Chained(content, o, cut)).ToList()) cut.Remove(opIndex);
                foreach (var (p, objIndex) in painters.Select((p, i) => (p, i)))
                {
                    if (!wanted.TryGetValue(objIndex, out var c)) continue;
                    if (!cut.Contains(p.Op))
                    {
                        kept.Add(new EraseKept(pageIndex, c.Target!.Kind, c.Target.Rect, EraseKeptWhy.Chained));
                        continue;
                    }
                    var op = content.Ops[p.Op];
                    edits.Add(op.Name switch
                    {
                        "'" => (op.Start, op.End, "T*"),
                        "\"" when op.Numbers.Count >= 2 => (op.Start, op.End, $"{op.Numbers[0]} Tw {op.Numbers[1]} Tc T*"),
                        "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" => (op.Start, op.End, "n"),   // keeps any clip
                        _ => (op.Start, op.End, ""),
                    });
                    removed.Add(c.Target!);
                }
                foreach (var e in edits)
                {
                    var op = content.Ops.First(o => o.Start == e.Start);
                    if (op.Name == "Do" && op.Names.Count > 0) cutXObjects.Add((page, "/" + op.Names[^1]));
                }
                if (edits.Count > 0) WriteContents(d, page, Apply(content.Bytes, edits));
            }
            if (removed.Count == 0)
                throw kept.Count > 0
                    ? new EraseException("nothing could be erased: " + string.Join("; ", kept.Select(k => k.Reason).Distinct()), "Err_EraseOnlyKept",
                        string.Join("; ", kept.Select(k => Washi.Core.Localization.L.Get("Erase_Kept" + k.Why)).Distinct()))
                    : new EraseException("nothing to erase inside the box (with the current options)", "Err_EraseNothing");
            DropUnusedXObjects(d, cutXObjects);
            d.Save(output);
            using var check = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            dropped = Math.Max(0, before - CountImages(check));
        }

            // 3. proof: no text left where it was erased, and nothing else on the page moved
            var (remaining, changed) = Verify(input, output, regions, removed);
            if (changed > 0)
                throw new EraseException($"the erased copy differs from the original outside the erased areas ({changed} pixels); it was not kept", "Err_EraseChanged", changed);
            return new EraseReport(removed.Count(t => t.Kind == EraseKind.Text), removed.Count(t => t.Kind == EraseKind.Image),
                removed.Count(t => t.Kind == EraseKind.Drawing), kept, remaining, changed, dropped);
        }
        catch
        {
            File.Delete(output);
            throw;
        }
    }

    // ------------------------------------------------------------------ choosing objects

    private sealed record Candidate(int Index, EraseTarget? Target, EraseKept? Kept);

    private static List<Candidate> Classify(FpdfDocumentT doc, FpdfPageT page, PdfiumDocument.PageMapper map, int pageIndex,
                                            IReadOnlyList<PdfRect> boxes, EraseOptions o)
    {
        var list = new List<Candidate>();
        FpdfTextpageT? textPage = null;
        try
        {
            int n = fpdf_edit.FPDFPageCountObjects(page);
            for (int i = 0; i < n; i++)
            {
                var obj = fpdf_edit.FPDFPageGetObject(page, i);
                var kind = fpdf_edit.FPDFPageObjGetType(obj) switch
                {
                    1 => EraseKind.Text, 2 => EraseKind.Drawing, 3 => EraseKind.Image, 5 => (EraseKind?)EraseKind.Drawing, _ => null,
                };
                if (kind is not { } k) continue;
                bool isForm = fpdf_edit.FPDFPageObjGetType(obj) == 5;
                float l = 0, b = 0, r = 0, t = 0;
                if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0) continue;
                var rect = map.ToDevice(l, t, r, b);
                // the box that holds most of it (cells of a form touch: the first box it grazes is not the right one)
                double share = 0;
                PdfRect box = default;
                foreach (var x in boxes)
                {
                    double sh = Share(rect, Grow(x));
                    if (sh > share) { share = sh; box = x; }
                }
                if (share <= 0) continue;

                if (k == EraseKind.Text && !o.Text || k == EraseKind.Image && !o.Images || k == EraseKind.Drawing && !o.Drawings) continue;

                bool inside = k == EraseKind.Drawing ? share >= 0.999 : share >= InsideShare;
                if (!inside)
                {
                    // only worth mentioning when it visibly reaches into the box
                    if (k == EraseKind.Text && share > 0.15)
                        list.Add(new(i, null, new(pageIndex, k, rect, EraseKeptWhy.RunsOutside)));
                    else if (k == EraseKind.Image && rect.Area > Grow(box).Area * 4 && share > 0)
                        list.Add(new(i, null, new(pageIndex, k, Clip(rect, box), EraseKeptWhy.LargeImage)));
                    continue;
                }
                if (isForm)
                {
                    list.Add(new(i, null, new(pageIndex, k, rect, EraseKeptWhy.Grouped)));
                    continue;
                }
                if (!o.Rules && k != EraseKind.Text && IsRule(rect)) continue;   // the form's own lines
                if (o.KeepColored && !IsDarkNeutral(Ink(doc, page, obj, k)))
                {
                    if (k == EraseKind.Text)
                        list.Add(new(i, null, new(pageIndex, k, rect, EraseKeptWhy.Colored)));
                    continue;
                }
                string? s = null;
                if (k == EraseKind.Text && (int)fpdf_edit.FPDFTextObjGetTextRenderMode(obj) >= 4)
                {
                    // render modes 4-7 add the glyphs to the clipping path: later content shows only through them
                    list.Add(new(i, null, new(pageIndex, k, rect, EraseKeptWhy.Clips)));
                    continue;
                }
                if (k == EraseKind.Text)
                {
                    textPage ??= fpdf_text.FPDFTextLoadPage(page);
                    s = TextOf(obj, textPage);
                }
                list.Add(new(i, new EraseTarget(pageIndex, k, rect, s), null));
            }
        }
        finally
        {
            if (textPage is not null) fpdf_text.FPDFTextClosePage(textPage);
        }
        return list;
    }

    private static PdfRect Grow(PdfRect r) => new(r.X0 - Tolerance, r.Y0 - Tolerance, r.X1 + Tolerance, r.Y1 + Tolerance);

    private static PdfRect Clip(PdfRect r, PdfRect box) => r.Intersect(box) is var i && i.Area > 0 ? i : box;

    private static double Overlap(PdfRect a, PdfRect b) => a.Intersect(b) is var i && i.Width > 0 && i.Height > 0 ? i.Area : 0;

    /// <summary>Share of <paramref name="r"/> inside <paramref name="box"/>; zero-area objects (a line) count by length.</summary>
    private static double Share(PdfRect r, PdfRect box)
    {
        if (r.Width <= 0.01 || r.Height <= 0.01)
        {
            var c = r.Center;
            return box.Contains(r.X0, r.Y0) && box.Contains(r.X1, r.Y1) ? 1 : box.Contains(c.X, c.Y) ? 0.5 : 0;
        }
        return Overlap(r, box) / r.Area;
    }

    /// <summary>A table rule or comb divider: thinner than 1.2 pt and at least 8 pt long.</summary>
    private static bool IsRule(PdfRect r) => Math.Min(r.Width, r.Height) < 1.2 && Math.Max(r.Width, r.Height) >= 8;

    /// <summary>Black or grey ink (not the coloured printing of a form). Null ink (blank image) counts as dark.</summary>
    internal static bool IsDarkNeutral((int R, int G, int B)? ink)
    {
        if (ink is not var (r, g, b)) return true;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return max - min <= 48 && 0.299 * r + 0.587 * g + 0.114 * b < 150;
    }

    /// <summary>The colour an object draws with: fill for text, stroke (or fill) for paths, the dark pixels of an image.</summary>
    private static (int R, int G, int B)? Ink(FpdfDocumentT doc, FpdfPageT page, FpdfPageobjectT obj, EraseKind kind)
    {
        uint r = 0, g = 0, b = 0, a = 0;
        if (kind == EraseKind.Text)
            return fpdf_edit.FPDFPageObjGetFillColor(obj, ref r, ref g, ref b, ref a) != 0 ? ((int)r, (int)g, (int)b) : null;
        if (kind == EraseKind.Drawing)
        {
            int fill = 0, stroke = 0;
            fpdf_edit.FPDFPathGetDrawMode(obj, ref fill, ref stroke);
            bool ok = stroke != 0 ? fpdf_edit.FPDFPageObjGetStrokeColor(obj, ref r, ref g, ref b, ref a) != 0
                                  : fpdf_edit.FPDFPageObjGetFillColor(obj, ref r, ref g, ref b, ref a) != 0;
            return ok ? ((int)r, (int)g, (int)b) : null;
        }
        var bmp = fpdf_edit.FPDFImageObjGetRenderedBitmap(doc, page, obj);
        if (bmp is null || bmp.__Instance == IntPtr.Zero) return null;
        try
        {
            int w = fpdfview.FPDFBitmapGetWidth(bmp), h = fpdfview.FPDFBitmapGetHeight(bmp), stride = fpdfview.FPDFBitmapGetStride(bmp);
            int format = fpdfview.FPDFBitmapGetFormat(bmp);   // 1 gray, 2 BGR, 3 BGRx, 4 BGRA
            int bpp = format switch { 1 => 1, 2 => 3, _ => 4 };
            if (w <= 0 || h <= 0 || (long)w * h > 4_000_000) return null;
            var buf = new byte[stride * h];
            Marshal.Copy(fpdfview.FPDFBitmapGetBuffer(bmp), buf, 0, buf.Length);
            long sr = 0, sg = 0, sb = 0, n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = y * stride + x * bpp;
                    int bb = buf[p], gg = bpp > 1 ? buf[p + 1] : bb, rr = bpp > 1 ? buf[p + 2] : bb;
                    if (format == 4 && buf[p + 3] < 128) continue;     // transparent
                    if (rr + gg + bb > 600) continue;                  // paper
                    sr += rr; sg += gg; sb += bb; n++;
                }
            return n == 0 ? null : ((int)(sr / n), (int)(sg / n), (int)(sb / n));
        }
        finally { fpdfview.FPDFBitmapDestroy(bmp); }
    }

    private static string TextOf(FpdfPageobjectT obj, FpdfTextpageT textPage)
    {
        ushort probe = 0;
        var len = fpdf_edit.FPDFTextObjGetText(obj, textPage, ref probe, 0);
        if (len <= 2) return "";
        var buf = new ushort[len];
        fpdf_edit.FPDFTextObjGetText(obj, textPage, ref buf[0], len);
        return new string([.. buf.TakeWhile(c => c != 0).Select(c => (char)c)]);
    }

    // ------------------------------------------------------------------ PDFium plumbing

    internal static FpdfDocumentT Load(string path)
    {
        var doc = fpdfview.FPDF_LoadDocument(path, PdfPasswords.For(path) ?? "");
        if (doc is null || doc.__Instance == IntPtr.Zero)
            throw new EraseException($"could not open {Path.GetFileName(path)} (PDFium error {fpdfview.FPDF_GetLastError()})", "Err_CannotOpen", Path.GetFileName(path));
        return doc;
    }

    internal static void WithPage(FpdfDocumentT doc, int index, Action<FpdfPageT, PdfiumDocument.PageMapper> action)
    {
        var page = fpdfview.FPDF_LoadPage(doc, index);
        if (page is null || page.__Instance == IntPtr.Zero) throw new EraseException($"page {index + 1} could not be read", "Err_PageUnreadable", index + 1);
        try { action(page, new PdfiumDocument.PageMapper(page, fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page))); }
        finally { fpdfview.FPDF_ClosePage(page); }
    }

    // ------------------------------------------------------------------ content streams (PDFsharp)

    /// <summary>The page's content streams, decoded and joined (stream boundaries count as white space).</summary>
    internal static byte[] ReadContents(PdfPage page)
    {
        var parts = new List<byte[]>();
        switch (Deref(page.Elements["/Contents"]))
        {
            case PdfDictionary single when single.Stream is not null: parts.Add(single.Stream.UnfilteredValue); break;
            case PdfArray array:
                foreach (var item in array.Elements)
                    if (Deref(item) is PdfDictionary s && s.Stream is not null) parts.Add(s.Stream.UnfilteredValue);
                break;
        }
        using var ms = new MemoryStream();
        foreach (var p in parts) { ms.Write(p); ms.WriteByte((byte)'\n'); }
        return ms.ToArray();
    }

    /// <summary>One new compressed content stream for the page; the old ones become unreachable and are not saved.</summary>
    internal static void WriteContents(PdfDocument d, PdfPage page, byte[] bytes)
    {
        var stream = new PdfDictionary(d);
        stream.CreateStream(new PdfSharp.Pdf.Filters.FlateDecode().Encode(bytes, PdfFlateEncodeMode.Default));
        stream.Elements["/Filter"] = new PdfName("/FlateDecode");
        d.Internals.AddObject(stream);
        page.Elements["/Contents"] = stream.Reference;
    }

    private static byte[] Apply(byte[] source, List<(int Start, int End, string With)> edits)
    {
        using var ms = new MemoryStream(source.Length);
        int at = 0;
        foreach (var (start, end, with) in edits.OrderBy(e => e.Start))
        {
            ms.Write(source, at, start - at);
            if (with.Length > 0) ms.Write(System.Text.Encoding.ASCII.GetBytes(" " + with + " "));
            at = end;
        }
        ms.Write(source, at, source.Length - at);
        return ms.ToArray();
    }

    internal static PdfDictionary? Resources(PdfPage page)
    {
        PdfDictionary? node = page;
        while (node is not null)
        {
            if (Deref(node.Elements["/Resources"]) is PdfDictionary res) return res;
            node = Deref(node.Elements["/Parent"]) as PdfDictionary;   // inherited from the page tree
        }
        return null;
    }

    /// <summary>
    /// The operators that make page objects, in PDFium's order: path painting (when a path was built), text showing
    /// (with a non-empty string), XObjects (image 3, form 5), inline images (3) and shadings (4).
    /// </summary>
    private static List<(int Op, int Type)> Painters(ContentStream content, PdfDictionary? resources)
    {
        var xobjects = resources is null ? null : Deref(resources.Elements["/XObject"]) as PdfDictionary;
        var list = new List<(int, int)>();
        int points = 0;            // points of the path being built
        bool lastMove = false;     // a moveto right after a moveto replaces it
        for (int k = 0; k < content.Ops.Count; k++)
        {
            var op = content.Ops[k];
            switch (op.Name)
            {
                case "m": if (!lastMove) points++; lastMove = true; break;
                case "l": points++; lastMove = false; break;
                case "c" or "v" or "y": points += 3; lastMove = false; break;
                case "re": points += 4; lastMove = false; break;
                case "h": Close(); break;
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                    if (op.Name is "s" or "b" or "b*") Close();   // these close the path first
                    if (points >= 2) list.Add((k, 2));   // a lone point paints nothing: PDFium makes no object
                    points = 0; lastMove = false;
                    break;
                case "n": points = 0; lastMove = false; break;
                case "Tj" or "TJ" or "'" or "\"":
                    if (op.Strings.Any(x => x.Length > 0)) list.Add((k, 1));
                    break;
                case "Do" when op.Names.Count > 0:
                    var x = xobjects is null ? null : Deref(xobjects.Elements["/" + op.Names[^1]]) as PdfDictionary;
                    var subtype = x?.Elements.GetName("/Subtype");
                    if (subtype == "/Image") list.Add((k, 3));
                    else if (subtype == "/Form") list.Add((k, 5));
                    break;
                case "BI": list.Add((k, 3)); break;
                case "sh": list.Add((k, 4)); break;
            }
        }
        return list;

        // closing a path that is only a moveto gives it a second point (seen on 厚労省 forms: "0 0 m s" is an object)
        void Close() { if (points == 1) points = 2; lastMove = false; }
    }

    /// <summary>
    /// True when removing the text-showing operator <paramref name="op"/> would move later text: another kept run
    /// follows in the same text object without being placed anew (it starts where this one ends).
    /// </summary>
    private static bool Chained(ContentStream content, int op, HashSet<int> cut)
    {
        if (content.Ops[op].Name is not ("Tj" or "TJ" or "'" or "\"")) return false;
        for (int k = op + 1; k < content.Ops.Count; k++)
        {
            switch (content.Ops[k].Name)
            {
                case "Td" or "TD" or "Tm" or "T*" or "'" or "\"" or "ET" or "BT": return false;
                case "Tj" or "TJ":
                    if (content.Ops[k].Strings.All(x => x.Length == 0)) continue;   // moves nothing visible
                    if (cut.Contains(k)) continue;
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Removes the XObject entries of erased images when no page sharing that dictionary draws them any more. Only
    /// names this run cut are touched: other entries may be used in ways a content scan cannot see.
    /// </summary>
    private static void DropUnusedXObjects(PdfDocument d, IReadOnlyList<(PdfPage Page, string Name)> cut)
    {
        var candidates = new Dictionary<PdfDictionary, HashSet<string>>(ReferenceEqualityComparer.Instance);
        foreach (var (page, name) in cut)
        {
            if (Resources(page) is not { } res || Deref(res.Elements["/XObject"]) is not PdfDictionary xobjects) continue;
            if (!candidates.TryGetValue(xobjects, out var names)) candidates[xobjects] = names = new HashSet<string>(StringComparer.Ordinal);
            names.Add(name);
        }
        if (candidates.Count == 0) return;
        foreach (var page in d.Pages)
        {
            if (Resources(page) is not { } res || Deref(res.Elements["/XObject"]) is not PdfDictionary xobjects) continue;
            if (!candidates.TryGetValue(xobjects, out var names) || names.Count == 0) continue;
            foreach (var op in new ContentStream(ReadContents(page)).Ops)
                if (op.Name == "Do" && op.Names.Count > 0) names.Remove("/" + op.Names[^1]);   // still drawn somewhere
        }
        foreach (var (xobjects, names) in candidates)
            foreach (var name in names) xobjects.Elements.Remove(name);
    }

    private static int CountImages(PdfDocument d) =>
        d.Internals.GetAllObjects().OfType<PdfDictionary>().Count(o => o.Elements.GetName("/Subtype") == "/Image");

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;

    // ------------------------------------------------------------------ checks

    internal static void CheckPermissions(string input)
    {
        try
        {
            using var d = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            if (!d.SecuritySettings.PermitModifyDocument)
                throw new EraseException($"{Path.GetFileName(input)} does not allow changes (its permissions forbid them); Washi does not remove protection", "Err_ProtectedChanges", Path.GetFileName(input));
        }
        catch (PdfReaderException e) when (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new EraseException($"{Path.GetFileName(input)} is protected with a password; Washi does not remove protection", "Err_Password", Path.GetFileName(input));
        }
    }

    /// <summary>
    /// Characters of text objects that still sit exactly where an erased text object was (same box). Text of the
    /// form that merely overlaps an erased spot (a kept label under the data) does not count.
    /// </summary>
    private static int StillThere(string output, int pageIndex, IReadOnlyList<EraseTarget> erasedText)
    {
        if (erasedText.Count == 0) return 0;
        int count = 0;
        var doc = Load(output);
        try
        {
            WithPage(doc, pageIndex, (page, map) =>
            {
                var textPage = fpdf_text.FPDFTextLoadPage(page);
                try
                {
                    int n = fpdf_edit.FPDFPageCountObjects(page);
                    for (int i = 0; i < n; i++)
                    {
                        var obj = fpdf_edit.FPDFPageGetObject(page, i);
                        if (fpdf_edit.FPDFPageObjGetType(obj) != 1) continue;
                        float l = 0, b = 0, r = 0, t = 0;
                        if (fpdf_edit.FPDFPageObjGetBounds(obj, ref l, ref b, ref r, ref t) == 0) continue;
                        var rect = map.ToDevice(l, t, r, b);
                        if (erasedText.Any(e => e.Rect.IoU(rect) > 0.95)) count += Math.Max(1, TextOf(obj, textPage).Length);
                    }
                }
                finally { fpdf_text.FPDFTextClosePage(textPage); }
            });
        }
        finally { fpdfview.FPDF_CloseDocument(doc); }
        return count;
    }

    /// <summary>Text left where it was erased, and pixels that changed outside the boxes (both should be 0).</summary>
    private static (int Remaining, long ChangedOutside) Verify(string before, string after, IReadOnlyList<EraseRegion> regions,
                                                               IReadOnlyList<EraseTarget> removed)
    {
        int remaining = 0;
        long changed = 0;
        using var a = PdfiumDocument.Open(before);
        using var b = PdfiumDocument.Open(after);
        foreach (var group in regions.GroupBy(r => r.Page))
        {
            var erased = removed.Where(t => t.Page == group.Key).Select(t => Grow(t.Rect)).ToList();
            var boxes = group.Select(g => Grow(g.Rect)).Concat(erased).ToList();
            remaining += StillThere(after, group.Key, [.. removed.Where(t => t.Page == group.Key && t.Kind == EraseKind.Text)]);
            const double scale = 1.0;
            var ia = a.RenderPage(group.Key, scale);
            var ib = b.RenderPage(group.Key, scale);
            if (ia.Width != ib.Width || ia.Height != ib.Height) { changed += (long)ia.Width * ia.Height; continue; }
            // where change is expected: the boxes and the erased objects, with one pixel of slack for anti-aliasing
            var allowed = new bool[ia.Width * ia.Height];
            foreach (var r in boxes)
            {
                int x0 = Math.Max(0, (int)Math.Floor(r.X0 * scale) - 1), x1 = Math.Min(ia.Width - 1, (int)Math.Ceiling(r.X1 * scale) + 1);
                int y0 = Math.Max(0, (int)Math.Floor(r.Y0 * scale) - 1), y1 = Math.Min(ia.Height - 1, (int)Math.Ceiling(r.Y1 * scale) + 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++) allowed[y * ia.Width + x] = true;
            }
            for (int i = 0, p = 0; i < allowed.Length; i++, p += 3)
            {
                if (allowed[i]) continue;
                if (Math.Abs(ia.Pixels[p] - ib.Pixels[p]) + Math.Abs(ia.Pixels[p + 1] - ib.Pixels[p + 1]) + Math.Abs(ia.Pixels[p + 2] - ib.Pixels[p + 2]) > 24)
                    changed++;
            }
        }
        return (remaining, changed);
    }
}
