// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Washi.Core.Pages;

/// <summary>
/// A change to a comment already in the PDF (<see cref="CommentInfo.Id"/>). Null leaves a property as it is.
/// </summary>
/// <param name="Rect">Where it goes, on the upright page: a note or a pen drawing moves to the box's top-left corner, a
/// text box or a box takes the box.</param>
/// <param name="Delete">Removes it, with its pop-up and the replies to it.</param>
/// <param name="Reply">Adds a reply to it (by <paramref name="Author"/>).</param>
public sealed record CommentChange(string Id, string? Text = null, string? Color = null, PdfRect? Rect = null, bool Delete = false,
                                   string? Reply = null, string Author = "");

public static partial class PdfComments
{
    /// <summary>
    /// Writes <paramref name="output"/> with <paramref name="changes"/> made to existing comments. Comments Washi draws
    /// itself get a new appearance; for others only the text can change (or they can be removed or answered).
    /// The copy is read back to check every change.
    /// </summary>
    /// <returns>How many comments were changed, removed and added as replies.</returns>
    public static (int Changed, int Removed, int Replies) Change(string input, string output, IReadOnlyList<CommentChange> changes)
    {
        if (changes.Count == 0) throw new PageAssemblyException("no change to make", "Err_NoComment");
        if (File.Exists(output)) throw new PageAssemblyException($"{System.IO.Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", System.IO.Path.GetFileName(output));
        int changed = 0, removed = 0, replies = 0;
        using (var doc = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            var ids = Ids(doc);
            var byId = ids.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
            var pageOf = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < doc.PageCount; i++)
                if (doc.Pages[i].Elements.GetArray("/Annots") is { } annots)
                    foreach (var item in annots.Elements)
                        if (Deref(item) is PdfDictionary a) pageOf[a] = i;
            string now = "D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";
            var fonts = new Dictionary<Forms.FormScript, PdfDictionary>();
            var gone = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

            foreach (var c in changes)
            {
                if (!byId.TryGetValue(c.Id, out var a) || NotComments.Contains(a.Elements.GetName("/Subtype")))
                    throw new PageAssemblyException($"there is no comment '{c.Id}' (list_comments gives the ids)", "Err_NoSuchComment", c.Id);
                int pageIndex = pageOf[a];
                var page = doc.Pages[pageIndex];
                var map = new Upright(page);
                if (c.Delete) { gone.Add(a); continue; }

                bool edited = false;
                var type = a.Elements.GetName("/Subtype");
                bool redraw = false;
                var spec = Spec(a, pageIndex, map);
                if (c.Text is { } text && text != a.Elements.GetString("/Contents"))
                {
                    if (text.Length > 0) a.Elements["/Contents"] = new PdfString(text, PdfStringEncoding.Unicode);
                    else a.Elements.Remove("/Contents");
                    a.Elements.Remove("/RC");   // the rich-text copy would show the old words
                    if (type == "/FreeText") redraw = true;
                    spec = spec is null ? null : spec with { Text = text };
                    edited = true;
                }
                if (c.Color is { } color && !string.Equals(color, Hex(a), StringComparison.OrdinalIgnoreCase))
                {
                    if (spec is null) throw new PageAssemblyException($"comment '{c.Id}' ({type.TrimStart('/')}) cannot be recoloured by Washi", "Err_CommentNotRedrawable", c.Id);
                    spec = spec with { Color = color };
                    redraw = edited = true;
                }
                if (c.Rect is { } to)
                {
                    if (spec is null || spec.Kind is CommentKind.Highlight or CommentKind.Underline or CommentKind.StrikeOut)
                        throw new PageAssemblyException($"comment '{c.Id}' ({type.TrimStart('/')}) cannot be moved", "Err_CommentNotMovable", c.Id);
                    spec = Moved(spec, to);
                    redraw = edited = true;
                }
                if (redraw && spec is not null)
                {
                    var rgb = Rgb(spec.Color.Length > 0 ? spec.Color : DefaultColor(spec.Kind));
                    if (spec.Kind != CommentKind.TextBox) a.Elements["/C"] = Nums(doc, rgb.R, rgb.G, rgb.B);
                    switch (spec.Kind)
                    {
                        case CommentKind.Highlight or CommentKind.Underline or CommentKind.StrikeOut: Markup(doc, a, spec, map, rgb); break;
                        case CommentKind.Note: Note(doc, a, spec, map, rgb); break;
                        case CommentKind.TextBox: TextBox(doc, a, spec, map, rgb, fonts); break;
                        case CommentKind.Pen: Pen(doc, a, spec, map, rgb); break;
                        default: Box(doc, a, spec, map, rgb); break;
                    }
                    // its pop-up follows a moved note
                    if (Deref(a.Elements["/Popup"]) is PdfDictionary popup && a.Elements.GetArray("/Rect") is { } r && popup.Elements.GetArray("/Rect") is { } pr)
                    {
                        double w = pr.Elements.GetReal(2) - pr.Elements.GetReal(0), h = pr.Elements.GetReal(3) - pr.Elements.GetReal(1);
                        popup.Elements["/Rect"] = Nums(doc, r.Elements.GetReal(2), r.Elements.GetReal(3) - h, r.Elements.GetReal(2) + w, r.Elements.GetReal(3));
                    }
                }
                if (edited) { a.Elements["/M"] = new PdfString(now); changed++; }

                if (c.Reply is { Length: > 0 } reply)
                {
                    var answer = new PdfDictionary(doc);
                    doc.Internals.AddObject(answer);
                    answer.Elements.SetName("/Type", "/Annot");
                    answer.Elements.SetName("/Subtype", "/Text");
                    answer.Elements.SetName("/Name", "/Comment");
                    answer.Elements.SetInteger("/F", AnnotPrint | 2);   // hidden: a reply lives in its thread, not on the page
                    answer.Elements["/IRT"] = a.Reference ?? throw new PageAssemblyException("the comment cannot be answered", "Err_NoSuchComment", c.Id);
                    answer.Elements["/NM"] = new PdfString("washi-" + Guid.NewGuid().ToString("N"));
                    answer.Elements["/M"] = new PdfString(now);
                    answer.Elements["/Contents"] = new PdfString(reply, PdfStringEncoding.Unicode);
                    if (c.Author.Length > 0) answer.Elements["/T"] = new PdfString(c.Author, PdfStringEncoding.Unicode);
                    if (a.Elements.GetArray("/Rect") is { } ar) answer.Elements["/Rect"] = ar.Clone();
                    answer.Elements.SetReference("/P", page);
                    Forms.AcroFormWriter.GetOrCreateArray(doc, page, "/Annots").Elements.Add(answer.Reference!);
                    replies++;
                }
            }

            // removed comments take their pop-ups and replies (and replies to those) with them
            if (gone.Count > 0)
            {
                bool more = true;
                var all = pageOf.Keys.ToList();
                while (more)
                {
                    more = false;
                    foreach (var a in all)
                        if (!gone.Contains(a) && (Deref(a.Elements["/IRT"]) is PdfDictionary irt && gone.Contains(irt)
                                                  || Deref(a.Elements["/Parent"]) is PdfDictionary parent && gone.Contains(parent)))
                        { gone.Add(a); more = true; }
                }
                foreach (var a in gone.ToList())
                    if (Deref(a.Elements["/Popup"]) is PdfDictionary popup) gone.Add(popup);
                for (int i = 0; i < doc.PageCount; i++)
                    if (doc.Pages[i].Elements.GetArray("/Annots") is { } annots)
                        for (int j = annots.Elements.Count - 1; j >= 0; j--)
                            if (Deref(annots.Elements[j]) is PdfDictionary a && gone.Contains(a))
                            {
                                annots.Elements.RemoveAt(j);
                                if (!NotComments.Contains(a.Elements.GetName("/Subtype"))) removed++;
                            }
            }
            doc.Options.CompressContentStreams = true;
            doc.Save(output);
        }

