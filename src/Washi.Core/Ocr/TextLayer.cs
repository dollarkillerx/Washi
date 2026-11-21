// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Washi.Core.Ocr;

/// <summary>
/// The invisible text layer of a searchable PDF: recognised text drawn with text render mode 3
/// (neither filled nor stroked) over the page image, so the page looks the same but can be
/// searched, selected and copied. The font is a tiny embedded TrueType font with no visible
/// glyphs; every character code is its UTF-16 value and the ToUnicode map says so, which keeps
/// any script (Japanese, Chinese, Korean, Latin) copyable. Each line is stretched horizontally
/// (Tz) to the width the OCR engine measured.
/// </summary>
public static class TextLayer
{
    /// <summary>Resource name; also marks pages Washi has already made searchable.</summary>
    public const string FontKey = "WashiOcr";

    public static bool HasLayer(PdfPage page) =>
        Deref(Deref(page.Elements["/Resources"]) is PdfDictionary r ? r.Elements["/Font"] : null) is PdfDictionary f
        && f.Elements.ContainsKey("/" + FontKey);

    /// <summary>Adds the lines (upright page points, top-left origin) to the page.</summary>
    public static void Add(PdfDocument doc, PdfPage page, IReadOnlyList<OcrLine> lines, ref PdfDictionary? font)
    {
        if (lines.Count == 0) return;
        font ??= CreateFont(doc);

        var resources = page.Resources;   // PDFsharp creates it (and has flattened inherited ones)
        var fonts = Deref(resources.Elements["/Font"]) as PdfDictionary;
        if (fonts is null) { fonts = new PdfDictionary(doc); resources.Elements["/Font"] = fonts; }
        fonts.Elements.SetReference("/" + FontKey, font);

        var (m, uprightHeight) = UprightToUser(page);
        var sb = new StringBuilder();
        sb.Append("q ").Append(string.Join(' ', m.Select(N))).Append(" cm\nBT 3 Tr\n");
        foreach (var line in lines) AppendLine(sb, line, uprightHeight);
        sb.Append("ET\nQ\n");

        // keep the page's own graphics state from leaking into the layer
        SetStream(page.Contents.PrependContent(), "q\n"u8.ToArray());
        SetStream(page.Contents.AppendContent(), Encoding.ASCII.GetBytes("\nQ\n" + sb));
    }

    private static void AppendLine(StringBuilder sb, OcrLine line, double pageHeight)
    {
        var codes = line.Text.Normalize(NormalizationForm.FormC)
            .Select(ch => char.IsSurrogate(ch) ? '�' : ch).ToArray();
        var text = new string(codes).TrimEnd();
        if (text.Length == 0) return;
        var b = line.Box;
        double w = b.Width, h = b.Height;
        if (w <= 0 || h <= 0) return;

        if (h > w * 1.5 && text.Length > 1 && !text.Contains(' '))
        {
            // a vertical line (縦書き): one character per row, top to bottom
            double step = h / text.Length, size = Math.Min(w, step);
            for (int i = 0; i < text.Length; i++)
            {
                double y = pageHeight - (b.Y0 + (i + 1) * step) + 0.2 * size;
                sb.Append('/').Append(FontKey).Append(' ').Append(N(size)).Append(" Tf ")
                  .Append(N(w / size * 100)).Append(" Tz 1 0 0 1 ").Append(N(b.X0)).Append(' ').Append(N(y))
                  .Append(" Tm <").Append(Hex(text[i].ToString())).Append("> Tj\n");
            }
            return;
        }
        // every code is 1000 units wide (DW): Tz stretches the run to the measured width
        double fs = h, baseline = pageHeight - b.Y1 + 0.2 * h;
        double tz = w / (text.Length * fs) * 100;
        sb.Append('/').Append(FontKey).Append(' ').Append(N(fs)).Append(" Tf ")
          .Append(N(tz)).Append(" Tz 1 0 0 1 ").Append(N(b.X0)).Append(' ').Append(N(baseline))
          .Append(" Tm <").Append(Hex(text)).Append("> Tj\n");
    }

