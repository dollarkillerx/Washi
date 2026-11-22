// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Core.Pdfium;
using Washi.Masume;

namespace Washi.Masume.Tests;

/// <summary>Radio groups, drop-down lists and signature fields; signing a page.</summary>
public sealed class FieldTypesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-kinds-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Blank(string name, int rotate = 0)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(rotate is 90 or 270 ? 400 : 600);
        page.Height = XUnit.FromPoint(rotate is 90 or 270 ? 600 : 400);
        page.Rotate = rotate;
        using (var g = XGraphics.FromPdfPage(page)) g.DrawRectangle(new XPen(XColors.Black, 1), 20, 20, 100, 30);
        doc.Save(path);
        return path;
    }

    private string Write(IReadOnlyList<FormField> fields)
    {
        var output = Path.Combine(_dir, "form.pdf");
        AcroFormWriter.Write(Blank("blank.pdf"), output, fields, FormScript.Japanese);
        return output;
    }

    // ------------------------------------------------------------------ writing and filling
    [Fact]
    public void A_radio_group_with_japanese_options_is_one_field_whose_value_is_the_label()
    {
        var form = Write([
            new FormField(0, FormFieldKind.Radio, new PdfRect(20, 20, 30, 30), "有無", Export: "有"),
            new FormField(0, FormFieldKind.Radio, new PdfRect(60, 20, 70, 30), "有無", Export: "無"),
        ]);

        var f = Assert.Single(AcroFormFields.List(form));
        Assert.Equal("radio", f.Type);
        Assert.Equal("有無", f.Name);
        Assert.Equal(["有", "無"], f.Options);   // states 0 and 1, shown through /Opt

        var filled = Path.Combine(_dir, "filled.pdf");
        var r = AcroFormFields.Fill(form, filled, new Dictionary<string, string> { ["有無"] = "無" });
        Assert.Equal(1, r.Filled);
        Assert.Equal("無", Assert.Single(AcroFormFields.List(filled)).Value);
        using var doc = PdfReader.Open(filled, PdfDocumentOpenMode.Import);
        var states = doc.Pages[0].Annotations.Cast<PdfSharp.Pdf.Annotations.PdfAnnotation>().Select(a => a.Elements.GetName("/AS")).ToList();
        Assert.Equal(["/Off", "/1"], states);   // only the chosen button is on
    }

    [Fact]
    public void Plain_ascii_options_are_the_states_themselves()
    {
        var form = Write([
            new FormField(0, FormFieldKind.Radio, new PdfRect(20, 20, 30, 30), "answer", Export: "Yes"),
            new FormField(0, FormFieldKind.Radio, new PdfRect(60, 20, 70, 30), "answer", Export: "No"),
        ]);
        var filled = Path.Combine(_dir, "filled.pdf");
        AcroFormFields.Fill(form, filled, new Dictionary<string, string> { ["answer"] = "Yes" });
        var f = Assert.Single(AcroFormFields.List(filled));
        Assert.Equal(["Yes", "No"], f.Options);
        Assert.Equal("Yes", f.Value);
        using var doc = PdfReader.Open(filled, PdfDocumentOpenMode.Import);
        Assert.Null(doc.AcroForm!.Fields[0].Elements["/Opt"]);
    }

    [Fact]
    public void A_drop_down_lists_its_items_and_draws_the_chosen_one()
    {
        var form = Write([new FormField(0, FormFieldKind.Choice, new PdfRect(20, 20, 120, 40), "続柄", Options: ["夫", "妻", "子", " "])]);
        var f = Assert.Single(AcroFormFields.List(form));
        Assert.Equal("choice", f.Type);
        Assert.Equal(["夫", "妻", "子"], f.Options);   // blank items are dropped

        var filled = Path.Combine(_dir, "filled.pdf");
        AcroFormFields.Fill(form, filled, new Dictionary<string, string> { ["続柄"] = "妻" });
        Assert.Equal("妻", Assert.Single(AcroFormFields.List(filled)).Value);
        PdfiumDocument.Gate.Wait();
        try
        {
            using var pdf = PdfiumDocument.Open(filled);
            var img = pdf.RenderPage(0, 2);
            Assert.True(Dark(img, new PdfRect(20, 20, 120, 40), 2) > 20);   // the item is drawn, not left to the viewer
        }
        finally { PdfiumDocument.Gate.Release(); }
    }

    [Fact]
    public void A_signature_field_is_an_empty_signature_field()
    {
        var form = Write([new FormField(0, FormFieldKind.Signature, new PdfRect(20, 20, 200, 50), "署名")]);
        var f = Assert.Single(AcroFormFields.List(form));
        Assert.Equal("signature", f.Type);
        Assert.Equal(new PdfRect(20, 20, 200, 50), f.Rect);
    }

    // ------------------------------------------------------------------ detection
    private static PdfChar Ch(string c, double x, double y, double size = 8) => new(c, new PdfRect(x, y, x + size, y + size), 0);

    private static PdfChar[] Line(string text, double x, double y, double size = 8) =>
        [.. text.Select((c, i) => Ch(c.ToString(), x + i * size, y, size))];

    [Fact]
    public void Ring_glyphs_with_words_after_them_are_one_radio_group()
    {
        var groups = MasumeDetector.RadioGroups(Line("○有　○無", 20, 20));
        var g = Assert.Single(groups);
        Assert.Equal(["有", "無"], g.Select(r => r.Export));
    }

    [Theory]
    [InlineData("○○県○○市")]     // a place-name placeholder
    [InlineData("令和○年○月○日")]  // a date placeholder
    [InlineData("○有")]            // one ring is not a choice
    public void Placeholders_and_lone_rings_are_not_radio_buttons(string text) =>
        Assert.Empty(MasumeDetector.RadioGroups(Line(text, 20, 20)));

    [Theory]
    [InlineData("署名", true)]
    [InlineData("Signature of beneficial owner", true)]
    [InlineData("본인 서명", true)]
    [InlineData("氏名", false)]
    [InlineData("署名欄の記載について", false)]
    public void Signature_labels(string label, bool expected) => Assert.Equal(expected, MasumeDetector.IsSignatureLabel(label));

    // ------------------------------------------------------------------ signing
    private static int Dark(RgbImage img, PdfRect r, double scale)
    {
        int n = 0;
        for (int y = (int)(r.Y0 * scale); y < (int)(r.Y1 * scale); y++)
            for (int x = (int)(r.X0 * scale); x < (int)(r.X1 * scale); x++)
            {
                int i = (y * img.Width + x) * 3;
                if (img.Pixels[i] + img.Pixels[i + 1] + img.Pixels[i + 2] < 300) n++;
            }
        return n;
    }

    private static readonly Ink Zigzag = Ink.From([[(0.0, 10.0), (10.0, 0.0), (20.0, 10.0), (30.0, 0.0), (40.0, 10.0)]]);

    [Fact]
    public void Ink_survives_a_round_trip_through_its_text_form()
    {
        var back = Ink.Parse(Zigzag.Serialize())!;
        Assert.Equal(4, back.Aspect, 3);
        Assert.Equal(Zigzag.Strokes[0].Count, back.Strokes[0].Count);
        Assert.Null(Ink.Parse("not a signature"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void A_signature_lands_inside_its_box_on_the_upright_page(int rotate)
    {
        var input = Blank($"r{rotate}.pdf", rotate);
        var output = Path.Combine(_dir, $"signed{rotate}.pdf");
        var box = new PdfRect(200, 150, 360, 200);

        Assert.Equal(1, PdfSigner.Sign(input, output, [new SignatureStamp(0, box, Zigzag)]));

        PdfiumDocument.Gate.Wait();
        try
        {
            using var before = PdfiumDocument.Open(input);
            using var after = PdfiumDocument.Open(output);
            var a = before.RenderPage(0, 2);
            var b = after.RenderPage(0, 2);
            Assert.True(Dark(b, box, 2) - Dark(a, box, 2) > 100, "ink inside the box");
            // nothing else on the page changed: the rest of the page is as dark as before
            Assert.Equal(Dark(a, new PdfRect(0, 0, 600, 140), 2), Dark(b, new PdfRect(0, 0, 600, 140), 2));
        }
        finally { PdfiumDocument.Gate.Release(); }
    }

    [Fact]
    public void Signing_keeps_the_form_fields()
    {
        var form = Write([new FormField(0, FormFieldKind.Text, new PdfRect(20, 20, 200, 40), "氏名"),
                          new FormField(0, FormFieldKind.Signature, new PdfRect(20, 60, 200, 90), "署名")]);
        var output = Path.Combine(_dir, "signed.pdf");
        PdfSigner.Sign(form, output, [new SignatureStamp(0, new PdfRect(20, 60, 200, 90), Zigzag)]);
        Assert.Equal(["氏名", "署名"], AcroFormFields.List(output).Select(f => f.Name));
        Assert.Equal("Err_NoSignature", Assert.Throws<PageAssemblyException>(() => PdfSigner.Sign(form, output, [])).Key);
    }
}
