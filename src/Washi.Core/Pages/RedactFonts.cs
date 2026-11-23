// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Washi.Core.Pages;

/// <summary>
/// What redaction needs to know about a font: how a shown string splits into character codes, and how far each code
/// moves the pen (in em, 1/1000 of the glyph space for all but Type 3 fonts). A width that cannot be known exactly is
/// estimated and flagged, so the caller removes the whole run instead of trusting it.
/// </summary>
internal sealed partial class FontMetrics
{
    public bool Vertical { get; private init; }
    public double Ascent { get; private init; } = 0.8;
    public double Descent { get; private init; } = -0.2;

    private readonly List<(int Length, byte[] Lo, byte[] Hi)> _codespace = [];
    private Func<int, int, (double W, bool Known)> _width = (_, _) => (0.5, false);
    private Func<int, int, double> _vertical = (_, _) => -1;   // w1 (vertical advance), em

    private FontMetrics() { }

    public static FontMetrics Fallback { get; } = Make1Byte(new FontMetrics(), (_, _) => (0.5, false));

    private static FontMetrics Make1Byte(FontMetrics m, Func<int, int, (double, bool)> width)
    {
        m._codespace.Add((1, [0], [255]));
        m._width = width;
        return m;
    }

    /// <summary>The codes of <paramref name="s"/>: (start, length, code).</summary>
    public IEnumerable<(int Start, int Length, int Code)> Codes(byte[] s)
    {
        int i = 0;
        while (i < s.Length)
        {
            int len = Match(s, i);
            int code = 0;
            for (int k = 0; k < len; k++) code = code << 8 | s[i + k];
            yield return (i, len, code);
            i += len;
        }
    }

    private int Match(byte[] s, int at)
    {
        foreach (var (len, lo, hi) in _codespace)
        {
            if (at + len > s.Length) continue;
            bool ok = true;
            for (int k = 0; k < len && ok; k++) ok = s[at + k] >= lo[k] && s[at + k] <= hi[k];
            if (ok) return len;
        }
        // not in any range: as many bytes as the shortest range whose first byte fits, else the shortest range
        foreach (var (len, lo, hi) in _codespace.OrderBy(c => c.Length))
            if (s[at] >= lo[0] && s[at] <= hi[0]) return Math.Min(len, s.Length - at);
        return Math.Min(_codespace.Count == 0 ? 1 : _codespace.Min(c => c.Length), s.Length - at);
    }

    /// <summary>Horizontal advance in em, and whether it is exact.</summary>
    public (double W, bool Known) Width(int code, int length) => _width(code, length);

    /// <summary>Vertical advance (w1, negative: downwards) in em.</summary>
    public double VerticalAdvance(int code, int length) => _vertical(code, length);

    // ------------------------------------------------------------------ building

    public static FontMetrics From(PdfDictionary? font)
    {
        if (font is null) return Fallback;
        var subtype = font.Elements.GetName("/Subtype");
        if (subtype == "/Type0") return Type0(font);
        var m = new FontMetrics { Ascent = Asc(font, 0.8), Descent = Desc(font, -0.2) };
        if (subtype == "/Type3")
        {
            double scale = Deref(font.Elements["/FontMatrix"]) is PdfArray fm && fm.Elements.Count >= 1 ? Num(fm.Elements[0]) : 0.001;
            var (first, widths) = SimpleWidths(font);
            return Make1Byte(m, (code, _) => widths is not null && code - first >= 0 && code - first < widths.Length
                ? (widths[code - first] * scale, true) : (0, widths is not null));
        }
        var (fc, w) = SimpleWidths(font);
        double missing = Descriptor(font)?.Elements.ContainsKey("/MissingWidth") == true ? Num(Descriptor(font)!.Elements["/MissingWidth"]) / 1000 : 0;
        if (w is not null)
            return Make1Byte(m, (code, _) => code - fc >= 0 && code - fc < w.Length ? (w[code - fc] / 1000, true) : (missing, true));

        // no /Widths: one of the 14 standard fonts, whose widths every reader knows
        var face = StandardFonts.Find(font.Elements.GetName("/BaseFont"));
        if (face is null) return Make1Byte(m, (_, _) => (0.5, false));
        var names = GlyphNames(font, face);
        m = new FontMetrics { Ascent = face.Ascent, Descent = face.Descent };
        return Make1Byte(m, (code, _) =>
        {
            if (face.Fixed is { } f) return (f / 1000.0, true);
            var name = names[code & 255];
            return name is not null && face.Widths.TryGetValue(name, out var gw) ? (gw / 1000.0, true) : (0, name is ".notdef" or null);
        });
    }

