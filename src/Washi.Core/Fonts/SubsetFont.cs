// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using PdfSharp.Pdf;
using Washi.Core.Forms;

namespace Washi.Core.Fonts;

/// <summary>
/// A font of this computer embedded in a PDF with only the characters drawn in it (a subset): the text then looks the
/// same in every viewer, and the file grows by a few kilobytes, not the font's megabytes. The PDF font is made at once
/// (pages can name it); its program, widths and ToUnicode map are written by <see cref="Finish"/>, when every
/// character is known.
/// </summary>
internal sealed class SubsetFont
{
    private readonly OpenTypeFont _font;
    private readonly PdfDocument _doc;
    private readonly PdfDictionary _cid, _descriptor;
    private readonly Dictionary<int, int> _codeToUnicode = [];   // code (CID or glyph) → character
    private readonly HashSet<int> _glyphs = [0];
    private bool _finished;

    public PdfDictionary Type0 { get; }
    public string Name { get; }

    private SubsetFont(PdfDocument doc, OpenTypeFont font)
    {
        _doc = doc;
        _font = font;
        // a subset's name starts with six capitals (here from the font's name, so the same font gets the same tag)
        uint h = 2166136261;
        foreach (var c in font.PostScriptName) h = (h ^ c) * 16777619;
        var tag = new string([.. Enumerable.Range(0, 6).Select(i => (char)('A' + (int)(h >> (i * 5) & 31) % 26))]);
        Name = font.PostScriptName;
        var baseFont = "/" + tag + "+" + font.PostScriptName;

        _descriptor = new PdfDictionary(doc);
        doc.Internals.AddObject(_descriptor);
        _descriptor.Elements.SetName("/Type", "/FontDescriptor");
        _descriptor.Elements.SetName("/FontName", baseFont);
        _descriptor.Elements.SetInteger("/Flags", 4);
        double k = 1000.0 / font.UnitsPerEm;
        int S(double v) => (int)Math.Round(v * k);
        _descriptor.Elements["/FontBBox"] = new PdfArray(doc, new PdfInteger(S(font.BBox.XMin)), new PdfInteger(S(font.BBox.YMin)),
                                                         new PdfInteger(S(font.BBox.XMax)), new PdfInteger(S(font.BBox.YMax)));
        _descriptor.Elements.SetReal("/ItalicAngle", font.ItalicAngle);
        _descriptor.Elements.SetInteger("/Ascent", S(font.Ascender));
        _descriptor.Elements.SetInteger("/Descent", S(font.Descender));
        _descriptor.Elements.SetInteger("/CapHeight", S(font.CapHeight));
        _descriptor.Elements.SetInteger("/StemV", 80);

        var info = new PdfDictionary(doc);
        var (reg, ord, sup) = font.Ros ?? ("Adobe", "Identity", 0);
        info.Elements["/Registry"] = new PdfString(reg);
        info.Elements["/Ordering"] = new PdfString(ord);
        info.Elements.SetInteger("/Supplement", sup);
        _cid = new PdfDictionary(doc);
        doc.Internals.AddObject(_cid);
        _cid.Elements.SetName("/Type", "/Font");
        _cid.Elements.SetName("/Subtype", font.IsCff ? "/CIDFontType0" : "/CIDFontType2");
        _cid.Elements.SetName("/BaseFont", baseFont);
        _cid.Elements["/CIDSystemInfo"] = info;
        _cid.Elements.SetReference("/FontDescriptor", _descriptor);
        if (!font.IsCff) _cid.Elements.SetName("/CIDToGIDMap", "/Identity");

        Type0 = new PdfDictionary(doc);
        doc.Internals.AddObject(Type0);
        Type0.Elements.SetName("/Type", "/Font");
        Type0.Elements.SetName("/Subtype", "/Type0");
        Type0.Elements.SetName("/BaseFont", baseFont);
        Type0.Elements.SetName("/Encoding", "/Identity-H");
        Type0.Elements["/DescendantFonts"] = new PdfArray(doc, _cid.Reference!);
    }

    public bool Covers(string text) => text.EnumerateRunes().All(r => _font.UnicodeToGlyph.ContainsKey(r.Value));

    /// <summary>The text as two-byte codes in this font, and its width in em. Every character must be covered.</summary>
    public (string Hex, double Em) Encode(string text)
    {
        if (_finished) throw new InvalidOperationException("the font is already written");
        var sb = new StringBuilder("<");
        double em = 0;
        foreach (var r in text.EnumerateRunes())
        {
            int gid = _font.UnicodeToGlyph[r.Value];
            int code = _font.IsCff ? _font.GlyphToCid[gid] : gid;
            _glyphs.Add(gid);
            _codeToUnicode.TryAdd(code, r.Value);
            sb.Append(code.ToString("X4", CultureInfo.InvariantCulture));
            em += _font.Advances[gid] / (double)_font.UnitsPerEm;
        }
        return (sb.Append('>').ToString(), em);
    }

