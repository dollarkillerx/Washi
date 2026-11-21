// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Washi.Core.Forms;

public sealed record AcroFormResult(int FieldsWritten, int PagesStraightened, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes form fields into an existing PDF (PDFsharp, MIT).
///
/// Coordinates come in on the upright page, as the user sees it. Rotated pages are
/// straightened first (content wrapped in a matrix, /Rotate 0), so every field is upright
/// and typed text runs the right way in every viewer.
///
/// Fonts: text fields ask for a well-known, non-embedded font chosen by
/// <see cref="FormScript"/> (KozMin for Japanese, STSong / MSung for Chinese,
/// HYSMyeongJo for Korean, Helvetica for Latin). Viewers substitute a local font, so
/// whatever the user types renders without shipping megabytes of CJK glyphs. All five
/// fonts are put in the form's resources, so an editor can switch a field later.
/// </summary>
public static class AcroFormWriter
{
    private const int FlagMultiline = 1 << 12;
    private const int FlagDoNotScroll = 1 << 23;
    private const int FlagComb = 1 << 24;
    private const int AnnotPrint = 4;

    private sealed record FontSpec(string Key, string BaseFont, string Encoding, string Ordering, int Supplement, string Widths);

    private static readonly Dictionary<FormScript, FontSpec> CidFonts = new()
    {
        [FormScript.Japanese] = new("KozMin", "KozMinPr6N-Regular", "UniJIS-UTF16-H", "Japan1", 6, "1 632 500"),
        [FormScript.ChineseSimplified] = new("STSong", "STSong-Light", "UniGB-UTF16-H", "GB1", 4, "1 95 500"),
        [FormScript.ChineseTraditional] = new("MSung", "MSung-Light", "UniCNS-UTF16-H", "CNS1", 4, "1 95 500"),
        [FormScript.Korean] = new("HYSMyeongJo", "HYSMyeongJo-Medium", "UniKS-UTF16-H", "Korea1", 2, "1 95 500"),
    };

    /// <summary>
    /// Resource name of an embedded font in the form's /DR (only a label: viewers use the font
    /// object it points to, whose /BaseFont is the font's real PostScript name).
    ///
    /// Why this label: when PDFium (Chrome, Edge) edits a field, it files every CJK ideograph
    /// under the Simplified Chinese charset and only tries the field's own font if that font's
    /// charset matches. An embedded font gets the ANSI charset, so it is skipped and a GB2312
    /// system font is used instead, which has no 燁, 陳 or 臺. PDFium gives the "symbol"
    /// charset to a default font whose resource name is "Wingdings", and tries a symbol-charset
    /// font first for every character. With this name PDFium therefore types with the embedded
    /// font. Verified by typing into generated forms through PDFium's form API (tests and
    /// README "Fonts").
    /// </summary>
    public const string EmbeddedKey = "Wingdings";

    public static string FontKey(FormScript script) => script == FormScript.Latin ? "Helv" : CidFonts[script].Key;

    /// <summary>Adds <paramref name="fields"/> to <paramref name="inputPath"/> and saves to <paramref name="outputPath"/>.</summary>
    public static AcroFormResult Write(string inputPath, string outputPath, IReadOnlyList<FormField> fields, FormScript script,
                                       Fonts.OpenTypeFont? embedFont = null, string? password = null)
    {
        using var doc = password is null
            ? PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify)
            : PdfReader.Open(inputPath, password, PdfDocumentOpenMode.Modify);
        var result = Write(doc, fields, script, embedFont);
        doc.Options.CompressContentStreams = true;
        doc.Save(outputPath);
        return result;
    }

    /// <param name="embedFont">
    /// Optional font embedded whole and used by every text field, so viewers that substitute
    /// fonts (PDFium: Chrome, Edge) can still show rare characters. Adds the font's size to the file.
    /// </param>
    public static AcroFormResult Write(PdfDocument doc, IReadOnlyList<FormField> fields, FormScript script,
                                       Fonts.OpenTypeFont? embedFont = null)
    {
        var warnings = new List<string>();
        int straightened = 0;
        var heights = new double[doc.PageCount];
        for (int i = 0; i < doc.PageCount; i++)
        {
            if (Straighten(doc.Pages[i], out var warn)) straightened++;
            if (warn is not null) warnings.Add($"page {i + 1}: {warn}");
            heights[i] = Box(doc.Pages[i]).Height;
        }

        var form = GetOrCreateAcroForm(doc);
        var fonts = EnsureFonts(doc, form);
        var fieldArray = GetOrCreateArray(doc, form, "/Fields");
        var used = CollectFieldNames(fieldArray);
        string daFont = FontKey(script);
        if (embedFont is not null)
        {
            // a PDF may already hold a real Wingdings: take the first symbol-charset name that is free
            daFont = new[] { EmbeddedKey, "Wingdings2", "Wingdings3", "Webdings" }
                         .FirstOrDefault(k => !fonts.Elements.ContainsKey("/" + k)) ?? "WashiFont";
            fonts.Elements.SetReference("/" + daFont, Fonts.PdfFontEmbedder.Embed(doc, embedFont));
        }

        foreach (var f in fields)
        {
            if (f.Page < 0 || f.Page >= doc.PageCount)
            {
                warnings.Add($"field '{f.Name}': page {f.Page + 1} does not exist, skipped");
                continue;
            }
            var page = doc.Pages[f.Page];
            double h = heights[f.Page];
            var r = f.Rect;
            double w = r.Width, fh = r.Height;
            if (w <= 0 || fh <= 0) continue;

            var widget = new PdfDictionary(doc);
            doc.Internals.AddObject(widget);
            widget.Elements.SetName("/Type", "/Annot");
            widget.Elements.SetName("/Subtype", "/Widget");
            widget.Elements["/T"] = new PdfString(UniqueName(f, used), PdfStringEncoding.Unicode);
            if (f.Tooltip.Length > 0) widget.Elements["/TU"] = new PdfString(f.Tooltip, PdfStringEncoding.Unicode);
            widget.Elements.SetInteger("/F", AnnotPrint);
            widget.Elements.SetReference("/P", page);
            widget.Elements["/Rect"] = Numbers(doc, r.X0, h - r.Y1, r.X1, h - r.Y0);

            if (f.Kind == FormFieldKind.Checkbox)
            {
                double fs = fh * 0.9;
                var yes = FormXObject(doc, w, fh, fonts, "ZaDb",
                    $"q BT /ZaDb {N(fs)} Tf 0 g {N((w - fs * 0.85) / 2)} {N((fh - fs * 0.75) / 2)} Td (4) Tj ET Q");
                var off = FormXObject(doc, w, fh, null, null, "");
                widget.Elements.SetName("/FT", "/Btn");
                widget.Elements.SetString("/DA", "/ZaDb 0 Tf 0 g");
                var mk = new PdfDictionary(doc);
                mk.Elements.SetString("/CA", "4");
                widget.Elements["/MK"] = mk;
                widget.Elements.SetName("/V", "/Off");
                widget.Elements.SetName("/AS", "/Off");
                var states = new PdfDictionary(doc);
                states.Elements.SetReference("/Yes", yes);
                states.Elements.SetReference("/Off", off);
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = states;
                widget.Elements["/AP"] = ap;
            }
            else
            {
                double size = Math.Round(Math.Clamp(fh * 0.6, 5, 10), 1);
                widget.Elements.SetName("/FT", "/Tx");
                widget.Elements.SetString("/DA", $"/{daFont} {N(size)} Tf 0 g");
                widget.Elements.SetInteger("/Q", Math.Clamp(f.Align, 0, 2));
                if (f.Kind == FormFieldKind.Comb && f.MaxLen > 0)
                {
                    widget.Elements.SetInteger("/Ff", FlagComb | FlagDoNotScroll);
                    widget.Elements.SetInteger("/MaxLen", f.MaxLen);
                }
                else if (f.Kind == FormFieldKind.Text && fh > 24)
                {
                    widget.Elements.SetInteger("/Ff", FlagMultiline);
                }
                widget.Elements["/MK"] = new PdfDictionary(doc);
                var ap = new PdfDictionary(doc);
                ap.Elements.SetReference("/N", FormXObject(doc, w, fh, null, null, "/Tx BMC EMC"));
                widget.Elements["/AP"] = ap;
            }

            GetOrCreateArray(doc, page, "/Annots").Elements.Add(widget.Reference!);
            fieldArray.Elements.Add(widget.Reference!);
        }

        return new AcroFormResult(fields.Count, straightened, warnings);
    }

    // ------------------------------------------------------------------ rotation
    /// <summary>The box PDFium maps device coordinates against: CropBox if set, else MediaBox.</summary>
    private static PdfRectangle Box(PdfPage page) =>
        page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;

    /// <summary>Wraps the content in a matrix so the page is upright with /Rotate 0 and its box at the origin.</summary>
    internal static bool Straighten(PdfPage page, out string? warning)
    {
        warning = null;
        int rot = ((page.Rotate % 360) + 360) % 360;
        var box = Box(page);
        double a = box.X1, b = box.Y1, c = box.X2, d = box.Y2;
        if (rot == 0 && a == 0 && b == 0) return false;

        string m = rot switch
        {
            0 => $"1 0 0 1 {N(-a)} {N(-b)}",
            90 => $"0 -1 1 0 {N(-b)} {N(c)}",
            180 => $"-1 0 0 -1 {N(c)} {N(d)}",
            270 => $"0 1 -1 0 {N(d)} {N(-a)}",
            _ => throw new NotSupportedException($"/Rotate {page.Rotate}"),
        };
        if (page.Elements.GetArray("/Annots") is { Elements.Count: > 0 })
            warning = "existing annotations were not moved with the page content";

        SetStream(page.Contents.PrependContent(), Encoding.ASCII.GetBytes($"q {m} cm\n"));
        SetStream(page.Contents.AppendContent(), Encoding.ASCII.GetBytes("\nQ\n"));

        double w = c - a, h = d - b;
        var (nw, nh) = rot is 90 or 270 ? (h, w) : (w, h);
        var upright = new PdfRectangle(new PdfSharp.Drawing.XPoint(0, 0), new PdfSharp.Drawing.XPoint(nw, nh));
        page.Elements.Remove("/Rotate");
        page.MediaBox = upright;
        // PDFsharp parses page boxes as PdfRectangle, not PdfArray: test for the key itself.
        foreach (var key in new[] { "/CropBox", "/TrimBox", "/BleedBox", "/ArtBox" })
            if (page.Elements.ContainsKey(key)) page.Elements[key] = upright;
        return true;
    }

    private static void SetStream(PdfDictionary content, byte[] bytes)
    {
        if (content.Stream is null) content.CreateStream(bytes);
        else content.Stream.Value = bytes;
    }

    // ------------------------------------------------------------------ objects
    private static PdfDictionary GetOrCreateAcroForm(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements.GetDictionary("/AcroForm") is { } existing) return existing;
        var form = new PdfDictionary(doc);
        doc.Internals.AddObject(form);
        catalog.Elements.SetReference("/AcroForm", form);
        return form;
    }

    private static PdfArray GetOrCreateArray(PdfDocument doc, PdfDictionary owner, string key)
    {
        if (owner.Elements.GetArray(key) is { } arr) return arr;
        var created = new PdfArray(doc);
        owner.Elements[key] = created;
        return created;
    }

    private static PdfDictionary EnsureFonts(PdfDocument doc, PdfDictionary form)
    {
        var dr = form.Elements.GetDictionary("/DR");
        if (dr is null) { dr = new PdfDictionary(doc); form.Elements["/DR"] = dr; }
        var fonts = dr.Elements.GetDictionary("/Font");
        if (fonts is null) { fonts = new PdfDictionary(doc); dr.Elements["/Font"] = fonts; }

        void Add(string key, Func<PdfDictionary> make)
        {
            if (fonts.Elements.ContainsKey("/" + key)) return;
            var f = make();
            doc.Internals.AddObject(f);
            fonts.Elements.SetReference("/" + key, f);
        }

        Add("Helv", () => Simple(doc, "Helvetica", withEncoding: true));
        Add("ZaDb", () => Simple(doc, "ZapfDingbats", withEncoding: false));
        foreach (var spec in CidFonts.Values) Add(spec.Key, () => Cid(doc, spec));
        if (!form.Elements.ContainsKey("/DA")) form.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
        return fonts;
    }

    private static PdfDictionary Simple(PdfDocument doc, string baseFont, bool withEncoding)
    {
        var f = new PdfDictionary(doc);
        f.Elements.SetName("/Type", "/Font");
        f.Elements.SetName("/Subtype", "/Type1");
        f.Elements.SetName("/BaseFont", "/" + baseFont);
        if (withEncoding) f.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        return f;
    }

    private static PdfDictionary Cid(PdfDocument doc, FontSpec s)
    {
        var fd = new PdfDictionary(doc);
        fd.Elements.SetName("/Type", "/FontDescriptor");
        fd.Elements.SetName("/FontName", "/" + s.BaseFont);
        fd.Elements.SetInteger("/Flags", 6);
        fd.Elements["/FontBBox"] = Numbers(doc, -200, -331, 1100, 1000);
        fd.Elements.SetInteger("/ItalicAngle", 0);
        fd.Elements.SetInteger("/Ascent", 880);
        fd.Elements.SetInteger("/Descent", -120);
        fd.Elements.SetInteger("/CapHeight", 740);
        fd.Elements.SetInteger("/StemV", 80);

        var info = new PdfDictionary(doc);
        info.Elements["/Registry"] = new PdfString("Adobe");
        info.Elements["/Ordering"] = new PdfString(s.Ordering);
        info.Elements.SetInteger("/Supplement", s.Supplement);

        var w = s.Widths.Split(' ').Select(int.Parse).ToArray();
        var cid = new PdfDictionary(doc);
        cid.Elements.SetName("/Type", "/Font");
        cid.Elements.SetName("/Subtype", "/CIDFontType0");
        cid.Elements.SetName("/BaseFont", "/" + s.BaseFont);
        cid.Elements["/CIDSystemInfo"] = info;
        cid.Elements["/FontDescriptor"] = fd;
        cid.Elements.SetInteger("/DW", 1000);
        cid.Elements["/W"] = new PdfArray(doc, new PdfInteger(w[0]), new PdfInteger(w[1]), new PdfInteger(w[2]));

        var t0 = new PdfDictionary(doc);
        t0.Elements.SetName("/Type", "/Font");
        t0.Elements.SetName("/Subtype", "/Type0");
        t0.Elements.SetName("/BaseFont", "/" + s.BaseFont);
        t0.Elements.SetName("/Encoding", "/" + s.Encoding);
        t0.Elements["/DescendantFonts"] = new PdfArray(doc, cid);
        return t0;
    }

    private static PdfDictionary FormXObject(PdfDocument doc, double w, double h, PdfDictionary? fonts, string? fontKey, string content)
    {
        var x = new PdfDictionary(doc);
        doc.Internals.AddObject(x);
        x.Elements.SetName("/Type", "/XObject");
        x.Elements.SetName("/Subtype", "/Form");
        x.Elements["/BBox"] = Numbers(doc, 0, 0, w, h);
        var res = new PdfDictionary(doc);
        if (fonts is not null && fontKey is not null && fonts.Elements["/" + fontKey] is { } fontItem)
        {
            var fd = new PdfDictionary(doc);
            fd.Elements["/" + fontKey] = fontItem;
            res.Elements["/Font"] = fd;
        }
        x.Elements["/Resources"] = res;
        x.CreateStream(Encoding.ASCII.GetBytes(content));
        return x;
    }

    private static PdfArray Numbers(PdfDocument doc, params double[] v) =>
        new(doc, v.Select(d => (PdfItem)new PdfReal(Math.Round(d, 3))).ToArray());

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ names
    private static HashSet<string> CollectFieldNames(PdfArray fields)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in fields.Elements)
        {
            var dict = item is PdfReference r ? r.Value as PdfDictionary : item as PdfDictionary;
            if (dict?.Elements.GetString("/T") is { Length: > 0 } t) names.Add(t);
        }
        return names;
    }

    /// <summary>A '.' in a partial field name would create a field hierarchy, so it is replaced.</summary>
    private static string UniqueName(FormField f, HashSet<string> used)
    {
        var baseName = f.Name.Replace('.', '・').Trim();
        if (baseName.Length == 0) baseName = $"p{f.Page + 1}_{f.Kind.ToString().ToLowerInvariant()}";
        var name = baseName;
        if (!used.Add(name))
        {
            name = $"{baseName}_p{f.Page + 1}";
            for (int k = 2; !used.Add(name); k++) name = $"{baseName}_p{f.Page + 1}_{k}";
        }
        return name;
    }
}
