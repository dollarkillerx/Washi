// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Forms;

namespace Washi.Core.Pages;

public enum CommentKind { Highlight, Underline, StrikeOut, Note, TextBox, Pen, Box }

/// <summary>
/// A comment to add, on the upright page (top-down points, as the user sees it).
/// </summary>
/// <param name="Rect">Note: where its icon goes (top-left corner is used). Text box and box: the box. Markup: the
/// union of <paramref name="Lines"/>. Pen: the ink's bounds.</param>
/// <param name="Lines">Highlight / underline / strike-out: one rect per line of marked text.</param>
/// <param name="Strokes">Pen: the strokes in page points.</param>
/// <param name="Color">"#RRGGBB"; empty = the kind's usual colour.</param>
public sealed record CommentSpec(
    int Page,
    CommentKind Kind,
    PdfRect Rect,
    string Text = "",
    string Color = "",
    IReadOnlyList<PdfRect>? Lines = null,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>>? Strokes = null,
    string Author = "");

/// <summary>A comment (annotation) already in a PDF. Widgets (form fields) and links are not comments.</summary>
/// <param name="Id">Names the comment for <see cref="PdfComments.Change"/>: its /NM, or its page and place on the page.</param>
/// <param name="Color">"#RRGGBB" (empty when it has none).</param>
/// <param name="ReplyTo">The <see cref="Id"/> of the comment this one answers.</param>
/// <param name="Redrawable">Washi can redraw it (highlight, underline, strike-out, note, text box, pen, box), so its
/// colour can change and, except for text markup, it can be moved.</param>
public sealed record CommentInfo(int Page, string Type, PdfRect Rect, string Text, string Author, string Modified,
                                 string Id = "", string Color = "", string? ReplyTo = null, bool Redrawable = false)
{
    public bool Movable => Redrawable && Type is not ("Highlight" or "Underline" or "StrikeOut");
    public bool Resizable => Redrawable && Type is "FreeText" or "Square";
}

/// <summary>
/// Adds review comments as standard PDF annotations, each with its own appearance so it looks the same in every viewer:
/// highlight / underline / strike-out (text markup with quad points), sticky notes, text boxes (FreeText), pen (Ink) and
/// boxes (Square). Lists the comments a PDF already has.
/// </summary>
public static partial class PdfComments
{
    private const int AnnotPrint = 4;

    public static string DefaultColor(CommentKind k) => k switch
    {
        CommentKind.Highlight => "#FFE14D",
        CommentKind.Underline or CommentKind.StrikeOut => "#DA1E28",
        CommentKind.Note => "#F1C21B",
        CommentKind.TextBox => "#161616",
        _ => "#0F62FE",
    };

    /// <returns>The number of comments written.</returns>
    public static int Add(string input, string output, IReadOnlyList<CommentSpec> comments)
    {
        if (comments.Count == 0) throw new PageAssemblyException("no comment to add", "Err_NoComment");
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (var c in comments)
            if (c.Page < 0 || c.Page >= doc.PageCount)
                throw new PageAssemblyException($"page {c.Page + 1} is outside 1-{doc.PageCount}", "Err_RangeOutside",
                                                (c.Page + 1).ToString(CultureInfo.InvariantCulture), doc.PageCount);
        var fonts = new Dictionary<FormScript, PdfDictionary>();
        string now = "D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";
        foreach (var c in comments)
        {
            var page = doc.Pages[c.Page];
            var map = new Upright(page);
            var a = new PdfDictionary(doc);
            doc.Internals.AddObject(a);
            a.Elements.SetName("/Type", "/Annot");
            a.Elements.SetInteger("/F", AnnotPrint);
            a.Elements.SetReference("/P", page);
            a.Elements["/NM"] = new PdfString("washi-" + Guid.NewGuid().ToString("N"));
            a.Elements["/M"] = new PdfString(now);
            if (c.Author.Length > 0) a.Elements["/T"] = new PdfString(c.Author, PdfStringEncoding.Unicode);
            if (c.Text.Length > 0) a.Elements["/Contents"] = new PdfString(c.Text, PdfStringEncoding.Unicode);
            var rgb = Rgb(c.Color.Length > 0 ? c.Color : DefaultColor(c.Kind));
            a.Elements["/C"] = Nums(doc, rgb.R, rgb.G, rgb.B);

            switch (c.Kind)
            {
                case CommentKind.Highlight or CommentKind.Underline or CommentKind.StrikeOut:
                    Markup(doc, a, c, map, rgb);
                    break;
                case CommentKind.Note:
                    Note(doc, a, c, map, rgb);
                    break;
                case CommentKind.TextBox:
                    TextBox(doc, a, c, map, rgb, fonts);
                    break;
                case CommentKind.Pen:
                    Pen(doc, a, c, map, rgb);
                    break;
                default:
                    Box(doc, a, c, map, rgb);
                    break;
            }
            AcroFormWriter.GetOrCreateArray(doc, page, "/Annots").Elements.Add(a.Reference!);
        }
        doc.Options.CompressContentStreams = true;
        doc.Save(output);
        return comments.Count;
    }

