// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using SkiaSharp;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>The page each image gets.</summary>
/// <param name="PageSize">"a4", "letter" (turned to suit each image), or "image" (the image's size at <paramref name="Dpi"/>).</param>
/// <param name="Margin">Points of paper around the image.</param>
public sealed record ImagePageOptions(string PageSize = "a4", double Margin = 0, double Dpi = 150);

/// <param name="Pages">Pages written.</param>
/// <param name="KeptAsIs">JPEG photos put in the file as they were (not decoded and compressed again).</param>
public sealed record ImagesResult(int Pages, int KeptAsIs);

/// <summary>
/// Photos and scans into PDF pages: JPEG files go in as they are (no loss, no growth), PNG, WebP, BMP and GIF are
/// decoded and stored losslessly (with their transparency). A photo's EXIF orientation is applied, so it stands as the
/// camera meant. Each page is rendered afterwards and compared with its image before the file is kept.
/// Callers hold <see cref="PdfiumDocument.Gate"/>.
/// </summary>
public static class PdfFromImages
{
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"];

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static ImagesResult Create(IReadOnlyList<string> images, string output, ImagePageOptions? options = null)
    {
        var o = options ?? new ImagePageOptions();
        if (images.Count == 0) throw new PageAssemblyException("no images", "Err_ImagesNone");
        if (o.PageSize is not ("a4" or "letter" or "image")) throw new PageAssemblyException($"unknown page size '{o.PageSize}': use a4, letter or image", "Err_ImagesPageSize", o.PageSize);
        if (o.Margin is < 0 or > 144 || o.Dpi is < 36 or > 1200) throw new PageAssemblyException("the margin must be 0 to 144 points and the resolution 36 to 1200 dpi", "Err_ImagesMargin");
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));

        var placed = new List<(string Path, SKEncodedOrigin Origin, PdfRect Area)>();
        int kept = 0;
        using (var doc = new PdfDocument())
        {
            doc.Info.Creator = "Washi";
            foreach (var path in images)
            {
                var image = Load(doc, path);
                if (image.AsIs) kept++;
                bool turned = image.Origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
                double iw = turned ? image.Height : image.Width, ih = turned ? image.Width : image.Height;   // as it stands

                // the page, and the image's place on it (as large as fits, centred)
                double pw, ph;
                if (o.PageSize == "image") { pw = iw * 72 / o.Dpi + 2 * o.Margin; ph = ih * 72 / o.Dpi + 2 * o.Margin; }
                else
                {
                    (pw, ph) = o.PageSize == "letter" ? (612.0, 792.0) : (595.28, 841.89);
                    if (iw > ih) (pw, ph) = (ph, pw);
                }
                double scale = Math.Min((pw - 2 * o.Margin) / iw, (ph - 2 * o.Margin) / ih);
                double w = iw * scale, h = ih * scale, x = (pw - w) / 2, y = (ph - h) / 2;

                var page = doc.AddPage();
                page.Width = PdfSharp.Drawing.XUnit.FromPoint(pw);
                page.Height = PdfSharp.Drawing.XUnit.FromPoint(ph);
                var resources = new PdfDictionary(doc);
                var xobjects = new PdfDictionary(doc);
                xobjects.Elements["/Im1"] = image.Dict.Reference!;
                resources.Elements["/XObject"] = xobjects;
                page.Elements["/Resources"] = resources;
                var (a, b, c, d, e, f) = Placement(image.Origin, x, y, w, h);
                var content = new PdfDictionary(doc);
                content.CreateStream(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                    $"q {N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} cm /Im1 Do Q\n")));
                doc.Internals.AddObject(content);
                page.Elements["/Contents"] = content.Reference!;
                placed.Add((path, image.Origin, new PdfRect(x, ph - y - h, x + w, ph - y)));
            }
            doc.Save(output);
        }

        try { Verify(output, placed); }
        catch
        {
            File.Delete(output);
            throw;
        }
        return new ImagesResult(placed.Count, kept);
    }

    /// <summary>
    /// Several files in order into one PDF: runs of images become pages as in <see cref="Create"/>, PDFs are added as
    /// they are (their form fields kept, see <see cref="PageAssembly.Merge"/>).
    /// </summary>
    public static int CombineFiles(IReadOnlyList<string> inputs, string output, ImagePageOptions? options = null)
    {
        if (inputs.Count == 0) throw new PageAssemblyException("nothing to merge", "Err_NothingToMerge");
        if (inputs.All(IsImage)) return Create(inputs, output, options).Pages;
        if (!inputs.Any(IsImage)) return PageAssembly.Merge(inputs, output);
        var dir = Directory.CreateTempSubdirectory("washi-images-").FullName;
        try
        {
            var parts = new List<string>();
            var run = new List<string>();
            void Flush()
            {
                if (run.Count == 0) return;
                var part = Path.Combine(dir, $"{parts.Count}.pdf");
                Create(run, part, options);
                parts.Add(part);
                run.Clear();
            }
            foreach (var input in inputs)
            {
                if (IsImage(input)) { run.Add(input); continue; }
                Flush();
                parts.Add(input);
            }
            Flush();
            return PageAssembly.Merge(parts, output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    internal sealed record Loaded(PdfDictionary Dict, int Width, int Height, SKEncodedOrigin Origin, bool AsIs);

    internal static Loaded Load(PdfDocument doc, string path)
    {
        if (!File.Exists(path)) throw new PageAssemblyException($"{Path.GetFileName(path)} does not exist", "Err_NotFound", Path.GetFileName(path));
        if (!IsImage(path)) throw new PageAssemblyException($"{Path.GetFileName(path)} is not an image Washi can read (JPEG, PNG, WebP, BMP, GIF)", "Err_ImageUnsupported", Path.GetFileName(path));
        var bytes = File.ReadAllBytes(path);
        using var codec = SKCodec.Create(new MemoryStream(bytes))
            ?? throw new PageAssemblyException($"{Path.GetFileName(path)} could not be read as an image", "Err_ImageUnreadable", Path.GetFileName(path));
        var origin = codec.EncodedOrigin;
        var dict = new PdfDictionary(doc);
        dict.Elements.SetName("/Type", "/XObject");
        dict.Elements.SetName("/Subtype", "/Image");
        dict.Elements.SetInteger("/BitsPerComponent", 8);

        // a JPEG goes in as it is
        if (codec.EncodedFormat == SKEncodedImageFormat.Jpeg && JpegInfo(bytes) is { } j && j.Components is 1 or 3 or 4)
        {
            dict.Elements.SetInteger("/Width", j.Width);
            dict.Elements.SetInteger("/Height", j.Height);
            dict.Elements.SetName("/ColorSpace", j.Components switch { 1 => "/DeviceGray", 3 => "/DeviceRGB", _ => "/DeviceCMYK" });
            if (j.Components == 4 && j.Adobe)   // Adobe's CMYK JPEGs are stored inverted
                dict.Elements["/Decode"] = new PdfArray(doc, [.. Enumerable.Range(0, 8).Select(k => (PdfItem)new PdfInteger(k % 2 == 0 ? 1 : 0))]);
            dict.Elements.SetName("/Filter", "/DCTDecode");
            dict.CreateStream(bytes);
            doc.Internals.AddObject(dict);
            return new Loaded(dict, j.Width, j.Height, origin, true);
        }

        // anything else: decoded, stored losslessly
        using var bmp = SKBitmap.Decode(codec)
            ?? throw new PageAssemblyException($"{Path.GetFileName(path)} could not be read as an image", "Err_ImageUnreadable", Path.GetFileName(path));
        using var rgba = bmp.ColorType == SKColorType.Rgba8888 && bmp.AlphaType != SKAlphaType.Premul ? null : Convert(bmp);
        var src = rgba ?? bmp;
        int n = src.Width * src.Height;
        var px = src.GetPixelSpan();
        bool gray = true, opaque = true;
        for (int i = 0; i < n && (gray || opaque); i++)
        {
            byte r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2], a = px[i * 4 + 3];
            if (r != g || g != b) gray = false;
            if (a != 255) opaque = false;
        }
        var color = new byte[n * (gray ? 1 : 3)];
        var alpha = opaque ? null : new byte[n];
        for (int i = 0; i < n; i++)
        {
            if (gray) color[i] = px[i * 4];
            else { color[i * 3] = px[i * 4]; color[i * 3 + 1] = px[i * 4 + 1]; color[i * 3 + 2] = px[i * 4 + 2]; }
            if (alpha is not null) alpha[i] = px[i * 4 + 3];
        }
        dict.Elements.SetInteger("/Width", src.Width);
        dict.Elements.SetInteger("/Height", src.Height);
        dict.Elements.SetName("/ColorSpace", gray ? "/DeviceGray" : "/DeviceRGB");
        dict.Elements.SetName("/Filter", "/FlateDecode");
        dict.CreateStream(Flate(color));
        if (alpha is not null)
        {
            var mask = new PdfDictionary(doc);
            mask.Elements.SetName("/Type", "/XObject");
            mask.Elements.SetName("/Subtype", "/Image");
            mask.Elements.SetInteger("/Width", src.Width);
            mask.Elements.SetInteger("/Height", src.Height);
            mask.Elements.SetName("/ColorSpace", "/DeviceGray");
            mask.Elements.SetInteger("/BitsPerComponent", 8);
            mask.Elements.SetName("/Filter", "/FlateDecode");
            mask.CreateStream(Flate(alpha));
            doc.Internals.AddObject(mask);
            dict.Elements["/SMask"] = mask.Reference!;
        }
        doc.Internals.AddObject(dict);
        return new Loaded(dict, src.Width, src.Height, origin, false);
    }

    private static SKBitmap Convert(SKBitmap bmp)
    {
        var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var copy = new SKBitmap(info);
        // read through a pixmap: it converts the colour type and undoes premultiplied alpha
        using var from = bmp.PeekPixels();
        using var to = copy.PeekPixels();
        if (!from.ReadPixels(to)) throw new InvalidOperationException("could not convert the image's pixels");
        return copy;
    }

    private static byte[] Flate(byte[] data) =>
        new PdfSharp.Pdf.Filters.FlateDecode().Encode(data, PdfFlateEncodeMode.BestCompression);

    /// <summary>The size and colour components from a JPEG's frame header, and whether Adobe's marker is there.</summary>
    internal static (int Width, int Height, int Components, bool Adobe)? JpegInfo(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
        bool adobe = false;
        int i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF) return null;
            byte marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            int len = b[i + 2] << 8 | b[i + 3];
            if (len < 2 || i + 2 + len > b.Length) return null;
            if (marker == 0xEE && len >= 7 && Encoding.ASCII.GetString(b, i + 4, 5) == "Adobe") adobe = true;
            // baseline, extended or progressive Huffman frames (arithmetic and lossless ones are decoded instead)
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (len < 8 || b[i + 4] != 8) return null;   // 8 bits per sample only
                int h = b[i + 5] << 8 | b[i + 6], w = b[i + 7] << 8 | b[i + 8], n = b[i + 9];
                return w > 0 && h > 0 ? (w, h, n, adobe) : null;
            }
            if (marker is >= 0xC3 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return null;
            if (marker == 0xDA) return null;   // scan before any frame header
            i += 2 + len;
        }
        return null;
    }

    /// <summary>The cm that draws the image (stored top row first) upright in the box, for each EXIF orientation.</summary>
    internal static (double A, double B, double C, double D, double E, double F) Placement(SKEncodedOrigin origin, double x, double y, double w, double h) => origin switch
    {
        SKEncodedOrigin.TopRight => (-w, 0, 0, h, x + w, y),
        SKEncodedOrigin.BottomRight => (-w, 0, 0, -h, x + w, y + h),
        SKEncodedOrigin.BottomLeft => (w, 0, 0, -h, x, y + h),
        SKEncodedOrigin.LeftTop => (0, -h, -w, 0, x + w, y + h),
        SKEncodedOrigin.RightTop => (0, -h, w, 0, x, y + h),
        SKEncodedOrigin.RightBottom => (0, h, w, 0, x, y),
        SKEncodedOrigin.LeftBottom => (0, h, -w, 0, x + w, y),
        _ => (w, 0, 0, h, x, y),
    };

    /// <summary>The image as it stands (its EXIF orientation applied), for comparing.</summary>
    internal static SKBitmap Upright(SKBitmap src, SKEncodedOrigin origin)
    {
        bool turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        float W = src.Width, H = src.Height;
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, W, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, W, 0, -1, H, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, H, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, H, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, H, -1, 0, W, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, W, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        var dst = new SKBitmap(new SKImageInfo(turned ? src.Height : src.Width, turned ? src.Width : src.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    /// <summary>Each page, rendered, looks like its image: compared cell by cell on a coarse grid.</summary>
    private static void Verify(string output, List<(string Path, SKEncodedOrigin Origin, PdfRect Area)> placed)
    {
        using var pdf = PdfiumDocument.Open(output);
        if (pdf.PageCount != placed.Count)
            throw new PageAssemblyException($"the PDF has {pdf.PageCount} pages instead of {placed.Count}; it was not kept", "Err_ImagesCheck", 1, Path.GetFileName(placed[0].Path));
        const int grid = 12;
        for (int p = 0; p < placed.Count; p++)
        {
            var (path, origin, area) = placed[p];
            double scale = Math.Min(4, 480 / Math.Max(1, area.Width));
            var page = pdf.RenderPage(p, scale);
            using var decoded = SKBitmap.Decode(path);
            if (decoded is null) continue;
            // the source on white (transparent parts show the paper), as it stands, shrunk to the grid
            using var flat = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(flat)) { canvas.Clear(SKColors.White); canvas.DrawBitmap(decoded, 0, 0); }
            using var upright = Upright(flat, origin);
            var cells = Averages(upright, grid);
            int bad = 0;
            for (int gy = 0; gy < grid; gy++)
                for (int gx = 0; gx < grid; gx++)
                {
                    // the same cell of the rendered page, averaged
                    int x0 = (int)((area.X0 + area.Width * gx / grid) * scale), x1 = (int)((area.X0 + area.Width * (gx + 1) / grid) * scale);
                    int y0 = (int)((area.Y0 + area.Height * gy / grid) * scale), y1 = (int)((area.Y0 + area.Height * (gy + 1) / grid) * scale);
                    x1 = Math.Min(Math.Max(x1, x0 + 1), page.Width); y1 = Math.Min(Math.Max(y1, y0 + 1), page.Height);
                    long r = 0, g = 0, bl = 0, count = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            int q = (yy * page.Width + xx) * 3;
                            r += page.Pixels[q]; g += page.Pixels[q + 1]; bl += page.Pixels[q + 2]; count++;
                        }
                    if (count == 0) continue;
                    var c = cells[gy * grid + gx];
                    double diff = (Math.Abs(r / (double)count - c.R) + Math.Abs(g / (double)count - c.G) + Math.Abs(bl / (double)count - c.B)) / 3;
                    if (diff > 40) bad++;
                }
            if (bad > grid * grid / 20)
                throw new PageAssemblyException($"page {p + 1} does not look like {Path.GetFileName(path)}; the PDF was not kept", "Err_ImagesCheck", p + 1, Path.GetFileName(path));
        }
    }

    /// <summary>The mean colour of each cell of a grid laid over the bitmap (every pixel counted).</summary>
    internal static (double R, double G, double B)[] Averages(SKBitmap bmp, int grid)
    {
        var sums = new (double R, double G, double B, int N)[grid * grid];
        var px = bmp.GetPixelSpan();
        int w = bmp.Width, h = bmp.Height;
        for (int y = 0; y < h; y++)
        {
            int gy = Math.Min(grid - 1, y * grid / h);
            for (int x = 0; x < w; x++)
            {
                int gx = Math.Min(grid - 1, x * grid / w), q = (y * w + x) * 4;
                ref var c = ref sums[gy * grid + gx];
                c.R += px[q]; c.G += px[q + 1]; c.B += px[q + 2]; c.N++;
            }
        }
        return [.. sums.Select(c => c.N == 0 ? (255.0, 255.0, 255.0) : (c.R / c.N, c.G / c.N, c.B / c.N))];
    }

    private static string N(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
