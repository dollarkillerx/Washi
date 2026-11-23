// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Washi.Core.Forms;
using Washi.Core.Pages;

namespace Washi.Core.Fonts;

/// <summary>
/// Embeds the fonts a PDF only names. Each font that is not embedded is replaced by a subset of a font of this computer
/// with the same characters (Arial for Helvetica, Times New Roman for Times, Hiragino or another CJK font for Japanese
/// and Chinese…), and every string drawn in it is written again in the new font's codes. Where a new glyph is wider or
/// narrower than the old width, the string moves the pen back by the difference (a TJ adjustment), so every character
/// stays exactly where it was: only the glyph shapes are the computer's.
/// </summary>
internal static class FontReplacer
{
    internal sealed record Replaced(string Name, string With);

    /// <summary>A font that cannot be replaced, and why (for the message).</summary>
    internal sealed record Problem(string Font, string Reason);

    /// <summary>The fonts drawn in some stream that are not embedded (not Type 3, which are drawings).</summary>
    public static List<string> Missing(PdfDocument doc)
    {
        var names = new List<string>();
        foreach (var unit in Units(doc))
            Walk(unit, (font, _, _) => { names.Add(Name(font)); return null; });
        return [.. names.Distinct()];
    }

    /// <summary>
    /// Replaces every font that is not embedded and is drawn somewhere. Throws when one cannot be (no Unicode for its codes,
    /// no font on this computer with its characters, vertical writing).
    /// </summary>
    public static List<Replaced> EmbedMissing(PdfDocument doc)
    {
        var units = Units(doc);
        // pass 1: what each font draws
        var used = new Dictionary<PdfDictionary, OldFont>(ReferenceEqualityComparer.Instance);
        var problems = new List<Problem>();
        foreach (var unit in units)
            Walk(unit, (font, op, codes) =>
            {
                if (!used.TryGetValue(font, out var old))
                {
                    try { used[font] = old = OldFont.For(font); }
                    catch (CannotReplace e) { problems.Add(new Problem(Name(font), e.Message)); used[font] = old = OldFont.Broken; }
                }
                if (old == OldFont.Broken) return null;
                foreach (var (code, len) in codes)
                    if (old.Text(code, len) is { } t) old.Seen.Append(t);
                    else { problems.Add(new Problem(Name(font), $"no character is known for code {code:X}")); used[font] = OldFont.Broken; return null; }
                return null;
            });
        var plans = new Dictionary<PdfDictionary, (OldFont Old, SubsetFont New)>(ReferenceEqualityComparer.Instance);
        foreach (var (font, old) in used)
        {
            if (old == OldFont.Broken) continue;
            var text = old.Seen.ToString();
            // a composite font's character collection names its language; otherwise the characters do
            var script = Ros(font) switch
            {
                (_, "Japan1") => FormScript.Japanese,
                (_, "GB1") => FormScript.ChineseSimplified,
                (_, "CNS1") => FormScript.ChineseTraditional,
                (_, "Korea1" or "KR") => FormScript.Korean,
                _ => PdfTextEditor.ScriptFor(text),
            };
            var style = Style(font);
            // the font asked for, then the other face of the same script, then any CJK font with the characters
            var sub = SubsetFont.Substitute(doc, script, style, text)
                      ?? SubsetFont.Substitute(doc, script, style with { Serif = !style.Serif }, text)
                      ?? new[] { FormScript.Japanese, FormScript.ChineseSimplified, FormScript.ChineseTraditional, FormScript.Korean }
                             .Where(x => x != script).SelectMany(x => new[] { (x, true), (x, false) })
                             .Select(x => SubsetFont.Substitute(doc, x.Item1, new FaceStyle(x.Item2), text)).FirstOrDefault(f => f is not null);
            if (sub is null)
            {
                problems.Add(new Problem(Name(font), "this computer has no font that may be embedded with all its characters"));
                continue;
            }
            plans[font] = (old, sub);
        }
        if (problems.Count > 0)
        {
            var list = string.Join("; ", problems.GroupBy(p => p.Font).Select(g => $"{g.Key} ({g.First().Reason})"));
            throw new PageAssemblyException($"these fonts are not embedded and cannot be replaced: {list}", "Err_PdfaFont", list);
        }
        if (plans.Count == 0) return [];

        // pass 2: the strings again, in the new fonts' codes
        foreach (var unit in units)
        {
            var edited = Walk(unit, (font, op, codes) =>
            {
                if (!plans.TryGetValue(font, out var plan)) return null;
                return codes;   // the walker writes the new operator
            }, plans);
            foreach (var (stream, bytes) in edited) ContentRedactor.SetStream(stream, bytes);
            if (unit.Resources is not null && Deref(unit.Resources.Elements["/Font"]) is PdfDictionary fonts)
                foreach (var key in fonts.Elements.Keys.ToList())
                    if (Deref(fonts.Elements[key]) is PdfDictionary f && plans.TryGetValue(f, out var plan))
                        fonts.Elements.SetReference(key, plan.New.Type0);
        }
        return [.. plans.Select(p => new Replaced(Name(p.Key), p.Value.New.Name))];
    }

