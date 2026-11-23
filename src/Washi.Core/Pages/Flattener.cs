// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Washi.Core.Forms;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>What flattening did: form fields and comments drawn into their pages, on how many pages.</summary>
public sealed record FlattenResult(int Fields, int Comments, int Pages, IReadOnlyList<string> Notes);

/// <summary>
/// Flattening: form fields and comments become part of the page. Each one's own appearance (what a viewer shows for it)
/// is drawn into the page content where it was, and the field or comment is taken away, so the page looks the same
/// everywhere and can no longer be changed in a form or comment tool. Links stay. The copy is rendered next to the
/// original: nothing may move or change.
/// </summary>
public static partial class PdfFlattener
{
    private const int FlagHidden = 2, FlagNoView = 32;

    public static FlattenResult Flatten(string input, string output, bool fields = true, bool comments = true)
    {
        if (!fields && !comments) throw new PageAssemblyException("nothing to flatten: choose form fields, comments or both", "Err_FlattenNothing");
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        var tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $".washi-flatten-{Guid.NewGuid():N}.pdf");
        try
        {
            FlattenResult result;
            List<(int Page, PdfRect Area, string Text, bool Ink)> redrawn;
            using (var doc = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
            {
                (result, redrawn) = Flatten(doc, fields, comments);
                if (result.Fields + result.Comments == 0 && result.Notes.Count == 0)
                    throw new PageAssemblyException("there are no form fields or comments to flatten", "Err_FlattenNone");
                doc.Options.CompressContentStreams = true;
                doc.Save(tmp);
            }
            Verify(input, tmp, fields, comments, redrawn);
            File.Move(tmp, output);
            return result;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    /// <summary>Flattens the document in memory. Also gives the areas whose appearance Washi drew anew, and their text.</summary>
    internal static (FlattenResult Result, List<(int Page, PdfRect Area, string Text, bool Ink)> Redrawn) Flatten(PdfDocument doc, bool fields, bool comments)
    {
        var notes = new List<string>();
        var catalog = doc.Internals.Catalog;
        var form = Deref(catalog.Elements["/AcroForm"]) as PdfDictionary;
        var terminals = form is null ? [] : AcroFormFields.Terminals(doc).ToList();
        if (terminals.Any(t => AcroFormFields.Inherited(t.Field, "/FT") is PdfName { Value: "/Sig" } && AcroFormFields.Inherited(t.Field, "/V") is not null))
            throw new PageAssemblyException("the file is signed: flattening would break the signatures", "Err_FlattenSigned");

        var redrawn = new List<(int, PdfRect, string, bool)>();
        var regenerated = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var widgetPage = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int p = 0; p < doc.PageCount; p++)
            if (Deref(doc.Pages[p].Elements["/Annots"]) is PdfArray annots)
                foreach (var a in annots.Elements.Select(Deref).OfType<PdfDictionary>())
                    widgetPage.TryAdd(a, p);

        // fields whose look the viewer works out (NeedAppearances, or no appearance of their own) are drawn by Washi first
        if (fields && form is not null)
        {
            bool need = form.Elements.GetBoolean("/NeedAppearances");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var redraw = new List<(string Name, List<PdfDictionary> Widgets, string Value)>();
            foreach (var (field, name, widgets) in terminals)
            {
                var ft = AcroFormFields.Inherited(field, "/FT") is PdfName n ? n.Value : "";
                if (ft is not ("/Tx" or "/Ch")) continue;
                var value = AcroFormFields.Inherited(field, "/V") switch
                {
                    PdfString s => s.Value,
                    PdfStringObject so => so.Value,
                    _ => null,
                };
                bool missing = widgets.Any(w => Deref(w.Elements["/AP"]) is not PdfDictionary);
                if (string.IsNullOrEmpty(value) || !need && !missing) continue;
                values[name] = value;
                redraw.Add((name, widgets, value));
            }
            if (values.Count > 0)
            {
                // a read-only field is filled too: here its value only gets drawn
                var readOnly = terminals.Where(t => values.ContainsKey(t.Name) && AcroFormFields.Inherited(t.Field, "/Ff") is PdfInteger { Value: var ff } && (ff & 1) != 0)
                                        .Select(t => t.Field).ToList();
                foreach (var f in readOnly) f.Elements.SetInteger("/Ff", f.Elements.GetInteger("/Ff") & ~1);
                AcroFormFields.Fill(doc, values);
                var cantDraw = redraw.Where(r => r.Widgets.Any(w => Deref(w.Elements["/AP"]) is not PdfDictionary)).Select(r => r.Name).ToList();
                if (cantDraw.Count > 0)
                    throw new PageAssemblyException($"Washi cannot draw these fields in their fonts: {string.Join(", ", cantDraw)}", "Err_FlattenCannotDraw", string.Join(", ", cantDraw));
                foreach (var (name, widgets, value) in redraw)
                    foreach (var w in widgets)
                        if (widgetPage.TryGetValue(w, out var wp) && RectOf(w) is { } r)
                        {
                            redrawn.Add((wp, ToUpright(doc.Pages[wp], r), value, false));
                            regenerated.Add(w);
                        }
            }
        }

        int fieldCount = 0, commentCount = 0, pages = 0;
        var resources = form is null ? null : Deref(form.Elements["/DR"]) as PdfDictionary;
        for (int p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            if (Deref(page.Elements["/Annots"]) is not PdfArray annots) continue;
            var keep = new List<PdfItem>();
            var draw = new StringBuilder();
            var all = annots.Elements.ToList();
            var gone = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var item in all)
            {
                if (Deref(item) is not PdfDictionary a) continue;
                var subtype = a.Elements.GetName("/Subtype");
                bool isWidget = subtype == "/Widget";
                bool flatten = subtype switch
                {
                    "/Link" or "/Popup" => false,
                    "/Widget" => fields,
                    _ => comments,
                };
                if (!flatten) continue;
                gone.Add(a);
                if (isWidget) fieldCount++; else commentCount++;
                int flags = a.Elements.GetInteger("/F");
                if ((flags & (FlagHidden | FlagNoView)) != 0) continue;   // not shown: nothing to draw
                if (RectOf(a) is not { } rect) continue;
                if (Appearance(a) is not { } x || Placement(x, rect) is not { } m)
                {
                    // no look of its own (a box left empty, its state without an appearance): nothing is drawn, as Acrobat
                    // shows it; PDFium's form layer would make one up, so that area is not compared
                    if (isWidget) redrawn.Add((p, ToUpright(page, rect), "", false));
                    continue;
                }
                // PDFium's form layer draws a field's text its own way (other smoothing): there the ink is compared, not each pixel
                if (isWidget && !regenerated.Contains(a)) redrawn.Add((p, ToUpright(page, rect), "", true));
                if (isWidget && x.Elements["/Resources"] is null && resources is not null) x.Elements["/Resources"] = resources;
                var name = AddResource(doc, page, "/XObject", "WashiFlat", x);
                draw.Append("q ");
                if (!isWidget && a.Elements["/CA"] is { } ca && Num(ca) is var opacity && opacity < 1)
                {
                    var gs = new PdfDictionary(doc);
                    gs.Elements.SetName("/Type", "/ExtGState");
                    gs.Elements["/CA"] = new PdfReal(opacity);
                    gs.Elements["/ca"] = new PdfReal(opacity);
                    doc.Internals.AddObject(gs);
                    draw.Append(AddResource(doc, page, "/ExtGState", "WashiFlatGS", gs)).Append(" gs ");
                }
                draw.Append(string.Join(' ', m.Select(N))).Append(" cm ").Append(name).Append(" Do Q\n");
            }
            if (gone.Count == 0) continue;
            foreach (var item in all)
            {
                if (Deref(item) is not PdfDictionary a) { keep.Add(item); continue; }
                if (gone.Contains(a)) continue;
                // a popup goes with the comment it opens
                if (a.Elements.GetName("/Subtype") == "/Popup" && Deref(a.Elements["/Parent"]) is PdfDictionary parent && gone.Contains(parent)) continue;
                keep.Add(item);
            }
            if (keep.Count == 0) page.Elements.Remove("/Annots");
            else
            {
                var arr = new PdfArray(doc);
                foreach (var k in keep) arr.Elements.Add(k);
                page.Elements["/Annots"] = arr;
            }
            if (draw.Length > 0) { PdfStamper.AddStreams(doc, page, "", draw.ToString()); pages++; }
        }

        if (fields && form is not null)
        {
            if (Deref(form.Elements["/XFA"]) is not null) notes.Add("xfa");
            catalog.Elements.Remove("/AcroForm");
            // widgets left on pages (a field whose widget is not in any page's list) have nothing to belong to any more
        }
        return (new FlattenResult(fieldCount, commentCount, pages, notes), redrawn);
    }

