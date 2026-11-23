// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Washi.Core;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Paragraphs: found from their lines, set again in their width, font, size and spacing, grown only into free space.</summary>
public sealed class TextReflowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-reflow-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>A 400 × 400 page: /F1 Helvetica, /F2 a Japanese CID font (UTF-16), /F3 Times.</summary>
    private string Doc(string name, string content)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R /F3 8 0 R >> >> >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /KozMinPr6N-Regular /Encoding /UniJIS-UTF16-H /DescendantFonts [7 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /KozMinPr6N-Regular /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >> /DW 1000 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>",
        ];
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString())); sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(P(name), Encoding.Latin1.GetBytes(sb.ToString()));
        return P(name);
    }

    /// <summary>Lines of text, one under another (y is the first baseline in PDF space; x of each line given).</summary>
    private static string Lines(string font, double size, double leading, double y, params (double X, string Text)[] lines) =>
        string.Join("\n", lines.Select((l, i) => font == "/F2"
            ? $"BT /F2 {size} Tf {l.X} {y - i * leading} Td <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(l.Text))}> Tj ET"
            : $"BT {font} {size} Tf {l.X} {y - i * leading} Td ({l.Text}) Tj ET"));

    private string Letter() => Doc("letter.pdf",
        Lines("/F1", 16, 20, 370, (30, "Notice of change")) + "\n" +
        Lines("/F1", 10, 13, 340, (40, "We are writing to let you know that the"), (30, "office will move to a new address next"), (30, "month. Please update your records.")) + "\n" +
        Lines("/F1", 10, 13, 120, (30, "Thank you.")));

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private static List<TextSegment> Lines(string pdf) => Locked(() => PdfTextEditor.Segments(pdf, 0)).OrderBy(s => s.Box.Y0).ToList();

    [Fact]
    public void Paragraphs_are_lines_in_one_font_at_an_even_spacing()
    {
        var paras = Locked(() => PdfTextEditor.Paragraphs(Letter(), 0));
        Assert.Equal(["Notice of change",
                      "We are writing to let you know that the office will move to a new address next month. Please update your records.",
                      "Thank you."], paras.Select(p => p.Text));
        Assert.Equal([1, 3, 1], paras.Select(p => p.Lines));
    }

    [Fact]
    public void A_longer_paragraph_is_set_again_in_its_width_and_grows_into_free_space()
    {
        var input = Letter();
        var para = Locked(() => PdfTextEditor.Paragraphs(input, 0))[1];
        var text = "We are writing to let you know that our office will move to a new address on the first of next month. " +
                   "From then on, please send all letters and parcels to the new address. Thank you for updating your records.";
        var r = Assert.Single(Locked(() => PdfTextEditor.Reflow(input, P("out.pdf"), [new TextEdit(0, para.Box, text)])));
        Assert.True(r.KeptFont);
        Assert.True(r.Wider);                                            // more lines than before
        var lines = Lines(P("out.pdf")).Where(l => l.Size < 11 && l.Text != "Thank you.").ToList();
        Assert.True(lines.Count > 3);
        Assert.Equal(40, lines[0].Box.X0, 0);                            // the first line's indent kept
        Assert.All(lines.Skip(1), l => Assert.Equal(30, l.Box.X0, 0));
        Assert.All(lines, l => Assert.True(l.Box.X1 <= para.Box.X1 + 1)); // within the paragraph's width
        var gaps = lines.Zip(lines.Skip(1), (a, b) => b.Box.Y1 - a.Box.Y1).ToList();
        Assert.All(gaps, g => Assert.Equal(13, g, 0));                   // the same spacing
        Assert.Equal(Norm(text), Norm(string.Concat(lines.Select(l => l.Text))));
        Assert.Contains("Thank you.", Lines(P("out.pdf")).Select(l => l.Text));   // the paragraph below stays
    }

    [Fact]
    public void A_paragraph_may_not_grow_over_what_is_below_it()
    {
        var input = Doc("tight.pdf",
            Lines("/F1", 10, 13, 340, (30, "A short paragraph of two lines that"), (30, "fills its width.")) + "\n" +
            Lines("/F1", 10, 13, 300, (30, "The next paragraph starts here.")));
        var para = Locked(() => PdfTextEditor.Paragraphs(input, 0))[0];
        var longer = string.Join(" ", Enumerable.Repeat("This sentence is much longer than the old one.", 4));
        Assert.Equal("Err_ParagraphTooLong", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfTextEditor.Reflow(input, P("x.pdf"), [new TextEdit(0, para.Box, longer)]))).Key);
        Assert.False(File.Exists(P("x.pdf")));
        // shorter: fewer lines, the rest of the page as it was
        Locked(() => PdfTextEditor.Reflow(input, P("short.pdf"), [new TextEdit(0, para.Box, "Shorter now.")]));
        Assert.Equal(["Shorter now.", "The next paragraph starts here."], Lines(P("short.pdf")).Select(l => l.Text));
    }

    [Fact]
    public void Japanese_breaks_between_characters_and_keeps_closing_marks_off_the_start_of_a_line()
    {
        var input = Doc("ja.pdf", Lines("/F2", 10, 15, 340, (30, "本書は、申告書の記入方法を説明します。"), (30, "不明な点は窓口へ。")));
        var para = Locked(() => PdfTextEditor.Paragraphs(input, 0))[0];
        Assert.Equal("本書は、申告書の記入方法を説明します。不明な点は窓口へ。", para.Text);
        var text = "本書は、令和九年分の申告書の記入方法と、提出の期限について説明します。不明な点は、お近くの窓口へお問い合わせください。";
        Locked(() => PdfTextEditor.Reflow(input, P("out.pdf"), [new TextEdit(0, para.Box, text)]));
        var lines = Lines(P("out.pdf"));
        Assert.True(lines.Count >= 3);
        Assert.All(lines.Skip(1), l => Assert.DoesNotContain(l.Text[0], "、。」）"));
        Assert.Equal(Norm(text), Norm(string.Concat(lines.Select(l => l.Text))));
    }

    [Fact]
    public void A_line_break_in_the_new_text_starts_a_new_line()
    {
        var input = Letter();
        var para = Locked(() => PdfTextEditor.Paragraphs(input, 0))[1];
        Locked(() => PdfTextEditor.Reflow(input, P("out.pdf"), [new TextEdit(0, para.Box, "Our office is moving.\nNew address: 1-2-3 Chiyoda.")]));
        var lines = Lines(P("out.pdf")).Where(l => l.Size < 11 && l.Text != "Thank you.").Select(l => l.Text).ToList();
        Assert.Equal(["Our office is moving.", "New address: 1-2-3 Chiyoda."], lines);
    }

    [Fact]
    public void Digits_in_another_font_stay_in_the_paragraph_and_mixed_sizes_are_refused()
    {
        // a Japanese line whose figures are in Helvetica, as Japanese documents often draw them
        var ja = (string t) => $"<{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(t))}>";
        var input = Doc("digits.pdf",
            $"BT /F2 10 Tf 30 340 Td {ja("提出期限は")} Tj ET BT /F1 10 Tf 80 340 Td (2027) Tj ET BT /F2 10 Tf 103 340 Td {ja("年3月15日です。")} Tj ET\n" +
            $"BT /F2 10 Tf 30 325 Td {ja("遅れないようにご注意ください。")} Tj ET");
        var para = Assert.Single(Locked(() => PdfTextEditor.Paragraphs(input, 0)));
        Assert.Equal(2, para.Lines);
        Assert.Equal("提出期限は2027年3月15日です。遅れないようにご注意ください。", para.Text);
        Locked(() => PdfTextEditor.Reflow(input, P("out.pdf"), [new TextEdit(0, para.Box, "提出期限は2028年3月15日です。")]));
        Assert.Equal(["提出期限は2028年3月15日です。"], Lines(P("out.pdf")).Select(l => l.Text));

        var sizes = Doc("sizes.pdf", Lines("/F1", 10, 14, 340, (30, "Small words here and")) + "\n" + Lines("/F1", 16, 14, 326, (30, "BIG WORDS")));
        Assert.Equal("Err_ParagraphMixed", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfTextEditor.Reflow(sizes, P("x.pdf"), [new TextEdit(0, new PdfRect(20, 50, 300, 80), "New")]))).Key);
    }

    [Fact]
    public async Task Agents_name_a_paragraph_by_a_phrase_in_it()
    {
        var input = Letter();
        var tools = Washi.Tools.WashiTools.CreateRegistry(new Washi.Tools.PathPolicy([_dir]));
        var r = await tools.Find("edit_text")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            path = input,
            edits = new object[]
            {
                new { paragraph_containing = "office will move", text = "The office stays where it is." },
                new { find = "Notice of change", text = "Notice" },
            },
        }), TestContext.Current.CancellationToken);
        Assert.False(r.IsError, r.Text);
        var lines = Lines(P("letter_edited.pdf")).Select(l => l.Text).ToList();
        Assert.Equal(["Notice", "The office stays where it is.", "Thank you."], lines);
    }

    private static string Norm(string s) => string.Concat(s.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
}
