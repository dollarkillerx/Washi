// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pdfium;
using Washi.Masume;

// PDFium is process-global and not thread-safe.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Washi.Masume.Tests;

public sealed class FormWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>An A4 landscape form drawn with lines only: a label column, a text cell, a 12-box comb.</summary>
    private string MakeForm(int rotate = 0)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        // Portrait page shown landscape when rotated by 90/270, like scanned office forms.
        page.Width = XUnit.FromPoint(rotate is 90 or 270 ? 595 : 842);
        page.Height = XUnit.FromPoint(rotate is 90 or 270 ? 842 : 595);
        page.Rotate = rotate;
        using (var g = XGraphics.FromPdfPage(page))
        {
            var pen = new XPen(XColors.Black, 1);
            // draw in unrotated page space; PDFium shows it rotated
            g.DrawRectangle(pen, 40, 40, 400, 30);
            g.DrawLine(pen, 140, 40, 140, 70);
            g.DrawRectangle(pen, 40, 90, 240, 30);
            for (int i = 1; i < 12; i++) g.DrawLine(pen, 40 + i * 20, 112, 40 + i * 20, 120);
        }
        var path = Path.Combine(_dir, $"form_{rotate}.pdf");
        doc.Save(path);
        return path;
    }

    private static (PageSnapshot Page, IReadOnlyList<DetectedField> Fields) Detect(string pdf)
    {
        using var doc = PdfiumDocument.Open(pdf);
        var page = doc.Snapshot(0, MasumeDetector.Scale);
        return (page, MasumeDetector.Detect(page));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Widgets_land_exactly_where_the_cells_were_seen(int rotate)
    {
        var src = MakeForm(rotate);
        var (before, fields) = Detect(src);
        Assert.NotEmpty(fields);
        if (rotate is 0 or 180)   // at 90/270 the drawing is sideways, so the comb ticks lie flat
            Assert.Contains(fields, f => f.Kind == FieldKind.Comb && f.MaxLen == 12);

        var dst = Path.Combine(_dir, $"out_{rotate}.pdf");
        var result = AcroFormWriter.Write(src, dst, fields.Select(f => f.ToFormField()).ToList(), FormScript.Japanese);

        Assert.Equal(rotate == 0 ? 0 : 1, result.PagesStraightened);
        var (after, again) = Detect(dst);
        // the page looks the same as before: same size, the same cells found again
        Assert.Equal(before.Width, after.Width, 1);
        Assert.Equal(before.Height, after.Height, 1);
        Assert.Equal(fields.Count, again.Count);
        // and every written widget sits on its field
        Assert.Equal(fields.Count, after.Widgets.Count);
        foreach (var f in fields)
            Assert.Contains(after.Widgets, w => w.Rect.IoU(f.Rect) > 0.98);
    }

    [Theory]
    [InlineData(FormScript.Japanese, "/KozMin")]
    [InlineData(FormScript.ChineseSimplified, "/STSong")]
    [InlineData(FormScript.ChineseTraditional, "/MSung")]
    [InlineData(FormScript.Korean, "/HYSMyeongJo")]
    [InlineData(FormScript.Latin, "/Helv")]
    public void Text_fields_ask_for_the_font_of_the_form_language(FormScript script, string font)
    {
        var src = MakeForm();
        var dst = Path.Combine(_dir, "font.pdf");
        AcroFormWriter.Write(src, dst, [new FormField(0, FormFieldKind.Text, new PdfRect(150, 40, 440, 70), "name")], script);

        using var doc = PdfReader.Open(dst, PdfDocumentOpenMode.Import);
        var form = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!;
        var widget = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)form.Elements.GetArray("/Fields")!.Elements[0]).Value;
        Assert.StartsWith(font + " ", widget.Elements.GetString("/DA"));
        var fonts = form.Elements.GetDictionary("/DR")!.Elements.GetDictionary("/Font")!;
        foreach (var key in new[] { "/Helv", "/ZaDb", "/KozMin", "/STSong", "/MSung", "/HYSMyeongJo" })
            Assert.True(fonts.Elements.ContainsKey(key), key);
    }

    [Fact]
    public void A_field_keeps_its_size_colour_and_face_and_a_long_value_is_shrunk_to_fit()
    {
        var src = MakeForm();
        var (_, detected) = Detect(src);
        var text = detected.First(f => f.Kind == FieldKind.Text);
        // 18 pt high: a one-line field (taller ones wrap their text instead)
        var styled = text with { Name = "styled", FontSize = 14, Color = "#C0392B", Font = "sans",
                                 Rect = new PdfRect(text.Rect.X0, text.Rect.Y0, text.Rect.X1, text.Rect.Y0 + 18) };
        var plain = text with { Name = "plain", Rect = new PdfRect(text.Rect.X0, text.Rect.Y0 + 200, text.Rect.X1, text.Rect.Y1 + 200) };
        var made = Path.Combine(_dir, "styled.pdf");
        AcroFormWriter.Write(src, made, [styled.ToFormField(), plain.ToFormField()], FormScript.Japanese);

        string Da(string pdf, string name)
        {
            using var d = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
            var fields = d.AcroForm.Elements.GetArray("/Fields")!;
            return fields.Elements.Select(e => (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)e).Value)
                .Single(f => f.Elements.GetString("/T") == name).Elements.GetString("/DA");
        }
        Assert.Equal("/KozGo 14 Tf 0.753 0.224 0.169 rg", Da(made, "styled"));    // Gothic, 14 pt, the chosen red
        Assert.Matches(@"^/KozMin \d+(\.\d+)? Tf 0 g$", Da(made, "plain"));       // defaults: Mincho, sized from the height, black

        // a short value keeps the chosen size; one too long for the box is shrunk, never cut off
        string Appearance(string value)
        {
            var filled = Path.Combine(_dir, $"filled_{value.Length}.pdf");
            AcroFormFields.Fill(made, filled, new Dictionary<string, string> { ["styled"] = value });
            using var d = PdfReader.Open(filled, PdfDocumentOpenMode.Import);
            var field = d.AcroForm.Elements.GetArray("/Fields")!.Elements.Select(e => (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)e).Value)
                .Single(f => f.Elements.GetString("/T") == "styled");
            var ap = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)field.Elements.GetDictionary("/AP")!.Elements["/N"]!).Value;
            return System.Text.Encoding.ASCII.GetString(ap.Stream.UnfilteredValue);
        }
        double Size(string ap) => double.Parse(System.Text.RegularExpressions.Regex.Match(ap, @"/KozGo ([\d.]+) Tf").Groups[1].Value,
                                               System.Globalization.CultureInfo.InvariantCulture);
        var shortAp = Appearance("林 燁");
        Assert.Equal(14, Size(shortAp));
        Assert.Contains("0.753 0.224 0.169 rg", shortAp);
        int many = (int)(text.Rect.Width / 6);                              // needs about 6 pt to fit: under 14, over the 4 pt floor
        var longAp = Appearance(new string('神', many));
        Assert.InRange(Size(longAp), 4, 13.9);
        Assert.True(Size(longAp) * many <= text.Rect.Width, $"{many} full-width characters must fit the field's width");

        // a tall field wraps; when even the wrapped lines do not fit its height, they are shrunk until they do
        var tall = text with { Name = "tall", FontSize = 14 };
        var tallPdf = Path.Combine(_dir, "tall.pdf");
        AcroFormWriter.Write(src, tallPdf, [tall.ToFormField()], FormScript.Japanese);
        var tallFilled = Path.Combine(_dir, "tall_filled.pdf");
        AcroFormFields.Fill(tallPdf, tallFilled, new Dictionary<string, string> { ["tall"] = new string('神', (int)(text.Rect.Width / 14 * 4)) });
        using var td = PdfReader.Open(tallFilled, PdfDocumentOpenMode.Import);
        var tf = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)td.AcroForm.Elements.GetArray("/Fields")!.Elements[0]).Value;
        var tap = System.Text.Encoding.ASCII.GetString(((PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)tf.Elements.GetDictionary("/AP")!.Elements["/N"]!).Value).Stream.UnfilteredValue);
        double ts = double.Parse(System.Text.RegularExpressions.Regex.Match(tap, @"/KozMin ([\d.]+) Tf").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        // one "1 0 0 1 x y Tm" per glyph or per line: the lines are the distinct y values
        int lines = System.Text.RegularExpressions.Regex.Matches(tap, @"1 0 0 1 [\d.\-]+ ([\d.\-]+) Tm").Select(m => m.Groups[1].Value).Distinct().Count();
        Assert.True(lines >= 2 && ts < 14, $"{lines} lines at {ts} pt");
        Assert.True(lines * ts * 1.15 <= text.Rect.Height, "every wrapped line stays inside the field");
    }

    [Fact]
    public void Comb_checkbox_and_names()
    {
        var src = MakeForm();
        var dst = Path.Combine(_dir, "kinds.pdf");
        AcroFormWriter.Write(src, dst,
        [
            new FormField(0, FormFieldKind.Comb, new PdfRect(40, 90, 280, 120), "番号", MaxLen: 12, Align: 1),
            new FormField(0, FormFieldKind.Checkbox, new PdfRect(300, 95, 310, 105), "a.b"),
            new FormField(0, FormFieldKind.Text, new PdfRect(150, 40, 440, 70), "番号"),
        ], FormScript.Japanese);

        using var doc = PdfReader.Open(dst, PdfDocumentOpenMode.Import);
        var fields = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!.Elements
            .Select(i => (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)i).Value).ToList();

        var comb = fields[0];
        Assert.Equal("/Tx", comb.Elements.GetName("/FT"));
        Assert.Equal(12, comb.Elements.GetInteger("/MaxLen"));
        Assert.NotEqual(0, comb.Elements.GetInteger("/Ff") & (1 << 24));

        var box = fields[1];
        Assert.Equal("/Btn", box.Elements.GetName("/FT"));
        Assert.Equal("a・b", box.Elements.GetString("/T"));   // '.' would create a field hierarchy

        Assert.Equal("番号_p1", fields[2].Elements.GetString("/T"));   // names stay unique
    }
}

