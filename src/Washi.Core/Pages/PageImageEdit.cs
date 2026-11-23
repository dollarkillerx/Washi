// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>An image the page's content draws. <see cref="Id"/> is "page-n" (1-based: the page, the image's place among the page's images).</summary>
public sealed record PageImage(string Id, int Page, PdfRect Rect, int PixelWidth, int PixelHeight);

/// <summary>An image file to put on a page, in <see cref="Rect"/> (upright points; the image keeps its shape, centred in it).</summary>
public sealed record AddImage(int Page, PdfRect Rect, string File);

/// <param name="Added">Images added, replaced and removed.</param>
public sealed record ImageEditResult(int Added, int Replaced, int Removed);

/// <summary>
/// Images on the page: list the images the page's own content draws, add an image file in a box, replace an image (the
/// new one keeps its shape inside the old one's place) or remove it. Only the operators that draw those images change;
/// the copy is rendered next to the original (nothing may change outside the images' places, and a new image must look
/// like its file) before it is kept. Images inside forms (XObjects) and inline images are not listed.
/// Callers hold <see cref="PdfiumDocument.Gate"/>.
/// </summary>
public static class PdfPageImageEdit
{
    private sealed record Use(int Op, PdfRect Pdf, Mx Ctm, PdfDictionary Image);

