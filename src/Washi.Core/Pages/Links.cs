// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Forms;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>A link on a page. <see cref="Target"/> is a web or mail address, or a page number (1-based) for a link inside the document.</summary>
/// <param name="Id">"page-index" (both 1-based, the index among the page's annotations), for removing or changing it.</param>
/// <param name="Kind">"uri", "page", or "other" (a named destination, a script, a file: kept, not changed).</param>
public sealed record LinkInfo(string Id, int Page, PdfRect Rect, string Kind, string Target);

/// <summary>A new link over <see cref="Rect"/> (upright points) to a web or mail address, or to a page ("7").</summary>
public sealed record NewLink(int Page, PdfRect Rect, string Target);

/// <param name="Added">Links added (by hand and found in the text).</param>
public sealed record LinkResult(int Added, int Removed, int Changed);

/// <summary>
/// Links: list them, add them over an area (to a web or mail address, or a page), turn the web and mail addresses
/// written in the text into links, change where a link goes, remove links. Links are drawn with no border, so the
/// pages look the same; the copy is rendered next to the original (no pixel may change) and its links read back
/// before it is kept. Callers hold <see cref="PdfiumDocument.Gate"/>.
/// </summary>
public static partial class PdfLinks
{
    public static IReadOnlyList<LinkInfo> List(string path)
    {
        using var doc = PdfReader.Open(path, PdfPasswords.For(path) ?? "", PdfDocumentOpenMode.Import);
        return Read(doc);
    }