    /// <summary>Glyph name per code: the encoding (or the face's own one) with its /Differences.</summary>
    private static string?[] GlyphNames(PdfDictionary font, StandardFonts.Face face)
    {
        var names = new string?[256];
        IReadOnlyList<string>? baseNames = null;
        PdfArray? differences = null;
        var enc = Deref(font.Elements["/Encoding"]);
        string? encName = enc is PdfName n ? n.Value : null;
        if (enc is PdfDictionary ed)
        {
            encName = ed.Elements.GetName("/BaseEncoding");
            differences = Deref(ed.Elements["/Differences"]) as PdfArray;
        }
        baseNames = encName switch
        {
            "/WinAnsiEncoding" => StandardFonts.WinAnsi,
            "/MacRomanEncoding" => StandardFonts.MacRoman,
            "/StandardEncoding" => StandardFonts.Standard,
            _ => null,
        };
        if (baseNames is null && face.BuiltIn is { } builtIn)
            foreach (var (c, g) in builtIn) names[c & 255] = g;
        else
            for (int i = 0; i < 256; i++) names[i] = (baseNames ?? StandardFonts.Standard)[i];
        if (differences is not null)
        {
            int code = 0;
            foreach (var item in differences.Elements)
                switch (Deref(item))
                {
                    case PdfInteger i: code = i.Value; break;
                    case PdfReal r: code = (int)r.Value; break;
                    case PdfName g: if (code is >= 0 and < 256) names[code] = g.Value.TrimStart('/'); code++; break;
                }
        }
        return names;
    }

    private static (int First, double[]? Widths) SimpleWidths(PdfDictionary font)
    {
        if (Deref(font.Elements["/Widths"]) is not PdfArray a) return (0, null);
        int first = font.Elements.GetInteger("/FirstChar");
        return (first, [.. a.Elements.Select(e => Num(Deref(e)))]);
    }

    private static PdfDictionary? Descriptor(PdfDictionary font) => Deref(font.Elements["/FontDescriptor"]) as PdfDictionary;

    private static double Asc(PdfDictionary font, double fallback) =>
        Descriptor(font) is { } d && d.Elements.ContainsKey("/Ascent") && Num(d.Elements["/Ascent"]) is var a && a > 0 ? a / 1000 : fallback;

    private static double Desc(PdfDictionary font, double fallback) =>
        Descriptor(font) is { } d && d.Elements.ContainsKey("/Descent") && Num(d.Elements["/Descent"]) is var a && a < 0 ? a / 1000 : fallback;

    // ------------------------------------------------------------------ composite fonts

