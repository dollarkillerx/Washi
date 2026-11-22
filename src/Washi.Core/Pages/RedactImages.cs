// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>
/// Blanks the pixels of an image that lie under redaction boxes, in a copy of the image (the original may be drawn
/// elsewhere too). Images PDFsharp can decode (Flate, LZW, ASCII, run length) keep their colour space and depth;
/// others (JPEG, JPEG 2000, JBIG2, CCITT) are decoded by PDFium and written as lossless Flate images.
/// </summary>
internal static class ImageBlanker
{
    /// <summary>
    /// A blanked copy of <paramref name="image"/> drawn with <paramref name="ctm"/> (unit square to page space), already
    /// added to the document, or null when no pixel lies under a box. The page-space bounds of the blanked pixels are
    /// added to <paramref name="covered"/>.
    /// </summary>
    public static PdfDictionary? Blank(PdfDocument doc, PdfDictionary image, Mx ctm, IReadOnlyList<Box> boxes, List<Box>? covered)
    {
        int w = image.Elements.GetInteger("/Width"), h = image.Elements.GetInteger("/Height");
        if (w <= 0 || h <= 0 || image.Stream is null) return null;
        var pixels = Pixels(w, h, ctm, boxes, covered);
        if (pixels.Count == 0) return null;

        bool mask = image.Elements.GetBoolean("/ImageMask");
        var copy = (PdfDictionary)image.Clone();
        var raw = Raw(image);
        if (raw is not null && Components(image) is { } n)
        {
            int bpc = mask ? 1 : image.Elements.GetInteger("/BitsPerComponent");
            if (bpc is not (1 or 2 or 4 or 8 or 16)) throw Unsupported();
            // a mask paints where its sample is 0 (or 1 with /Decode [1 0]): blank means "not painted"
            bool blankOn = mask && !DecodeInverted(image);
            int rowBytes = (w * n * bpc + 7) / 8;
            if (raw.Length < rowBytes * h) throw Unsupported();
            foreach (var (u, v) in pixels)
                for (int c = 0; c < n; c++) SetBits(raw, v * rowBytes, (u * n + c) * bpc, bpc, blankOn);
            ContentRedactor.SetStream(copy, raw);
        }
        else
        {
            var (data, gray) = Decoded(image, w, h, mask);
            foreach (var (u, v) in pixels)
            {
                if (gray) data[v * w + u] = (byte)(mask ? 255 : 0);
                else { int p = (v * w + u) * 3; data[p] = data[p + 1] = data[p + 2] = 0; }
            }
            if (mask || gray && data.All(b => b is 0 or 255) && image.Elements.GetInteger("/BitsPerComponent") == 1)
            {
                // back to one bit per pixel (a scan stays small)
                int rowBytes = (w + 7) / 8;
                var bits = new byte[rowBytes * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (data[y * w + x] >= 128) bits[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));
                ContentRedactor.SetStream(copy, bits);
                copy.Elements["/BitsPerComponent"] = new PdfInteger(1);
            }
            else
            {
                ContentRedactor.SetStream(copy, data);
                copy.Elements["/BitsPerComponent"] = new PdfInteger(8);
            }
            if (mask) copy.Elements.Remove("/ColorSpace");
            else copy.Elements["/ColorSpace"] = new PdfName(gray ? "/DeviceGray" : "/DeviceRGB");
            copy.Elements.Remove("/Decode");
            if (copy.Elements["/Mask"] is PdfArray) copy.Elements.Remove("/Mask");   // colour key of the old colours
            copy.Elements.Remove("/SMaskInData");
        }
        copy.Elements.Remove("/Alternates");
        copy.Elements.Remove("/OPI");
        // its soft mask (alpha) may show the same shapes
        if (Deref(image.Elements["/SMask"]) is PdfDictionary smask && Blank(doc, smask, ctm, boxes, null) is { } s)
            copy.Elements["/SMask"] = s.Reference;
        doc.Internals.AddObject(copy);
        return copy;
    }

    private static RedactException Unsupported() =>
        new("an image under a redaction area could not be read; nothing was changed", "Err_RedactImage");

