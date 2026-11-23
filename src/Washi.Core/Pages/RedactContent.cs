// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Washi.Core.Pages;

/// <summary>A PDF matrix [a b c d e f]; <c>x.Then(y)</c> applies x first, then y (PDF's "x × y").</summary>
internal readonly record struct Mx(double A, double B, double C, double D, double E, double F)
{
    public static Mx Identity { get; } = new(1, 0, 0, 1, 0, 0);
    public static Mx Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    public Mx Then(Mx m) => new(A * m.A + B * m.C, A * m.B + B * m.D, C * m.A + D * m.C, C * m.B + D * m.D,
                                E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);

    public (double X, double Y) Apply(double x, double y) => (x * A + y * C + E, x * B + y * D + F);

    public Mx? Inverse()
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return null;
        return new(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }

    /// <summary>The bounds of the rectangle <paramref name="r"/> once transformed.</summary>
    public Box Bounds(Box r)
    {
        var p = new[] { Apply(r.X0, r.Y0), Apply(r.X1, r.Y0), Apply(r.X0, r.Y1), Apply(r.X1, r.Y1) };
        return new(p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X), p.Max(q => q.Y));
    }

    public static Mx From(IReadOnlyList<double> v) => v.Count >= 6 ? new(v[0], v[1], v[2], v[3], v[4], v[5]) : Identity;
}

/// <summary>An axis-aligned rectangle in PDF space (y up).</summary>
internal readonly record struct Box(double X0, double Y0, double X1, double Y1)
{
    public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    public bool Contains(Box b) => b.X0 >= X0 - 0.01 && b.X1 <= X1 + 0.01 && b.Y0 >= Y0 - 0.01 && b.Y1 <= Y1 + 0.01;
    public bool Overlaps(Box b) => Math.Min(X1, b.X1) - Math.Max(X0, b.X0) > 0.01 && Math.Min(Y1, b.Y1) - Math.Max(Y0, b.Y0) > 0.01;
    public Box Grow(double d) => new(X0 - d, Y0 - d, X1 + d, Y1 + d);
    public Box Union(Box b) => new(Math.Min(X0, b.X0), Math.Min(Y0, b.Y0), Math.Max(X1, b.X1), Math.Max(Y1, b.Y1));
}

/// <summary>What one redaction pass removed.</summary>
internal sealed class RedactStats
{
    public int Glyphs, Runs, Paths, Images, ImagesRemoved, Tags;
    /// <summary>Areas (page PDF space) that lost content beyond the boxes asked for: blanked image pixels.</summary>
    public List<Box> Covered { get; } = [];
}

/// <summary>Everything one page's redaction shares: the boxes, the document, caches.</summary>
internal sealed class RedactContext(PdfDocument doc, IReadOnlyList<Box> boxes, Func<string, bool>? matches)
{
    public PdfDocument Doc { get; } = doc;
    public IReadOnlyList<Box> Boxes { get; } = boxes;
    /// <summary>True for text that must not stay (marked-content /ActualText, /Alt, /E).</summary>
    public Func<string, bool>? Matches { get; } = matches;
    public RedactStats Stats { get; } = new();
    /// <summary>Only text goes (editing text): drawings, images and forms' other content stay.</summary>
    public bool TextOnly { get; init; }
    /// <summary>Editing text: content drawn in page space right after the text object whose glyphs inside its box went.</summary>
    public IReadOnlyList<TextInsert> Inserts { get; init; } = [];
    /// <summary>Editing text: shapes drawn entirely inside one of these go even when only text is removed (an underline that moves with its words).</summary>
    public IReadOnlyList<Box> PathBoxes { get; init; } = [];
    public Dictionary<PdfDictionary, FontMetrics> Fonts { get; } = new(ReferenceEqualityComparer.Instance);

