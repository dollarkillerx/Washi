// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using PDFiumCore;

namespace Washi.Core.Pdfium;

/// <summary>
/// Thin wrapper over PDFium (BSD). PDFium is not thread-safe: use one document per thread
/// and do not share instances.
/// </summary>
public sealed class PdfiumDocument : IDisposable
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    private const int PageObjText = 1;   // FPDF_PAGEOBJ_TEXT
    private const int PageObjForm = 5;   // FPDF_PAGEOBJ_FORM
    private const int AnnotWidget = 20;  // FPDF_ANNOT_WIDGET
    private const int PrecisionScale = 64;

    private readonly FpdfDocumentT _doc;
    private bool _disposed;

    private PdfiumDocument(FpdfDocumentT doc) => _doc = doc;

    public static PdfiumDocument Open(string path, string? password = null)
    {
        EnsureInit();
        var doc = fpdfview.FPDF_LoadDocument(path, password ?? "");
        if (doc is null || doc.__Instance == IntPtr.Zero)
            throw new IOException($"PDFium could not open '{path}' (error {fpdfview.FPDF_GetLastError()})");
        return new PdfiumDocument(doc);
    }

    private static void EnsureInit()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            fpdfview.FPDF_InitLibrary();
            _initialized = true;
        }
    }

    public int PageCount => fpdfview.FPDF_GetPageCount(_doc);

    /// <summary>
    /// Collects the text layer, a full render and a graphics-only render (all text objects
    /// removed in memory, so lines drawn as paths or as tiny images both survive). Page
    /// rotation is applied: everything is on the upright page, top-down, in points.
    /// Annotations and widgets are never rendered.
    /// </summary>
    public PageSnapshot Snapshot(int pageIndex, double scale)
    {
        var page = fpdfview.FPDF_LoadPage(_doc, pageIndex);
        try
        {
            double w = fpdfview.FPDF_GetPageWidthF(page), h = fpdfview.FPDF_GetPageHeightF(page);
            var map = new PageMapper(page, w, h);
            var chars = ReadChars(page, map);
            var widgets = ReadWidgets(page, map);
            var full = Render(page, w, h, scale);
            RemoveText(page);
            var graphics = Render(page, w, h, scale);
            return new PageSnapshot(pageIndex, w, h, scale, chars, full, graphics, widgets);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private static List<PdfChar> ReadChars(FpdfPageT page, PageMapper map)
    {
        var list = new List<PdfChar>();
        var tp = fpdf_text.FPDFTextLoadPage(page);
        try
        {
            int n = fpdf_text.FPDFTextCountChars(tp);
            var box = new FS_RECTF_();
            for (int i = 0; i < n; i++)
            {
                uint u = fpdf_text.FPDFTextGetUnicode(tp, i);
                if (u == 0 || u == 0xFFFE || u == 0xFFFF) continue;
                string s = char.ConvertFromUtf32((int)u);
                if (string.IsNullOrWhiteSpace(s) || char.IsControl(s, 0)) continue;
                if (fpdf_text.FPDFTextGetLooseCharBox(tp, i, box) == 0) continue;
                var r = map.ToDevice(box.Left, box.Top, box.Right, box.Bottom);
                if (r.Width <= 0 || r.Height <= 0) continue;
                uint cr = 0, cg = 0, cb = 0, ca = 0;
                fpdf_text.FPDFTextGetFillColor(tp, i, ref cr, ref cg, ref cb, ref ca);
                list.Add(new PdfChar(s, r, (int)((cr << 16) | (cg << 8) | cb)));
            }
        }
        finally
        {
            fpdf_text.FPDFTextClosePage(tp);
        }
        return list;
    }

    private static List<PdfWidget> ReadWidgets(FpdfPageT page, PageMapper map)
    {
        var list = new List<PdfWidget>();
        int n = fpdf_annot.FPDFPageGetAnnotCount(page);
        var rect = new FS_RECTF_();
        for (int i = 0; i < n; i++)
        {
            var a = fpdf_annot.FPDFPageGetAnnot(page, i);
            try
            {
                if (fpdf_annot.FPDFAnnotGetSubtype(a) != AnnotWidget) continue;
                if (fpdf_annot.FPDFAnnotGetRect(a, rect) == 0) continue;
                var r = map.ToDevice(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (r.Width >= 1) list.Add(new PdfWidget(r, 0));
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(a);
            }
        }
        return list;
    }

    private static void RemoveText(FpdfPageT page)
    {
        for (int i = fpdf_edit.FPDFPageCountObjects(page) - 1; i >= 0; i--)
        {
            var o = fpdf_edit.FPDFPageGetObject(page, i);
            int t = fpdf_edit.FPDFPageObjGetType(o);
            if (t == PageObjText)
            {
                if (fpdf_edit.FPDFPageRemoveObject(page, o) != 0) fpdf_edit.FPDFPageObjDestroy(o);
            }
            else if (t == PageObjForm)
            {
                RemoveTextInForm(o);
            }
        }
    }

    private static void RemoveTextInForm(FpdfPageobjectT form)
    {
        for (int i = (int)fpdf_edit.FPDFFormObjCountObjects(form) - 1; i >= 0; i--)
        {
            var o = fpdf_edit.FPDFFormObjGetObject(form, (uint)i);
            int t = fpdf_edit.FPDFPageObjGetType(o);
            if (t == PageObjText)
            {
                if (fpdf_edit.FPDFFormObjRemoveObject(form, o) != 0) fpdf_edit.FPDFPageObjDestroy(o);
            }
            else if (t == PageObjForm)
            {
                RemoveTextInForm(o);
            }
        }
    }

    private static RgbImage Render(FpdfPageT page, double w, double h, double scale)
    {
        int pw = (int)Math.Round(w * scale), ph = (int)Math.Round(h * scale);
        var bmp = fpdfview.FPDFBitmapCreate(pw, ph, 0);
        try
        {
            fpdfview.FPDFBitmapFillRect(bmp, 0, 0, pw, ph, 0xFFFFFFFF);
            fpdfview.FPDF_RenderPageBitmap(bmp, page, 0, 0, pw, ph, 0, 0);
            int stride = fpdfview.FPDFBitmapGetStride(bmp);
            var src = new byte[stride * ph];
            Marshal.Copy(fpdfview.FPDFBitmapGetBuffer(bmp), src, 0, src.Length);
            var rgb = new byte[pw * ph * 3];
            for (int y = 0; y < ph; y++)
            {
                int s = y * stride, d = y * pw * 3;
                for (int x = 0; x < pw; x++, s += 4, d += 3)
                {
                    rgb[d] = src[s + 2];     // BGRx -> RGB
                    rgb[d + 1] = src[s + 1];
                    rgb[d + 2] = src[s];
                }
            }
            return new RgbImage(pw, ph, rgb);
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bmp);
        }
    }

    /// <summary>PDF user space (bottom-up, unrotated) to upright top-down points.</summary>
    private readonly struct PageMapper(FpdfPageT page, double width, double height)
    {
        public PdfRect ToDevice(double left, double top, double right, double bottom)
        {
            var (ax, ay) = Point(left, top);
            var (bx, by) = Point(right, bottom);
            return new PdfRect(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
        }

        private (double, double) Point(double x, double y)
        {
            int dx = 0, dy = 0;
            int sx = (int)Math.Round(width * PrecisionScale), sy = (int)Math.Round(height * PrecisionScale);
            fpdfview.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, x, y, ref dx, ref dy);
            return (dx / (double)PrecisionScale, dy / (double)PrecisionScale);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        fpdfview.FPDF_CloseDocument(_doc);
    }
}