    /// <summary>The pixels (column, row; row 0 at the top) that touch a box.</summary>
    private static List<(int U, int V)> Pixels(int w, int h, Mx ctm, IReadOnlyList<Box> boxes, List<Box>? covered)
    {
        var list = new List<(int, int)>();
        if (ctm.Inverse() is not { } inv) return list;
        // half a pixel in page space, so a pixel that only partly lies in the box goes too
        double hx = 0.5 * (Math.Abs(ctm.A) / w + Math.Abs(ctm.C) / h), hy = 0.5 * (Math.Abs(ctm.B) / w + Math.Abs(ctm.D) / h);
        var seen = new HashSet<(int, int)>();
        foreach (var box in boxes)
        {
            var g = new Box(box.X0 - hx, box.Y0 - hy, box.X1 + hx, box.Y1 + hy);
            var u = inv.Bounds(g);   // in unit space
            int u0 = Math.Max(0, (int)Math.Floor(u.X0 * w)), u1 = Math.Min(w - 1, (int)Math.Ceiling(u.X1 * w));
            int v0 = Math.Max(0, (int)Math.Floor((1 - u.Y1) * h)), v1 = Math.Min(h - 1, (int)Math.Ceiling((1 - u.Y0) * h));
            for (int v = v0; v <= v1; v++)
                for (int x = u0; x <= u1; x++)
                {
                    var (px, py) = ctm.Apply((x + 0.5) / w, 1 - (v + 0.5) / h);
                    if (!g.Contains(px, py) || !seen.Add((x, v))) continue;
                    list.Add((x, v));
                    covered?.Add(new Box(px - hx, py - hy, px + hx, py + hy));
                }
        }
        if (covered is not null && list.Count > 0)
        {
            // one rectangle per box is enough for the cover
            var mine = covered.Skip(covered.Count - list.Count).ToList();
            covered.RemoveRange(covered.Count - list.Count, list.Count);
            covered.Add(mine.Aggregate((a, b) => a.Union(b)));
        }
        return list;
    }

    private static void SetBits(byte[] data, int rowStart, int bit, int bits, bool on)
    {
        for (int i = 0; i < bits; i++, bit++)
        {
            int at = rowStart + bit / 8;
            byte m = (byte)(0x80 >> (bit % 8));
            data[at] = on ? (byte)(data[at] | m) : (byte)(data[at] & ~m);
        }
    }

    private static bool DecodeInverted(PdfDictionary image) =>
        Deref(image.Elements["/Decode"]) is PdfArray d && d.Elements.Count >= 2 && FontMetrics.Num(d.Elements[0]) > FontMetrics.Num(d.Elements[1]);

    /// <summary>Colour components per pixel, or null for colour spaces this does not know.</summary>
    internal static int? Components(PdfDictionary image)
    {
        if (image.Elements.GetBoolean("/ImageMask")) return 1;
        return Count(Deref(image.Elements["/ColorSpace"]));

        static int? Count(PdfItem? cs) => cs switch
        {
            PdfName { Value: "/DeviceGray" or "/CalGray" or "/G" } => 1,
            PdfName { Value: "/DeviceRGB" or "/CalRGB" or "/RGB" } => 3,
            PdfName { Value: "/DeviceCMYK" or "/CMYK" } => 4,
            PdfArray a when a.Elements.Count > 0 && Deref(a.Elements[0]) is PdfName n => n.Value switch
            {
                "/ICCBased" when a.Elements.Count > 1 && Deref(a.Elements[1]) is PdfDictionary icc => icc.Elements.GetInteger("/N") is var k and > 0 ? k : null,
                "/CalGray" or "/Separation" or "/Indexed" or "/I" => 1,
                "/CalRGB" or "/Lab" => 3,
                "/DeviceN" when a.Elements.Count > 1 && Deref(a.Elements[1]) is PdfArray names => names.Elements.Count,
                _ => Count(n),
            },
            _ => null,
        };
    }

    /// <summary>The samples, when PDFsharp can undo every filter (no image codec needed); otherwise null.</summary>
    internal static byte[]? Raw(PdfDictionary image)
    {
        var filters = Deref(image.Elements["/Filter"]) switch
        {
            null => [],
            PdfName n => [n.Value],
            PdfArray a => a.Elements.Select(e => (Deref(e) as PdfName)?.Value ?? "").ToList(),
            _ => (List<string>?)null,
        };
        if (filters is null) return null;
        var parms = Deref(image.Elements["/DecodeParms"]) switch
        {
            PdfDictionary d => [d],
            PdfArray a => a.Elements.Select(e => Deref(e) as PdfDictionary).ToList(),
            _ => new List<PdfDictionary?>(),
        };
        var data = image.Stream!.Value;
        for (int i = 0; i < filters.Count; i++)
        {
            var f = filters[i] switch { "/Fl" => "/FlateDecode", "/LZW" => "/LZWDecode", "/AHx" => "/ASCIIHexDecode", "/A85" => "/ASCII85Decode", "/RL" => "/RunLengthDecode", var x => x };
            if (f is not ("/FlateDecode" or "/LZWDecode" or "/ASCIIHexDecode" or "/ASCII85Decode" or "/RunLengthDecode")) return null;
            data = f == "/RunLengthDecode" ? RunLength(data) : PdfSharp.Pdf.Filters.Filtering.Decode(data, f);
            if (data is null) return null;
            if (i < parms.Count && parms[i] is { } p && p.Elements.GetInteger("/Predictor") > 1)
            {
                data = Unpredict(data, p);
                if (data is null) return null;
            }
        }
        return data;
    }