    /// <summary>The appearance a viewer shows for the annotation now (/N, or its state /AS), as a form XObject.</summary>
    internal static PdfDictionary? Appearance(PdfDictionary a)
    {
        if (Deref(a.Elements["/AP"]) is not PdfDictionary ap) return null;
        var n = Deref(ap.Elements["/N"]);
        if (n is PdfDictionary { Stream: null } states)
        {
            var state = a.Elements.GetName("/AS");
            n = state.Length > 0 ? Deref(states.Elements[state]) : null;
        }
        if (n is not PdfDictionary { Stream: not null } x) return null;
        if (x.Elements.GetName("/Subtype") != "/Form") x.Elements.SetName("/Subtype", "/Form");
        if (x.Elements.GetName("/Type") != "/XObject") x.Elements.SetName("/Type", "/XObject");
        return x;
    }

    /// <summary>
    /// The matrix that puts the appearance in the annotation's rectangle (PDF 32000 12.5.5): its box, turned by its own
    /// /Matrix, is scaled and moved onto /Rect.
    /// </summary>
    internal static double[]? Placement(PdfDictionary x, (double X0, double Y0, double X1, double Y1) rect)
    {
        var bbox = Deref(x.Elements["/BBox"]) is PdfArray b && b.Elements.Count >= 4 ? b.Elements.Select(Num).ToArray() : null;
        if (bbox is null)
        {
            bbox = [0, 0, rect.X1 - rect.X0, rect.Y1 - rect.Y0];
            x.Elements["/BBox"] = new PdfArray(x.Owner, bbox.Select(v => (PdfItem)new PdfReal(v)).ToArray());
        }
        var mx = Deref(x.Elements["/Matrix"]) is PdfArray ma && ma.Elements.Count >= 6 ? ma.Elements.Select(Num).ToArray() : [1, 0, 0, 1, 0, 0];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (px, py) in new[] { (bbox[0], bbox[1]), (bbox[2], bbox[1]), (bbox[0], bbox[3]), (bbox[2], bbox[3]) })
        {
            double tx = mx[0] * px + mx[2] * py + mx[4], ty = mx[1] * px + mx[3] * py + mx[5];
            minX = Math.Min(minX, tx); maxX = Math.Max(maxX, tx); minY = Math.Min(minY, ty); maxY = Math.Max(maxY, ty);
        }
        double bw = maxX - minX, bh = maxY - minY, rw = rect.X1 - rect.X0, rh = rect.Y1 - rect.Y0;
        if (bw <= 1e-6 || bh <= 1e-6 || rw <= 1e-6 || rh <= 1e-6) return null;
        double sx = rw / bw, sy = rh / bh;
        return [sx, 0, 0, sy, rect.X0 - minX * sx, rect.Y0 - minY * sy];
    }

    internal static (double X0, double Y0, double X1, double Y1)? RectOf(PdfDictionary a)
    {
        if (Deref(a.Elements["/Rect"]) is not PdfArray r || r.Elements.Count < 4) return null;
        double x0 = Num(r.Elements[0]), y0 = Num(r.Elements[1]), x1 = Num(r.Elements[2]), y1 = Num(r.Elements[3]);
        return (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>Adds <paramref name="obj"/> to the page's resources under a new name (the page gets resources of its own).</summary>
    internal static string AddResource(PdfDocument doc, PdfPage page, string kind, string prefix, PdfDictionary obj)
    {
        if (Deref(page.Elements["/Resources"]) is not PdfDictionary res)
        {
            res = new PdfDictionary(doc);
            page.Elements["/Resources"] = res;
        }
        if (Deref(res.Elements[kind]) is not PdfDictionary dict)
        {
            dict = new PdfDictionary(doc);
            res.Elements[kind] = dict;
        }
        int i = 1;
        while (dict.Elements.ContainsKey($"/{prefix}{i}")) i++;
        if (obj.Reference is null) doc.Internals.AddObject(obj);
        dict.Elements.SetReference($"/{prefix}{i}", obj);
        return $"/{prefix}{i}";
    }

    /// <summary>A rectangle in PDF space as upright, top-down points (how PDFium's renders are laid out).</summary>
    private static PdfRect ToUpright(PdfPage page, (double X0, double Y0, double X1, double Y1) r) =>
        new PdfComments.Upright(page).ToUpright(new PdfRect(r.X0, r.Y0, r.X1, r.Y1));

    /// <summary>
    /// The copy, rendered, looks like the original (fields and comments included) except where Washi drew a field anew,
    /// whose value must then read back; and no field or comment that was flattened is left.
    /// </summary>
    private static void Verify(string before, string after, bool fields, bool comments, List<(int Page, PdfRect Area, string Text, bool Ink)> redrawn)
    {
        // the caller holds PDFium's lock (as for every Washi operation that renders)
        {
            using var a = PdfiumDocument.Open(before, PdfPasswords.For(before));
            using var b = PdfiumDocument.Open(after, PdfPasswords.For(before));
            if (a.PageCount != b.PageCount) throw new PageAssemblyException("the copy has another number of pages; it was not kept", "Err_FlattenChanged", 1, 0);
            for (int page = 0; page < a.PageCount; page++)
            {
                const double scale = 1.5;
                var ia = a.RenderPage(page, scale);
                var ib = b.RenderPage(page, scale);
                if (ia.Width != ib.Width || ia.Height != ib.Height)
                    throw new PageAssemblyException($"page {page + 1} changed size; the copy was not kept", "Err_FlattenChanged", page + 1, 0);
                var ok = new bool[ia.Width * ia.Height];
                foreach (var (_, r, _, ink) in redrawn.Where(r => r.Page == page))
                {
                    int x0 = Math.Max(0, (int)Math.Floor(r.X0 * scale) - 2), x1 = Math.Min(ia.Width - 1, (int)Math.Ceiling(r.X1 * scale) + 2);
                    int y0 = Math.Max(0, (int)Math.Floor(r.Y0 * scale) - 2), y1 = Math.Min(ia.Height - 1, (int)Math.Ceiling(r.Y1 * scale) + 2);
                    for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) ok[y * ia.Width + x] = true;
                    if (ink && Ink(ia, x0, y0, x1, y1) is var ka && Ink(ib, x0, y0, x1, y1) is var kb && !Near(ka, kb))
                        throw new PageAssemblyException($"a field on page {page + 1} looks different after flattening; the copy was not kept",
                                                        "Err_FlattenChanged", page + 1, 0);
                }
                int changed = 0;
                for (int i = 0, q = 0; i < ok.Length; i++, q += 3)
                    if (!ok[i] && Math.Abs(ia.Pixels[q] - ib.Pixels[q]) + Math.Abs(ia.Pixels[q + 1] - ib.Pixels[q + 1]) + Math.Abs(ia.Pixels[q + 2] - ib.Pixels[q + 2]) > 48)
                        changed++;
                // drawing an appearance in the page instead of over it may shift an edge's smoothing by a pixel
                if (changed > ia.Width * ia.Height / 2000)
                    throw new PageAssemblyException($"page {page + 1} looks different after flattening ({changed} pixels); the copy was not kept",
                                                    "Err_FlattenChanged", page + 1, changed);
                var text = Norm(b.GetPageText(page));
                foreach (var (_, _, value, _) in redrawn.Where(r => r.Page == page && r.Text.Length > 0))
                    if (!text.Contains(Norm(value), StringComparison.Ordinal))
                        throw new PageAssemblyException($"“{value}” did not read back on page {page + 1}; the copy was not kept", "Err_FlattenCheck", value, page + 1);
            }
        }

        using var d = PdfSharp.Pdf.IO.PdfReader.Open(after, PdfPasswords.For(before) ?? "", PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        if (fields && d.Internals.Catalog.Elements.ContainsKey("/AcroForm"))
            throw new PageAssemblyException("form fields are still there; the copy was not kept", "Err_FlattenCheck", "AcroForm", 0);
        for (int p = 0; p < d.PageCount; p++)
            if (Deref(d.Pages[p].Elements["/Annots"]) is PdfArray annots)
                foreach (var a in annots.Elements.Select(Deref).OfType<PdfDictionary>())
                {
                    var t = a.Elements.GetName("/Subtype");
                    if (fields && t == "/Widget" || comments && t is not ("/Widget" or "/Link" or "/Popup"))
                        throw new PageAssemblyException($"a {t.TrimStart('/')} is still on page {p + 1}; the copy was not kept", "Err_FlattenCheck", t.TrimStart('/'), p + 1);
                }
    }

    /// <summary>Where the dark pixels in the area are: (left, top, right, bottom), or null when there are none.</summary>
    private static (int, int, int, int)? Ink(RgbImage img, int x0, int y0, int x1, int y1)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int q = (y * img.Width + x) * 3;
                if (img.Pixels[q] + img.Pixels[q + 1] + img.Pixels[q + 2] >= 3 * 110) continue;
                l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y);
            }
        return r < 0 ? null : (l, t, r, b);
    }

    private static bool Near((int L, int T, int R, int B)? a, (int L, int T, int R, int B)? b) =>
        a is null && b is null
        || a is { } p && b is { } q && Math.Abs(p.L - q.L) <= 3 && Math.Abs(p.T - q.T) <= 3 && Math.Abs(p.R - q.R) <= 3 && Math.Abs(p.B - q.B) <= 3;

    private static string Norm(string s) => WhiteRx().Replace(s.Normalize(NormalizationForm.FormKC), "");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteRx();

    private static string N(double v) => Math.Round(v, 5).ToString("0.#####", CultureInfo.InvariantCulture);

    private static double Num(PdfItem? i) => Deref(i) switch
    {
        PdfInteger n => n.Value,
        PdfReal r => r.Value,
        PdfLongInteger l => l.Value,
        _ => 0,
    };

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
