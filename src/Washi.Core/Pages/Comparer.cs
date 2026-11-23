// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;
using Washi.Core.Localization;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

public enum ChangeKind { Inserted, Deleted, Changed, Visual, PageAdded, PageRemoved }

/// <summary>
/// One difference between two versions of a document. Pages are 0-based; boxes are upright points on their page. A
/// deletion's <see cref="NewBoxes"/> holds the place in the new version where the text was.
/// </summary>
public sealed record PdfChange(ChangeKind Kind, int? OldPage, int? NewPage, string OldText, string NewText,
                               IReadOnlyList<PdfRect> OldBoxes, IReadOnlyList<PdfRect> NewBoxes);

/// <param name="Pairs">Which page of the old version became which page of the new one (null: added or removed).</param>
public sealed record CompareReport(int OldPages, int NewPages, IReadOnlyList<(int? Old, int? New)> Pairs, IReadOnlyList<PdfChange> Changes)
{
    public bool Same => Changes.Count == 0;
    public int Count(ChangeKind kind) => Changes.Count(c => c.Kind == kind);
}

/// <summary>
/// Compares two versions of a document: pages are paired by their text (an inserted or removed page does not shift the
/// rest), the text of each pair is compared word by word (each CJK character is a word), and the pages are rendered to
/// find what changed outside the text (images, drawings, stamps). Full-width and half-width forms count as the same.
/// Callers hold <see cref="PdfiumDocument.Gate"/>.
/// </summary>
public static class PdfComparer
{
    private sealed record Token(string Key, string Raw, PdfRect Box, bool Word);
    private sealed record PageText(List<Token> Tokens, double Width, double Height);

    public static CompareReport Compare(string oldPath, string newPath, CancellationToken cancel = default)
    {
        PdfiumDocument.EnsureInit();
        var a = Read(oldPath, cancel);
        var b = Read(newPath, cancel);
        var pairs = Pair(a, b);
        var changes = new List<PdfChange>();
        using var da = PdfiumDocument.Open(oldPath);
        using var db = PdfiumDocument.Open(newPath);
        int lastNew = 0;
        foreach (var (oi, ni) in pairs)
        {
            cancel.ThrowIfCancellationRequested();
            if (oi is { } o && ni is null)
            {
                changes.Add(new PdfChange(ChangeKind.PageRemoved, o, Math.Min(lastNew, Math.Max(0, b.Count - 1)), Snippet(a[o]), "", [Whole(a[o])], []));
                continue;
            }
            if (ni is { } n0 && oi is null)
            {
                changes.Add(new PdfChange(ChangeKind.PageAdded, null, n0, "", Snippet(b[n0]), [], [Whole(b[n0])]));
                lastNew = n0;
                continue;
            }
            int op = oi!.Value, np = ni!.Value;
            lastNew = np;
            changes.AddRange(TextChanges(a[op], b[np], op, np));
            changes.AddRange(VisualChanges(da, db, a[op], b[np], op, np));
        }
        return new CompareReport(a.Count, b.Count, pairs, changes);
    }

    // ------------------------------------------------------------------ text

    private static List<PageText> Read(string path, CancellationToken cancel)
    {
        var pages = new List<PageText>();
        var doc = PdfEraser.Load(path);
        try
        {
            int count = fpdfview.FPDF_GetPageCount(doc);
            for (int p = 0; p < count; p++)
            {
                cancel.ThrowIfCancellationRequested();
                PdfEraser.WithPage(doc, p, (page, map) =>
                {
                    double w = fpdfview.FPDF_GetPageWidthF(page), h = fpdfview.FPDF_GetPageHeightF(page);
                    var size = map.ToDevice(0, 0, w, h);
                    pages.Add(new PageText(Tokens(page, map), size.Width, size.Height));
                });
            }
        }
        finally { fpdfview.FPDF_CloseDocument(doc); }
        return pages;
    }