    // ------------------------------------------------------------------ the old font: codes → characters and widths

    private sealed class CannotReplace(string reason) : Exception(reason);

    private sealed class OldFont
    {
        public static readonly OldFont Broken = new();
        public FontMetrics Metrics { get; private init; } = FontMetrics.Fallback;
        public StringBuilder Seen { get; } = new();
        private Func<int, int, string?> _text = (_, _) => null;
        private Func<int, int, double?> _width = (_, _) => null;

        public string? Text(int code, int len) => _text(code, len);
        public double? Width(int code, int len) => _width(code, len);

        public static OldFont For(PdfDictionary font)
        {
            var m = FontMetrics.From(font);
            if (m.Vertical) throw new CannotReplace("vertical writing");
            var old = new OldFont { Metrics = m };
            bool composite = font.Elements.GetName("/Subtype") == "/Type0";
            var toUnicode = ToUnicodeMap(font);
            Func<int, int, string?>? fromEncoding = composite ? CompositeText(font) : SimpleText(font);
            old._text = (code, len) =>
                toUnicode is not null && (toUnicode.TryGetValue((code, len), out var t) || toUnicode.TryGetValue((code, 0), out t)) && Usable(t) ? t
                : fromEncoding?.Invoke(code, len) is { } e && Usable(e) ? e : null;
            var cidWidth = composite ? CidWidth(font, old._text) : null;
            old._width = (code, len) =>
            {
                var (w, known) = m.Width(code, len);
                if (known) return w;
                return cidWidth?.Invoke(code, len);
            };
            return old;
        }

        private static bool Usable(string t) => t.Length > 0 && !t.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r'));

        /// <summary>(code, bytes) → text from the font's ToUnicode map; (code, 0) as well, for maps written with other lengths.</summary>
        private static Dictionary<(int, int), string>? ToUnicodeMap(PdfDictionary font)
        {
            if (Deref(font.Elements["/ToUnicode"]) is not PdfDictionary { Stream: not null } tu) return null;
            var map = new Dictionary<(int, int), string>();
            foreach (var (code, text) in PdfTextEditor.ToUnicode(Encoding.Latin1.GetString(tu.Stream.UnfilteredValue)))
            {
                int v = 0;
                foreach (var b in code) v = v << 8 | b;
                map.TryAdd((v, code.Length), text);
                map.TryAdd((v, 0), text);
            }
            return map.Count > 0 ? map : null;
        }

        /// <summary>A simple font's code → character, through its encoding's glyph names.</summary>
        private static Func<int, int, string?>? SimpleText(PdfDictionary font)
        {
            var baseFont = font.Elements.GetName("/BaseFont").TrimStart('/');
            // the two symbol fonts of the standard 14, with their own encodings (unless the file gives differences)
            if (SymbolTable(font) is { } table) return (code, _) => table.TryGetValue(code, out var c) ? c.ToString() : null;
            if (baseFont.Contains("Symbol", StringComparison.OrdinalIgnoreCase) || baseFont.Contains("Dingbats", StringComparison.OrdinalIgnoreCase)
                || (font.Elements.GetName("/Subtype") == "/TrueType" && font.Elements["/Encoding"] is null && Flags(font) is var fl && (fl & 4) != 0))
                return null;   // symbol fonts: their glyphs are not characters
            var names = new string?[256];
            var enc = Deref(font.Elements["/Encoding"]);
            string? encName = enc is PdfName n ? n.Value : (enc as PdfDictionary)?.Elements.GetName("/BaseEncoding");
            var baseNames = encName switch
            {
                "/WinAnsiEncoding" => StandardFonts.WinAnsi,
                "/MacRomanEncoding" => StandardFonts.MacRoman,
                "/StandardEncoding" => StandardFonts.Standard,
                _ => font.Elements.GetName("/Subtype") == "/TrueType" ? StandardFonts.WinAnsi : StandardFonts.Standard,
            };
            for (int i = 0; i < 256; i++) names[i] = baseNames[i];
            if (enc is PdfDictionary ed && Deref(ed.Elements["/Differences"]) is PdfArray diff)
            {
                int code = 0;
                foreach (var item in diff.Elements)
                    switch (Deref(item))
                    {
                        case PdfInteger i: code = i.Value; break;
                        case PdfReal r: code = (int)r.Value; break;
                        case PdfName g: if (code is >= 0 and < 256) names[code] = g.Value.TrimStart('/'); code++; break;
                    }
            }
            return (code, _) => code is >= 0 and < 256 && names[code] is { } g && g != ".notdef" ? GlyphText(g) : null;
        }

        internal static Dictionary<int, char>? SymbolTable(PdfDictionary font)
        {
            var name = Name(font);
            if (Deref(font.Elements["/Encoding"]) is PdfDictionary { } e && e.Elements.ContainsKey("/Differences")) return null;
            if (name.Contains("Dingbats", StringComparison.OrdinalIgnoreCase)) return Dingbats;
            if (name is "Symbol" || name.StartsWith("Symbol,", StringComparison.Ordinal) || name.StartsWith("SymbolMT", StringComparison.Ordinal)) return Symbols;
            return null;
        }

