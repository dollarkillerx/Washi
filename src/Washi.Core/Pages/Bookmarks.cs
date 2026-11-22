// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Washi.Core.Pages;

/// <summary>
/// A bookmark (outline item). <see cref="Page"/> is 0-based, or -1 when it does something other than open a page of this
/// document (a web link, another file). <see cref="Top"/> is where on the page it opens, in upright top-down points
/// (null: the top of the page). <see cref="Source"/> is its place in the file's own outline (pre-order), so a bookmark
/// that was not changed keeps its action, colour and style when the outline is written back.
/// </summary>
public sealed record Bookmark(string Title, int Page, double? Top, IReadOnlyList<Bookmark> Children, bool Open = false, int? Source = null)
{
    public static Bookmark Make(string title, int page, params Bookmark[] children) => new(title, page, null, children);

    /// <summary>This bookmark and all below it, in order.</summary>
    public IEnumerable<Bookmark> Flatten() => Children.SelectMany(c => c.Flatten()).Prepend(this);
}

/// <summary>
/// Reads and writes a PDF's bookmarks (its outline). Writing replaces the whole outline with the one given; every
/// bookmark the file already had and that still points where it did keeps its original action and style. Each copy is
/// read back to check it holds exactly the outline asked for.
/// </summary>
public static class PdfBookmarks
{
    private const int MaxItems = 20000;

    public static IReadOnlyList<Bookmark> Read(string path)
    {
        using var d = Open(path, PdfDocumentOpenMode.Import);
        return Read(d, out _);
    }

    private static List<Bookmark> Read(PdfDocument d, out List<PdfDictionary> items)
    {
        var found = items = [];
        var pages = PageIndexes(d);
        var named = NamedDestinations(d);
        if (Deref(d.Internals.Catalog.Elements["/Outlines"]) is not PdfDictionary root) return [];
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        return Children(root, 0);

        List<Bookmark> Children(PdfDictionary parent, int depth)
        {
            var list = new List<Bookmark>();
            if (depth > 64) return list;
            for (var item = Deref(parent.Elements["/First"]) as PdfDictionary; item is not null && found.Count < MaxItems;
                 item = Deref(item.Elements["/Next"]) as PdfDictionary)
            {
                if (!seen.Add(item)) break;   // a loop in a damaged file
                int source = found.Count;
                found.Add(item);
                var (page, top) = Target(item, d, pages, named);
                var title = (Deref(item.Elements["/Title"]) as PdfString)?.Value ?? "";
                bool open = item.Elements.GetInteger("/Count") > 0;
                list.Add(new Bookmark(Clean(title), page, top, Children(item, depth + 1), open, source));
            }
            return list;
        }
    }

    /// <summary>
    /// The title as a reader shows it. PDFsharp reads a PDFDocEncoding string as Latin-1, so the characters where the two
    /// differ (’ “ ” • — … and others) come out as control characters: they are mapped back. Line breaks and other
    /// control characters become spaces.
    /// </summary>
    internal static string Clean(string s) => new string([.. s.Select(c => c switch
    {
        >= '\u0018' and <= '\u001f' => "˘ˇˆ˙˝˛˚˜"[c - 0x18],
        >= '\u0080' and <= '\u009e' => "•†‡…—–ƒ⁄‹›−‰„“”‘’‚™ﬁﬂŁŒŠŸŽıłœšž"[c - 0x80],
        _ when char.IsControl(c) => ' ',
        _ => c,
    })]).Trim();

    // ------------------------------------------------------------------ where a bookmark goes

    private static (int Page, double? Top) Target(PdfDictionary item, PdfDocument d, Dictionary<PdfDictionary, int> pages,
                                                  Dictionary<string, PdfItem> named)
    {
        var dest = Deref(item.Elements["/Dest"]);
        if (dest is null && Deref(item.Elements["/A"]) is PdfDictionary a && a.Elements.GetName("/S") == "/GoTo")
            dest = Deref(a.Elements["/D"]);
        return Resolve(dest, d, pages, named, 0);
    }

