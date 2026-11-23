// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Washi.Core.Forms;

namespace Washi.Core.Pages;

public static partial class PdfTextEditor
{
    /// <summary>
    /// The font to draw <paramref name="text"/> with, and the text encoded for it: the font the old text used when it
    /// has a glyph for every character (found through its ToUnicode map, or its encoding when it is not a subset),
    /// otherwise a standard font of the same kind (serif or not; Latin or CJK). Width in em.
    /// </summary>
    private static (PdfItem Font, string Hex, double Width, bool Kept, string Name) Encode(PdfDocument doc, PdfDictionary resources, Char first, string text)
    {
        if (FindFont(resources, first.Font) is { } font && Codes(font) is { } map)
        {
            var sb = new StringBuilder("<");
            double width = 0;
            var metrics = FontMetrics.From(Deref(font) as PdfDictionary);
            bool all = true;
            foreach (var rune in text.EnumerateRunes())
            {
                if (!map.TryGetValue(rune.ToString(), out var code)) { all = false; break; }
                sb.Append(Convert.ToHexString(code));
                int c = 0;
                foreach (var b in code) c = c << 8 | b;
                width += metrics.Width(c, code.Length).W;
            }
            if (all) return (font, sb.Append('>').ToString(), width, true, Clean(first.Font));
        }

        // a standard font of the same kind
        var (std, stdPen, name) = StandardPen(doc, text, Serif(first));
        var (hex, em) = stdPen(text);
        return (std, hex, em, false, name);
    }

    /// <summary>
    /// A font of the kind <paramref name="text"/> needs (Latin or CJK, serif or not) and a pen for it: a CJK font of this
    /// computer embedded as a subset when there is one that may be (the text then looks the same in every viewer),
    /// otherwise a standard font the viewer supplies (<paramref name="reuse"/> gives the document's one, when it has it).
    /// </summary>
    internal static (PdfItem Font, Func<string, (string Hex, double Em)> Pen, string Name) StandardPen(PdfDocument doc, string text, bool serif,
                                                                                                     Func<FormScript, PdfDictionary>? reuse = null)
    {
        var script = ScriptFor(text);
        if (Washi.Core.Fonts.SubsetFont.For(doc, script, serif, text) is { } embedded)
            return (embedded.Type0.Reference!, embedded.Encode, embedded.Name);
        var dict = reuse?.Invoke(script) ?? AcroFormWriter.TextFont(doc, script, serif);
        return (dict.Reference!, t => EncodeStandard(t, script, serif), StandardName(dict));
    }

    /// <summary>
    /// The extra space the old text was set with between its characters (character spacing, in points): what is left of
    /// the step from one character to the next once the font's own width is taken off, the median over the text. 0 when
    /// it cannot be told (a font without a ToUnicode map, too few characters) or is too small to matter.
    /// </summary>
    private static double Spacing(PdfDictionary resources, IEnumerable<List<Char>> lines)
    {
        var fonts = new Dictionary<string, (Dictionary<string, byte[]> Map, FontMetrics Metrics)?>();
        var gaps = new List<double>();
        double size = 0;
        foreach (var line in lines)
            for (int i = 0; i + 1 < line.Count; i++)
            {
                var (a, b) = (line[i], line[i + 1]);
                if (string.IsNullOrWhiteSpace(a.Text) || string.IsNullOrWhiteSpace(b.Text) || a.Font != b.Font) continue;
                if (!fonts.TryGetValue(a.Font, out var f))
                    fonts[a.Font] = f = FindFont(resources, a.Font) is { } item && Codes(item) is { } map ? (map, FontMetrics.From(Deref(item) as PdfDictionary)) : null;
                if (f is not { } fm || !fm.Map.TryGetValue(a.Text!, out var code)) continue;
                int c = 0;
                foreach (var x in code) c = c << 8 | x;
                double advance = fm.Metrics.Width(c, code.Length).W * a.FontSize * Math.Sqrt(a.M.A * a.M.A + a.M.B * a.M.B);
                double gap = b.UX - a.UX - advance;
                size = a.Size;
                if (Math.Abs(gap) < 0.5 * a.Size) gaps.Add(gap);
            }
        if (gaps.Count < 3) return 0;
        gaps.Sort();
        double median = gaps[gaps.Count / 2];
        return Math.Abs(median) < 0.01 * size ? 0 : median;
    }