    private static List<Token> Tokens(FpdfPageT page, PdfiumDocument.PageMapper map)
    {
        var tokens = new List<Token>();
        var tp = fpdf_text.FPDFTextLoadPage(page);
        try
        {
            int n = fpdf_text.FPDFTextCountChars(tp);
            var box = new FS_RECTF_();
            var word = new StringBuilder();
            PdfRect wordBox = default;
            void Close()
            {
                if (word.Length == 0) return;
                var raw = word.ToString();
                tokens.Add(new Token(raw.Normalize(NormalizationForm.FormKC), raw, wordBox, true));
                word.Clear();
            }
            for (int i = 0; i < n; i++)
            {
                uint u = fpdf_text.FPDFTextGetUnicode(tp, i);
                if (fpdf_text.FPDFTextIsGenerated(tp, i) == 1 || u is 0 or 0xFFFE or 0xFFFF or > 0x10FFFF || u is >= 0xD800 and <= 0xDFFF) { Close(); continue; }
                var s = char.ConvertFromUtf32((int)u);
                if (string.IsNullOrWhiteSpace(s) || fpdf_text.FPDFTextGetLooseCharBox(tp, i, box) == 0) { Close(); continue; }
                var r = map.ToDevice(box.Left, box.Top, box.Right, box.Bottom);
                var cat = CharUnicodeInfo.GetUnicodeCategory(s, 0);
                bool letter = cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                              or UnicodeCategory.ModifierLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.NonSpacingMark
                              or UnicodeCategory.SpacingCombiningMark || s is "." or "," && word.Length > 0 && char.IsDigit(word[^1]);
                if (letter && !Cjk(u))
                {
                    // letters and digits run on into a word (a point or comma between digits too: 1,200.50)
                    if (word.Length > 0 && (Math.Abs(r.Center.Y - wordBox.Center.Y) > wordBox.Height * 0.6 || r.X0 < wordBox.X0 - 1)) Close();
                    wordBox = word.Length == 0 ? r : Union(wordBox, r);
                    word.Append(s);
                    continue;
                }
                Close();
                tokens.Add(new Token(s.Normalize(NormalizationForm.FormKC), s, r, false));   // a CJK character or a sign: a word of its own
            }
            Close();
        }
        finally { fpdf_text.FPDFTextClosePage(tp); }
        return tokens;
    }

    private static bool Cjk(uint u) => u is >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
                                       or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2FFFF;