    private static byte[] RunLength(byte[] s)
    {
        var o = new List<byte>(s.Length * 2);
        for (int i = 0; i < s.Length;)
        {
            int n = s[i++];
            if (n == 128) break;
            if (n < 128) { for (int k = 0; k <= n && i < s.Length; k++) o.Add(s[i++]); }
            else if (i < s.Length) { byte b = s[i++]; for (int k = 0; k < 257 - n; k++) o.Add(b); }
        }
        return [.. o];
    }

    /// <summary>Undoes a TIFF (2) or PNG (10-15) predictor.</summary>
    private static byte[]? Unpredict(byte[] data, PdfDictionary p)
    {
        int predictor = p.Elements.GetInteger("/Predictor");
        int colors = p.Elements.ContainsKey("/Colors") ? p.Elements.GetInteger("/Colors") : 1;
        int bpc = p.Elements.ContainsKey("/BitsPerComponent") ? p.Elements.GetInteger("/BitsPerComponent") : 8;
        int columns = p.Elements.ContainsKey("/Columns") ? p.Elements.GetInteger("/Columns") : 1;
        int bpp = Math.Max(1, colors * bpc / 8), row = (colors * bpc * columns + 7) / 8;
        if (predictor == 2)
        {
            if (bpc != 8) return null;
            var o = (byte[])data.Clone();
            for (int r = 0; r + row <= o.Length; r += row)
                for (int i = bpp; i < row; i++) o[r + i] = (byte)(o[r + i] + o[r + i - bpp]);
            return o;
        }
        var outp = new List<byte>(data.Length);
        var prev = new byte[row];
        for (int at = 0; at + 1 + row <= data.Length; at += row + 1)
        {
            int type = data[at];
            var cur = new byte[row];
            for (int i = 0; i < row; i++)
            {
                int x = data[at + 1 + i];
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                cur[i] = type switch
                {
                    0 => (byte)x,
                    1 => (byte)(x + a),
                    2 => (byte)(x + b),
                    3 => (byte)(x + (a + b) / 2),
                    4 => (byte)(x + Paeth(a, b, c)),
                    _ => (byte)x,
                };
            }
            outp.AddRange(cur);
            prev = cur;
        }
        return [.. outp];

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    // ------------------------------------------------------------------ images that need a codec (PDFium)

    /// <summary>
    /// Decodes the image with PDFium: it is copied into a one-page PDF in memory (nothing is written to disk) and read
    /// back as a bitmap. Gray images give one byte per pixel, everything else RGB.
    /// </summary>
    internal static (byte[] Data, bool Gray) Decoded(PdfDictionary image, int w, int h, bool mask)
    {
        var pdf = MiniPdf(image, mask);
        PdfiumDocument.EnsureInit();
        var handle = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        try
        {
            var doc = fpdfview.FPDF_LoadMemDocument(handle.AddrOfPinnedObject(), pdf.Length, null);
            if (doc is null || doc.__Instance == IntPtr.Zero) throw Unsupported();
            try
            {
                var page = fpdfview.FPDF_LoadPage(doc, 0);
                try
                {
                    if (fpdf_edit.FPDFPageCountObjects(page) < 1) throw Unsupported();
                    var obj = fpdf_edit.FPDFPageGetObject(page, 0);
                    var bmp = fpdf_edit.FPDFImageObjGetBitmap(obj);
                    if (bmp is null || bmp.__Instance == IntPtr.Zero) throw Unsupported();
                    try
                    {
                        int bw = fpdfview.FPDFBitmapGetWidth(bmp), bh = fpdfview.FPDFBitmapGetHeight(bmp), stride = fpdfview.FPDFBitmapGetStride(bmp);
                        int format = fpdfview.FPDFBitmapGetFormat(bmp);   // 1 gray, 2 BGR, 3 BGRx, 4 BGRA
                        if (bw != w || bh != h || format is < 1 or > 4) throw Unsupported();
                        var buf = new byte[stride * bh];
                        Marshal.Copy(fpdfview.FPDFBitmapGetBuffer(bmp), buf, 0, buf.Length);
                        bool gray = format == 1;
                        int bpp = format switch { 1 => 1, 2 => 3, _ => 4 };
                        var data = new byte[w * h * (gray ? 1 : 3)];
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                int s = y * stride + x * bpp;
                                if (gray) data[y * w + x] = buf[s];
                                else { int d = (y * w + x) * 3; data[d] = buf[s + 2]; data[d + 1] = buf[s + 1]; data[d + 2] = buf[s]; }
                            }
                        return (data, gray);
                    }
                    finally { fpdfview.FPDFBitmapDestroy(bmp); }
                }
                finally { fpdfview.FPDF_ClosePage(page); }
            }
            finally { fpdfview.FPDF_CloseDocument(doc); }
        }
        finally { handle.Free(); }
    }

