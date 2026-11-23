// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using PdfSharp.Pdf;
using SkiaSharp;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <param name="Format">"png" or "jpeg".</param>
/// <param name="Dpi">Pixels per inch, 36 to 600.</param>
/// <param name="Pages">A page range (1-3,5) or null for every page.</param>
/// <param name="Quality">JPEG quality, 1 to 100.</param>
public sealed record ExportOptions(string Format = "png", double Dpi = 150, string? Pages = null, int Quality = 90);

/// <summary>Margins to cut off (points, on the page as shown), or <see cref="Trim"/>: cut the white paper around the content.</summary>
/// <param name="Padding">With <see cref="Trim"/>: paper kept around the content, in points.</param>
public sealed record CropOptions(double Top = 0, double Right = 0, double Bottom = 0, double Left = 0, bool Trim = false, double Padding = 6,
                                 string? Pages = null);

/// <summary>
/// Pages as images (PNG or JPEG, each read back before the next is written) and cropped pages (the page's crop box is
/// set, nothing is removed from the file; each cropped page is rendered and compared with the same part of the
/// original). Callers hold <see cref="PdfiumDocument.Gate"/>.
/// </summary>
public static class PdfPageImages
{
    /// <summary>Writes &lt;name&gt;_p&lt;n&gt;.png (or .jpg) into <paramref name="folder"/>; returns the files written.</summary>
    public static IReadOnlyList<string> Export(string input, string folder, ExportOptions? options = null)
    {
        var o = options ?? new ExportOptions();
        var format = o.Format.ToLowerInvariant() switch
        {
            "png" => SKEncodedImageFormat.Png,
            "jpeg" or "jpg" => SKEncodedImageFormat.Jpeg,
            _ => throw new PageAssemblyException($"unknown image format '{o.Format}': use png or jpeg", "Err_ExportFormat", o.Format),
        };
        if (o.Dpi is < 36 or > 600) throw new PageAssemblyException("the resolution must be 36 to 600 dpi", "Err_ExportDpi");
        if (o.Quality is < 1 or > 100) throw new PageAssemblyException("the JPEG quality must be 1 to 100", "Err_ExportQuality");
        PdfiumDocument.EnsureInit();
        using var doc = PdfiumDocument.Open(input);
        var pages = PageAssembly.ParseRanges(o.Pages, doc.PageCount).SelectMany(r => Enumerable.Range(r.From, r.To - r.From + 1)).Distinct().Order().ToList();
        var stem = Path.GetFileNameWithoutExtension(input);
        int digits = doc.PageCount.ToString(CultureInfo.InvariantCulture).Length;
        var ext = format == SKEncodedImageFormat.Png ? ".png" : ".jpg";
        var names = pages.Select(p => Path.Combine(folder, $"{stem}_p{p.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}{ext}")).ToList();
        // nothing is written when any of the files is already there
        if (names.FirstOrDefault(File.Exists) is { } taken)
            throw new PageAssemblyException($"{Path.GetFileName(taken)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(taken));
        Directory.CreateDirectory(folder);

        var written = new List<string>();
        try
        {
            for (int i = 0; i < pages.Count; i++)
            {
                var img = doc.RenderPage(pages[i] - 1, o.Dpi / 72);
                using var bmp = ToBitmap(img);
                using var data = SKImage.FromBitmap(bmp).Encode(format, o.Quality);
                using (var fs = new FileStream(names[i], FileMode.CreateNew)) data.SaveTo(fs);
                written.Add(names[i]);
                // read back: a whole image of the page's size
                using var check = SKBitmap.Decode(names[i]);
                if (check is null || check.Width != img.Width || check.Height != img.Height)
                    throw new PageAssemblyException($"{Path.GetFileName(names[i])} did not read back; nothing was kept", "Err_ExportCheck", Path.GetFileName(names[i]));
            }
        }
        catch
        {
            foreach (var f in written) File.Delete(f);
            throw;
        }
        return written;
    }

    internal static SKBitmap ToBitmap(RgbImage img)
    {
        var bmp = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var px = new byte[img.Width * img.Height * 4];
        for (int i = 0, q = 0; i < img.Width * img.Height; i++, q += 3)
        {
            px[i * 4] = img.Pixels[q];
            px[i * 4 + 1] = img.Pixels[q + 1];
            px[i * 4 + 2] = img.Pixels[q + 2];
            px[i * 4 + 3] = 255;
        }
        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        return bmp;
    }

    /// <summary>Sets the crop box of the pages; returns how many were cropped (a blank page is left as it is when trimming).</summary>
    public static int Crop(string input, string output, CropOptions o)
    {
        if (!o.Trim && o.Top == 0 && o.Right == 0 && o.Bottom == 0 && o.Left == 0)
            throw new PageAssemblyException("nothing to crop: give margins or trim", "Err_CropNothing");
        if (new[] { o.Top, o.Right, o.Bottom, o.Left, o.Padding }.Any(v => v < 0))
            throw new PageAssemblyException("margins cannot be negative", "Err_CropNegative");
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        PdfiumDocument.EnsureInit();

        var kept = new Dictionary<int, PdfRect>();   // page (0-based) → the part kept, upright points of the original
        using (var before = PdfiumDocument.Open(input))
        using (var doc = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            var pages = PageAssembly.ParseRanges(o.Pages, doc.PageCount).SelectMany(r => Enumerable.Range(r.From, r.To - r.From + 1)).Distinct().Order();
            foreach (var number in pages)
            {
                var page = doc.Pages[number - 1];
                var (w, h) = PdfStamper.UprightSize(page);
                PdfRect keep;
                if (o.Trim)
                {
                    if (Content(before.RenderPage(number - 1, 1.0)) is not { } c) continue;   // blank: nothing to trim to
                    keep = new PdfRect(Math.Max(0, c.X0 - o.Padding), Math.Max(0, c.Y0 - o.Padding), Math.Min(w, c.X1 + o.Padding), Math.Min(h, c.Y1 + o.Padding));
                }
                else keep = new PdfRect(o.Left, o.Top, w - o.Right, h - o.Bottom);
                if (keep.Width < 36 || keep.Height < 36)
                    throw new PageAssemblyException($"page {number} would be smaller than half an inch", "Err_CropTooSmall", number);
                var box = PdfRedactor.Upright(page).Bounds(new Box(keep.X0, keep.Y0, keep.X1, keep.Y1));
                page.CropBox = new PdfRectangle(new PdfSharp.Drawing.XPoint(box.X0, box.Y0), new PdfSharp.Drawing.XPoint(box.X1, box.Y1));
                kept[number - 1] = keep;
            }
            if (kept.Count == 0) throw new PageAssemblyException("the pages are blank: there is nothing to trim to", "Err_CropBlank");
            doc.Save(output);
        }

        try { VerifyCrop(input, output, kept); }
        catch
        {
            File.Delete(output);
            throw;
        }
        return kept.Count;
    }

    /// <summary>The box around everything that is not white paper, in points (scale 1), or null for a blank page.</summary>
    private static PdfRect? Content(RgbImage img)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int q = (y * img.Width + x) * 3;
                if (img.Pixels[q] < 240 || img.Pixels[q + 1] < 240 || img.Pixels[q + 2] < 240)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
        return x1 < 0 ? null : new PdfRect(x0, y0, x1 + 1, y1 + 1);
    }

