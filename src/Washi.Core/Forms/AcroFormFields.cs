// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Fonts;

namespace Washi.Core.Forms;

/// <summary>A field already in a PDF.</summary>
public sealed record FieldInfo(
    string Name,
    string Type,                 // text | checkbox | radio | choice | signature | button
    string Value,
    int Page,                    // 0-based, -1 when the widget is on no page
    PdfRect Rect,                // first widget, upright page, top-down points
    int MaxLen,
    bool Comb,
    bool Multiline,
    bool ReadOnly,
    IReadOnlyList<string> Options,   // choice options, or the on-states of a checkbox / radio group
    string Tooltip,
    FieldLogic? Logic = null,        // format, range and calculation (Acrobat's AF field actions)
    string Shown = "");              // the value as the field shows it ("1,234" for a stored "1234")

public sealed record FillResult(int Filled, IReadOnlyList<string> UnknownNames, IReadOnlyList<string> Notes);

/// <summary>
/// Reads and fills the AcroForm fields of a PDF (PDFsharp).
/// Filling writes the value and draws the field's appearance itself, so the result looks the
/// same in every viewer and does not depend on the viewer's font substitution.
/// </summary>
public static class AcroFormFields
{
    private const int FfReadOnly = 1, FfMultiline = 1 << 12, FfPushButton = 1 << 16, FfRadio = 1 << 15, FfComb = 1 << 24;

    // ------------------------------------------------------------------ list
    public static IReadOnlyList<FieldInfo> List(string path)
    {
        using var doc = PdfReader.Open(path, Pages.PdfPasswords.For(path) ?? "", PdfDocumentOpenMode.Import);
        return List(doc);
    }

    public static IReadOnlyList<FieldInfo> List(PdfDocument doc)
    {
        var result = new List<FieldInfo>();
        var pages = PageLookup(doc);
        foreach (var (field, name, widgets) in Terminals(doc))
        {
            string ft = Inherited(field, "/FT") is PdfName n ? n.Value : "";
            int ff = Inherited(field, "/Ff") is PdfInteger i ? i.Value : 0;
            var w0 = widgets.FirstOrDefault();
            int page = w0 is null ? -1 : PageOf(w0, pages, doc);
            var rect = w0 is null || page < 0 ? default : TopDown(doc.Pages[page], w0.Elements.GetArray("/Rect"));
            string type = ft switch
            {
                "/Tx" => "text",
                "/Ch" => "choice",
                "/Sig" => "signature",
                "/Btn" when (ff & FfPushButton) != 0 => "button",
                "/Btn" when (ff & FfRadio) != 0 => "radio",
                "/Btn" => "checkbox",
                _ => "unknown",
            };
            var options = type switch
            {
                "choice" => ChoiceOptions(field),
                "checkbox" or "radio" => ButtonStates(field, widgets).Select(st => st.Label).ToList(),
                _ => (IReadOnlyList<string>)[],
            };
            string value = Inherited(field, "/V") switch
            {
                PdfString s => s.Value,
                PdfStringObject so => so.Value,
                PdfName nm => nm.Value.TrimStart('/'),
                PdfArray a => string.Join(", ", a.Elements.Select(e => e is PdfString es ? es.Value : e.ToString())),
                _ => "",
            };
            if (type is "checkbox" or "radio" && ButtonStates(field, widgets).FirstOrDefault(st => st.State == value) is { Label: { } shown })
                value = shown;                                    // 0 / 1 with an /Opt list: show the option's label
            int maxLen = Inherited(field, "/MaxLen") is PdfInteger ml ? ml.Value : 0;
            var logic = type == "text" ? FieldScripts.Read(field) : null;
            result.Add(new FieldInfo(name, type, value, page, rect, maxLen,
                type == "text" && (ff & FfComb) != 0 && maxLen > 0, type == "text" && (ff & FfMultiline) != 0,
                (ff & FfReadOnly) != 0, options, field.Elements.GetString("/TU"), logic, logic?.Display(value) ?? value));
        }
        return result;
    }

