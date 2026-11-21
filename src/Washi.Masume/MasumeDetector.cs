// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Washi.Core;

namespace Washi.Masume;

/// <summary>What the detector decided about one table cell (for review overlays and debugging).</summary>
public sealed record CellInfo(PdfRect Rect, string Kind, string Text);

/// <summary>
/// Masume: finds the table cells of a flat PDF form and suggests input fields.
/// A port of <c>prototype/autoform/detect.py</c>; keep the two in step (same thresholds,
/// same rule order) so their scores can be compared.
/// </summary>
public static class MasumeDetector
{
    /// <summary>Raster pixels per PDF point.</summary>
    public const double Scale = 4.0;

    /// <summary>Prefilled marker: the cell holds printed content drawn as graphics, not as text.</summary>
    public const string DrawnMarker = "(drawn, not text)";

    /// <summary>Prefilled marker: printed text whose font has no Unicode mapping (garbled text layer).</summary>
    public const string UnreadableMarker = "(text, unreadable)";
    private const int InkLum = 210;      // graphics raster: darker = ink
    private const int MaxTickPx = 6;     // 1.5 pt at Scale: wider ink is a shape, not a divider line
    private const int ShadedLum = 238;   // cell interior median darker = shaded, not for input
    private const int CoreLum = 150;     // ink pixels this dark carry the ink's own colour, not anti-aliasing
    private const double SameInk = 0.2;  // InkDirection distance below this: the same ink, only lighter or darker

    private sealed class Cell(PdfRect rect)
    {
        public PdfRect Rect { get; } = rect;
        public List<PdfChar> LabelChars { get; } = [];
        public List<PdfChar> DataChars { get; } = [];
        public List<PdfRect> Boxes { get; } = [];
        public bool Shaded;
        public bool GraphicFill;
        public bool GraphicLabel;
        public bool FormInk;
        public string Kind = "?";
        public string Text = "";
    }

    public static IReadOnlyList<DetectedField> Detect(PageSnapshot page) => Detect(page, out _);