    /// <summary>Each cropped page looks like the same part of the original: compared cell by cell on a coarse grid.</summary>
    private static void VerifyCrop(string before, string after, Dictionary<int, PdfRect> kept)
    {
        using var a = PdfiumDocument.Open(before);
        using var b = PdfiumDocument.Open(after);
        const int grid = 12;
        const double scale = 1.5;
        foreach (var (page, keep) in kept.OrderBy(k => k.Key).Take(12))
        {
            var ia = a.RenderPage(page, scale);
            var ib = b.RenderPage(page, scale);
            if (Math.Abs(ib.Width - keep.Width * scale) > 3 || Math.Abs(ib.Height - keep.Height * scale) > 3)
                throw new PageAssemblyException($"page {page + 1} did not come out at the size it was cropped to; the copy was not kept", "Err_CropCheck", page + 1);
            int bad = 0;
            for (int gy = 0; gy < grid; gy++)
                for (int gx = 0; gx < grid; gx++)
                {
                    var (r1, g1, b1) = Mean(ib, gx * ib.Width / grid, gy * ib.Height / grid, (gx + 1) * ib.Width / grid, (gy + 1) * ib.Height / grid);
                    var (r0, g0, b0) = Mean(ia, (int)((keep.X0 + keep.Width * gx / grid) * scale), (int)((keep.Y0 + keep.Height * gy / grid) * scale),
                                                (int)((keep.X0 + keep.Width * (gx + 1) / grid) * scale), (int)((keep.Y0 + keep.Height * (gy + 1) / grid) * scale));
                    if ((Math.Abs(r1 - r0) + Math.Abs(g1 - g0) + Math.Abs(b1 - b0)) / 3 > 16) bad++;
                }
            if (bad > grid * grid / 20)
                throw new PageAssemblyException($"page {page + 1} did not look like the part of the original it was cropped to; the copy was not kept", "Err_CropCheck", page + 1);
        }
    }

    private static (double R, double G, double B) Mean(RgbImage img, int x0, int y0, int x1, int y1)
    {
        x0 = Math.Clamp(x0, 0, img.Width - 1); y0 = Math.Clamp(y0, 0, img.Height - 1);
        x1 = Math.Clamp(Math.Max(x1, x0 + 1), 1, img.Width); y1 = Math.Clamp(Math.Max(y1, y0 + 1), 1, img.Height);
        long r = 0, g = 0, b = 0, n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int q = (y * img.Width + x) * 3;
                r += img.Pixels[q]; g += img.Pixels[q + 1]; b += img.Pixels[q + 2]; n++;
            }
        return n == 0 ? (255, 255, 255) : (r / (double)n, g / (double)n, b / (double)n);
    }
}