    // ------------------------------------------------------------------ fill
    /// <param name="values">Field name → value. Checkboxes take true/false (or an on-state name), radio groups an option name.</param>
    /// <param name="embedFont">Optional font embedded and used for every filled text field.</param>
    public static FillResult Fill(string input, string output, IReadOnlyDictionary<string, string> values, OpenTypeFont? embedFont = null)
    {
        using var doc = Pages.PdfSecurity.OpenForChanges(input, Pages.PdfPasswords.For(input));   // a clear error for a locked form
        var result = Fill(doc, values, embedFont);
        doc.Options.CompressContentStreams = true;
        doc.Save(output);
        return result;
    }

    public static FillResult Fill(PdfDocument doc, IReadOnlyDictionary<string, string> values, OpenTypeFont? embedFont = null)
    {
        var form = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        var notes = new List<string>();
        if (form is null) return new FillResult(0, [.. values.Keys], ["the PDF has no form fields"]);
        var fonts = form.Elements.GetDictionary("/DR")?.Elements.GetDictionary("/Font");

        string? embeddedKey = null;
        if (embedFont is not null)
        {
            if (fonts is null)
            {
                var dr = form.Elements.GetDictionary("/DR") ?? new PdfDictionary(doc);
                form.Elements["/DR"] = dr;
                fonts = new PdfDictionary(doc);
                dr.Elements["/Font"] = fonts;
            }
            embeddedKey = new[] { AcroFormWriter.EmbeddedKey, "Wingdings2", "Wingdings3", "Webdings" }
                              .FirstOrDefault(k => !fonts.Elements.ContainsKey("/" + k)) ?? "WashiFont";
            fonts.Elements.SetReference("/" + embeddedKey, PdfFontEmbedder.Embed(doc, embedFont));
        }

        // field logic: values are checked and stored as Acrobat keeps them, then calculated fields are worked out
        var terminals = Terminals(doc).ToList();
        var logics = new Dictionary<string, FieldLogic>(StringComparer.Ordinal);
        foreach (var (field, name, _) in terminals)
            if (Inherited(field, "/FT") is PdfName { Value: "/Tx" } && FieldScripts.Read(field) is { } fl) logics[name] = fl;
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, raw) in values)
        {
            if (!logics.TryGetValue(name, out var fl)) { accepted[name] = raw; continue; }
            if (fl.Calc is not null) { notes.Add($"{name}: calculated from {string.Join(", ", fl.Calc.Sources)}; the value given was not used"); continue; }
            var (stored, error, _) = fl.Accept(raw);
            if (error is not null) notes.Add($"{name}: {error}; not filled");
            else accepted[name] = stored!;
        }
        var calculated = new HashSet<string>(StringComparer.Ordinal);
        var calcs = logics.Where(p => p.Value.Calc is not null).ToDictionary(p => p.Key, p => p.Value.Calc!);
        if (calcs.Count > 0)
        {
            var current = List(doc).GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            foreach (var (k, v) in accepted) current[k] = v;
            foreach (var name in FieldLogic.CalculationOrder(calcs))
            {
                var fmt = logics[name].Format;
                var result = FieldLogic.Calculate(calcs[name], current, fmt?.Separator ?? 0);
                if (fmt is { Kind: FieldFormatKind.Number or FieldFormatKind.Percent } && double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                    result = FieldLogic.Plain(Math.Round(r, Math.Clamp(fmt.Decimals + (fmt.Kind == FieldFormatKind.Percent ? 2 : 0), 0, 10)));
                if (current.GetValueOrDefault(name) == result) continue;
                current[name] = accepted[name] = result;
                calculated.Add(name);
            }
        }
        values = accepted;

        var encoders = new Dictionary<string, FontEncoder?>();
        int filled = 0;
        bool needAppearances = false;
        var known = new HashSet<string>();
        foreach (var (field, name, widgets) in terminals)
        {
            known.Add(name);
            if (!values.TryGetValue(name, out var value)) continue;
            var shown = logics.TryGetValue(name, out var logic) ? logic.Display(value) : value;
            string ft = Inherited(field, "/FT") is PdfName n ? n.Value : "";
            int ff = Inherited(field, "/Ff") is PdfInteger i ? i.Value : 0;
            if ((ff & FfReadOnly) != 0 && !calculated.Contains(name)) { notes.Add($"{name}: read-only, not changed"); continue; }

            switch (ft)
            {
                case "/Tx":
                {
                    field.Elements["/V"] = new PdfString(value, PdfStringEncoding.Unicode);
                    string da = Inherited(field, "/DA") switch
                    {
                        PdfString d => d.Value,
                        PdfStringObject d => d.Value,
                        _ => form.Elements.GetString("/DA"),
                    };
                    if (embeddedKey is not null)
                    {
                        da = ReplaceFont(da, embeddedKey);
                        foreach (var w in widgets.Append(field).Distinct()) w.Elements.SetString("/DA", da);
                    }
                    var (key, size, color) = ParseDa(da);
                    var enc = key is null ? null : Encoder(key, fonts, encoders);
                    if (enc is null)
                    {
                        needAppearances = true;
                        notes.Add($"{name}: font '{key}' cannot be drawn by Washi; the viewer will draw the value");
                        foreach (var w in widgets) w.Elements.Remove("/AP");
                    }
                    else
                    {
                        int maxLen = Inherited(field, "/MaxLen") is PdfInteger ml ? ml.Value : 0;
                        int q = Inherited(field, "/Q") is PdfInteger qi ? qi.Value : 0;
                        var missing = enc.Missing(shown);
                        if (missing.Length > 0) notes.Add($"{name}: the font has no glyph for '{missing}'");
                        foreach (var w in widgets)
                            SetAppearance(doc, w, TextAppearance(w, enc, key!, size, color, shown, q,
                                comb: (ff & FfComb) != 0 && maxLen > 0 ? maxLen : 0, multiline: (ff & FfMultiline) != 0), fonts!, key!);
                    }
                    filled++;
                    break;
                }
                case "/Btn" when (ff & FfPushButton) == 0:
                {
                    var pairs = ButtonStates(field, widgets);
                    var states = pairs.Select(p => p.State).ToList();
                    // a radio value may be the state name or the option's label (有 for state 0 with /Opt [(有) (無)])
                    string? Match(string v) => pairs.FirstOrDefault(p => p.State.Equals(v, StringComparison.OrdinalIgnoreCase)
                                                                         || p.Label.Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)).State;
                    string? on = (ff & FfRadio) != 0 ? Match(value)
                        : IsTrue(value) ? states.FirstOrDefault() ?? "Yes"
                        : Match(value);
                    if ((ff & FfRadio) != 0 && on is null && value.Trim() is not ("Off" or ""))   // Off or nothing: no choice
                    {
                        notes.Add($"{name}: '{value}' is not one of {string.Join(", ", pairs.Select(p => p.Label))}");
                        continue;
                    }
                    field.Elements.SetName("/V", "/" + (on ?? "Off"));
                    foreach (var w in widgets)
                        w.Elements.SetName("/AS", "/" + (on is not null && OnStates(w).Contains(on) ? on : "Off"));
                    filled++;
                    break;
                }
                case "/Ch":
                {
                    field.Elements["/V"] = new PdfString(value, PdfStringEncoding.Unicode);
                    // drawn like a one-line text field, so the chosen item shows the same everywhere
                    string da = Inherited(field, "/DA") switch
                    {
                        PdfString d => d.Value,
                        PdfStringObject d => d.Value,
                        _ => form.Elements.GetString("/DA"),
                    };
                    if (embeddedKey is not null)
                    {
                        da = ReplaceFont(da, embeddedKey);
                        foreach (var w in widgets.Append(field).Distinct()) w.Elements.SetString("/DA", da);
                    }
                    var (key, size, color) = ParseDa(da);
                    var enc = key is null || fonts is null ? null : Encoder(key, fonts, encoders);
                    if (enc is null) needAppearances = true;
                    else
                    {
                        int q = Inherited(field, "/Q") is PdfInteger qi ? qi.Value : 0;
                        foreach (var w in widgets)
                            SetAppearance(doc, w, TextAppearance(w, enc, key!, size, color, value, q, comb: 0, multiline: false), fonts!, key!);
                    }
                    filled++;
                    break;
                }
                default:
                    notes.Add($"{name}: {(ft == "/Sig" ? "signature" : "button")} fields are not filled");
                    break;
            }
        }
        if (needAppearances) form.Elements.SetBoolean("/NeedAppearances", true);
        foreach (var name in calculated) notes.Add($"{name}: calculated as {(logics[name].Display(values[name]))}");
        return new FillResult(filled - calculated.Count, values.Keys.Where(k => !known.Contains(k)).ToList(), notes);
    }

    /// <summary>
    /// Sets the AcroForm's /CO (calculation order) from the calculated fields it has. A calculation that depends on
    /// itself, or on a field the form does not have, is reported.
    /// </summary>
    internal static void RebuildCalculationOrder(PdfDocument doc, List<string> warnings)
    {
        var form = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        if (form is null) return;
        var all = Terminals(doc).ToList();
        var names = all.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var calculated = new List<(string, PdfDictionary, FieldCalc)>();
        foreach (var (field, name, _) in all)
            if (FieldScripts.Read(field) is { Calc: { } c })
            {
                foreach (var missing in c.Sources.Where(s => !names.Contains(s)))
                    warnings.Add($"field '{name}': its calculation uses '{missing}', which the form does not have");
                calculated.Add((name, field, c));
            }
        try { FieldScripts.WriteOrder(doc, form, calculated); }
        catch (Pages.PageAssemblyException e) { warnings.Add(e.Message); form.Elements.Remove("/CO"); }
    }

    private static bool IsTrue(string v) =>
        v.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1" or "x" or "✓" or "✔" or "checked";

    // ------------------------------------------------------------------ appearance
    private static string TextAppearance(PdfDictionary widget, FontEncoder enc, string key, double size, string color,
                                         string value, int q, int comb, bool multiline)
    {
        var r = widget.Elements.GetArray("/Rect")!;
        double w = Math.Abs(Num(r, 2) - Num(r, 0)), h = Math.Abs(Num(r, 3) - Num(r, 1));
        const double pad = 2;
        if (size <= 0) size = Math.Clamp(multiline ? 10 : h * 0.66, 4, 12);   // auto size
        // The size is the most the text gets: a value too long for its box is shrunk to fit instead of being cut
        // off (a viewer would scroll it, a printed form cannot), and a comb character never outgrows its cell.
        if (comb > 0) size = Math.Max(3, Math.Min(size, Math.Min(h - 2, w / comb) * 0.95));
        else if (!multiline)
        {
            double tw = enc.Width(value.Replace("\r", "").Replace('\n', ' '), 1);
            if (tw > 0) size = Math.Max(4, Math.Min(size, (w - 2 * pad) / tw));
            size = Math.Min(size, Math.Max(4, h - 2));
        }
        else
        {
            // wrapped text that still runs out of the box gets smaller until every line is inside
            while (size > 4 && Wrap(value, enc, size, w - 2 * pad).Count() * size * 1.15 > h - pad) size = Math.Max(4, size - 0.5);
        }
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"/Tx BMC q 1 1 {N(w - 2)} {N(h - 2)} re W n BT /{key} {N(size)} Tf {color} ");
        double baseline = (h - size * 0.72) / 2;

        if (comb > 0)
        {
            double cell = w / comb;
            int k = 0;
            foreach (var ch in Runes(value).Take(comb))
            {
                double cw = enc.Width(ch, size);
                sb.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {N(k * cell + (cell - cw) / 2)} {N(baseline)} Tm {enc.Hex(ch)} Tj ");
                k++;
            }
        }
        else if (multiline)
        {
            double lead = size * 1.15, y = h - pad - size;
            foreach (var line in Wrap(value, enc, size, w - 2 * pad))
            {
                if (y < -size) break;
                Line(line, X(line), y);
                y -= lead;
            }
        }
        else
        {
            var line = value.Replace("\r", "").Replace('\n', ' ');
            Line(line, X(line), baseline);
        }
        sb.Append("ET Q EMC");
        return sb.ToString();

        void Line(string line, double x, double y)
        {
            if (!enc.PlaceEachGlyph)
            {
                sb.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {N(x)} {N(y)} Tm {enc.Hex(line)} Tj ");
                return;
            }
            foreach (var ch in Runes(line))
            {
                sb.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {N(x)} {N(y)} Tm {enc.Hex(ch)} Tj ");
                x += enc.Width(ch, size);
            }
        }

        double X(string s) => q switch
        {
            1 => (w - enc.Width(s, size)) / 2,
            2 => w - pad - enc.Width(s, size),
            _ => pad,
        };
    }

    private static IEnumerable<string> Wrap(string text, FontEncoder enc, double size, double width)
    {
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var ch in Runes(para))
            {
                if (line.Length > 0 && enc.Width(line + ch, size) > width)
                {
                    // break at the last space for Latin text, anywhere for CJK
                    var s = line.ToString();
                    int sp = s.LastIndexOf(' ');
                    if (sp > 0 && ch != " ") { yield return s[..sp]; line.Clear().Append(s[(sp + 1)..]); }
                    else { yield return s; line.Clear(); }
                }
                line.Append(ch);
            }
            yield return line.ToString();
        }
    }

    private static IEnumerable<string> Runes(string s) => s.EnumerateRunes().Select(r => r.ToString());

    private static void SetAppearance(PdfDocument doc, PdfDictionary widget, string content, PdfDictionary fonts, string key)
    {
        var r = widget.Elements.GetArray("/Rect")!;
        double w = Math.Abs(Num(r, 2) - Num(r, 0)), h = Math.Abs(Num(r, 3) - Num(r, 1));
        var x = new PdfDictionary(doc);
        doc.Internals.AddObject(x);
        x.Elements.SetName("/Type", "/XObject");
        x.Elements.SetName("/Subtype", "/Form");
        x.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(Math.Round(w, 3)), new PdfReal(Math.Round(h, 3)));
        var fd = new PdfDictionary(doc);
        fd.Elements["/" + key] = fonts.Elements["/" + key];
        var res = new PdfDictionary(doc);
        res.Elements["/Font"] = fd;
        x.Elements["/Resources"] = res;
        x.CreateStream(Encoding.ASCII.GetBytes(content));
        var ap = new PdfDictionary(doc);
        ap.Elements.SetReference("/N", x);
        widget.Elements["/AP"] = ap;
    }

    private static (string? Key, double Size, string Color) ParseDa(string da)
    {
        var t = da.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int tf = Array.IndexOf(t, "Tf");
        if (tf < 2) return (null, 0, "0 g");
        double size = double.TryParse(t[tf - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var color = string.Join(' ', t.Skip(tf + 1));
        return (t[tf - 2].TrimStart('/'), size, color.Length > 0 ? color : "0 g");
    }

    private static string ReplaceFont(string da, string key)
    {
        var (_, size, color) = ParseDa(da);
        return $"/{key} {N(size)} Tf {color}";
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static double Num(PdfArray a, int i) => a.Elements.GetReal(i);

    // ------------------------------------------------------------------ fonts for appearances
    private abstract class FontEncoder
    {
        /// <summary>Place every glyph with its own matrix (fonts whose /W widths are only estimates).</summary>
        public virtual bool PlaceEachGlyph => false;
        public abstract string Hex(string text);
        public abstract double Width(string text, double size);
        public virtual string Missing(string text) => "";
    }

    /// <summary>Embedded Identity-H font: codes are CIDs from the font's own cmap.</summary>
    private sealed class EmbeddedEncoder(OpenTypeFont f) : FontEncoder
    {
        private int Gid(Rune r) => f.UnicodeToGlyph.TryGetValue(r.Value, out var g) ? g : 0;
        public override string Hex(string text) =>
            "<" + string.Concat(text.EnumerateRunes().Select(r => f.GlyphToCid[Gid(r)].ToString("X4"))) + ">";
        public override double Width(string text, double size) =>
            text.EnumerateRunes().Sum(r => f.Advances[Gid(r)]) * size / f.UnitsPerEm;
        public override string Missing(string text) =>
            string.Concat(text.EnumerateRunes().Where(r => !Rune.IsWhiteSpace(r) && Gid(r) == 0).Select(r => r.ToString()));
    }

    /// <summary>Referenced CJK font with a Unicode CMap (UniXX-UTF16-H / -UCS2-H): codes are UTF-16BE.</summary>
    private sealed class UnicodeCMapEncoder(string cmap) : FontEncoder
    {
        private readonly bool _korean = cmap.Contains("KS");
        public override bool PlaceEachGlyph => true;
        public override string Hex(string text) => "<" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";
        // Latin glyphs in CJK fonts are proportional: Helvetica widths are a close estimate
        public override double Width(string text, double size) =>
            text.EnumerateRunes().Sum(r => r.Value < 0x250 ? SimpleEncoder.Em(r.Value) : 1.0) * size;
        // Japanese and Chinese character collections have no Hangul
        public override string Missing(string text) => _korean ? "" :
            string.Concat(text.EnumerateRunes().Where(r => r.Value is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F)
                                               .Select(r => r.ToString()));
    }

    /// <summary>Simple font (Helvetica etc.) with WinAnsi-style single bytes.</summary>
    private sealed class SimpleEncoder : FontEncoder
    {
        private static readonly int[] Helvetica =
        [
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
            278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667,
            611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833,
            556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
        ];

        private static byte Code(char c) => c <= 0xFF ? (byte)c : (byte)'?';
        public static double Em(int c) => (c is >= ' ' and <= '~' ? Helvetica[c - ' '] : 556) / 1000.0;
        public override string Hex(string text) => "<" + Convert.ToHexString(text.Select(Code).ToArray()) + ">";
        public override double Width(string text, double size) =>
            text.Sum(c => c is >= ' ' and <= '~' ? Helvetica[c - ' '] : 556) * size / 1000;
        public override string Missing(string text) => new(text.Where(c => c > 0xFF).ToArray());
    }

    private static FontEncoder? Encoder(string key, PdfDictionary? fonts, Dictionary<string, FontEncoder?> cache)
    {
        if (cache.TryGetValue(key, out var e)) return e;
        e = null;
        var font = Deref(fonts?.Elements["/" + key]) as PdfDictionary;
        if (font is not null)
        {
            if (font.Elements.GetName("/Subtype") == "/Type0")
            {
                var encoding = font.Elements.GetName("/Encoding");
                if (encoding is "/Identity-H")
                {
                    var cid = Deref(font.Elements.GetArray("/DescendantFonts")?.Elements[0]) as PdfDictionary;
                    var fd = Deref(cid?.Elements["/FontDescriptor"]) as PdfDictionary;
                    var file = Deref(fd?.Elements["/FontFile2"] ?? fd?.Elements["/FontFile3"]) as PdfDictionary;
                    if (file?.Stream?.UnfilteredValue is { Length: > 0 } bytes)
                    {
                        try { e = new EmbeddedEncoder(OpenTypeFont.Parse(bytes)); }
                        catch (FontNotUsableException) { e = null; }   // e.g. a restricted font someone else embedded
                    }
                }
                else if (encoding is { } enc && (enc.Contains("UTF16") || enc.Contains("UCS2")))
                {
                    e = new UnicodeCMapEncoder(enc);
                }
            }
            else
            {
                e = new SimpleEncoder();
            }
        }
        cache[key] = e;
        return e;
    }

    // ------------------------------------------------------------------ structure
    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;

    internal static PdfItem? Inherited(PdfDictionary d, string key)
    {
        for (var cur = d; cur is not null; cur = Deref(cur.Elements["/Parent"]) as PdfDictionary)
            if (cur.Elements[key] is { } v) return Deref(v);
        return null;
    }

    /// <summary>Terminal fields (the ones that hold a value) with their full names and widgets.</summary>
    internal static IEnumerable<(PdfDictionary Field, string Name, List<PdfDictionary> Widgets)> Terminals(PdfDocument doc)
    {
        var form = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        if (form?.Elements.GetArray("/Fields") is not { } roots) yield break;
        var stack = new Stack<(PdfDictionary, string)>();
        foreach (var r in roots.Elements.Reverse())
            if (Deref(r) is PdfDictionary d) stack.Push((d, ""));
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var (node, parent) = stack.Pop();
            if (!seen.Add(node)) continue;
            string t = node.Elements.GetString("/T");
            string name = t.Length == 0 ? parent : parent.Length == 0 ? t : $"{parent}.{t}";
            var kids = node.Elements.GetArray("/Kids")?.Elements.Select(Deref).OfType<PdfDictionary>().ToList() ?? [];
            var childFields = kids.Where(k => k.Elements.ContainsKey("/T")).ToList();
            if (childFields.Count > 0)
            {
                for (int i = childFields.Count - 1; i >= 0; i--) stack.Push((childFields[i], name));
                continue;
            }
            var widgets = kids.Count > 0 ? kids : node.Elements.ContainsKey("/Rect") ? [node] : [];
            yield return (node, name, widgets);
        }
    }

    private static IEnumerable<string> OnStates(PdfDictionary widget) =>
        (Deref(widget.Elements.GetDictionary("/AP")?.Elements["/N"]) as PdfDictionary)?.Elements.Keys
            .Select(k => k.TrimStart('/')).Where(k => k != "Off") ?? [];

    /// <summary>A button field's on-states with what they stand for: the /Opt item when the parent has one (states
    /// 0, 1, 2 ... index it), else the state name itself.</summary>
    /// <summary>Each check box's and radio group's on-states and their labels (the label differs when the form has /Opt).</summary>
    internal static Dictionary<string, List<(string State, string Label)>> ButtonStateMap(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var map = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        foreach (var (field, name, widgets) in Terminals(doc))
            if (Inherited(field, "/FT") is PdfName { Value: "/Btn" }) map[name] = ButtonStates(field, widgets);
        return map;
    }

    private static List<(string State, string Label)> ButtonStates(PdfDictionary field, List<PdfDictionary> widgets)
    {
        var states = widgets.SelectMany(OnStates).Distinct().ToList();
        var opt = ChoiceOptions(field);
        return [.. states.Select(st => (st, opt.Count > 0 && int.TryParse(st, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < opt.Count
                                              ? opt[i] : st))];
    }

    private static List<string> ChoiceOptions(PdfDictionary field) =>
        (Inherited(field, "/Opt") as PdfArray ?? Deref(Inherited(field, "/Opt")) as PdfArray)?.Elements.Select(e => Deref(e) switch
        {
            PdfString s => s.Value,
            PdfStringObject so => so.Value,
            PdfArray pair when pair.Elements.Count > 1 => pair.Elements.GetString(1),
            _ => e.ToString() ?? "",
        }).ToList() ?? [];

    private static Dictionary<PdfDictionary, int> PageLookup(PdfDocument doc)
    {
        var map = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < doc.PageCount; i++) map[doc.Pages[i]] = i;
        return map;
    }

    private static int PageOf(PdfDictionary widget, Dictionary<PdfDictionary, int> pages, PdfDocument doc)
    {
        if (Deref(widget.Elements["/P"]) is PdfDictionary p && pages.TryGetValue(p, out var idx)) return idx;
        for (int i = 0; i < doc.PageCount; i++)
            if (doc.Pages[i].Elements.GetArray("/Annots")?.Elements.Any(a => ReferenceEquals(Deref(a), widget)) == true) return i;
        return -1;
    }

    /// <summary>Widget /Rect (PDF space) → upright page, top-down points (same mapping as PDFium).</summary>
    private static PdfRect TopDown(PdfPage page, PdfArray? rect)
    {
        if (rect is null || rect.Elements.Count < 4) return default;
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        double a = box.X1, b = box.Y1, c = box.X2, d = box.Y2;
        double x0 = Math.Min(Num(rect, 0), Num(rect, 2)), x1 = Math.Max(Num(rect, 0), Num(rect, 2));
        double y0 = Math.Min(Num(rect, 1), Num(rect, 3)), y1 = Math.Max(Num(rect, 1), Num(rect, 3));
        return (((page.Rotate % 360) + 360) % 360) switch
        {
            90 => new PdfRect(y0 - b, x0 - a, y1 - b, x1 - a),
            180 => new PdfRect(c - x1, y0 - b, c - x0, y1 - b),
            270 => new PdfRect(d - y1, c - x1, d - y0, c - x0),
            _ => new PdfRect(x0 - a, d - y1, x1 - a, d - y0),
        };
    }
}
