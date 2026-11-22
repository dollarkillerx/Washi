// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Washi.Core.Pages;

/// <summary>
/// A hand-drawn signature as vector strokes. Points are relative to the drawing's own box: x and y run from 0 to 1,
/// top-left origin, and <see cref="Aspect"/> (width / height) keeps its shape when it is placed.
/// </summary>
public sealed record Ink(IReadOnlyList<IReadOnlyList<(double X, double Y)>> Strokes, double Aspect)
{
    public bool IsEmpty => Strokes.All(s => s.Count == 0);

    /// <summary>
    /// From strokes in any coordinates (pixels of a drawing pad): trimmed to the ink's bounding box and normalised.
    /// A single dot gets a tiny box, so it still shows.
    /// </summary>
    public static Ink From(IEnumerable<IReadOnlyList<(double X, double Y)>> strokes)
    {
        var list = strokes.Where(s => s.Count > 0).ToList();
        if (list.Count == 0) return new([], 1);
        double x0 = list.Min(s => s.Min(p => p.X)), x1 = list.Max(s => s.Max(p => p.X));
        double y0 = list.Min(s => s.Min(p => p.Y)), y1 = list.Max(s => s.Max(p => p.Y));
        double w = Math.Max(x1 - x0, 1), h = Math.Max(y1 - y0, 1);
        return new([.. list.Select(s => (IReadOnlyList<(double, double)>)[.. s.Select(p => ((p.X - x0) / w, (p.Y - y0) / h))])], w / h);
    }

    /// <summary>"aspect;x,y x,y|x,y ..." with 4 decimals: small enough to keep in a settings file.</summary>
    public string Serialize()
    {
        var inv = CultureInfo.InvariantCulture;
        return Aspect.ToString("0.####", inv) + ";" + string.Join("|", Strokes.Select(s =>
            string.Join(" ", s.Select(p => p.X.ToString("0.####", inv) + "," + p.Y.ToString("0.####", inv)))));
    }

    public static Ink? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Split(';', 2) is not [var a, var rest]) return null;
        var inv = CultureInfo.InvariantCulture;
        if (!double.TryParse(a, NumberStyles.Float, inv, out var aspect) || aspect <= 0) return null;
        var strokes = new List<IReadOnlyList<(double, double)>>();
        foreach (var s in rest.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var pts = new List<(double, double)>();
            foreach (var p in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (p.Split(',') is [var x, var y] && double.TryParse(x, NumberStyles.Float, inv, out var px)
                    && double.TryParse(y, NumberStyles.Float, inv, out var py))
                    pts.Add((Math.Clamp(px, 0, 1), Math.Clamp(py, 0, 1)));
            if (pts.Count > 0) strokes.Add(pts);
        }
        return strokes.Count == 0 ? null : new Ink(strokes, aspect);
    }
}

/// <summary>A signature placed on a page: <see cref="Rect"/> in points on the upright page, top-left origin. The ink keeps
/// its shape and is centred in the rect.</summary>
public sealed record SignatureStamp(int Page, PdfRect Rect, Ink Ink, string? Color = null);

/// <summary>
/// Fill &amp; Sign: draws hand-drawn signatures into the page content as smooth vector curves (they print sharply and
/// cannot be moved or removed like an annotation). The page's own content is wrapped in q/Q first, so whatever graphics
/// state it leaves behind cannot shift or recolour the signature. Form fields are left as they are.
/// </summary>
public static class PdfSigner
{
    /// <returns>The number of signatures drawn.</returns>
    public static int Sign(string input, string output, IReadOnlyList<SignatureStamp> stamps)
    {
        if (stamps.Count == 0) throw new PageAssemblyException("no signature to place", "Err_NoSignature");
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (var s in stamps)
            if (s.Page < 0 || s.Page >= doc.PageCount)
                throw new PageAssemblyException($"page {s.Page + 1} is outside 1-{doc.PageCount}", "Err_RangeOutside",
                                                (s.Page + 1).ToString(CultureInfo.InvariantCulture), doc.PageCount);
        foreach (var group in stamps.GroupBy(s => s.Page))
        {
            var page = doc.Pages[group.Key];
            var sb = new StringBuilder("\nq ").Append(UprightMatrix(page)).Append(" cm\n");
            foreach (var s in group) Draw(sb, s);
            sb.Append("Q\n");
            Wrap(page);
            SetStream(page.Contents.AppendContent(), Encoding.ASCII.GetBytes(sb.ToString()));
        }
        doc.Options.CompressContentStreams = true;
        doc.Save(output);
        return stamps.Count;
    }