    /// <summary>Pages of the two versions paired in order, each pair as alike as their text allows.</summary>
    private static List<(int? Old, int? New)> Pair(List<PageText> a, List<PageText> b)
    {
        int n = a.Count, m = b.Count;
        var sim = new double[n, m];
        var bags = b.Select(Bag).ToList();
        for (int i = 0; i < n; i++)
        {
            var bag = Bag(a[i]);
            for (int j = 0; j < m; j++) sim[i, j] = Similarity(bag, bags[j]);
        }
        // pages may pair when their text is alike, or, in versions of the same length, when they are in the same place
        bool Allowed(int i, int j) => sim[i, j] >= 0.2 || n == m && i == j;
        var score = new double[n + 1, m + 1];
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                double best = Math.Max(score[i - 1, j], score[i, j - 1]);
                if (Allowed(i - 1, j - 1)) best = Math.Max(best, score[i - 1, j - 1] + 0.01 + sim[i - 1, j - 1]);
                score[i, j] = best;
            }
        var pairs = new List<(int?, int?)>();
        int x = n, y = m;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && Allowed(x - 1, y - 1) && Math.Abs(score[x, y] - (score[x - 1, y - 1] + 0.01 + sim[x - 1, y - 1])) < 1e-9)
            { pairs.Add((x - 1, y - 1)); x--; y--; }
            else if (x > 0 && (y == 0 || score[x - 1, y] >= score[x, y - 1])) { pairs.Add((x - 1, null)); x--; }
            else { pairs.Add((null, y - 1)); y--; }
        }
        pairs.Reverse();
        return pairs;
    }

    private static Dictionary<string, int> Bag(PageText p)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in p.Tokens) d[t.Key] = d.GetValueOrDefault(t.Key) + 1;
        return d;
    }

    private static double Similarity(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1;   // two pages without text (blank or scanned): alike
        int common = 0, total = 0;
        foreach (var (k, v) in a) { int w = b.GetValueOrDefault(k); common += Math.Min(v, w); total += Math.Max(v, w); }
        foreach (var (k, w) in b) if (!a.ContainsKey(k)) total += w;
        return total == 0 ? 0 : common / (double)total;
    }

    private enum Op : byte { Same, Delete, Insert }

    /// <summary>The edit script from <paramref name="a"/> to <paramref name="b"/> (longest common subsequence).</summary>
    private static List<Op> Diff(List<Token> a, List<Token> b)
    {
        // the common start and end first; the middle by dynamic programming (when it is not too large)
        int start = 0;
        while (start < a.Count && start < b.Count && a[start].Key == b[start].Key) start++;
        int ea = a.Count, eb = b.Count;
        while (ea > start && eb > start && a[ea - 1].Key == b[eb - 1].Key) { ea--; eb--; }
        int n = ea - start, m = eb - start;
        var ops = new List<Op>(a.Count + b.Count);
        ops.AddRange(Enumerable.Repeat(Op.Same, start));
        if ((long)n * m > 16_000_000)
        {
            // too different to compare word by word: all of the middle changed
            ops.AddRange(Enumerable.Repeat(Op.Delete, n));
            ops.AddRange(Enumerable.Repeat(Op.Insert, m));
        }
        else if (n > 0 || m > 0)
        {
            var len = new int[2, m + 1];
            var dir = new byte[(n + 1) * (m + 1)];
            for (int i = n - 1; i >= 0; i--)
            {
                int cur = i & 1, next = cur ^ 1;
                len[cur, m] = 0;
                for (int j = m - 1; j >= 0; j--)
                {
                    if (a[start + i].Key == b[start + j].Key) { len[cur, j] = len[next, j + 1] + 1; dir[i * (m + 1) + j] = 0; }
                    else if (len[next, j] >= len[cur, j + 1]) { len[cur, j] = len[next, j]; dir[i * (m + 1) + j] = 1; }
                    else { len[cur, j] = len[cur, j + 1]; dir[i * (m + 1) + j] = 2; }
                }
            }
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x == n) { ops.Add(Op.Insert); y++; }
                else if (y == m) { ops.Add(Op.Delete); x++; }
                else
                    switch (dir[x * (m + 1) + y])
                    {
                        case 0: ops.Add(Op.Same); x++; y++; break;
                        case 1: ops.Add(Op.Delete); x++; break;
                        default: ops.Add(Op.Insert); y++; break;
                    }
            }
        }
        ops.AddRange(Enumerable.Repeat(Op.Same, a.Count - ea));
        return ops;
    }

    private static IEnumerable<PdfChange> TextChanges(PageText a, PageText b, int op, int np)
    {
        var ops = Diff(a.Tokens, b.Tokens);
        int i = 0, j = 0, k = 0;
        while (k < ops.Count)
        {
            if (ops[k] == Op.Same) { i++; j++; k++; continue; }
            var gone = new List<Token>();
            var come = new List<Token>();
            int goneAt = i, anchor = j;
            while (k < ops.Count && ops[k] != Op.Same)
            {
                if (ops[k] == Op.Delete) gone.Add(a.Tokens[i++]); else come.Add(b.Tokens[j++]);
                k++;
            }
            // words removed at the end of a line and a line added after it are two changes, not one
            if (gone.Count > 0 && come.Count > 0 && NewLine(a.Tokens, goneAt) != NewLine(b.Tokens, anchor))
            {
                yield return new PdfChange(ChangeKind.Deleted, op, np, Join(gone), "", Lines(gone), [Where(b, anchor)]);
                yield return new PdfChange(ChangeKind.Inserted, op, np, "", Join(come), [], Lines(come));
                continue;
            }
            var kind = gone.Count == 0 ? ChangeKind.Inserted : come.Count == 0 ? ChangeKind.Deleted : ChangeKind.Changed;
            IReadOnlyList<PdfRect> newBoxes = come.Count > 0 ? Lines(come) : [Where(b, anchor)];
            yield return new PdfChange(kind, op, np, Join(gone), Join(come), Lines(gone), newBoxes);
        }
    }

    /// <summary>Whether the word at <paramref name="i"/> starts a line.</summary>
    private static bool NewLine(List<Token> t, int i) =>
        i == 0 || i >= t.Count || Math.Abs(t[i].Box.Center.Y - t[i - 1].Box.Center.Y) > Math.Max(t[i].Box.Height, t[i - 1].Box.Height) * 0.5;

    /// <summary>Where, in the new version, text was removed before word <paramref name="anchor"/>.</summary>
    private static PdfRect Where(PageText b, int anchor) =>
        b.Tokens.Count == 0 ? new PdfRect(36, 36, 48, 48)
        : anchor > 0 ? Mark(b.Tokens[anchor - 1].Box, after: true)
        : Mark(b.Tokens[0].Box, after: false);

    /// <summary>A narrow box just after (or before) a word: where removed text was.</summary>
    private static PdfRect Mark(PdfRect r, bool after) =>
        after ? new PdfRect(r.X1, r.Y0, r.X1 + Math.Max(2, r.Height * 0.3), r.Y1) : new PdfRect(r.X0 - Math.Max(2, r.Height * 0.3), r.Y0, r.X0, r.Y1);

    /// <summary>The words' boxes joined line by line.</summary>
    private static List<PdfRect> Lines(List<Token> tokens)
    {
        var lines = new List<PdfRect>();
        foreach (var t in tokens)
        {
            if (lines.Count > 0 && Math.Abs(lines[^1].Center.Y - t.Box.Center.Y) < Math.Max(lines[^1].Height, t.Box.Height) * 0.5
                && t.Box.X0 >= lines[^1].X0 - 2 && t.Box.X0 - lines[^1].X1 < Math.Max(t.Box.Height, 4) * 3)
                lines[^1] = Union(lines[^1], t.Box);
            else lines.Add(t.Box);
        }
        return lines;
    }

    private static string Join(List<Token> tokens)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (i > 0 && tokens[i - 1].Word && tokens[i].Word) sb.Append(' ');
            sb.Append(tokens[i].Raw);
        }
        return sb.ToString();
    }

    private static string Snippet(PageText p)
    {
        var text = Join(p.Tokens.Take(24).ToList());
        return p.Tokens.Count > 24 ? text + "…" : text;
    }

    private static PdfRect Whole(PageText p) => new(0, 0, p.Width, p.Height);

    // ------------------------------------------------------------------ pictures

    /// <summary>What changed outside the text (images, drawings, stamps), found by rendering both pages.</summary>
    private static IEnumerable<PdfChange> VisualChanges(PdfiumDocument da, PdfiumDocument db, PageText a, PageText b, int op, int np)
    {
        if (Math.Abs(a.Width - b.Width) > 1 || Math.Abs(a.Height - b.Height) > 1)
        {
            yield return new PdfChange(ChangeKind.Visual, op, np, L.F("Cmp_Size", (int)a.Width, (int)a.Height), L.F("Cmp_Size", (int)b.Width, (int)b.Height),
                                       [Whole(a)], [Whole(b)]);
            yield break;
        }
        const double scale = 1.0;
        const int cell = 8;
        var ia = da.RenderPage(op, scale);
        var ib = db.RenderPage(np, scale);
        int w = Math.Min(ia.Width, ib.Width), h = Math.Min(ia.Height, ib.Height);
        int cw = (w + cell - 1) / cell, ch = (h + cell - 1) / cell;
        // cells with text in either version are the text comparison's business
        var text = new bool[cw * ch];
        foreach (var t in a.Tokens.Concat(b.Tokens))
            for (int cy = Math.Max(0, (int)((t.Box.Y0 - 1) / cell)); cy <= Math.Min(ch - 1, (int)((t.Box.Y1 + 1) / cell)); cy++)
                for (int cx = Math.Max(0, (int)((t.Box.X0 - 1) / cell)); cx <= Math.Min(cw - 1, (int)((t.Box.X1 + 1) / cell)); cx++)
                    text[cy * cw + cx] = true;
        var changed = new int[cw * ch];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int qa = (y * ia.Width + x) * 3, qb = (y * ib.Width + x) * 3;
                if (Math.Abs(ia.Pixels[qa] - ib.Pixels[qb]) + Math.Abs(ia.Pixels[qa + 1] - ib.Pixels[qb + 1]) + Math.Abs(ia.Pixels[qa + 2] - ib.Pixels[qb + 2]) > 60)
                    changed[y / cell * cw + x / cell]++;
            }
        // neighbouring changed cells make one box
        var seen = new bool[cw * ch];
        for (int c0 = 0; c0 < changed.Length; c0++)
        {
            if (seen[c0] || text[c0] || changed[c0] < 4) continue;
            var stack = new Stack<int>();
            stack.Push(c0);
            seen[c0] = true;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0, pixels = 0;
            while (stack.Count > 0)
            {
                int c = stack.Pop(), cx = c % cw, cy = c / cw;
                x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy); x1 = Math.Max(x1, cx); y1 = Math.Max(y1, cy);
                pixels += changed[c];
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                        int nc = ny * cw + nx;
                        if (!seen[nc] && !text[nc] && changed[nc] >= 4) { seen[nc] = true; stack.Push(nc); }
                    }
            }
            if (pixels < 12) continue;
            var box = new PdfRect(x0 * cell / scale, y0 * cell / scale, Math.Min(w, (x1 + 1) * cell) / scale, Math.Min(h, (y1 + 1) * cell) / scale);
            yield return new PdfChange(ChangeKind.Visual, op, np, "", "", [box], [box]);
        }
    }

    // ------------------------------------------------------------------ marking up

    /// <summary>
    /// A copy of the new version with the changes marked as comments: added text highlighted green, changed text yellow
    /// (the old wording in the comment), removed text and pages as notes, other changes boxed. Returns the number of marks.
    /// </summary>
    public static int MarkUp(string newPath, string output, CompareReport report)
    {
        if (report.Same) throw new PageAssemblyException("the two versions are the same: there is nothing to mark", "Err_CompareSame");
        var specs = new List<CommentSpec>();
        foreach (var c in report.Changes)
        {
            if (c.NewPage is not { } page) continue;
            var union = c.NewBoxes.Count == 0 ? new PdfRect(36, 36, 48, 48) : c.NewBoxes.Aggregate(Union);
            switch (c.Kind)
            {
                case ChangeKind.Inserted:
                    specs.Add(new CommentSpec(page, CommentKind.Highlight, union, L.F("Cmp_NoteAdded", Short(c.NewText)), "#3FB950", c.NewBoxes, Author: "Washi"));
                    break;
                case ChangeKind.Changed:
                    specs.Add(new CommentSpec(page, CommentKind.Highlight, union, L.F("Cmp_NoteChanged", Short(c.OldText)), "#F2CC60", c.NewBoxes, Author: "Washi"));
                    break;
                case ChangeKind.Deleted:
                    specs.Add(new CommentSpec(page, CommentKind.Note, NoteAt(union), L.F("Cmp_NoteDeleted", Short(c.OldText)), "#F85149", Author: "Washi"));
                    break;
                case ChangeKind.Visual:
                    specs.Add(new CommentSpec(page, CommentKind.Box, union, c.OldText.Length > 0 ? L.F("Cmp_NoteSize", c.OldText, c.NewText) : L.Get("Cmp_NoteVisual"),
                                              "#A371F7", Author: "Washi"));
                    break;
                case ChangeKind.PageAdded:
                    specs.Add(new CommentSpec(page, CommentKind.Note, new PdfRect(8, 8, 28, 28), L.Get("Cmp_NotePageAdded"), "#3FB950", Author: "Washi"));
                    break;
                case ChangeKind.PageRemoved:
                    specs.Add(new CommentSpec(page, CommentKind.Note, new PdfRect(8, 36, 28, 56), L.F("Cmp_NotePageRemoved", c.OldPage!.Value + 1, Short(c.OldText)), "#F85149", Author: "Washi"));
                    break;
            }
        }
        return PdfComments.Add(newPath, output, specs);
    }

    private static PdfRect NoteAt(PdfRect r) => new(r.X0, Math.Max(0, r.Y0 - 4), r.X0 + 18, Math.Max(0, r.Y0 - 4) + 18);

    private static string Short(string s) => s.Length > 300 ? s[..297] + "…" : s;

    private static PdfRect Union(PdfRect a, PdfRect b) =>
        new(Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1));
}