    private static FontMetrics Type0(PdfDictionary font)
    {
        var cid = Deref(font.Elements["/DescendantFonts"]) is PdfArray df && df.Elements.Count > 0 ? Deref(df.Elements[0]) as PdfDictionary : null;
        var enc = Deref(font.Elements["/Encoding"]);
        var name = enc is PdfName n ? n.Value.TrimStart('/') : null;
        CMap? embedded = enc is PdfDictionary es && es.Stream is not null ? CMap.Parse(es.Stream.UnfilteredValue, es.Elements.GetInteger("/WMode")) : null;
        bool vertical = embedded?.WMode == 1 || name is not null && (name.EndsWith("-V", StringComparison.Ordinal) || name == "V");

        var m = new FontMetrics
        {
            Vertical = vertical,
            Ascent = cid is null ? 0.88 : Asc(cid, 0.88),
            Descent = cid is null ? -0.12 : Desc(cid, -0.12),
        };
        Func<int, int, int?> toCid;
        if (embedded is not null && embedded.Codespace.Count > 0)
        {
            m._codespace.AddRange(embedded.Codespace);
            toCid = embedded.Cid;
        }
        else if (name is "Identity-H" or "Identity-V" || embedded is not null && embedded.UsesIdentity)
        {
            m._codespace.Add((2, [0, 0], [255, 255]));
            toCid = (code, _) => code;
        }
        else
        {
            m._codespace.AddRange(Predefined(name ?? ""));
            toCid = (_, _) => null;
        }

        double dw = cid is not null && cid.Elements.ContainsKey("/DW") ? Num(cid.Elements["/DW"]) / 1000 : 1;
        var w = cid is null ? null : Deref(cid.Elements["/W"]) as PdfArray;
        var widths = w is null ? null : Ranges(w, 1);
        m._width = (code, len) =>
        {
            if (widths is null) return (dw, true);   // every glyph has the default width
            if (toCid(code, len) is not { } c) return (len == 1 ? dw / 2 : dw, false);
            return widths.TryGetValue(c, out var v) ? (v[0] / 1000, true) : (dw, true);
        };
        if (vertical)
        {
            double dw2 = cid is not null && Deref(cid.Elements["/DW2"]) is PdfArray d2 && d2.Elements.Count >= 2 ? Num(d2.Elements[1]) / 1000 : -1;
            var w2 = cid is null ? null : Deref(cid.Elements["/W2"]) is PdfArray a2 ? Ranges(a2, 3) : null;
            m._vertical = (code, len) => w2 is not null && toCid(code, len) is { } c && w2.TryGetValue(c, out var v) ? v[0] / 1000 : dw2;
        }
        return m;
    }

    /// <summary>/W (n = 1) or /W2 (n = 3): "c [v v …]" and "c1 c2 v…" forms.</summary>
    internal static Dictionary<int, double[]> Ranges(PdfArray a, int n)
    {
        var d = new Dictionary<int, double[]>();
        var items = a.Elements.Select(Deref).ToList();
        int i = 0;
        while (i < items.Count)
        {
            int first = (int)Num(items[i]);
            if (i + 1 < items.Count && items[i + 1] is PdfArray list)
            {
                var vals = list.Elements.Select(e => Num(Deref(e))).ToList();
                for (int k = 0; k + n <= vals.Count; k += n) d[first + k / n] = [.. vals.Skip(k).Take(n)];
                i += 2;
            }
            else if (i + 2 + n - 1 < items.Count)
            {
                int last = (int)Num(items[i + 1]);
                var vals = items.Skip(i + 2).Take(n).Select(Num).ToArray();
                for (int c = first; c <= last && c - first < 65536; c++) d[c] = vals;
                i += 2 + n;
            }
            else break;
        }
        return d;
    }

    /// <summary>Code lengths of the predefined CMaps (the CIDs they map to are not known here).</summary>
    private static List<(int, byte[], byte[])> Predefined(string name)
    {
        if (name.Contains("RKSJ", StringComparison.Ordinal))
            return [(1, [0x00], [0x80]), (1, [0xA0], [0xDF]), (1, [0xFD], [0xFF]), (2, [0x81, 0x40], [0x9F, 0xFC]), (2, [0xE0, 0x40], [0xFC, 0xFC])];
        if (name.Contains("EUC", StringComparison.Ordinal))
            return [(1, [0x00], [0x80]), (2, [0x8E, 0xA0], [0x8E, 0xDF]), (2, [0xA1, 0xA1], [0xFE, 0xFE])];
        if (name.Contains("UTF16", StringComparison.Ordinal))
            return [(4, [0xD8, 0x00, 0xDC, 0x00], [0xDB, 0xFF, 0xDF, 0xFF]), (2, [0x00, 0x00], [0xFF, 0xFF])];
        if (name.Contains("UTF8", StringComparison.Ordinal))
            return [(1, [0x00], [0x7F]), (2, [0xC0, 0x80], [0xDF, 0xBF]), (3, [0xE0, 0x80, 0x80], [0xEF, 0xBF, 0xBF]), (4, [0xF0, 0x80, 0x80, 0x80], [0xF7, 0xBF, 0xBF, 0xBF])];
        return [(2, [0x00, 0x00], [0xFF, 0xFF])];   // UCS2, ISO-2022 based (H, V, …)
    }