    /// <summary>A minimal PDF whose only page draws the image (its data and parameters copied as they are).</summary>
    private static byte[] MiniPdf(PdfDictionary image, bool mask)
    {
        var objects = new List<byte[]>();   // object n+1
        int Reserve() { objects.Add([]); return objects.Count; }
        int Stream(string dict, byte[] data)
        {
            int n = Reserve();
            using var ms = new MemoryStream();
            var head = Encoding.Latin1.GetBytes($"<<{dict}/Length {data.Length}>>\nstream\n");
            ms.Write(head); ms.Write(data); ms.Write("\nendstream"u8);
            objects[n - 1] = ms.ToArray();
            return n;
        }
        string Write(PdfItem? item, int depth)
        {
            if (depth > 8) return "null";
            switch (item)
            {
                case null: return "null";
                case PdfReference r when r.Value is PdfDictionary sd && sd.Stream is not null:
                    var inner = new StringBuilder();
                    foreach (var key in sd.Elements.Keys)
                        if (key is not "/Length") inner.Append(key).Append(' ').Append(Write(sd.Elements[key], depth + 1)).Append(' ');
                    return Stream(inner.ToString(), sd.Stream.Value) + " 0 R";
                case PdfReference r: return Write(r.Value, depth + 1);
                case PdfName n: return n.Value;
                case PdfInteger i: return i.Value.ToString(CultureInfo.InvariantCulture);
                case PdfLongInteger l: return l.Value.ToString(CultureInfo.InvariantCulture);
                case PdfReal f: return f.Value.ToString("0.######", CultureInfo.InvariantCulture);
                case PdfBoolean b: return b.Value ? "true" : "false";
                case PdfString s: return "<" + Convert.ToHexString(Encoding.Latin1.GetBytes(s.Value)) + ">";
                case PdfArray a: return "[" + string.Join(" ", a.Elements.Select(e => Write(e, depth + 1))) + "]";
                case PdfDictionary d when d.Stream is not null:
                    var sb2 = new StringBuilder();
                    foreach (var key in d.Elements.Keys)
                        if (key is not "/Length") sb2.Append(key).Append(' ').Append(Write(d.Elements[key], depth + 1)).Append(' ');
                    return Stream(sb2.ToString(), d.Stream.Value) + " 0 R";
                case PdfDictionary d:
                    return "<<" + string.Join(" ", d.Elements.Keys.Select(k => k + " " + Write(d.Elements[k], depth + 1))) + ">>";
                default: return "null";
            }
        }

        int w = image.Elements.GetInteger("/Width"), h = image.Elements.GetInteger("/Height");
        int catalog = Reserve(), pages = Reserve(), page = Reserve(), content = Reserve();
        var dict = new StringBuilder("/Type /XObject /Subtype /Image ");
        foreach (var key in image.Elements.Keys)
        {
            if (key is "/Length" or "/Type" or "/Subtype" or "/SMask" or "/Mask" or "/Metadata" or "/Alternates" or "/OPI" or "/ImageMask") continue;
            if (mask && key is "/BitsPerComponent" or "/ColorSpace") continue;
            dict.Append(key).Append(' ').Append(Write(image.Elements[key], 0)).Append(' ');
        }
        if (mask) dict.Append("/ColorSpace /DeviceGray /BitsPerComponent 1 ");   // read as a gray image: painted = black
        int img = Stream(dict.ToString(), image.Stream!.Value);
        objects[catalog - 1] = Encoding.ASCII.GetBytes($"<</Type /Catalog /Pages {pages} 0 R>>");
        objects[pages - 1] = Encoding.ASCII.GetBytes($"<</Type /Pages /Kids [{page} 0 R] /Count 1>>");
        objects[page - 1] = Encoding.ASCII.GetBytes($"<</Type /Page /Parent {pages} 0 R /MediaBox [0 0 {w} {h}] /Resources <</XObject <</I {img} 0 R>>>> /Contents {content} 0 R>>");
        var draw = Encoding.ASCII.GetBytes($"q {w} 0 0 {h} 0 0 cm /I Do Q");
        objects[content - 1] = [.. Encoding.ASCII.GetBytes($"<</Length {draw.Length}>>\nstream\n"), .. draw, .. "\nendstream"u8.ToArray()];

        using var o = new MemoryStream();
        o.Write("%PDF-1.7\n"u8);
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(o.Position);
            o.Write(Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n"));
            o.Write(objects[i]);
            o.Write("\nendobj\n"u8);
        }
        long xref = o.Position;
        var x = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) x.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        x.Append($"trailer\n<</Size {objects.Count + 1} /Root {catalog} 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        o.Write(Encoding.ASCII.GetBytes(x.ToString()));
        return o.ToArray();
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