        // the built-in encodings of ZapfDingbats and Symbol, as characters (Adobe's glyph lists)
        private static readonly Dictionary<int, char> Dingbats = new() { [0x20] = '\u0020', [0x21] = '\u2701', [0x22] = '\u2702', [0x23] = '\u2703', [0x24] = '\u2704', [0x25] = '\u260E', [0x26] = '\u2706', [0x27] = '\u2707', [0x28] = '\u2708', [0x29] = '\u2709', [0x2A] = '\u261B', [0x2B] = '\u261E', [0x2C] = '\u270C', [0x2D] = '\u270D', [0x2E] = '\u270E', [0x2F] = '\u270F', [0x30] = '\u2710', [0x31] = '\u2711', [0x32] = '\u2712', [0x33] = '\u2713', [0x34] = '\u2714', [0x35] = '\u2715', [0x36] = '\u2716', [0x37] = '\u2717', [0x38] = '\u2718', [0x39] = '\u2719', [0x3A] = '\u271A', [0x3B] = '\u271B', [0x3C] = '\u271C', [0x3D] = '\u271D', [0x3E] = '\u271E', [0x3F] = '\u271F', [0x40] = '\u2720', [0x41] = '\u2721', [0x42] = '\u2722', [0x43] = '\u2723', [0x44] = '\u2724', [0x45] = '\u2725', [0x46] = '\u2726', [0x47] = '\u2727', [0x48] = '\u2605', [0x49] = '\u2729', [0x4A] = '\u272A', [0x4B] = '\u272B', [0x4C] = '\u272C', [0x4D] = '\u272D', [0x4E] = '\u272E', [0x4F] = '\u272F', [0x50] = '\u2730', [0x51] = '\u2731', [0x52] = '\u2732', [0x53] = '\u2733', [0x54] = '\u2734', [0x55] = '\u2735', [0x56] = '\u2736', [0x57] = '\u2737', [0x58] = '\u2738', [0x59] = '\u2739', [0x5A] = '\u273A', [0x5B] = '\u273B', [0x5C] = '\u273C', [0x5D] = '\u273D', [0x5E] = '\u273E', [0x5F] = '\u273F', [0x60] = '\u2740', [0x61] = '\u2741', [0x62] = '\u2742', [0x63] = '\u2743', [0x64] = '\u2744', [0x65] = '\u2745', [0x66] = '\u2746', [0x67] = '\u2747', [0x68] = '\u2748', [0x69] = '\u2749', [0x6A] = '\u274A', [0x6B] = '\u274B', [0x6C] = '\u25CF', [0x6D] = '\u274D', [0x6E] = '\u25A0', [0x6F] = '\u274F', [0x70] = '\u2750', [0x71] = '\u2751', [0x72] = '\u2752', [0x73] = '\u25B2', [0x74] = '\u25BC', [0x75] = '\u25C6', [0x76] = '\u2756', [0x77] = '\u25D7', [0x78] = '\u2758', [0x79] = '\u2759', [0x7A] = '\u275A', [0x7B] = '\u275B', [0x7C] = '\u275C', [0x7D] = '\u275D', [0x7E] = '\u275E', [0x80] = '\u2768', [0x81] = '\u2769', [0x82] = '\u276A', [0x83] = '\u276B', [0x84] = '\u276C', [0x85] = '\u276D', [0x86] = '\u276E', [0x87] = '\u276F', [0x88] = '\u2770', [0x89] = '\u2771', [0x8A] = '\u2772', [0x8B] = '\u2773', [0x8C] = '\u2774', [0x8D] = '\u2775', [0xA1] = '\u2761', [0xA2] = '\u2762', [0xA3] = '\u2763', [0xA4] = '\u2764', [0xA5] = '\u2765', [0xA6] = '\u2766', [0xA7] = '\u2767', [0xA8] = '\u2663', [0xA9] = '\u2666', [0xAA] = '\u2665', [0xAB] = '\u2660', [0xAC] = '\u2460', [0xAD] = '\u2461', [0xAE] = '\u2462', [0xAF] = '\u2463', [0xB0] = '\u2464', [0xB1] = '\u2465', [0xB2] = '\u2466', [0xB3] = '\u2467', [0xB4] = '\u2468', [0xB5] = '\u2469', [0xB6] = '\u2776', [0xB7] = '\u2777', [0xB8] = '\u2778', [0xB9] = '\u2779', [0xBA] = '\u277A', [0xBB] = '\u277B', [0xBC] = '\u277C', [0xBD] = '\u277D', [0xBE] = '\u277E', [0xBF] = '\u277F', [0xC0] = '\u2780', [0xC1] = '\u2781', [0xC2] = '\u2782', [0xC3] = '\u2783', [0xC4] = '\u2784', [0xC5] = '\u2785', [0xC6] = '\u2786', [0xC7] = '\u2787', [0xC8] = '\u2788', [0xC9] = '\u2789', [0xCA] = '\u278A', [0xCB] = '\u278B', [0xCC] = '\u278C', [0xCD] = '\u278D', [0xCE] = '\u278E', [0xCF] = '\u278F', [0xD0] = '\u2790', [0xD1] = '\u2791', [0xD2] = '\u2792', [0xD3] = '\u2793', [0xD4] = '\u2794', [0xD5] = '\u2192', [0xD6] = '\u2194', [0xD7] = '\u2195', [0xD8] = '\u2798', [0xD9] = '\u2799', [0xDA] = '\u279A', [0xDB] = '\u279B', [0xDC] = '\u279C', [0xDD] = '\u279D', [0xDE] = '\u279E', [0xDF] = '\u279F', [0xE0] = '\u27A0', [0xE1] = '\u27A1', [0xE2] = '\u27A2', [0xE3] = '\u27A3', [0xE4] = '\u27A4', [0xE5] = '\u27A5', [0xE6] = '\u27A6', [0xE7] = '\u27A7', [0xE8] = '\u27A8', [0xE9] = '\u27A9', [0xEA] = '\u27AA', [0xEB] = '\u27AB', [0xEC] = '\u27AC', [0xED] = '\u27AD', [0xEE] = '\u27AE', [0xEF] = '\u27AF', [0xF1] = '\u27B1', [0xF2] = '\u27B2', [0xF3] = '\u27B3', [0xF4] = '\u27B4', [0xF5] = '\u27B5', [0xF6] = '\u27B6', [0xF7] = '\u27B7', [0xF8] = '\u27B8', [0xF9] = '\u27B9', [0xFA] = '\u27BA', [0xFB] = '\u27BB', [0xFC] = '\u27BC', [0xFD] = '\u27BD', [0xFE] = '\u27BE' };
        private static readonly Dictionary<int, char> Symbols = new() { [0x20] = '\u0020', [0x21] = '\u0021', [0x22] = '\u2200', [0x23] = '\u0023', [0x24] = '\u2203', [0x25] = '\u0025', [0x26] = '\u0026', [0x27] = '\u220B', [0x28] = '\u0028', [0x29] = '\u0029', [0x2A] = '\u2217', [0x2B] = '\u002B', [0x2C] = '\u002C', [0x2D] = '\u2212', [0x2E] = '\u002E', [0x2F] = '\u002F', [0x30] = '\u0030', [0x31] = '\u0031', [0x32] = '\u0032', [0x33] = '\u0033', [0x34] = '\u0034', [0x35] = '\u0035', [0x36] = '\u0036', [0x37] = '\u0037', [0x38] = '\u0038', [0x39] = '\u0039', [0x3A] = '\u003A', [0x3B] = '\u003B', [0x3C] = '\u003C', [0x3D] = '\u003D', [0x3E] = '\u003E', [0x3F] = '\u003F', [0x40] = '\u2245', [0x41] = '\u0391', [0x42] = '\u0392', [0x43] = '\u03A7', [0x44] = '\u0394', [0x45] = '\u0395', [0x46] = '\u03A6', [0x47] = '\u0393', [0x48] = '\u0397', [0x49] = '\u0399', [0x4A] = '\u03D1', [0x4B] = '\u039A', [0x4C] = '\u039B', [0x4D] = '\u039C', [0x4E] = '\u039D', [0x4F] = '\u039F', [0x50] = '\u03A0', [0x51] = '\u0398', [0x52] = '\u03A1', [0x53] = '\u03A3', [0x54] = '\u03A4', [0x55] = '\u03A5', [0x56] = '\u03C2', [0x57] = '\u03A9', [0x58] = '\u039E', [0x59] = '\u03A8', [0x5A] = '\u0396', [0x5B] = '\u005B', [0x5C] = '\u2234', [0x5D] = '\u005D', [0x5E] = '\u22A5', [0x5F] = '\u005F', [0x61] = '\u03B1', [0x62] = '\u03B2', [0x63] = '\u03C7', [0x64] = '\u03B4', [0x65] = '\u03B5', [0x66] = '\u03C6', [0x67] = '\u03B3', [0x68] = '\u03B7', [0x69] = '\u03B9', [0x6A] = '\u03D5', [0x6B] = '\u03BA', [0x6C] = '\u03BB', [0x6D] = '\u03BC', [0x6E] = '\u03BD', [0x6F] = '\u03BF', [0x70] = '\u03C0', [0x71] = '\u03B8', [0x72] = '\u03C1', [0x73] = '\u03C3', [0x74] = '\u03C4', [0x75] = '\u03C5', [0x76] = '\u03D6', [0x77] = '\u03C9', [0x78] = '\u03BE', [0x79] = '\u03C8', [0x7A] = '\u03B6', [0x7B] = '\u007B', [0x7C] = '\u007C', [0x7D] = '\u007D', [0x7E] = '\u223C', [0xA0] = '\u20AC', [0xA1] = '\u03D2', [0xA2] = '\u2032', [0xA3] = '\u2264', [0xA4] = '\u2044', [0xA5] = '\u221E', [0xA6] = '\u0192', [0xA7] = '\u2663', [0xA8] = '\u2666', [0xA9] = '\u2665', [0xAA] = '\u2660', [0xAB] = '\u2194', [0xAC] = '\u2190', [0xAD] = '\u2191', [0xAE] = '\u2192', [0xAF] = '\u2193', [0xB0] = '\u00B0', [0xB1] = '\u00B1', [0xB2] = '\u2033', [0xB3] = '\u2265', [0xB4] = '\u00D7', [0xB5] = '\u221D', [0xB6] = '\u2202', [0xB7] = '\u2022', [0xB8] = '\u00F7', [0xB9] = '\u2260', [0xBA] = '\u2261', [0xBB] = '\u2248', [0xBC] = '\u2026', [0xBF] = '\u21B5', [0xC0] = '\u2135', [0xC1] = '\u2111', [0xC2] = '\u211C', [0xC3] = '\u2118', [0xC4] = '\u2297', [0xC5] = '\u2295', [0xC6] = '\u2205', [0xC7] = '\u2229', [0xC8] = '\u222A', [0xC9] = '\u2283', [0xCA] = '\u2287', [0xCB] = '\u2284', [0xCC] = '\u2282', [0xCD] = '\u2286', [0xCE] = '\u2208', [0xCF] = '\u2209', [0xD0] = '\u2220', [0xD1] = '\u2207', [0xD2] = '\u00AE', [0xD3] = '\u00A9', [0xD4] = '\u2122', [0xD5] = '\u220F', [0xD6] = '\u221A', [0xD7] = '\u22C5', [0xD8] = '\u00AC', [0xD9] = '\u2227', [0xDA] = '\u2228', [0xDB] = '\u21D4', [0xDC] = '\u21D0', [0xDD] = '\u21D1', [0xDE] = '\u21D2', [0xDF] = '\u21D3', [0xE0] = '\u25CA', [0xE1] = '\u2329', [0xE2] = '\u00AE', [0xE3] = '\u00A9', [0xE4] = '\u2122', [0xE5] = '\u2211', [0xF1] = '\u232A', [0xF2] = '\u222B' };

