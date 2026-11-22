// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Forms;

namespace Washi.Core.Pages;

public sealed class PageAssemblyException(string message, string? key = null, params object[] args) : Exception(message), Washi.Core.Localization.ILocalizedError
{
    public string? Key { get; } = key;
    public object[] Args { get; } = args;
}

public sealed record SplitPart(string Path, int FirstPage, int LastPage);

/// <summary>One page of an organized document: page <paramref name="Page"/> (1-based) of <paramref name="Source"/>,
/// turned clockwise by <paramref name="Rotate"/> degrees (a multiple of 90) on top of its own rotation.</summary>
public sealed record PagePick(string Source, int Page, int Rotate = 0);

/// <summary>
/// Merging and splitting (PDFsharp). Copied pages keep their form fields: the fields are
/// registered again in the new document's AcroForm, names that would collide get a suffix
/// (two "氏名" fields would otherwise share one value), and the fonts their appearances use
/// are put in the form resources. Files whose permissions forbid reusing pages are refused.
/// </summary>
public static class PageAssembly
{
    /// <summary>Merges <paramref name="inputs"/> in order. Returns the number of pages written.</summary>
    public static int Merge(IReadOnlyList<string> inputs, string output)
    {
        if (inputs.Count == 0) throw new PageAssemblyException("nothing to merge", "Err_NothingToMerge");
        var sources = inputs.Select(Open).ToList();     // all checked before anything is written
        using var target = new PdfDocument();
        foreach (var src in sources)
        {
            for (int p = 0; p < src.PageCount; p++) target.AddPage(src.Pages[p]);
        }
        RebuildForm(target);
        sources.ForEach(s => s.Dispose());
        int count = target.PageCount;
        target.Options.CompressContentStreams = true;
        target.Save(output);
        return count;
    }

    /// <summary>
    /// Writes the pages in <paramref name="pages"/>, in that order: reorder, delete (leave a page out), rotate, and
    /// insert pages from other files in one go. Form fields are kept, as in <see cref="Merge"/>; on a turned page their
    /// widgets turn with it (/MK /R), so text typed later still reads upright. Returns the number of pages written.
    /// </summary>
    public static int Organize(IReadOnlyList<PagePick> pages, string output)
    {
        if (pages.Count == 0) throw new PageAssemblyException("no pages to write", "Err_NoPages");
        var seen = new HashSet<(string, int)>();
        foreach (var pick in pages)
        {
            if (pick.Rotate % 90 != 0)
                throw new PageAssemblyException($"rotation {pick.Rotate} is not a multiple of 90", "Err_BadRotation", pick.Rotate);
            // the same page twice would share its form fields (one value in two places): not supported
            if (!seen.Add((Path.GetFullPath(pick.Source), pick.Page)))
                throw new PageAssemblyException($"page {pick.Page} of {Path.GetFileName(pick.Source)} is listed twice", "Err_PageTwice",
                                                pick.Page, Path.GetFileName(pick.Source));
        }
        var sources = new Dictionary<string, PdfDocument>();
        try
        {
            foreach (var path in pages.Select(p => Path.GetFullPath(p.Source)).Distinct())
                sources[path] = Open(path);                                  // all checked before anything is written
            foreach (var pick in pages)
            {
                int n = sources[Path.GetFullPath(pick.Source)].PageCount;
                if (pick.Page < 1 || pick.Page > n)
                    throw new PageAssemblyException($"page {pick.Page} is outside 1-{n}", "Err_RangeOutside", pick.Page.ToString(System.Globalization.CultureInfo.InvariantCulture), n);
            }
            using var target = new PdfDocument();
            foreach (var pick in pages)
            {
                var page = target.AddPage(sources[Path.GetFullPath(pick.Source)].Pages[pick.Page - 1]);
                if (Norm(pick.Rotate) is var turn and not 0) Turn(page, turn);
            }
            RebuildForm(target);
            int count = target.PageCount;
            target.Options.CompressContentStreams = true;
            target.Save(output);
            return count;
        }
        finally
        {
            foreach (var d in sources.Values) d.Dispose();
        }
    }

