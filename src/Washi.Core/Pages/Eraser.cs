// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using PDFiumCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

public sealed class EraseException(string message) : Exception(message);

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
public enum EraseKeptWhy { RunsOutside, LargeImage, Grouped, Colored, Chained }

/// <summary>Something inside a box that was left alone, and why (<see cref="Reason"/> in English, for agents).</summary>
public sealed record EraseKept(int Page, EraseKind Kind, PdfRect Rect, EraseKeptWhy Why)
{
    public string Reason => Why switch
    {
        EraseKeptWhy.RunsOutside => "a line of text that runs outside the box: widen the box to erase it",
        EraseKeptWhy.LargeImage => "a large image (a scan?) covers this area; its pixels are not changed",
        EraseKeptWhy.Grouped => "grouped content (a form XObject) is not erased",
        EraseKeptWhy.Colored => "coloured text (the form's own printing) is kept; turn off 'keep coloured printing' to erase it",
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

    /// <summary>Writes <paramref name="output"/> (a new file) with the content under <paramref name="regions"/> removed.</summary>
    /// <exception cref="EraseException">Nothing could be erased safely; no file is left behind.</exception>
    public static EraseReport Erase(string input, string output, IReadOnlyList<EraseRegion> regions, EraseOptions? options = null)
    {
        options ??= EraseOptions.Default;
        if (regions.Count == 0) throw new EraseException("no area to erase");
        if (File.Exists(output)) throw new EraseException($"{Path.GetFileName(output)} already exists; Washi never overwrites files");
        CheckPermissions(input);
        PdfiumDocument.EnsureInit();

        // 1. PDFium decides what is inside the boxes (bounds on the upright page, ink colour)
        var perPage = new Dictionary<int, (List<Candidate> Found, List<int> Types)>();
        var doc = Load(input);
        try
        {
            int pages = fpdfview.FPDF_GetPageCount(doc);
            foreach (var r in regions)
                if (r.Page < 0 || r.Page >= pages) throw new EraseException($"page {r.Page + 1} does not exist (the PDF has {pages})");
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
        int dropped;
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
                    throw new EraseException($"page {pageIndex + 1}: its drawing commands could not be matched one to one; nothing was changed");
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
                if (edits.Count > 0) WriteContents(d, page, Apply(content.Bytes, edits));
            }
            if (removed.Count == 0)
                throw new EraseException(kept.Count > 0
                    ? "nothing could be erased: " + string.Join("; ", kept.Select(k => k.Reason).Distinct())
                    : "nothing to erase inside the box (with the current options)");
            DropUnusedXObjects(d);
            d.Save(output);
            using var check = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            dropped = Math.Max(0, before - CountImages(check));
        }

        // 3. proof: no text left where it was erased, and nothing else on the page moved
        try
        {
            var (remaining, changed) = Verify(input, output, regions, removed);
            if (changed > 0)
                throw new EraseException($"the erased copy differs from the original outside the erased areas ({changed} pixels); it was not kept");
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
                var box = boxes.FirstOrDefault(x => Overlap(rect, Grow(x)) > 0);
                if (box == default) continue;
                double share = Share(rect, Grow(box));

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

    private static FpdfDocumentT Load(string path)
    {
        var doc = fpdfview.FPDF_LoadDocument(path, "");
        if (doc is null || doc.__Instance == IntPtr.Zero)
            throw new EraseException($"could not open {Path.GetFileName(path)} (PDFium error {fpdfview.FPDF_GetLastError()})");
        return doc;
    }

    private static void WithPage(FpdfDocumentT doc, int index, Action<FpdfPageT, PdfiumDocument.PageMapper> action)
    {
        var page = fpdfview.FPDF_LoadPage(doc, index);
        if (page is null || page.__Instance == IntPtr.Zero) throw new EraseException($"page {index + 1} could not be read");
        try { action(page, new PdfiumDocument.PageMapper(page, fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page))); }
        finally { fpdfview.FPDF_ClosePage(page); }
    }

    // ------------------------------------------------------------------ content streams (PDFsharp)

    /// <summary>The page's content streams, decoded and joined (stream boundaries count as white space).</summary>
    private static byte[] ReadContents(PdfPage page)
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
    private static void WriteContents(PdfDocument d, PdfPage page, byte[] bytes)
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

    private static PdfDictionary? Resources(PdfPage page)
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
        bool path = false;
        for (int k = 0; k < content.Ops.Count; k++)
        {
            var op = content.Ops[k];
            switch (op.Name)
            {
                case "m" or "l" or "c" or "v" or "y" or "h" or "re": path = true; break;
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                    if (path) list.Add((k, 2));
                    path = false;
                    break;
                case "n": path = false; break;
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

    /// <summary>Removes XObject entries that no page content uses any more (the erased images).</summary>
    private static void DropUnusedXObjects(PdfDocument d)
    {
        var used = new Dictionary<PdfDictionary, HashSet<string>>(ReferenceEqualityComparer.Instance);
        foreach (var page in d.Pages)
        {
            if (Resources(page) is not { } res || Deref(res.Elements["/XObject"]) is not PdfDictionary xobjects) continue;
            if (!used.TryGetValue(xobjects, out var names)) used[xobjects] = names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var op in new ContentStream(ReadContents(page)).Ops)
                if (op.Name == "Do" && op.Names.Count > 0) names.Add("/" + op.Names[^1]);
        }
        foreach (var (xobjects, names) in used)
            foreach (var key in xobjects.Elements.Keys.ToList())
                if (!names.Contains(key)) xobjects.Elements.Remove(key);
    }

    private static int CountImages(PdfDocument d) =>
        d.Internals.GetAllObjects().OfType<PdfDictionary>().Count(o => o.Elements.GetName("/Subtype") == "/Image");

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;

    // ------------------------------------------------------------------ checks

    private static void CheckPermissions(string input)
    {
        try
        {
            using var d = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            if (!d.SecuritySettings.PermitModifyDocument)
                throw new EraseException($"{Path.GetFileName(input)} does not allow changes (its permissions forbid them); Washi does not remove protection");
        }
        catch (PdfReaderException e) when (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new EraseException($"{Path.GetFileName(input)} is protected with a password; Washi does not remove protection");
        }
    }

    /// <summary>Text left inside the boxes, and pixels that changed outside them (both should be 0).</summary>
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
            // characters still sitting where erased text was
            remaining += b.ReadChars(group.Key).Count(c => erased.Any(x => x.Contains(c.Rect.Center.X, c.Rect.Center.Y)));
            const double scale = 1.0;
            var ia = a.RenderPage(group.Key, scale);
            var ib = b.RenderPage(group.Key, scale);
            if (ia.Width != ib.Width || ia.Height != ib.Height) { changed += (long)ia.Width * ia.Height; continue; }
            for (int y = 0; y < ia.Height; y++)
                for (int x = 0; x < ia.Width; x++)
                {
                    // one pixel of slack around each box for anti-aliasing at its edge
                    if (boxes.Any(r => x >= r.X0 * scale - 1 && x <= r.X1 * scale + 1 && y >= r.Y0 * scale - 1 && y <= r.Y1 * scale + 1)) continue;
                    int p = (y * ia.Width + x) * 3;
                    if (Math.Abs(ia.Pixels[p] - ib.Pixels[p]) + Math.Abs(ia.Pixels[p + 1] - ib.Pixels[p + 1]) + Math.Abs(ia.Pixels[p + 2] - ib.Pixels[p + 2]) > 24)
                        changed++;
                }
        }
        return (remaining, changed);
    }
}
