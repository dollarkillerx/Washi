// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Washi.Core.Pdfium;

namespace Washi.Core.Printing;

/// <summary>
/// Windows: each page is rendered by PDFium at the printer's resolution (at most 600 dpi) and drawn on the printed
/// page, turned to match the paper's orientation, fitted to the printable area or at its actual size. Callers hold
/// <see cref="PdfiumDocument.Gate"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrinting : IPrinting
{
    public IReadOnlyList<PrinterInfo> Printers()
    {
        string? dflt = new PrinterSettings().PrinterName;
        return [.. PrinterSettings.InstalledPrinters.Cast<string>().Select(n => new PrinterInfo(n, n == dflt)).OrderByDescending(p => p.IsDefault)];
    }

    public PrintResult Print(string pdf, PrintOptions o)
    {
        var (ranges, count, _) = PdfPrinting.Check(pdf, o);
        var pages = ranges.SelectMany(r => Enumerable.Range(r.From - 1, r.To - r.From + 1)).ToList();
        using var doc = PdfiumDocument.Open(pdf);
        using var job = new PrintDocument { DocumentName = Path.GetFileName(pdf) };
        if (!string.IsNullOrWhiteSpace(o.Printer)) job.PrinterSettings.PrinterName = o.Printer;
        if (!job.PrinterSettings.IsValid)
            throw new PrintException($"there is no printer called {o.Printer}", "Err_PrintNoPrinter", o.Printer ?? "");
        job.PrinterSettings.Copies = (short)o.Copies;
        job.PrinterSettings.Collate = true;
        if (o.TwoSided && job.PrinterSettings.CanDuplex) job.PrinterSettings.Duplex = Duplex.Vertical;
        job.DefaultPageSettings.Color = !o.Grayscale && job.PrinterSettings.SupportsColor;
        job.PrintController = new StandardPrintController();   // no "printing page n" window

        int next = 0;
        job.QueryPageSettings += (_, e) =>
        {
            var info = doc.GetPageInfo(pages[next]);
            e.PageSettings.Landscape = info.Width > info.Height;   // the paper follows the page
        };
        job.PrintPage += (_, e) =>
        {
            int page = pages[next++];
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Display;   // hundredths of an inch, like the bounds
            var info = doc.GetPageInfo(page);
            int dpi = Math.Clamp(Math.Max(e.PageSettings.PrinterResolution.X, 150), 150, 600);
            var image = doc.RenderPage(page, dpi / 72.0);
            using var bmp = ToBitmap(image);
            RectangleF area = o.FitToPage ? e.MarginBounds : e.PageBounds;
            float w = (float)(info.Width / 72 * 100), h = (float)(info.Height / 72 * 100);
            if (o.FitToPage)
            {
                float k = Math.Min(area.Width / w, area.Height / h);
                w *= k; h *= k;
            }
            g.DrawImage(bmp, area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            e.HasMorePages = next < pages.Count;
        };
        job.Print();
        return new PrintResult(job.PrinterSettings.PrinterName, null, count * o.Copies);
    }

    private static Bitmap ToBitmap(RgbImage image)
    {
        var bmp = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[image.Width * 3];
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0, s = y * image.Width * 3; x < image.Width; x++, s += 3)
                {
                    row[x * 3] = image.Pixels[s + 2];     // RGB -> BGR
                    row[x * 3 + 1] = image.Pixels[s + 1];
                    row[x * 3 + 2] = image.Pixels[s];
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