        private static string? GlyphText(string glyph) => glyph switch
        {
            "space" => " ",
            "fi" => "ﬁ",
            "fl" => "ﬂ",
            "ff" => "ﬀ",
            "ffi" => "ﬃ",
            "ffl" => "ﬄ",
            "dotlessi" => "ı",
            "nbspace" or "nonbreakingspace" => " ",
            "minus" => "−",
            "fraction" => "⁄",
            _ => PdfTextEditor.Unicode(glyph),
        };

        /// <summary>A composite font's code → character, from its CMap's encoding (Unicode, Shift-JIS, GBK, Big5, UHC…).</summary>
        private static Func<int, int, string?>? CompositeText(PdfDictionary font)
        {
            var cmap = Deref(font.Elements["/Encoding"]) is PdfName n ? n.Value.TrimStart('/') : null;
            var ros = Ros(font);
            if (cmap is null or "Identity-H" or "Identity-V")
            {
                // codes are CIDs: a character only through a font of this computer in the same character collection
                var cids = cmap is null ? null : CidCollection.For(ros);
                return cids is null ? null : (code, _) => cids.Text(code);
            }
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding? e = cmap switch
            {
                _ when cmap.Contains("UCS2", StringComparison.Ordinal) || cmap.Contains("UTF16", StringComparison.Ordinal) => Encoding.BigEndianUnicode,
                _ when cmap.Contains("UTF8", StringComparison.Ordinal) => Encoding.UTF8,
                _ when cmap.Contains("UTF32", StringComparison.Ordinal) => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
                _ when cmap.Contains("RKSJ", StringComparison.Ordinal) => Encoding.GetEncoding(932),
                _ when cmap.StartsWith("GB", StringComparison.Ordinal) => Encoding.GetEncoding(936),
                _ when cmap.StartsWith("KSC", StringComparison.Ordinal) => Encoding.GetEncoding(949),
                _ when cmap.Contains("B5", StringComparison.Ordinal) || cmap.StartsWith("CNS", StringComparison.Ordinal) => Encoding.GetEncoding(950),
                "EUC-H" or "EUC-V" => Encoding.GetEncoding(20932),
                "H" or "V" => Encoding.GetEncoding(20932),   // JIS X 0208 codes, read as EUC-JP below
                _ => null,
            };
            if (e is null) return null;
            bool jis = cmap is "H" or "V";
            return (code, len) =>
            {
                var bytes = new byte[len];
                for (int i = len - 1, v = code; i >= 0; i--, v >>= 8) bytes[i] = (byte)v;
                if (jis) for (int i = 0; i < len; i++) bytes[i] |= 0x80;
                try
                {
                    var s = e.GetString(bytes);
                    return s.Contains('�') || s.Length == 0 ? null : s;
                }
                catch (DecoderFallbackException) { return null; }
            };
        }

