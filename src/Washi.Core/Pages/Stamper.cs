// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Washi.Core.Forms;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>Bates numbers: <see cref="Prefix"/>, the number with at least <see cref="Digits"/> digits, <see cref="Suffix"/>.</summary>
public sealed record BatesNumbers(string Prefix = "", int Start = 1, int Digits = 6, string Suffix = "")
{
    public string Format(int n) => Prefix + n.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0') + Suffix;
}

/// <summary>Text across the page at an angle, see-through.</summary>
/// <param name="Size">Font size in points; 0 makes it as large as fits the page.</param>
/// <param name="Opacity">0 (invisible) to 1.</param>
/// <param name="Behind">Under the page's content instead of over it.</param>
public sealed record Watermark(string Text, double Size = 0, double Angle = 45, double Opacity = 0.3, int Color = 0x808080, bool Behind = false);

/// <summary>
/// What to add to the pages. Header and footer texts (left, centre, right) may hold {page}, {pages}, {date}, {file} and
/// {bates}. <see cref="Pages"/> is a page range (1-3,5) or null for every page.
/// </summary>
public sealed record StampOptions
{
    public string HeaderLeft { get; init; } = "";
    public string HeaderCenter { get; init; } = "";
    public string HeaderRight { get; init; } = "";
    public string FooterLeft { get; init; } = "";
    public string FooterCenter { get; init; } = "";
    public string FooterRight { get; init; } = "";
    public double FontSize { get; init; } = 9;
    /// <summary>From the page's edges to the header's top and the footer's baseline, and to the left and right texts.</summary>
    public double Margin { get; init; } = 28;
    public int Color { get; init; }
    public bool Serif { get; init; }
    public string? Pages { get; init; }
    /// <summary>The number {page} gives the first page stamped is its own page number; this adds to it (a document that continues another).</summary>
    public int NumberOffset { get; init; }
    public BatesNumbers? Bates { get; init; }
    public Watermark? Watermark { get; init; }
    public DateTime? Date { get; init; }
    /// <summary>Take out the headers and footers (when new ones are given) and the watermark (when a new one is) that Washi added before.</summary>
    public bool ReplaceExisting { get; init; }
    /// <summary>What {file} says, when the input is not the user's file (a temporary copy).</summary>
    internal string? FileName { get; init; }

    public bool HasText => new[] { HeaderLeft, HeaderCenter, HeaderRight, FooterLeft, FooterCenter, FooterRight }.Any(t => t.Trim().Length > 0);
    public bool IsEmpty => !HasText && string.IsNullOrWhiteSpace(Watermark?.Text);
}

/// <param name="Pages">How many pages were stamped.</param>
/// <param name="FirstBates">The first and last Bates numbers given, when there were any.</param>
public sealed record StampResult(int Pages, string? FirstBates, string? LastBates);