    /// <summary>
    /// One font for a whole paragraph (the document's own when it has every character of <paramref name="text"/>,
    /// otherwise a similar standard one) and a pen that encodes any part of the text in it and measures it (in em).
    /// </summary>
    private static (PdfItem Font, Func<string, (string Hex, double Em)> Pen, bool Kept, string Name) PenFor(PdfDocument doc, PdfDictionary resources, Char first, string text)
    {
        if (FindFont(resources, first.Font) is { } font && Codes(font) is { } map && text.EnumerateRunes().All(r => map.ContainsKey(r.ToString())))
        {
            var metrics = FontMetrics.From(Deref(font) as PdfDictionary);
            return (font, t =>
            {
                var sb = new StringBuilder("<");
                double em = 0;
                foreach (var rune in t.EnumerateRunes())
                {
                    var code = map[rune.ToString()];
                    sb.Append(Convert.ToHexString(code));
                    int c = 0;
                    foreach (var b in code) c = c << 8 | b;
                    em += metrics.Width(c, code.Length).W;
                }
                return (sb.Append('>').ToString(), em);
            }, true, Clean(first.Font));
        }
        var (std, pen, name) = StandardPen(doc, text, Serif(first));
        return (std, pen, false, name);
    }

    /// <summary>The standard font that can draw <paramref name="text"/>: Latin (WinAnsi) when it can, otherwise the CJK script it is in.</summary>
    internal static FormScript ScriptFor(string text)
    {
        if (CanWinAnsi(text)) return FormScript.Latin;
        var detected = FormScripts.Detect(text);
        // kanji only (社外秘, 御中) tells nothing: unless a character is Chinese-only, the user's language decides
        if (detected == FormScript.ChineseSimplified && !text.Any(c => FormScripts.IsSimplifiedOnly(c)))
            return FormScripts.FromCulture(System.Globalization.CultureInfo.CurrentUICulture) is var ui and not FormScript.Latin ? ui : FormScript.Japanese;
        return detected is { } d && d != FormScript.Latin ? d : FormScript.Japanese;
    }

    /// <summary><paramref name="text"/> as a hex string for the standard font of <paramref name="script"/>, and its width in em.</summary>
    internal static (string Hex, double Width) EncodeStandard(string text, FormScript script, bool serif)
    {
        if (script == FormScript.Latin)
        {
            var face = StandardFonts.Find(serif ? "Times-Roman" : "Helvetica")!;
            var bytes = Encoding.GetEncoding(1252).GetBytes(text);
            double width = bytes.Sum(b => face.Widths.TryGetValue(StandardFonts.WinAnsi[b], out var w) ? w / 1000.0 : 0.5);
            return ("<" + Convert.ToHexString(bytes) + ">", width);
        }
        return ("<" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">", text.EnumerateRunes().Sum(r => r.Value <= 0x7E ? 0.5 : 1.0));
    }

    internal static string StandardName(PdfDictionary font) => font.Elements.GetName("/BaseFont").TrimStart('/');

    internal static bool CanWinAnsi(string text)
    {
        try
        {
            var enc = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            enc.GetBytes(text);
            return true;
        }
        catch (EncoderFallbackException) { return false; }
    }

    static PdfTextEditor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static string Clean(string name) => name.Length > 7 && name[6] == '+' ? name[7..] : name;

    /// <summary>The font in the page's resources whose name PDFium reported (with or without its subset tag).</summary>
    private static PdfItem? FindFont(PdfDictionary resources, string name)
    {
        if (name.Length == 0 || Deref(resources.Elements["/Font"]) is not PdfDictionary fonts) return null;
        PdfItem? loose = null;
        foreach (var key in fonts.Elements.Keys)
        {
            var item = fonts.Elements[key];
            if (Deref(item) is not PdfDictionary f) continue;
            var base0 = f.Elements.GetName("/BaseFont").TrimStart('/');
            if (base0 == name) return item;
            if (Clean(base0) == Clean(name)) loose ??= item;
        }
        return loose;
    }

