// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Washi.Core.Pages;

/// <summary>
/// A run of page labels: from page <paramref name="Start"/> (0-based) on, pages are numbered in
/// <paramref name="Style"/> ("1" decimal, "I" or "i" Roman, "A" or "a" letters, "" no number), after
/// <paramref name="Prefix"/>, counting from <paramref name="First"/>. A front matter in i, ii, iii and a body from 1
/// are two runs.
/// </summary>
public sealed record PageLabelRange(int Start, string Style, string Prefix = "", int First = 1);

/// <summary>
/// Page labels (PDF 32000 12.4.2): the numbers a viewer shows for pages instead of their position (i, ii, 1, 2, A-1 …).
/// Reads them, and writes a copy with a new set (or none).
/// </summary>
public static class PdfPageLabels
{
    private static readonly string[] Styles = ["1", "I", "i", "A", "a", ""];

    public static IReadOnlyList<PageLabelRange> Read(string path)
    {
        using var d = Open(path);
        return Read(d);
    }

    /// <summary>The label of every page (its number from 1 when the file has no labels).</summary>
    public static IReadOnlyList<string> Labels(string path)
    {
        using var d = Open(path);
        var ranges = Read(d);
        return [.. Enumerable.Range(0, d.PageCount).Select(i => Label(ranges, i))];
    }

    internal static IReadOnlyList<PageLabelRange> Read(PdfDocument d)
    {
        if (Deref(d.Internals.Catalog.Elements["/PageLabels"]) is not PdfDictionary tree) return [];
        var list = new List<PageLabelRange>();
        Collect(tree, list, 0);
        return [.. list.Where(r => r.Start >= 0 && r.Start < d.PageCount).GroupBy(r => r.Start).Select(g => g.First()).OrderBy(r => r.Start)];
    }

    private static void Collect(PdfDictionary node, List<PageLabelRange> into, int depth)
    {
        if (depth > 32) return;
        if (Deref(node.Elements["/Nums"]) is PdfArray nums)
            for (int i = 0; i + 1 < nums.Elements.Count; i += 2)
            {
                if (Deref(nums.Elements[i]) is not PdfInteger key || Deref(nums.Elements[i + 1]) is not PdfDictionary label) continue;
                var style = Deref(label.Elements["/S"]) is PdfName s ? s.Value switch { "/D" => "1", "/R" => "I", "/r" => "i", "/A" => "A", "/a" => "a", _ => "" } : "";
                var prefix = (Deref(label.Elements["/P"]) as PdfString)?.Value ?? "";
                int first = Deref(label.Elements["/St"]) is PdfInteger st && st.Value >= 1 ? st.Value : 1;
                into.Add(new PageLabelRange(key.Value, style, prefix, first));
            }
        if (Deref(node.Elements["/Kids"]) is PdfArray kids)
            foreach (var k in kids.Elements)
                if (Deref(k) is PdfDictionary kid) Collect(kid, into, depth + 1);
    }

    /// <summary>The label of page <paramref name="page"/> (0-based).</summary>
    public static string Label(IReadOnlyList<PageLabelRange> ranges, int page)
    {
        var r = ranges.LastOrDefault(x => x.Start <= page);
        if (r is null) return (page + 1).ToString(CultureInfo.InvariantCulture);
        int n = r.First + page - r.Start;
        return r.Prefix + Number(n, r.Style);
    }

    public static string Number(int n, string style) => style switch
    {
        "1" => n.ToString(CultureInfo.InvariantCulture),
        "I" => Roman(n),
        "i" => Roman(n).ToLowerInvariant(),
        "A" => Letters(n),
        "a" => Letters(n).ToLowerInvariant(),
        _ => "",
    };

    private static string Roman(int n)
    {
        if (n <= 0) return n.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var (v, s) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
            while (n >= v) { sb.Append(s); n -= v; }
        return sb.ToString();
    }

    /// <summary>A to Z, then AA to ZZ, then AAA … (the letter repeated), as PDF defines it.</summary>
    private static string Letters(int n) => n <= 0 ? "" : new string((char)('A' + (n - 1) % 26), (n - 1) / 26 + 1);

