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

        // ○ glyphs in a row with words after each (○有 ○無, ○ Yes ○ No) -> radio groups, removed from the label set
        var radioGroups = readable ? RadioGroups(labels) : [];
        var radioGlyphs = radioGroups.SelectMany(gr => gr).Select(r => r.Glyph).ToHashSet();
        labels = [.. labels.Where(c => !radioGlyphs.Contains(c))];

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
        // radio buttons count as boxes for the cell rules: a cell of choices is not a blank to write in
        var boxLike = boxes.Concat(radioGlyphs.Select(c => c.Rect)).ToList();

        var cells = new List<Cell>();
        // horizontal rules: they close rows that are open at the sides, and are the lines people write on
        var rules = HorizontalRules(g);
        foreach (var (r, innerInk, inkCol) in FindCells(CloseOpenRows(g, rules, labels), page.GraphicsOnly))
        {
            var cell = new Cell(r);
            cell.LabelChars.AddRange(labels.Where(c => Inside(c.Rect, r)));
            cell.DataChars.AddRange(data.Where(c => Inside(c.Rect, r)));
            if (Paragraph(cell.DataChars))
            {
                // a paragraph in the "other" colour is printed explanation, not a filled-in value: on a guide page
                // whose commentary is mostly red, the black body text would otherwise be taken for data
                cell.LabelChars.AddRange(cell.DataChars);
                cell.DataChars.Clear();
            }
            cell.Boxes.AddRange(boxLike.Where(b => Contains(r, b.Center)));
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
                if (ValueColumn(c.LabelChars))
                {
                    // printed values stacked flush right ($375 / $385 / $395 in a wage-bracket table): a table of
                    // data, not a caption or a blank with marks, so no rule below may turn the cell into a field
                    c.Kind = "label";
                    c.Text = JoinText(c.LabelChars);
                    continue;
                }
                if (!Spills(c.LabelChars, labels, r) && ((lab.Y1 <= r.Y0 + 0.45 * h && r.Y1 - lab.Y1 >= 8)
                    // a one-line Latin caption in the top left: Latin glyph boxes carry the ascender and descender
                    // room, so the caption looks twice as tall as its ink, yet a full line is left below it
                    || (lab.Y1 <= r.Y0 + 0.6 * h && r.Y1 - lab.Y1 >= 12 && lab.X0 - r.X0 <= 0.1 * w
                        && Lines(c.LabelChars).Count == 1 && c.LabelChars.All(ch => ch.Text.All(t => t < '\u0250')))))
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

        fields.AddRange(UnderlineBlanks(g, rules, labels, data, cells.Select(c => c.Rect).ToList(), fields, pno));

        foreach (var b in boxes)
            fields.Add(new DetectedField { Page = pno, Kind = FieldKind.Checkbox, Rect = b, Notes = ["□ glyph"] });
        for (int gi = 0; gi < radioGroups.Count; gi++)
            foreach (var (glyph, export) in radioGroups[gi])
                fields.Add(new DetectedField
                {
                    Page = pno, Kind = FieldKind.Radio, Rect = Inset(glyph.Rect, 0.08), Group = $"p{pno + 1}_radio_{gi + 1:00}",
                    Export = export, Label = export, Notes = ["○ glyph"],
                });

        // printed text the text layer cannot see: glyphs without a Unicode mapping never reach page.Chars, so a field
        // printed in such a font looked empty (法人番号 digits on some e-Gov forms)
        for (int i = 0; i < fields.Count; i++)
            if (fields[i].Prefilled.Length == 0 && fields[i].Kind != FieldKind.Checkbox && UnmappedInk(page, g, fields[i].Rect, labelCol))
                fields[i] = fields[i] with { Prefilled = UnreadableMarker };

        var named = NameFields(fields, cells, labels, readable, pno);
        // a blank whose label asks for a signature (署名, Signature, 서명 ...) is a signature field
        named = [.. named.Select(f => f.Kind is FieldKind.Text && readable && IsSignatureLabel(f.Label + " " + f.Name)
            ? f with { Kind = FieldKind.Signature, Align = 0, Notes = [.. f.Notes, "signature label"] } : f)];
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
        var groupHeading = new Dictionary<string, string>();   // radio group id -> the heading of its first button
        for (int k = 0; k < order.Count; k++)
        {
            var (f, idx) = order[k];
            var r = f.Rect;
            string name = "";
            if (readable && f.Kind == FieldKind.Radio && !groupHeading.ContainsKey(f.Group))
                groupHeading[f.Group] = Truncate(string.Concat(Heading(r).Where(ch => !char.IsWhiteSpace(ch))), 24);
            if (readable)
            {
                if (f.Kind is FieldKind.Checkbox or FieldKind.Radio)
                {
                    // the words printed right after the box
                    var right = labels.Where(c => Math.Abs(c.Rect.Center.Y - r.Center.Y) < r.Height
                                                  && c.Rect.X0 > r.X1 - 1 && c.Rect.X0 < r.X1 + 80)
                                      .OrderBy(c => c.Rect.X0).Take(12);
                    name = JoinText(right);
                }
                else
                {
                    name = Heading(r);
                    // a blank on a line is named by the words printed beside it, not by a heading cell above
                    if (f.Notes.Contains("underline") && f.Label.Length > 0) name = f.Label;
                }
                name = Truncate(string.Concat(name.Where(ch => !char.IsWhiteSpace(ch))), 24);
            }
            string label = f.Label.Length > 0 ? f.Label : name;
            string baseName = name.Length > 0 ? name : $"p{pno + 1}_{f.Kind.ToString().ToLowerInvariant()}_{k + 1:000}";
            seen[baseName] = seen.GetValueOrDefault(baseName) + 1;
            result[idx] = f with { Label = label, Name = seen[baseName] == 1 ? baseName : $"{baseName}_{seen[baseName]}" };
        }
        // a radio group is named by the heading beside its first button (性別, 有無 ...), when there is one
        var taken = result.Where(f => f.Kind != FieldKind.Radio).Select(f => f.Name).ToHashSet();
        foreach (var (id, heading) in groupHeading)
        {
            if (heading.Length == 0) continue;
            var g = heading;
            for (int k = 2; !taken.Add(g); k++) g = $"{heading}_{k}";
            for (int i = 0; i < result.Length; i++)
                if (result[i].Kind == FieldKind.Radio && result[i].Group == id) result[i] = result[i] with { Group = g };
        }
        return [.. result];

        // the label cell to the left of (or above) a rect
        string Heading(PdfRect r)
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
            return best?.Text ?? "";
        }
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

    private static readonly string[] SignatureWords =
        ["署名", "自署", "サイン", "签名", "簽名", "签字", "簽字", "서명", "signature", "unterschrift", "sign here"];

    /// <summary>A label that asks for a signature. 記名 (a printed name) and 印 (a seal) are not signatures.</summary>
    internal static bool IsSignatureLabel(string label)
    {
        var l = label.ToLowerInvariant();
        return SignatureWords.Any(l.Contains) && !l.Contains("署名欄") && !l.Contains("記載");
    }

    private static PdfRect Inset(PdfRect r, double f) =>
        new(r.X0 + r.Width * f, r.Y0 + r.Height * f, r.X1 - r.Width * f, r.Y1 - r.Height * f);

    /// <summary>
    /// ○ glyphs that make a choice: two or more on one line, each followed by its words (○有 ○無, ○ Yes ○ No), the
    /// words running up to the next ○. A lone ○, or ○ with nothing after it (○○市, a bullet), is left as text.
    /// </summary>
    internal static List<List<(PdfChar Glyph, string Export)>> RadioGroups(IReadOnlyList<PdfChar> chars)
    {
        var groups = new List<List<(PdfChar, string)>>();
        foreach (var line in Lines(chars))
        {
            var row = line.OrderBy(c => c.Rect.X0).ToList();
            List<(PdfChar, string)> current = [];
            static bool Ring(PdfChar c) => c.Text is "○" or "◯";
            for (int i = 0; i < row.Count; i++)
            {
                if (!Ring(row[i])) continue;
                // ○○県 ○丁目 令和○年: a placeholder glued to the words around it, not a button to choose
                double gw = Math.Max(row[i].Rect.Width, 4);
                bool glued = i > 0 && row[i - 1] is var prev && !string.IsNullOrWhiteSpace(prev.Text)
                             && prev.Rect.X1 > row[i].Rect.X0 - 0.25 * gw && !"・、,/／(（[「:：".Contains(prev.Text);
                bool doubled = (i > 0 && Ring(row[i - 1]) && row[i].Rect.X0 - row[i - 1].Rect.X1 < 0.5 * gw)
                               || (i + 1 < row.Count && Ring(row[i + 1]) && row[i + 1].Rect.X0 - row[i].Rect.X1 < 0.5 * gw);
                if (glued || doubled)
                {
                    if (current.Count >= 2) groups.Add(current);
                    current = [];
                    continue;
                }
                // the words after this ○: until the next ○, a gap wider than two glyphs, or 12 characters
                var words = new List<PdfChar>();
                double edge = row[i].Rect.X1;
                for (int j = i + 1; j < row.Count && words.Count < 12; j++)
                {
                    var c = row[j];
                    if (Ring(c) || c.Rect.X0 - edge > 2 * gw) break;
                    if (!string.IsNullOrWhiteSpace(c.Text)) words.Add(c);
                    edge = c.Rect.X1;
                }
                var export = JoinText(words).Trim().TrimEnd('・', '、', ',', '/', '／').Trim();
                // ○○市 (a blank place name) or a ○ bullet before a sentence is not a choice
                if (export.Length == 0 || export.StartsWith('○') || export.Length > 10)
                {
                    if (current.Count >= 2) groups.Add(current);
                    current = [];
                    continue;
                }
                // a new group when this ○ is far from the previous one
                if (current.Count > 0 && row[i].Rect.X0 - current[^1].Item1.Rect.X1 > 160)
                {
                    if (current.Count >= 2) groups.Add(current);
                    current = [];
                }
                current.Add((row[i], export));
            }
            if (current.Count >= 2) groups.Add(current);
        }
        return groups;
    }

    /// <summary>The chars grouped into text lines: chars whose vertical centres are within half a glyph share one.</summary>
    internal static List<List<PdfChar>> Lines(IReadOnlyList<PdfChar> chars)
    {
        var lines = new List<List<PdfChar>>();
        foreach (var ch in chars.OrderBy(c => c.Rect.Center.Y))
        {
            var last = lines.Count > 0 ? lines[^1][0].Rect : default;
            if (lines.Count > 0 && Math.Abs(ch.Rect.Center.Y - last.Center.Y) < 0.5 * Math.Max(Math.Max(ch.Rect.Height, last.Height), 1))
                lines[^1].Add(ch);
            else
                lines.Add([ch]);
        }
        return lines;
    }

    /// <summary>Running prose: three or more lines with at least two sentence endings. Filled-in values (names,
    /// numbers, addresses) are short and rarely end a sentence, let alone two.</summary>
    internal static bool Paragraph(IReadOnlyList<PdfChar> chars)
    {
        var lines = Lines(chars);
        if (chars.Count < 20 || lines.Count < 3) return false;
        var text = string.Join(" ", lines.Select(l => string.Concat(l.OrderBy(c => c.Rect.X0).Select(c => c.Text)))) + " ";
        int ends = 0;
        for (int i = 0; i < text.Length - 1; i++)
            if (text[i] is '。' or '．' || (text[i] is '.' or '!' or '?' && char.IsWhiteSpace(text[i + 1]))) ends++;
        return ends >= 2;
    }

    /// <summary>Two or more lines that end at the same x: a right-aligned column of printed values.</summary>
    internal static bool ValueColumn(IReadOnlyList<PdfChar> chars)
    {
        var lines = Lines(chars);
        if (lines.Count < 2) return false;
        var ends = lines.Select(l => l.Max(c => c.Rect.X1)).ToList();
        return ends.Max() - ends.Min() < 1.5;
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

    // ------------------------------------------------------------------ rules and underlines
    private const int MaxRulePx = 10;      // 2.5 pt: thicker is a bar or a filled block, not a rule
    private const double MinBlank = 18;   // pt: a narrower gap on a line is spacing, not a place to write
    private const double BlankHeight = 12; // pt above an underline that a value may use

    /// <summary>A horizontal rule in raster pixels (X1, Y1 exclusive).</summary>
    private readonly record struct HRule(int X0, int X1, int Y0, int Y1)
    {
        public int Length => X1 - X0;
    }

    /// <summary>Thin horizontal runs of ink at least 15 pt long, merged across the rows of their thickness.</summary>
    private static List<HRule> HorizontalRules(Gray g)
    {
        int minLen = Px(15);
        var done = new List<HRule>();
        var open = new List<HRule>();
        for (int y = 0; y <= g.H; y++)
        {
            var next = new List<HRule>();
            if (y < g.H)
            {
                int row = y * g.W;
                for (int x = 0; x < g.W;)
                {
                    if (g.Px[row + x] >= InkLum) { x++; continue; }
                    int s = x;
                    while (x < g.W && g.Px[row + x] < InkLum) x++;
                    if (x - s < minLen) continue;
                    int k = open.FindIndex(o => Math.Abs(o.X0 - s) <= 2 && Math.Abs(o.X1 - x) <= 2);
                    if (k >= 0)
                    {
                        var o = open[k];
                        open.RemoveAt(k);
                        next.Add(new HRule(Math.Min(o.X0, s), Math.Max(o.X1, x), o.Y0, y + 1));
                    }
                    else next.Add(new HRule(s, x, y, y + 1));
                }
            }
            done.AddRange(open);
            open = next;
        }
        return done.Where(r => r.Y1 - r.Y0 <= MaxRulePx).ToList();
    }

    /// <summary>
    /// Rows drawn as long rules with no frame at the sides (IRS style: each row a line across the page, the label in
    /// its top left) have no closed cells. Where the next long rule below ends at the same x, draw the missing side.
    /// </summary>
    private static Gray CloseOpenRows(Gray g, List<HRule> rules, List<PdfChar> labels)
    {
        var longRules = rules.Where(r => r.Length >= 0.3 * g.W).OrderBy(r => r.Y0).ToList();
        var below = rules.OrderBy(r => r.Y0).ToList();     // the next rule down may be a short one
        byte[]? px = null;
        int maxGap = Px(60), tol = 6;
        for (int i = 0; i < longRules.Count; i++)
            foreach (bool right in new[] { false, true })
            {
                var a = longRules[i];
                int xa = right ? a.X1 - 1 : a.X0;
                foreach (var b in below)
                {
                    if (b.Y0 - a.Y1 > maxGap) break;
                    if (b.Y0 <= a.Y1 + 1 || b.X0 > xa + tol || b.X1 - 1 < xa - tol) continue;   // same rule, or beside it
                    int xb = right ? b.X1 - 1 : b.X0;
                    // a row has its label at the top (IRS style "1 Name of individual"); an empty band between two
                    // rules is spacing, and words low in the band are a table's column headings
                    double ry0 = a.Y1 / Scale, ry1 = b.Y0 / Scale, rx0 = Math.Max(a.X0, b.X0) / Scale, rx1 = Math.Min(a.X1, b.X1) / Scale;
                    var inBand = labels.Where(c => !string.IsNullOrWhiteSpace(c.Text) && c.Rect.Center.Y > ry0 && c.Rect.Center.Y < ry1
                                                   && c.Rect.Center.X > rx0 && c.Rect.Center.X < rx1).ToList();
                    bool labelled = inBand.Count > 0 && inBand.Min(c => c.Rect.Y0) - ry0 <= 0.4 * (ry1 - ry0);
                    if (Math.Abs(xa - xb) <= tol && labelled && !Closed(xa))
                    {
                        px ??= (byte[])g.Px.Clone();
                        int x = right ? Math.Max(xa, xb) : Math.Min(xa, xb);
                        for (int y = a.Y0; y < b.Y1; y++)
                            for (int d = 0; d < 2; d++)
                                px[y * g.W + Math.Clamp(right ? x - d : x + d, 0, g.W - 1)] = 0;
                    }
                    break;   // only ever up to the next rule below: never across one with other ends

                    // a side already drawn near the end: nothing to add (and a line beside it would cut the cell)
                    bool Closed(int x0)
                    {
                        for (int x = Math.Max(x0 - Px(4), 0); x <= Math.Min(x0 + Px(4), g.W - 1); x++)
                        {
                            int ink = 0;
                            for (int y = a.Y1; y < b.Y0; y++) if (g.Px[y * g.W + x] < InkLum) ink++;
                            if (ink >= 0.5 * (b.Y0 - a.Y1)) return true;    // a box's side, even one that stops short
                        }
                        return false;
                    }
                }
            }
        return px is null ? g : new Gray(px, g.W, g.H);
    }

    /// <summary>
    /// Blanks on underlines outside any cell: "resident of ______ within", "金 ____ 円也", a signature line. A blank
    /// needs words beside it on its line, or (for a short line) a caption under it; the free end of a rule under a
    /// heading is not a blank.
    /// </summary>
    private static IEnumerable<DetectedField> UnderlineBlanks(Gray g, List<HRule> rules, List<PdfChar> labels, List<PdfChar> data,
        List<PdfRect> cells, List<DetectedField> fields, int pno)
    {
        double pageW = g.W / Scale;
        var found = new List<DetectedField>();
        foreach (var u in rules)
        {
            double x0 = u.X0 / Scale, x1 = u.X1 / Scale, y = u.Y0 / Scale;
            if (x1 - x0 < MinBlank || u.Y1 - u.Y0 > Px(2) || Descends(g, u) || Rises(g, u) || Turns(g, u)) continue;
            if (u.Y0 < 0.06 * g.H || u.Y0 > 0.94 * g.H) continue;          // a running header or footer rule

            // label chars standing on this line (in the band above it), also just past its ends
            var onLine = labels.Where(c => !string.IsNullOrWhiteSpace(c.Text) && Overlap(c.Rect.Y0, c.Rect.Y1, y - BlankHeight, y) >= 0.4 * c.Rect.Height
                                           && c.Rect.Y1 <= y + 3 && c.Rect.X1 > x0 - 40 && c.Rect.X0 < x1 + 40).ToList();
            // the whole printed line, for the words that name a blank
            var line = labels.Where(c => !string.IsNullOrWhiteSpace(c.Text) && Overlap(c.Rect.Y0, c.Rect.Y1, y - BlankHeight, y) >= 0.4 * c.Rect.Height
                                         && c.Rect.Y1 <= y + 3).ToList();
            var blocked = new bool[u.Length];
            foreach (var c in onLine)
                for (int x = Math.Max(Px(c.Rect.X0 - 1) - u.X0, 0); x < Math.Min(Px(c.Rect.X1 + 1) - u.X0, u.Length); x++) blocked[x] = true;
            for (int x = 0; x < u.Length; x++)
                for (int yy = Math.Max(u.Y0 - Px(9), 0); yy < u.Y0 - 3 && !blocked[x]; yy++)
                    if (g.Px[yy * g.W + u.X0 + x] < InkLum) blocked[x] = true;   // a divider or a box over the line

            for (int a = 0; a < u.Length;)
            {
                if (blocked[a]) { a++; continue; }
                int b = a;
                while (b < u.Length && !blocked[b]) b++;
                if (b - a >= Px(MinBlank) && Blank(a, b) is { } f) found.Add(f);
                a = b;
            }

            DetectedField? Blank(int a, int b)
            {
                double bx0 = (u.X0 + a) / Scale + 0.5, bx1 = (u.X0 + b) / Scale - 0.5;
                var left = onLine.Where(c => c.Rect.X1 <= bx0 + 1 && c.Rect.X1 >= bx0 - 15).ToList();
                var right = onLine.Where(c => c.Rect.X0 >= bx1 - 1 && c.Rect.X0 <= bx1 + 15).ToList();
                string label = "";
                if (left.Count > 0 && right.Count == 0 && b == u.Length && left.Min(c => c.Rect.X0) >= x0 - 1
                    && onLine.All(c => c.Rect.X0 >= x0 - 1))
                    return null;                                            // a heading standing on its rule
                if (right.Count > 0 && left.Count == 0 && a == 0 && right.Max(c => c.Rect.X1) <= x1 + 1
                    && onLine.All(c => c.Rect.X1 <= x1 + 1))
                    return null;                                            // ... or at the right end of it
                if (left.Count > 0) label = Words(line, bx0, toLeft: true);
                else if (right.Count > 0) label = Words(line, bx1, toLeft: false);
                else
                {
                    // a signature line: the caption printed under it, indented, on a line that is not a page divider
                    var under = labels.Where(c => !string.IsNullOrWhiteSpace(c.Text) && c.Rect.Y0 >= y && c.Rect.Y0 <= y + 14
                                                  && c.Rect.Center.X > bx0 && c.Rect.Center.X < bx1).ToList();
                    if (under.Count == 0 || x1 - x0 > 0.6 * pageW) return null;
                    double top0 = under.Min(c => c.Rect.Y0);
                    var caption = under.Where(c => c.Rect.Y0 < top0 + 4).ToList();
                    if (caption.Max(c => c.Rect.Height) > 12) return null;   // a heading under a box edge, not a caption
                    double cx0 = caption.Min(c => c.Rect.X0), cx1 = caption.Max(c => c.Rect.X1);
                    // centred under the line and narrower than it, like "Signature of applicant" or "Date"
                    if (cx0 < bx0 + 10 || Math.Abs((cx0 + cx1) / 2 - (bx0 + bx1) / 2) > 0.15 * (bx1 - bx0)) return null;
                    label = JoinText(caption.OrderBy(c => c.Rect.X0));
                }

                // under the line above, if it reaches down into the blank
                double top = y - BlankHeight;
                foreach (var c in labels)
                    if (c.Rect.X1 > bx0 && c.Rect.X0 < bx1 && c.Rect.Y1 > top && c.Rect.Y1 <= y - 6) top = c.Rect.Y1 + 0.5;
                var r = new PdfRect(bx0, top, bx1, y - 0.3);
                if (r.Height < 6) return null;
                var value = data.Where(c => !string.IsNullOrWhiteSpace(c.Text) && Inside(c.Rect, r)).ToList();
                // underlined words (a link) fill their line from end to end, give or take a bracket around them
                if (value.Count > 0 && value.Max(c => c.Rect.X1) - value.Min(c => c.Rect.X0) >= 0.85 * (bx1 - bx0)
                    && value.Min(c => c.Rect.X0) <= bx0 + 6 && value.Max(c => c.Rect.X1) >= bx1 - 6)
                    return null;                                            // underlined words (a link), not a blank
                var (cx, cy) = r.Center;
                if (cells.Any(c => Contains(c, (cx, cy)))) return null;      // inside a cell: the cell rules decide
                if (fields.Concat(found).Any(f => f.Rect.Intersect(r) is { Width: > 0, Height: > 0 } i && i.Area > 0.3 * Math.Min(r.Area, f.Rect.Area))) return null;
                return new DetectedField
                {
                    Page = pno, Kind = FieldKind.Text, Rect = r,
                    Prefilled = JoinText(value),
                    Label = Truncate(label, 24), Notes = ["underline"],
                };
            }
        }
        return found;
    }

    /// <summary>Ink running down from the rule (a frame's top edge, a table's grid): not a line to write on.</summary>
    private static bool Descends(Gray g, HRule u)
    {
        int y0 = u.Y1 + 1, y1 = Math.Min(u.Y1 + 1 + Px(4), g.H);
        if (y1 - y0 < Px(4)) return false;
        for (int x = Math.Max(u.X0 - 2, 0); x < Math.Min(u.X1 + 2, g.W); x++)
        {
            bool all = true;
            for (int y = y0; y < y1 && all; y++) all = g.Px[y * g.W + x] < InkLum;
            if (all) return true;
        }
        return false;
    }

    /// <summary>A line rising from the rule (a frame's bottom edge, a header's column divider): part of a table.</summary>
    private static bool Rises(Gray g, HRule u)
    {
        int y1 = u.Y0 - 1, y0 = u.Y0 - 1 - Px(8);
        if (y0 < 0) return false;
        for (int x = Math.Max(u.X0 - 2, 0); x < Math.Min(u.X1 + 2, g.W); x++)
        {
            bool all = true;
            for (int y = y0; y < y1 && all; y++) all = g.Px[y * g.W + x] < InkLum;
            if (all) return true;
        }
        return false;
    }

    /// <summary>
    /// The rule is the top or bottom edge of a box, also a rounded one: at one of its ends, ink carries on in every row
    /// for 6 pt on one side of it only (down from a top edge, up from a bottom edge). A line passing the end on both
    /// sides (a frame beside a worksheet's amount lines) or nothing at all does not count.
    /// </summary>
    private static bool Turns(Gray g, HRule u)
    {
        int inward = Px(2), outward = Px(14), len = Px(6);
        foreach (int end in new[] { u.X0, u.X1 - 1 })
        {
            int xa = end == u.X0 ? end - outward : end - inward, xb = end == u.X0 ? end + inward : end + outward;
            bool down = Run(u.Y1, 1), up = Run(u.Y0 - 1, -1);
            if (down != up) return true;

            bool Run(int from, int dir)
            {
                for (int k = 1; k <= len; k++)
                {
                    int y = from + dir * k;
                    if (y < 0 || y >= g.H) return false;
                    bool any = false;
                    for (int x = Math.Max(xa, 0); x <= Math.Min(xb, g.W - 1) && !any; x++) any = g.Px[y * g.W + x] < InkLum;
                    if (!any) return false;
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>The caption's line carries on past the cell's right edge: a heading over several cells, not a caption.</summary>
    private static bool Spills(List<PdfChar> caption, List<PdfChar> page, PdfRect cell)
    {
        // the words run on without a break: a char just past the cell edge follows a caption char on its own baseline
        // (rows of small print can sit closer than one glyph box, so this goes by char, not by Lines)
        return caption.Any(c0 => page.Any(c => !string.IsNullOrWhiteSpace(c.Text) && c.Rect.Center.X > cell.X1
                                               && Math.Abs(c.Rect.Center.Y - c0.Rect.Center.Y) < 1.5
                                               && c.Rect.X0 >= c0.Rect.X1 - 1 && c.Rect.X0 - c0.Rect.X1 < 4));
    }

    private static double Overlap(double a0, double a1, double b0, double b1) => Math.Min(a1, b1) - Math.Max(a0, b0);

    /// <summary>The words next to a blank on its line: going away from <paramref name="edge"/> while the gaps stay small.</summary>
    private static string Words(List<PdfChar> line, double edge, bool toLeft)
    {
        var side = toLeft ? line.Where(c => c.Rect.X1 <= edge + 1).OrderByDescending(c => c.Rect.X1).ToList()
                          : line.Where(c => c.Rect.X0 >= edge - 1).OrderBy(c => c.Rect.X0).ToList();
        var taken = new List<PdfChar>();
        double at = edge;
        foreach (var c in side)
        {
            double gap = toLeft ? at - c.Rect.X1 : c.Rect.X0 - at;
            if (gap > 12 && taken.Count > 0) break;
            taken.Add(c);
            at = toLeft ? c.Rect.X0 : c.Rect.X1;
            if (taken.Count >= 24) break;
        }
        return JoinText(taken.OrderBy(c => c.Rect.X0)).Trim();
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