    public bool AnyOverlap(Box b) => Boxes.Any(x => x.Overlaps(b));
    public bool InsideOne(Box b) => Boxes.Any(x => x.Contains(b));
    public bool ContainsPoint(double x, double y) => Boxes.Any(b => b.Contains(x, y));
}

/// <summary>New content (in page space) that takes the place of the glyphs removed inside <see cref="Box"/>.</summary>
internal sealed class TextInsert(Box box, string draw)
{
    public Box Box { get; } = box;
    public string Draw { get; } = draw;
    public bool Pending { get; set; }
    public bool Placed { get; set; }
}

/// <summary>
/// The resources of one content stream. Adding an XObject first gives the stream its own copy of the dictionary, so
/// pages or forms that shared it are not changed.
/// </summary>
internal sealed class ResourceScope(PdfDocument doc, PdfDictionary? resources, Action<PdfDictionary> usePrivate)
{
    private PdfDictionary? _private;
    private int _next;
    private readonly HashSet<string> _replaced = new(StringComparer.Ordinal);

    /// <summary>A name whose drawing was replaced by a copy (it may now be unused).</summary>
    public void Replaced(string name) => _replaced.Add(name);

    /// <summary>
    /// Removes the replaced names that <paramref name="content"/> no longer draws from this stream's own dictionary, so the
    /// originals (which still hold what was redacted) are not saved with it.
    /// </summary>
    public void DropUnused(byte[] content)
    {
        if (_private is null || _replaced.Count == 0 || _private.Elements["/XObject"] is not PdfDictionary xobjects) return;
        var used = new ContentStream(content).Ops.Where(o => o.Name == "Do" && o.Names.Count > 0).Select(o => "/" + o.Names[^1]).ToHashSet(StringComparer.Ordinal);
        foreach (var name in _replaced) if (!used.Contains(name)) xobjects.Elements.Remove(name);
    }

    public PdfDictionary? Dict => _private ?? resources;
    public bool Changed => _private is not null;

    public PdfDictionary? Get(string category, string name) =>
        Deref(Dict?.Elements[category]) is PdfDictionary c ? Deref(c.Elements[name]) as PdfDictionary : null;

    public PdfItem? GetItem(string category, string name) =>
        Deref(Dict?.Elements[category]) is PdfDictionary c ? c.Elements[name] : null;

    public string AddXObject(PdfDictionary obj)
    {
        MakePrivate();
        var xobjects = (PdfDictionary)_private!.Elements["/XObject"]!;
        string name;
        do name = "/WashiR" + (++_next).ToString(CultureInfo.InvariantCulture);
        while (xobjects.Elements.ContainsKey(name));
        xobjects.Elements[name] = obj.Reference;
        return name;
    }

    /// <summary>Gives the stream its own copy of the dictionary (and of its XObjects), once.</summary>
    public void MakePrivate()
    {
        if (_private is null)
        {
            _private = resources is null ? new PdfDictionary(doc) : (PdfDictionary)resources.Clone();
            var x = Deref(_private.Elements["/XObject"]) as PdfDictionary;
            _private.Elements["/XObject"] = x is null ? new PdfDictionary(doc) : (PdfDictionary)x.Clone();
            usePrivate(_private);
        }
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}

public sealed class RedactException(string message, string? key = null, params object[] args) : Exception(message), Washi.Core.Localization.ILocalizedError
{
    public string? Key { get; } = key;
    public object[] Args { get; } = args;
}

/// <summary>
/// Removes what a content stream draws inside the boxes, editing only the operators concerned (every other byte is
/// copied). Text: each glyph whose centre lies in a box is taken out of its string and replaced by a kerning number of
/// the same width, so nothing else on the line moves. When a width cannot be known exactly, the whole run goes, with
/// every run chained to it (the caller covers what that removes). Images: the pixels under the boxes are blanked in a
/// copy of the image (a whole image inside a box is dropped). Paths entirely inside a box are not painted. Forms are
/// redacted recursively, each use in its own copy.
/// </summary>
internal sealed class ContentRedactor(RedactContext ctx)
{
    private sealed class GState
    {
        public Mx Ctm = Mx.Identity;
        public FontMetrics Font = FontMetrics.Fallback;
        public double Size, Tc, Tw, Th = 1, TL, Ts, Tr;
        public GState Copy() => (GState)MemberwiseClone();
    }