    private static (int Page, double? Top) Resolve(PdfItem? dest, PdfDocument d, Dictionary<PdfDictionary, int> pages,
                                                   Dictionary<string, PdfItem> named, int depth)
    {
        if (depth > 4) return (-1, null);
        switch (dest)
        {
            case PdfString s when named.TryGetValue(s.Value, out var n): return Resolve(Deref(n), d, pages, named, depth + 1);
            case PdfName nm when named.TryGetValue(nm.Value.TrimStart('/'), out var n): return Resolve(Deref(n), d, pages, named, depth + 1);
            case PdfDictionary dd when dd.Elements.ContainsKey("/D"): return Resolve(Deref(dd.Elements["/D"]), d, pages, named, depth + 1);
            case PdfArray arr when arr.Elements.Count > 0:
                int page = Deref(arr.Elements[0]) switch
                {
                    PdfDictionary p => pages.GetValueOrDefault(p, -1),
                    PdfInteger i => i.Value,   // some writers put a page number here
                    _ => -1,
                };
                if (page < 0 || page >= d.PageCount) return (-1, null);
                var kind = arr.Elements.Count > 1 ? (Deref(arr.Elements[1]) as PdfName)?.Value : null;
                int at = kind switch { "/XYZ" => 3, "/FitH" or "/FitBH" => 2, "/FitR" => 5, _ => -1 };
                double? top = null;
                if (at > 0 && at < arr.Elements.Count && Deref(arr.Elements[at]) is PdfInteger or PdfReal)
                {
                    double y = FontMetrics.Num(arr.Elements[at]);
                    var down = PdfRedactor.Upright(d.Pages[page]).Inverse() ?? Mx.Identity;
                    top = Math.Max(0, down.Apply(0, y).Y);
                    if (((d.Pages[page].Rotate % 360) + 360) % 360 is 90 or 270) top = null;   // a y on a turned page is a left/right
                }
                return (page, top);
        }
        return (-1, null);
    }

