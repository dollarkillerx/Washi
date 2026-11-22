// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Compression: smaller files that look the same, with forms and text kept.</summary>
public sealed class CompressTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-compress-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    /// <summary>A photo-like picture (smooth colours and grain) as a JPEG of quality 95.</summary>
    private static byte[] Photo(int w, int h)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var rnd = new Random(7);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int n = rnd.Next(-12, 12);
                bmp.SetPixel(x, y, new SKColor((byte)Math.Clamp(x * 255 / w + n, 0, 255), (byte)Math.Clamp(y * 255 / h + n, 0, 255),
                                               (byte)Math.Clamp(128 + 60 * Math.Sin(x / 40.0) + n, 0, 255)));
            }
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();
    }

    /// <summary>
    /// Three A4 pages, each with its own copy of the same 1800 × 2550 photo shown at a quarter page (about 436 dpi), a line-art
    /// image (two colours, 1200 dpi) in a corner, uncompressed page content, and a thumbnail on page 1.
    /// </summary>
    private string Heavy()
    {
        var doc = new PdfDocument();
        var photo = Photo(1800, 2550);
        var lineArt = new byte[600 * 600];
        for (int i = 0; i < lineArt.Length; i++) lineArt[i] = (byte)((i / 600 / 20 + i % 600 / 20) % 2 == 0 ? 0 : 255);
        for (int p = 0; p < 3; p++)
        {
            var page = doc.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(595);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
            PdfDictionary Image(byte[] data, int w, int h, string cs, string? filter)
            {
                var im = new PdfDictionary(doc);
                im.Elements.SetName("/Type", "/XObject");
                im.Elements.SetName("/Subtype", "/Image");
                im.Elements.SetInteger("/Width", w);
                im.Elements.SetInteger("/Height", h);
                im.Elements.SetName("/ColorSpace", cs);
                im.Elements.SetInteger("/BitsPerComponent", 8);
                if (filter is not null) im.Elements.SetName("/Filter", filter);
                im.CreateStream(filter == "/FlateDecode" ? new PdfSharp.Pdf.Filters.FlateDecode().Encode(data, PdfFlateEncodeMode.Default) : data);
                doc.Internals.AddObject(im);
                return im;
            }
            var a = Image(photo, 1800, 2550, "/DeviceRGB", "/DCTDecode");
            var b = Image(lineArt, 600, 600, "/DeviceGray", "/FlateDecode");
            var xo = new PdfDictionary(doc);
            xo.Elements.SetReference("/Im0", a);
            xo.Elements.SetReference("/Im1", b);
            var res = new PdfDictionary(doc);
            res.Elements["/XObject"] = xo;
            page.Elements["/Resources"] = res;
            var content = new PdfDictionary(doc);
            content.CreateStream(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q 297 0 0 421 0 421 cm /Im0 Do Q q 36 0 0 36 20 20 cm /Im1 Do Q\n", 40))));
            doc.Internals.AddObject(content);
            page.Elements["/Contents"] = content.Reference;
            if (p == 0) page.Elements["/Thumb"] = Image(new byte[106 * 150 * 3], 106, 150, "/DeviceRGB", null).Reference;
        }
        doc.Options.CompressContentStreams = false;
        doc.Save(P("heavy.pdf"));
        return P("heavy.pdf");
    }

    [Fact]
    public void Photos_are_scaled_to_the_level_line_art_stays_sharp_and_the_pages_look_the_same()
    {
        var input = Heavy();
        var r = Locked(() => PdfCompressor.Compress(input, P("out.pdf")));
        Assert.True(r.Saved > 0.6, $"saved {r.Saved:P0}");
        Assert.Equal(1, r.Thumbnails);
        Assert.True(r.Duplicates >= 4, $"duplicates {r.Duplicates}");   // the copies of the photo and of the line art, kept once
        Assert.InRange(r.Difference, 0, 5);

        using var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import);
        var images = d.Internals.GetAllObjects().OfType<PdfDictionary>().Where(o => o.Elements.GetName("/Subtype") == "/Image").ToList();
        var photo = images.Single(i => i.Elements.GetName("/Filter") == "/DCTDecode");
        Assert.InRange(photo.Elements.GetInteger("/Width"), 600, 640);     // 4.1 in at 150 dpi
        var line = images.Single(i => i.Elements.GetName("/Filter") == "/FlateDecode");
        Assert.InRange(line.Elements.GetInteger("/Width"), 70, 80);         // 0.5 in at 150 dpi, still lossless
        Assert.Equal(2, images.Count);                                      // three pages share them
        Assert.False(d.Pages[0].Elements.ContainsKey("/Thumb"));
        Assert.Matches(@"^%PDF-(1\.[5-7]|2\.0)", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(P("out.pdf")), 0, 8));   // object streams need 1.5
        Assert.Contains("/ObjStm", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(P("out.pdf"))));
    }

    [Fact]
    public void Light_keeps_images_that_are_not_far_too_detailed_and_forms_survive()
    {
        ToolsTests.MakeFlatForm(P("flat.pdf"));
        AcroFormWriter.Write(P("flat.pdf"), P("form.pdf"), Washi.Tools.FormPipeline.Detect(P("flat.pdf")).Fields.Select(f => f.ToFormField()).ToList(), FormScript.Japanese);
        AcroFormFields.Fill(P("form.pdf"), P("filled.pdf"), AcroFormFields.List(P("form.pdf")).Where(f => f.Type == "text").Take(1).ToDictionary(f => f.Name, _ => "山田"));
        var before = AcroFormFields.List(P("filled.pdf"));
        var r = Locked(() => PdfCompressor.Compress(P("filled.pdf"), P("small.pdf"), CompressLevel.Light));
        Assert.Equal(0, r.Images);
        var after = AcroFormFields.List(P("small.pdf"));
        Assert.Equal(before.Select(f => (f.Name, f.Value)), after.Select(f => (f.Name, f.Value)));
        Assert.Equal(0, r.Difference);
    }

    [Fact]
    public void Protected_files_and_existing_outputs_are_refused()
    {
        var input = Heavy();
        PdfSecurity.Protect(input, P("locked.pdf"), null, "boss", new PdfPermissions(Edit: false));
        Assert.Equal("Err_CompressProtected", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfCompressor.Compress(P("locked.pdf"), P("x.pdf")))).Key);
        File.WriteAllText(P("taken.pdf"), "x");
        Assert.Equal("Err_Exists", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfCompressor.Compress(input, P("taken.pdf")))).Key);
    }

    [Fact]
    public async Task Agents_compress_with_a_level()
    {
        var input = Heavy();
        var tools = Washi.Tools.WashiTools.CreateRegistry(new Washi.Tools.PathPolicy([_dir]));
        var r = await tools.Find("compress_pdf")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = input, level = "strong" }));
        Assert.False(r.IsError, r.Text);
        var json = System.Text.Json.JsonDocument.Parse(r.Text).RootElement;
        Assert.True(json.GetProperty("saved_percent").GetDouble() > 70);
        Assert.True(File.Exists(P("heavy_compressed.pdf")));
    }
}