    private const int MaxDepth = 12;
    private readonly List<(double X, double Y)> _removed = [];   // centres of the glyphs the last text operator lost

    /// <summary>The redacted bytes, or null when nothing changed (resources may still have been given new entries).</summary>
    public byte[]? Run(byte[] bytes, ResourceScope res, Mx ctm, int depth = 0)
    {
        if (depth > MaxDepth) throw new RedactException("forms nested too deeply", "Err_RedactNested");
        var content = new ContentStream(bytes);
        var edits = new List<(int Start, int End, string With)>();
        var gs = new GState { Ctm = ctm };
        var stack = new Stack<GState>();
        Mx tm = Mx.Identity, tlm = Mx.Identity;
        bool killChain = false;      // a run was removed whole: the runs placed after it go too
        bool reliable = true;        // the pen position is exact (no estimated width since the last placement)
        var path = new List<(double X, double Y)>();
        var pathOps = new List<int>();
        bool clips = false;

        for (int k = 0; k < content.Ops.Count; k++)
        {
            var op = content.Ops[k];
            switch (op.Name)
            {
                case "q": stack.Push(gs.Copy()); break;
                case "Q": if (stack.Count > 0) gs = stack.Pop(); break;
                case "cm": gs.Ctm = Mx.From(Numbers(op)).Then(gs.Ctm); break;
                case "BT": tm = tlm = Mx.Identity; Placed(); break;
                case "ET": Placed(); if (depth == 0) Insert(op.End, gs.Ctm); break;
                case "Tc": gs.Tc = Num(op, 0); break;
                case "Tw": gs.Tw = Num(op, 0); break;
                case "Tz": gs.Th = Num(op, 0) / 100; break;
                case "TL": gs.TL = Num(op, 0); break;
                case "Ts": gs.Ts = Num(op, 0); break;
                case "Tr": gs.Tr = Num(op, 0); break;
                case "Tf":
                    gs.Size = Num(op, 0);
                    gs.Font = op.Names.Count > 0 ? Font(res.Get("/Font", "/" + op.Names[^1])) : FontMetrics.Fallback;
                    break;
                case "gs" when op.Names.Count > 0:
                    if (res.Get("/ExtGState", "/" + op.Names[^1]) is { } eg && Deref(eg.Elements["/Font"]) is PdfArray fa && fa.Elements.Count >= 2)
                    {
                        gs.Font = Font(Deref(fa.Elements[0]) as PdfDictionary);
                        gs.Size = FontMetrics.Num(fa.Elements[1]);
                    }
                    break;
                case "Td": tlm = Mx.Translate(Num(op, 0), Num(op, 1)).Then(tlm); tm = tlm; Placed(); break;
                case "TD": gs.TL = -Num(op, 1); tlm = Mx.Translate(Num(op, 0), Num(op, 1)).Then(tlm); tm = tlm; Placed(); break;
                case "Tm": tlm = tm = Mx.From(Numbers(op)); Placed(); break;
                case "T*": tlm = Mx.Translate(0, -gs.TL).Then(tlm); tm = tlm; Placed(); break;
                case "Tj" or "TJ" or "'" or "\"":
                    ShowText(content, op, gs, ref tm, ref tlm, ref killChain, ref reliable, edits, depth);
                    if (depth == 0)
                        foreach (var ins in ctx.Inserts)
                            if (!ins.Placed && _removed.Any(h => ins.Box.Contains(h.X, h.Y))) ins.Pending = true;
                    _removed.Clear();
                    break;
                case "m" or "l":
                    if (op.Numbers.Count >= 2) path.Add(gs.Ctm.Apply(Num(op, 0), Num(op, 1)));
                    pathOps.Add(k);
                    break;
                case "c" or "v" or "y":
                    var cn = Numbers(op);
                    for (int i = 0; i + 1 < cn.Count; i += 2) path.Add(gs.Ctm.Apply(cn[i], cn[i + 1]));
                    pathOps.Add(k);
                    break;
                case "re" when op.Numbers.Count >= 4:
                    double x = Num(op, 0), y = Num(op, 1), w = Num(op, 2), h = Num(op, 3);
                    path.AddRange([gs.Ctm.Apply(x, y), gs.Ctm.Apply(x + w, y), gs.Ctm.Apply(x, y + h), gs.Ctm.Apply(x + w, y + h)]);
                    pathOps.Add(k);
                    break;
                case "h": pathOps.Add(k); break;
                case "W" or "W*": clips = true; break;
                case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                    if (path.Count > 0)
                    {
                        var b = new Box(path.Min(p => p.X), path.Min(p => p.Y), path.Max(p => p.X), path.Max(p => p.Y));
                        if (!ctx.TextOnly && ctx.InsideOne(b) || ctx.PathBoxes.Any(x => x.Contains(b)))
                        {
                            // the shape goes with its painting (a drawn signature must not stay as coordinates);
                            // a path that also clips keeps its outline, unpainted
                            if (!clips) foreach (var po in pathOps) edits.Add((content.Ops[po].Start, content.Ops[po].End, ""));
                            edits.Add((op.Start, op.End, clips ? "n" : ""));
                            ctx.Stats.Paths++;
                        }
                    }
                    path.Clear(); pathOps.Clear(); clips = false;
                    break;
                case "n": path.Clear(); pathOps.Clear(); clips = false; break;
                case "Do" when op.Names.Count > 0:
                    DoXObject(op, res, gs.Ctm, depth, edits);
                    break;
                case "BI" when !ctx.TextOnly:
                    var ib = gs.Ctm.Bounds(new Box(0, 0, 1, 1));
                    if (!ctx.AnyOverlap(ib)) break;
                    if (ctx.InsideOne(ib)) { edits.Add((op.Start, op.End, "")); ctx.Stats.ImagesRemoved++; break; }
                    throw new RedactException("an inline image lies partly under a redaction area; draw the area over the whole image",
                                              "Err_RedactInlineImage");
                case "BDC" or "DP" when ctx.Matches is not null:
                    var operands = content.Operands(op);
                    if (operands.Count >= 2 && operands[0] is string tag && operands[1] is Dictionary<string, object> props && Says(props))
                    {
                        edits.Add((op.Start, op.End, tag + (op.Name == "BDC" ? " BMC" : " MP")));
                        ctx.Stats.Tags++;
                    }
                    break;
            }
        }
        if (depth == 0) Insert(bytes.Length, gs.Ctm);   // text left open at the end
        return edits.Count == 0 ? null : Apply(bytes, edits);

        void Placed() { killChain = false; reliable = true; }

        void Insert(int at, Mx ctm)
        {
            foreach (var ins in ctx.Inserts.Where(i => i.Pending && !i.Placed))
            {
                if (ctm.Inverse() is not { } inv) return;
                var undo = inv == Mx.Identity ? "" : Matrix(inv) + " cm ";
                edits.Add((at, at, "q " + undo + ins.Draw + " Q"));
                ins.Placed = true;
            }
        }
    }

