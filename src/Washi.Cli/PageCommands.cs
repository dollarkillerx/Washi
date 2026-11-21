// SPDX-License-Identifier: AGPL-3.0-or-later
using Washi.Ai;
using Washi.Core.Forms;
using Washi.Core.Localization;
using Washi.Core.Ocr;
using Washi.Core.Pages;

/// <summary>washi pdf merge|split and washi ocr.</summary>
internal static class PageCommands
{
    private static readonly HashSet<string> Flags = ["--force", "--yes", "--all-ink", "--rules"];

    public static int Pdf(string[] args)
    {
        var (pos, opt) = Parse(args);
        try
        {
            switch (pos)
            {
                case ["merge", .. var inputs] when inputs.Length >= 1 && opt.TryGetValue("-o", out var output):
                {
                    if (File.Exists(output) && inputs.Any(i => Path.GetFullPath(i) == Path.GetFullPath(output)))
                        throw new PageAssemblyException($"{output} is also an input; choose another output name");
                    int pages = PageAssembly.Merge(inputs, output);
                    Console.WriteLine(L.F("Pdf_Merged", inputs.Length, output, pages, AcroFormFields.List(output).Count));
                    return 0;
                }
                case ["split", var input]:
                {
                    var ranges = PageAssembly.ParseRanges(opt.GetValueOrDefault("--pages"), PageAssembly.PageCount(input));
                    var dir = opt.GetValueOrDefault("-o", Path.GetFileNameWithoutExtension(input) + "_split");
                    foreach (var part in PageAssembly.Split(input, ranges, dir))
                        Console.WriteLine(L.F("Pdf_SplitPart", part.Path, part.FirstPage, part.LastPage));
                    return 0;
                }
                case ["erase", var input] when opt.TryGetValue("--box", out var boxes):
                {
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_erased.pdf"));
                    var report = PdfEraser.Erase(input, output, ParseBoxes(boxes),
                        new EraseOptions(KeepColored: !opt.ContainsKey("--all-ink"), Rules: opt.ContainsKey("--rules")));
                    Console.WriteLine(L.F("Pdf_Erased", output, report.TextRemoved, report.ImagesRemoved, report.DrawingsRemoved,
                        report.RemainingChars, report.ChangedPixelsOutside));
                    foreach (var k in report.Kept) Console.WriteLine(L.F("Pdf_EraseKept", k.Page + 1, L.Get("Erase_Kept" + k.Why)));
                    return 0;
                }
                default:
                    Console.Error.WriteLine(L.Get("UsagePdf"));
                    return 2;
            }
        }
        catch (EraseException e)
        {
            Console.Error.WriteLine(L.F("Pdf_Failed", e.Message));
            return 1;
        }
        catch (PageAssemblyException e)
        {
            Console.Error.WriteLine(L.F("Pdf_Failed", e.Message));
            return 1;
        }
    }

    public static async Task<int> Ocr(string[] args)
    {
        var (pos, opt) = Parse(args);
        if (pos is not [var input]) { Console.Error.WriteLine(L.Get("UsagePdf")); return 2; }
        try
        {
            var options = new OcrOptions
            {
                Languages = OcrLanguages.Parse(opt.GetValueOrDefault("--lang")),
                Force = opt.ContainsKey("--force"),
                Pages = opt.TryGetValue("--pages", out var spec)
                    ? PageAssembly.ParseRanges(spec, PageAssembly.PageCount(input)).SelectMany(r => Enumerable.Range(r.From, r.To - r.From + 1)).ToHashSet()
                    : null,
            };
            var plan = OcrPipeline.Plan(input, options, forWriting: true);
            if (plan.Count == 0) { Console.WriteLine(L.Get("Ocr_Nothing")); return 0; }

            var engineName = opt.GetValueOrDefault("--engine", "auto");
            IOcrEngine engine;
            if (engineName.Equals("ai", StringComparison.OrdinalIgnoreCase))
            {
                var model = AiSettings.Load().CreateModel();
                engine = new AiOcrEngine(model);
                if (!AiCommands.Confirm(AiOcrEngine.DescribeOcr(model, plan.Count), opt)) return 1;
            }
            else engine = OcrEngines.Create(engineName);

            Console.Error.WriteLine(L.F("Ocr_Engine", engine.Describe, plan.Count));
            var output = opt.GetValueOrDefault("-o", Path.GetFileNameWithoutExtension(input) + "_ocr.pdf");
            var progress = new Progress<(int Done, int Total)>(p => { if (p.Done > 0) Console.Error.WriteLine(L.F("Ocr_Progress", p.Done, p.Total)); });
            var result = await OcrPipeline.RunAsync(input, output, engine, options, progress);

            if (engine is TesseractOcr { Missing.Count: > 0 } t) Console.Error.WriteLine(L.F("Ocr_MissingLangs", string.Join(", ", t.Missing)));
            Console.WriteLine(L.F("Ocr_Done", result.Recognized, result.Output ?? "-"));
            if (opt.TryGetValue("--text", out var textPath))
            {
                await File.WriteAllTextAsync(textPath, string.Join("\n\n", result.Pages
                    .Where(p => p.Status == OcrPageStatus.Recognized).Select(p => $"=== page {p.Page} ===\n{p.Text}")));
                Console.WriteLine(L.F("Ocr_TextWritten", textPath));
            }
            return 0;
        }
        catch (Exception e) when (e is OcrException or PageAssemblyException or AiException or HttpRequestException
                                      or System.ClientModel.ClientResultException or Anthropic.Exceptions.AnthropicException
                                      or OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine(L.F("Pdf_Failed", e.Message));
            return 1;
        }
    }

    /// <summary>"1:40,60,200,80;2:10,10,50,30" (page:x0,y0,x1,y1 in points, top-left origin).</summary>
    private static List<EraseRegion> ParseBoxes(string spec) => [.. spec.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(b =>
    {
        var parts = b.Split(':', 2);
        var v = parts.Length == 2 ? parts[1].Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : [];
        if (parts.Length != 2 || !int.TryParse(parts[0], out var page) || page < 1 || v.Length != 4)
            throw new EraseException($"bad box '{b}': use page:x0,y0,x1,y1 (points, top-left origin)");
        return new EraseRegion(page - 1, new Washi.Core.PdfRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3])));
    })];

    private static (string[] Positional, Dictionary<string, string> Options) Parse(string[] args)
    {
        var pos = new List<string>();
        var opt = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (Flags.Contains(args[i])) opt[args[i]] = "";
            else if (args[i] == "--box" && i + 1 < args.Length) opt["--box"] = opt.TryGetValue("--box", out var b) ? b + ";" + args[++i] : args[++i];
            else if (args[i].StartsWith('-') && i + 1 < args.Length) opt[args[i]] = args[++i];
            else pos.Add(args[i]);
        }
        return ([.. pos], opt);
    }
}
