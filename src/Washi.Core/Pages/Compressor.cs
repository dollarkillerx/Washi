// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using SkiaSharp;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>File sizes as people read them.</summary>
public static class CompressFormat
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1 << 20 => (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1 << 10 => (bytes / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(CultureInfo.CurrentCulture) + " B",
    };
}

public enum CompressLevel
{
    /// <summary>Lossless clean-up, and only very high-resolution JPEGs made smaller (above 300 dpi to 200).</summary>
    Light,
    /// <summary>Images above 225 dpi down to 150 dpi, photos as JPEG (quality 75): fine on screen and in print.</summary>
    Standard,
    /// <summary>Images above 150 dpi down to 100 dpi, JPEG quality 60: for e-mail.</summary>
    Strong,
}

/// <param name="Before">Bytes of the input.</param>
/// <param name="After">Bytes of the copy.</param>
/// <param name="Images">Images written anew (smaller), of which <paramref name="Downsampled"/> at a lower resolution.</param>
/// <param name="Streams">Other streams compressed (or compressed better).</param>
/// <param name="Duplicates">Identical images, fonts and other streams kept once.</param>
/// <param name="Thumbnails">Page thumbnails dropped (viewers draw their own).</param>
/// <param name="Difference">The largest difference between a page of the input and of the copy, rendered (mean, 0-255).</param>
public sealed record CompressReport(long Before, long After, int Images, int Downsampled, int Streams, int Duplicates, int Thumbnails,
                                    double Difference, IReadOnlyList<string> Notes)
{
    public double Saved => Before == 0 ? 0 : 1 - (double)After / Before;
}

/// <summary>
/// Makes a PDF smaller: images shown at a higher resolution than the level needs are scaled down and photos re-encoded
/// as JPEG, uncompressed or loosely compressed streams are compressed, identical streams (a logo on every page, the same
/// font embedded twice) are kept once, page thumbnails and earlier versions are dropped, and the small objects are packed
/// into compressed object streams. Line art, one-bit scans and
/// colour spaces that cannot go through JPEG without a shift (CMYK, spot colours) are left as they are. The copy is
/// rendered next to the input, page by page, before it is kept. Callers hold <see cref="PdfiumDocument.Gate"/>.
/// </summary>
public static class PdfCompressor
{
    private sealed record Level(double Above, double Target, int Quality, bool PhotosToJpeg, double MaxDifference);

    private static Level Settings(CompressLevel l) => l switch
    {
        CompressLevel.Light => new(300, 200, 85, false, 3),
        CompressLevel.Strong => new(150, 100, 60, true, 8),
        _ => new(225, 150, 75, true, 5),
    };

    public static CompressReport Compress(string input, string output, CompressLevel level = CompressLevel.Standard)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        if (PdfSecurity.Inspect(input, PdfPasswords.For(input)).Encrypted)
            throw new PageAssemblyException($"{Path.GetFileName(input)} is protected: remove its password first, compress it, then protect it again",
                                            "Err_CompressProtected", Path.GetFileName(input));
        var s = Settings(level);
        var notes = new List<string>();
        int images = 0, downsampled = 0, streams = 0, duplicates = 0, thumbnails = 0;
        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            if (doc.Internals.GetAllObjects().OfType<PdfDictionary>().Any(o => o.Elements.GetName("/Type") == "/Sig" && o.Elements.ContainsKey("/ByteRange")))
                notes.Add("the file is digitally signed (or carries Reader usage rights): the signatures do not hold for the compressed copy");

