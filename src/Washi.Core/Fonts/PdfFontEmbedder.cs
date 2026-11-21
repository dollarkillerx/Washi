// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfSharp.Pdf;

namespace Washi.Core.Fonts;

/// <summary>
/// Embeds a whole font as a Type0 / Identity-H font, so a viewer can draw any character
/// the font has, including ones its own substitute fonts lack (燁, 陳, 臺 ...).
///
/// The full font goes in (not a subset): people will type characters nobody knows yet.
/// A complete ToUnicode map is written too, because viewers such as PDFium find the code
/// for a typed character by looking it up in reverse in ToUnicode.
/// </summary>
public static class PdfFontEmbedder
{
    public static PdfDictionary Embed(PdfDocument doc, OpenTypeFont font)
    {
        if (!font.EmbeddingAllowedForEditing) throw new FontNotUsableException("license");
        double k = 1000.0 / font.UnitsPerEm;
        int S(double v) => (int)Math.Round(v * k);

        // font program, deflated
        var program = new PdfDictionary(doc);
        doc.Internals.AddObject(program);
        program.CreateStream(Deflate(font.Data));
        program.Elements.SetName("/Filter", "/FlateDecode");
        program.Elements.SetInteger("/Length1", font.Data.Length);
        if (font.IsCff) program.Elements.SetName("/Subtype", "/OpenType");

        var fd = new PdfDictionary(doc);
        doc.Internals.AddObject(fd);
        fd.Elements.SetName("/Type", "/FontDescriptor");
        fd.Elements.SetName("/FontName", "/" + font.PostScriptName);
        fd.Elements.SetInteger("/Flags", 4);
        fd.Elements["/FontBBox"] = new PdfArray(doc, new PdfInteger(S(font.BBox.XMin)), new PdfInteger(S(font.BBox.YMin)),
                                                new PdfInteger(S(font.BBox.XMax)), new PdfInteger(S(font.BBox.YMax)));
        fd.Elements.SetReal("/ItalicAngle", font.ItalicAngle);
        fd.Elements.SetInteger("/Ascent", S(font.Ascender));
        fd.Elements.SetInteger("/Descent", S(font.Descender));
        fd.Elements.SetInteger("/CapHeight", S(font.CapHeight));
        fd.Elements.SetInteger("/StemV", 80);
        fd.Elements.SetReference(font.IsCff ? "/FontFile3" : "/FontFile2", program);

        // widths keyed by CID (= glyph index for TrueType)
        var widths = new SortedDictionary<int, int>();
        for (int g = 0; g < font.GlyphCount; g++) widths[font.GlyphToCid[g]] = S(font.Advances[g]);
        int dw = widths.Values.GroupBy(w => w).OrderByDescending(gr => gr.Count()).First().Key;

        var info = new PdfDictionary(doc);
        var (reg, ord, sup) = font.Ros ?? ("Adobe", "Identity", 0);
        info.Elements["/Registry"] = new PdfString(reg);
        info.Elements["/Ordering"] = new PdfString(ord);
        info.Elements.SetInteger("/Supplement", sup);

        var cid = new PdfDictionary(doc);
        doc.Internals.AddObject(cid);
        cid.Elements.SetName("/Type", "/Font");
        cid.Elements.SetName("/Subtype", font.IsCff ? "/CIDFontType0" : "/CIDFontType2");
        cid.Elements.SetName("/BaseFont", "/" + font.PostScriptName);
        cid.Elements["/CIDSystemInfo"] = info;
        cid.Elements.SetReference("/FontDescriptor", fd);
        cid.Elements.SetInteger("/DW", dw);
        cid.Elements["/W"] = WidthArray(doc, widths, dw);
        if (!font.IsCff) cid.Elements.SetName("/CIDToGIDMap", "/Identity");

        var toUnicode = new PdfDictionary(doc);
        doc.Internals.AddObject(toUnicode);
        toUnicode.CreateStream(Deflate(Encoding.ASCII.GetBytes(ToUnicode(font))));
        toUnicode.Elements.SetName("/Filter", "/FlateDecode");

        var type0 = new PdfDictionary(doc);
        doc.Internals.AddObject(type0);
        type0.Elements.SetName("/Type", "/Font");
        type0.Elements.SetName("/Subtype", "/Type0");
        type0.Elements.SetName("/BaseFont", "/" + font.PostScriptName);
        type0.Elements.SetName("/Encoding", "/Identity-H");
        type0.Elements["/DescendantFonts"] = new PdfArray(doc, cid.Reference!);
        type0.Elements.SetReference("/ToUnicode", toUnicode);
        return type0;
    }

    /// <summary>[c [w1 w2 ...]] runs for consecutive CIDs; the default width is left out.</summary>
    private static PdfArray WidthArray(PdfDocument doc, SortedDictionary<int, int> widths, int dw)
    {
        var arr = new PdfArray(doc);
        int? runStart = null;
        var run = new List<int>();
        int prev = int.MinValue;
        void Flush()
        {
            if (runStart is null) return;
            arr.Elements.Add(new PdfInteger(runStart.Value));
            arr.Elements.Add(new PdfArray(doc, run.Select(w => (PdfItem)new PdfInteger(w)).ToArray()));
            runStart = null;
            run.Clear();
        }
        foreach (var (c, w) in widths)
        {
            if (w == dw) { Flush(); prev = c; continue; }
            if (runStart is null || c != prev + 1) { Flush(); runStart = c; }
            run.Add(w);
            prev = c;
        }
        Flush();
        return arr;
    }

    private static string ToUnicode(OpenTypeFont font)
    {
        // CID → the smallest code point that maps to it (prefers the basic form over compatibility ones)
        var byCid = new SortedDictionary<int, int>();
        foreach (var (u, g) in font.UnicodeToGlyph)
        {
            int c = font.GlyphToCid[g];
            if (!byCid.TryGetValue(c, out var have) || u < have) byCid[c] = u;
        }
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n")
          .Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
          .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n")
          .Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var items = byCid.ToList();
        for (int i = 0; i < items.Count; i += 100)
        {
            var chunk = items.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            foreach (var (c, u) in chunk)
                sb.Append('<').Append(c.ToString("X4")).Append("> <")
                  .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(u)))).Append(">\n");
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }
}