    private FontMetrics Font(PdfDictionary? font)
    {
        if (font is null) return FontMetrics.Fallback;
        if (!ctx.Fonts.TryGetValue(font, out var m)) ctx.Fonts[font] = m = FontMetrics.From(font);
        return m;
    }

    private bool Says(Dictionary<string, object> props) =>
        props.Values.Any(v => v is byte[] b && ctx.Matches!(TextString(b)) || v is Dictionary<string, object> d && Says(d));

    internal static string TextString(byte[] b) =>
        b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2)
        : b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? Encoding.UTF8.GetString(b, 3, b.Length - 3)
        : Encoding.Latin1.GetString(b);

    // ------------------------------------------------------------------ text

    private void ShowText(ContentStream content, ContentStream.Op op, GState gs, ref Mx tm, ref Mx tlm, ref bool killChain,
                          ref bool reliable, List<(int, int, string)> edits, int depth)
    {
        var operands = content.Operands(op);
        string prefix = "";
        if (op.Name == "'" || op.Name == "\"")
        {
            if (op.Name == "\"" && operands.Count >= 3 && operands[0] is double aw && operands[1] is double ac)
            {
                gs.Tw = aw; gs.Tc = ac;
                prefix = $"{N(aw)} Tw {N(ac)} Tc ";
            }
            tlm = Mx.Translate(0, -gs.TL).Then(tlm);
            tm = tlm;
            killChain = false; reliable = true;
            prefix += "T* ";
        }
        var elements = op.Name == "TJ"
            ? operands.LastOrDefault() as List<object> ?? []
            : operands.LastOrDefault() is byte[] one ? [one] : [];

        var font = gs.Font;
        double size = gs.Size, th = gs.Th;
        bool vertical = font.Vertical;
        // pass 1: where each glyph is, and which are inside a box
        var glyphs = new List<(int Element, int Start, int Length, double Advance, bool Known, bool Inside)>();
        var centres = new List<(double X, double Y)>();
        var startTm = tm;
        var m = tm;
        bool allKnown = reliable;
        bool touched = false;
        for (int e = 0; e < elements.Count; e++)
        {
            if (elements[e] is double kern)
            {
                m = (vertical ? Mx.Translate(0, -kern / 1000 * size) : Mx.Translate(-kern / 1000 * size * th, 0)).Then(m);
                continue;
            }
            if (elements[e] is not byte[] s) continue;
            foreach (var (start, len, code) in font.Codes(s))
            {
                var (w0, known) = font.Width(code, len);
                double tw = len == 1 && code == 32 ? gs.Tw : 0;
                var trm = new Mx(size * th, 0, 0, size, 0, gs.Ts).Then(m).Then(gs.Ctm);
                bool inside;
                double advance;
                if (vertical)
                {
                    double w1 = font.VerticalAdvance(code, len);
                    var (cx, cy) = trm.Apply(0, w1 / 2);
                    inside = ctx.ContainsPoint(cx, cy);
                    advance = w1 * size + gs.Tc + tw;
                    m = Mx.Translate(0, advance).Then(m);
                }
                else
                {
                    var (cx, cy) = trm.Apply(w0 / 2, (font.Ascent + font.Descent) / 2);
                    inside = ctx.ContainsPoint(cx, cy);
                    centres.Add((cx, cy));
                    advance = (w0 * size + gs.Tc + tw) * th;
                    m = Mx.Translate(advance, 0).Then(m);
                }
                touched |= inside;
                allKnown &= known;
                if (inside && ctx.Inserts.Count > 0 && !vertical) _removed.Add(centres[^1]);
                glyphs.Add((e, start, len, advance, known, inside));
            }
        }
        tm = m;
        if (!allKnown) reliable = false;

        if (killChain || touched && !allKnown || touched && size == 0)
        {
            // the run goes whole; runs placed after it in the same line go with it (their pen position depends on it)
            if (glyphs.Count == 0 && !killChain) return;
            edits.Add((op.Start, op.End, prefix.TrimEnd()));
            ctx.Stats.Glyphs += glyphs.Count;
            ctx.Stats.Runs++;
            killChain = true;
            return;
        }
        if (!glyphs.Any(g => g.Inside)) return;

        // pass 2: the same run with the glyphs inside replaced by kerning of their width
        var sb = new StringBuilder("[");
        var hex = new StringBuilder();
        double pending = 0;
        int gi = 0;
        // editing text: where the pen is, from the start of the line (Tlm), so the text object can be split there
        double x = 0;
        Mx? undo = null;
        if (depth == 0 && ctx.Inserts.Count > 0 && !vertical && size != 0 && th != 0 && gs.Tr < 4 && tlm.Inverse() is { } tlmInv
            && startTm.Then(tlmInv) is var off && Math.Abs(off.A - 1) < 1e-6 && Math.Abs(off.B) < 1e-6 && Math.Abs(off.C) < 1e-6
            && Math.Abs(off.D - 1) < 1e-6 && Math.Abs(off.F) < 1e-6)
        {
            x = off.E;
            undo = gs.Ctm.Inverse();
        }
        var gone = new List<(double X, double Y)>();
        var lineMatrix = tlm;
        for (int e = 0; e < elements.Count; e++)
        {
            if (elements[e] is double kern) { pending += kern; x -= kern / 1000 * size * th; continue; }
            if (elements[e] is not byte[] s) continue;
            for (; gi < glyphs.Count && glyphs[gi].Element == e; gi++)
            {
                var g = glyphs[gi];
                if (g.Inside)
                {
                    // the same displacement as the glyph: -n/1000 × size (× Th horizontally) = advance
                    pending += vertical ? -g.Advance * 1000 / size : -g.Advance / th * 1000 / size;
                    ctx.Stats.Glyphs++;
                    x += g.Advance;
                    if (!vertical) gone.Add(centres[gi]);
                    continue;
                }
                if (gone.Count > 0) Split();
                if (Math.Abs(pending) > 1e-9) { Flush(); sb.Append(N(pending)).Append(' '); pending = 0; }
                for (int b = 0; b < g.Length; b++) hex.Append(s[g.Start + b].ToString("X2", CultureInfo.InvariantCulture));
                x += g.Advance;
            }
        }
        if (gone.Count > 0) Split();
        Flush();
        if (Math.Abs(pending) > 1e-9) sb.Append(N(pending));
        sb.Append("] TJ");
        edits.Add((op.Start, op.End, prefix + sb.ToString().Replace(" ]", "]")));
        ctx.Stats.Runs++;

        // the new text where the old went: the text object ends, the new text is drawn in page space, and a new text
        // object goes on from the same pen position (the line matrix again, then a kerning to where the pen was)
        void Split()
        {
            var ins = undo is null ? null : ctx.Inserts.FirstOrDefault(i => !i.Placed && gone.Any(h => i.Box.Contains(h.X, h.Y)));
            gone.Clear();
            if (ins is null || undo is not { } u) return;
            Flush();
            sb.Append("] TJ ET q ").Append(Matrix(u)).Append(" cm ").Append(ins.Draw).Append(" Q BT ").Append(Matrix(lineMatrix)).Append(" Tm [");
            pending = -x * 1000 / (size * th);
            ins.Placed = true;
        }

        void Flush()
        {
            if (hex.Length == 0) return;
            sb.Append('<').Append(hex).Append("> ");
            hex.Clear();
        }
    }