public class FormScriptTests
{
    [Theory]
    [InlineData("給与所得者の扶養控除等（異動）申告書", FormScript.Japanese)]
    [InlineData("근로소득자 소득·세액 공제신고서", FormScript.Korean)]
    [InlineData("个人所得税专项附加扣除信息表 请填写", FormScript.ChineseSimplified)]
    [InlineData("綜合所得稅結算申報書 請填寫個人資料", FormScript.ChineseTraditional)]
    [InlineData("Déclaration des revenus — Prénom, Nom", FormScript.Latin)]
    [InlineData("Einkommensteuererklärung für Straße und Ort", FormScript.Latin)]
    public void Detects_the_writing_system(string text, FormScript expected) =>
        Assert.Equal(expected, FormScripts.Detect(text));

    [Fact]
    public void No_text_means_no_guess() => Assert.Null(FormScripts.Detect("12345 ---"));

    [Theory]
    [InlineData("ja", FormScript.Japanese)]
    [InlineData("ja-JP", FormScript.Japanese)]
    [InlineData("zh-Hans", FormScript.ChineseSimplified)]
    [InlineData("zh-CN", FormScript.ChineseSimplified)]
    [InlineData("zh-Hant", FormScript.ChineseTraditional)]
    [InlineData("zh-TW", FormScript.ChineseTraditional)]
    [InlineData("ko-KR", FormScript.Korean)]
    [InlineData("en", FormScript.Latin)]
    [InlineData("fr-FR", FormScript.Latin)]
    [InlineData("de", FormScript.Latin)]
    public void Maps_language_tags(string tag, FormScript expected) =>
        Assert.Equal(expected, FormScripts.FromLanguage(tag));

    [Fact]
    public void Korean_text_counts_as_readable() =>
        Assert.True(MasumeDetector.Readable("근로소득자 소득·세액 공제신고서"));

    [Fact]
    public void French_and_German_accents_count_as_readable() =>
        Assert.True(MasumeDetector.Readable("Prénom à remplir — Straße, Größe"));
}