        /// <summary>
        /// The width of a code whose CID the predefined CMap gives (not known here): the CID is found through its character
        /// in a font of this computer from the same collection (Adobe-Japan1 in Hiragino). One-byte codes of the
        /// Japanese CMaps are the half-width CIDs, which take one width.
        /// </summary>
        private static Func<int, int, double?>? CidWidth(PdfDictionary font, Func<int, int, string?> text)
        {
            var cid = Descendant(font);
            if (cid is null || Deref(cid.Elements["/W"]) is not PdfArray wa) return null;
            double dw = cid.Elements.ContainsKey("/DW") ? FontMetrics.Num(cid.Elements["/DW"]) / 1000 : 1;
            var widths = FontMetrics.Ranges(wa, 1);
            var cmap = Deref(font.Elements["/Encoding"]) is PdfName n ? n.Value.TrimStart('/') : "";
            var collection = CidCollection.For(Ros(font));
            double? halfWidth = null;
            if (cmap.Contains("RKSJ", StringComparison.Ordinal) || cmap.StartsWith("EUC", StringComparison.Ordinal))
            {
                var half = Enumerable.Range(231, 402).Select(c => widths.TryGetValue(c, out var v) ? v[0] / 1000 : dw).Distinct().ToList();
                if (half.Count == 1) halfWidth = half[0];
            }
            return (code, len) =>
            {
                if (len == 1 && halfWidth is { } hw) return hw;
                if (collection is null || text(code, len) is not { } t || collection.Cid(t) is not { } c) return null;
                return widths.TryGetValue(c, out var v) ? v[0] / 1000 : dw;
            };
        }
    }