    // ------------------------------------------------------------------ kinds
    private static void Markup(PdfDocument doc, PdfDictionary a, CommentSpec c, Upright map, (double R, double G, double B) rgb)
    {
        var lines = c.Lines is { Count: > 0 } l ? l : [c.Rect];
        a.Elements.SetName("/Subtype", c.Kind switch { CommentKind.Highlight => "/Highlight", CommentKind.Underline => "/Underline", _ => "/StrikeOut" });
        // QuadPoints per line, in the order viewers expect: upper-left, upper-right, lower-left, lower-right as seen
        var quads = new List<double>();
        var ap = new StringBuilder();
        foreach (var r in lines)
        {
            var (ul, ur, ll, lr) = (map.Pdf(r.X0, r.Y0), map.Pdf(r.X1, r.Y0), map.Pdf(r.X0, r.Y1), map.Pdf(r.X1, r.Y1));
            quads.AddRange([ul.X, ul.Y, ur.X, ur.Y, ll.X, ll.Y, lr.X, lr.Y]);
            if (c.Kind == CommentKind.Highlight)
                ap.Append(Path([ul, ur, lr, ll], close: true)).Append("f\n");
            else
            {
                // underline at the foot of the line, strike-out through its middle (as seen on the upright page)
                double t = c.Kind == CommentKind.Underline ? 0.92 : 0.55, lw = Math.Max(0.5, r.Height * 0.07);
                var p0 = map.Pdf(r.X0, r.Y0 + r.Height * t);
                var p1 = map.Pdf(r.X1, r.Y0 + r.Height * t);
                ap.Append(N(lw)).Append(" w ").Append(Path([p0, p1], close: false)).Append("S\n");
            }
        }
        a.Elements["/QuadPoints"] = Nums(doc, [.. quads]);
        var bounds = Union(lines);
        var rect = map.PdfRect(bounds);
        a.Elements["/Rect"] = Nums(doc, rect.X0, rect.Y0, rect.X1, rect.Y1);
        string color = c.Kind == CommentKind.Highlight ? $"{N(rgb.R)} {N(rgb.G)} {N(rgb.B)} rg" : $"{N(rgb.R)} {N(rgb.G)} {N(rgb.B)} RG";
        // a highlight multiplies, so the text under it stays readable
        var gs = c.Kind == CommentKind.Highlight ? Multiply(doc) : null;
        SetAppearance(doc, a, rect, $"q {(gs is null ? "" : "/GS0 gs ")}{color}\n{ap}Q", gs is null ? null : ("/ExtGState", "/GS0", gs));
    }