/// <summary>
/// Headers, footers, page numbers, Bates numbers and watermarks. Each page gets its own content stream before or after
/// the page's content, which is not touched; the marks are tagged as page artifacts (screen readers skip them). The copy
/// is rendered next to the original (nothing may change outside the marks) and its text read back before it is kept.
/// Callers hold <see cref="PdfiumDocument.Gate"/>.
/// </summary>
public static partial class PdfStamper
{
    /// <summary>The texts as they come out on one page.</summary>
    public static string Resolve(string template, int page, int pages, string file, DateTime date, string? bates) =>
        TokenRx().Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "page" => page.ToString(CultureInfo.InvariantCulture),
            "pages" => pages.ToString(CultureInfo.InvariantCulture),
            "file" => file,
            "date" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "bates" => bates ?? "",
            _ => m.Value,
        });

    [GeneratedRegex(@"\{(page|pages|file|date|bates)\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRx();

    private sealed record Placed(int Page, string Text, PdfRect Area);

    public static StampResult Apply(string input, string output, StampOptions o)
    {
        if (o.IsEmpty) throw new PageAssemblyException("nothing to add: give a header, a footer or a watermark", "Err_StampNothing");
        if (o.FontSize is < 4 or > 72) throw new PageAssemblyException("the text size must be 4 to 72 points", "Err_StampSize");
        if (o.Watermark is { } w0 && (w0.Opacity is <= 0 or > 1 || w0.Size is < 0 or > 400))
            throw new PageAssemblyException("the watermark's opacity must be above 0 and at most 1, its size 0 to 400 points", "Err_StampWatermark");
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));

        if (o.ReplaceExisting)
        {
            var (headerPages, watermarkPages) = Existing(input);
            bool headers = headerPages > 0 && o.HasText, watermarks = watermarkPages > 0 && !string.IsNullOrWhiteSpace(o.Watermark?.Text);
            if (headers || watermarks)
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"washi-stamp-{Guid.NewGuid():N}.pdf");
                try
                {
                    Remove(input, tmp, headers, watermarks);
                    return Apply(tmp, output, o with { ReplaceExisting = false, FileName = o.FileName ?? Path.GetFileName(input) });
                }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
            }
        }

        var placed = new List<Placed>();
        var date = o.Date ?? DateTime.Now;
        var file = o.FileName ?? Path.GetFileName(input);
        int stamped = 0;
        string? firstBates = null, lastBates = null;
        using (var doc = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            int count = doc.PageCount;
            var targets = PageAssembly.ParseRanges(o.Pages, count).SelectMany(r => Enumerable.Range(r.From, r.To - r.From + 1)).Distinct().Order().ToList();
            var fonts = new Dictionary<(FormScript, bool), PdfDictionary>();
            PdfDictionary FontFor(FormScript s, bool serif)
            {
                if (!fonts.TryGetValue((s, serif), out var f)) fonts[(s, serif)] = f = AcroFormWriter.TextFont(doc, s, serif);
                return f;
            }
            PdfDictionary? alpha = null;
            if (o.Watermark is { Text.Length: > 0 } wm && wm.Opacity < 1)
            {
                alpha = new PdfDictionary(doc);
                alpha.Elements.SetName("/Type", "/ExtGState");
                alpha.Elements["/ca"] = new PdfReal(wm.Opacity);
                alpha.Elements["/CA"] = new PdfReal(wm.Opacity);
                doc.Internals.AddObject(alpha);
            }

            foreach (var number in targets)
            {
                var page = doc.Pages[number - 1];
                var (width, height) = UprightSize(page);
                var toPdf = new Mx(1, 0, 0, -1, 0, height).Then(PdfRedactor.Upright(page));   // upright, y up → PDF space

                // our own copy of the page's resources, with the fonts and the transparency added
                var resources = PdfEraser.Resources(page) is { } pr ? (PdfDictionary)pr.Clone() : new PdfDictionary(doc);
                page.Elements["/Resources"] = resources;
                var fontDict = Sub(resources, "/Font");
                string Key(PdfDictionary dict, string prefix, PdfDictionary sub)
                {
                    foreach (var k in sub.Elements.Keys) if (sub.Elements[k] is PdfReference r && ReferenceEquals(r.Value, dict)) return k;
                    int n = 1;
                    string key;
                    do key = prefix + (n++).ToString(CultureInfo.InvariantCulture); while (sub.Elements.ContainsKey(key));
                    sub.Elements[key] = dict.Reference!;
                    return key;
                }

                var bates = o.Bates?.Format(o.Bates.Start + stamped);
                firstBates ??= bates;
                lastBates = bates ?? lastBates;
                stamped++;
                int shown = number + o.NumberOffset, total = count + o.NumberOffset;

                var front = new StringBuilder();
                var behind = new StringBuilder();
                string Cm() => string.Create(CultureInfo.InvariantCulture, $"{N(toPdf.A)} {N(toPdf.B)} {N(toPdf.C)} {N(toPdf.D)} {N(toPdf.E)} {N(toPdf.F)} cm");

                // header and footer
                var texts = new (string Template, int Row, int Column)[]
                {
                    (o.HeaderLeft, 0, 0), (o.HeaderCenter, 0, 1), (o.HeaderRight, 0, 2),
                    (o.FooterLeft, 1, 0), (o.FooterCenter, 1, 1), (o.FooterRight, 1, 2),
                };
                var line = new StringBuilder();
                foreach (var (template, row, column) in texts)
                {
                    var text = Resolve(template, shown, total, file, date, bates).Replace("\r", "").Replace("\n", " ").Trim();
                    if (text.Length == 0) continue;
                    var (font, pen, _) = PdfTextEditor.StandardPen(doc, text, o.Serif, sc => FontFor(sc, o.Serif));
                    var key = Key((PdfDictionary)Deref(font)!, "/WashiF", fontDict);
                    var (hex, em) = pen(text);
                    double w = em * o.FontSize;
                    double x = column switch { 0 => o.Margin, 1 => (width - w) / 2, _ => width - o.Margin - w };
                    double y = row == 0 ? height - o.Margin - o.FontSize * 0.75 : o.Margin;
                    line.Append(CultureInfo.InvariantCulture, $"BT {key} {N(o.FontSize)} Tf 1 0 0 1 {N(x)} {N(y)} Tm {hex} Tj ET\n");
                    placed.Add(new Placed(number - 1, text, new PdfRect(x, height - y - o.FontSize, x + w, height - y + o.FontSize * 0.35)));
                }
                if (line.Length > 0)
                {
                    var (r, g, b) = Rgb(o.Color);
                    front.Append(CultureInfo.InvariantCulture, $"/Artifact <</Type /Pagination>> BDC q {Cm()} 0 Tr 0 Tc 0 Tw 100 Tz 0 Ts {N(r)} {N(g)} {N(b)} rg\n")
                         .Append(line).Append("Q EMC\n");
                }

                // the watermark: centred, turned, as large as asked or as fits
                if (o.Watermark is { Text.Length: > 0 } wmk && wmk.Text.Trim() is { Length: > 0 } wtext)
                {
                    var (font, pen, _) = PdfTextEditor.StandardPen(doc, wtext, o.Serif, sc => FontFor(sc, o.Serif));
                    var key = Key((PdfDictionary)Deref(font)!, "/WashiF", fontDict);
                    var (hex, em) = pen(wtext);
                    double a = wmk.Angle * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
                    double size = wmk.Size > 0 ? wmk.Size
                        : Math.Min(200, Math.Min(0.85 * width / (em * Math.Abs(cos) + Math.Abs(sin)), 0.85 * height / (em * Math.Abs(sin) + Math.Abs(cos))));
                    double w = em * size;
                    // the text's middle at the page's middle
                    double ox = width / 2 - (cos * w / 2 - sin * size * 0.35), oy = height / 2 - (sin * w / 2 + cos * size * 0.35);
                    var gs = alpha is null ? "" : Key(alpha, "/WashiGS", Sub(resources, "/ExtGState")) + " gs ";
                    var (r, g, b) = Rgb(wmk.Color);
                    var target = wmk.Behind ? behind : front;
                    target.Append(CultureInfo.InvariantCulture,
                        $"/Artifact <</Type /Pagination /Subtype /Watermark>> BDC q {Cm()} {gs}0 Tr 0 Tc 0 Tw 100 Tz 0 Ts {N(r)} {N(g)} {N(b)} rg ")
                          .Append(CultureInfo.InvariantCulture, $"BT {key} {N(size)} Tf {N(cos)} {N(sin)} {N(-sin)} {N(cos)} {N(ox)} {N(oy)} Tm {hex} Tj ET Q EMC\n");
                    // its box on the upright page (y down)
                    var corners = new[] { (0.0, -0.3), (em, -0.3), (em, 1.0), (0.0, 1.0) }
                        .Select(c => (X: ox + (c.Item1 * cos - c.Item2 * sin) * size, Y: height - (oy + (c.Item1 * sin + c.Item2 * cos) * size))).ToList();
                    placed.Add(new Placed(number - 1, wtext, new PdfRect(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y))));
                }

                AddStreams(doc, page, behind.ToString(), front.ToString());
            }
            Washi.Core.Fonts.SubsetFont.FinishAll(doc);
            doc.Save(output);
        }

        try { Verify(input, output, placed); }
        catch
        {
            File.Delete(output);
            throw;
        }
        return new StampResult(stamped, firstBates, lastBates);
    }

    // ------------------------------------------------------------------ marks Washi added before

    [GeneratedRegex(@"(/WashiF\d+) [\d.]+ Tf(?:(?!Tj).)*?<([0-9A-Fa-f]*)> Tj", RegexOptions.Singleline)]
    private static partial Regex ShownRx();

    /// <summary>A page artifact's marked block: where it is in its stream, and whether it is a watermark.</summary>
    private sealed record Mark(int Start, int End, bool Watermark, bool Washi);

    /// <summary>
    /// The page artifacts of a content stream (headers, footers, page numbers, Bates numbers, watermarks, by Washi or any
    /// other program that tags them, as Acrobat and Word do): marked blocks tagged /Artifact whose properties say
    /// /Pagination or /Watermark, given inline or by name in the page's /Properties.
    /// </summary>
    private static List<Mark> Marks(byte[] bytes, PdfDictionary? properties)
    {
        var content = new ContentStream(bytes);
        var marks = new List<Mark>();
        var open = new Stack<(int Start, bool? Watermark)>();   // null: a block that is not a page artifact
        foreach (var op in content.Ops)
        {
            switch (op.Name)
            {
                case "BMC": open.Push((op.Start, null)); break;
                case "BDC":
                {
                    bool? kind = null;
                    if (op.Names.Count > 0 && op.Names[0] == "Artifact")
                    {
                        var props = Encoding.Latin1.GetString(bytes, op.Start, op.End - op.Start);
                        if (op.Names.Count > 1 && !props.Contains("<<", StringComparison.Ordinal)
                            && Deref(properties?.Elements["/" + op.Names[^1]]) is PdfDictionary named)
                            props = Washi.Core.Signatures.PdfSyntax.Write(named);
                        if (props.Contains("/Watermark", StringComparison.Ordinal)) kind = true;
                        else if (props.Contains("/Pagination", StringComparison.Ordinal)) kind = false;
                    }
                    open.Push((op.Start, kind));
                    break;
                }
                case "EMC" when open.Count > 0:
                {
                    var (start, kind) = open.Pop();
                    // the outermost artifact only (one inside another goes with it)
                    if (kind is { } wm && !open.Any(o => o.Watermark is not null))
                    {
                        int end = op.End;
                        if (end < bytes.Length && bytes[end] == (byte)'\n') end++;
                        bool washi = Encoding.Latin1.GetString(bytes, start, end - start).Contains("/WashiF", StringComparison.Ordinal);
                        marks.Add(new Mark(start, end, wm, washi));
                    }
                    break;
                }
            }
        }
        return marks;
    }

    /// <summary>How many pages carry headers or footers (page numbers, Bates numbers), and how many a watermark, tagged as page artifacts by Washi or another program.</summary>
    public static (int HeaderPages, int WatermarkPages) Existing(string path)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfPasswords.For(path) ?? "", PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        int headers = 0, watermarks = 0;
        foreach (var page in doc.Pages)
        {
            var props = Deref(PdfEraser.Resources(page)?.Elements["/Properties"]) as PdfDictionary;
            var marks = Streams(page).SelectMany(sd => Marks(sd.Stream!.UnfilteredValue, props)).ToList();
            if (marks.Any(m => !m.Watermark)) headers++;
            if (marks.Any(m => m.Watermark)) watermarks++;
        }
        return (headers, watermarks);
    }

    /// <summary>
    /// Takes out the headers and footers (<paramref name="headers"/>) and/or the watermarks tagged as page artifacts,
    /// whoever added them; nothing else in the pages' content changes. Returns how many marks went.
    /// </summary>
    public static int Remove(string input, string output, bool headers, bool watermarks)
    {
        if (!headers && !watermarks) throw new PageAssemblyException("nothing to remove", "Err_StampNothing");
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        int removed = 0;
        var texts = new List<(int Page, string Text)>();
        using (var doc = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            for (int p = 0; p < doc.PageCount; p++)
            {
                var page = doc.Pages[p];
                var resources = PdfEraser.Resources(page);
                var fonts = Deref(resources?.Elements["/Font"]) as PdfDictionary;
                var props = Deref(resources?.Elements["/Properties"]) as PdfDictionary;
                foreach (var sd in Streams(page))
                {
                    var bytes = sd.Stream!.UnfilteredValue;
                    var gone = Marks(bytes, props).Where(m => m.Watermark ? watermarks : headers).ToList();
                    if (gone.Count == 0) continue;
                    foreach (var m in gone)
                    {
                        removed++;
                        // the texts of Washi's own marks, to check they are gone (another program's fonts cannot be read back here)
                        if (m.Washi)
                            foreach (Match t in ShownRx().Matches(Encoding.Latin1.GetString(bytes, m.Start, m.End - m.Start)))
                                if (Decode(Deref(fonts?.Elements[t.Groups[1].Value]) as PdfDictionary, t.Groups[2].Value) is { Length: > 0 } text) texts.Add((p, text));
                    }
                    sd.Stream.Value = ContentRedactor.Apply(bytes, [.. gone.Select(m => (m.Start, m.End, ""))]);
                    sd.Elements.Remove("/Filter");
                    sd.Elements.Remove("/DecodeParms");
                }
            }
            if (removed == 0) throw new PageAssemblyException("there are no headers, footers or watermarks tagged as page artifacts", "Err_StampNone");
            doc.Save(output);
        }

        // proof: none is left, the pages' text only lost (never gained) characters, and Washi's own texts are gone
        try
        {
            var left = Existing(output);
            if (headers && left.HeaderPages > 0 || watermarks && left.WatermarkPages > 0)
                throw new PageAssemblyException("some marks were still there; the copy was not kept", "Err_StampRemoveCheck");
            using var a = PdfiumDocument.Open(input);
            using var b = PdfiumDocument.Open(output);
            for (int p = 0; p < Math.Min(a.PageCount, 50); p++)
            {
                string ta = Norm(a.GetPageText(p)), tb = Norm(b.GetPageText(p));
                if (tb.Length > ta.Length)
                    throw new PageAssemblyException($"page {p + 1} gained text; the copy was not kept", "Err_StampRemoveCheck");
            }
            foreach (var g in texts.GroupBy(t => t.Page).Take(20))
            {
                string ta = Norm(a.GetPageText(g.Key)), tb = Norm(b.GetPageText(g.Key));
                foreach (var (_, text) in g.DistinctBy(t => t.Text))
                {
                    var n = Norm(text);
                    if (n.Length > 0 && Count(tb, n) > Math.Max(0, Count(ta, n) - g.Count(t => t.Text == text)))
                        throw new PageAssemblyException($"“{text}” was still on page {g.Key + 1}; the copy was not kept", "Err_StampRemoveCheck");
                }
            }
        }
        catch
        {
            File.Delete(output);
            throw;
        }
        return removed;
    }

    private static int Count(string hay, string needle)
    {
        int n = 0;
        for (int i = hay.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>A shown string in one of Washi's fonts: UTF-16 for a CJK font, Windows-1252 for Helvetica or Times.</summary>
    private static string? Decode(PdfDictionary? font, string hex)
    {
        if (hex.Length % 2 != 0) return null;
        var bytes = Convert.FromHexString(hex);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (font?.Elements.GetName("/Subtype") == "/Type0")
            return font.Elements.GetName("/Encoding") == "/Identity-H" ? null : Encoding.BigEndianUnicode.GetString(bytes);   // an embedded subset's codes are glyphs
        return Encoding.GetEncoding(1252).GetString(bytes);
    }

    /// <summary>The page's content streams.</summary>
    private static IEnumerable<PdfDictionary> Streams(PdfPage page)
    {
        var items = page.Elements["/Contents"] switch
        {
            PdfReference { Value: PdfArray arr } => arr.Elements.ToList(),
            PdfArray arr => arr.Elements.ToList(),
            PdfItem one => [one],
            _ => [],
        };
        foreach (var i in items)
            if (Deref(i) is PdfDictionary { Stream: not null } sd) yield return sd;
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;

    /// <summary>The page's own content stays as it is: a stream before it (what goes behind, then q) and one after (Q, then the marks).</summary>
    internal static void AddStreams(PdfDocument doc, PdfPage page, string behind, string front)
    {
        PdfReference Stream(string content)
        {
            var s = new PdfDictionary(doc);
            s.CreateStream(Encoding.Latin1.GetBytes(content));
            doc.Internals.AddObject(s);
            return s.Reference!;
        }
        var items = new List<PdfItem>();
        switch (page.Elements["/Contents"])
        {
            case PdfReference { Value: PdfArray arr }: items.AddRange(arr.Elements); break;
            case PdfArray arr: items.AddRange(arr.Elements); break;
            case PdfReference r: items.Add(r); break;
        }
        var contents = new PdfArray(doc);
        contents.Elements.Add(Stream(behind + "q\n"));
        foreach (var i in items) contents.Elements.Add(i);
        contents.Elements.Add(Stream("\nQ\n" + front));
        page.Elements["/Contents"] = contents;
    }

    private static void Verify(string before, string after, List<Placed> placed)
    {
        using var a = PdfiumDocument.Open(before);
        using var b = PdfiumDocument.Open(after);
        var pages = placed.Select(p => p.Page).Distinct().Order().ToList();
        // a sample: the first pages, the last, and some between (a long document need not be rendered whole)
        var sample = pages.Count <= 8 ? pages : pages.Take(3).Concat(pages.Skip(pages.Count / 2).Take(2)).Concat(pages.TakeLast(3)).Distinct().ToList();
        foreach (var page in sample)
        {
            const double scale = 1.5;
            var ia = a.RenderPage(page, scale);
            var ib = b.RenderPage(page, scale);
            if (ia.Width != ib.Width || ia.Height != ib.Height)
                throw new PageAssemblyException($"page {page + 1} changed size; the copy was not kept", "Err_StampChanged", page + 1);
            var ok = new bool[ia.Width * ia.Height];
            foreach (var r in placed.Where(p => p.Page == page).Select(p => p.Area))
            {
                int x0 = Math.Max(0, (int)Math.Floor(r.X0 * scale) - 3), x1 = Math.Min(ia.Width - 1, (int)Math.Ceiling(r.X1 * scale) + 3);
                int y0 = Math.Max(0, (int)Math.Floor(r.Y0 * scale) - 3), y1 = Math.Min(ia.Height - 1, (int)Math.Ceiling(r.Y1 * scale) + 3);
                for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) ok[y * ia.Width + x] = true;
            }
            int changed = 0;
            for (int i = 0, q = 0; i < ok.Length; i++, q += 3)
                if (!ok[i] && Math.Abs(ia.Pixels[q] - ib.Pixels[q]) + Math.Abs(ia.Pixels[q + 1] - ib.Pixels[q + 1]) + Math.Abs(ia.Pixels[q + 2] - ib.Pixels[q + 2]) > 24)
                    changed++;
            if (changed > 0)
                throw new PageAssemblyException($"page {page + 1} changed outside the header, footer and watermark ({changed} pixels); the copy was not kept",
                                                "Err_StampChanged", page + 1);
            // every text is there (a watermark behind the page may be covered, but its text is still in the page)
            var text = Norm(b.GetPageText(page));
            foreach (var p in placed.Where(p => p.Page == page))
                if (!text.Contains(Norm(p.Text), StringComparison.Ordinal))
                    throw new PageAssemblyException($"“{p.Text}” did not read back on page {page + 1}; the copy was not kept", "Err_StampCheck", p.Text, page + 1);
        }
    }

    private static string Norm(string s) => Regex.Replace(s.Normalize(NormalizationForm.FormKC), @"\s+", "");

    internal static (double Width, double Height) UprightSize(PdfPage page)
    {
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        double w = Math.Abs(box.X2 - box.X1), h = Math.Abs(box.Y2 - box.Y1);
        return (((page.Rotate % 360) + 360) % 360) is 90 or 270 ? (h, w) : (w, h);
    }

    private static PdfDictionary Sub(PdfDictionary resources, string key)
    {
        var sub = resources.Elements[key] is PdfReference { Value: PdfDictionary d } ? (PdfDictionary)d.Clone()
                : resources.Elements[key] is PdfDictionary dd ? (PdfDictionary)dd.Clone() : new PdfDictionary(resources.Owner);
        resources.Elements[key] = sub;
        return sub;
    }

    private static (double R, double G, double B) Rgb(int c) => ((c >> 16 & 255) / 255.0, (c >> 8 & 255) / 255.0, (c & 255) / 255.0);

    private static string N(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
