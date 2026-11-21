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