    private static PdfDictionary? Descendant(PdfDictionary font) =>
        Deref(font.Elements["/DescendantFonts"]) is PdfArray df && df.Elements.Count > 0 ? Deref(df.Elements[0]) as PdfDictionary : null;

    private static (string Registry, string Ordering)? Ros(PdfDictionary font)
    {
        if (Descendant(font) is not { } cid || Deref(cid.Elements["/CIDSystemInfo"]) is not PdfDictionary info) return null;
        string S(string key) => Deref(info.Elements[key]) switch { PdfString s => s.Value, PdfStringObject so => so.Value, _ => "" };
        return (S("/Registry"), S("/Ordering"));
    }

    /// <summary>A character collection (Adobe-Japan1, Adobe-GB1) read from a CID-keyed font of this computer: character ↔ CID.</summary>
    private sealed class CidCollection
    {
        private readonly Dictionary<string, int> _cid = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _text = [];
        private static readonly Dictionary<string, CidCollection?> Cache = [];
        private static readonly Lock Gate = new();

        public int? Cid(string text) => _cid.TryGetValue(text, out var c) ? c : null;
        public string? Text(int cid) => _text.TryGetValue(cid, out var t) ? t : null;

        public static CidCollection? For((string Registry, string Ordering)? ros)
        {
            if (ros is not ("Adobe", var ordering) || ordering is not ("Japan1" or "GB1")) return null;
            lock (Gate)
            {
                if (Cache.TryGetValue(ordering, out var cached)) return cached;
                string[] files = !OperatingSystem.IsMacOS() ? []
                    : ordering == "Japan1" ? ["/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc", "/System/Library/Fonts/ヒラギノ明朝 ProN.ttc"]
                    : ["/System/Library/Fonts/Hiragino Sans GB.ttc"];
                CidCollection? found = null;
                foreach (var file in files.Where(File.Exists))
                {
                    try
                    {
                        var f = OpenTypeFont.Load(file);
                        if (f is not { IsCff: true, Ros: { Ordering: var o } } || o != ordering) continue;
                        found = new CidCollection();
                        foreach (var (u, g) in f.UnicodeToGlyph.OrderBy(kv => kv.Key))
                        {
                            var t = char.ConvertFromUtf32(u);
                            int cid = f.GlyphToCid[g];
                            found._cid.TryAdd(t, cid);
                            found._text.TryAdd(cid, t);
                        }
                        break;
                    }
                    catch (FontNotUsableException) { }
                    catch (IOException) { }
                }
                Cache[ordering] = found;
                return found;
            }
        }
    }

    // ------------------------------------------------------------------ content streams

    /// <summary>Streams that draw with one set of resources: a page's contents (in order), a form, an appearance, a pattern.</summary>
    private sealed record Unit(List<PdfDictionary> Streams, PdfDictionary? Resources);

