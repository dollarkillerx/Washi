// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Washi.Core.Pages;

/// <summary>Information a PDF carries that the pages do not show.</summary>
public enum HiddenInfo
{
    /// <summary>Document properties (title, author, dates, the program that made it) and XMP metadata.</summary>
    Metadata,
    /// <summary>Files attached to the document or to comments.</summary>
    Attachments,
    /// <summary>JavaScript, actions that run on opening or on events, launching programs, sending form data, XFA.</summary>
    Scripts,
    /// <summary>Comments and markup (notes, highlights, drawings, stamps); not form fields or links.</summary>
    Comments,
    /// <summary>Data programs keep for themselves (/PieceInfo: often a whole editable copy) and page thumbnails.</summary>
    PrivateData,
    /// <summary>Earlier versions kept in the file by incremental saves. Always removed: Washi writes the file anew.</summary>
    EarlierVersions,
}

/// <summary>Something found: how many, and what (names of properties or attached files; may be empty).</summary>
public sealed record HiddenItem(HiddenInfo Kind, int Count, IReadOnlyList<string> Details);

/// <summary>
/// Remove hidden information (PDFsharp). <see cref="Inspect"/> lists what a file carries besides its pages;
/// <see cref="Clean"/> writes a copy without the chosen kinds and checks the copy. Pages, form fields, links inside the
/// document and web links are kept.
/// </summary>
public static class PdfCleaner
{
    private static readonly HashSet<string> RiskyActions = new(StringComparer.Ordinal)
    {
        "/JavaScript", "/Launch", "/SubmitForm", "/ImportData", "/GoToR", "/GoToE", "/Rendition", "/Sound", "/Movie", "/RichMediaExecute",
    };

    private static readonly HashSet<string> CommentTypes = new(StringComparer.Ordinal)
    {
        "/Text", "/FreeText", "/Line", "/Square", "/Circle", "/Polygon", "/PolyLine", "/Highlight", "/Underline", "/Squiggly",
        "/StrikeOut", "/Stamp", "/Caret", "/Ink", "/Sound", "/Redact", "/Popup",
    };

    /// <summary>What <paramref name="input"/> carries, by kind; kinds with nothing are left out.</summary>
    public static IReadOnlyList<HiddenItem> Inspect(string input)
    {
        using var d = Open(input, PdfDocumentOpenMode.Import);
        return Inspect(d, File.ReadAllBytes(input));
    }