    private static int Norm(int degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>Turns the page clockwise; its form widgets get the same /MK /R, as Acrobat does.</summary>
    private static void Turn(PdfPage page, int degrees)
    {
        page.Rotate = Norm(page.Rotate + degrees);
        if (page.Elements.GetArray("/Annots") is not { } annots) return;
        foreach (var a in annots.Elements)
        {
            if (Deref(a) is not PdfDictionary w || w.Elements.GetName("/Subtype") != "/Widget") continue;
            if (Deref(w.Elements["/MK"]) is not PdfDictionary mk)
            {
                mk = new PdfDictionary(page.Owner);
                w.Elements["/MK"] = mk;
            }
            mk.Elements["/R"] = new PdfInteger(Norm(mk.Elements.GetInteger("/R") + degrees));
        }
    }

    /// <summary>
    /// "3,1-2,5" → pages 3, 1, 2, 5 of <paramref name="input"/> in that order (pages not listed are dropped);
    /// <paramref name="rotate"/> "2:90,5:-90" turns source pages 2 and 5. Empty <paramref name="order"/> keeps every page.
    /// </summary>
    public static List<PagePick> ParsePicks(string input, string? order, string? rotate, int pageCount)
    {
        var turns = new Dictionary<int, int>();
        foreach (var part in (rotate ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':', StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !int.TryParse(kv[0], out var pg) || !int.TryParse(kv[1], out var deg) || deg % 90 != 0)
                throw new PageAssemblyException($"bad rotation '{part}' (use e.g. 2:90,5:-90)", "Err_BadRotateSpec", part);
            if (pg < 1 || pg > pageCount) throw new PageAssemblyException($"page '{part}' is outside 1-{pageCount}", "Err_RangeOutside", part, pageCount);
            turns[pg] = deg;
        }
        var ranges = string.IsNullOrWhiteSpace(order) ? [(1, pageCount)] : ParseRanges(order, pageCount);
        return [.. ranges.SelectMany(r => Enumerable.Range(r.From, r.To - r.From + 1))
                         .Select(p => new PagePick(input, p, turns.GetValueOrDefault(p)))];
    }

    /// <summary>
    /// Splits into one file per range (1-based, inclusive), named "&lt;stem&gt;_p&lt;from&gt;-&lt;to&gt;.pdf" in
    /// <paramref name="outputDir"/>.
    /// </summary>
    public static IReadOnlyList<SplitPart> Split(string input, IReadOnlyList<(int From, int To)> ranges, string outputDir)
    {
        using var src = Open(input);
        Directory.CreateDirectory(outputDir);
        var stem = Path.GetFileNameWithoutExtension(input);
        var parts = new List<SplitPart>();
        foreach (var (from, to) in ranges)
        {
            if (from < 1 || to > src.PageCount || from > to)
                throw new PageAssemblyException($"page range {from}-{to} is outside 1-{src.PageCount}", "Err_RangeOutside", $"{from}-{to}", src.PageCount);
            var target = Path.Combine(outputDir, PartName(stem, from, to));
            if (File.Exists(target)) throw new PageAssemblyException($"{target} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(target));
        }
        foreach (var (from, to) in ranges)
        {
            using var part = new PdfDocument();
            for (int p = from; p <= to; p++) part.AddPage(src.Pages[p - 1]);
            RebuildForm(part);
            var path = Path.Combine(outputDir, PartName(stem, from, to));
            part.Options.CompressContentStreams = true;
            part.Save(path);
            parts.Add(new SplitPart(path, from, to));
        }
        return parts;
    }

    private static string PartName(string stem, int from, int to) => from == to ? $"{stem}_p{from}.pdf" : $"{stem}_p{from}-{to}.pdf";

    /// <summary>"1-3,5" → (1,3),(5,5); "each" → one range per page.</summary>
    public static List<(int From, int To)> ParseRanges(string? spec, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Trim().Equals("each", StringComparison.OrdinalIgnoreCase))
            return [.. Enumerable.Range(1, pageCount).Select(p => (p, p))];
        var ranges = new List<(int, int)>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(ends[0], out var a) || (ends.Length > 1 && !int.TryParse(ends[1], out _)) || ends.Length > 2)
                throw new PageAssemblyException($"bad page range '{part}' (use e.g. 1-3,5 or each)", "Err_BadRange", part);
            int b = ends.Length > 1 ? int.Parse(ends[1]) : a;
            if (a < 1 || b > pageCount || a > b) throw new PageAssemblyException($"page range '{part}' is outside 1-{pageCount}", "Err_RangeOutside", part, pageCount);
            ranges.Add((a, b));
        }
        return ranges;
    }

    public static int PageCount(string input) => Inspect(input).Pages;

    /// <summary>Page count, and whether the file's permissions let its pages be reused (no exception for protected files).</summary>
    public static (int Pages, bool MayAssemble) Inspect(string input)
    {
        using var d = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        return (d.PageCount, d.SecuritySettings.PermitAssembleDocument);
    }

    /// <summary>
    /// Opens for copying pages. PDFsharp needs no PDFium lock here, so the tools (which hold it)
    /// can call this. Encrypted files that forbid assembly are refused, not unlocked.
    /// </summary>
    private static PdfDocument Open(string input)
    {
        var d = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        if (!d.SecuritySettings.PermitAssembleDocument)
        {
            d.Dispose();
            throw new PageAssemblyException($"{Path.GetFileName(input)} does not allow its pages to be reused (its permissions forbid assembly); Washi does not remove protection", "Err_ProtectedReuse", Path.GetFileName(input));
        }
        return d;
    }

    // ------------------------------------------------------------------ forms
    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;

    /// <summary>Registers the copied widgets' fields in the document's AcroForm.</summary>
    internal static void RebuildForm(PdfDocument doc)
    {
        var roots = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var apFonts = new Dictionary<string, PdfItem>();
        foreach (var page in doc.Pages)
        {
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) continue;
            foreach (var a in annots.Elements)
            {
                if (Deref(a) is not PdfDictionary w || w.Elements.GetName("/Subtype") != "/Widget") continue;
                w.Elements.SetReference("/P", page);
                var root = w;
                for (int guard = 0; guard < 32 && Deref(root.Elements["/Parent"]) is PdfDictionary parent; guard++) root = parent;
                if (seen.Add(root)) roots.Add(root);
                CollectFonts(w, apFonts);
            }
        }
        if (roots.Count == 0) return;

        var form = AcroFormWriter.GetOrCreateAcroForm(doc);
        var fields = AcroFormWriter.GetOrCreateArray(doc, form, "/Fields");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (root.Elements.GetString("/T") is { Length: > 0 } t)
            {
                var name = t;
                for (int k = 2; !names.Add(name); k++) name = $"{t}_{k}";
                if (name != t) root.Elements["/T"] = new PdfString(name, PdfStringEncoding.Unicode);
            }
            fields.Elements.Add(root.Reference is { } r ? r : root);
        }
        var fonts = AcroFormWriter.EnsureFonts(doc, form);
        foreach (var (key, font) in apFonts)
            if (!fonts.Elements.ContainsKey(key)) fonts.Elements[key] = font;
    }

    /// <summary>Fonts used by a widget's appearance streams (so DA font names resolve after the copy).</summary>
    private static void CollectFonts(PdfDictionary widget, Dictionary<string, PdfItem> into)
    {
        if (Deref(widget.Elements["/AP"]) is not PdfDictionary ap) return;
        foreach (var state in ap.Elements.Values.Select(Deref))
        {
            var streams = state is PdfDictionary d && d.Stream is null ? d.Elements.Values.Select(Deref) : [state];
            foreach (var s in streams.OfType<PdfDictionary>())
                if (Deref(s.Elements.GetDictionary("/Resources")?.Elements["/Font"]) is PdfDictionary f)
                    foreach (var (key, value) in f.Elements)
                        if (value is not null) into.TryAdd(key, value);
        }
    }
}