    private static List<Unit> Units(PdfDocument doc)
    {
        var units = new List<Unit>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var seenRes = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        void Resources(PdfDictionary? res)
        {
            if (res is null || !seenRes.Add(res)) return;
            if (Deref(res.Elements["/XObject"]) is PdfDictionary xs)
                foreach (var key in xs.Elements.Keys)
                    if (Deref(xs.Elements[key]) is PdfDictionary { Stream: not null } x && x.Elements.GetName("/Subtype") == "/Form")
                        Stream(x, res);
            if (Deref(res.Elements["/Pattern"]) is PdfDictionary ps)
                foreach (var key in ps.Elements.Keys)
                    if (Deref(ps.Elements[key]) is PdfDictionary { Stream: not null } pt && pt.Elements.GetInteger("/PatternType") == 1)
                        Stream(pt, res);
            if (Deref(res.Elements["/Font"]) is PdfDictionary fs)
                foreach (var key in fs.Elements.Keys)
                    if (Deref(fs.Elements[key]) is PdfDictionary t3 && t3.Elements.GetName("/Subtype") == "/Type3"
                        && Deref(t3.Elements["/CharProcs"]) is PdfDictionary procs)
                        foreach (var g in procs.Elements.Keys)
                            if (Deref(procs.Elements[g]) is PdfDictionary { Stream: not null } proc)
                                Stream(proc, Deref(t3.Elements["/Resources"]) as PdfDictionary ?? res);
        }

        void Stream(PdfDictionary s, PdfDictionary? parentRes)
        {
            if (!seen.Add(s)) return;
            var res = Deref(s.Elements["/Resources"]) as PdfDictionary ?? parentRes;
            units.Add(new Unit([s], res));
            Resources(res);
        }

        foreach (var page in doc.Pages)
        {
            var res = Inherited(page, "/Resources") as PdfDictionary;
            var streams = new List<PdfDictionary>();
            switch (Deref(page.Elements["/Contents"]))
            {
                case PdfArray arr: streams.AddRange(arr.Elements.Select(Deref).OfType<PdfDictionary>().Where(d => d.Stream is not null)); break;
                case PdfDictionary { Stream: not null } one: streams.Add(one); break;
            }
            units.Add(new Unit(streams, res));
            Resources(res);
            if (Deref(page.Elements["/Annots"]) is PdfArray annots)
                foreach (var a in annots.Elements.Select(Deref).OfType<PdfDictionary>())
                    if (Deref(a.Elements["/AP"]) is PdfDictionary ap)
                        foreach (var key in ap.Elements.Keys)
                            switch (Deref(ap.Elements[key]))
                            {
                                case PdfDictionary { Stream: not null } x: Stream(x, null); break;
                                case PdfDictionary states:
                                    foreach (var st in states.Elements.Keys)
                                        if (Deref(states.Elements[st]) is PdfDictionary { Stream: not null } sx) Stream(sx, null);
                                    break;
                            }
        }
        return units;
    }

    private static int Flags(PdfDictionary font) => (Deref(font.Elements["/FontDescriptor"]) as PdfDictionary)?.Elements.GetInteger("/Flags") ?? 0;

    private sealed class State
    {
        public PdfDictionary? Font;
        public double Size, Tc, Tw;
        public State Copy() => (State)MemberwiseClone();
    }

    /// <summary>
    /// Goes through the unit's text operators. <paramref name="visit"/> sees each string drawn in a font that is not
    /// embedded; when <paramref name="plans"/> is given, the strings of planned fonts are written again and the new
    /// streams returned.
    /// </summary>
    private static List<(PdfDictionary Stream, byte[] Bytes)> Walk(Unit unit, Func<PdfDictionary, ContentStream.Op, List<(int Code, int Len)>, object?> visit,
                                                                   Dictionary<PdfDictionary, (OldFont Old, SubsetFont New)>? plans = null)
    {
        var result = new List<(PdfDictionary, byte[])>();
        var fonts = unit.Resources is not null && Deref(unit.Resources.Elements["/Font"]) is PdfDictionary fd ? fd : null;
        var state = new State();
        var stack = new Stack<State>();
        foreach (var stream in unit.Streams)
        {
            byte[] bytes;
            try { bytes = stream.Stream.UnfilteredValue; }
            catch (Exception) { continue; }
            var cs = new ContentStream(bytes);
            var edits = new List<(int, int, string)>();
            foreach (var op in cs.Ops)
            {
                switch (op.Name)
                {
                    case "q": stack.Push(state.Copy()); break;
                    case "Q": if (stack.Count > 0) state = stack.Pop(); break;
                    case "Tf":
                        state.Font = op.Names.Count > 0 && fonts is not null ? Deref(fonts.Elements["/" + op.Names[^1]]) as PdfDictionary : null;
                        state.Size = op.Numbers.Count > 0 ? D(op.Numbers[^1]) : state.Size;
                        break;
                    case "Tc": if (op.Numbers.Count > 0) state.Tc = D(op.Numbers[^1]); break;
                    case "Tw": if (op.Numbers.Count > 0) state.Tw = D(op.Numbers[^1]); break;
                    case "Tj" or "TJ" or "'" or "\"":
                    {
                        if (op.Name == "\"" && op.Numbers.Count >= 2) { state.Tw = D(op.Numbers[0]); state.Tc = D(op.Numbers[1]); }
                        if (state.Font is not { } font || IsEmbedded(font) || font.Elements.GetName("/Subtype") == "/Type3") break;
                        var metrics = plans is not null && plans.TryGetValue(font, out var p0) ? p0.Old.Metrics : FontMetrics.From(font);
                        var operands = cs.Operands(op);
                        var items = op.Name == "TJ" && operands.Count > 0 && operands[^1] is List<object> arr ? arr
                                    : operands.OfType<byte[]>().Take(1).Cast<object>().ToList();
                        var codes = items.OfType<byte[]>().SelectMany(s => metrics.Codes(s).Select(c => (c.Code, c.Length))).ToList();
                        visit(font, op, codes);
                        if (plans is null || !plans.TryGetValue(font, out var plan)) break;
                        var sb = new StringBuilder("[");
                        var hex = new StringBuilder();
                        void Flush() { if (hex.Length > 0) { sb.Append('<').Append(hex).Append('>'); hex.Clear(); } }
                        foreach (var item in items)
                        {
                            if (item is double adj) { Flush(); sb.Append(' ').Append(N(adj)).Append(' '); continue; }
                            if (item is not byte[] s) continue;
                            foreach (var (_, length, code) in metrics.Codes(s).Select(c => (c.Start, c.Length, c.Code)))
                            {
                                var text = plan.Old.Text(code, length)!;
                                var oldW = plan.Old.Width(code, length)
                                           ?? throw new PageAssemblyException($"the width of a character in {Name(font)} is not known", "Err_PdfaFont", Name(font));
                                var (h, newW) = plan.New.Encode(text);
                                hex.Append(h, 1, h.Length - 2);
                                int glyphs = (h.Length - 2) / 4;
                                // the old pen move: w·size + Tc (+ Tw for a one-byte space); the new one: w'·size + Tc per glyph
                                double extra = (newW - oldW) * state.Size + (glyphs - 1) * state.Tc - (length == 1 && code == 32 ? state.Tw : 0);
                                if (state.Size != 0 && Math.Abs(extra) > 1e-6)
                                {
                                    Flush();
                                    sb.Append(' ').Append(N(extra * 1000 / state.Size)).Append(' ');
                                }
                            }
                        }
                        Flush();
                        sb.Append("] TJ");
                        var prefix = op.Name switch
                        {
                            "'" => "T* ",
                            "\"" => $"{N(state.Tw)} Tw {N(state.Tc)} Tc T* ",
                            _ => "",
                        };
                        edits.Add((op.Start, op.End, prefix + sb));
                        break;
                    }
                }
            }
            if (edits.Count > 0) result.Add((stream, ContentRedactor.Apply(bytes, edits)));
        }
        return result;
    }

