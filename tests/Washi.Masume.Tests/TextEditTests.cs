// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Washi.Core;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Editing page text: the old glyphs go, the new text is drawn in their place, and nothing else moves.</summary>
public sealed class TextEditTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-textedit-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

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

    private static string Text(string pdf, int page = 0) => Locked(() =>
    {
        using var d = PdfiumDocument.Open(pdf);
        return d.GetPageText(page);
    });

    private string Form() => Pdf("form.pdf", """
        BT /F1 12 Tf 30 250 Td (Name: Taro Yamada, Tokyo) Tj ET
        0 0 1 rg BT /F1 14 Tf 30 200 Td (Total due: 1,200 yen) Tj ET
        BT /F1 12 Tf 30 150 Td (Yamada) Tj ET
        """, "/Font << /F1 5 0 R >>", extra: Helvetica);

    [Fact]
    public void Segments_are_the_lines_of_the_page()
    {
        var segments = Locked(() => PdfTextEditor.Segments(Form(), 0));
        Assert.Equal(["Name: Taro Yamada, Tokyo", "Total due: 1,200 yen", "Yamada"], segments.Select(s => s.Text));
        var total = segments[1];
        Assert.Equal(14, total.Size, 1);
        Assert.Equal(0x0000FF, total.Color);
        Assert.Equal(30, total.OriginX, 0);
        Assert.Equal(200, total.OriginY, 0);
        Assert.True(total.Horizontal);
        Assert.False(total.Serif);
    }

    [Fact]
    public void A_word_is_replaced_in_the_documents_own_font()
    {
        var input = Form();
        var results = Locked(() => PdfTextEditor.Replace(input, P("out.pdf"), "1,200", "1,350"));
        var r = Assert.Single(results);
        Assert.True(r.KeptFont);
        Assert.Equal("Helvetica", r.Font);
        var text = Text(P("out.pdf"));
        Assert.Contains("Total due: 1,350 yen", text);
        Assert.Contains("Name: Taro Yamada, Tokyo", text);
        Assert.DoesNotContain("1,200", text);
        // drawn in the old colour and size
        var line = Locked(() => PdfTextEditor.Segments(P("out.pdf"), 0)).Single(s => s.Text.Contains("1,350"));
        Assert.Equal(0x0000FF, line.Color);
        Assert.Equal(14, line.Size, 1);
    }

    [Fact]
    public void A_longer_word_may_not_run_into_the_rest_of_the_line()
    {
        var input = Form();
        // "Yamada, Tokyo" has text after it
        Assert.Equal("Err_EditTooLong", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfTextEditor.Replace(input, P("long.pdf"), "Yamada", "Yamamoto-Takahashi"))).Key);
        Assert.False(File.Exists(P("long.pdf")));
    }

    [Fact]
    public void A_whole_line_may_grow()
    {
        var input = Form();
        var seg = Locked(() => PdfTextEditor.Segments(input, 0))[2];
        var results = Locked(() => PdfTextEditor.Apply(input, P("grow.pdf"), [new TextEdit(0, seg.Box, "Yamamoto-Takahashi")]));
        Assert.True(Assert.Single(results).Wider);
        Assert.Contains("Yamamoto-Takahashi", Text(P("grow.pdf")));
    }

    [Fact]
    public void Characters_the_font_lacks_come_from_a_similar_font()
    {
        var input = Form();
        var seg = Locked(() => PdfTextEditor.Segments(input, 0))[2];
        var r = Assert.Single(Locked(() => PdfTextEditor.Apply(input, P("ja.pdf"), [new TextEdit(0, seg.Box, "山田太郎")])));
        Assert.False(r.KeptFont);
        Assert.Contains("山田太郎", Text(P("ja.pdf")));
        Assert.EndsWith("山田太郎", Text(P("ja.pdf")).TrimEnd());
    }

    [Fact]
    public void Any_character_of_a_whole_simple_font_is_kept()
    {
        var input = Form();
        var seg = Locked(() => PdfTextEditor.Segments(input, 0))[2];
        var r = Assert.Single(Locked(() => PdfTextEditor.Apply(input, P("euro.pdf"), [new TextEdit(0, seg.Box, "€ 5")])));
        Assert.True(r.KeptFont);   // Helvetica with WinAnsi has the euro sign
        Assert.Contains("€ 5", Text(P("euro.pdf")));
    }

    [Fact]
    public void Places_without_text_or_over_two_lines_are_refused()
    {
        var input = Form();
        Assert.Equal("Err_EditNoText", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfTextEditor.Apply(input, P("a.pdf"), [new TextEdit(0, new PdfRect(300, 10, 390, 40), "x")]))).Key);
        Assert.Equal("Err_EditOneLine", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfTextEditor.Apply(input, P("b.pdf"), [new TextEdit(0, new PdfRect(20, 40, 390, 160), "x")]))).Key);
        Assert.Equal("Err_EditNotFound", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfTextEditor.Replace(input, P("c.pdf"), "Osaka", "Kyoto"))).Key);
    }

    [Fact]
    public void Scaled_kerned_and_rotated_text_reads_in_order()
    {
        var input = Pdf("scaled.pdf", """
            q 0 2 -2 0 300 0 cm BT /F1 6 Tf 1 Tc 15 125 Td [(Total ) -50 (due: 1,200 yen) 200 (!)] TJ 0 -10 Td (next line) Tj ET Q
            """, "/Font << /F1 5 0 R >>", pageExtra: "/Rotate 90", extra: Helvetica);
        // the page is turned a quarter: the text runs up the page in PDF space and left to right as shown
        Assert.All(Locked(() => PdfTextEditor.Segments(input, 0)), s => Assert.True(s.Horizontal));
        var r = Assert.Single(Locked(() => PdfTextEditor.Replace(input, P("out.pdf"), "1,200", "9,999")));
        Assert.True(r.KeptFont);
        var text = Text(P("out.pdf"));
        Assert.DoesNotContain("1,200", text);
        // (the character spacing makes the text layer space the letters out, before and after)
        Assert.Contains("due:9,999yen!next", System.Text.RegularExpressions.Regex.Replace(text, @"\s+", ""));
        Assert.Contains("next line", text);
        // drawn at the size it shows at (6 pt doubled), not the size the content gave the font
        var line = Locked(() => PdfTextEditor.Segments(P("out.pdf"), 0)).First(s => s.Text.Contains("9,999"));
        Assert.Equal(12, line.Size, 0);
    }

    [Fact]
    public void A_turned_page_with_many_lines_in_one_text_object()
    {
        var input = Pdf("turned.pdf", """
            BT /F1 1 Tf 0 5 -5 0 300 100 Tm (Heading) Tj 0 8 -8 0 73 52 Tm [(Hello) 14.7 (World)] TJ /F2 1 Tf 0 8.0987 -7.9772 0 26.4198 24.2528 Tm (Third line) Tj ET
            """, "/Font << /F1 5 0 R /F2 6 0 R >>", pageExtra: "/Rotate 90", extra: [Helvetica, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>"]);
        var segs = Locked(() => PdfTextEditor.Segments(input, 0));
        Assert.All(segs, s => Assert.True(s.Horizontal));
        var hello = segs.Single(s => s.Text.StartsWith("Hello"));
        Assert.Equal(8, hello.Size, 1);
        var r = Assert.Single(Locked(() => PdfTextEditor.Replace(input, P("out.pdf"), "World", "Earth")));
        Assert.True(r.KeptFont);
        Assert.Contains("HelloEarth", Text(P("out.pdf")));

        // and in a similar font
        var ja = Assert.Single(Locked(() => PdfTextEditor.Replace(input, P("ja.pdf"), "Third line", "三行目")));
        Assert.False(ja.KeptFont);
        Assert.Contains("三行目", Text(P("ja.pdf")));
    }

    [Fact]
    public void Text_that_runs_down_the_page_as_shown_is_refused()
    {
        var input = Pdf("down.pdf", "BT /F1 12 Tf 30 250 Td (Sideways) Tj ET", "/Font << /F1 5 0 R >>", pageExtra: "/Rotate 90", extra: Helvetica);
        Assert.False(Locked(() => PdfTextEditor.Segments(input, 0))[0].Horizontal);
        Assert.Equal("Err_EditNotHorizontal", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfTextEditor.Replace(input, P("out.pdf"), "Sideways", "Upright"))).Key);
    }

    [Fact]
    public void Text_scaled_by_its_matrix_keeps_its_size()
    {
        var input = Pdf("tm.pdf", "BT /F1 1 Tf 12 0 0 12 30 250 Tm (Invoice 2025) Tj ET", "/Font << /F1 5 0 R >>", extra: Helvetica);
        Assert.Equal(12, Locked(() => PdfTextEditor.Segments(input, 0))[0].Size, 1);
        Locked(() => PdfTextEditor.Replace(input, P("out.pdf"), "2025", "2026"));
        var seg = Assert.Single(Locked(() => PdfTextEditor.Segments(P("out.pdf"), 0)), s => s.Text.Contains("2026"));
        Assert.Equal(12, seg.Size, 1);
        Assert.Contains("Invoice 2026", Text(P("out.pdf")));
    }

    [Fact]
    public async Task Agents_find_and_replace_or_change_a_rect()
    {
        var input = Form();
        var tools = Washi.Tools.WashiTools.CreateRegistry(new Washi.Tools.PathPolicy([_dir]));
        var edit = tools.Find("edit_text")!;
        var seg = Locked(() => PdfTextEditor.Segments(input, 0))[2];
        var r = await edit.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            path = input,
            edits = new object[]
            {
                new { find = "1,200", text = "1,350" },
                new { page = 1, rect = new[] { seg.Box.X0, seg.Box.Y0, seg.Box.X1, seg.Box.Y1 }, text = "Suzuki" },
            },
        }), TestContext.Current.CancellationToken);
        Assert.False(r.IsError, r.Text);
        var json = System.Text.Json.JsonDocument.Parse(r.Text).RootElement;
        Assert.Equal(2, json.GetProperty("changes").GetArrayLength());
        Assert.True(json.GetProperty("changes")[0].GetProperty("kept_font").GetBoolean());
        var text = Text(P("form_edited.pdf"));
        Assert.Contains("Total due: 1,350 yen", text);
        Assert.Contains("Suzuki", text);

        var missing = await edit.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            path = input, edits = new[] { new { find = "Osaka", text = "Kyoto" } }, output = P("none.pdf"),
        }), TestContext.Current.CancellationToken);
        Assert.True(missing.IsError);
        Assert.False(File.Exists(P("none.pdf")));
    }
}
