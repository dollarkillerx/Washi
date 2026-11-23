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
        var doc = fpdfview.FPDF_LoadDocument(path, password ?? Pages.PdfPasswords.For(path) ?? "");
        if (doc is null || doc.__Instance == IntPtr.Zero)
            throw new IOException($"PDFium could not open '{path}' (error {fpdfview.FPDF_GetLastError()})");
        return new PdfiumDocument(doc);
    }

    /// <summary>True when <paramref name="path"/> cannot be opened without a password (none, or the remembered one, works).
    /// Callers hold <see cref="Gate"/>.</summary>
    public static bool NeedsPassword(string path, string? password = null)
    {
        EnsureInit();
        var doc = fpdfview.FPDF_LoadDocument(path, password ?? Pages.PdfPasswords.For(path) ?? "");
        if (doc is null || doc.__Instance == IntPtr.Zero) return fpdfview.FPDF_GetLastError() == 4;   // FPDF_ERR_PASSWORD
        fpdfview.FPDF_CloseDocument(doc);
        return false;
    }

    internal static void EnsureInit()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            fpdfview.FPDF_InitLibrary();
            _initialized = true;
        }
    }

    public int PageCount => fpdfview.FPDF_GetPageCount(_doc);

    /// <summary>True when the file is encrypted (it may still open with an empty user password).</summary>
    public bool IsEncrypted => fpdfview.FPDF_GetSecurityHandlerRevision(_doc) != -1;

    /// <summary>One process-wide lock: PDFium must never run on two threads at once.</summary>
    public static readonly SemaphoreSlim Gate = new(1, 1);

    public readonly record struct PageInfo(double Width, double Height, int Rotation);

    /// <summary>Upright size in points (rotation applied) and the page's /Rotate in degrees.</summary>
    public PageInfo GetPageInfo(int pageIndex)
    {
        var page = fpdfview.FPDF_LoadPage(_doc, pageIndex);
        try
        {
            return new PageInfo(fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page),
                                fpdf_edit.FPDFPageGetRotation(page) * 90);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>The page text in reading order as PDFium sees it, with line breaks.</summary>
    public string GetPageText(int pageIndex)
    {
        var page = fpdfview.FPDF_LoadPage(_doc, pageIndex);
        var tp = fpdf_text.FPDFTextLoadPage(page);
        try
        {
            int n = fpdf_text.FPDFTextCountChars(tp);
            var sb = new System.Text.StringBuilder(n);
            for (int i = 0; i < n; i++)
            {
                uint u = fpdf_text.FPDFTextGetUnicode(tp, i);
                if (u == 0 || u == 0xFFFE || u == 0xFFFF || u > 0x10FFFF || u is >= 0xD800 and <= 0xDFFF) continue;
                sb.Append(char.ConvertFromUtf32((int)u));
            }
            return sb.ToString().Replace("\r\n", "\n");
        }
        finally
        {
            fpdf_text.FPDFTextClosePage(tp);
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private FPDF_FORMFILLINFO? _formInfo;   // kept alive as long as the form handle
    private FpdfFormHandleT? _form;

    /// <summary>Renders the page as the user sees it, including form fields and other annotations.</summary>
    public RgbImage RenderPage(int pageIndex, double scale)
    {
        if (_form is null)
        {
            _formInfo = new FPDF_FORMFILLINFO { Version = 2 };
            _form = fpdf_formfill.FPDFDOC_InitFormFillEnvironment(_doc, _formInfo);
        }
        var page = fpdfview.FPDF_LoadPage(_doc, pageIndex);
        fpdf_formfill.FORM_OnAfterLoadPage(page, _form);
        try
        {
            // widgets are drawn by the form layer (FFLDraw), other annotations by the page renderer
            return Render(page, fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page), scale, FlagAnnotations,
                          (bmp, pw, ph) => fpdf_formfill.FPDF_FFLDraw(_form, bmp, page, 0, 0, pw, ph, 0, FlagAnnotations));
        }
        finally
        {
            fpdf_formfill.FORM_OnBeforeClosePage(page, _form);
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private const int FlagAnnotations = 0x01;   // FPDF_ANNOT

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

    /// <summary>The page's text layer only (no rendering).</summary>
    public IReadOnlyList<PdfChar> ReadChars(int pageIndex)
    {
        var page = fpdfview.FPDF_LoadPage(_doc, pageIndex);
        try
        {
            return ReadChars(page, new PageMapper(page, fpdfview.FPDF_GetPageWidthF(page), fpdfview.FPDF_GetPageHeightF(page)));
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

    private static RgbImage Render(FpdfPageT page, double w, double h, double scale, int flags = 0,
                                   Action<FpdfBitmapT, int, int>? drawOver = null)
    {
        int pw = (int)Math.Round(w * scale), ph = (int)Math.Round(h * scale);
        var bmp = fpdfview.FPDFBitmapCreate(pw, ph, 0);
        try
        {
            fpdfview.FPDFBitmapFillRect(bmp, 0, 0, pw, ph, 0xFFFFFFFF);
            fpdfview.FPDF_RenderPageBitmap(bmp, page, 0, 0, pw, ph, 0, flags);
            drawOver?.Invoke(bmp, pw, ph);
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
    internal readonly struct PageMapper(FpdfPageT page, double width, double height)
    {
        public PdfRect ToDevice(double left, double top, double right, double bottom)
        {
            var (ax, ay) = Point(left, top);
            var (bx, by) = Point(right, bottom);
            return new PdfRect(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
        }

        /// <summary>A point of PDF space on the upright page.</summary>
        public (double X, double Y) ToDevice(double x, double y) => Point(x, y);

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
        if (_form is not null) fpdf_formfill.FPDFDOC_ExitFormFillEnvironment(_form);
        fpdfview.FPDF_CloseDocument(_doc);
    }
}