    // ------------------------------------------------------------------ XObjects

    private void DoXObject(ContentStream.Op op, ResourceScope res, Mx ctm, int depth, List<(int, int, string)> edits)
    {
        var name = "/" + op.Names[^1];
        if (res.Get("/XObject", name) is not { } x) return;
        var subtype = x.Elements.GetName("/Subtype");
        if (subtype == "/Image")
        {
            if (ctx.TextOnly) return;
            var bounds = ctm.Bounds(new Box(0, 0, 1, 1));
            if (!ctx.AnyOverlap(bounds)) return;
            if (ctx.InsideOne(bounds))
            {
                edits.Add((op.Start, op.End, ""));
                ctx.Stats.ImagesRemoved++;
                res.MakePrivate();
                res.Replaced(name);
                return;
            }
            if (ImageBlanker.Blank(ctx.Doc, x, ctm, ctx.Boxes, ctx.Stats.Covered) is not { } blanked) return;
            edits.Add((op.Start, op.End, res.AddXObject(blanked) + " Do"));
            res.Replaced(name);
            ctx.Stats.Images++;
            return;
        }
        if (subtype != "/Form" || x.Stream is null) return;
        var fm = Deref(x.Elements["/Matrix"]) is PdfArray ma ? Mx.From([.. ma.Elements.Select(FontMetrics.Num)]) : Mx.Identity;
        var full = fm.Then(ctm);
        if (ctx.Matches is null)
        {
            var bbox = Deref(x.Elements["/BBox"]) is PdfArray ba && ba.Elements.Count >= 4
                ? new Box(FontMetrics.Num(ba.Elements[0]), FontMetrics.Num(ba.Elements[1]), FontMetrics.Num(ba.Elements[2]), FontMetrics.Num(ba.Elements[3]))
                : (Box?)null;
            if (bbox is { } bb && !ctx.AnyOverlap(full.Bounds(new Box(Math.Min(bb.X0, bb.X1), Math.Min(bb.Y0, bb.Y1), Math.Max(bb.X0, bb.X1), Math.Max(bb.Y0, bb.Y1)))))
                return;
        }
        PdfDictionary? own = Deref(x.Elements["/Resources"]) as PdfDictionary;
        PdfDictionary? newResources = null;
        var scope = new ResourceScope(ctx.Doc, own ?? res.Dict, d => newResources = d);
        var bytes = new ContentRedactor(ctx).Run(x.Stream.UnfilteredValue, scope, full, depth + 1);
        if (bytes is null && newResources is null) return;
        bytes ??= x.Stream.UnfilteredValue;
        scope.DropUnused(bytes);
        var copy = (PdfDictionary)x.Clone();
        SetStream(copy, bytes);
        if (newResources is not null) copy.Elements["/Resources"] = newResources;
        ctx.Doc.Internals.AddObject(copy);
        edits.Add((op.Start, op.End, res.AddXObject(copy) + " Do"));
        res.Replaced(name);
    }

