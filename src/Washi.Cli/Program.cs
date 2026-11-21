// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Reflection;
using Washi.Core;
using Washi.Core.Pdfium;
using Washi.Masume;

var version = Assembly.GetExecutingAssembly().GetName().Version;

return args switch
{
    ["--version"] => Print($"washi {version}"),
    ["masume", "detect", var pdf, .. var rest] => Detect(pdf, OutDir(rest)),
    ["masume", "eval", var pdf, .. var rest] => Eval(pdf, OutDir(rest)),
    _ => Usage(),
};

int Usage()
{
    Console.Error.WriteLine($"""
        washi {version} - open-source PDF toolkit (pre-alpha)

        usage:
          washi --version
          washi masume detect <in.pdf> [-o out]   suggest form fields -> fields.json + overlay PNGs
          washi masume eval   <in.pdf> [-o out]   PDF that already has fields: re-detect on the
                                                  flat page and score against the real fields
        """);
    return args.Length == 0 ? 0 : 2;
}

static int Print(string s) { Console.WriteLine(s); return 0; }

static string OutDir(string[] rest) => rest is ["-o", var d, ..] ? d : "out";

static IEnumerable<(PageSnapshot Page, IReadOnlyList<DetectedField> Fields, IReadOnlyList<CellInfo> Cells)> Run(string pdf)
{
    using var doc = PdfiumDocument.Open(pdf);
    for (int i = 0; i < doc.PageCount; i++)
    {
        var page = doc.Snapshot(i, MasumeDetector.Scale);
        var fields = MasumeDetector.Detect(page, out var cells);
        yield return (page, fields, cells);
    }
}

static string Summary(IEnumerable<DetectedField> fields) =>
    string.Join(", ", fields.GroupBy(f => f.Kind.ToString().ToLowerInvariant()).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"));

static int Detect(string pdf, string outDir)
{
    var sw = Stopwatch.StartNew();
    Directory.CreateDirectory(outDir);
    var stem = Path.GetFileNameWithoutExtension(pdf);
    var all = new List<DetectedField>();
    foreach (var (page, fields, cells) in Run(pdf))
    {
        all.AddRange(fields);
        PngWriter.Save(MasumeOverlay.Draw(page, fields, cells), Path.Combine(outDir, $"{stem}_overlay_p{page.PageIndex + 1}.png"));
    }
    File.WriteAllText(Path.Combine(outDir, $"{stem}_fields.json"), FieldsJson.Serialize(all));
    Console.WriteLine($"{stem}: {all.Count} fields ({Summary(all)}) -> {outDir}/  [{sw.Elapsed.TotalSeconds:F1}s]");
    int pre = all.Count(f => f.Prefilled.Length > 0);
    if (pre > 0) Console.WriteLine($"  {pre} fields already contain printed data (red dashed in overlay)");
    return 0;
}

static int Eval(string pdf, string outDir)
{
    var sw = Stopwatch.StartNew();
    Directory.CreateDirectory(outDir);
    var stem = Path.GetFileNameWithoutExtension(pdf);
    int gtTotal = 0, hitTotal = 0, detTotal = 0, goodTotal = 0;
    var all = new List<DetectedField>();
    foreach (var (page, fields, cells) in Run(pdf))
    {
        var truth = page.Widgets.Select(w => w.Rect).ToList();
        all.AddRange(fields);
        gtTotal += truth.Count;
        hitTotal += truth.Count(t => fields.Any(f => Covers(f.Rect, t)));
        detTotal += fields.Count;
        goodTotal += fields.Count(f => truth.Any(t => Covers(f.Rect, t)));
        PngWriter.Save(MasumeOverlay.Draw(page, fields, cells, truth), Path.Combine(outDir, $"{stem}_eval_p{page.PageIndex + 1}.png"));
    }
    File.WriteAllText(Path.Combine(outDir, $"{stem}_fields.json"), FieldsJson.Serialize(all));
    Console.WriteLine($"{stem}: real fields {gtTotal}, detected {detTotal} ({Summary(all)})  [{sw.Elapsed.TotalSeconds:F1}s]");
    Console.WriteLine($"  recall    {hitTotal}/{gtTotal} = {hitTotal / (double)Math.Max(gtTotal, 1):P0}   (real fields covered by a detection)");
    Console.WriteLine($"  precision {goodTotal}/{detTotal} = {goodTotal / (double)Math.Max(detTotal, 1):P0}   (detections that sit on a real field)");
    return 0;

    static bool Covers(PdfRect r, PdfRect inner) => r.Contains(inner.Center.X, inner.Center.Y);
}