    private static Dictionary<PdfDictionary, int> PageIndexes(PdfDocument d)
    {
        var map = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < d.PageCount; i++) map[d.Pages[i]] = i;
        return map;
    }

    /// <summary>Named destinations: the old /Dests dictionary and the /Names /Dests name tree.</summary>
    private static Dictionary<string, PdfItem> NamedDestinations(PdfDocument d)
    {
        var map = new Dictionary<string, PdfItem>(StringComparer.Ordinal);
        if (Deref(d.Internals.Catalog.Elements["/Dests"]) is PdfDictionary old)
            foreach (var k in old.Elements.Keys) map[k.TrimStart('/')] = old.Elements[k]!;
        if (Deref(d.Internals.Catalog.Elements["/Names"]) is PdfDictionary names && Deref(names.Elements["/Dests"]) is PdfDictionary tree)
            Walk(tree, 0);
        return map;

        void Walk(PdfDictionary node, int depth)
        {
            if (depth > 32) return;
            if (Deref(node.Elements["/Names"]) is PdfArray pairs)
                for (int i = 0; i + 1 < pairs.Elements.Count; i += 2)
                    if (Deref(pairs.Elements[i]) is PdfString key) map[key.Value] = pairs.Elements[i + 1]!;
            if (Deref(node.Elements["/Kids"]) is PdfArray kids)
                foreach (var k in kids.Elements)
                    if (Deref(k) is PdfDictionary kid) Walk(kid, depth + 1);
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Writes <paramref name="output"/> with <paramref name="outline"/> as its bookmarks (an empty list removes them).</summary>
    public static void Write(string input, string output, IReadOnlyList<Bookmark> outline)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        using (var d = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            foreach (var b in outline.SelectMany(b => b.Flatten()))
            {
                if (b.Page >= d.PageCount)
                    throw new PageAssemblyException($"bookmark '{b.Title}' points to page {b.Page + 1}, but the PDF has {d.PageCount}", "Err_BookmarkPage", b.Title, b.Page + 1, d.PageCount);
                if (string.IsNullOrWhiteSpace(b.Title))
                    throw new PageAssemblyException("every bookmark needs a title", "Err_BookmarkTitle");
            }
            var old = Read(d, out var originals);
            var oldByIndex = old.SelectMany(b => b.Flatten()).ToDictionary(b => b.Source!.Value);
            // a bookmark given without its source (from a text file or an agent) is matched to the old one with the
            // same title and page, when there is exactly one
            var oldByTitle = oldByIndex.Values.GroupBy(b => (b.Title, b.Page)).Where(g => g.Count() == 1)
                                       .ToDictionary(g => g.Key, g => g.First().Source!.Value);
            var catalog = d.Internals.Catalog;
            if (outline.Count == 0) catalog.Elements.Remove("/Outlines");
            else
            {
                var root = new PdfDictionary(d);
                d.Internals.AddObject(root);
                root.Elements.SetName("/Type", "/Outlines");
                int count = Add(root, outline);
                root.Elements.SetInteger("/Count", count);
                catalog.Elements["/Outlines"] = root.Reference;
            }
            d.Save(output);

            int Add(PdfDictionary parent, IReadOnlyList<Bookmark> items)
            {
                PdfDictionary? prev = null;
                int visible = 0;
                foreach (var b in items)
                {
                    var item = new PdfDictionary(d);
                    d.Internals.AddObject(item);
                    item.Elements["/Title"] = new PdfString(b.Title.Trim(), PdfStringEncoding.Unicode);
                    item.Elements["/Parent"] = parent.Reference;
                    if (prev is null) parent.Elements["/First"] = item.Reference;
                    else { prev.Elements["/Next"] = item.Reference; item.Elements["/Prev"] = prev.Reference; }
                    parent.Elements["/Last"] = item.Reference;
                    Target(item, b);
                    visible++;
                    if (b.Children.Count > 0)
                    {
                        int below = Add(item, b.Children);
                        item.Elements.SetInteger("/Count", b.Open ? below : -b.Children.Count);
                        if (b.Open) visible += below;
                    }
                    prev = item;
                }
                return visible;
            }

            void Target(PdfDictionary item, Bookmark b)
            {
                // unchanged since it was read: its own action, colour and style
                int? source = b.Source ?? (oldByTitle.TryGetValue((b.Title.Trim(), b.Page), out var m) ? m : null);
                if (source is { } s && s < originals.Count && oldByIndex.TryGetValue(s, out var was))
                {
                    var src = originals[s];
                    if (was.Title == b.Title.Trim() && src.Elements["/Title"] is PdfString title) item.Elements["/Title"] = title;   // as it was written
                    foreach (var key in new[] { "/C", "/F" })
                        if (src.Elements[key] is { } v) item.Elements[key] = v is PdfObject { IsIndirect: false } o ? o.Clone() : v;
                    if (was.Page == b.Page && Same(was.Top, b.Top))
                    {
                        foreach (var key in new[] { "/Dest", "/A", "/SE" })
                            if (src.Elements[key] is { } v) item.Elements[key] = v is PdfObject { IsIndirect: false } o ? o.Clone() : v;
                        return;
                    }
                }
                if (b.Page < 0) return;   // goes nowhere in this document
                var page = d.Pages[b.Page];
                var dest = new PdfArray(d) { Elements = { page.Reference! } };
                if (b.Top is { } top)
                {
                    var up = PdfRedactor.Upright(page);
                    var (_, y) = up.Apply(0, top);
                    dest.Elements.Add(new PdfName("/XYZ"));
                    dest.Elements.Add(PdfNull.Value);
                    dest.Elements.Add(new PdfReal(Math.Round(y, 2)));
                    dest.Elements.Add(PdfNull.Value);
                }
                else dest.Elements.Add(new PdfName("/Fit"));
                item.Elements["/Dest"] = dest;
            }
        }

        // proof: the copy has exactly the outline asked for
        var back = Read(output);
        if (!Same(back, outline))
        {
            File.Delete(output);
            throw new PageAssemblyException("the bookmarks of the copy did not come out as asked; it was not kept", "Err_BookmarkCheck");
        }
    }

    // ------------------------------------------------------------------ the text form (CLI)

    /// <summary>One line per bookmark: two spaces per level, the title, a tab, the page (1-based; "-" for a link elsewhere).</summary>
    public static IEnumerable<string> ToLines(IReadOnlyList<Bookmark> outline, int level = 0) =>
        outline.SelectMany(b => ToLines(b.Children, level + 1)
            .Prepend(new string(' ', level * 2) + b.Title + "\t" + (b.Page >= 0 ? (b.Page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")));

    /// <summary>Reads the text form back (blank lines and lines starting with # are skipped).</summary>
    public static IReadOnlyList<Bookmark> FromLines(IEnumerable<string> lines)
    {
        var rows = new List<(int Level, string Title, int Page)>();
        int n = 0;
        foreach (var raw in lines)
        {
            n++;
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#')) continue;
            int spaces = raw.Length - raw.TrimStart(' ').Length;
            var text = raw.Trim();
            int cut = text.LastIndexOf('\t');
            if (cut < 0) cut = text.LastIndexOf(" | ", StringComparison.Ordinal) is var bar and >= 0 ? bar : -1;
            if (cut < 0) throw new PageAssemblyException($"line {n}: put a tab (or \" | \") between the title and the page");
            var title = text[..cut].Trim();
            var pageText = text[(cut + 1)..].Trim().TrimStart('|').Trim();
            int page = pageText == "-" ? -1
                : int.TryParse(pageText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) && p >= 1 ? p - 1
                : throw new PageAssemblyException($"line {n}: '{pageText}' is not a page number");
            int level = Math.Min(spaces / 2, rows.Count == 0 ? 0 : rows[^1].Level + 1);
            rows.Add((level, title, page));
        }
        int i = 0;
        return Level(0);

        List<Bookmark> Level(int level)
        {
            var list = new List<Bookmark>();
            while (i < rows.Count && rows[i].Level >= level)
            {
                var r = rows[i++];
                list.Add(new Bookmark(r.Title, r.Page, null, Level(r.Level + 1)));
            }
            return list;
        }
    }

    private static bool Same(double? a, double? b) => a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) < 0.6;

    private static bool Same(IReadOnlyList<Bookmark> a, IReadOnlyList<Bookmark> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Title == p.Second.Title.Trim()
            && (p.First.Page == p.Second.Page || p.Second.Page < 0)
            && Same(p.First.Children, p.Second.Children));

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try { return PdfReader.Open(path, PdfPasswords.For(path) ?? "", mode); }
        catch (PdfReaderException e) when (PdfSecurity.IsPasswordProblem(e))
        {
            throw new PageAssemblyException($"{Path.GetFileName(path)} is protected with a password", "Err_NeedsPassword", Path.GetFileName(path));
        }
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