    private static List<LinkInfo> Read(PdfDocument doc)
    {
        var pages = PageNumbers(doc);
        var list = new List<LinkInfo>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            if (page.Elements.GetArray("/Annots") is not { } annots) continue;
            var map = new PdfComments.Upright(page);
            for (int k = 0; k < annots.Elements.Count; k++)
            {
                if (Deref(annots.Elements[k]) is not PdfDictionary a || a.Elements.GetName("/Subtype") != "/Link") continue;
                var rc = a.Elements.GetArray("/Rect");
                var rect = rc is { Elements.Count: >= 4 }
                    ? map.ToUpright(new PdfRect(rc.Elements.GetReal(0), rc.Elements.GetReal(1), rc.Elements.GetReal(2), rc.Elements.GetReal(3)))
                    : default;
                var (kind, target) = TargetOf(a, pages);
                list.Add(new LinkInfo($"{i + 1}-{k + 1}", i, rect, kind, target));
            }
        }
        return list;
    }

    private static (string Kind, string Target) TargetOf(PdfDictionary a, Dictionary<PdfDictionary, int> pages)
    {
        if (Deref(a.Elements["/A"]) is PdfDictionary action)
        {
            var s = action.Elements.GetName("/S");
            if (s == "/URI") return ("uri", action.Elements.GetString("/URI"));
            if (s == "/GoTo" && PageOf(action.Elements["/D"], pages) is { } p) return ("page", (p + 1).ToString(CultureInfo.InvariantCulture));
            return ("other", s.TrimStart('/'));
        }
        if (PageOf(a.Elements["/Dest"], pages) is { } d) return ("page", (d + 1).ToString(CultureInfo.InvariantCulture));
        return ("other", "");
    }

    private static int? PageOf(PdfItem? dest, Dictionary<PdfDictionary, int> pages) =>
        Deref(dest) is PdfArray { Elements.Count: > 0 } arr && Deref(arr.Elements[0]) is PdfDictionary p && pages.TryGetValue(p, out var n) ? n : null;

    private static Dictionary<PdfDictionary, int> PageNumbers(PdfDocument doc)
    {
        var d = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < doc.PageCount; i++) d[doc.Pages[i]] = i;
        return d;
    }

    /// <summary>Web and mail addresses written in the text (https://…, www.…, name@host), as new links; addresses that already have a link are left out.</summary>
    public static IReadOnlyList<NewLink> FindAddresses(string path)
    {
        var existing = List(path);
        var found = PdfRedactor.Find(path, new RedactSearch([], Regexes: [AddressRx().ToString()], MatchCase: true));
        var links = new List<NewLink>();
        foreach (var m in found.Where(m => m.Source == RedactSource.Text && m.Boxes.Count > 0))
        {
            var text = m.Text.Trim().TrimEnd('.', ',', ')', '」', '。', '、');
            if (text.Length < 5) continue;
            foreach (var box in m.Boxes)
                if (!existing.Any(l => l.Page == m.Page && l.Rect.Intersect(box).Area > box.Area * 0.5) && !links.Any(l => l.Page == m.Page && l.Rect.Intersect(box).Area > 0))
                    links.Add(new NewLink(m.Page, box, text));
        }
        return links;
    }

    [GeneratedRegex(@"(?:https?://|www\.)[A-Za-z0-9\-._~:/?#\[\]@!$&'*+,;=%]+|[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex AddressRx();

    /// <summary>What a typed target means: a page number, a mail address, or a web address (https:// added when missing).</summary>
    public static (string Kind, string Value) Parse(string target)
    {
        var t = target.Trim();
        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0) return ("page", t);
        if (t.Length == 0 || t.Any(char.IsWhiteSpace)) throw new PageAssemblyException($"“{target}” is not a web address, a mail address or a page number", "Err_LinkTarget", target);
        if (Regex.IsMatch(t, @"^[A-Za-z][A-Za-z0-9+.\-]*:")) return ("uri", t);
        if (Regex.IsMatch(t, @"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")) return ("uri", "mailto:" + t);
        if (Regex.IsMatch(t, @"^[^\s/]+\.[A-Za-z]{2,}(?:[/?#].*)?$")) return ("uri", "https://" + t);
        throw new PageAssemblyException($"“{target}” is not a web address, a mail address or a page number", "Err_LinkTarget", target);
    }

    /// <summary>
    /// Adds <paramref name="add"/>, removes the links whose ids are in <paramref name="remove"/>, and points the links in
    /// <paramref name="change"/> (id → target) elsewhere.
    /// </summary>
    public static LinkResult Apply(string input, string output, IReadOnlyList<NewLink> add, IReadOnlyCollection<string>? remove = null,
                                   IReadOnlyDictionary<string, string>? change = null)
    {
        remove ??= [];
        change ??= new Dictionary<string, string>();
        if (add.Count == 0 && remove.Count == 0 && change.Count == 0) throw new PageAssemblyException("no link to add, change or remove", "Err_LinkNothing");
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));

        var expected = new List<(int Page, PdfRect Rect, string Kind, string Target)>();
        int removed = 0, changed = 0;
        using (var doc = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            var before = Read(doc);
            foreach (var id in remove.Concat(change.Keys))
                if (before.All(l => l.Id != id)) throw new PageAssemblyException($"there is no link {id}", "Err_LinkId", id);
            foreach (var l in add)
                if (l.Page < 0 || l.Page >= doc.PageCount)
                    throw new PageAssemblyException($"page {l.Page + 1} is outside 1-{doc.PageCount}", "Err_RangeOutside", (l.Page + 1).ToString(CultureInfo.InvariantCulture), doc.PageCount);

            // the dictionaries to change or drop, found before anything moves
            PdfDictionary Dict(string id)
            {
                var parts = id.Split('-');
                var annots = doc.Pages[int.Parse(parts[0], CultureInfo.InvariantCulture) - 1].Elements.GetArray("/Annots")!;
                return (PdfDictionary)Deref(annots.Elements[int.Parse(parts[1], CultureInfo.InvariantCulture) - 1])!;
            }
            var gone = remove.Select(Dict).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var (id, target) in change)
            {
                var a = Dict(id);
                if (gone.Contains(a)) continue;
                a.Elements.Remove("/Dest");
                a.Elements["/A"] = Action(doc, target);
                changed++;
            }
            foreach (var page in doc.Pages)
            {
                if (page.Elements.GetArray("/Annots") is not { } annots) continue;
                for (int k = annots.Elements.Count - 1; k >= 0; k--)
                    if (Deref(annots.Elements[k]) is PdfDictionary a && gone.Contains(a)) { annots.Elements.RemoveAt(k); removed++; }
            }
            foreach (var l in add)
            {
                var page = doc.Pages[l.Page];
                var map = new PdfComments.Upright(page);
                var r = map.PdfRect(l.Rect);
                var a = new PdfDictionary(doc);
                doc.Internals.AddObject(a);
                a.Elements.SetName("/Type", "/Annot");
                a.Elements.SetName("/Subtype", "/Link");
                a.Elements["/Rect"] = new PdfArray(doc, new PdfReal(r.X0), new PdfReal(r.Y0), new PdfReal(r.X1), new PdfReal(r.Y1));
                a.Elements["/Border"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));
                a.Elements["/A"] = Action(doc, l.Target);
                a.Elements["/P"] = page.Reference!;
                AcroFormWriter.GetOrCreateArray(doc, page, "/Annots").Elements.Add(a.Reference!);
                var (kind, value) = Parse(l.Target);
                expected.Add((l.Page, l.Rect, kind, kind == "page" ? value : value));
            }
            doc.Save(output);
        }

        try { Verify(input, output, expected); }
        catch
        {
            File.Delete(output);
            throw;
        }
        return new LinkResult(add.Count, removed, changed);
    }

    private static PdfDictionary Action(PdfDocument doc, string target)
    {
        var (kind, value) = Parse(target);
        var action = new PdfDictionary(doc);
        action.Elements.SetName("/Type", "/Action");
        if (kind == "page")
        {
            int n = int.Parse(value, CultureInfo.InvariantCulture);
            if (n > doc.PageCount) throw new PageAssemblyException($"page {n} is outside 1-{doc.PageCount}", "Err_RangeOutside", value, doc.PageCount);
            action.Elements.SetName("/S", "/GoTo");
            action.Elements["/D"] = new PdfArray(doc, doc.Pages[n - 1].Reference!, new PdfName("/Fit"));
        }
        else
        {
            action.Elements.SetName("/S", "/URI");
            action.Elements["/URI"] = new PdfString(value);
        }
        return action;
    }

    /// <summary>The pages look the same (links are invisible), and every new link is there with its target.</summary>
    private static void Verify(string before, string after, List<(int Page, PdfRect Rect, string Kind, string Target)> expected)
    {
        var links = List(after);
        foreach (var (page, rect, kind, target) in expected)
            if (!links.Any(l => l.Page == page && l.Kind == kind && l.Target == target && l.Rect.IoU(rect) > 0.9))
                throw new PageAssemblyException($"the link to {target} on page {page + 1} did not read back; the copy was not kept", "Err_LinkCheck", target, page + 1);
        using var a = PdfiumDocument.Open(before);
        using var b = PdfiumDocument.Open(after);
        foreach (var page in expected.Select(e => e.Page).Distinct().Take(20))
        {
            var ia = a.RenderPage(page, 1.0);
            var ib = b.RenderPage(page, 1.0);
            if (ia.Width != ib.Width || ia.Height != ib.Height || !ia.Pixels.AsSpan().SequenceEqual(ib.Pixels))
                throw new PageAssemblyException($"page {page + 1} looked different with its links; the copy was not kept", "Err_LinkChanged", page + 1);
        }
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
