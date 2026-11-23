// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using Washi.Core;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.Masume.Tests;

/// <summary>Photos and scans into PDF pages: JPEGs as they are, others lossless, upright, checked against the images.</summary>
public sealed class ImagesToPdfTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-images-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>An image with a red top-left quarter on blue (so a turn or a mirror shows), saved in the format of its extension.</summary>
    private string Image(string name, int w, int h, byte alpha = 255, bool gray = false)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(gray ? new SKColor(200, 200, 200, alpha) : new SKColor(30, 60, 200, alpha));
            using var red = new SKPaint { Color = gray ? new SKColor(20, 20, 20, alpha) : new SKColor(220, 30, 30, alpha) };
            c.DrawRect(0, 0, w / 2f, h / 2f, red);
        }
        var format = Path.GetExtension(name) switch { ".png" => SKEncodedImageFormat.Png, ".webp" => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Jpeg };
        using var data = SKImage.FromBitmap(bmp).Encode(format, 92);
        File.WriteAllBytes(P(name), data.ToArray());
        return P(name);
    }

    /// <summary>A JPEG with an EXIF orientation tag, as a phone writes it.</summary>
    private string Rotated(string name, int orientation)
    {
        var jpeg = File.ReadAllBytes(Image("plain_" + name, 160, 100));
        byte[] tiff = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,     // little endian, IFD at 8
                       0x01, 0x00,                                         // one entry
                       0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
                       0x00, 0x00, 0x00, 0x00];                            // no next IFD
        byte[] exif = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        int len = exif.Length + 2;
        byte[] app1 = [0xFF, 0xE1, (byte)(len >> 8), (byte)len, .. exif];
        File.WriteAllBytes(P(name), [.. jpeg[..2], .. app1, .. jpeg[2..]]);
        return P(name);
    }

    private static T Locked<T>(Func<T> f)
    {
        PdfiumDocument.Gate.Wait();
        try { return f(); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    /// <summary>The colour at a point of a page (fractions of its width and height).</summary>
    private static (byte R, byte G, byte B) At(string pdf, int page, double fx, double fy) => Locked(() =>
    {
        using var d = PdfiumDocument.Open(pdf);
        var img = d.RenderPage(page, 0.5);
        int q = ((int)(img.Height * fy) * img.Width + (int)(img.Width * fx)) * 3;
        return (img.Pixels[q], img.Pixels[q + 1], img.Pixels[q + 2]);
    });

    private static bool Red((byte R, byte G, byte B) c) => c.R > 180 && c.G < 90 && c.B < 90;
    private static bool Blue((byte R, byte G, byte B) c) => c.B > 160 && c.R < 90;

    [Fact]
    public void A_JPEG_goes_in_as_it_is_on_a_page_turned_to_suit_it()
    {
        var wide = Image("wide.jpg", 300, 200);
        var tall = Image("tall.jpg", 200, 300);
        var r = Locked(() => PdfFromImages.Create([wide, tall], P("out.pdf")));
        Assert.Equal(new ImagesResult(2, 2), r);
        using var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import);
        Assert.True(d.Pages[0].Width.Point > d.Pages[0].Height.Point);           // A4 landscape for the wide photo
        Assert.Equal(841.89, d.Pages[0].Width.Point, 1);
        Assert.True(d.Pages[1].Width.Point < d.Pages[1].Height.Point);
        var im = ((PdfReference)d.Pages[0].Resources.Elements.GetDictionary("/XObject")!.Elements["/Im1"]!).Value as PdfDictionary;
        Assert.Equal("/DCTDecode", im!.Elements.GetName("/Filter"));
        Assert.Equal(File.ReadAllBytes(wide), im.Stream.Value);                  // byte for byte
        Assert.True(Red(At(P("out.pdf"), 0, 0.3, 0.3)));                         // the red quarter at the top left
        Assert.True(Blue(At(P("out.pdf"), 0, 0.7, 0.7)));
    }

    [Theory]
    [InlineData(6, 0.75, 0.25)]   // turned 90° clockwise: the stored top left ends up at the top right
    [InlineData(8, 0.25, 0.75)]   // 90° counter-clockwise: at the bottom left
    [InlineData(3, 0.75, 0.75)]   // 180°
    [InlineData(2, 0.75, 0.25)]   // mirrored
    public void A_phone_photo_stands_as_the_camera_meant(int orientation, double fx, double fy)
    {
        var photo = Rotated($"o{orientation}.jpg", orientation);
        Locked(() => PdfFromImages.Create([photo], P("out.pdf"), new ImagePageOptions("image", 0, 72)));
        using (var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import))
        {
            bool turned = orientation is 5 or 6 or 7 or 8;
            Assert.Equal(turned ? 100 : 160, d.Pages[0].Width.Point, 1);         // the image's size at 72 dpi, as it stands
            Assert.Equal(turned ? 160 : 100, d.Pages[0].Height.Point, 1);
        }
        Assert.True(Red(At(P("out.pdf"), 0, fx, fy)));
    }

    [Fact]
    public void PNGs_are_stored_losslessly_with_their_transparency_and_gray_stays_gray()
    {
        var clear = Image("clear.png", 120, 80, alpha: 128);
        var gray = Image("gray.png", 120, 80, gray: true);
        var r = Locked(() => PdfFromImages.Create([clear, gray], P("out.pdf"), new ImagePageOptions("letter", 36)));
        Assert.Equal(new ImagesResult(2, 0), r);
        using var d = PdfReader.Open(P("out.pdf"), PdfDocumentOpenMode.Import);
        PdfDictionary Im(int page) => (((PdfReference)d.Pages[page].Resources.Elements.GetDictionary("/XObject")!.Elements["/Im1"]!).Value as PdfDictionary)!;
        Assert.Equal("/FlateDecode", Im(0).Elements.GetName("/Filter"));
        Assert.NotNull(Im(0).Elements["/SMask"]);
        Assert.Equal("/DeviceRGB", Im(0).Elements.GetName("/ColorSpace"));
        Assert.Null(Im(1).Elements["/SMask"]);
        Assert.Equal("/DeviceGray", Im(1).Elements.GetName("/ColorSpace"));
        Assert.Equal(792, d.Pages[0].Width.Point, 1);                             // Letter, landscape
        // half see-through red over half see-through blue, on white paper; and the blue alone
        var mixed = At(P("out.pdf"), 0, 0.3, 0.35);
        Assert.InRange(mixed.R, 170, 190);
        Assert.InRange(mixed.G, 84, 104);
        Assert.InRange(mixed.B, 118, 138);
        var blue = At(P("out.pdf"), 0, 0.7, 0.7);
        Assert.InRange(blue.R, 132, 152);
        Assert.InRange(blue.B, 216, 236);
    }

    [Fact]
    public void Images_and_PDFs_combine_in_order()
    {
        var first = Image("a.jpg", 200, 300);
        var pdf = P("doc.pdf");
        Locked(() => PdfFromImages.Create([Image("b.png", 200, 300), Image("c.webp", 200, 300)], pdf));
        int pages = Locked(() => PdfFromImages.CombineFiles([first, pdf, Image("d.jpg", 300, 200)], P("all.pdf")));
        Assert.Equal(4, pages);
        Assert.Equal(4, Locked(() => PageAssembly.PageCount(P("all.pdf"))));
    }

    [Fact]
    public void Files_that_are_not_images_are_refused_and_nothing_is_written()
    {
        File.WriteAllText(P("photo.heic"), "x");
        File.WriteAllText(P("broken.jpg"), "not a jpeg");
        Assert.Equal("Err_ImageUnsupported", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfFromImages.Create([P("photo.heic")], P("a.pdf")))).Key);
        Assert.Equal("Err_ImageUnreadable", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfFromImages.Create([P("broken.jpg")], P("b.pdf")))).Key);
        Assert.Equal("Err_ImagesNone", Assert.Throws<PageAssemblyException>(() => Locked(() => PdfFromImages.Create([], P("c.pdf")))).Key);
        Assert.Equal("Err_ImagesPageSize", Assert.Throws<PageAssemblyException>(() =>
            Locked(() => PdfFromImages.Create([Image("x.jpg", 10, 10)], P("d.pdf"), new ImagePageOptions("a3")))).Key);
        Assert.False(File.Exists(P("a.pdf")) || File.Exists(P("b.pdf")));
    }

    [Fact]
    public void JPEG_frame_headers_are_read()
    {
        var info = PdfFromImages.JpegInfo(File.ReadAllBytes(Image("i.jpg", 33, 17)));
        Assert.Equal((33, 17, 3, false), info);
        Assert.Null(PdfFromImages.JpegInfo([0x89, 0x50, 0x4E, 0x47]));
    }

    [Fact]
    public async Task Agents_make_a_PDF_from_images()
    {
        var a = Image("scan1.jpg", 200, 300);
        var b = Image("scan2.png", 200, 300);
        var tools = Washi.Tools.WashiTools.CreateRegistry(new Washi.Tools.PathPolicy([_dir]));
        var r = await tools.Find("images_to_pdf")!.InvokeAsync(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            images = new[] { a, b }, page_size = "letter", margin = 18,
        }), TestContext.Current.CancellationToken);
        Assert.False(r.IsError, r.Text);
        var json = System.Text.Json.JsonDocument.Parse(r.Text).RootElement;
        Assert.Equal(2, json.GetProperty("pages").GetInt32());
        Assert.Equal(1, json.GetProperty("jpegs_kept_as_is").GetInt32());
        Assert.True(File.Exists(P("scan1.pdf")));
    }
}
