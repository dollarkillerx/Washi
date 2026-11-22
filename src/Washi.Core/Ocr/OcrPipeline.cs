// SPDX-License-Identifier: AGPL-3.0-or-later
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Core.Pdfium;

namespace Washi.Core.Ocr;

public sealed record OcrOptions
{
    /// <summary>Washi language codes (ja, zh-Hans, …); empty = let the engine decide.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>1-based pages to consider; null = all.</summary>
    public IReadOnlySet<int>? Pages { get; init; }

    /// <summary>Also OCR pages that already have readable text.</summary>
    public bool Force { get; init; }
}

public enum OcrPageStatus { Recognized, HasText, AlreadySearchable, NotSelected }

public sealed record OcrPageResult(int Page, OcrPageStatus Status, IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join('\n', Lines.Select(l => l.Text));
}

public sealed record OcrResult(string Engine, IReadOnlyList<OcrPageResult> Pages, string? Output)
{
    public int Recognized => Pages.Count(p => p.Status == OcrPageStatus.Recognized);
}

/// <summary>
/// OCR for PDFs: pages without readable text are rendered, recognised by an engine and, when an
/// output path is given, written back as an invisible text layer (a searchable PDF). The page
/// image is not touched. Protected files (owner password) are refused, never unlocked.
/// </summary>
public static class OcrPipeline
{
    /// <summary>A page counts as having text when it has a readable layer of some length (not just a page number).</summary>
    public static bool HasText(string text) =>
        TextQuality.Readable(text) && text.Count(c => !char.IsWhiteSpace(c)) >= 10;

    /// <summary>
    /// Which pages a run would recognise (1-based), without recognising anything. With
    /// <paramref name="forWriting"/>, a file that cannot be changed fails here already.
    /// </summary>
    public static IReadOnlyList<int> Plan(string input, OcrOptions? options = null, bool forWriting = false)
    {
        options ??= new OcrOptions();
        using var doc = PdfReader.Open(input, forWriting ? PdfDocumentOpenMode.Modify : PdfDocumentOpenMode.Import);
        var already = Enumerable.Range(0, doc.PageCount).Where(i => TextLayer.HasLayer(doc.Pages[i])).ToHashSet();
        PdfiumDocument.Gate.Wait();
        try
        {
            using var pdf = PdfiumDocument.Open(input);
            return [.. Enumerable.Range(0, pdf.PageCount).Where(i => Status(pdf, i, already, options) == OcrPageStatus.Recognized).Select(i => i + 1)];
        }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private static OcrPageStatus Status(PdfiumDocument pdf, int i, HashSet<int> already, OcrOptions options) =>
        options.Pages is { } sel && !sel.Contains(i + 1) ? OcrPageStatus.NotSelected
        : already.Contains(i) ? OcrPageStatus.AlreadySearchable
        : !options.Force && HasText(pdf.GetPageText(i)) ? OcrPageStatus.HasText
        : OcrPageStatus.Recognized;

    public static async Task<OcrResult> RunAsync(string input, string? output, IOcrEngine engine, OcrOptions? options = null,
                                                 IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        options ??= new OcrOptions();
        // Open for writing first, so a protected file fails before any (slow) recognition.
        using var doc = PdfReader.Open(input, output is null ? PdfDocumentOpenMode.Import : PdfDocumentOpenMode.Modify);
        var already = Enumerable.Range(0, doc.PageCount).Where(i => TextLayer.HasLayer(doc.Pages[i])).ToHashSet();

        var statuses = new OcrPageStatus[doc.PageCount];
        var jobs = new List<(int Index, OcrImage Image, double Scale)>();
        await PdfiumDocument.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var pdf = PdfiumDocument.Open(input);
            double scale = engine.Dpi / 72.0;
            for (int i = 0; i < pdf.PageCount; i++)
            {
                statuses[i] = Status(pdf, i, already, options);
                if (statuses[i] != OcrPageStatus.Recognized) continue;
                var info = pdf.GetPageInfo(i);
                // keep very large pages within ~40 megapixels
                double s = Math.Min(scale, Math.Sqrt(40e6 / Math.Max(1, info.Width * info.Height)));
                var img = pdf.RenderPage(i, s);
                using var ms = new MemoryStream();
                PngWriter.Write(img, ms);
                jobs.Add((i, new OcrImage(ms.ToArray(), img.Width, img.Height), s));
            }
        }
        finally { PdfiumDocument.Gate.Release(); }

        var lines = new Dictionary<int, IReadOnlyList<OcrLine>>();
        int done = 0;
        progress?.Report((0, jobs.Count));
        foreach (var (index, image, s) in jobs)
        {
            var raw = await engine.RecognizeAsync(image, options.Languages, ct);
            lines[index] = [.. raw.Select(l => l with { Box = new PdfRect(l.Box.X0 / s, l.Box.Y0 / s, l.Box.X1 / s, l.Box.Y1 / s) })];
            progress?.Report((++done, jobs.Count));
        }

        if (output is not null && jobs.Count > 0)
        {
            PdfDictionary? font = null;
            foreach (var (index, list) in lines) TextLayer.Add(doc, doc.Pages[index], list, ref font);
            doc.Options.CompressContentStreams = true;
            doc.Save(output);
        }
        else if (output is not null)
        {
            output = null;   // nothing to add: no file written
        }

        var pages = Enumerable.Range(0, statuses.Length)
            .Select(i => new OcrPageResult(i + 1, statuses[i], lines.GetValueOrDefault(i, []))).ToList();
        return new OcrResult(engine.Describe, pages, output);
    }
}