    /// <summary>The page's content between q and Q, so the signature starts from a clean graphics state.</summary>
    private static void Wrap(PdfPage page)
    {
        SetStream(page.Contents.PrependContent(), "q\n"u8.ToArray());
        SetStream(page.Contents.AppendContent(), "\nQ\n"u8.ToArray());
    }

    private static void SetStream(PdfDictionary content, byte[] bytes)
    {
        if (content.Stream is null) content.CreateStream(bytes);
        else content.Stream.Value = bytes;
    }

    /// <summary>
    /// Maps the upright page (top-down points, as the user sees it) to PDF space: the inverse of the mapping PDFium and
    /// <see cref="Forms.AcroFormFields"/> use for widget rects, for every /Rotate.
    /// </summary>
    private static string UprightMatrix(PdfPage page)
    {
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        double a = box.X1, b = box.Y1, c = box.X2, d = box.Y2;
        return (((page.Rotate % 360) + 360) % 360) switch
        {
            90 => $"0 1 1 0 {N(a)} {N(b)}",
            180 => $"-1 0 0 1 {N(c)} {N(b)}",
            270 => $"0 -1 -1 0 {N(c)} {N(d)}",
            _ => $"1 0 0 -1 {N(a)} {N(d)}",
        };
    }

    internal static void Draw(StringBuilder sb, SignatureStamp s)
    {
        // the ink keeps its shape: as large as fits the rect, centred
        var r = s.Rect;
        double h = Math.Min(r.Height, r.Width / Math.Max(s.Ink.Aspect, 0.01)), w = h * s.Ink.Aspect;
        double ox = r.X0 + (r.Width - w) / 2, oy = r.Y0 + (r.Height - h) / 2;
        double lw = Math.Clamp(h * 0.035, 0.6, 2.4);
        sb.Append(Forms.FormField.ColorOps(s.Color).Replace(" rg", " RG").Replace(" g", " G"))
          .Append(' ').Append(N(lw)).Append(" w 1 J 1 j\n");
        foreach (var stroke in s.Ink.Strokes)
        {
            var p = stroke.Select(q => (X: ox + q.X * w, Y: oy + q.Y * h)).ToList();
            if (p.Count == 0) continue;
            sb.Append(N(p[0].X)).Append(' ').Append(N(p[0].Y)).Append(" m\n");
            if (p.Count == 1)
            {
                sb.Append(N(p[0].X + 0.01)).Append(' ').Append(N(p[0].Y)).Append(" l\n");   // a dot: round caps draw it
            }
            else
            {
                // through the midpoints with each point as the control: a smooth line, no corners at the samples
                var prev = p[0];
                for (int i = 1; i < p.Count - 1; i++)
                {
                    var mid = ((p[i].X + p[i + 1].X) / 2, (p[i].Y + p[i + 1].Y) / 2);
                    var c1 = (prev.X + 2.0 / 3 * (p[i].X - prev.X), prev.Y + 2.0 / 3 * (p[i].Y - prev.Y));
                    var c2 = (mid.Item1 + 2.0 / 3 * (p[i].X - mid.Item1), mid.Item2 + 2.0 / 3 * (p[i].Y - mid.Item2));
                    sb.Append(N(c1.Item1)).Append(' ').Append(N(c1.Item2)).Append(' ')
                      .Append(N(c2.Item1)).Append(' ').Append(N(c2.Item2)).Append(' ')
                      .Append(N(mid.Item1)).Append(' ').Append(N(mid.Item2)).Append(" c\n");
                    prev = mid;
                }
                sb.Append(N(p[^1].X)).Append(' ').Append(N(p[^1].Y)).Append(" l\n");
            }
            sb.Append("S\n");
        }
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