    public static IReadOnlyList<DetectedField> Detect(PageSnapshot page, out IReadOnlyList<CellInfo> cellInfo)
    {
        if (Math.Abs(page.Scale - Scale) > 1e-9)
            throw new ArgumentException($"page must be rendered at scale {Scale}", nameof(page));

        var g = new Gray(page.GraphicsOnly.ToGray(), page.GraphicsOnly.Width, page.GraphicsOnly.Height);
        var full = new Gray(page.Full.ToGray(), page.Full.Width, page.Full.Height);
        var (labels, data, labelCol) = SplitLabelData(page.Chars);
        // the form's own ink: its text colour, or, when every word is drawn as outlines, the page's commonest ink
        var formInk = page.Chars.Count > 0 ? InkDirection(labelCol) : DominantInk(g, page.GraphicsOnly);
        bool readable = Readable(string.Concat(page.Chars.Select(c => c.Text)));
        int pno = page.PageIndex;

        // □ glyphs -> checkboxes, removed from the label set
        var boxes = new List<PdfRect>();
        var rest = new List<PdfChar>();
        foreach (var c in labels)
        {
            PdfRect? sq = null;
            if (c.Text is "□" or "☐" || !readable) sq = HollowSquare(full, c.Rect);
            if (sq is { } s) boxes.Add(s); else rest.Add(c);
        }
        labels = rest;

        var cells = new List<Cell>();
        foreach (var (r, innerInk, inkCol) in FindCells(g, page.GraphicsOnly))
        {
            var cell = new Cell(r);
            cell.LabelChars.AddRange(labels.Where(c => Inside(c.Rect, r)));
            cell.DataChars.AddRange(data.Where(c => Inside(c.Rect, r)));
            cell.Boxes.AddRange(boxes.Where(b => Contains(r, b.Center)));
            var interior = g.Slice(Px(r.X0) + 3, Px(r.Y0) + 3, Px(r.X1) - 3, Px(r.Y1) - 3);
            cell.Shaded = interior.Length > 0 && Median(interior) < ShadedLum;
            cell.GraphicFill = innerInk > 0.02;
            // drawn glyphs in the form's own colour are labels (row numbers, headings)
            cell.GraphicLabel = cell.GraphicFill && inkCol is { } ic && ColorDistance(ic, labelCol) < 0.3;
            // the same test, but blind to how dark the ink looks: thin outlined glyphs come out grey once
            // anti-aliased, so a black heading reads as (0.25, 0.25, 0.25) and must still count as the form's own
            cell.FormInk = cell.GraphicFill && inkCol is { } fc && InkDirection(fc) is { } d && formInk is { } fd
                           && Distance(d, fd) < SameInk;
            cells.Add(cell);
        }

        var fields = new List<DetectedField>();

        // runs of equal boxes in a row (法人番号 style digit boxes) -> one comb
        var used = new HashSet<int>();
        var rows = new Dictionary<(double, double), List<int>>();
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (c.LabelChars.Count == 0 && c.Rect.Width < 20 && c.Rect.Height >= 6)
            {
                var key = (Math.Round(c.Rect.Y0), Math.Round(c.Rect.Y1));
                if (!rows.TryGetValue(key, out var l)) rows[key] = l = [];
                l.Add(i);
            }
        }
        foreach (var idx in rows.Values)
        {
            idx.Sort((a, b) => cells[a].Rect.X0.CompareTo(cells[b].Rect.X0));
            var run = new List<int> { idx[0] };
            foreach (int? next in idx.Skip(1).Select(i => (int?)i).Append(null))
            {
                if (next is int ni)
                {
                    PdfRect a = cells[run[^1]].Rect, b = cells[ni].Rect;
                    if (b.X0 - a.X1 < 2.5 && Math.Abs(b.Width - a.Width) < 0.2 * a.Width) { run.Add(ni); continue; }
                }
                if (run.Count >= 4)
                {
                    var r = new PdfRect(cells[run[0]].Rect.X0, cells[run[0]].Rect.Y0, cells[run[^1]].Rect.X1, cells[run[^1]].Rect.Y1);
                    var pre = JoinText(run.SelectMany(j => cells[j].DataChars));
                    fields.Add(new DetectedField { Page = pno, Kind = FieldKind.Comb, Rect = r, MaxLen = run.Count, Align = 1, Prefilled = pre });
                    foreach (int j in run) { used.Add(j); cells[j].Kind = "comb-part"; }
                }
                run = next is int n2 ? [n2] : [];
            }
        }