            // 1. images, at the size they are shown
            foreach (var (image, shown) in ImageUsage(doc))
            {
                var r = Recompress(doc, image, shown, s);
                if (r is null) continue;
                images++;
                if (r.Value) downsampled++;
            }
            // 2. page thumbnails
            foreach (var page in doc.Pages)
                if (page.Elements.ContainsKey("/Thumb")) { page.Elements.Remove("/Thumb"); thumbnails++; }
            // 3. identical streams kept once
            duplicates = Deduplicate(doc);
            // 4. every other stream compressed as well as Flate can
            streams = CompressStreams(doc);
            doc.Options.CompressContentStreams = true;
            doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            using var ms = new MemoryStream();
            doc.Save(ms, false);
            // 5. small objects packed into compressed object streams
            var bytes = ms.ToArray();
            File.WriteAllBytes(output, ObjectStreams.Pack(bytes) ?? bytes);
        }

        // proof: every page looks as it did
        double worst;
        try
        {
            worst = Difference(input, output);
            if (worst > s.MaxDifference)
                throw new PageAssemblyException($"the compressed copy looked different from the original (difference {worst:0.0}); it was not kept",
                                                "Err_CompressChanged", worst.ToString("0.0", CultureInfo.InvariantCulture));
        }
        catch
        {
            File.Delete(output);
            throw;
        }
        long before = new FileInfo(input).Length, after = new FileInfo(output).Length;
        if (after >= before)
        {
            File.Delete(output);
            throw new PageAssemblyException($"{Path.GetFileName(input)} is already as small as Washi can make it", "Err_CompressNoGain", Path.GetFileName(input));
        }
        return new CompressReport(before, after, images, downsampled, streams, duplicates, thumbnails, Math.Round(worst, 2), notes);
    }

    // ------------------------------------------------------------------ where images are shown

    /// <summary>Each image XObject drawn by a page (or a form on it), with the largest size it is shown at (points).</summary>
    private static Dictionary<PdfDictionary, (double W, double H)> ImageUsage(PdfDocument doc)
    {
        var shown = new Dictionary<PdfDictionary, (double W, double H)>(ReferenceEqualityComparer.Instance);
        foreach (var page in doc.Pages)
            Walk(PdfEraser.ReadContents(page), PdfEraser.Resources(page), Mx.Identity, 0);
        return shown;

        void Walk(byte[] bytes, PdfDictionary? resources, Mx start, int depth)
        {
            if (depth > 12) return;
            var content = new ContentStream(bytes);
            var ctm = start;
            var stack = new Stack<Mx>();
            foreach (var op in content.Ops)
            {
                switch (op.Name)
                {
                    case "q": stack.Push(ctm); break;
                    case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                    case "cm" when op.Numbers.Count >= 6:
                        ctm = Mx.From([.. op.Numbers.Take(6).Select(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)]).Then(ctm);
                        break;
                    case "Do" when op.Names.Count > 0:
                        var xobjects = resources is null ? null : Deref(resources.Elements["/XObject"]) as PdfDictionary;
                        if (xobjects is null || Deref(xobjects.Elements["/" + op.Names[^1]]) is not PdfDictionary x) break;
                        if (x.Elements.GetName("/Subtype") == "/Image")
                        {
                            double w = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B), h = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D);
                            var was = shown.GetValueOrDefault(x);
                            shown[x] = (Math.Max(was.W, w), Math.Max(was.H, h));
                        }
                        else if (x.Elements.GetName("/Subtype") == "/Form" && x.Stream is not null)
                        {
                            var fm = Deref(x.Elements["/Matrix"]) is PdfArray ma ? Mx.From([.. ma.Elements.Select(FontMetrics.Num)]) : Mx.Identity;
                            Walk(x.Stream.UnfilteredValue, Deref(x.Elements["/Resources"]) as PdfDictionary ?? resources, fm.Then(ctm), depth + 1);
                        }
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ images

    /// <summary>
    /// Writes the image anew when that makes it clearly smaller. Returns null when it was left alone, true when it was
    /// also scaled down.
    /// </summary>
    private static bool? Recompress(PdfDocument doc, PdfDictionary image, (double W, double H) shown, Level s)
    {
        int w = image.Elements.GetInteger("/Width"), h = image.Elements.GetInteger("/Height");
        if (w < 16 || h < 16 || image.Stream is null || shown.W <= 0 || shown.H <= 0) return null;
        if (image.Elements.GetBoolean("/ImageMask") || image.Elements.ContainsKey("/Decode") || image.Elements["/Mask"] is PdfArray
            || image.Elements.ContainsKey("/SMaskInData") || image.Elements.GetInteger("/BitsPerComponent") is not (8 or 0))
            return null;
        int? n = ImageBlanker.Components(image);
        var cs = Deref(image.Elements["/ColorSpace"]);
        bool indexedOrSpecial = cs is PdfArray a && a.Elements.Count > 0 && Deref(a.Elements[0]) is PdfName { Value: not "/ICCBased" and not "/CalRGB" and not "/CalGray" };
        if (n is not (1 or 3) || indexedOrSpecial) return null;   // CMYK, spot colours, indexed: kept as they are

        var filter = Deref(image.Elements["/Filter"]) switch
        {
            PdfName f => f.Value,
            PdfArray fa when fa.Elements.Count == 1 => (Deref(fa.Elements[0]) as PdfName)?.Value,
            null => "",
            _ => null,
        };
        bool isJpeg = filter is "/DCTDecode" or "/JPXDecode";
        double dpi = Math.Min(w / (shown.W / 72), h / (shown.H / 72));
        bool scale = dpi > s.Above;
        if (!scale && !s.PhotosToJpeg) return null;   // light: only what is far too detailed

        // the pixels (8 bits, gray or RGB)
        byte[] pixels;
        bool gray, device;   // device: the pixels are DeviceGray / DeviceRGB (PDFium converted them)
        if (!isJpeg && ImageBlanker.Raw(image) is { } raw && raw.Length >= w * h * n.Value)
        {
            pixels = raw;
            gray = n == 1;
            device = false;
        }
        else
        {
            try { (pixels, gray) = ImageBlanker.Decoded(image, w, h, false); }
            catch (RedactException) { return null; }
            device = true;
        }
        bool photo = isJpeg || Photo(pixels, gray);
        if (!photo && !scale) return null;   // line art and screenshots stay sharp (Flate), unless far too detailed

        int tw = w, th = h;
        if (scale)
        {
            double k = s.Target / dpi;
            tw = Math.Max(1, (int)Math.Round(w * k));
            th = Math.Max(1, (int)Math.Round(h * k));
        }
        using var bitmap = ToBitmap(pixels, gray, w, h);
        using var sized = tw == w && th == h ? null : bitmap.Resize(new SKImageInfo(tw, th, bitmap.ColorType, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell));
        var final = sized ?? bitmap;
        bool lossless = !photo;
        byte[] data = lossless ? Flate(Pixels(final, gray)) : Jpeg(final, s.Quality);
        if (data.Length >= image.Stream.Value.Length * 0.9) return null;   // not worth it

        image.Stream.Value = data;
        image.Elements["/Length"] = new PdfInteger(data.Length);
        image.Elements["/Filter"] = new PdfName(lossless ? "/FlateDecode" : "/DCTDecode");
        image.Elements.Remove("/DecodeParms");
        image.Elements["/Width"] = new PdfInteger(tw);
        image.Elements["/Height"] = new PdfInteger(th);
        image.Elements["/BitsPerComponent"] = new PdfInteger(8);
        if (device) image.Elements["/ColorSpace"] = new PdfName(gray ? "/DeviceGray" : "/DeviceRGB");   // as PDFium gave them
        return scale;
    }

    /// <summary>Many different colours: a photo or a scan (JPEG suits it). Few: line art, text, a chart (Flate keeps it sharp).</summary>
    private static bool Photo(byte[] pixels, bool gray)
    {
        var seen = new HashSet<int>();
        int step = gray ? 1 : 3, stride = Math.Max(step, pixels.Length / 40000 / step * step);
        for (int i = 0; i + step <= pixels.Length; i += stride)
        {
            int v = gray ? pixels[i] : pixels[i] << 16 | pixels[i + 1] << 8 | pixels[i + 2];
            if (seen.Add(v) && seen.Count > (gray ? 160 : 2048)) return true;
        }
        return false;
    }

    private static SKBitmap ToBitmap(byte[] pixels, bool gray, int w, int h)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, gray ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Opaque));
        var span = bmp.GetPixelSpan();
        var buf = new byte[span.Length];
        if (gray) Buffer.BlockCopy(pixels, 0, buf, 0, Math.Min(buf.Length, w * h));
        else
            for (int i = 0, j = 0; i < w * h; i++, j += 3)
            {
                buf[i * 4] = pixels[j]; buf[i * 4 + 1] = pixels[j + 1]; buf[i * 4 + 2] = pixels[j + 2]; buf[i * 4 + 3] = 255;
            }
        System.Runtime.InteropServices.Marshal.Copy(buf, 0, bmp.GetPixels(), buf.Length);
        return bmp;
    }

    private static byte[] Pixels(SKBitmap bmp, bool gray)
    {
        var src = bmp.GetPixelSpan().ToArray();
        if (gray) return src;
        var rgb = new byte[bmp.Width * bmp.Height * 3];
        for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3) { rgb[j] = src[i]; rgb[j + 1] = src[i + 1]; rgb[j + 2] = src[i + 2]; }
        return rgb;
    }

    private static byte[] Jpeg(SKBitmap bmp, int quality)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    private static byte[] Flate(byte[] data) => Zlib.Compress(data);

    // ------------------------------------------------------------------ streams

    /// <summary>Streams with no filter are Flate-compressed; Flate streams are compressed again at the best level when that helps.</summary>
    private static int CompressStreams(PdfDocument doc)
    {
        int count = 0;
        foreach (var o in doc.Internals.GetAllObjects().OfType<PdfDictionary>())
        {
            if (o.Stream is null || o.Elements.GetName("/Type") is "/Metadata" or "/XRef" or "/ObjStm" || o.Elements.GetName("/Subtype") == "/Image") continue;
            if (o.Elements.ContainsKey("/DecodeParms")) continue;
            var filter = Deref(o.Elements["/Filter"]);
            byte[]? plain = filter switch
            {
                null => o.Stream.Value,
                PdfName { Value: "/FlateDecode" } => PdfSharp.Pdf.Filters.Filtering.Decode(o.Stream.Value, "/FlateDecode"),
                _ => null,
            };
            if (plain is null || plain.Length < 64) continue;
            var packed = Flate(plain);
            if (packed.Length >= o.Stream.Value.Length * 0.97) continue;
            o.Stream.Value = packed;
            o.Elements["/Filter"] = new PdfName("/FlateDecode");
            o.Elements["/Length"] = new PdfInteger(packed.Length);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Streams with the same data and the same dictionary become one object (a logo stored on every page, a font
    /// embedded twice). Repeated until nothing more joins, since joining parts can make their owners identical.
    /// </summary>
    private static int Deduplicate(PdfDocument doc)
    {
        int total = 0;
        for (int round = 0; round < 3; round++)
        {
            var canonical = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
            var replace = new Dictionary<PdfDictionary, PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var o in doc.Internals.GetAllObjects().OfType<PdfDictionary>())
            {
                if (o.Stream is null || o.Reference is null || o.Elements.GetName("/Type") is "/Metadata" or "/XRef" or "/ObjStm") continue;
                var key = Key(o);
                if (key is null) continue;
                if (canonical.TryGetValue(key, out var first)) replace[o] = first;
                else canonical[key] = o;
            }
            if (replace.Count == 0) break;
            total += replace.Count;
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var o in doc.Internals.GetAllObjects().ToList()) Swap(o, 0);

            void Swap(PdfItem item, int depth)
            {
                if (depth > 40 || !seen.Add(item)) return;
                switch (item)
                {
                    case PdfDictionary d:
                        foreach (var k in d.Elements.Keys.ToList())
                        {
                            var v = d.Elements[k];
                            if (v is PdfReference r && r.Value is PdfDictionary target && replace.TryGetValue(target, out var keep)) d.Elements[k] = keep.Reference;
                            else if (v is PdfDictionary or PdfArray) Swap(v, depth + 1);
                        }
                        break;
                    case PdfArray arr:
                        for (int i = 0; i < arr.Elements.Count; i++)
                        {
                            var v = arr.Elements[i];
                            if (v is PdfReference r && r.Value is PdfDictionary target && replace.TryGetValue(target, out var keep)) arr.Elements[i] = keep.Reference!;
                            else if (v is PdfDictionary or PdfArray) Swap(v, depth + 1);
                        }
                        break;
                }
            }
        }
        return total;
    }

    /// <summary>A stream's identity: its data and its dictionary (references by object number), or null when it cannot be told.</summary>
    private static string? Key(PdfDictionary o)
    {
        var sb = new StringBuilder();
        foreach (var k in o.Elements.Keys.Order(StringComparer.Ordinal))
        {
            if (k == "/Length") continue;
            if (!Write(o.Elements[k], sb, 0)) return null;
            sb.Append(k).Append('=');
        }
        var head = Encoding.UTF8.GetBytes(sb.ToString());
        using var sha = SHA256.Create();
        sha.TransformBlock(head, 0, head.Length, null, 0);
        sha.TransformFinalBlock(o.Stream!.Value, 0, o.Stream.Value.Length);
        return Convert.ToHexString(sha.Hash!) + ":" + o.Stream.Value.Length.ToString(CultureInfo.InvariantCulture);

        static bool Write(PdfItem? item, StringBuilder sb, int depth)
        {
            if (depth > 8) return false;
            switch (item)
            {
                case PdfReference r: sb.Append('@').Append(r.ObjectNumber).Append(' ').Append(r.GenerationNumber).Append(';'); return true;
                case PdfName or PdfInteger or PdfReal or PdfBoolean or PdfLongInteger: sb.Append(item).Append(';'); return true;
                case PdfString s: sb.Append('(').Append(s.Value).Append(");"); return true;
                case PdfArray a:
                    sb.Append('[');
                    foreach (var e in a.Elements) if (!Write(e, sb, depth + 1)) return false;
                    sb.Append("];");
                    return true;
                case PdfDictionary { Stream: null } d:
                    sb.Append("<<");
                    foreach (var k in d.Elements.Keys.Order(StringComparer.Ordinal)) { sb.Append(k); if (!Write(d.Elements[k], sb, depth + 1)) return false; }
                    sb.Append(">>;");
                    return true;
                case null: sb.Append("null;"); return true;
                default: return false;
            }
        }
    }

    // ------------------------------------------------------------------ the check

    /// <summary>The largest mean difference between a page of each file, rendered at 72 dpi with its annotations.</summary>
    private static double Difference(string before, string after)
    {
        using var a = PdfiumDocument.Open(before);
        using var b = PdfiumDocument.Open(after);
        if (a.PageCount != b.PageCount) return 255;
        double worst = 0;
        for (int p = 0; p < a.PageCount; p++)
        {
            var ia = a.RenderPage(p, 1.0);
            var ib = b.RenderPage(p, 1.0);
            if (ia.Width != ib.Width || ia.Height != ib.Height) return 255;
            long sum = 0;
            for (int i = 0; i < ia.Pixels.Length; i++) sum += Math.Abs(ia.Pixels[i] - ib.Pixels[i]);
            worst = Math.Max(worst, (double)sum / ia.Pixels.Length);
        }
        return worst;
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