    public static IReadOnlyList<PageImage> List(string path)
    {
        using var doc = PdfReader.Open(path, PdfPasswords.For(path) ?? "", PdfDocumentOpenMode.Import);
        var list = new List<PageImage>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            var map = new PdfComments.Upright(doc.Pages[i]);
            int n = 0;
            foreach (var u in Uses(doc.Pages[i]))
                list.Add(new PageImage($"{i + 1}-{++n}", i, map.ToUpright(u.Pdf),
                                       u.Image.Elements.GetInteger("/Width"), u.Image.Elements.GetInteger("/Height")));
        }
        return list;
    }

    /// <summary>The image XObjects the page's own content draws, in order, with where they land in PDF space.</summary>
    private static List<Use> Uses(PdfPage page)
    {
        var uses = new List<Use>();
        var content = new ContentStream(PdfEraser.ReadContents(page));
        var xobjects = Deref(PdfEraser.Resources(page)?.Elements["/XObject"]) as PdfDictionary;
        var ctm = Mx.Identity;
        var stack = new Stack<Mx>();
        for (int k = 0; k < content.Ops.Count; k++)
        {
            var op = content.Ops[k];
            switch (op.Name)
            {
                case "q": stack.Push(ctm); break;
                case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                case "cm" when op.Numbers.Count >= 6:
                    var v = op.Numbers.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    ctm = new Mx(v[^6], v[^5], v[^4], v[^3], v[^2], v[^1]).Then(ctm);
                    break;
                case "Do" when op.Names.Count > 0:
                    if (Deref(xobjects?.Elements["/" + op.Names[^1]]) is PdfDictionary x && x.Elements.GetName("/Subtype") == "/Image")
                    {
                        var b = ctm.Bounds(new Box(0, 0, 1, 1));
                        uses.Add(new Use(k, new PdfRect(b.X0, b.Y0, b.X1, b.Y1), ctm, x));
                    }
                    break;
            }
        }
        return uses;
    }

    public static ImageEditResult Apply(string input, string output, IReadOnlyList<AddImage> add,
                                        IReadOnlyDictionary<string, string>? replace = null, IReadOnlyCollection<string>? remove = null)
    {
        replace ??= new Dictionary<string, string>();
        remove ??= [];
        if (add.Count == 0 && replace.Count == 0 && remove.Count == 0) throw new PageAssemblyException("no image to add, replace or remove", "Err_ImageEditNothing");
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        foreach (var f in add.Select(a => a.File).Concat(replace.Values))
            if (!File.Exists(f)) throw new PageAssemblyException($"{Path.GetFileName(f)} does not exist", "Err_NotFound", Path.GetFileName(f));
        PdfiumDocument.EnsureInit();

        var changed = new List<(int Page, PdfRect Rect)>();             // places that may change (upright)
        var placed = new List<(int Page, PdfRect Rect, string File, SKEncodedOrigin Origin, bool Under)>();   // where new images must show (Under: the page may draw over it)
        using (var doc = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            var all = List(doc);
            foreach (var id in replace.Keys.Concat(remove))
                if (all.All(i => i.Id != id)) throw new PageAssemblyException($"there is no image {id}", "Err_ImageId", id);
            foreach (var a in add)
                if (a.Page < 0 || a.Page >= doc.PageCount)
                    throw new PageAssemblyException($"page {a.Page + 1} is outside 1-{doc.PageCount}", "Err_RangeOutside", (a.Page + 1).ToString(CultureInfo.InvariantCulture), doc.PageCount);

            for (int p = 0; p < doc.PageCount; p++)
            {
                var page = doc.Pages[p];
                var map = new PdfComments.Upright(page);
                var mine = all.Where(i => i.Page == p).ToList();
                var adds = add.Where(a => a.Page == p).ToList();
                bool touches = mine.Any(i => replace.ContainsKey(i.Id) || remove.Contains(i.Id));
                if (!touches && adds.Count == 0) continue;

                // our own copy of the resources, for the new images
                var resources = PdfEraser.Resources(page) is { } pr ? (PdfDictionary)pr.Clone() : new PdfDictionary(doc);
                page.Elements["/Resources"] = resources;
                var xobjects = Deref(resources.Elements["/XObject"]) is PdfDictionary xd ? (PdfDictionary)xd.Clone() : new PdfDictionary(doc);
                resources.Elements["/XObject"] = xobjects;
                string Key(PdfDictionary image)
                {
                    int n = 1;
                    string key;
                    do key = "/WashiImg" + (n++).ToString(CultureInfo.InvariantCulture); while (xobjects.Elements.ContainsKey(key));
                    xobjects.Elements[key] = image.Reference!;
                    return key;
                }

                if (touches)
                {
                    var bytes = PdfEraser.ReadContents(page);
                    var content = new ContentStream(bytes);
                    var uses = Uses(page);
                    var edits = new List<(int Start, int End, string With)>();
                    for (int k = 0; k < uses.Count && k < mine.Count; k++)
                    {
                        var id = mine[k].Id;
                        var op = content.Ops[uses[k].Op];
                        if (remove.Contains(id))
                        {
                            edits.Add((op.Start, op.End, ""));
                            changed.Add((p, mine[k].Rect));
                        }
                        else if (replace.TryGetValue(id, out var file))
                        {
                            // the new image keeps its shape, centred in the old one's place (in the old image's own space)
                            var image = PdfFromImages.Load(doc, file);
                            var ctm = uses[k].Ctm;
                            double w = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B), h = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D);
                            bool turned = image.Origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
                            double ratio = (turned ? image.Height : image.Width) / (double)(turned ? image.Width : image.Height);
                            double nw = w / h > ratio ? h * ratio : w, nh = w / h > ratio ? h : w / ratio;
                            double sx = nw / w, sy = nh / h;
                            var (a, b, c, d, e, f) = PdfFromImages.Placement(image.Origin, (1 - sx) / 2, (1 - sy) / 2, sx, sy);
                            edits.Add((op.Start, op.End, string.Create(CultureInfo.InvariantCulture,
                                $"q {N(a)} {N(b)} {N(c)} {N(d)} {N(e)} {N(f)} cm {Key(image.Dict)} Do Q")));
                            changed.Add((p, mine[k].Rect));
                            var inner = new Mx(sx, 0, 0, sy, (1 - sx) / 2, (1 - sy) / 2).Then(ctm).Bounds(new Box(0, 0, 1, 1));
                            placed.Add((p, map.ToUpright(new PdfRect(inner.X0, inner.Y0, inner.X1, inner.Y1)), file, image.Origin, true));
                        }
                    }
                    PdfEraser.WriteContents(doc, page, ContentRedactor.Apply(bytes, edits));
                }

                var front = new StringBuilder();
                var (_, height) = PdfStamper.UprightSize(page);
                var toPdf = new Mx(1, 0, 0, -1, 0, height).Then(PdfRedactor.Upright(page));   // upright, y up → PDF space
                foreach (var a in adds)
                {
                    var image = PdfFromImages.Load(doc, a.File);
                    bool turned = image.Origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
                    double iw = turned ? image.Height : image.Width, ih = turned ? image.Width : image.Height;
                    double scale = Math.Min(a.Rect.Width / iw, a.Rect.Height / ih);
                    double w = iw * scale, h = ih * scale;
                    var box = new PdfRect(a.Rect.X0 + (a.Rect.Width - w) / 2, a.Rect.Y0 + (a.Rect.Height - h) / 2, 0, 0);
                    box = box with { X1 = box.X0 + w, Y1 = box.Y0 + h };
                    // drawn upright (y up) and turned into PDF space with the page
                    var (ua, ub, uc, ud, ue, uf) = PdfFromImages.Placement(image.Origin, box.X0, height - box.Y1, w, h);
                    front.Append(CultureInfo.InvariantCulture,
                        $"q {N(toPdf.A)} {N(toPdf.B)} {N(toPdf.C)} {N(toPdf.D)} {N(toPdf.E)} {N(toPdf.F)} cm {N(ua)} {N(ub)} {N(uc)} {N(ud)} {N(ue)} {N(uf)} cm {Key(image.Dict)} Do Q\n");
                    changed.Add((p, box));
                    placed.Add((p, box, a.File, image.Origin, false));
                }
                if (front.Length > 0) PdfStamper.AddStreams(doc, page, "", front.ToString());
            }
            doc.Save(output);
        }

        try { Verify(input, output, changed, placed); }
        catch
        {
            File.Delete(output);
            throw;
        }
        return new ImageEditResult(add.Count, replace.Count, remove.Count);
    }

    private static List<PageImage> List(PdfDocument doc)
    {
        var list = new List<PageImage>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            var map = new PdfComments.Upright(doc.Pages[i]);
            int n = 0;
            foreach (var u in Uses(doc.Pages[i]))
                list.Add(new PageImage($"{i + 1}-{++n}", i, map.ToUpright(u.Pdf), u.Image.Elements.GetInteger("/Width"), u.Image.Elements.GetInteger("/Height")));
        }
        return list;
    }

    /// <summary>Nothing changed outside the images' places, and each new image looks like its file.</summary>
    private static void Verify(string before, string after, List<(int Page, PdfRect Rect)> changed, List<(int Page, PdfRect Rect, string File, SKEncodedOrigin Origin, bool Under)> placed)
    {
        using var a = PdfiumDocument.Open(before);
        using var b = PdfiumDocument.Open(after);
        const double scale = 1.5;
        foreach (var page in changed.Select(c => c.Page).Distinct().Take(20))
        {
            var ia = a.RenderPage(page, scale);
            var ib = b.RenderPage(page, scale);
            var ok = new bool[ia.Width * ia.Height];
            foreach (var r in changed.Where(c => c.Page == page).Select(c => c.Rect))
                for (int y = Math.Max(0, (int)(r.Y0 * scale) - 2); y <= Math.Min(ia.Height - 1, (int)(r.Y1 * scale) + 2); y++)
                    for (int x = Math.Max(0, (int)(r.X0 * scale) - 2); x <= Math.Min(ia.Width - 1, (int)(r.X1 * scale) + 2); x++)
                        ok[y * ia.Width + x] = true;
            int diff = 0;
            for (int i = 0, q = 0; i < ok.Length; i++, q += 3)
                if (!ok[i] && Math.Abs(ia.Pixels[q] - ib.Pixels[q]) + Math.Abs(ia.Pixels[q + 1] - ib.Pixels[q + 1]) + Math.Abs(ia.Pixels[q + 2] - ib.Pixels[q + 2]) > 24)
                    diff++;
            if (diff > 0)
                throw new PageAssemblyException($"page {page + 1} changed outside the images ({diff} pixels); the copy was not kept", "Err_ImageEditChanged", page + 1);

            // each new image, cell by cell (a see-through image is not checked)
            foreach (var (_, rect, file, origin, under) in placed.Where(x => x.Page == page))
            {
                using var src = SKBitmap.Decode(file);
                if (src is null || src.AlphaType != SKAlphaType.Opaque && src.Pixels.Any(c => c.Alpha < 250)) continue;
                using var upright = PdfFromImages.Upright(src, origin);
                const int grid = 8;
                var cells = PdfFromImages.Averages(upright, grid);
                int bad = 0;
                for (int gy = 0; gy < grid; gy++)
                    for (int gx = 0; gx < grid; gx++)
                    {
                        int x0 = (int)((rect.X0 + rect.Width * gx / grid) * scale), x1 = (int)((rect.X0 + rect.Width * (gx + 1) / grid) * scale);
                        int y0 = (int)((rect.Y0 + rect.Height * gy / grid) * scale), y1 = (int)((rect.Y0 + rect.Height * (gy + 1) / grid) * scale);
                        double r = 0, g = 0, bl = 0, n = 0;
                        for (int y = Math.Max(0, y0); y < Math.Min(ib.Height, Math.Max(y1, y0 + 1)); y++)
                            for (int x = Math.Max(0, x0); x < Math.Min(ib.Width, Math.Max(x1, x0 + 1)); x++)
                            {
                                int q = (y * ib.Width + x) * 3;
                                r += ib.Pixels[q]; g += ib.Pixels[q + 1]; bl += ib.Pixels[q + 2]; n++;
                            }
                        if (n == 0) continue;
                        var c = cells[gy * grid + gx];
                        if ((Math.Abs(r / n - c.R) + Math.Abs(g / n - c.G) + Math.Abs(bl / n - c.B)) / 3 > 40) bad++;
                    }
                // a replaced image stays where it was in the drawing order: text or lines over it are no fault
                if (bad > grid * grid * (under ? 0.35 : 0.1))
                    throw new PageAssemblyException($"{Path.GetFileName(file)} did not show on page {page + 1} as it should; the copy was not kept",
                                                    "Err_ImageEditCheck", Path.GetFileName(file), page + 1);
            }
        }
    }

    private static string N(double v) => Math.Round(v, 5).ToString("0.#####", CultureInfo.InvariantCulture);

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
