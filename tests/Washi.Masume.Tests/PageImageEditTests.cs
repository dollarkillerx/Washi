// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using SkiaSharp;
using Washi.Core;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Images on the page: listed, added in a box, replaced in place (keeping their shape), removed.</summary>
public sealed class PageImageEditTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-pageimgedit-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>A 400 × 300 page with a 2 × 2 green image drawn at 50,200 (100 × 50 pt) and a line of text; /Rotate as given.</summary>
    private string Doc(string name, int rotate = 0)
    {
        var px = new byte[] { 0, 160, 0, 0, 160, 0, 0, 160, 0, 0, 160, 0 };
        var content = "BT /F1 12 Tf 30 100 Td (Caption under the logo) Tj ET\nq 100 0 0 50 50 200 cm /Im0 Do Q";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Rotate {rotate} /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {px.Length} >>\nstream\n{Encoding.Latin1.GetString(px)}\nendstream",
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

    /// <summary>A photo with a red top-left quarter on blue.</summary>
    private string Photo(string name, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(30, 60, 200));
            using var red = new SKPaint { Color = new SKColor(220, 30, 30) };
            c.DrawRect(0, 0, w / 2f, h / 2f, red);
        }
        var format = name.EndsWith(".png") ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        using var data = SKImage.FromBitmap(bmp).Encode(format, 92);
        File.WriteAllBytes(P(name), data.ToArray());
        return P(name);
    }

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private static string Text(string pdf) => Locked(() =>
    {
        using var d = PdfiumDocument.Open(pdf);
        return d.GetPageText(0);
    });

    [Fact]
    public void The_pages_images_are_listed_where_they_show()
    {
        var img = Assert.Single(PdfPageImageEdit.List(Doc("d.pdf")));
        Assert.Equal(("1-1", 0, 2, 2), (img.Id, img.Page, img.PixelWidth, img.PixelHeight));
        Assert.Equal(new PdfRect(50, 50, 150, 100), img.Rect);
    }

    [Fact]
    public void A_replaced_image_keeps_its_shape_in_the_old_place()
    {
        var input = Doc("d.pdf");
        var r = Locked(() => PdfPageImageEdit.Apply(input, P("out.pdf"), [], replace: new Dictionary<string, string> { ["1-1"] = Photo("logo.jpg", 160, 100) }));
        Assert.Equal(new ImageEditResult(0, 1, 0), r);
        var img = Assert.Single(PdfPageImageEdit.List(P("out.pdf")));
        Assert.Equal((160, 100), (img.PixelWidth, img.PixelHeight));
        Assert.Equal(60, img.Rect.X0, 1);                                  // 80 × 50 (1.6 : 1) centred in 100 × 50
        Assert.Equal(140, img.Rect.X1, 1);
        Assert.Equal(50, img.Rect.Y0, 1);
        Assert.Contains("Caption under the logo", Text(P("out.pdf")));
    }

    [Fact]
    public void A_removed_image_is_gone_and_the_rest_stays()
    {
        var input = Doc("d.pdf");
        Locked(() => PdfPageImageEdit.Apply(input, P("out.pdf"), [], remove: ["1-1"]));
        Assert.Empty(PdfPageImageEdit.List(P("out.pdf")));
        Assert.Contains("Caption under the logo", Text(P("out.pdf")));
    }

    [Fact]
    public void An_added_image_fits_its_box_on_a_turned_page_too()
    {
        foreach (var rotate in new[] { 0, 90 })
        {
            var input = Doc($"d{rotate}.pdf", rotate);
            var box = new PdfRect(150, 150, 250, 250);                     // upright points, square
            Locked(() => PdfPageImageEdit.Apply(input, P($"out{rotate}.pdf"), [new AddImage(0, box, Photo($"p{rotate}.png", 200, 100))]));
            var added = PdfPageImageEdit.List(P($"out{rotate}.pdf")).Single(i => i.PixelWidth == 200);
            Assert.Equal(150, added.Rect.X0, 1);                           // 100 × 50, centred
            Assert.Equal(175, added.Rect.Y0, 1);
            Assert.Equal(250, added.Rect.X1, 1);
            Assert.Equal(225, added.Rect.Y1, 1);
        }
    }

    [Fact]
    public void Unknown_images_missing_files_and_no_edits_are_refused()
    {
        var input = Doc("d.pdf");
        Assert.Equal("Err_ImageEditNothing", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfPageImageEdit.Apply(input, P("a.pdf"), []))).Key);
        Assert.Equal("Err_ImageId", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfPageImageEdit.Apply(input, P("b.pdf"), [], remove: ["1-5"]))).Key);
        Assert.Equal("Err_NotFound", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfPageImageEdit.Apply(input, P("c.pdf"), [new AddImage(0, new PdfRect(0, 0, 50, 50), P("nope.png"))]))).Key);
        Assert.False(File.Exists(P("a.pdf")) || File.Exists(P("b.pdf")) || File.Exists(P("c.pdf")));
    }

    /// <summary>
    /// Two pages drawing one form (a 2 × 2 blue image inside it, under the form's matrix), and page 1 also draws a 2 × 2
    /// inline red image.
    /// </summary>
    private string Forms(string name)
    {
        var blue = Encoding.Latin1.GetString([0, 0, 200, 0, 0, 200, 0, 0, 200, 0, 0, 200]);
        var red = Encoding.Latin1.GetString([200, 0, 0, 200, 0, 0, 200, 0, 0, 200, 0, 0]);
        var page1 = $"q 1 0 0 1 20 20 cm /Fm0 Do Q\nq 60 0 0 40 250 200 cm BI /W 2 /H 2 /CS /RGB /BPC 8 ID {red} EI Q\nBT /F1 12 Tf 30 100 Td (Text stays) Tj ET";
        var page2 = "q 1 0 0 1 20 20 cm /Fm0 Do Q";
        var form = "q 100 0 0 60 0 0 cm /Im0 Do Q";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Contents 5 0 R /Resources << /Font << /F1 9 0 R >> /XObject << /Fm0 7 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Contents 6 0 R /Resources << /XObject << /Fm0 7 0 R >> >> >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(page1)} >>\nstream\n{page1}\nendstream",
            $"<< /Length {page2.Length} >>\nstream\n{page2}\nendstream",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Matrix [1 0 0 1 30 40] /Resources << /XObject << /Im0 8 0 R >> >> /Length {form.Length} >>\nstream\n{form}\nendstream",
            $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 12 >>\nstream\n{blue}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
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

    [Fact]
    public void Images_inside_forms_and_inline_images_are_listed_and_edited()
    {
        var input = Forms("forms.pdf");
        var images = PdfPageImageEdit.List(input);
        Assert.Equal(["1-1", "1-2", "2-1"], images.Select(i => i.Id));
        Assert.Equal(new PdfRect(50, 180, 150, 240), images[0].Rect);           // the form's matrix and the page's cm: 20+30, 20+40
        Assert.Equal((2, 2), (images[1].PixelWidth, images[1].PixelHeight));    // the inline one
        Assert.Equal(new PdfRect(250, 60, 310, 100), images[1].Rect);

        // the form's image replaced on page 1 only; the inline image removed
        var r = Locked(() => PdfPageImageEdit.Apply(input, P("out.pdf"), [],
            replace: new Dictionary<string, string> { ["1-1"] = Photo("logo.png", 100, 60) }, remove: ["1-2"]));
        Assert.Equal(new ImageEditResult(0, 1, 1), r);
        var after = PdfPageImageEdit.List(P("out.pdf"));
        Assert.Equal(["1-1", "2-1"], after.Select(i => i.Id));
        Assert.Equal((100, 60), (after[0].PixelWidth, after[0].PixelHeight));
        Assert.Equal((2, 2), (after[1].PixelWidth, after[1].PixelHeight));       // page 2 still draws the old form
        Assert.Contains("Text stays", Text(P("out.pdf")));
    }
}