    /// <summary>
    /// Writes <paramref name="output"/> without the kinds in <paramref name="remove"/>, then checks that the copy has
    /// none of them left. Returns what was removed.
    /// </summary>
    public static IReadOnlyList<HiddenItem> Clean(string input, string output, IReadOnlySet<HiddenInfo> remove)
    {
        if (remove.Count == 0) throw new PageAssemblyException("choose what to remove", "Err_CleanNothingChosen");
        IReadOnlyList<HiddenItem> found;
        using (var d = Open(input, PdfDocumentOpenMode.Modify))
        {
            found = Inspect(d, File.ReadAllBytes(input));
            var all = Objects(d);
            var catalog = d.Internals.Catalog;
            if (remove.Contains(HiddenInfo.Metadata))
            {
                d.Info.Elements.Clear();
                foreach (var o in all) o.Elements.Remove("/Metadata");
            }
            if (remove.Contains(HiddenInfo.Attachments))
            {
                Deref(catalog.Elements["/Names"]).AsDict()?.Elements.Remove("/EmbeddedFiles");
                catalog.Elements.Remove("/Collection");
                foreach (var o in all) o.Elements.Remove("/AF");
                RemoveAnnotations(d, t => t == "/FileAttachment");
            }
            if (remove.Contains(HiddenInfo.Scripts))
            {
                Deref(catalog.Elements["/Names"]).AsDict()?.Elements.Remove("/JavaScript");
                Deref(catalog.Elements["/AcroForm"]).AsDict()?.Elements.Remove("/XFA");
                foreach (var o in all)
                {
                    o.Elements.Remove("/AA");
                    foreach (var key in new[] { "/A", "/OpenAction", "/Next", "/PA" })
                        if (IsRisky(o.Elements[key])) o.Elements.Remove(key);
                }
            }
            if (remove.Contains(HiddenInfo.Comments)) RemoveAnnotations(d, t => CommentTypes.Contains(t));
            if (remove.Contains(HiddenInfo.PrivateData))
                foreach (var o in all)
                {
                    o.Elements.Remove("/PieceInfo");
                    o.Elements.Remove("/Thumb");
                    o.Elements.Remove("/SpiderInfo");
                }
            d.Save(output);
        }
        if (remove.Contains(HiddenInfo.Metadata)) NeutralTimeZone(output);

        // proof: nothing of the chosen kinds is left
        var left = Inspect(output).Where(i => remove.Contains(i.Kind)).ToList();
        if (left.Count > 0)
        {
            File.Delete(output);
            throw new PageAssemblyException("the cleaned copy still had " + string.Join(", ", left.Select(l => l.Kind)) + "; it was not kept",
                                            "Err_CleanIncomplete", string.Join(", ", left.Select(l => l.Kind)));
        }
        return [.. found.Where(f => remove.Contains(f.Kind) || f.Kind == HiddenInfo.EarlierVersions)];
    }

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            var d = PdfReader.Open(path, mode);
            if (d.SecurityHandler.Elements.GetInteger("/V") > 0)
            {
                d.Dispose();
                throw new PageAssemblyException($"{Path.GetFileName(path)} is protected: remove its password first, clean it, then protect it again",
                                                "Err_CleanProtected", Path.GetFileName(path));
            }
            return d;
        }
        catch (PdfReaderException e) when (PdfSecurity.IsPasswordProblem(e))
        {
            throw new PageAssemblyException($"{Path.GetFileName(path)} is protected: remove its password first, clean it, then protect it again",
                                            "Err_CleanProtected", Path.GetFileName(path));
        }
    }

    private static List<HiddenItem> Inspect(PdfDocument d, byte[] bytes)
    {
        var all = Objects(d);
        var catalog = d.Internals.Catalog;
        var items = new List<HiddenItem>();
        void Add(HiddenInfo kind, int count, IEnumerable<string>? details = null)
        {
            if (count > 0) items.Add(new(kind, count, [.. (details ?? []).Distinct()]));
        }

        // metadata: properties with a value (PDFsharp's own name, written on every save, does not count) and XMP
        var props = d.Info.Elements.Keys
            .Where(k => d.Info.Elements[k] is { } v && Text(v) is { Length: > 0 } s && !s.StartsWith("PDFsharp", StringComparison.Ordinal))
            .Select(k => k.TrimStart('/')).ToList();
        int xmp = all.Count(o => !ReferenceEquals(o, catalog.Elements.GetDictionary("/Metadata")) && o.Elements.GetName("/Type") == "/Metadata")
                  + (catalog.Elements.GetDictionary("/Metadata") is { } m && XmpSaysSomething(m) ? 1 : 0);
        Add(HiddenInfo.Metadata, props.Count + xmp, xmp > 0 ? props.Append("XMP") : props);

        // attachments
        var names = new List<string>();
        if (Deref(catalog.Elements["/Names"]).AsDict() is { } nameTree && Deref(nameTree.Elements["/EmbeddedFiles"]).AsDict() is { } files)
            CollectNames(files, names, 0);
        var attachedToComments = Annotations(d).Where(a => a.Elements.GetName("/Subtype") == "/FileAttachment").ToList();
        foreach (var a in attachedToComments)
            if (Deref(a.Elements["/FS"]).AsDict() is { } fs) names.Add(Text(fs.Elements["/UF"]) is { Length: > 0 } uf ? uf : Text(fs.Elements["/F"]));
        int embedded = all.Count(o => o.Elements.GetName("/Type") == "/EmbeddedFile");
        Add(HiddenInfo.Attachments, Math.Max(embedded, names.Count), names.Where(n => n.Length > 0));

        // scripts and actions
        int scripts = 0;
        var what = new List<string>();
        if (Deref(catalog.Elements["/Names"]).AsDict() is { } nt && nt.Elements.ContainsKey("/JavaScript")) { scripts++; what.Add("JavaScript"); }
        if (Deref(catalog.Elements["/AcroForm"]).AsDict() is { } af && af.Elements.ContainsKey("/XFA")) { scripts++; what.Add("XFA"); }
        foreach (var o in all)
        {
            if (o.Elements.ContainsKey("/AA")) { scripts++; what.Add("AA"); }
            foreach (var key in new[] { "/A", "/OpenAction", "/Next", "/PA" })
                if (IsRisky(o.Elements[key])) { scripts++; what.Add(Deref(o.Elements[key]).AsDict()!.Elements.GetName("/S").TrimStart('/')); }
        }
        Add(HiddenInfo.Scripts, scripts, what);

        // comments (popups belong to their comment: not counted on their own)
        Add(HiddenInfo.Comments, Annotations(d).Count(a => a.Elements.GetName("/Subtype") is var t && t != "/Popup" && CommentTypes.Contains(t)));

        // private application data and thumbnails
        int priv = all.Count(o => o.Elements.ContainsKey("/PieceInfo") || o.Elements.ContainsKey("/Thumb") || o.Elements.ContainsKey("/SpiderInfo"));
        Add(HiddenInfo.PrivateData, priv);

        // earlier versions: one %%EOF per save (a linearised file has one more, for its first page)
        int eofs = Regex.Count(Encoding.Latin1.GetString(bytes), "%%EOF");
        bool linearized = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 1024)).Contains("/Linearized", StringComparison.Ordinal);
        Add(HiddenInfo.EarlierVersions, Math.Max(0, eofs - 1 - (linearized ? 1 : 0)));
        return items;
    }

    /// <summary>The catalog's XMP says more than what PDFsharp writes for an empty document (its name, empty values,
    /// fresh random ids, a zero date).</summary>
    private static bool XmpSaysSomething(PdfDictionary metadata)
    {
        if (metadata.Stream is null) return false;
        var xml = Encoding.UTF8.GetString(metadata.Stream.UnfilteredValue);
        foreach (Match v in Regex.Matches(xml, @">([^<>]+)<"))
        {
            var s = v.Groups[1].Value.Trim();
            if (s.Length == 0 || s.StartsWith("PDFsharp", StringComparison.Ordinal) || s.StartsWith("0001-01-01", StringComparison.Ordinal)
                || Regex.IsMatch(s, @"^uuid:[0-9a-f-]{36}$")) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// PDFsharp writes the zero dates of an empty document with this computer's time zone ("+09:18"): replaced with
    /// "+00:00" in place (same length, so the file's offsets stay right).
    /// </summary>
    private static void NeutralTimeZone(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(bytes);
        var fixedText = Regex.Replace(text, @"(0001-01-01T00:00:00)[+-]\d\d:\d\d", "$1+00:00");
        if (fixedText != text) File.WriteAllBytes(path, Encoding.Latin1.GetBytes(fixedText));
    }

    private static void CollectNames(PdfDictionary node, List<string> names, int depth)
    {
        if (depth > 32) return;
        if (Deref(node.Elements["/Names"]) is PdfArray pairs)
            for (int i = 0; i + 1 < pairs.Elements.Count; i += 2)
            {
                var spec = Deref(pairs.Elements[i + 1]).AsDict();
                var name = spec is null ? "" : Text(spec.Elements["/UF"]) is { Length: > 0 } uf ? uf : Text(spec.Elements["/F"]);
                names.Add(name.Length > 0 ? name : Text(pairs.Elements[i]));
            }
        if (Deref(node.Elements["/Kids"]) is PdfArray kids)
            foreach (var k in kids.Elements)
                if (Deref(k).AsDict() is { } kid) CollectNames(kid, names, depth + 1);
    }

    private static IEnumerable<PdfDictionary> Annotations(PdfDocument d) =>
        d.Pages.Cast<PdfPage>().SelectMany(p => Deref(p.Elements["/Annots"]) is PdfArray a ? a.Elements.Select(e => Deref(e).AsDict()).OfType<PdfDictionary>() : []);

    /// <summary>Removes annotations whose subtype matches, and the popups that belong to them.</summary>
    private static void RemoveAnnotations(PdfDocument d, Func<string, bool> subtype)
    {
        foreach (PdfPage page in d.Pages)
        {
            if (Deref(page.Elements["/Annots"]) is not PdfArray annots) continue;
            var gone = annots.Elements.Select(e => Deref(e).AsDict()).OfType<PdfDictionary>()
                .Where(a => subtype(a.Elements.GetName("/Subtype"))).ToHashSet(ReferenceEqualityComparer.Instance);
            if (gone.Count == 0) continue;
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
            {
                var a = Deref(annots.Elements[i]).AsDict();
                if (a is null) continue;
                bool popupOfGone = a.Elements.GetName("/Subtype") == "/Popup" && Deref(a.Elements["/Parent"]).AsDict() is { } parent && gone.Contains(parent);
                if (gone.Contains(a) || popupOfGone) annots.Elements.RemoveAt(i);
            }
            if (annots.Elements.Count == 0) page.Elements.Remove("/Annots");
        }
    }

    private static bool IsRisky(PdfItem? item) =>
        Deref(item).AsDict() is { } a && RiskyActions.Contains(a.Elements.GetName("/S"));

    private static string Text(PdfItem? item) => Deref(item) switch
    {
        PdfString s => s.Value,
        PdfName n => n.Value.TrimStart('/'),
        PdfDate dt => dt.Value.ToString("O"),
        PdfItem i => i.ToString() ?? "",
        null => "",
    };

    /// <summary>Every dictionary in the file, the catalog included, once each.</summary>
    private static List<PdfDictionary> Objects(PdfDocument d) =>
        [d.Internals.Catalog, .. d.Internals.GetAllObjects().OfType<PdfDictionary>().Where(o => o.Reference is null || o.Reference != d.Internals.Catalog.Reference)];

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;

    private static PdfDictionary? AsDict(this PdfItem? i) => i as PdfDictionary;
}
