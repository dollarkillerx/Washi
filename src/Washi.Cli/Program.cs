// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Localization;
using Washi.Core.Pdfium;
using Washi.Masume;

var version = Assembly.GetExecutingAssembly().GetName().Version;
CultureInfo.CurrentCulture = L.Culture;   // numbers and percentages in the UI language

try
{
    return args switch
    {
        ["--version"] => Print($"washi {version}"),
        ["masume", "detect", var pdf, .. var rest] => Detect(pdf, Options(rest)),
        ["masume", "eval", var pdf, .. var rest] => Eval(pdf, Options(rest)),
        ["form", "make", var pdf, .. var rest] => MakeForm(pdf, Options(rest)),
        _ => Usage(),
    };
}
catch (PdfSharp.Pdf.IO.PdfReaderException e) when (e.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(L.F("ProtectedPdf", args.Length > 2 ? args[2] : "?"));
    return 1;
}
catch (Exception e) when (e is IOException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(L.F("OpenFailed", args.Length > 2 ? args[2] : "?", e.Message));
    return 1;
}

int Usage()
{
    Console.Error.WriteLine(L.F("Usage", version!));
    return args.Length == 0 ? 0 : 2;
}

static int Print(string s) { Console.WriteLine(s); return 0; }

static Dictionary<string, string> Options(string[] rest)
{
    var o = new Dictionary<string, string>();
    for (int i = 0; i + 1 < rest.Length; i += 2) o[rest[i]] = rest[i + 1];
    return o;
}

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

static string Kinds(IEnumerable<DetectedField> fields) =>
    string.Join(", ", fields.GroupBy(f => f.Kind.ToString().ToLowerInvariant()).OrderBy(g => g.Key)
                            .Select(g => $"{g.Count()} {L.Get("Kind_" + g.Key)}"));

static string Seconds(Stopwatch sw) => sw.Elapsed.TotalSeconds.ToString("F1", L.Culture);

static int Detect(string pdf, Dictionary<string, string> opt)
{
    var sw = Stopwatch.StartNew();
    var outDir = opt.GetValueOrDefault("-o", "out");
    Directory.CreateDirectory(outDir);
    var stem = Path.GetFileNameWithoutExtension(pdf);
    var all = new List<DetectedField>();
    foreach (var (page, fields, cells) in Run(pdf))
    {
        all.AddRange(fields);
        PngWriter.Save(MasumeOverlay.Draw(page, fields, cells), Path.Combine(outDir, $"{stem}_overlay_p{page.PageIndex + 1}.png"));
    }
    File.WriteAllText(Path.Combine(outDir, $"{stem}_fields.json"), FieldsJson.Serialize(all));
    Console.WriteLine(L.F("DetectSummary", stem, all.Count, Kinds(all), outDir, Seconds(sw)));
    int pre = all.Count(f => f.Prefilled.Length > 0);
    if (pre > 0) Console.WriteLine(L.F("PrefilledNote", pre));
    return 0;
}

static int Eval(string pdf, Dictionary<string, string> opt)
{
    var sw = Stopwatch.StartNew();
    var outDir = opt.GetValueOrDefault("-o", "out");
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
    Console.WriteLine(L.F("EvalSummary", stem, gtTotal, detTotal, Kinds(all), Seconds(sw)));
    Console.WriteLine(L.F("EvalRecall", hitTotal, gtTotal, hitTotal / (double)Math.Max(gtTotal, 1)));
    Console.WriteLine(L.F("EvalPrecision", goodTotal, detTotal, goodTotal / (double)Math.Max(detTotal, 1)));
    return 0;

    static bool Covers(PdfRect r, PdfRect inner) => r.Contains(inner.Center.X, inner.Center.Y);
}

static int MakeForm(string pdf, Dictionary<string, string> opt)
{
    var lang = opt.GetValueOrDefault("--lang", "auto");
    if (!Washi.Tools.FormPipeline.Languages.Contains(lang, StringComparer.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(L.F("UnknownLanguage", lang));
        return 2;
    }

    var stem = Path.GetFileNameWithoutExtension(pdf);
    var output = opt.GetValueOrDefault("-o", $"{stem}_fillable.pdf");

    IReadOnlyList<DetectedField> fields;
    string text;
    if (opt.TryGetValue("--fields", out var fieldsPath))
    {
        fields = FieldsJson.Parse(File.ReadAllText(fieldsPath));
        using var doc = PdfiumDocument.Open(pdf);
        text = string.Concat(Enumerable.Range(0, doc.PageCount).SelectMany(doc.ReadChars).Select(c => c.Text));
    }
    else
    {
        var all = new List<DetectedField>();
        var sb = new System.Text.StringBuilder();
        foreach (var (page, f, _) in Run(pdf))
        {
            all.AddRange(f);
            foreach (var c in page.Chars) sb.Append(c.Text);
        }
        fields = all;
        text = sb.ToString();
    }

    var (script, from) = Washi.Tools.FormPipeline.ChooseScript(lang, text, L.Culture);
    string source = L.Get(from switch
    {
        Washi.Tools.FormPipeline.ScriptSource.Option => "LangFromOption",
        Washi.Tools.FormPipeline.ScriptSource.PageText => "LangFromText",
        _ => "LangFromSystem",
    });

    Washi.Core.Fonts.OpenTypeFont? font = null;
    if (opt.TryGetValue("--embed-font", out var fontPath))
    {
        int face = opt.TryGetValue("--font-index", out var fi) && int.TryParse(fi, out var n) ? n : 0;
        try
        {
            font = Washi.Core.Fonts.OpenTypeFont.Load(fontPath, face);
        }
        catch (Washi.Core.Fonts.FontNotUsableException e) when (e.Reason == "license")
        {
            Console.Error.WriteLine(L.F("FontNotAllowed", Path.GetFileName(fontPath), $"0x{e.FsType:X4}"));
            return 1;
        }
        catch (Washi.Core.Fonts.FontNotUsableException)
        {
            Console.Error.WriteLine(L.F("FontNotSupported", fontPath));
            return 1;
        }
        if (!font.EmbeddingAllowedForEditing)
        {
            Console.Error.WriteLine(L.F("FontNotAllowed", font.PostScriptName, $"0x{font.FsType:X4}"));
            return 1;
        }
    }

    var result = AcroFormWriter.Write(pdf, output, fields.Select(f => f.ToFormField()).ToList(), script, font);
    Console.WriteLine(L.F("FormMade", stem, result.FieldsWritten, output, FormScripts.LanguageTag(script), source));
    if (font is not null)
        Console.WriteLine(L.F("FontEmbedded", font.PostScriptName, (new FileInfo(output).Length / 1048576.0).ToString("F1", L.Culture)));
    if (result.PagesStraightened > 0) Console.WriteLine(L.F("PagesStraightened", result.PagesStraightened));
    foreach (var w in result.Warnings) Console.WriteLine(L.F("Warning", w));
    return 0;
}