    /// <summary>Replaces a stream's data with <paramref name="bytes"/>, Flate-compressed.</summary>
    internal static void SetStream(PdfDictionary d, byte[] bytes)
    {
        var packed = new PdfSharp.Pdf.Filters.FlateDecode().Encode(bytes, PdfFlateEncodeMode.Default);
        if (d.Stream is null) d.CreateStream(packed); else d.Stream.Value = packed;
        d.Elements["/Filter"] = new PdfName("/FlateDecode");
        d.Elements.Remove("/DecodeParms");
        d.Elements["/Length"] = new PdfInteger(packed.Length);
    }

    // ------------------------------------------------------------------ helpers

    internal static byte[] Apply(byte[] source, List<(int Start, int End, string With)> edits)
    {
        using var ms = new MemoryStream(source.Length + 64);
        int at = 0;
        foreach (var (start, end, with) in edits.OrderBy(e => e.Start))
        {
            if (start < at) continue;
            ms.Write(source, at, start - at);
            if (with.Length > 0) ms.Write(Encoding.ASCII.GetBytes(" " + with + " "));
            at = end;
        }
        ms.Write(source, at, source.Length - at);
        return ms.ToArray();
    }

    private static List<double> Numbers(ContentStream.Op op) =>
        [.. op.Numbers.Select(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)];

    private static double Num(ContentStream.Op op, int i) =>
        i < op.Numbers.Count && double.TryParse(op.Numbers[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string Matrix(Mx m) => string.Join(' ', new[] { m.A, m.B, m.C, m.D, m.E, m.F }
        .Select(v => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture)));

    internal static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