    /// <summary>The font program (only the glyphs used), the widths and the ToUnicode map.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        var program = new PdfDictionary(_doc);
        _doc.Internals.AddObject(program);
        byte[] data;
        if (_font.IsCff)
        {
            data = FontSubsetter.Cff(FontSubsetter.CffTable(_font.Data), _glyphs);
            program.Elements.SetName("/Subtype", "/CIDFontType0C");
        }
        else
        {
            data = FontSubsetter.TrueType(_font.Data, _glyphs);
            program.Elements.SetInteger("/Length1", data.Length);
        }
        program.CreateStream(Deflate(data));
        program.Elements.SetName("/Filter", "/FlateDecode");
        _descriptor.Elements.SetReference(_font.IsCff ? "/FontFile3" : "/FontFile2", program);

        // widths of the codes used; the rest take the default
        double k = 1000.0 / _font.UnitsPerEm;
        var widths = new PdfArray(_doc);
        foreach (var g in _glyphs.Order())
        {
            int code = _font.IsCff ? _font.GlyphToCid[g] : g;
            widths.Elements.Add(new PdfInteger(code));
            widths.Elements.Add(new PdfArray(_doc, new PdfInteger((int)Math.Round(_font.Advances[g] * k))));
        }
        _cid.Elements["/W"] = widths;
        _cid.Elements.SetInteger("/DW", 1000);

        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
                                     "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var chunk in _codeToUnicode.OrderBy(kv => kv.Key).Chunk(100))
        {
            cmap.Append(chunk.Length.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            foreach (var (code, u) in chunk)
                cmap.Append('<').Append(code.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                    .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(u)))).Append(">\n");
            cmap.Append("endbfchar\n");
        }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var toUnicode = new PdfDictionary(_doc);
        _doc.Internals.AddObject(toUnicode);
        toUnicode.CreateStream(Deflate(Encoding.ASCII.GetBytes(cmap.ToString())));
        toUnicode.Elements.SetName("/Filter", "/FlateDecode");
        Type0.Elements.SetReference("/ToUnicode", toUnicode);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ the fonts of this computer, per document

    private static readonly ConditionalWeakTable<PdfDocument, Dictionary<(FormScript, FaceStyle), SubsetFont?>> PerDocument = new();
    private static readonly Dictionary<(FormScript, FaceStyle), OpenTypeFont?> Loaded = [];
    private static readonly Lock Gate = new();

    /// <summary>
    /// A font of this computer for <paramref name="script"/> (serif or not) that may be embedded and has every character of
    /// <paramref name="text"/>, shared by the whole document; null when there is none (the caller then names a standard
    /// font the viewer supplies).
    /// </summary>
    public static SubsetFont? For(PdfDocument doc, FormScript script, bool serif, string text)
    {
        if (script == FormScript.Latin) return null;
        return Substitute(doc, script, new FaceStyle(serif), text);
    }

    /// <summary>
    /// Like <see cref="For"/>, for any script (Latin too) and style: the font that stands in for one a PDF names without
    /// embedding it. Bold, italic and fixed-width faces are found for Latin text; other scripts get the regular face.
    /// </summary>
    public static SubsetFont? Substitute(PdfDocument doc, FormScript script, FaceStyle style, string text)
    {
        if (Environment.GetEnvironmentVariable("WASHI_NO_EMBED") == "1") return null;
        if (style.Family is not null) script = FormScript.Latin;
        else if (script != FormScript.Latin) style = new FaceStyle(style.Serif);
        var fonts = PerDocument.GetOrCreateValue(doc);
        if (!fonts.TryGetValue((script, style), out var f))
        {
            var font = System(script, style);
            fonts[(script, style)] = f = font is null ? null : new SubsetFont(doc, font);
        }
        return f is not null && f.Covers(text) ? f : null;
    }

    /// <summary>Writes the subsets of every font <see cref="For"/> gave for this document (before it is saved).</summary>
    public static void FinishAll(PdfDocument doc)
    {
        if (PerDocument.TryGetValue(doc, out var fonts))
            foreach (var f in fonts.Values) f?.Finish();
    }

    private static OpenTypeFont? System(FormScript script, FaceStyle style)
    {
        lock (Gate)
        {
            if (Loaded.TryGetValue((script, style), out var cached)) return cached;
            OpenTypeFont? found = null;
            foreach (var (path, face) in script == FormScript.Latin ? LatinCandidates(style) : Candidates(script, style.Serif))
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var font = OpenTypeFont.Load(path, face);
                    if (font.EmbeddingAllowedForEditing && (font.IsCff ? font.Ros is not null : true)) { found = font; break; }
                }
                catch (FontNotUsableException) { }
                catch (IOException) { }
            }
            Loaded[(script, style)] = found;
            return found;
        }
    }

    /// <summary>
    /// Latin faces with the widths of the standard PDF fonts (Arial for Helvetica, Times New Roman for Times, Courier New
    /// for Courier, or Liberation on Linux): the ones a viewer would show for a font that is not embedded.
    /// </summary>
    private static IEnumerable<(string Path, int Face)> LatinCandidates(FaceStyle s)
    {
        if (s.Family is { } symbolFamily)
        {
            // the symbol fonts: the same design where the system has it, otherwise a font with those characters
            if (OperatingSystem.IsMacOS())
            {
                yield return ("/System/Library/Fonts/" + (symbolFamily == "ZapfDingbats" ? "ZapfDingbats.ttf" : "Symbol.ttf"), 0);
                yield return ("/System/Library/Fonts/Apple Symbols.ttf", 0);
                yield return ("/System/Library/Fonts/Supplemental/Arial Unicode.ttf", 0);
            }
            else if (OperatingSystem.IsWindows())
                yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "seguisym.ttf"), 0);
            else
                foreach (var f in new[] { "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/dejavu/DejaVuSans.ttf", "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf" })
                    yield return (f, 0);
            yield break;
        }
        string suffix = (s.Bold, s.Italic) switch { (true, true) => " Bold Italic", (true, false) => " Bold", (false, true) => " Italic", _ => "" };
        string family = s.Mono ? "Courier New" : s.Serif ? "Times New Roman" : "Arial";
        if (OperatingSystem.IsMacOS())
        {
            yield return ("/System/Library/Fonts/Supplemental/" + family + suffix + ".ttf", 0);
            yield return ("/Library/Fonts/" + family + suffix + ".ttf", 0);
        }
        else if (OperatingSystem.IsWindows())
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            string stem = s.Mono ? "cour" : s.Serif ? "times" : "arial";
            string end = (s.Bold, s.Italic) switch { (true, true) => "bi", (true, false) => "bd", (false, true) => "i", _ => "" };
            yield return (Path.Combine(dir, stem + end + ".ttf"), 0);
        }
        else
        {
            string lib = s.Mono ? "LiberationMono" : s.Serif ? "LiberationSerif" : "LiberationSans";
            string style = (s.Bold, s.Italic) switch { (true, true) => "BoldItalic", (true, false) => "Bold", (false, true) => "Italic", _ => "Regular" };
            foreach (var dir in new[] { "/usr/share/fonts/truetype/liberation/", "/usr/share/fonts/truetype/liberation2/", "/usr/share/fonts/liberation-sans/",
                                        "/usr/share/fonts/liberation-serif/", "/usr/share/fonts/liberation-mono/", "/usr/share/fonts/liberation/" })
                yield return (dir + lib + "-" + style + ".ttf", 0);
        }
    }

    /// <summary>Where each system keeps fonts for the script (the first one found that may be embedded is used).</summary>
    private static IEnumerable<(string Path, int Face)> Candidates(FormScript script, bool serif)
    {
        if (OperatingSystem.IsMacOS())
        {
            const string sys = "/System/Library/Fonts/", sup = "/System/Library/Fonts/Supplemental/";
            switch (script)
            {
                case FormScript.Japanese:
                    if (serif) { yield return (sys + "ヒラギノ明朝 ProN.ttc", 0); yield return (sup + "ヒラギノ明朝 ProN.ttc", 0); }
                    else { yield return (sys + "ヒラギノ角ゴシック W3.ttc", 0); }
                    break;
                case FormScript.ChineseSimplified:
                    if (!serif) yield return (sys + "Hiragino Sans GB.ttc", 0);
                    break;
                case FormScript.Korean:
                    if (!serif) yield return (sys + "AppleSDGothicNeo.ttc", 0);
                    break;
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            string[] names = script switch
            {
                FormScript.Japanese => serif ? ["yumin.ttf", "msmincho.ttc"] : ["YuGothM.ttc", "meiryo.ttc", "msgothic.ttc"],
                FormScript.ChineseSimplified => serif ? ["simsun.ttc"] : ["msyh.ttc", "simhei.ttf"],
                FormScript.ChineseTraditional => serif ? ["mingliu.ttc"] : ["msjh.ttc"],
                FormScript.Korean => serif ? ["batang.ttc"] : ["malgun.ttf"],
                _ => [],
            };
            foreach (var n in names) yield return (Path.Combine(dir, n), 0);
        }
        else
        {
            // Noto CJK collections: one face per language (Japanese, Korean, Simplified, Traditional, Hong Kong)
            int face = script switch { FormScript.Korean => 1, FormScript.ChineseSimplified => 2, FormScript.ChineseTraditional => 3, _ => 0 };
            var file = serif ? "NotoSerifCJK-Regular.ttc" : "NotoSansCJK-Regular.ttc";
            foreach (var dir in new[] { "/usr/share/fonts/opentype/noto/", "/usr/share/fonts/noto-cjk/", "/usr/share/fonts/google-noto-cjk/", "/usr/share/fonts/truetype/noto/" })
                yield return (dir + file, face);
        }
    }
}

/// <summary>The look a stand-in font should have.</summary>
internal readonly record struct FaceStyle(bool Serif, bool Bold = false, bool Italic = false, bool Mono = false, string? Family = null);
