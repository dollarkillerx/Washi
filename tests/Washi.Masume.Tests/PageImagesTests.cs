// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using SkiaSharp;
using Washi.Core;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Pages saved as images, and pages cropped (by margins, or trimmed to their content).</summary>
public sealed class PageImagesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-pageimg-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>400 × 300 pt pages (Helvetica as /F1), each with its content and /Rotate.</summary>
    private string Doc(string name, params (string Content, int Rotate)[] pages)
    {
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii($"<< /Type /Pages /Kids [{string.Join(" ", pages.Select((_, i) => $"{4 + i * 2} 0 R"))}] /Count {pages.Length} >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        };
        foreach (var (content, rotate) in pages)
        {
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Rotate {rotate} /Contents {objects.Count + 2} 0 R /Resources << /Font << /F1 3 0 R >> >> >>"));
            var data = Ascii(content);
            objects.Add([.. Ascii($"<< /Length {data.Length} >>\nstream\n"), .. data, .. Ascii("\nendstream")]);
        }
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
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(Ascii(sb.ToString()));
        File.WriteAllBytes(P(name), ms.ToArray());
        return P(name);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>A dark block from (100,100) to (200,150) on the page as shown (PDF y up: 150..200), and a word.</summary>
    private const string Block = "0.1 0.3 0.7 rg 100 150 100 50 re f BT /F1 10 Tf 110 170 Td (Inside) Tj ET";

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private static (int W, int H) Rendered(string pdf, int page) => Locked(() =>
    {
        using var d = PdfiumDocument.Open(pdf);
        var img = d.RenderPage(page, 1.0);
        return (img.Width, img.Height);
    });

    [Fact]
    public void Pages_become_PNG_or_JPEG_files_at_the_resolution_asked()
    {
        var input = Doc("doc.pdf", (Block, 0), (Block, 90), (Block, 0));
        var files = Locked(() => PdfPageImages.Export(input, P("out"), new ExportOptions("png", 72)));
        Assert.Equal(["doc_p1.png", "doc_p2.png", "doc_p3.png"], files.Select(Path.GetFileName));
        using (var first = SKBitmap.Decode(files[0])) Assert.Equal((400, 300), (first.Width, first.Height));
        using (var turned = SKBitmap.Decode(files[1])) Assert.Equal((300, 400), (turned.Width, turned.Height));   // as shown
        using (var px = SKBitmap.Decode(files[0])) Assert.Equal(new SKColor(26, 77, 179), px.GetPixel(150, 110));   // the block

        var jpeg = Assert.Single(Locked(() => PdfPageImages.Export(input, P("out"), new ExportOptions("jpeg", 144, "2"))));
        Assert.Equal("doc_p2.jpg", Path.GetFileName(jpeg));
        using (var big = SKBitmap.Decode(jpeg)) Assert.Equal((600, 800), (big.Width, big.Height));

        // nothing is overwritten: no file at all is written when one is there
        File.Delete(files[2]);
        Assert.Equal("Err_Exists", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfPageImages.Export(input, P("out")))).Key);
        Assert.False(File.Exists(files[2]));
        Assert.Equal("Err_ExportFormat", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfPageImages.Export(input, P("x"), new ExportOptions("tiff")))).Key);
        Assert.Equal("Err_ExportDpi", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfPageImages.Export(input, P("x"), new ExportOptions(Dpi: 2000)))).Key);
    }

    [Fact]
    public void Margins_are_cut_off_the_page_as_shown()
    {
        var input = Doc("doc.pdf", (Block, 0), (Block, 90));
        int n = Locked(() => PdfPageImages.Crop(input, P("crop.pdf"), new CropOptions(Top: 20, Right: 30, Bottom: 40, Left: 50)));
        Assert.Equal(2, n);
        Assert.Equal((320, 240), Rendered(P("crop.pdf"), 0));
        Assert.Equal((220, 340), Rendered(P("crop.pdf"), 1));                  // the turned page: 300 × 400 as shown
        using var d = Locked(() => PdfiumDocument.Open(P("crop.pdf")));
        Assert.Contains("Inside", Locked(() => d.GetPageText(0)));
    }

    [Fact]
    public void Trimming_keeps_the_content_and_a_little_paper()
    {
        var input = Doc("doc.pdf", (Block, 0), ("", 0), (Block, 0));
        int n = Locked(() => PdfPageImages.Crop(input, P("trim.pdf"), new CropOptions(Trim: true, Padding: 10)));
        Assert.Equal(2, n);                                                     // the blank page stays as it is
        var (w, h) = Rendered(P("trim.pdf"), 0);
        Assert.InRange(w, 118, 122);                                            // 100 wide + 2 × 10
        Assert.InRange(h, 68, 72);
        Assert.Equal((400, 300), Rendered(P("trim.pdf"), 1));
    }

    [Fact]
    public void Nothing_to_crop_too_small_and_blank_pages_are_refused()
    {
        var input = Doc("doc.pdf", (Block, 0));
        Assert.Equal("Err_CropNothing", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfPageImages.Crop(input, P("a.pdf"), new CropOptions()))).Key);
        Assert.Equal("Err_CropTooSmall", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfPageImages.Crop(input, P("b.pdf"), new CropOptions(Left: 200, Right: 180)))).Key);
        Assert.Equal("Err_CropNegative", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfPageImages.Crop(input, P("c.pdf"), new CropOptions(Top: -5)))).Key);
        var blank = Doc("blank.pdf", ("", 0));
        Assert.Equal("Err_CropBlank", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfPageImages.Crop(blank, P("d.pdf"), new CropOptions(Trim: true)))).Key);
        Assert.False(File.Exists(P("a.pdf")) || File.Exists(P("b.pdf")) || File.Exists(P("d.pdf")));
    }
}