        // proof: the copy holds what was asked
        var after = List(output);
        foreach (var c in changes)
        {
            bool positional = System.Text.RegularExpressions.Regex.IsMatch(c.Id, @"^p\d+-\d+$");   // shifts once something goes
            bool ok = c.Delete ? positional || after.All(x => x.Id != c.Id)
                : after.FirstOrDefault(x => x.Id == c.Id) is not { } x || (c.Text is null || x.Text == c.Text)
                  && (c.Color is null || string.Equals(x.Color, c.Color, StringComparison.OrdinalIgnoreCase));
            if (!ok)
            {
                File.Delete(output);
                throw new PageAssemblyException("the changed comments did not come out as asked; the copy was not kept", "Err_CommentCheck");
            }
        }
        return (changed, removed, replies);
    }

    /// <summary>The comment as Washi would draw it, read back from the PDF (null for kinds Washi does not draw).</summary>
    private static CommentSpec? Spec(PdfDictionary a, int page, Upright map)
    {
        var rc = a.Elements.GetArray("/Rect");
        var rect = rc is { Elements.Count: >= 4 }
            ? map.ToUpright(new PdfRect(rc.Elements.GetReal(0), rc.Elements.GetReal(1), rc.Elements.GetReal(2), rc.Elements.GetReal(3)))
            : default;
        string text = a.Elements.GetString("/Contents"), color = Hex(a);
        switch (a.Elements.GetName("/Subtype"))
        {
            case "/Highlight" or "/Underline" or "/StrikeOut" when a.Elements.GetArray("/QuadPoints") is { } q:
                var lines = new List<PdfRect>();
                for (int i = 0; i + 7 < q.Elements.Count; i += 8)
                {
                    var pts = Enumerable.Range(0, 4).Select(k => (q.Elements.GetReal(i + 2 * k), q.Elements.GetReal(i + 2 * k + 1))).ToList();
                    lines.Add(map.ToUpright(new PdfRect(pts.Min(p => p.Item1), pts.Min(p => p.Item2), pts.Max(p => p.Item1), pts.Max(p => p.Item2))));
                }
                var kind = a.Elements.GetName("/Subtype") switch { "/Highlight" => CommentKind.Highlight, "/Underline" => CommentKind.Underline, _ => CommentKind.StrikeOut };
                return lines.Count == 0 ? null : new CommentSpec(page, kind, Union(lines), text, color, lines);
            case "/Text":
                return new CommentSpec(page, CommentKind.Note, rect, text, color);
            case "/FreeText":
                return new CommentSpec(page, CommentKind.TextBox, rect, text, color);
            case "/Square":
                return new CommentSpec(page, CommentKind.Box, rect, text, color);
            case "/Ink" when a.Elements.GetArray("/InkList") is { } ink:
                var strokes = new List<IReadOnlyList<(double X, double Y)>>();
                foreach (var s in ink.Elements)
                    if (Deref(s) is PdfArray arr)
                    {
                        var pts = new List<(double, double)>();
                        for (int i = 0; i + 1 < arr.Elements.Count; i += 2)
                        {
                            var u = map.ToUpright(new PdfRect(arr.Elements.GetReal(i), arr.Elements.GetReal(i + 1), arr.Elements.GetReal(i), arr.Elements.GetReal(i + 1)));
                            pts.Add((u.X0, u.Y0));
                        }
                        strokes.Add(pts);
                    }
                return new CommentSpec(page, CommentKind.Pen, rect, text, color, Strokes: strokes);
            default:
                return null;
        }
    }

    /// <summary>The comment placed at <paramref name="to"/>: notes and pen drawings move, text boxes and boxes take the box.</summary>
    private static CommentSpec Moved(CommentSpec s, PdfRect to)
    {
        double dx = to.X0 - s.Rect.X0, dy = to.Y0 - s.Rect.Y0;
        return s.Kind switch
        {
            CommentKind.TextBox or CommentKind.Box when to.Width >= 4 && to.Height >= 4 => s with { Rect = to },
            CommentKind.Pen => s with
            {
                Rect = new PdfRect(s.Rect.X0 + dx, s.Rect.Y0 + dy, s.Rect.X1 + dx, s.Rect.Y1 + dy),
                Strokes = [.. (s.Strokes ?? []).Select(st => (IReadOnlyList<(double X, double Y)>)[.. st.Select(p => (p.X + dx, p.Y + dy))])],
            },
            _ => s with { Rect = new PdfRect(s.Rect.X0 + dx, s.Rect.Y0 + dy, s.Rect.X1 + dx, s.Rect.Y1 + dy) },
        };
    }
}