    private static void Note(PdfDocument doc, PdfDictionary a, CommentSpec c, Upright map, (double R, double G, double B) rgb)
    {
        const double S = 18;
        a.Elements.SetName("/Subtype", "/Text");
        a.Elements.SetName("/Name", "/Comment");
        a.Elements.SetBoolean("/Open", false);
        var rect = map.PdfRect(new PdfRect(c.Rect.X0, c.Rect.Y0, c.Rect.X0 + S, c.Rect.Y0 + S));
        a.Elements["/Rect"] = Nums(doc, rect.X0, rect.Y0, rect.X1, rect.Y1);
        // a speech bubble with two lines of "text", drawn upright
        string icon = $"q {N(rgb.R)} {N(rgb.G)} {N(rgb.B)} rg 0.2 0.2 0.2 RG 0.8 w\n" +
                      "1.5 5 m 1.5 16.5 l 16.5 16.5 l 16.5 5 l 8 5 l 4.5 1.5 l 5 5 l h B\n" +
                      "4.5 12.5 m 13.5 12.5 l S 4.5 9 m 11.5 9 l S Q";
        SetUprightAppearance(doc, a, map, S, S, icon, null);
    }

    private static void TextBox(PdfDocument doc, PdfDictionary a, CommentSpec c, Upright map, (double R, double G, double B) rgb,
                                Dictionary<FormScript, PdfDictionary> fonts)
    {
        a.Elements.SetName("/Subtype", "/FreeText");
        a.Elements.Remove("/C");                                   // /C is a FreeText's background: keep it clear
        var rect = map.PdfRect(c.Rect);
        a.Elements["/Rect"] = Nums(doc, rect.X0, rect.Y0, rect.X1, rect.Y1);
        var script = FormScripts.Detect(c.Text) ?? FormScript.Latin;
        if (!fonts.TryGetValue(script, out var font)) fonts[script] = font = AcroFormWriter.TextFont(doc, script);
        double w = c.Rect.Width, h = c.Rect.Height, size = Math.Clamp(Math.Min(12, h - 4), 6, 12);
        string color = $"{N(rgb.R)} {N(rgb.G)} {N(rgb.B)} rg";
        a.Elements["/DA"] = new PdfString($"/Helv {N(size)} Tf {color}");
        var body = new StringBuilder($"q BT /F1 {N(size)} Tf {color}\n");
        double y = h - 2 - size;
        foreach (var line in Wrap(c.Text, size, w - 4, script))
        {
            if (y < -size * 0.2) break;
            double x = 2;
            foreach (var r in line.EnumerateRunes())
            {
                var ch = r.ToString();
                body.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {N(x)} {N(y)} Tm {Hex(ch, script)} Tj\n");
                x += Em(r, script) * size;
            }
            y -= size * 1.2;
        }
        body.Append("ET Q");
        SetUprightAppearance(doc, a, map, w, h, body.ToString(), ("/Font", "/F1", font));
    }

