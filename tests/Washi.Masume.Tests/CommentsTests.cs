// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Review comments: each kind is written as a standard annotation that renders where it was put.</summary>
public sealed class CommentsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-comments-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Blank(int rotate)
    {
        var path = Path.Combine(_dir, $"blank{rotate}.pdf");
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(rotate is 90 or 270 ? 400 : 600);
        page.Height = XUnit.FromPoint(rotate is 90 or 270 ? 600 : 400);
        page.Rotate = rotate;
        doc.Save(path);
        return path;
    }

    /// <summary>Pixels in the upright rect whose colour differs from white by more than a little.</summary>
    private static int Marked(RgbImage img, PdfRect r, double scale)
    {
        int n = 0;
        for (int y = Math.Max(0, (int)(r.Y0 * scale)); y < Math.Min(img.Height, (int)(r.Y1 * scale)); y++)
            for (int x = Math.Max(0, (int)(r.X0 * scale)); x < Math.Min(img.Width, (int)(r.X1 * scale)); x++)
            {
                int i = (y * img.Width + x) * 3;
                if (765 - img.Pixels[i] - img.Pixels[i + 1] - img.Pixels[i + 2] > 60) n++;
            }
        return n;
    }

    private static RgbImage Render(string path)
    {
        PdfiumDocument.Gate.Wait();
        try { using var d = PdfiumDocument.Open(path); return d.RenderPage(0, 2); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Every_kind_lands_where_it_was_put_on_the_upright_page(int rotate)
    {
        var input = Blank(rotate);
        var output = Path.Combine(_dir, $"c{rotate}.pdf");
        var spots = new Dictionary<CommentKind, PdfRect>
        {
            [CommentKind.Highlight] = new(40, 40, 200, 52),
            [CommentKind.Underline] = new(40, 80, 200, 92),
            [CommentKind.StrikeOut] = new(40, 120, 200, 132),
            [CommentKind.Note] = new(300, 40, 318, 58),
            [CommentKind.TextBox] = new(300, 100, 520, 140),
            [CommentKind.Box] = new(40, 200, 200, 280),
            [CommentKind.Pen] = new(300, 200, 500, 280),
        };
        var specs = spots.Select(kv => new CommentSpec(0, kv.Key, kv.Value,
            Text: kv.Key == CommentKind.TextBox ? "確認お願いします check" : kv.Key == CommentKind.Note ? "メモ" : "",
            Lines: kv.Key is CommentKind.Highlight or CommentKind.Underline or CommentKind.StrikeOut ? [kv.Value] : null,
            Strokes: kv.Key == CommentKind.Pen ? [[(300.0, 200.0), (400.0, 280.0), (500.0, 200.0)]] : null)).ToList();

        Assert.Equal(7, PdfComments.Add(input, output, specs));

        var img = Render(output);
        foreach (var (kind, r) in spots)
            Assert.True(Marked(img, r, 2) > 20, $"{kind} drawn in its place");
        Assert.Equal(0, Marked(img, new PdfRect(220, 300, 280, 380), 2));   // nowhere else

        var listed = PdfComments.List(output);
        Assert.Equal(["Highlight", "Underline", "StrikeOut", "Text", "FreeText", "Square", "Ink"], listed.Select(c => c.Type));
        var note = listed.Single(c => c.Type == "Text");
        Assert.Equal("メモ", note.Text);
        Assert.InRange(note.Rect.X0, 299, 301);
        Assert.InRange(note.Rect.Y0, 39, 41);
    }

    [Fact]
    public void Comments_keep_the_form_fields_and_skip_them_when_listed()
    {
        var form = Path.Combine(_dir, "form.pdf");
        AcroFormWriter.Write(Blank(0), form, [new FormField(0, FormFieldKind.Text, new PdfRect(40, 300, 200, 320), "氏名")], FormScript.Japanese);
        var output = Path.Combine(_dir, "commented.pdf");
        PdfComments.Add(form, output, [new CommentSpec(0, CommentKind.Note, new PdfRect(10, 10, 28, 28), "ok", Author: "reviewer")]);
        Assert.Equal("氏名", Assert.Single(AcroFormFields.List(output)).Name);
        var c = Assert.Single(PdfComments.List(output));
        Assert.Equal("reviewer", c.Author);
        Assert.Equal("Err_NoComment", Assert.Throws<PageAssemblyException>(() => PdfComments.Add(form, output, [])).Key);
    }
}
