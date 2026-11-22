// SPDX-License-Identifier: AGPL-3.0-or-later
using PDFiumCore;
using Washi.Core.Pdfium;

namespace Washi.Core.Pages;

/// <summary>A place where the searched text was found: its page (0-based), the text, a little around it, and its
/// boxes (upright top-down points, one per line it spans).</summary>
public sealed record TextHit(int Page, string Text, string Context, IReadOnlyList<PdfRect> Boxes);

/// <summary>
/// Finds text in a document's text layer, the way the viewer's find bar does: case, full-width and half-width forms
/// and spaces do not matter ("山田太郎" finds "山田 太郎"). Callers hold <see cref="PdfiumDocument.Gate"/>.
/// </summary>
public static class PdfSearch
{
    /// <summary>The hits page by page, in the order of the text layer, at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<TextHit> Find(string path, string query, bool matchCase = false, bool wholeWords = false,
                                              int max = 2000, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var matcher = new Matcher(new RedactSearch([query], MatchCase: matchCase, WholeWords: wholeWords));
        var hits = new List<TextHit>();
        PdfiumDocument.EnsureInit();
        var doc = PdfEraser.Load(path);
        try
        {
            int pages = fpdfview.FPDF_GetPageCount(doc);
            for (int p = 0; p < pages && hits.Count < max; p++)
            {
                cancel.ThrowIfCancellationRequested();
                PdfEraser.WithPage(doc, p, (page, map) =>
                    hits.AddRange(PdfRedactor.FindOnPage(page, map, p, matcher)          // in the text layer's order
                        .Select(m => new TextHit(m.Page, m.Text, m.Context, m.Boxes))));
            }
        }
        finally { fpdfview.FPDF_CloseDocument(doc); }
        return hits.Count > max ? hits[..max] : hits;
    }
}