    private static void Pen(PdfDocument doc, PdfDictionary a, CommentSpec c, Upright map, (double R, double G, double B) rgb)
    {
        a.Elements.SetName("/Subtype", "/Ink");
        var strokes = (c.Strokes ?? []).Where(s => s.Count > 0).ToList();
        const double W = 1.5;
        var bs = new PdfDictionary(doc);
        bs.Elements.SetReal("/W", W);
        a.Elements["/BS"] = bs;
        var ink = new PdfArray(doc);
        var ap = new StringBuilder($"q {N(rgb.R)} {N(rgb.G)} {N(rgb.B)} RG {N(W)} w 1 J 1 j\n");
        foreach (var s in strokes)
        {
            var pts = s.Select(p => map.Pdf(p.X, p.Y)).ToList();
            ink.Elements.Add(Nums(doc, [.. pts.SelectMany(p => new[] { p.X, p.Y })]));
            ap.Append(Path(pts.Count == 1 ? [pts[0], (pts[0].X + 0.01, pts[0].Y)] : pts, close: false)).Append("S\n");
        }
        ap.Append('Q');
        a.Elements["/InkList"] = ink;
        var all = strokes.SelectMany(s => s).ToList();
        var bounds = all.Count == 0 ? c.Rect : new PdfRect(all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y));
        var rect = map.PdfRect(new PdfRect(bounds.X0 - W, bounds.Y0 - W, bounds.X1 + W, bounds.Y1 + W));
        a.Elements["/Rect"] = Nums(doc, rect.X0, rect.Y0, rect.X1, rect.Y1);
        SetAppearance(doc, a, rect, ap.ToString(), null);
    }

    private static void Box(PdfDocument doc, PdfDictionary a, CommentSpec c, Upright map, (double R, double G, double B) rgb)
    {
        a.Elements.SetName("/Subtype", "/Square");
        const double W = 1.5;
        var bs = new PdfDictionary(doc);
        bs.Elements.SetReal("/W", W);
        a.Elements["/BS"] = bs;
        var rect = map.PdfRect(c.Rect);
        a.Elements["/Rect"] = Nums(doc, rect.X0, rect.Y0, rect.X1, rect.Y1);
        double h = W / 2;
        SetAppearance(doc, a, rect,
            $"q {N(rgb.R)} {N(rgb.G)} {N(rgb.B)} RG {N(W)} w {N(rect.X0 + h)} {N(rect.Y0 + h)} {N(rect.Width - W)} {N(rect.Height - W)} re S Q", null);
    }

    // ------------------------------------------------------------------ list
    private static readonly HashSet<string> NotComments = ["/Widget", "/Link", "/Popup"];

    public static IReadOnlyList<CommentInfo> List(string path)
    {
        using var doc = PdfReader.Open(path, PdfPasswords.For(path) ?? "", PdfDocumentOpenMode.Import);
        var ids = Ids(doc);
        var result = new List<CommentInfo>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            var map = new Upright(page);
            if (page.Elements.GetArray("/Annots") is not { } annots) continue;
            foreach (var item in annots.Elements)
            {
                if ((item is PdfReference r ? r.Value : item) is not PdfDictionary a) continue;
                var type = a.Elements.GetName("/Subtype");
                if (type.Length == 0 || NotComments.Contains(type)) continue;
                var rc = a.Elements.GetArray("/Rect");
                var rect = rc is { Elements.Count: >= 4 }
                    ? map.ToUpright(new PdfRect(rc.Elements.GetReal(0), rc.Elements.GetReal(1), rc.Elements.GetReal(2), rc.Elements.GetReal(3)))
                    : default;
                string? replyTo = Deref(a.Elements["/IRT"]) is PdfDictionary irt && ids.TryGetValue(irt, out var pid) ? pid : null;
                result.Add(new CommentInfo(i, type.TrimStart('/'), rect, a.Elements.GetString("/Contents"), a.Elements.GetString("/T"),
                                           a.Elements.GetString("/M"), ids[a], Hex(a), replyTo, Redraws.Contains(type)));
            }
        }
        return result;
    }

    /// <summary>Annotation types Washi draws itself (and so can recolour and move).</summary>
    private static readonly HashSet<string> Redraws = ["/Highlight", "/Underline", "/StrikeOut", "/Text", "/FreeText", "/Ink", "/Square"];

    /// <summary>
    /// Each comment's id: its /NM when no other annotation has the same one, otherwise "p{page}-{n}" (its place in the
    /// page's /Annots, both 1-based).
    /// </summary>
    private static Dictionary<PdfDictionary, string> Ids(PdfDocument doc)
    {
        var all = new List<(PdfDictionary A, int Page, int Index)>();
        for (int i = 0; i < doc.PageCount; i++)
            if (doc.Pages[i].Elements.GetArray("/Annots") is { } annots)
                for (int j = 0; j < annots.Elements.Count; j++)
                    if (Deref(annots.Elements[j]) is PdfDictionary a) all.Add((a, i, j));
        var nmCount = all.GroupBy(x => x.A.Elements.GetString("/NM")).ToDictionary(g => g.Key, g => g.Count());
        var ids = new Dictionary<PdfDictionary, string>(ReferenceEqualityComparer.Instance);
        foreach (var (a, page, index) in all)
        {
            var nm = a.Elements.GetString("/NM");
            ids[a] = nm.Length > 0 && nmCount[nm] == 1 ? nm : $"p{page + 1}-{index + 1}";
        }
        return ids;
    }

    /// <summary>The annotation's colour (/C) as #RRGGBB; a text box's is the colour of its text.</summary>
    private static string Hex(PdfDictionary a)
    {
        double[]? c = null;
        if (a.Elements.GetName("/Subtype") == "/FreeText")
        {
            var m = System.Text.RegularExpressions.Regex.Match(a.Elements.GetString("/DA"), @"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+rg");
            if (m.Success) c = [.. m.Groups.Values.Skip(1).Select(g => double.Parse(g.Value, CultureInfo.InvariantCulture))];
            else c = [0, 0, 0];
        }
        else if (a.Elements.GetArray("/C") is { Elements.Count: 3 } arr) c = [arr.Elements.GetReal(0), arr.Elements.GetReal(1), arr.Elements.GetReal(2)];
        else if (a.Elements.GetArray("/C") is { Elements.Count: 1 } g) c = [g.Elements.GetReal(0), g.Elements.GetReal(0), g.Elements.GetReal(0)];
        return c is null ? "" : "#" + string.Concat(c.Select(v => ((int)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;

    // ------------------------------------------------------------------ geometry
    /// <summary>Upright page (top-down points) ↔ PDF space, for the page's /Rotate (as PDFium maps them).</summary>
    internal sealed class Upright
    {
        private readonly double _a, _b, _c, _d;
        public int Rotate { get; }

        public Upright(PdfPage page)
        {
            var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
            (_a, _b, _c, _d) = (box.X1, box.Y1, box.X2, box.Y2);
            Rotate = ((page.Rotate % 360) + 360) % 360;
        }

        public (double X, double Y) Pdf(double u, double v) => Rotate switch
        {
            90 => (_a + v, _b + u),
            180 => (_c - u, _b + v),
            270 => (_c - v, _d - u),
            _ => (_a + u, _d - v),
        };

        public PdfRect PdfRect(PdfRect r)
        {
            var (x0, y0) = Pdf(r.X0, r.Y0);
            var (x1, y1) = Pdf(r.X1, r.Y1);
            return new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
        }

        /// <summary>PDF-space rect back to the upright page.</summary>
        public PdfRect ToUpright(PdfRect p)
        {
            double x0 = Math.Min(p.X0, p.X1), x1 = Math.Max(p.X0, p.X1), y0 = Math.Min(p.Y0, p.Y1), y1 = Math.Max(p.Y0, p.Y1);
            return Rotate switch
            {
                90 => new(y0 - _b, x0 - _a, y1 - _b, x1 - _a),
                180 => new(_c - x1, y0 - _b, _c - x0, y1 - _b),
                270 => new(_d - y1, _c - x1, _d - y0, _c - x0),
                _ => new(x0 - _a, _d - y1, x1 - _a, _d - y0),
            };
        }

        /// <summary>Turns an upright appearance (y up, as the user reads it) into PDF space: counter-clockwise by /Rotate.</summary>
        public string Matrix => Rotate switch { 90 => "0 1 -1 0 0 0", 180 => "-1 0 0 -1 0 0", 270 => "0 -1 1 0 0 0", _ => "1 0 0 1 0 0" };
    }

    private static PdfRect Union(IReadOnlyList<PdfRect> rs) =>
        new(rs.Min(r => r.X0), rs.Min(r => r.Y0), rs.Max(r => r.X1), rs.Max(r => r.Y1));

    private static string Path(IReadOnlyList<(double X, double Y)> pts, bool close)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pts.Count; i++) sb.Append(N(pts[i].X)).Append(' ').Append(N(pts[i].Y)).Append(i == 0 ? " m " : " l ");
        if (close) sb.Append("h ");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ appearances
    /// <summary>An appearance drawn in page space (BBox = the annotation's rect).</summary>
    private static void SetAppearance(PdfDocument doc, PdfDictionary a, PdfRect rect, string content, (string Kind, string Name, PdfDictionary Obj)? res)
    {
        var x = Form(doc, Nums(doc, rect.X0, rect.Y0, rect.X1, rect.Y1), null, content, res);
        var ap = new PdfDictionary(doc);
        ap.Elements.SetReference("/N", x);
        a.Elements["/AP"] = ap;
    }

    /// <summary>An appearance drawn upright (w × h, y up) and turned with the page's rotation, so it reads the right way.</summary>
    private static void SetUprightAppearance(PdfDocument doc, PdfDictionary a, Upright map, double w, double h, string content,
                                             (string Kind, string Name, PdfDictionary Obj)? res)
    {
        var m = map.Matrix.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var x = Form(doc, Nums(doc, 0, 0, w, h), Nums(doc, m), content, res);
        var ap = new PdfDictionary(doc);
        ap.Elements.SetReference("/N", x);
        a.Elements["/AP"] = ap;
    }

    private static PdfDictionary Form(PdfDocument doc, PdfArray bbox, PdfArray? matrix, string content, (string Kind, string Name, PdfDictionary Obj)? res)
    {
        var x = new PdfDictionary(doc);
        doc.Internals.AddObject(x);
        x.Elements.SetName("/Type", "/XObject");
        x.Elements.SetName("/Subtype", "/Form");
        x.Elements["/BBox"] = bbox;
        if (matrix is not null) x.Elements["/Matrix"] = matrix;
        var resources = new PdfDictionary(doc);
        if (res is { } r)
        {
            var group = new PdfDictionary(doc);
            group.Elements.SetReference(r.Name, r.Obj);
            resources.Elements[r.Kind] = group;
        }
        x.Elements["/Resources"] = resources;
        x.CreateStream(Encoding.ASCII.GetBytes(content));
        return x;
    }

    private static PdfDictionary Multiply(PdfDocument doc)
    {
        var gs = new PdfDictionary(doc);
        doc.Internals.AddObject(gs);
        gs.Elements.SetName("/Type", "/ExtGState");
        gs.Elements.SetName("/BM", "/Multiply");
        return gs;
    }

    // ------------------------------------------------------------------ text
    private static double Em(Rune r, FormScript script) =>
        r.Value < 0x80 ? (r.Value == ' ' ? 0.28 : char.IsUpper((char)r.Value) ? 0.67 : 0.55) : script == FormScript.Latin ? 0.55 : 1.0;

    private static string Hex(string ch, FormScript script) => script == FormScript.Latin
        ? "<" + Convert.ToHexString([.. ch.Select(c => c <= 0xFF ? (byte)c : (byte)'?')]) + ">"
        : "<" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(ch)) + ">";

    private static IEnumerable<string> Wrap(string text, double size, double width, FormScript script)
    {
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var line = new StringBuilder();
            double w = 0;
            foreach (var r in para.EnumerateRunes())
            {
                double cw = Em(r, script) * size;
                if (line.Length > 0 && w + cw > width)
                {
                    var s = line.ToString();
                    int sp = s.LastIndexOf(' ');
                    if (sp > 0 && r.Value != ' ') { yield return s[..sp]; line.Clear().Append(s[(sp + 1)..]); }
                    else { yield return s; line.Clear(); }
                    w = line.ToString().EnumerateRunes().Sum(x => Em(x, script) * size);
                }
                line.Append(r.ToString());
                w += cw;
            }
            yield return line.ToString();
        }
    }

    // ------------------------------------------------------------------ numbers
    private static (double R, double G, double B) Rgb(string hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#' && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, null, out var v))
            return (((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
        return (0, 0, 0);
    }

    private static PdfArray Nums(PdfDocument doc, params double[] v) =>
        new(doc, [.. v.Select(d => (PdfItem)new PdfReal(Math.Round(d, 3)))]);

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
