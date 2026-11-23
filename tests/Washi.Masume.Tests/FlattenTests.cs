// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Core.Pdfium;
using Washi.Core.Signatures;

namespace Washi.Masume.Tests;

/// <summary>Flattening: fields and comments drawn into the page, looking exactly as they did, and no longer editable.</summary>
public sealed class FlattenTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-flatten-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private string Blank(int rotate = 0)
    {
        var path = P($"blank{rotate}.pdf");
        if (File.Exists(path)) return path;
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(rotate is 90 or 270 ? 400 : 600);
        page.Height = XUnit.FromPoint(rotate is 90 or 270 ? 600 : 400);
        page.Rotate = rotate;
        using (var g = XGraphics.FromPdfPage(page)) g.DrawRectangle(new XPen(XColors.Black, 1), 20, 20, 100, 30);
        doc.Save(path);
        return path;
    }

    /// <summary>A form with a name, a checkbox and a radio group, filled in, with five kinds of comment on top.</summary>
    private string Filled(int rotate = 0)
    {
        var form = P($"form{rotate}.pdf");
        AcroFormWriter.Write(Blank(rotate), form,
        [
            new FormField(0, FormFieldKind.Text, new PdfRect(40, 300, 260, 324), "氏名"),
            new FormField(0, FormFieldKind.Checkbox, new PdfRect(300, 300, 314, 314), "同意"),
            new FormField(0, FormFieldKind.Radio, new PdfRect(340, 300, 352, 312), "有無", Export: "有"),
            new FormField(0, FormFieldKind.Radio, new PdfRect(370, 300, 382, 312), "有無", Export: "無"),
        ], FormScript.Japanese);
        var filled = P($"filled{rotate}.pdf");
        AcroFormFields.Fill(form, filled, new Dictionary<string, string> { ["氏名"] = "山田 太郎", ["同意"] = "true", ["有無"] = "無" });
        var commented = P($"commented{rotate}.pdf");
        PdfComments.Add(filled, commented,
        [
            new CommentSpec(0, CommentKind.Highlight, new PdfRect(40, 40, 200, 52), Lines: [new PdfRect(40, 40, 200, 52)]),
            new CommentSpec(0, CommentKind.Note, new PdfRect(300, 40, 318, 58), "メモ"),
            new CommentSpec(0, CommentKind.TextBox, new PdfRect(300, 100, 520, 140), "確認済み checked"),
            new CommentSpec(0, CommentKind.Box, new PdfRect(40, 200, 200, 280)),
            new CommentSpec(0, CommentKind.Pen, new PdfRect(300, 200, 500, 280), Strokes: [[(300.0, 200.0), (400.0, 280.0), (500.0, 200.0)]]),
        ]);
        return commented;
    }

    private static FlattenResult Flatten(string input, string output, bool fields = true, bool comments = true) =>
        Locked(() => PdfFlattener.Flatten(input, output, fields, comments));

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private static string Text(string pdf) => Locked(() =>
    {
        using var d = PdfiumDocument.Open(pdf);
        return Regex.Replace(d.GetPageText(0), @"\s+", "");
    });

    private static RgbImage Render(string pdf) => Locked(() =>
    {
        using var d = PdfiumDocument.Open(pdf);
        return d.RenderPage(0, 1.5);
    });

    /// <summary>
    /// Pixels that differ, outside the name field (PDFium's form layer smooths a field's text its own way; the page
    /// renderer, which draws it after flattening, another).
    /// </summary>
    private static int Differing(RgbImage a, RgbImage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        int n = 0;
        for (int i = 0, q = 0; q < a.Pixels.Length; i++, q += 3)
        {
            double x = i % a.Width / 1.5, y = i / a.Width / 1.5;
            if (x is >= 38 and <= 262 && y is >= 298 and <= 326) continue;
            if (Math.Abs(a.Pixels[q] - b.Pixels[q]) + Math.Abs(a.Pixels[q + 1] - b.Pixels[q + 1]) + Math.Abs(a.Pixels[q + 2] - b.Pixels[q + 2]) > 48) n++;
        }
        return n;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void A_filled_form_and_its_comments_become_part_of_the_page_and_look_the_same(int rotate)
    {
        var input = Filled(rotate);
        var output = P($"flat{rotate}.pdf");
        var r = Flatten(input, output);

        Assert.Equal(4, r.Fields);      // the name, the checkbox and both radio buttons
        Assert.Equal(5, r.Comments);
        Assert.Equal(1, r.Pages);
        Assert.Empty(AcroFormFields.List(output));
        Assert.Empty(PdfComments.List(output));
        using (var d = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            Assert.False(d.Internals.Catalog.Elements.ContainsKey("/AcroForm"));
            Assert.False(d.Pages[0].Elements.ContainsKey("/Annots"));
        }
        Assert.Contains("山田太郎", Text(output));        // the value is now text of the page
        Assert.Contains("確認済みchecked", Text(output));
        Assert.True(Differing(Render(input), Render(output)) < 60, "the page looks as it did");
    }

    [Fact]
    public void Only_the_comments_or_only_the_fields_can_be_flattened()
    {
        var input = Filled();
        var r = Flatten(input, P("comments-only.pdf"), fields: false, comments: true);
        Assert.Equal((0, 5), (r.Fields, r.Comments));
        Assert.Equal(3, AcroFormFields.List(P("comments-only.pdf")).Count);   // still a form to fill
        Assert.Empty(PdfComments.List(P("comments-only.pdf")));

        r = Flatten(input, P("fields-only.pdf"), fields: true, comments: false);
        Assert.Equal((4, 0), (r.Fields, r.Comments));
        Assert.Empty(AcroFormFields.List(P("fields-only.pdf")));
        Assert.Equal(5, PdfComments.List(P("fields-only.pdf")).Count);       // still comments to answer
        Assert.True(Differing(Render(input), Render(P("fields-only.pdf"))) < 60);
    }

    [Fact]
    public void A_value_the_viewer_was_left_to_draw_is_drawn_by_Washi_and_reads_back()
    {
        // the field has a value but no appearance, and the form asks viewers to draw it (NeedAppearances)
        var form = P("form.pdf");
        AcroFormWriter.Write(Blank(), form, [new FormField(0, FormFieldKind.Text, new PdfRect(40, 300, 260, 324), "氏名")], FormScript.Japanese);
        using (var d = PdfReader.Open(form, PdfDocumentOpenMode.Modify))
        {
            var w = (PdfDictionary)((PdfReference)d.Pages[0].Elements.GetArray("/Annots")!.Elements[0]).Value;
            w.Elements["/V"] = new PdfString("佐藤 花子", PdfStringEncoding.Unicode);
            w.Elements.Remove("/AP");
            d.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.SetBoolean("/NeedAppearances", true);
            d.Save(P("bare.pdf"));
        }
        var r = Flatten(P("bare.pdf"), P("flat.pdf"));
        Assert.Equal(1, r.Fields);
        Assert.Contains("佐藤花子", Text(P("flat.pdf")));
    }

    [Fact]
    public void Signed_files_files_with_nothing_to_flatten_and_existing_outputs_are_refused()
    {
        var id = DigitalIds.Create("山田 太郎", "taro@example.com", P("id.p12"), "secret-1");
        PdfDigitalSigner.Sign(Blank(), P("signed.pdf"), new SignRequest(id, Page: 0, Rect: new PdfRect(300, 300, 500, 350)));
        Assert.Equal("Err_FlattenSigned", Assert.Throws<PageAssemblyException>(() => Flatten(P("signed.pdf"), P("a.pdf"))).Key);
        Assert.Equal("Err_FlattenNone", Assert.Throws<PageAssemblyException>(() => Flatten(Blank(), P("b.pdf"))).Key);
        Assert.Equal("Err_FlattenNothing", Assert.Throws<PageAssemblyException>(() => Flatten(Blank(), P("c.pdf"), false, false)).Key);
        Assert.Equal("Err_Exists", Assert.Throws<PageAssemblyException>(() => Flatten(Filled(), Blank())).Key);
        Assert.False(File.Exists(P("a.pdf")) || File.Exists(P("b.pdf")));
    }
}
