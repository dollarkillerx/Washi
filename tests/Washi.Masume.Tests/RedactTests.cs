// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Fonts;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Redaction: what was under a black box is gone from the file, and nothing else on the page moved.</summary>
public sealed class RedactTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-redact-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private static readonly RedactSearch Yamada = new(["Yamada"]);

    /// <summary>
    /// A 400 × 300 pt page written by hand: objects 1-4 are the catalog, pages, page and its content; <paramref name="extra"/>
    /// become objects 5, 6, … (a string is a dictionary, a (dict, data) pair a stream).
    /// </summary>
    private string Pdf(string name, string content, string resources, string pageExtra = "", string catalogExtra = "", string trailerExtra = "",
                       params object[] extra)
    {
        var objects = new List<byte[]>
        {
            Ascii($"<< /Type /Catalog /Pages 2 0 R {catalogExtra}>>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Contents 4 0 R /Resources << {resources} >> {pageExtra}>>"),
            Stream("", Ascii(content)),
        };
        foreach (var e in extra)
            objects.Add(e switch
            {
                string d => Ascii(d),
                (string d, byte[] data) => Stream(d, data),
                _ => throw new ArgumentException("object"),
            });
        var path = P(name);
        using var ms = new MemoryStream();
        ms.Write(Ascii("%PDF-1.7\n"));
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            ms.Write(Ascii($"{i + 1} 0 obj\n"));
            ms.Write(objects[i]);
            ms.Write(Ascii("\nendobj\n"));
        }
        long xref = ms.Position;
        var sb = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailerExtra}>>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(Ascii(sb.ToString()));
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static byte[] Stream(string dict, byte[] data) =>
        [.. Ascii($"<< {dict}/Length {data.Length} >>\nstream\n"), .. data, .. Ascii("\nendstream")];

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private static (IReadOnlyList<RedactMatch> Found, RedactReport Report) Redact(string input, string output, RedactSearch search) =>
        Locked(() =>
        {
            var found = PdfRedactor.Find(input, search);
            return (found, PdfRedactor.Redact(input, output, PdfRedactor.Areas(found), search));
        });

    private static string Text(string pdf) => Locked(() =>
    {
        using var d = PdfiumDocument.Open(pdf);
        return d.GetPageText(0);
    });

    /// <summary>Every stream and string in the file, decoded: what someone digging through the PDF would find.</summary>
    private static string Everything(string pdf)
    {
        using var d = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        var sb = new StringBuilder();
        foreach (var o in d.Internals.GetAllObjects().OfType<PdfDictionary>())
        {
            if (o.Stream is not null) sb.Append(Encoding.Latin1.GetString(o.Stream.UnfilteredValue)).Append('\n');
            foreach (var v in o.Elements.Values) if (v is PdfString s) sb.Append(s.Value).Append('\n');
        }
        foreach (var v in d.Info.Elements.Values) if (v is PdfString s) sb.Append(s.Value).Append('\n');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ text

    [Fact]
    public void Words_go_glyph_by_glyph_and_the_rest_of_the_line_stays()
    {
        // Helvetica without /Widths (the standard widths are used), then a TJ with kerning, character and word spacing
        var input = Pdf("lines.pdf", """
            BT /F1 12 Tf 30 250 Td (Name: Taro Yamada, Tokyo) Tj ET
            BT /F1 11 Tf 1.5 Tc 4 Tw 30 220 Td [(Please call Ya) -120 (mada or YAMADA now) 300 (.)] TJ ET
            BT /F1 12 Tf 30 190 Td (Yamadaya is a shop.) Tj ET
            """, "/Font << /F1 5 0 R >>", extra: Helvetica);

        var (found, report) = Redact(input, P("out.pdf"), Yamada);
        Assert.Equal(4, found.Count);               // case does not matter; "Yamadaya" contains it
        Assert.Equal(4 * 6, report.Glyphs);
        var text = Text(P("out.pdf"));
        Assert.DoesNotContain("yamada", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Name: Taro", text);
        Assert.Contains(", Tokyo", text);
        Assert.Contains("now", text);
        Assert.Contains("ya is a shop.", text);
        Assert.DoesNotContain("Yamada", Everything(P("out.pdf")));
        Assert.DoesNotContain("mada", Everything(P("out.pdf")));   // not even the part after the kerning
        Assert.Equal(4, report.Boxes.Count);

        // whole words only: the shop's name stays
        var (whole, _) = Redact(input, P("whole.pdf"), Yamada with { WholeWords = true });
        Assert.Equal(3, whole.Count);
        Assert.Contains("Yamadaya", Text(P("whole.pdf")));
    }

    [Fact]
    public void Embedded_composite_fonts_lose_only_the_matched_glyphs()
    {
        var fontPath = new[]
        {
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans.ttf",
            @"C:\Windows\Fonts\arial.ttf",
        }.FirstOrDefault(File.Exists);
        Assert.SkipWhen(fontPath is null, "no TrueType font on this machine");
        var font = OpenTypeFont.Load(fontPath!);
        string Hex(string s) => string.Concat(s.Select(c => font.UnicodeToGlyph[c].ToString("X4")));

        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(400);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(300);
        var f = PdfFontEmbedder.Embed(doc, font);
        var res = new PdfDictionary(doc);
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/F0"] = f.Reference;
        res.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = res;
        var content = $"BT /F0 14 Tf 30 250 Td <{Hex("Dear Mr Yamada, hello")}> Tj ET\nBT /F0 14 Tf 0 1 -1 0 300 60 Tm <{Hex("Up Yamada side")}> Tj ET";
        var cs = new PdfDictionary(doc);
        cs.CreateStream(Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(cs);
        page.Elements["/Contents"] = cs.Reference;
        doc.Save(P("cid.pdf"));

        var (found, report) = Redact(P("cid.pdf"), P("out.pdf"), Yamada);
        Assert.Equal(2, found.Count);
        Assert.Equal(12, report.Glyphs);
        var text = Text(P("out.pdf"));
        Assert.DoesNotContain("Yamada", text);
        Assert.Contains("Dear Mr", text);
        Assert.Contains(", hello", text);
        Assert.Contains("side", text);
        Assert.DoesNotContain(Hex("Yamada"), Everything(P("out.pdf")).Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_run_whose_widths_are_unknown_goes_whole_with_the_runs_chained_to_it_and_is_covered()
    {
        // an embedded font without /Widths and without a standard name: positions after a removed glyph cannot be known
        var ttf = new[] { "/System/Library/Fonts/Supplemental/Arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", @"C:\Windows\Fonts\arial.ttf" }
            .FirstOrDefault(File.Exists);
        Assert.SkipWhen(ttf is null, "no TrueType font on this machine");
        var program = File.ReadAllBytes(ttf!);
        var input = Pdf("chain.pdf", """
            BT /F2 12 Tf 30 250 Td (AA ) Tj (Yamada) Tj ( BB) Tj 0 -30 Td (CC) Tj ET
            """, "/Font << /F2 5 0 R >>",
            extra:
            [
                "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Mystery /Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>",
                "<< /Type /FontDescriptor /FontName /ABCDEF+Mystery /Flags 32 /FontBBox [-665 -325 2000 1006] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 80 /FontFile2 7 0 R >>",
                ($"/Length1 {program.Length} ", program),
            ]);

        var (found, report) = Redact(input, P("out.pdf"), Yamada);
        Assert.Single(found);
        var text = Text(P("out.pdf"));
        Assert.Contains("AA", text);
        Assert.DoesNotContain("Yamada", text);
        Assert.DoesNotContain("BB", text);       // chained to the removed run: removed, and its place covered
        Assert.Contains("CC", text);             // placed anew: stays
        Assert.True(report.Boxes.Max(b => b.Rect.X1) > found[0].Boxes[0].X1 + 10);
    }

    // ------------------------------------------------------------------ images

    [Fact]
    public void Pixels_of_a_scan_under_the_box_are_blanked_and_its_hidden_text_removed()
    {
        // a 200 × 40 pixel "scan" (mid grey) with an invisible text layer, as OCR leaves it
        var pixels = Enumerable.Repeat((byte)128, 200 * 40).ToArray();
        var packed = new PdfSharp.Pdf.Filters.FlateDecode().Encode(pixels, PdfFlateEncodeMode.Default);
        var input = Pdf("scan.pdf", """
            q 200 0 0 40 30 200 cm /Im1 Do Q
            BT 3 Tr /F1 12 Tf 34 214 Td (Ref Yamada 2026) Tj ET
            """, "/Font << /F1 5 0 R >> /XObject << /Im1 6 0 R >>",
            extra: [Helvetica, ("/Type /XObject /Subtype /Image /Width 200 /Height 40 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode ", packed)]);

        var (found, report) = Redact(input, P("out.pdf"), Yamada);
        Assert.Single(found);
        Assert.Equal(1, report.Images);
        Assert.DoesNotContain("Yamada", Text(P("out.pdf")));
        Assert.Contains("Ref", Text(P("out.pdf")));

        using var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import);
        var images = d.Internals.GetAllObjects().OfType<PdfDictionary>().Where(o => o.Elements.GetName("/Subtype") == "/Image").ToList();
        var img = Assert.Single(images);                 // the original is gone from the file, not just unused
        var data = img.Stream!.UnfilteredValue;
        var box = found[0].Boxes[0];
        // pixel (x, y) of the image covers page x 30 + x, upright y 60 + y
        Assert.Equal(0, data[20 * 200 + (int)(box.X0 - 30 + box.Width / 2)]);   // under the box
        Assert.Equal(128, data[20 * 200 + 2]);                                    // left of it
        Assert.Equal(128, data[2 * 200 + (int)(box.X0 - 30 + box.Width / 2)]);    // above it
    }

    private const string Jpeg = "/9j/wAALCAAQADABAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9sAQwABAQEBAQECAQECAwICAgMEAwMDAwQFBAQEBAQFBgUFBQUFBQYGBgYGBgYGBwcHBwcHCAgICAgJCQkJCQkJCQkJ/90ABAAG/9oACAEBAAA/AK/hr/go9/w9Y2/sQf8ACG/8IH/wm/P9tf2j/an2X+zP+Jn/AMev2W08zzPsnlf65Nu/f823a30B4a0L/h0Tt0/zf+Fhf8LC/i2/2T9j/sn2zeeb5v2z/Y2bP4t3y/QHhr4Af8Ndbf2n/wC1v+Ee/wCEm5/szyPtfkfY/wDQ/wDX+ZDv3+Rv/wBUuN23nGT9AeGvjh/wvnb4B/sv+yv7Q58/z/P2eT+++55cec7MfeGM556V9AeGtC/4QTbp/m/avtX8W3Zt2e2Wznd7dK+gPDXi/wD4Rrav2fz8c/f29ef7pr//0K/hr/go9/w9Y2/sQf8ACG/8IH/wm/P9tf2j/an2X+zP+Jn/AMev2W08zzPsnlf65Nu/f823a30B4a0L/h0Tt0/zf+Fhf8LC/i2/2T9j/sn2zeeb5v2z/Y2bP4t3y/QHhr4Af8Ndbf2n/wC1v+Ee/wCEm5/szyPtfkfY/wDQ/wDX+ZDv3+Rv/wBUuN23nGT9AeGvjh/wvnb4B/sv+yv7Q58/z/P2eT+++55cec7MfeGM556V9AeGtC/4QTbp/m/avtX8W3Zt2e2Wznd7dK+gPDXi/wD4Rrav2fz8c/f29ef7pr//2Q==";

    [Fact]
    public void A_jpeg_under_the_box_is_decoded_blanked_and_kept_lossless()
    {
        var input = Pdf("jpeg.pdf", """
            q 240 0 0 80 30 150 cm /Im1 Do Q
            """, "/XObject << /Im1 5 0 R >>",
            extra: ("/Type /XObject /Subtype /Image /Width 48 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode ", Convert.FromBase64String(Jpeg)));
        var area = new RedactArea(0, new PdfRect(60, 80, 140, 120));   // upright: page y 180..220, inside the picture

        var report = Locked(() => PdfRedactor.Redact(input, P("out.pdf"), [area]));
        Assert.Equal(1, report.Images);
        using var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import);
        var img = d.Internals.GetAllObjects().OfType<PdfDictionary>().Single(o => o.Elements.GetName("/Subtype") == "/Image");
        Assert.Equal("/FlateDecode", img.Elements.GetName("/Filter"));
        Assert.Equal("/DeviceGray", img.Elements.GetName("/ColorSpace"));
        var data = img.Stream!.UnfilteredValue;
        Assert.Equal(48 * 16, data.Length);
        // pixel column x covers page x 30 + 5x; row y covers upright 70 + 5y
        Assert.Equal(0, data[8 * 48 + 14]);
        Assert.NotEqual(0, data[1 * 48 + 1] + data[1 * 48 + 2] + data[14 * 48 + 46]);
    }

    // ------------------------------------------------------------------ outside the page text

    [Fact]
    public void Fields_comments_forms_and_properties_lose_the_word_too()
    {
        var input = Pdf("doc.pdf", """
            BT /F1 12 Tf 30 250 Td (Visible text) Tj ET
            q 1 0 0 1 30 150 cm /Fm1 Do Q
            /Span << /ActualText (Yamada) >> BDC BT /F1 12 Tf 30 100 Td (Y.) Tj ET EMC
            """, "/Font << /F1 5 0 R >> /XObject << /Fm1 6 0 R >>",
            pageExtra: "/Annots [7 0 R 8 0 R]",
            catalogExtra: "/AcroForm << /Fields [7 0 R] /DA (/Helv 10 Tf 0 g) >>",
            trailerExtra: "/Info 9 0 R",
            extra:
            [
                Helvetica,
                ("/Type /XObject /Subtype /Form /BBox [0 0 200 30] /Resources << /Font << /F1 5 0 R >> >> ",
                    Ascii("BT /F1 12 Tf 2 10 Td (Flattened: Yamada) Tj ET")),
                "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Taro Yamada) /Rect [250 240 380 260] /P 3 0 R >>",
                "<< /Type /Annot /Subtype /Text /Contents (Ask Yamada about this) /Rect [350 20 370 40] /P 3 0 R >>",
                "<< /Title (Notes on Yamada) /Author (Somebody) >>",
            ]);

        var (found, report) = Redact(input, P("out.pdf"), Yamada);
        Assert.Contains(found, m => m.Source == RedactSource.Field);
        Assert.Contains(found, m => m.Source == RedactSource.Comment);
        Assert.Contains(found, m => m.Source == RedactSource.Text && m.Context.Contains("Flattened"));
        Assert.Equal(2, report.Annotations);

        var all = Everything(P("out.pdf"));
        Assert.DoesNotContain("Yamada", all);
        Assert.Contains("Flattened:", Text(P("out.pdf")));
        using var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import);
        Assert.Equal("Notes on ■■■■■■", d.Info.Title);
        Assert.Equal("Somebody", d.Info.Author);
        Assert.Equal(0, (d.AcroForm?.Fields.Count) ?? 0);
        Assert.Null(d.Pages[0].Elements["/Annots"] is PdfArray { Elements.Count: > 0 } ? "annotations left" : null);
        Assert.Contains("Visible text", Text(P("out.pdf")));
    }

    [Fact]
    public void A_drawn_area_removes_drawings_inside_it_and_leaves_the_rest()
    {
        var input = Pdf("draw.pdf", """
            0 0 0 RG 2 w 40 240 m 120 260 l 90 230 l S
            0 0 1 rg 200 200 150 60 re f
            BT /F1 12 Tf 30 150 Td (Keep this line) Tj ET
            """, "/Font << /F1 5 0 R >>", extra: Helvetica);
        // the scribble (upright y 40..70) and a corner of the blue block
        var report = Locked(() => PdfRedactor.Redact(input, P("out.pdf"), [new RedactArea(0, new PdfRect(30, 30, 230, 75))]));
        Assert.Equal(1, report.Paths);                        // the block reaches outside: kept, covered where it is under the box
        Assert.Contains("Keep this line", Text(P("out.pdf")));
        Assert.DoesNotContain("120 260 l", Everything(P("out.pdf")));
        Assert.Contains("200 200 150 60 re", Everything(P("out.pdf")));
    }

    [Fact]
    public void Matches_left_out_stay_even_though_the_search_finds_them()
    {
        var input = Pdf("two.pdf", """
            BT /F1 12 Tf 30 250 Td (First Yamada) Tj ET
            BT /F1 12 Tf 30 200 Td (Second Yamada) Tj ET
            """, "/Font << /F1 5 0 R >>", extra: Helvetica);
        var report = Locked(() =>
        {
            var found = PdfRedactor.Find(input, Yamada);
            return PdfRedactor.Redact(input, P("out.pdf"), PdfRedactor.Areas(found.Take(1)), Yamada);
        });
        Assert.Equal(6, report.Glyphs);
        var text = Text(P("out.pdf"));
        Assert.Contains("Second Yamada", text);
        Assert.DoesNotContain("First Yamada", text);
    }

    [Fact]
    public async Task The_agent_tool_lists_matches_then_redacts()
    {
        var input = Pdf("agent.pdf", "BT /F1 12 Tf 30 250 Td (Call 03-1234-5678 or Yamada) Tj ET", "/Font << /F1 5 0 R >>", extra: Helvetica);
        var tools = Washi.Tools.WashiTools.CreateRegistry(new Washi.Tools.PathPolicy([_dir]));
        var redact = tools.Find("redact")!;
        Assert.Equal(Washi.Tools.ToolRisk.Write, redact.Risk);
        var dry = await redact.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = input, find = new[] { "yamada" }, patterns = new[] { "phone" }, dry_run = true }));
        Assert.False(dry.IsError, dry.Text);
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(dry.Text).RootElement.GetProperty("matches").GetArrayLength());
        Assert.False(File.Exists(P("agent_redacted.pdf")));

        var run = await redact.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = input, find = new[] { "yamada" }, patterns = new[] { "phone" } }));
        Assert.False(run.IsError, run.Text);
        Assert.True(File.Exists(P("agent_redacted.pdf")));
        var text = Text(P("agent_redacted.pdf"));
        Assert.DoesNotContain("5678", text);
        Assert.DoesNotContain("Yamada", text);
        Assert.Contains("Call", text);

        var again = await redact.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = input, find = new[] { "yamada" } }));
        Assert.True(again.IsError);                                   // never overwrites
        var bad = await redact.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = input, patterns = new[] { "dna" } }));
        Assert.True(bad.IsError);
    }

    // ------------------------------------------------------------------ searching

    [Theory]
    [InlineData("mail taro.yamada@example.co.jp now", RedactPattern.Email, "taro.yamada@example.co.jp")]
    [InlineData("電話 03-1234-5678 まで", RedactPattern.Phone, "03-1234-5678")]
    [InlineData("携帯 090 1234 5678", RedactPattern.Phone, "090 1234 5678")]
    [InlineData("call (555) 123-4567 today", RedactPattern.Phone, "(555) 123-4567")]
    [InlineData("個人番号 1234 5678 9012 です", RedactPattern.Number, "1234 5678 9012")]
    [InlineData("口座 １２３４５６７", RedactPattern.Number, "１２３４５６７")]
    public void Patterns_find_personal_data(string text, RedactPattern pattern, string expected)
    {
        var m = new Matcher(new RedactSearch([], [pattern]));
        var units = Matcher.Units(text);
        var hit = Assert.Single(m.Find(units));
        Assert.Equal(expected.Replace(" ", ""), string.Concat(hit.Select(i => units[i])));
    }

    [Fact]
    public void Version_numbers_and_dates_are_not_phone_numbers()
    {
        var m = new Matcher(new RedactSearch([], [RedactPattern.Phone]));
        Assert.False(m.Matches("x:xmptk=\"3.1-701\""));
        Assert.False(m.Matches("PDFsharp 6.2.4 (www.pdfsharp.com)"));
        Assert.False(m.Matches("2026-11-22"));
        Assert.True(m.Matches("TEL 0120-123-456"));
    }

    [Fact]
    public void Words_match_across_spaces_widths_and_case()
    {
        var m = new Matcher(new RedactSearch(["山田太郎", "abc"]));
        Assert.True(m.Matches("氏名 山田 太郎"));
        Assert.True(m.Matches("ＡＢＣ商事"));
        Assert.False(new Matcher(new RedactSearch(["abc"], MatchCase: true)).Matches("ABC"));
        Assert.Equal("氏名 ■■ ■■ 様", m.Mask("氏名 山田 太郎 様"));
        Assert.False(m.Matches("2023年 12月"));
        Assert.Throws<RedactException>(() => new Matcher(new RedactSearch([], Regexes: ["(unclosed"])));
    }

    // ------------------------------------------------------------------ the find bar's search

    [Fact]
    public async Task Search_finds_text_page_by_page_with_boxes()
    {
        var input = Pdf("find.pdf", """
            BT /F1 12 Tf 30 250 Td (Contact: Yamada Taro) Tj ET
            BT /F1 12 Tf 30 220 Td (Mr YAMADA called; Yamadaya closed.) Tj ET
            """, "/Font << /F1 5 0 R >>", extra: Helvetica);
        var hits = Locked(() => PdfSearch.Find(input, "yamada"));
        Assert.Equal(3, hits.Count);
        Assert.All(hits, h => Assert.Equal(0, h.Page));
        Assert.True(hits[0].Boxes[0].Y0 < hits[1].Boxes[0].Y0);           // the first line first
        Assert.Contains("Contact", hits[0].Context);
        Assert.InRange(hits[0].Boxes[0].Height, 8, 20);
        Assert.Equal(2, Locked(() => PdfSearch.Find(input, "yamada", wholeWords: true)).Count);
        Assert.Single(Locked(() => PdfSearch.Find(input, "YAMADA", matchCase: true)));
        Assert.Empty(Locked(() => PdfSearch.Find(input, "   ")));

        var tools = Washi.Tools.WashiTools.CreateRegistry(new Washi.Tools.PathPolicy([_dir]));
        var r = await tools.Find("search_text")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = input, text = "Yamada", max = 2 }));
        Assert.False(r.IsError, r.Text);
        var json = System.Text.Json.JsonDocument.Parse(r.Text).RootElement;
        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.True(json.GetProperty("more").GetBoolean());
        Assert.Equal(1, json.GetProperty("hits")[0].GetProperty("page").GetInt32());
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void Nothing_is_overwritten_and_protected_files_need_their_password()
    {
        var input = Pdf("p.pdf", "BT /F1 12 Tf 30 250 Td (Yamada) Tj ET", "/Font << /F1 5 0 R >>", extra: Helvetica);
        File.WriteAllText(P("exists.pdf"), "x");
        var area = new RedactArea(0, new PdfRect(20, 30, 200, 60));
        Assert.Equal("Err_Exists", Assert.Throws<RedactException>(() => Locked(() => PdfRedactor.Redact(input, P("exists.pdf"), [area]))).Key);
        Assert.Equal("Err_RedactNothing", Assert.Throws<RedactException>(() => Locked(() => PdfRedactor.Redact(input, P("x.pdf"), []))).Key);

        PdfSecurity.Protect(input, P("locked.pdf"), null, "boss", new PdfPermissions(Edit: false));
        var e = Assert.Throws<PageAssemblyException>(() => Locked(() => PdfRedactor.Redact(P("locked.pdf"), P("y.pdf"), [area])));
        Assert.Equal("Err_NeedsPermissionsPassword", e.Key);
        Assert.False(File.Exists(P("y.pdf")));
    }
}