    /// <summary>Character → the code that draws it in this font, or null when that cannot be known.</summary>
    private static Dictionary<string, byte[]>? Codes(PdfItem fontItem)
    {
        if (Deref(fontItem) is not PdfDictionary font) return null;
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (Deref(font.Elements["/ToUnicode"]) is PdfDictionary { Stream: not null } tu)
        {
            // the codes as long as the font reads them: one byte in a simple font, two in Identity-H/V (some files write
            // the map's codes longer or shorter than that)
            bool composite = font.Elements.GetName("/Subtype") == "/Type0";
            int? bytes = !composite ? 1 : Deref(font.Elements["/Encoding"]) is PdfName { Value: "/Identity-H" or "/Identity-V" } ? 2 : null;
            foreach (var (code, text) in ToUnicode(Encoding.Latin1.GetString(tu.Stream.UnfilteredValue)))
            {
                var c = code;
                if (bytes is { } want && c.Length != want)
                {
                    long v = 0;
                    foreach (var x in c) v = v << 8 | x;
                    if (v >= 1L << (8 * want)) continue;
                    c = new byte[want];
                    for (int i = want - 1; i >= 0; i--) { c[i] = (byte)v; v >>= 8; }
                }
                map.TryAdd(text, c);
            }
            return map.Count > 0 ? map : null;
        }
        if (font.Elements.GetName("/Subtype") is "/Type0" or "/Type3") return null;
        // a simple font without ToUnicode: its encoding, when the font is whole (a subset may lack the glyph)
        var baseFont = font.Elements.GetName("/BaseFont").TrimStart('/');
        bool subset = baseFont.Length > 7 && baseFont[6] == '+';
        var names = new string?[256];
        var enc = Deref(font.Elements["/Encoding"]);
        string? encName = enc is PdfName n ? n.Value : (enc as PdfDictionary)?.Elements.GetName("/BaseEncoding");
        var baseNames = encName switch { "/MacRomanEncoding" => StandardFonts.MacRoman, "/StandardEncoding" => StandardFonts.Standard, _ => StandardFonts.WinAnsi };
        if (!subset) for (int i = 32; i < 256; i++) names[i] = baseNames[i];
        if (enc is PdfDictionary ed && Deref(ed.Elements["/Differences"]) is PdfArray diff)
        {
            int code = 0;
            foreach (var item in diff.Elements)
                switch (Deref(item))
                {
                    case PdfInteger i: code = i.Value; break;
                    case PdfName g: if (code is >= 0 and < 256) names[code] = g.Value.TrimStart('/'); code++; break;
                }
        }
        for (int c = 0; c < 256; c++)
            if (names[c] is { } g && Unicode(g) is { } u) map.TryAdd(u, [(byte)c]);
        return map.Count > 0 ? map : null;
    }

    private static readonly Lazy<Dictionary<string, string>> GlyphToUnicode = new(() =>
    {
        // the WinAnsi glyph names and the characters cp1252 gives their codes
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var cp = Encoding.GetEncoding(1252);
        for (int c = 32; c < 256; c++)
        {
            var name = StandardFonts.WinAnsi[c];
            if (name is ".notdef" or "bullet" && c != 0x95) continue;
            try { d.TryAdd(name, cp.GetString([(byte)c])); } catch (ArgumentException) { }
        }
        return d;
    });

    private static string? Unicode(string glyph)
    {
        if (GlyphToUnicode.Value.TryGetValue(glyph, out var u)) return u;
        var m = Regex.Match(glyph, @"^(?:uni([0-9A-Fa-f]{4})|u([0-9A-Fa-f]{4,6}))$");
        if (m.Success) return char.ConvertFromUtf32(int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>The pairs (code bytes, text) of a ToUnicode CMap's bfchar and bfrange entries.</summary>
    private static IEnumerable<(byte[] Code, string Text)> ToUnicode(string cmap)
    {
        foreach (Match block in Regex.Matches(cmap, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
            foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]*)>"))
                if (Hex(m.Groups[1].Value) is { } code && Utf16(m.Groups[2].Value) is { Length: > 0 } text) yield return (code, text);
        foreach (Match block in Regex.Matches(cmap, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
            foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(<[0-9A-Fa-f]*>|\[[^\]]*\])"))
            {
                var lo = Hex(m.Groups[1].Value); var hi = Hex(m.Groups[2].Value);
                if (lo is null || hi is null || lo.Length != hi.Length) continue;
                long a = Int(lo), b = Int(hi);
                if (b < a || b - a > 65535) continue;
                var dst = m.Groups[3].Value;
                if (dst.StartsWith('['))
                {
                    var items = Regex.Matches(dst, @"<([0-9A-Fa-f]*)>").Select(x => x.Groups[1].Value).ToList();
                    for (long c = a; c <= b && c - a < items.Count; c++)
                        if (Utf16(items[(int)(c - a)]) is { Length: > 0 } t) yield return (Bytes(c, lo.Length), t);
                }
                else
                {
                    var start = Hex(dst.Trim('<', '>'));
                    if (start is null || start.Length < 2) continue;
                    for (long c = a; c <= b; c++)
                    {
                        var u = (byte[])start.Clone();
                        long add = c - a;
                        for (int i = u.Length - 1; i >= 0 && add > 0; i--) { long v = u[i] + add; u[i] = (byte)v; add = v >> 8; }
                        if (Encoding.BigEndianUnicode.GetString(u) is { Length: > 0 } t) yield return (Bytes(c, lo.Length), t);
                    }
                }
            }

        static byte[]? Hex(string h) => h.Length % 2 == 0 && h.Length > 0 ? Convert.FromHexString(h) : null;
        static string? Utf16(string h) => h.Length >= 4 && h.Length % 4 == 0 ? Encoding.BigEndianUnicode.GetString(Convert.FromHexString(h)) : null;
        static long Int(byte[] b) { long v = 0; foreach (var x in b) v = v << 8 | x; return v; }
        static byte[] Bytes(long v, int n) { var r = new byte[n]; for (int i = n - 1; i >= 0; i--) { r[i] = (byte)v; v >>= 8; } return r; }
    }
}