    /// <summary>
    /// Writes <paramref name="output"/> with <paramref name="ranges"/> as its page labels (an empty list removes them).
    /// Pages before the first run keep their plain numbers. The copy is read back to check it.
    /// </summary>
    public static void Write(string input, string output, IReadOnlyList<PageLabelRange> ranges)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        using (var d = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            foreach (var r in sorted)
            {
                if (r.Start < 0 || r.Start >= d.PageCount)
                    throw new PageAssemblyException($"page labels start on page {r.Start + 1}, but the PDF has {d.PageCount}", "Err_LabelPage", r.Start + 1, d.PageCount);
                if (!Styles.Contains(r.Style))
                    throw new PageAssemblyException($"'{r.Style}' is not a numbering style (1, I, i, A, a or none)", "Err_LabelStyle", r.Style);
                if (r.First < 1) throw new PageAssemblyException("page labels count from 1 or more", "Err_LabelFirst");
            }
            if (sorted.Select(r => r.Start).Distinct().Count() != sorted.Count)
                throw new PageAssemblyException("two runs of page labels start on the same page", "Err_LabelTwice");
            // the number tree must cover the first page; plain numbers until the first run
            if (sorted.Count > 0 && sorted[0].Start > 0) sorted.Insert(0, new PageLabelRange(0, "1"));

            var catalog = d.Internals.Catalog;
            if (sorted.Count == 0) catalog.Elements.Remove("/PageLabels");
            else
            {
                var nums = new PdfArray(d);
                foreach (var r in sorted)
                {
                    var label = new PdfDictionary(d);
                    label.Elements.SetName("/Type", "/PageLabel");
                    if (r.Style.Length > 0) label.Elements.SetName("/S", r.Style switch { "1" => "/D", "I" => "/R", "i" => "/r", "A" => "/A", _ => "/a" });
                    if (r.Prefix.Length > 0) label.Elements["/P"] = new PdfString(r.Prefix, PdfStringEncoding.Unicode);
                    if (r.First != 1) label.Elements.SetInteger("/St", r.First);
                    nums.Elements.Add(new PdfInteger(r.Start));
                    nums.Elements.Add(label);
                }
                var tree = new PdfDictionary(d);
                tree.Elements["/Nums"] = nums;
                catalog.Elements["/PageLabels"] = tree;
            }
            d.Save(output);
        }

        var back = Read(output);
        if (back.Count != sorted.Count || back.Zip(sorted).Any(p => p.First != p.Second))
        {
            File.Delete(output);
            throw new PageAssemblyException("the page labels did not read back as written; the copy was not kept", "Err_LabelCheck");
        }
    }

    /// <summary>
    /// Runs written as <c>page:style[:prefix[:first]]</c>, comma-separated, pages from 1: <c>1:i,5:1</c> numbers the
    /// first four pages i–iv and the rest from 1; <c>20:A:付録-</c> labels page 20 on as 付録-A, 付録-B … A style of
    /// "-" or nothing gives the prefix alone.
    /// </summary>
    public static IReadOnlyList<PageLabelRange> Parse(string spec)
    {
        var list = new List<PageLabelRange>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = part.Split(':');
            if (f.Length is < 2 or > 4 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page) || page < 1)
                throw new PageAssemblyException($"'{part}' is not page:style[:prefix[:first]]", "Err_LabelSpec", part);
            var style = f[1] == "-" ? "" : f[1];
            int first = 1;
            if (f.Length == 4 && (!int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out first) || first < 1))
                throw new PageAssemblyException($"'{part}' is not page:style[:prefix[:first]]", "Err_LabelSpec", part);
            list.Add(new PageLabelRange(page - 1, style, f.Length >= 3 ? f[2] : "", first));
        }
        return list;
    }

    public static string Format(IReadOnlyList<PageLabelRange> ranges) =>
        string.Join(",", ranges.Select(r => $"{r.Start + 1}:{(r.Style.Length == 0 ? "-" : r.Style)}"
                                            + (r.Prefix.Length > 0 || r.First != 1 ? ":" + r.Prefix : "")
                                            + (r.First != 1 ? ":" + r.First.ToString(CultureInfo.InvariantCulture) : "")));

    private static PdfDocument Open(string path)
    {
        try { return PdfReader.Open(path, PdfPasswords.For(path) ?? "", PdfDocumentOpenMode.Import); }
        catch (PdfReaderException e) when (PdfSecurity.IsPasswordProblem(e))
        {
            throw new PageAssemblyException($"{Path.GetFileName(path)} is protected with a password", "Err_NeedsPassword", Path.GetFileName(path));
        }
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