    /// <summary>An embedded CMap: its codespace and the CIDs it maps codes to.</summary>
    private sealed partial class CMap
    {
        public List<(int, byte[], byte[])> Codespace { get; } = [];
        public int WMode { get; private set; }
        public bool UsesIdentity { get; private set; }
        private readonly List<(int Len, int Lo, int Hi, int Cid)> _ranges = [];

        public int? Cid(int code, int len)
        {
            foreach (var r in _ranges)
                if (r.Len == len && code >= r.Lo && code <= r.Hi) return r.Cid + code - r.Lo;
            return UsesIdentity && len == 2 ? code : null;
        }

        public static CMap Parse(byte[] bytes, int wmode)
        {
            var c = new CMap { WMode = wmode };
            var text = Encoding.Latin1.GetString(bytes);
            if (WModeRx().Match(text) is { Success: true } wm) c.WMode = int.Parse(wm.Groups[1].Value, CultureInfo.InvariantCulture);
            c.UsesIdentity = UseIdentityRx().IsMatch(text);
            foreach (Match block in BlockRx().Matches(text))
            {
                var kind = block.Groups[1].Value;
                var body = block.Groups[2].Value;
                var tokens = TokenRx().Matches(body).Select(t => t.Value).ToList();
                if (kind == "codespacerange")
                    for (int i = 0; i + 1 < tokens.Count; i += 2)
                    {
                        var lo = Hex(tokens[i]); var hi = Hex(tokens[i + 1]);
                        if (lo.Length == hi.Length && lo.Length is > 0 and <= 4) c.Codespace.Add((lo.Length, lo, hi));
                    }
                else if (kind == "cidrange")
                    for (int i = 0; i + 2 < tokens.Count; i += 3)
                    {
                        var lo = Hex(tokens[i]); var hi = Hex(tokens[i + 1]);
                        if (int.TryParse(tokens[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cid))
                            c._ranges.Add((lo.Length, Int(lo), Int(hi), cid));
                    }
                else if (kind == "cidchar")
                    for (int i = 0; i + 1 < tokens.Count; i += 2)
                    {
                        var code = Hex(tokens[i]);
                        if (int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cid))
                            c._ranges.Add((code.Length, Int(code), Int(code), cid));
                    }
            }
            c.Codespace.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return c;
        }

        private static byte[] Hex(string token) =>
            token.StartsWith('<') ? Convert.FromHexString(token.Trim('<', '>').Length % 2 == 1 ? token.Trim('<', '>') + "0" : token.Trim('<', '>')) : [];

        private static int Int(byte[] b) { int v = 0; foreach (var x in b) v = v << 8 | x; return v; }

        [GeneratedRegex(@"begin(codespacerange|cidrange|cidchar)(.*?)end\1", RegexOptions.Singleline)]
        private static partial Regex BlockRx();
        [GeneratedRegex(@"<[0-9A-Fa-f\s]*>|\d+")]
        private static partial Regex TokenRx();
        [GeneratedRegex(@"/WMode\s+(\d)")]
        private static partial Regex WModeRx();
        [GeneratedRegex(@"/Identity-[HV]\s+usecmap")]
        private static partial Regex UseIdentityRx();
    }

    internal static double Num(PdfItem? i) => Deref(i) switch
    {
        PdfInteger n => n.Value,
        PdfReal r => r.Value,
        PdfLongInteger l => l.Value,
        _ => 0,
    };

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
