// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Forms;

namespace Washi.Core.Pages;

public sealed class PageAssemblyException(string message) : Exception(message);

public sealed record SplitPart(string Path, int FirstPage, int LastPage);

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
        if (inputs.Count == 0) throw new PageAssemblyException("nothing to merge");
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
                throw new PageAssemblyException($"page range {from}-{to} is outside 1-{src.PageCount}");
            var target = Path.Combine(outputDir, PartName(stem, from, to));
            if (File.Exists(target)) throw new PageAssemblyException($"{target} already exists; Washi never overwrites files");
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
                throw new PageAssemblyException($"bad page range '{part}' (use e.g. 1-3,5 or each)");
            int b = ends.Length > 1 ? int.Parse(ends[1]) : a;
            if (a < 1 || b > pageCount || a > b) throw new PageAssemblyException($"page range '{part}' is outside 1-{pageCount}");
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
            throw new PageAssemblyException($"{Path.GetFileName(input)} does not allow its pages to be reused (its permissions forbid assembly); Washi does not remove protection");
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
