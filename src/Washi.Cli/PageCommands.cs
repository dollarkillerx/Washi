// SPDX-License-Identifier: AGPL-3.0-or-later
using Washi.Ai;
using Washi.Core.Forms;
using Washi.Core.Localization;
using Washi.Core.Ocr;
using Washi.Core.Pages;

/// <summary>washi pdf merge|split|organize|erase|protect|unlock|clean|redact|find|bookmarks and washi ocr.</summary>
internal static class PageCommands
{
    private static readonly HashSet<string> Flags = ["--force", "--yes", "--all-ink", "--rules", "--open", "--list", "--case", "--whole-words", "--clear"];
    private const char Many = '\u001f';   // between repeated --find / --regex values

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
                case ["organize", var input] when opt.TryGetValue("-o", out var output):
                {
                    if (Path.GetFullPath(input) == Path.GetFullPath(output))
                        throw new PageAssemblyException($"{output} is also the input; choose another output name");
                    var picks = PageAssembly.ParsePicks(input, opt.GetValueOrDefault("--pages"), opt.GetValueOrDefault("--rotate"),
                                                        PageAssembly.PageCount(input));
                    int pages = PageAssembly.Organize(picks, output);
                    Console.WriteLine(L.F("Pdf_Organized", output, pages, AcroFormFields.List(output).Count));
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
                case ["protect", var input] when opt.TryGetValue("-o", out var output) && (opt.ContainsKey("--open") || opt.ContainsKey("--allow")):
                {
                    var perms = opt.TryGetValue("--allow", out var allow) ? ParseAllow(allow) : PdfPermissions.All;
                    var open = opt.ContainsKey("--open") ? Password("WASHI_OPEN_PASSWORD", L.Get("Pdf_AskOpenPassword")) : null;
                    var owner = opt.ContainsKey("--allow") ? Password("WASHI_PERMISSIONS_PASSWORD", L.Get("Pdf_AskPermissionsPassword")) : null;
                    var current = PdfSecurity.Inspect(input).Encrypted ? Password("WASHI_PASSWORD", L.F("Pdf_AskPassword", Path.GetFileName(input))) : null;
                    PdfSecurity.Protect(input, output, open, owner, perms, current);
                    Console.WriteLine(L.F("Pdf_Protected", output));
                    return 0;
                }
                case ["unlock", var input] when opt.TryGetValue("-o", out var output):
                {
                    PdfSecurity.Unlock(input, output, Password("WASHI_PASSWORD", L.F("Pdf_AskPassword", Path.GetFileName(input))));
                    Console.WriteLine(L.F("Pdf_Unlocked", output));
                    return 0;
                }
                case ["clean", var input] when opt.ContainsKey("--list"):
                {
                    var found = PdfCleaner.Inspect(input);
                    if (found.Count == 0) Console.WriteLine(L.Get("Ui_HiddenNothing"));
                    foreach (var item in found) Console.WriteLine(HiddenLine(item));
                    return 0;
                }
                case ["clean", var input]:
                {
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_cleaned.pdf"));
                    if (File.Exists(output)) throw new PageAssemblyException($"{output} already exists; choose another output name");
                    var remove = opt.TryGetValue("--only", out var only) ? ParseHidden(only) : [.. Enum.GetValues<HiddenInfo>()];
                    remove.Add(HiddenInfo.EarlierVersions);
                    var removed = PdfCleaner.Clean(input, output, remove);
                    Console.WriteLine(L.F("Ui_Cleaned", output, removed.Sum(r => r.Count)));
                    foreach (var item in removed) Console.WriteLine(HiddenLine(item));
                    return 0;
                }
                case ["bookmarks", var input] when opt.ContainsKey("--set") || opt.ContainsKey("--clear"):
                {
                    var outline = opt.TryGetValue("--set", out var file) ? PdfBookmarks.FromLines(File.ReadAllLines(file)) : [];
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_bookmarks.pdf"));
                    PdfBookmarks.Write(input, output, outline);
                    Console.WriteLine(L.F("Pdf_BookmarksSaved", output, outline.Sum(b => b.Flatten().Count())));
                    return 0;
                }
                case ["bookmarks", var input]:
                {
                    var outline = PdfBookmarks.Read(input);
                    if (outline.Count == 0) Console.WriteLine(L.Get("Pdf_BookmarksNone"));
                    foreach (var line in PdfBookmarks.ToLines(outline)) Console.WriteLine(line);
                    return 0;
                }
                case ["find", var input, var text]:
                {
                    var hits = PdfSearch.Find(input, text, opt.ContainsKey("--case"), opt.ContainsKey("--whole-words"));
                    Console.WriteLine(L.F("Pdf_FindCount", hits.Count, hits.Select(h => h.Page).Distinct().Count()));
                    foreach (var h in hits) Console.WriteLine(L.F("Pdf_FindHit", h.Page + 1, h.Context));
                    return 0;
                }
                case ["redact", var input] when opt.ContainsKey("--find") || opt.ContainsKey("--pattern") || opt.ContainsKey("--regex") || opt.ContainsKey("--box"):
                {
                    var search = new RedactSearch(
                        opt.TryGetValue("--find", out var words) ? words.Split(Many, StringSplitOptions.RemoveEmptyEntries) : [],
                        opt.TryGetValue("--pattern", out var pats) ? ParsePatterns(pats) : [],
                        opt.TryGetValue("--regex", out var rx) ? rx.Split(Many, StringSplitOptions.RemoveEmptyEntries) : [],
                        MatchCase: opt.ContainsKey("--case"), WholeWords: opt.ContainsKey("--whole-words"));
                    var found = PdfRedactor.Find(input, search);
                    Console.WriteLine(L.F("Pdf_RedactFound", found.Count, found.Select(m => m.Page).Distinct().Count()));
                    foreach (var m in found)
                        Console.WriteLine(L.F("Pdf_RedactMatch", m.Page + 1, m.Source.ToString().ToLowerInvariant(), m.Context));
                    if (opt.ContainsKey("--list")) return 0;
                    var areas = PdfRedactor.Areas(found).ToList();
                    if (opt.TryGetValue("--box", out var boxes))
                        areas.AddRange(ParseBoxes(boxes).Select(b => new RedactArea(b.Page, b.Rect)));
                    if (areas.Count == 0)
                    {
                        Console.WriteLine(L.Get("Ui_RedactNoneFound"));   // nothing is written
                        return 0;
                    }
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_redacted.pdf"));
                    var report = PdfRedactor.Redact(input, output, areas, search);
                    Console.WriteLine(L.F("Pdf_Redacted", output, report.Glyphs, report.Images, report.Paths, report.Annotations, report.Strings));
                    return 0;
                }
                default:
                    Console.Error.WriteLine(L.Get("UsagePdf"));
                    return 2;
            }
        }
        catch (RedactException e)
        {
            Console.Error.WriteLine(L.F("Pdf_Failed", Washi.Core.Localization.Errors.Text(e)));
            return 1;
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

    private static string HiddenLine(HiddenItem item) =>
        "  " + L.F("Ui_Hidden" + item.Kind, item.Count) + (item.Details.Count > 0 ? ": " + string.Join(", ", item.Details) : "");

    /// <summary>"print,fill": what stays allowed; everything else is restricted ("none" restricts all).</summary>
    private static PdfPermissions ParseAllow(string spec)
    {
        var words = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).ToHashSet();
        var known = new HashSet<string> { "print", "copy", "edit", "comment", "fill", "assemble", "none" };
        if (words.FirstOrDefault(w => !known.Contains(w)) is { } bad)
            throw new PageAssemblyException($"--allow: unknown permission '{bad}' (use print, copy, edit, comment, fill, assemble or none)");
        return new PdfPermissions(words.Contains("print"), words.Contains("copy"), words.Contains("edit"), words.Contains("comment"),
                                  words.Contains("fill"), words.Contains("assemble"));
    }

    private static HashSet<HiddenInfo> ParseHidden(string spec) => [.. spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(w => w.ToLowerInvariant() switch
        {
            "metadata" => HiddenInfo.Metadata, "attachments" => HiddenInfo.Attachments, "scripts" => HiddenInfo.Scripts,
            "comments" => HiddenInfo.Comments, "private" or "private_data" => HiddenInfo.PrivateData,
            _ => throw new PageAssemblyException($"--only: unknown kind '{w}' (use metadata, attachments, scripts, comments or private)"),
        })];

    /// <summary>
    /// A password from an environment variable, or typed at the terminal without showing it. Never a command-line
    /// argument: those end up in the shell history and the process list.
    /// </summary>
    private static string Password(string variable, string prompt)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnv) return fromEnv;
        if (Console.IsInputRedirected)
            throw new PageAssemblyException($"set {variable} (no terminal to ask for the password)");
        Console.Error.Write(prompt + " ");
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        if (sb.Length == 0) throw new PageAssemblyException("no password given");
        return sb.ToString();
    }

    /// <summary>"1:40,60,200,80;2:10,10,50,30" (page:x0,y0,x1,y1 in points, top-left origin).</summary>
    private static List<RedactPattern> ParsePatterns(string spec) => [.. spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.ToLowerInvariant() switch
        {
            "email" or "mail" => RedactPattern.Email,
            "phone" or "tel" => RedactPattern.Phone,
            "number" or "numbers" or "id" => RedactPattern.Number,
            _ => throw new RedactException($"unknown pattern '{p}': use email, phone or number"),
        })];

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
            else if (args[i] is "--find" or "--regex" && i + 1 < args.Length) opt[args[i]] = opt.TryGetValue(args[i], out var f) ? f + Many + args[++i] : args[++i];
            else if (args[i].StartsWith('-') && i + 1 < args.Length) opt[args[i]] = args[++i];
            else pos.Add(args[i]);
        }
        return ([.. pos], opt);
    }
}