    private static string Hex(string s)
    {
        var sb = new StringBuilder(s.Length * 4);
        foreach (var ch in s) sb.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Matrix from upright page space (what the user sees, bottom-left origin at the visible box)
    /// to the page's user space: the inverse of what <c>AcroFormWriter.Straighten</c> applies.
    /// </summary>
    internal static (double[] M, double UprightHeight) UprightToUser(PdfPage page)
    {
        int rot = ((page.Rotate % 360) + 360) % 360;
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        double a = box.X1, b = box.Y1, c = box.X2, d = box.Y2;
        double[] s = rot switch   // user → upright
        {
            0 => [1, 0, 0, 1, -a, -b],
            90 => [0, -1, 1, 0, -b, c],
            180 => [-1, 0, 0, -1, c, d],
            270 => [0, 1, -1, 0, d, -a],
            _ => throw new NotSupportedException($"/Rotate {page.Rotate}"),
        };
        double det = s[0] * s[3] - s[1] * s[2];
        double[] inv = [s[3] / det, -s[1] / det, -s[2] / det, s[0] / det,
                        (s[2] * s[5] - s[3] * s[4]) / det, (s[1] * s[4] - s[0] * s[5]) / det];
        return (inv, rot is 90 or 270 ? c - a : d - b);
    }

    // ------------------------------------------------------------------ the font
    private static PdfDictionary CreateFont(PdfDocument doc)
    {
        var ttf = GlyphlessTrueType();
        var program = Stream(doc, ttf);
        program.Elements.SetInteger("/Length1", ttf.Length);

        var fd = new PdfDictionary(doc);
        doc.Internals.AddObject(fd);
        fd.Elements.SetName("/Type", "/FontDescriptor");
        fd.Elements.SetName("/FontName", "/GlyphLessFont");
        fd.Elements.SetInteger("/Flags", 5);
        fd.Elements["/FontBBox"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(-200), new PdfInteger(1000), new PdfInteger(800));
        fd.Elements.SetInteger("/ItalicAngle", 0);
        fd.Elements.SetInteger("/Ascent", 800);
        fd.Elements.SetInteger("/Descent", -200);
        fd.Elements.SetInteger("/CapHeight", 800);
        fd.Elements.SetInteger("/StemV", 80);
        fd.Elements.SetReference("/FontFile2", program);

        var info = new PdfDictionary(doc);
        info.Elements["/Registry"] = new PdfString("Adobe");
        info.Elements["/Ordering"] = new PdfString("Identity");
        info.Elements.SetInteger("/Supplement", 0);

        var cid = new PdfDictionary(doc);
        doc.Internals.AddObject(cid);
        cid.Elements.SetName("/Type", "/Font");
        cid.Elements.SetName("/Subtype", "/CIDFontType2");
        cid.Elements.SetName("/BaseFont", "/GlyphLessFont");
        cid.Elements["/CIDSystemInfo"] = info;
        cid.Elements.SetReference("/FontDescriptor", fd);
        cid.Elements.SetInteger("/DW", 1000);
        // every code → glyph 1, a plain box. Render mode 3 never draws it, but PDFium (and others)
        // need a glyph with an outline to place and extract text that is rotated (pages with /Rotate).
        var map = new byte[65536 * 2];
        for (int i = 1; i < map.Length; i += 2) map[i] = 1;
        cid.Elements.SetReference("/CIDToGIDMap", Stream(doc, map));

        var toUnicode = Stream(doc, Encoding.ASCII.GetBytes(IdentityToUnicode()));

        var type0 = new PdfDictionary(doc);
        doc.Internals.AddObject(type0);
        type0.Elements.SetName("/Type", "/Font");
        type0.Elements.SetName("/Subtype", "/Type0");
        type0.Elements.SetName("/BaseFont", "/GlyphLessFont");
        type0.Elements.SetName("/Encoding", "/Identity-H");
        type0.Elements["/DescendantFonts"] = new PdfArray(doc, cid.Reference!);
        type0.Elements.SetReference("/ToUnicode", toUnicode);
        return type0;
    }

    /// <summary>Code = UTF-16 unit, for the whole BMP except surrogates (ranges may not cross a high byte).</summary>
    private static string IdentityToUnicode()
    {
        var ranges = Enumerable.Range(0, 256).Where(hi => hi is < 0xD8 or > 0xDF).ToList();
        var sb = new StringBuilder("""
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
            /CMapName /Adobe-Identity-UCS def
            /CMapType 2 def
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange

            """);
        foreach (var chunk in ranges.Chunk(100))
        {
            sb.Append(chunk.Length).Append(" beginbfrange\n");
            foreach (var hi in chunk) sb.Append($"<{hi:X2}00> <{hi:X2}FF> <{hi:X2}00>\n");
            sb.Append("endbfrange\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    /// <summary>A valid TrueType font: an empty .notdef and glyph 1, a 1000 × 1000 box (never drawn).</summary>
    internal static byte[] GlyphlessTrueType()
    {
        static byte[] B(params (int Size, long Value)[] fields)
        {
            var bytes = new byte[fields.Sum(f => f.Size)];
            int o = 0;
            foreach (var (size, v) in fields)
            {
                if (size == 2) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(o), (ushort)v);
                else if (size == 4) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(o), (uint)v);
                else if (size == 8) BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(o), v);
                o += size;
            }
            return bytes;
        }
        var name = NameTable();
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["OS/2"] = B((2, 1), (2, 1000), (2, 400), (2, 5), (2, 0), (2, 650), (2, 700), (2, 0), (2, 140), (2, 650), (2, 700),
                         (2, 0), (2, 480), (2, 50), (2, 250), (2, 0), (4, 0), (4, 0), (2, 0), (4, 0), (4, 0), (4, 0), (4, 0),
                         (4, 0x57534849 /* WSHI */), (2, 0x40), (2, 0x20), (2, 0xFFFF), (2, 800), (2, unchecked((ushort)-200)), (2, 0),
                         (2, 800), (2, 200), (4, 1), (4, 0)),
            ["cmap"] = B((2, 0), (2, 1), (2, 3), (2, 1), (4, 12),
                         (2, 4), (2, 24), (2, 0), (2, 2), (2, 2), (2, 0), (2, 0), (2, 0xFFFF), (2, 0), (2, 0xFFFF), (2, 1), (2, 0)),
            // glyph 1: one contour, four on-curve points (0,-200) (1000,-200) (1000,800) (0,800)
            ["glyf"] = B((2, 1), (2, 0), (2, unchecked((ushort)-200)), (2, 1000), (2, 800), (2, 3), (2, 0),
                         (2, 0x0101), (2, 0x0101),
                         (2, 0), (2, 1000), (2, 0), (2, unchecked((ushort)-1000)),
                         (2, unchecked((ushort)-200)), (2, 0), (2, 1000), (2, 0)),
            ["head"] = B((4, 0x00010000), (4, 0x00010000), (4, 0), (4, 0x5F0F3CF5), (2, 0x000B), (2, 1000), (8, 0), (8, 0),
                         (2, 0), (2, unchecked((ushort)-200)), (2, 1000), (2, 800), (2, 0), (2, 8), (2, 2), (2, 0), (2, 0)),
            ["hhea"] = B((4, 0x00010000), (2, 800), (2, unchecked((ushort)-200)), (2, 0), (2, 1000), (2, 0), (2, 0), (2, 1000),
                         (2, 1), (2, 0), (2, 0), (2, 0), (2, 0), (2, 0), (2, 0), (2, 0), (2, 1)),
            ["hmtx"] = B((2, 1000), (2, 0), (2, 0)),
            ["loca"] = B((2, 0), (2, 0), (2, 17)),   // short offsets (÷2): .notdef empty, glyph 1 = 34 bytes
            ["maxp"] = B((4, 0x00010000), (2, 2), (2, 4), (2, 1), (2, 0), (2, 0), (2, 2), (2, 0), (2, 0), (2, 0), (2, 0),
                         (2, 0), (2, 0), (2, 0), (2, 0)),
            ["name"] = name,
            ["post"] = B((4, 0x00030000), (4, 0), (2, unchecked((ushort)-100)), (2, 50), (4, 0), (4, 0), (4, 0), (4, 0), (4, 0)),
        };
        int n = tables.Count, offset = 12 + 16 * n;
        int pow = 1, log = 0;
        while (pow * 2 <= n) { pow *= 2; log++; }
        using var ms = new MemoryStream();
        ms.Write(B((4, 0x00010000), (2, n), (2, pow * 16), (2, log), (2, n * 16 - pow * 16)));
        var dir = new List<byte[]>();
        var body = new MemoryStream();
        foreach (var (tag, data) in tables)
        {
            var padded = new byte[(data.Length + 3) & ~3];
            data.CopyTo(padded, 0);
            dir.Add([.. Encoding.ASCII.GetBytes(tag), .. B((4, Checksum(padded)), (4, offset + body.Length), (4, data.Length))]);
            body.Write(padded);
        }
        foreach (var d in dir) ms.Write(d);
        ms.Write(body.ToArray());
        var font = ms.ToArray();
        // head.checkSumAdjustment = 0xB1B0AFBA − checksum of the whole font
        int headOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(12 + 16 * tables.Keys.ToList().IndexOf("head") + 8));
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8), unchecked(0xB1B0AFBA - Checksum(font)));
        return font;
    }

    private static byte[] NameTable()
    {
        (int Id, string Text)[] names = [(1, "GlyphLessFont"), (2, "Regular"), (4, "GlyphLessFont"), (6, "GlyphLessFont")];
        var strings = names.Select(n => Encoding.BigEndianUnicode.GetBytes(n.Text)).ToList();
        using var ms = new MemoryStream();
        var buf = new byte[2];
        void U16(int v) { BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)v); ms.Write(buf); }
        U16(0); U16(names.Length); U16(6 + 12 * names.Length);
        int off = 0;
        for (int i = 0; i < names.Length; i++)
        {
            U16(3); U16(1); U16(0x409); U16(names[i].Id); U16(strings[i].Length); U16(off);
            off += strings[i].Length;
        }
        foreach (var s in strings) ms.Write(s);
        return ms.ToArray();
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i + 3 < data.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
        int rest = data.Length % 4;
        if (rest > 0)
        {
            Span<byte> last = stackalloc byte[4];
            data.AsSpan(data.Length - rest).CopyTo(last);
            sum += BinaryPrimitives.ReadUInt32BigEndian(last);
        }
        return sum;
    }

    // ------------------------------------------------------------------ helpers
    private static PdfDictionary Stream(PdfDocument doc, byte[] data)
    {
        var d = new PdfDictionary(doc);
        doc.Internals.AddObject(d);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        d.CreateStream(ms.ToArray());
        d.Elements.SetName("/Filter", "/FlateDecode");
        return d;
    }

    private static void SetStream(PdfDictionary content, byte[] bytes)
    {
        if (content.Stream is null) content.CreateStream(bytes);
        else content.Stream.Value = bytes;
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;

    private static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