    // ------------------------------------------------------------------ fonts

    internal static bool IsEmbedded(PdfDictionary font)
    {
        var subtype = font.Elements.GetName("/Subtype");
        if (subtype == "/Type3") return true;
        var desc = subtype == "/Type0" ? Descendant(font) is { } cid ? Deref(cid.Elements["/FontDescriptor"]) as PdfDictionary : null
                                       : Deref(font.Elements["/FontDescriptor"]) as PdfDictionary;
        return desc is not null && (desc.Elements.ContainsKey("/FontFile") || desc.Elements.ContainsKey("/FontFile2") || desc.Elements.ContainsKey("/FontFile3"));
    }

    internal static string Name(PdfDictionary font)
    {
        var n = font.Elements.GetName("/BaseFont").TrimStart('/');
        return n.Length > 7 && n[6] == '+' ? n[7..] : n.Length > 0 ? n : "(unnamed)";
    }

    /// <summary>Serif, bold, italic or fixed width, from the font's name and its descriptor's flags.</summary>
    private static FaceStyle Style(PdfDictionary font)
    {
        var name = Name(font);
        var desc = font.Elements.GetName("/Subtype") == "/Type0"
            ? Descendant(font) is { } cid ? Deref(cid.Elements["/FontDescriptor"]) as PdfDictionary : null
            : Deref(font.Elements["/FontDescriptor"]) as PdfDictionary;
        int flags = desc?.Elements.GetInteger("/Flags") ?? 0;
        double weight = desc is not null && desc.Elements.ContainsKey("/FontWeight") ? FontMetrics.Num(desc.Elements["/FontWeight"]) : 0;
        bool Has(params string[] parts) => parts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
        bool sans = Has("Sans", "Gothic", "Goth", "Arial", "Helvetica", "Verdana", "Meiryo", "Hei", "Dotum", "Gulim", "Calibri", "Segoe", "Tahoma");
        bool serif = !sans && (Has("Serif", "Times", "Mincho", "Minchō", "Ming", "Song", "Batang", "Myeongjo", "Century", "Georgia", "Garamond", "Roman", "明朝", "宋")
                               || !Has("Courier", "Mono") && (flags & 2) != 0);
        bool mono = Has("Courier", "Mono", "Consolas") || (flags & 1) != 0 && !Has("Gothic", "Mincho", "Ming", "Song");
        bool bold = Has("Bold", "Black", "Heavy", "Semibold", "Demi") || weight >= 600 || (flags & (1 << 18)) != 0;
        bool italic = Has("Italic", "Oblique") || (flags & 64) != 0;
        if (OldFont.SymbolTable(font) is { } t) return new FaceStyle(false, Family: name.Contains("Dingbats", StringComparison.OrdinalIgnoreCase) ? "ZapfDingbats" : "Symbol");
        return new FaceStyle(serif, bold, italic, mono);
    }

    private static PdfItem? Inherited(PdfDictionary d, string key)
    {
        for (var cur = d; cur is not null; cur = Deref(cur.Elements["/Parent"]) as PdfDictionary)
            if (cur.Elements[key] is { } v) return Deref(v);
        return null;
    }

    private static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