        for (int i = 0; i < cells.Count; i++)
        {
            if (used.Contains(i)) continue;
            var c = cells[i];
            var r = c.Rect;
            var pre = JoinText(c.DataChars);
            if (pre.Length == 0 && c.GraphicFill && !c.FormInk && CountTicks(g, r) == 0) pre = DrawnMarker;
            if (c.Shaded) { c.Kind = "shaded"; continue; }
            if (c.GraphicLabel && c.DataChars.Count == 0 && CountTicks(g, r) == 0) { c.Kind = "label"; continue; }

            if (c.LabelChars.Count > 0)
            {
                var lab = c.LabelChars.Select(ch => ch.Rect).Aggregate(Union);
                double w = r.Width, h = r.Height;
                bool small = lab.Area < 0.2 * r.Area && c.LabelChars.Count <= 3;
                PdfRect? sub = null;
                if (small && lab.X0 > r.X0 + 0.6 * w && h >= 7)
                {
                    // A: unit mark such as 円 in the right/bottom corner -> amount field
                    c.Kind = "amount";
                    fields.Add(new DetectedField
                    {
                        Page = pno, Kind = FieldKind.Amount, Rect = new PdfRect(r.X0, r.Y0, lab.X0 - 0.5, r.Y1),
                        Align = 2, Prefilled = pre, Label = JoinText(c.LabelChars),
                    });
                    continue;
                }
                if (c.Boxes.Count == 0 && h >= 7 && SplitAtMarks(r, c.LabelChars) is { } blanks)
                {
                    // D2: ＿年＿月＿日 — lone marks in one row, each with a blank before it: one field per blank, so
                    // a value never has to be spaced by hand to land between the printed marks
                    c.Kind = "text";
                    foreach (var (blank, mark) in blanks)
                        fields.Add(new DetectedField
                        {
                            Page = pno, Kind = FieldKind.Text, Rect = blank, Align = 2,
                            Prefilled = JoinText(c.DataChars.Where(ch => Contains(blank, ch.Rect.Center))) is { Length: > 0 } t0 ? t0 : pre == DrawnMarker ? pre : "",
                            Label = readable ? mark : "", Notes = ["blank before a mark"],
                        });
                    continue;
                }
                if (lab.Y1 <= r.Y0 + 0.45 * h && r.Y1 - lab.Y1 >= 8)
                    sub = new PdfRect(r.X0, lab.Y1 + 0.5, r.X1, r.Y1);          // B: note along the top
                else if (lab.Y0 >= r.Y0 + 0.55 * h && lab.Y0 - r.Y0 >= 8)
                    sub = new PdfRect(r.X0, r.Y0, r.X1, lab.Y0 - 0.5);          // C: note along the bottom
                else if (c.Boxes.Count == 0 && lab.X0 - r.X0 >= 0.25 * w && r.X1 - lab.X1 <= 0.2 * w && h >= 7)
                    sub = r;                                                    // D: ＿年＿月＿日 / ＿税務署長
                else if (c.Boxes.Count == 0 && c.LabelChars.Count <= 6 && h >= 7)
                {
                    // E: a few marks scattered in the cell, e.g. "(a) ........ 円"
                    var e = LargestEmpty(r, c.LabelChars.Select(ch => ch.Rect).ToList());
                    if (e is { } er && er.Area >= 0.5 * r.Area && er.Height >= 7 && er.Width >= 12) sub = er;
                }
                if (sub is not { } sr)
                {
                    c.Kind = "label";
                    c.Text = JoinText(c.LabelChars);
                    continue;
                }
                int t = CountTicks(g, sr);
                c.Kind = t > 0 ? "comb" : "text";
                fields.Add(new DetectedField
                {
                    Page = pno, Kind = t > 0 ? FieldKind.Comb : FieldKind.Text, Rect = sr,
                    MaxLen = t > 0 ? t + 1 : 0, Align = t > 0 ? 1 : 0, Prefilled = pre,
                    Label = Truncate(JoinText(c.LabelChars), 24), Notes = ["label inside cell"],
                });
                continue;
            }

            if (r.Width < 6 || r.Height < 5) { c.Kind = "skip"; continue; }
            if (r.Width <= 12 && r.Height <= 12 && r.Width / r.Height is > 0.75 and < 1.33)
            {
                c.Kind = "checkbox";
                fields.Add(new DetectedField { Page = pno, Kind = FieldKind.Checkbox, Rect = r });
                continue;
            }
            int n = CountTicks(g, r);
            if (n > 0)
            {
                c.Kind = "comb";
                fields.Add(new DetectedField { Page = pno, Kind = FieldKind.Comb, Rect = r, MaxLen = n + 1, Align = 1, Prefilled = pre });
                continue;
            }
            c.Kind = "text";
            fields.Add(new DetectedField { Page = pno, Kind = FieldKind.Text, Rect = r, Prefilled = pre });
        }

        foreach (var b in boxes)
            fields.Add(new DetectedField { Page = pno, Kind = FieldKind.Checkbox, Rect = b, Notes = ["□ glyph"] });

        // printed text the text layer cannot see: glyphs without a Unicode mapping never reach page.Chars, so a field
        // printed in such a font looked empty (法人番号 digits on some e-Gov forms)
        for (int i = 0; i < fields.Count; i++)
            if (fields[i].Prefilled.Length == 0 && fields[i].Kind != FieldKind.Checkbox && UnmappedInk(page, g, fields[i].Rect, labelCol))
                fields[i] = fields[i] with { Prefilled = UnreadableMarker };

