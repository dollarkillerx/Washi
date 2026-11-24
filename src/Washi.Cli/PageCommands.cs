// SPDX-License-Identifier: AGPL-3.0-or-later
using Washi.Ai;
using Washi.Core;
using Washi.Core.Forms;
using Washi.Core.Localization;
using Washi.Core.Ocr;
using Washi.Core.Pages;
using Washi.Core.Printing;

/// <summary>washi pdf merge|split|organize|erase|protect|unlock|clean|redact|find|bookmarks|print|printers|compress|flatten|pdfa|sign|verify|make-id and washi ocr.</summary>
internal static class PageCommands
{
    private static string Beside(string input, string suffix) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + suffix);

    private static List<string> List(string value) => [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The certificates given with --trust (comma-separated .cer / .pem files) to trust besides the system's.</summary>
    private static List<System.Security.Cryptography.X509Certificates.X509Certificate2>? Trust(Dictionary<string, string> opt) =>
        opt.TryGetValue("--trust", out var files)
            ? [.. files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile)]
            : null;

    private static readonly HashSet<string> Flags = ["--force", "--yes", "--all-ink", "--rules", "--open", "--list", "--case", "--whole-words", "--clear", "--two-sided", "--gray", "--actual-size", "--ignore-case", "--behind", "--trim", "--find-addresses", "--replace-existing", "--fields-only", "--comments-only", "--check", "--flatten", "--ltv", "--clear"];
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
                case ["protect", var input] when opt.TryGetValue("--to", out var to):
                {
                    var certs = to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Select(System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile).ToList();
                    var output = opt.GetValueOrDefault("-o", Beside(input, "_sealed.pdf"));
                    var current = PdfSecurity.Inspect(input).Encrypted ? Password("WASHI_PASSWORD", L.F("Pdf_AskPassword", Path.GetFileName(input))) : null;
                    PdfCertificateSecurity.Encrypt(input, output, certs, current);
                    Console.WriteLine(L.F("Ui_CertSealed", output, certs.Count));
                    return 0;
                }
                case ["unlock", var input] when opt.TryGetValue("--id", out var idFile):
                {
                    var id = Washi.Core.Signatures.DigitalIds.FromFile(idFile, Password("WASHI_ID_PASSWORD", L.Get("Pdf_AskIdPassword")));
                    var output = opt.GetValueOrDefault("-o", Beside(input, "_opened.pdf"));
                    PdfCertificateSecurity.Decrypt(input, output, id);
                    Console.WriteLine(L.F("Ui_CertOpened", output, id.Name));
                    return 0;
                }
                case ["data", var input] when opt.TryGetValue("--import", out var data):
                {
                    var output = opt.GetValueOrDefault("-o", Beside(input, "_filled.pdf"));
                    var r = Washi.Core.Forms.PdfFormData.Import(input, data, output);
                    Console.WriteLine(L.F("Ui_FormDataImported", output, r.Filled));
                    foreach (var n in r.UnknownNames) Console.WriteLine("    " + L.F("Ui_FormDataUnknown", n));
                    foreach (var n in r.Notes) Console.WriteLine("    " + n);
                    return 0;
                }
                case ["data", var input] when opt.TryGetValue("--rows", out var table):
                {
                    var folder = opt.GetValueOrDefault("-o", Beside(input, "_filled"));
                    var r = Washi.Core.Forms.PdfFormData.FillMany(input, table, folder, opt.GetValueOrDefault("--name-column"));
                    foreach (var row in r.Rows)
                    {
                        Console.WriteLine($"{row.Row}\t{row.Output}\t{row.Filled}");
                        foreach (var n in row.Notes) Console.WriteLine("    " + n);
                    }
                    foreach (var c in r.UnknownColumns) Console.WriteLine(L.F("Ui_FormDataUnknown", c));
                    Console.WriteLine(L.F("Ui_FormDataFilledMany", r.Rows.Count, folder));
                    return 0;
                }
                case ["data", var input] when opt.TryGetValue("-o", out var output):
                {
                    int n = Washi.Core.Forms.PdfFormData.Export(input, output);
                    Console.WriteLine(L.F("Ui_FormDataExported", output, n));
                    return 0;
                }
                case ["data", var input]:
                {
                    foreach (var (k, v) in Washi.Core.Forms.PdfFormData.Values(input)) Console.WriteLine($"{k}\t{v}");
                    return 0;
                }
                case ["collect", .. var inputs] when inputs.Length >= 1 && opt.TryGetValue("-o", out var output):
                {
                    var files = inputs.SelectMany(i => Directory.Exists(i) ? Directory.GetFiles(i, "*.pdf").Order(StringComparer.Ordinal) : (IEnumerable<string>)[i]).ToList();
                    int n = Washi.Core.Forms.PdfFormData.Collect(files, output);
                    Console.WriteLine(L.F("Ui_FormDataCollected", output, n));
                    return 0;
                }
                case ["labels", var input] when opt.ContainsKey("--set") || opt.ContainsKey("--clear"):
                {
                    var ranges = opt.ContainsKey("--clear") ? [] : PdfPageLabels.Parse(opt["--set"]);
                    var output = opt.GetValueOrDefault("-o", Beside(input, "_labels.pdf"));
                    PdfPageLabels.Write(input, output, ranges);
                    Console.WriteLine(ranges.Count == 0 ? L.F("Ui_LabelsRemoved", output) : L.F("Ui_LabelsSaved", output, ranges.Count));
                    return 0;
                }
                case ["labels", var input]:
                {
                    var ranges = PdfPageLabels.Read(input);
                    Console.WriteLine(ranges.Count == 0 ? L.Get("Ui_LabelsNone") : PdfPageLabels.Format(ranges));
                    var labels = PdfPageLabels.Labels(input);
                    for (int i = 0; i < labels.Count; i++) Console.WriteLine($"{i + 1}\t{labels[i]}");
                    return 0;
                }
                case ["attachments", var input] when opt.TryGetValue("--extract", out var dir):
                {
                    var names = opt.TryGetValue("--name", out var n) ? List(n) : [];
                    var written = PdfAttachments.Extract(input, dir, names);
                    foreach (var w in written) Console.WriteLine(w);
                    Console.WriteLine(L.F("Ui_AttachmentsSaved", written.Count, dir));
                    return 0;
                }
                case ["attachments", var input] when opt.ContainsKey("--add") || opt.ContainsKey("--remove"):
                {
                    var add = opt.TryGetValue("--add", out var a) ? List(a) : [];
                    var remove = opt.TryGetValue("--remove", out var r) ? List(r) : [];
                    var output = opt.GetValueOrDefault("-o", Beside(input, add.Count > 0 ? "_attached.pdf" : "_detached.pdf"));
                    var added = PdfAttachments.Edit(input, output, add, remove, opt.GetValueOrDefault("--description", ""));
                    Console.WriteLine(add.Count > 0 ? L.F("Ui_Attached", output, added.Count) : L.F("Ui_Detached", output, remove.Count));
                    return 0;
                }
                case ["attachments", var input]:
                {
                    var list = PdfAttachments.List(input);
                    if (list.Count == 0) Console.WriteLine(L.Get("Ui_AttachNone"));
                    foreach (var x in list)
                        Console.WriteLine(x.Name + "\t" + (x.Size >= 0 ? CompressFormat.Size(x.Size) : "?") + (x.Page >= 0 ? "\t" + L.F("Ui_AttachOnPage", x.Page + 1) : "")
                                          + (x.Description.Length > 0 ? "\t" + x.Description : ""));
                    return 0;
                }
                case ["layers", var input] when opt.ContainsKey("--show") || opt.ContainsKey("--hide"):
                {
                    var output = opt.GetValueOrDefault("-o", Beside(input, "_layers.pdf"));
                    var after = PdfLayers.SetVisible(input, output, opt.TryGetValue("--show", out var sh) ? List(sh) : [], opt.TryGetValue("--hide", out var hi) ? List(hi) : []);
                    Console.WriteLine(L.F("Ui_LayersSaved", output));
                    foreach (var l in after) Console.WriteLine($"{(l.Visible ? "[x]" : "[ ]")} {l.Name}");
                    return 0;
                }
                case ["layers", var input]:
                {
                    var layers = PdfLayers.List(input);
                    if (layers.Count == 0) Console.WriteLine(L.Get("Ui_LayersNone"));
                    foreach (var l in layers)
                        Console.WriteLine($"{(l.Visible ? "[x]" : "[ ]")} {l.Name}\t{l.Id}" + (l.Locked ? "\tlocked" : "")
                                          + (l.Pages.Count > 0 ? "\t" + L.F("Ui_LayerPages", string.Join(",", l.Pages.Select(p => p + 1))) : ""));
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
                case ["verify", var input]:
                {
                    var checks = Washi.Core.Signatures.PdfSignatureVerifier.Verify(input, Trust(opt));
                    if (checks.Count == 0) Console.WriteLine(L.Get("Pdf_SigNone"));
                    foreach (var c in checks)
                    {
                        var tags = new List<string>();
                        if (c.SignedAt is { } t) tags.Add($"{t:yyyy-MM-dd HH:mm:ss zzz}");
                        if (c.Timestamped && c.Kind != "timestamp") tags.Add(L.Get("Ui_SigTimestamped"));
                        if (c.Ltv && c.Trusted) tags.Add(L.Get("Ui_SigLtv"));
                        var who = c.Kind == "timestamp" ? $"{L.Get("Ui_SigDocTimestamp")} ({c.Signer})" : c.Signer;
                        Console.WriteLine(L.F("Pdf_SigLine", c.Field, who, L.Get("Ui_SigStatus_" + c.Status)) + (tags.Count > 0 ? $" ({string.Join(", ", tags)})" : ""));
                        if (c.Kind == "certification") Console.WriteLine("    " + L.F("Ui_SigCertified", L.Get("Ui_SigPerm_" + c.Permissions)));
                        if (c.LaterChanges.Count > 0)
                            Console.WriteLine("    " + L.F("Ui_SigChanges", string.Join(", ", c.LaterChanges.Select(k => L.Get("Ui_SigChange_" + k)))));
                        foreach (var p in c.Problems) Console.WriteLine("    " + p);
                    }
                    return checks.Any(c => !c.Intact || c.Revoked) ? 3 : 0;
                }
                case ["ltv", var input]:
                {
                    Uri? tsa = opt.TryGetValue("--tsa", out var t) ? new Uri(t) : null;
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_ltv.pdf"));
                    var r = Washi.Core.Signatures.PdfLtv.AddValidation(input, output, tsa, Trust(opt));
                    Console.WriteLine(L.F("Ui_SigLtvAdded", output, r.Certificates, r.Ocsps, r.Crls));
                    if (r.Timestamped) Console.WriteLine(L.Get("Ui_SigLtvStamped"));
                    if (r.NotLtv.Count == 0) return 0;
                    Console.WriteLine(L.F("Ui_SigLtvMissing", string.Join(", ", r.NotLtv)));
                    foreach (var n in r.Notes) Console.WriteLine("    " + n);
                    return 4;
                }
                case ["make-id"] when opt.TryGetValue("--name", out var name) && opt.TryGetValue("-o", out var output):
                {
                    var pw = Password("WASHI_ID_PASSWORD", L.Get("Pdf_AskIdPassword"));
                    var id = Washi.Core.Signatures.DigitalIds.Create(name, opt.GetValueOrDefault("--email", ""), output, pw);
                    Console.WriteLine(L.F("Ui_IdCreated", output) + $" ({id.Name}, {id.Certificate.NotAfter:yyyy-MM-dd})");
                    return 0;
                }
                case ["sign", var input] when opt.TryGetValue("--id", out var idFile):
                {
                    var id = Washi.Core.Signatures.DigitalIds.FromFile(idFile, Password("WASHI_ID_PASSWORD", L.Get("Pdf_AskIdPassword")));
                    PdfRect? box = null;
                    if (opt.TryGetValue("--box", out var b))
                    {
                        var v = b.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        if (v.Length != 4) throw new PageAssemblyException("--box takes x0,y0,x1,y1 (points, top-left origin)");
                        box = new PdfRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
                    }
                    int page = opt.TryGetValue("--page", out var pg) && int.TryParse(pg, out var pn) ? pn - 1 : 0;
                    Uri? tsa = opt.TryGetValue("--tsa", out var t) ? new Uri(t) : null;
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_signed.pdf"));
                    int certify = opt.GetValueOrDefault("--certify", "") switch
                    {
                        "" => 0,
                        "no-changes" or "1" => 1,
                        "forms" or "2" => 2,
                        "comments" or "3" => 3,
                        _ => throw new PageAssemblyException("--certify takes no-changes, forms or comments", "Err_CertifyLevel"),
                    };
                    var r = Washi.Core.Signatures.PdfDigitalSigner.Sign(input, output, new Washi.Core.Signatures.SignRequest(
                        id, opt.GetValueOrDefault("--field"), page, box, opt.GetValueOrDefault("--reason", ""), opt.GetValueOrDefault("--location", ""), null, tsa,
                        certify, opt.ContainsKey("--ltv")));
                    Console.WriteLine(L.F("Ui_SigSigned", output, r.Signer));
                    if (!opt.ContainsKey("--ltv")) return 0;
                    if (r.Ltv) { Console.WriteLine(L.Get("Ui_SigLtvDone")); return 0; }
                    Console.WriteLine(L.F("Ui_SigLtvMissing", r.Field));
                    foreach (var n in r.Notes) Console.WriteLine("    " + n);
                    return 4;
                }
                case ["compress", var input]:
                {
                    var level = opt.GetValueOrDefault("--level", "standard").ToLowerInvariant() switch
                    {
                        "light" => CompressLevel.Light,
                        "strong" => CompressLevel.Strong,
                        "standard" => CompressLevel.Standard,
                        var x => throw new PageAssemblyException($"unknown level '{x}': use light, standard or strong"),
                    };
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_compressed.pdf"));
                    var r = PdfCompressor.Compress(input, output, level);
                    Console.WriteLine(L.F("Ui_Compressed", output, Washi.Core.Pages.CompressFormat.Size(r.Before), Washi.Core.Pages.CompressFormat.Size(r.After), (int)Math.Round(r.Saved * 100)));
                    Console.WriteLine(L.F("Ui_CompressDetails", r.Images, r.Downsampled, r.Duplicates, r.Streams));
                    foreach (var n in r.Notes) Console.WriteLine("  " + n);
                    return 0;
                }
                case ["flatten", var input]:
                {
                    if (opt.ContainsKey("--fields-only") && opt.ContainsKey("--comments-only"))
                        throw new PageAssemblyException("choose --fields-only or --comments-only, not both", "Err_FlattenNothing");
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_flattened.pdf"));
                    var r = PdfFlattener.Flatten(input, output, fields: !opt.ContainsKey("--comments-only"), comments: !opt.ContainsKey("--fields-only"));
                    Console.WriteLine(L.F("Ui_Flattened", output, r.Fields, r.Comments));
                    return 0;
                }
                case ["pdfa", var input] when opt.ContainsKey("--check"):
                {
                    var items = PdfArchive.Check(input);
                    if (items.Count == 0) { Console.WriteLine(L.Get("Ui_PdfaOk")); return 0; }
                    Console.WriteLine(L.Get("Ui_PdfaIssues"));
                    foreach (var line in ArchiveLines(items)) Console.WriteLine("  " + line);
                    return 4;
                }
                case ["pdfa", var input]:
                {
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_pdfa.pdf"));
                    var r = PdfArchive.Convert(input, output, flatten: opt.ContainsKey("--flatten"));
                    Console.WriteLine(L.F("Ui_PdfaSaved", output));
                    if (r.Changes.Count > 0)
                    {
                        Console.WriteLine(L.Get("Ui_PdfaChanges"));
                        foreach (var line in ArchiveLines(r.Changes)) Console.WriteLine("  " + line);
                    }
                    return 0;
                }
                case ["printers"]:
                {
                    var printers = PdfPrinting.Default.Printers();
                    if (printers.Count == 0) Console.WriteLine(L.Get("Pdf_PrintersNone"));
                    foreach (var p in printers) Console.WriteLine(p.IsDefault ? L.F("Pdf_PrinterDefault", p.Name) : p.Name);
                    return 0;
                }
                case ["print", var input]:
                {
                    int copies = opt.TryGetValue("--copies", out var c) && int.TryParse(c, out var n) ? n : 1;
                    var result = PdfPrinting.Default.Print(input, new PrintOptions(opt.GetValueOrDefault("--printer"), copies,
                        opt.GetValueOrDefault("--pages"), opt.ContainsKey("--two-sided"), opt.ContainsKey("--gray"), !opt.ContainsKey("--actual-size")));
                    Console.WriteLine(L.F("Ui_Printed", result.Pages, Path.GetFileName(input), result.Printer));
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
                case ["export-images", var input]:
                {
                    double Num(string key, double fallback) =>
                        opt.TryGetValue(key, out var v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;
                    var folder = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_images"));
                    var files = PdfPageImages.Export(input, folder, new ExportOptions(opt.GetValueOrDefault("--format", "png"), Num("--dpi", 150),
                                                                                    opt.GetValueOrDefault("--pages"), (int)Num("--quality", 90)));
                    Console.WriteLine(L.F("Pdf_Exported", files.Count, folder));
                    return 0;
                }
                case ["crop", var input] when opt.ContainsKey("--margins") || opt.ContainsKey("--trim"):
                {
                    var m = (opt.GetValueOrDefault("--margins") ?? "0").Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    // like CSS: one value for all sides, two for top/bottom and left/right, four for top, right, bottom, left
                    var (t, r, b, l) = m.Length switch
                    {
                        1 => (m[0], m[0], m[0], m[0]), 2 => (m[0], m[1], m[0], m[1]), 4 => (m[0], m[1], m[2], m[3]),
                        _ => throw new PageAssemblyException("--margins takes 1, 2 or 4 numbers (points)", "Err_CropMargins"),
                    };
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_cropped.pdf"));
                    var padding = opt.TryGetValue("--padding", out var pd) ? double.Parse(pd, System.Globalization.CultureInfo.InvariantCulture) : 6;
                    int n = PdfPageImages.Crop(input, output, new CropOptions(t, r, b, l, opt.ContainsKey("--trim"), padding, opt.GetValueOrDefault("--pages")));
                    Console.WriteLine(L.F("Pdf_Cropped", output, n));
                    return 0;
                }
                case ["images", var input]:
                {
                    var add = new List<AddImage>();
                    if (opt.TryGetValue("--add", out var adds))
                        foreach (var spec in adds.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int eq = spec.IndexOf('=');
                            if (eq < 0) throw new PageAssemblyException("--add takes page:x0,y0,x1,y1=image file", "Err_ImageAdd");
                            var region = ParseBoxes(spec[..eq]).Single();
                            add.Add(new AddImage(region.Page, region.Rect, spec[(eq + 1)..]));
                        }
                    var replace = new Dictionary<string, string>();
                    if (opt.TryGetValue("--replace", out var reps))
                        foreach (var spec in reps.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int eq = spec.IndexOf('=');
                            if (eq < 0) throw new PageAssemblyException("--replace takes id=image file", "Err_ImageReplaceArg");
                            replace[spec[..eq].Trim()] = spec[(eq + 1)..];
                        }
                    var remove = opt.TryGetValue("--remove", out var rm) ? rm.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
                    if (add.Count == 0 && replace.Count == 0 && remove.Length == 0)
                    {
                        foreach (var i in PdfPageImageEdit.List(input))
                            Console.WriteLine(L.F("Pdf_ImageLine", i.Id, i.Page + 1, i.PixelWidth, i.PixelHeight,
                                $"{i.Rect.X0:0},{i.Rect.Y0:0},{i.Rect.X1:0},{i.Rect.Y1:0}"));
                        return 0;
                    }
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_images.pdf"));
                    var r = PdfPageImageEdit.Apply(input, output, add, replace, remove);
                    Console.WriteLine(L.F("Ui_ImagesSaved", output, r.Added, r.Replaced, r.Removed));
                    return 0;
                }
                case ["links", var input]:
                {
                    var add = new List<NewLink>();
                    if (opt.ContainsKey("--find-addresses")) add.AddRange(PdfLinks.FindAddresses(input));
                    if (opt.TryGetValue("--add", out var adds))
                        foreach (var spec in adds.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int eq = spec.IndexOf('=');
                            if (eq < 0) throw new PageAssemblyException("--add takes page:x0,y0,x1,y1=target", "Err_LinkAdd");
                            var region = ParseBoxes(spec[..eq]).Single();
                            add.Add(new NewLink(region.Page, region.Rect, spec[(eq + 1)..]));
                        }
                    var remove = opt.TryGetValue("--remove", out var rm) ? rm.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
                    if (add.Count == 0 && remove.Length == 0 && opt.ContainsKey("--find-addresses"))
                    {
                        Console.WriteLine(L.Get("Ui_LinksNoneFound"));   // nothing is written
                        return 0;
                    }
                    if (add.Count == 0 && remove.Length == 0)
                    {
                        foreach (var l in PdfLinks.List(input))
                            Console.WriteLine(L.F("Pdf_LinkLine", l.Id, l.Page + 1, l.Kind == "page" ? L.F("Ui_LinkToPage", l.Target) : l.Target,
                                $"{l.Rect.X0:0},{l.Rect.Y0:0},{l.Rect.X1:0},{l.Rect.Y1:0}"));
                        return 0;
                    }
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_links.pdf"));
                    var r = PdfLinks.Apply(input, output, add, remove);
                    Console.WriteLine(L.F("Ui_LinksSaved", output, r.Added, r.Removed));
                    return 0;
                }
                case ["compare", var older, var newer]:
                {
                    var report = PdfComparer.Compare(older, newer);
                    if (report.Same) { Console.WriteLine(L.Get("Ui_CompareNone")); return 0; }
                    Console.WriteLine(L.F("Ui_CompareSummary", report.Changes.Count, report.Count(ChangeKind.Changed), report.Count(ChangeKind.Inserted),
                        report.Count(ChangeKind.Deleted), report.Count(ChangeKind.Visual), report.Count(ChangeKind.PageAdded), report.Count(ChangeKind.PageRemoved)));
                    foreach (var c in report.Changes)
                        Console.WriteLine(L.F("Pdf_CompareLine", c.OldPage is { } o ? (o + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-",
                            c.NewPage is { } n && c.Kind != ChangeKind.PageRemoved ? (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-",
                            c.Kind switch
                            {
                                ChangeKind.Changed => L.F("Ui_CmpRowChanged", c.OldText, c.NewText),
                                ChangeKind.Inserted => L.F("Ui_CmpRowAdded", c.NewText),
                                ChangeKind.Deleted => L.F("Ui_CmpRowDeleted", c.OldText),
                                ChangeKind.Visual => c.OldText.Length > 0 ? L.F("Cmp_NoteSize", c.OldText, c.NewText) : L.Get("Ui_CmpRowVisual"),
                                ChangeKind.PageAdded => L.Get("Ui_CmpRowPageAdded"),
                                _ => L.F("Ui_CmpRowPageRemoved", c.OldPage!.Value + 1),
                            }));
                    if (opt.TryGetValue("-o", out var marked))
                        Console.WriteLine(L.F("Ui_CompareMarked", marked, PdfComparer.MarkUp(newer, marked, report)));
                    return 0;
                }
                case ["from-images", .. var images] when images.Length > 0 && opt.ContainsKey("-o"):
                {
                    var o = new ImagePageOptions(opt.GetValueOrDefault("--page", "a4").ToLowerInvariant(),
                        opt.TryGetValue("--margin", out var mg) ? double.Parse(mg, System.Globalization.CultureInfo.InvariantCulture) : 0,
                        opt.TryGetValue("--dpi", out var dpi) ? double.Parse(dpi, System.Globalization.CultureInfo.InvariantCulture) : 150);
                    var r = PdfFromImages.Create(images, opt["-o"], o);
                    Console.WriteLine(L.F("Pdf_FromImages", opt["-o"], r.Pages, r.KeptAsIs));
                    return 0;
                }
                case ["stamp", var input] when opt.TryGetValue("--remove", out var what) && !opt.ContainsKey("--header") && !opt.ContainsKey("--footer") && !opt.ContainsKey("--watermark"):
                {
                    bool headers = what is "headers" or "all", watermarks = what is "watermarks" or "all";
                    if (!headers && !watermarks) throw new PageAssemblyException("--remove takes headers, watermarks or all", "Err_StampRemoveArg");
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, Path.GetFileNameWithoutExtension(input) + "_unstamped.pdf"));
                    Console.WriteLine(L.F("Ui_StampRemoved", output, PdfStamper.Remove(input, output, headers, watermarks)));
                    return 0;
                }
                case ["stamp", var input] when opt.ContainsKey("--header") || opt.ContainsKey("--footer") || opt.ContainsKey("--watermark"):
                {
                    static (string L, string C, string R) Split3(string? v)
                    {
                        if (v is null) return ("", "", "");
                        var p = v.Split('|');
                        return p.Length switch { 1 => ("", p[0], ""), 2 => (p[0], "", p[1]), _ => (p[0], p[1], string.Join("|", p[2..])) };
                    }
                    double Num(string key, double fallback) =>
                        opt.TryGetValue(key, out var v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;
                    var (hl, hc, hr) = Split3(opt.GetValueOrDefault("--header"));
                    var (fl, fc, fr) = Split3(opt.GetValueOrDefault("--footer"));
                    var options = new StampOptions
                    {
                        HeaderLeft = hl, HeaderCenter = hc, HeaderRight = hr, FooterLeft = fl, FooterCenter = fc, FooterRight = fr,
                        FontSize = Num("--size", 9), Pages = opt.GetValueOrDefault("--pages"),
                        Bates = opt.ContainsKey("--bates") || opt.ContainsKey("--bates-start")
                            ? new BatesNumbers(opt.GetValueOrDefault("--bates", ""), (int)Num("--bates-start", 1), (int)Num("--bates-digits", 6)) : null,
                        ReplaceExisting = opt.ContainsKey("--replace-existing"),
                        Watermark = opt.TryGetValue("--watermark", out var wm)
                            ? new Watermark(wm, Num("--watermark-size", 0), Num("--angle", 45), Num("--opacity", 0.3), 0x808080, opt.ContainsKey("--behind")) : null,
                    };
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_stamped.pdf"));
                    var result = PdfStamper.Apply(input, output, options);
                    Console.WriteLine(result.FirstBates is { } first
                        ? L.F("Pdf_StampedBates", output, result.Pages, first, result.LastBates!)
                        : L.F("Pdf_Stamped", output, result.Pages));
                    return 0;
                }
                case ["replace-text", var input] when opt.ContainsKey("--with") && opt.TryGetValue("--paragraph", out var containing):
                {
                    // the paragraph that holds the text, set again in its width
                    int? inPage = opt.TryGetValue("--page", out var pp) && int.TryParse(pp, out var ppn) ? ppn - 1 : null;
                    var para = PdfTextEditor.ParagraphContaining(input, containing, inPage);
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_edited.pdf"));
                    var results = PdfTextEditor.Reflow(input, output, [new TextEdit(para.Page, para.Box, opt["--with"].Replace("\\n", "\n"))]);
                    foreach (var r in results)
                        Console.WriteLine(L.F(r.KeptFont ? "Pdf_TextEditKept" : "Pdf_TextEditSimilar", r.Page + 1, r.Old, r.New, r.Font));
                    Console.WriteLine(L.F("Pdf_TextEdited", output, results.Count));
                    return 0;
                }
                case ["replace-text", var input] when opt.ContainsKey("--with") && (opt.ContainsKey("--find") || opt.ContainsKey("--box")):
                {
                    var with = opt["--with"];
                    var output = opt.GetValueOrDefault("-o", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,
                        Path.GetFileNameWithoutExtension(input) + "_edited.pdf"));
                    int? page = opt.TryGetValue("--page", out var pg) && int.TryParse(pg, out var pn) ? pn - 1 : null;
                    var results = opt.TryGetValue("--find", out var find)
                        ? PdfTextEditor.Replace(input, output, find.Split(Many)[0], with, page, matchCase: !opt.ContainsKey("--ignore-case"),
                                                wholeWords: opt.ContainsKey("--whole-words"))
                        : PdfTextEditor.Apply(input, output, [.. ParseBoxes(opt["--box"]).Select(b => new TextEdit(b.Page, b.Rect, with))]);
                    foreach (var r in results)
                        Console.WriteLine(L.F(r.KeptFont ? "Pdf_TextEditKept" : "Pdf_TextEditSimilar", r.Page + 1, r.Old, r.New, r.Font));
                    Console.WriteLine(L.F("Pdf_TextEdited", output, results.Count));
                    return 0;
                }
                default:
                    Console.Error.WriteLine(L.Get("UsagePdf"));
                    return 2;
            }
        }
        catch (PrintException e)
        {
            Console.Error.WriteLine(L.F("Pdf_Failed", Errors.Text(e)));
            return 1;
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
            Console.Error.WriteLine(L.F("Pdf_Failed", Washi.Core.Localization.Errors.Text(e)));
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

    /// <summary>One line per kind: what it is, how many, and the details (font names, pages…).</summary>
    internal static IEnumerable<string> ArchiveLines(IEnumerable<ArchiveItem> items) =>
        items.Select(i =>
        {
            var name = i.Kind == "Flattened" ? L.Get("Ui_FlattenTitle") : L.Get("Pdfa_" + i.Kind);
            var details = i.Kind == "Fonts" ? i.Details.Select(d => d.Contains(" → ", StringComparison.Ordinal)
                                                  ? L.F("Ui_PdfaFontReplaced", d[..d.IndexOf(" → ", StringComparison.Ordinal)], d[(d.IndexOf(" → ", StringComparison.Ordinal) + 3)..]) : d)
                                            : i.Details;
            var list = string.Join(", ", details.Take(8)) + (i.Details.Count > 8 ? ", …" : "");
            return list.Length > 0 ? $"{name} ({i.Count}): {list}" : $"{name} ({i.Count})";
        });

    private static (string[] Positional, Dictionary<string, string> Options) Parse(string[] args)
    {
        var pos = new List<string>();
        var opt = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (Flags.Contains(args[i])) opt[args[i]] = "";
            else if (args[i] is "--box" or "--add" or "--replace" && i + 1 < args.Length) opt[args[i]] = opt.TryGetValue(args[i], out var b) ? b + ";" + args[++i] : args[++i];
            else if (args[i] is "--find" or "--regex" && i + 1 < args.Length) opt[args[i]] = opt.TryGetValue(args[i], out var f) ? f + Many + args[++i] : args[++i];
            else if (args[i].StartsWith('-') && i + 1 < args.Length) opt[args[i]] = args[++i];
            else pos.Add(args[i]);
        }
        return ([.. pos], opt);
    }
}