        var named = NameFields(fields, cells, labels, readable, pno);
        if (!readable)
        {
            // a garbled text layer gives mojibake: keep only the fact that something is printed there
            named = named.Select(f => f with
            {
                Label = "",
                Prefilled = f.Prefilled is "" or DrawnMarker ? f.Prefilled : UnreadableMarker,
            }).ToList();
        }
        cellInfo = cells.Select(c => new CellInfo(c.Rect, c.Kind, c.Text)).ToList();
        return named;
    }

    // ------------------------------------------------------------------ naming
    private static List<DetectedField> NameFields(List<DetectedField> fields, List<Cell> cells, List<PdfChar> labels, bool readable, int pno)
    {
        var labCells = cells.Where(c => c.Kind == "label" && c.Text.Length > 0).ToList();
        var order = fields.Select((f, i) => (f, i))
            .OrderBy(t => Math.Round(t.f.Rect.Y0 / 6)).ThenBy(t => t.f.Rect.X0).ToList();
        var seen = new Dictionary<string, int>();
        var result = fields.ToArray();
        for (int k = 0; k < order.Count; k++)
        {
            var (f, idx) = order[k];
            var r = f.Rect;
            string name = "";
            if (readable)
            {
                if (f.Kind == FieldKind.Checkbox)
                {
                    // the words printed right after the box
                    var right = labels.Where(c => Math.Abs(c.Rect.Center.Y - r.Center.Y) < r.Height
                                                  && c.Rect.X0 > r.X1 - 1 && c.Rect.X0 < r.X1 + 80)
                                      .OrderBy(c => c.Rect.X0).Take(12);
                    name = JoinText(right);
                }
                else
                {
                    (double D, string Text)? best = null;
                    foreach (var c in labCells)
                    {
                        var cr = c.Rect;
                        double vo = Math.Min(cr.Y1, r.Y1) - Math.Max(cr.Y0, r.Y0);
                        double ho = Math.Min(cr.X1, r.X1) - Math.Max(cr.X0, r.X0);
                        double d;
                        if (cr.X1 <= r.X0 + 2 && vo > 0.5 * Math.Min(cr.Height, r.Height)) d = r.X0 - cr.X1;
                        else if (cr.Y1 <= r.Y0 + 2 && ho > 0.5 * Math.Min(cr.Width, r.Width)) d = (r.Y0 - cr.Y1) * 1.5;
                        else continue;
                        if (best is null || d < best.Value.D) best = (d, c.Text);
                    }
                    name = best?.Text ?? "";
                }
                name = Truncate(string.Concat(name.Where(ch => !char.IsWhiteSpace(ch))), 24);
            }
            string label = f.Label.Length > 0 ? f.Label : name;
            string baseName = name.Length > 0 ? name : $"p{pno + 1}_{f.Kind.ToString().ToLowerInvariant()}_{k + 1:000}";
            seen[baseName] = seen.GetValueOrDefault(baseName) + 1;
            result[idx] = f with { Label = label, Name = seen[baseName] == 1 ? baseName : $"{baseName}_{seen[baseName]}" };
        }
        return [.. result];
    }

    // ------------------------------------------------------------------ text
    // Latin (incl. French/German accents), CJK punctuation and kana, CJK ideographs,
    // Hangul (syllables, jamo, compatibility jamo), Bopomofo, full/half-width forms, symbols.
    /// <summary>False when the text layer decodes to garbage (fonts without a Unicode mapping).</summary>
    public static bool Readable(string text) => TextQuality.Readable(text);

    private static (double R, double G, double B) Rgb(int c) =>
        (((c >> 16) & 255) / 255.0, ((c >> 8) & 255) / 255.0, (c & 255) / 255.0);

    private static double ColorDistance((double R, double G, double B) a, (double R, double G, double B) b) =>
        Math.Sqrt((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));

    private static double Distance((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>
    /// The hue of an ink, whatever its strength: how much of each channel it takes away from white, scaled to unit
    /// length. Black, a grey anti-aliased black stroke and a 60% tint all point the same way; red or blue does not.
    /// Null for (near) white.
    /// </summary>
    private static (double X, double Y, double Z)? InkDirection((double R, double G, double B) c)
    {
        double x = 1 - c.R, y = 1 - c.G, z = 1 - c.B, n = Math.Sqrt(x * x + y * y + z * z);
        return n < 0.05 ? null : (x / n, y / n, z / n);
    }

    /// <summary>The commonest ink hue on the page (rulings and outlined headings): the form's own colour.</summary>
    private static (double X, double Y, double Z)? DominantInk(Gray g, RgbImage rgb)
    {
        var bins = new Dictionary<int, (long N, double X, double Y, double Z)>();
        var px = rgb.Pixels;
        for (int i = 0; i < g.Px.Length; i++)
        {
            if (g.Px[i] >= CoreLum) continue;
            if (InkDirection((px[i * 3] / 255.0, px[i * 3 + 1] / 255.0, px[i * 3 + 2] / 255.0)) is not { } d) continue;
            int key = (int)Math.Round(d.X * 10) * 400 + (int)Math.Round(d.Y * 10) * 20 + (int)Math.Round(d.Z * 10);
            var b = bins.GetValueOrDefault(key);
            bins[key] = (b.N + 1, b.X + d.X, b.Y + d.Y, b.Z + d.Z);
        }
        if (bins.Count == 0) return null;
        var top = bins.Values.MaxBy(b => b.N);
        double n = Math.Sqrt(top.X * top.X + top.Y * top.Y + top.Z * top.Z);
        return (top.X / n, top.Y / n, top.Z / n);
    }

    /// <summary>Dominant colour = form labels. A clearly different colour = prefilled data.</summary>
    private static (List<PdfChar> Labels, List<PdfChar> Data, (double, double, double) LabelColor) SplitLabelData(IReadOnlyList<PdfChar> chars)
    {
        if (chars.Count == 0) return ([], [], (0, 0, 0));
        int dominant = chars.GroupBy(c => c.Rgb)
            .OrderByDescending(gr => gr.Count()).ThenBy(gr => gr.Key).First().Key;
        var lc = Rgb(dominant);
        List<PdfChar> labels = [], data = [];
        foreach (var c in chars) (ColorDistance(Rgb(c.Rgb), lc) > 0.35 ? data : labels).Add(c);
        return (labels, data, lc);
    }

    /// <summary>Dark pixels (3 pt² at <see cref="Scale"/>) that make a field count as printed.</summary>
    private const int MinUnmappedInk = 48;

    /// <summary>
    /// Ink inside <paramref name="r"/> that only the full render has (so it is text), away from every character the
    /// text layer knows and not in the labels' colour: text whose glyphs have no Unicode mapping.
    /// </summary>
    private static bool UnmappedInk(PageSnapshot page, Gray g, PdfRect r, (double, double, double) labelCol)
    {
        var known = page.Chars.Where(c => c.Rect.X1 > r.X0 && c.Rect.X0 < r.X1 && c.Rect.Y1 > r.Y0 && c.Rect.Y0 < r.Y1)
            .Select(c => (X0: Px(c.Rect.X0) - 2, Y0: Px(c.Rect.Y0) - 2, X1: Px(c.Rect.X1) + 2, Y1: Px(c.Rect.Y1) + 2)).ToList();
        var full = page.Full;
        int x0 = Math.Max(0, Px(r.X0) + 2), y0 = Math.Max(0, Px(r.Y0) + 2);
        int x1 = Math.Min(Math.Min(full.Width, g.W), Px(r.X1) - 2), y1 = Math.Min(Math.Min(full.Height, g.H), Px(r.Y1) - 2);
        int ink = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                if (g.Px[y * g.W + x] < 200) continue;                     // a rule or tick of the form itself
                int s = (y * full.Width + x) * 3;
                byte cr = full.Pixels[s], cg = full.Pixels[s + 1], cb = full.Pixels[s + 2];
                if ((cr * 299 + cg * 587 + cb * 114) / 1000 >= 128) continue;
                if (ColorDistance((cr / 255.0, cg / 255.0, cb / 255.0), labelCol) <= 0.35) continue;
                if (known.Any(k => x >= k.X0 && x < k.X1 && y >= k.Y0 && y < k.Y1)) continue;
                if (++ink >= MinUnmappedInk) return true;
            }
        return false;
    }

    private static string JoinText(IEnumerable<PdfChar> chars) =>
        string.Concat(chars.OrderBy(c => Math.Round(c.Rect.Y0 / 4)).ThenBy(c => c.Rect.X0).Select(c => c.Text))
              .Normalize(NormalizationForm.FormKC);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    // ------------------------------------------------------------------ geometry
    private static int Px(double v) => (int)(v * Scale);

    private static bool Contains(PdfRect r, (double X, double Y) p) => p.X >= r.X0 && p.X < r.X1 && p.Y >= r.Y0 && p.Y < r.Y1;

    private static bool Inside(PdfRect ch, PdfRect r) => Contains(r, ch.Center);

    private static PdfRect Union(PdfRect a, PdfRect b) =>
        new(Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1));

    /// <summary>Largest axis-aligned rectangle inside r touching none of the obstacles (brute force; n is tiny).</summary>
    internal static PdfRect? LargestEmpty(PdfRect r, IReadOnlyList<PdfRect> obstacles)
    {
        var obs = obstacles.Select(o => new PdfRect(o.X0 - 0.5, o.Y0 - 0.5, o.X1 + 0.5, o.Y1 + 0.5)).ToList();
        double Cx(double v) => Math.Min(Math.Max(v, r.X0), r.X1);
        double Cy(double v) => Math.Min(Math.Max(v, r.Y0), r.Y1);
        var xs = new SortedSet<double> { r.X0, r.X1 };
        var ys = new SortedSet<double> { r.Y0, r.Y1 };
        foreach (var o in obs) { xs.Add(Cx(o.X0)); xs.Add(Cx(o.X1)); ys.Add(Cy(o.Y0)); ys.Add(Cy(o.Y1)); }
        double[] X = [.. xs], Y = [.. ys];
        PdfRect? best = null;
        double area = 0;
        for (int i = 0; i < X.Length; i++)
            for (int i2 = i + 1; i2 < X.Length; i2++)
                for (int j = 0; j < Y.Length; j++)
                    for (int j2 = j + 1; j2 < Y.Length; j2++)
                    {
                        double a = (X[i2] - X[i]) * (Y[j2] - Y[j]);
                        if (a <= area) continue;
                        var cand = new PdfRect(X[i], Y[j], X[i2], Y[j2]);
                        if (obs.Any(o => cand.Intersect(o) is { Width: > 0, Height: > 0 })) continue;
                        best = cand; area = a;
                    }
        return best;
    }

    // ------------------------------------------------------------------ rasters
    private sealed class Gray(byte[] px, int w, int h)
    {
        public byte[] Px { get; } = px;
        public int W { get; } = w;
        public int H { get; } = h;

        /// <summary>Python-style slice [y0:y1, x0:x1] with clamping; returns row-major values.</summary>
        public byte[] Slice(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Clamp(x0, 0, W); x1 = Math.Clamp(x1, 0, W);
            y0 = Math.Clamp(y0, 0, H); y1 = Math.Clamp(y1, 0, H);
            if (x1 <= x0 || y1 <= y0) return [];
            var o = new byte[(x1 - x0) * (y1 - y0)];
            for (int y = y0, k = 0; y < y1; y++)
                for (int x = x0; x < x1; x++) o[k++] = Px[y * W + x];
            return o;
        }

        public int SliceWidth(int x0, int x1) => Math.Max(0, Math.Clamp(x1, 0, W) - Math.Clamp(x0, 0, W));
    }

    private static double Median(byte[] v)
    {
        var s = (byte[])v.Clone();
        Array.Sort(s);
        int n = s.Length;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }

    private static List<(PdfRect Rect, double InnerInk, (double, double, double)? InkColor)> FindCells(Gray g, RgbImage rgb)
    {
        int W = g.W, H = g.H;
        var ink = new bool[g.Px.Length];
        for (int i = 0; i < ink.Length; i++) ink[i] = g.Px[i] < InkLum;
        ink = Bitmaps.DilateCross(ink, W, H);
        var bg = new bool[ink.Length];
        for (int i = 0; i < bg.Length; i++) bg[i] = !ink[i];
        var (labels, comps) = Bitmaps.Label(bg, W, H);

        var cells = new List<(PdfRect, double, (double, double, double)?)>();
        foreach (var c in comps)
        {
            if (c.Y0 == 0 || c.X0 == 0 || c.Y1 == H || c.X1 == W) continue;   // outside the form
            int w = c.Width, h = c.Height;
            if (w < 3 * Scale || h < 3 * Scale) continue;
            if ((double)w * h > 0.25 * H * W) continue;
            var holes = Bitmaps.Holes(labels, W, c);
            int holeCount = holes.Count(b => b);
            int solid = c.Count + holeCount;
            if (solid / ((double)w * h) < 0.85) continue;                     // triangles, rounded blobs
            double innerInk = holeCount / (double)solid;
            (double, double, double)? col = null;
            if (holeCount > 0)
                col = MeanColor(CoreLum) ?? MeanColor(InkLum);
            cells.Add((new PdfRect((c.X0 - 1) / Scale, (c.Y0 - 1) / Scale, (c.X1 + 1) / Scale, (c.Y1 + 1) / Scale), innerInk, col));

            // core pixels only: anti-aliased edges wash the colour out
            (double, double, double)? MeanColor(int below)
            {
                long r = 0, gg = 0, b = 0, n = 0;
                for (int by = 0; by < h; by++)
                    for (int bx = 0; bx < w; bx++)
                    {
                        if (!holes[by * w + bx]) continue;
                        int i = (c.Y0 + by) * W + c.X0 + bx;
                        if (g.Px[i] >= below) continue;
                        r += rgb.Pixels[i * 3]; gg += rgb.Pixels[i * 3 + 1]; b += rgb.Pixels[i * 3 + 2]; n++;
                    }
                return n == 0 ? null : (r / (255.0 * n), gg / (255.0 * n), b / (255.0 * n));
            }
        }
        return cells;
    }

    /// <summary>Short vertical marks inside a cell that do not reach both walls (comb dividers).</summary>
    /// <summary>
    /// The blanks of a "＿年＿月＿日" cell: two or three lone marks of one size on one line, the first well inside the
    /// cell, the last near its right end, each with at least 10 pt (and 1.5 marks) of blank before it. Geometry only,
    /// so it also works when the text layer cannot be read. Null when the cell is not of that kind (a spaced-out
    /// heading such as 氏　名 starts at the left; a word such as 税務署長 has no blanks between its characters).
    /// </summary>
    private static List<(PdfRect Blank, string Mark)>? SplitAtMarks(PdfRect r, List<PdfChar> labelChars)
    {
        if (labelChars.Count is < 2 or > 3) return null;
        var marks = labelChars.OrderBy(ch => ch.Rect.X0).ToList();
        double size = marks.Average(m => m.Rect.Width);
        if (marks.Any(m => Math.Abs(m.Rect.Width - size) > 0.35 * size)) return null;
        double mid = marks.Average(m => m.Rect.Center.Y);
        if (marks.Any(m => Math.Abs(m.Rect.Center.Y - mid) > 0.3 * size)) return null;
        if (marks[0].Rect.X0 - r.X0 < 0.2 * r.Width || r.X1 - marks[^1].Rect.X1 > 0.2 * r.Width) return null;
        var parts = new List<(PdfRect, string)>();
        double left = r.X0;
        foreach (var m in marks)
        {
            double blank = m.Rect.X0 - left;
            if (blank < Math.Max(10, 1.5 * size)) return null;
            parts.Add((new PdfRect(left + (parts.Count == 0 ? 0 : 0.5), r.Y0, m.Rect.X0 - 0.5, r.Y1), m.Text));
            left = m.Rect.X1;
        }
        return parts;
    }

    private static int CountTicks(Gray g, PdfRect r)
    {
        int x0 = Px(r.X0) + 3, x1 = Px(r.X1) - 3, y0 = Px(r.Y0) + 2, y1 = Px(r.Y1) - 2;
        var sub = g.Slice(x0, y0, x1, y1);
        if (sub.Length == 0) return 0;
        int sw = g.SliceWidth(x0, x1), sh = sub.Length / sw;
        var xs = new List<double>();
        var cur = new List<int>();     // a run of columns with ink
        var full = new List<bool>();   // ... and whether each one is ink from top to bottom
        for (int x = 0; x < sw; x++)
        {
            int inkCount = 0;
            for (int y = 0; y < sh; y++) if (sub[y * sw + x] < InkLum) inkCount++;
            double frac = inkCount / (double)sh;
            if (frac > 0.12) { cur.Add(x); full.Add(frac >= 0.97); }
            else if (cur.Count > 0) Flush();
        }
        if (cur.Count > 0) Flush();

        void Flush()
        {
            if (full.Contains(true) && cur.Count <= MaxTickPx)
            {
                // a thin line as tall as the row is a divider too: digit groups are often separated by full-height
                // lines (法人番号: 1 + 4 + 4 + 4 boxes)
                xs.Add(cur.Average());
            }
            else
            {
                // short ticks; inside a wide solid block only its soft edges count (as before)
                var part = new List<int>();
                for (int k = 0; k < cur.Count; k++)
                {
                    if (!full[k]) { part.Add(cur[k]); continue; }
                    if (part.Count > 0) { xs.Add(part.Average()); part.Clear(); }
                }
                if (part.Count > 0) xs.Add(part.Average());
            }
            cur.Clear();
            full.Clear();
        }

        if (xs.Count < 3) return 0;
        var pts = new List<double> { 0 };
        pts.AddRange(xs);
        pts.Add(sw);
        var gaps = pts.Zip(pts.Skip(1), (a, b) => b - a).ToArray();
        double mean = gaps.Average();
        double std = Math.Sqrt(gaps.Average(v => (v - mean) * (v - mean)));
        return std / mean > 0.2 ? 0 : xs.Count;
    }

    /// <summary>Is this glyph a □ ? Works even when the font has no Unicode mapping.</summary>
    private static PdfRect? HollowSquare(Gray full, PdfRect r)
    {
        if (!(r.Width is >= 3 and <= 14 && r.Height is >= 3 and <= 14)) return null;
        int x0 = Px(r.X0), y0 = Px(r.Y0), x1 = Px(r.X1), y1 = Px(r.Y1);
        var crop = full.Slice(x0, y0, x1, y1);
        if (crop.Length == 0) return null;
        int cw = full.SliceWidth(x0, x1), ch = crop.Length / cw;
        int a = int.MaxValue, b = -1, c = int.MaxValue, d = -1, count = 0;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
                if (crop[y * cw + x] < 160)
                {
                    count++;
                    if (y < a) a = y;
                    if (y > b) b = y;
                    if (x < c) c = x;
                    if (x > d) d = x;
                }
        if (count < 8) return null;
        int h = b - a + 1, w = d - c + 1;
        if (h < 0.5 * ch || !(w / (double)h is > 0.75 and < 1.33)) return null;
        bool Ink(int yy, int xx) => crop[(a + yy) * cw + c + xx] < 160;
        double RowMean(int yy) => Enumerable.Range(0, w).Count(xx => Ink(yy, xx)) / (double)w;
        double ColMean(int xx) => Enumerable.Range(0, h).Count(yy => Ink(yy, xx)) / (double)h;
        double edge = new[] { RowMean(0), RowMean(h - 1), ColMean(0), ColMean(w - 1) }.Min();
        int iy0 = h / 4, iy1 = h - h / 4, ix0 = w / 4, ix1 = w - w / 4;
        int innerN = Math.Max(0, iy1 - iy0) * Math.Max(0, ix1 - ix0), innerInk = 0;
        for (int yy = iy0; yy < iy1; yy++)
            for (int xx = ix0; xx < ix1; xx++) if (Ink(yy, xx)) innerInk++;
        if (edge > 0.7 && (innerN == 0 || innerInk / (double)innerN < 0.08))
            return new PdfRect((x0 + c) / Scale, (y0 + a) / Scale, (x0 + d + 1) / Scale, (y0 + b + 1) / Scale);
        return null;
    }
}
