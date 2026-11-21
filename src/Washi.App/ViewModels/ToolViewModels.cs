// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.Ai;
using Washi.App.Services;
using Washi.Core.Localization;
using Washi.Core.Ocr;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>A language chip in the OCR panel.</summary>
public sealed partial class LanguageOption(string code, string text, bool selected) : ObservableObject
{
    public string Code { get; } = code;
    public string Text { get; } = text;
    [ObservableProperty] private bool _isSelected = selected;
}

/// <summary>Make a scan searchable: recognise pages and add the invisible text layer.</summary>
public sealed partial class OcrToolViewModel(MainViewModel main) : ObservableObject
{
    public static IReadOnlyList<Choice<string>> Engines =>
    [
        new("auto", L.Get("Ui_EngineAuto")), new("vision", "Apple Vision"), new("tesseract", "Tesseract"), new("ai", L.Get("Ui_EngineAi")),
    ];

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("ja", "日本語", true), new("zh-Hans", "简体中文", false), new("zh-Hant", "繁體中文", false), new("ko", "한국어", false),
        new("en", "English", true), new("fr", "Français", false), new("de", "Deutsch", false),
    ];

    [ObservableProperty] private Choice<string> _engine = Engines[0];
    [ObservableProperty] private bool _force;
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private string _progress = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRun))] private int _pagesToRecognize;

    public bool CanRun => main.HasDocument && PagesToRecognize > 0;

    /// <summary>The AI engine for the "ai" choice; tests replace it.</summary>
    public Func<IAiModel> ModelFactory { get; set; } = () => AiSettings.Load().CreateModel();

    partial void OnForceChanged(bool value) => _ = RefreshPlanAsync();

    private OcrOptions Options => new()
    {
        Languages = [.. Languages.Where(l => l.IsSelected).Select(l => l.Code)],
        Force = Force,
    };

    public async Task RefreshPlanAsync()
    {
        if (main.PdfPath is not { } path) { PlanText = ""; PagesToRecognize = 0; return; }
        try
        {
            var plan = await Task.Run(() => OcrPipeline.Plan(path, Options));
            PagesToRecognize = plan.Count;
            PlanText = plan.Count == 0 ? L.Get("Ui_OcrNothing") : L.F("Ui_OcrPlan", plan.Count, main.PageCount);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException)
        {
            PlanText = Errors.Text(e);
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        IOcrEngine engine;
        try
        {
            if (Engine.Value == "ai")
            {
                var model = ModelFactory();
                engine = new AiOcrEngine(model);
                var payload = AiOcrEngine.DescribeOcr(model, Math.Max(1, PagesToRecognize));
                if (payload.OffDevice && !await main.Dialogs.ConfirmAsync(L.Get("Ui_AiConfirmTitle"),
                        L.F("Ai_WillSend", payload.Model, payload.Summary()), L.Get("Ui_Send"), L.Get("Ui_Cancel")))
                {
                    main.Status = L.Get("Ai_Cancelled");
                    return;
                }
            }
            else engine = OcrEngines.Create(Engine.Value);
        }
        catch (Exception e) when (e is AiException or OcrException)
        {
            main.Status = L.F("Ui_Error", Errors.Text(e));
            return;
        }

        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_ocr.pdf") is not { } output) return;
        string? written = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            var progress = new Progress<(int Done, int Total)>(p =>
                Progress = p.Total == 0 ? "" : L.F("Ui_OcrProgress", Math.Min(p.Done + 1, p.Total), p.Total));
            main.Status = L.F("Ocr_Engine", engine.Describe, PagesToRecognize);
            try
            {
                var r = await OcrPipeline.RunAsync(pdf, tmp, engine, Options, progress);
                if (r.Output is null) { main.Status = L.Get("Ui_OcrNothing"); return; }
                File.Move(tmp, output, overwrite: true);   // the save dialog already asked about overwriting
                written = output;
                main.Status = L.F("Ocr_Done", r.Recognized, Path.GetFileName(output))
                              + (engine is TesseractOcr { Missing.Count: > 0 } t ? "  " + L.F("Ocr_MissingLangs", string.Join(", ", t.Missing)) : "");
            }
            catch (Exception e) when (e is AiException or HttpRequestException or System.ClientModel.ClientResultException
                                          or Anthropic.Exceptions.AnthropicException or OperationCanceledException or TimeoutException)
            {
                main.Status = L.F("Ai_Error", UiErrors.Text(e));
            }
            finally
            {
                Progress = "";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        });
        if (written is not null)
        {
            var status = main.Status;
            await main.LoadAsync(written);   // show the searchable result
            main.Status = status;
        }
    }
}

/// <summary>One file in the Combine list.</summary>
public sealed record CombineItem(string Path, int Pages, bool Protected = false)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Detail => L.F("Ui_PageCount", Pages) + (Protected ? " · " + L.Get("Ui_ProtectedFile") : "");
}

/// <summary>Combine files into one PDF (form fields stay fillable).</summary>
public sealed partial class CombineToolViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<CombineItem> Files { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(RemoveCommand))]
    private CombineItem? _selected;

    public bool CanCombine => Files.Count >= 2 && Files.All(f => !f.Protected);

    /// <summary>The open document is the natural first file.</summary>
    public void OfferCurrentDocument()
    {
        if (Files.Count == 0 && main.PdfPath is { } p) _ = AddAsync([p]);
    }

    public async Task AddAsync(IEnumerable<string> paths)
    {
        await main.Run(async () =>
        {
            foreach (var path in paths)
            {
                var (pages, mayAssemble) = await Task.Run(() => PageAssembly.Inspect(path));
                Files.Add(new CombineItem(path, pages, !mayAssemble));
                if (!mayAssemble) main.Status = L.F("Ui_ProtectedNote", Path.GetFileName(path));
            }
        });
        OnPropertyChanged(nameof(CanCombine));
    }

    [RelayCommand]
    private async Task AddFilesAsync() => await AddAsync(await main.Dialogs.OpenPdfsAsync());

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveDown() => Move(1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (Selected is not { } s) return;
        int i = Files.IndexOf(s);
        Files.Remove(s);
        Selected = Files.Count == 0 ? null : Files[Math.Min(i, Files.Count - 1)];
        OnPropertyChanged(nameof(CanCombine));
    }

    private void Move(int delta)
    {
        if (Selected is not { } s) return;
        int i = Files.IndexOf(s), j = i + delta;
        if (j < 0 || j >= Files.Count) return;
        Files.Move(i, j);
        Selected = s;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (!CanCombine) return;
        var first = Files[0].Path;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(first) + "_combined.pdf") is not { } output) return;
        var inputs = Files.Select(f => f.Path).ToList();
        string? written = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            try
            {
                int pages = await Task.Run(() => PageAssembly.Merge(inputs, tmp));
                File.Move(tmp, output, overwrite: true);
                written = output;
                main.Status = L.F("Pdf_Merged", inputs.Count, Path.GetFileName(output), pages,
                                  Washi.Core.Forms.AcroFormFields.List(output).Count);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (written is not null)
        {
            var status = main.Status;
            Files.Clear();
            OnPropertyChanged(nameof(CanCombine));
            await main.LoadAsync(written);
            main.Status = status;
        }
    }
}

/// <summary>Split the open document into files by page ranges.</summary>
public sealed partial class SplitToolViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview), nameof(CanSplit))] private string _ranges = "";

    public void Reset()
    {
        Ranges = "";
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(CanSplit));
    }

    private List<(int From, int To)>? Parsed(out string? error)
    {
        error = null;
        if (main.PageCount == 0) return null;
        try { return PageAssembly.ParseRanges(Ranges, main.PageCount); }
        catch (PageAssemblyException e) { error = Errors.Text(e); return null; }
    }

    public bool CanSplit => Parsed(out _) is { Count: > 0 };

    /// <summary>What will be written, e.g. "3 files: p1-3, p5, p6".</summary>
    public string Preview
    {
        get
        {
            var r = Parsed(out var error);
            if (r is null) return error ?? "";
            var names = r.Select(x => x.From == x.To ? $"p{x.From}" : $"p{x.From}-{x.To}").ToList();
            return L.F("Ui_SplitPreview", r.Count, string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", …" : ""));
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (main.PdfPath is not { } pdf || Parsed(out _) is not { Count: > 0 } ranges) return;
        if (await main.Dialogs.PickFolderAsync() is not { } folder) return;
        await main.Run(async () =>
        {
            var parts = await Task.Run(() => PageAssembly.Split(pdf, ranges, folder));
            main.Status = L.F("Ui_SplitDone", parts.Count, folder);
        });
    }
}

/// <summary>A box drawn in the Erase tool (0-based page, page points).</summary>
public sealed record EraseBox(int Page, Washi.Core.PdfRect Rect)
{
    public string Label => L.F("Ui_EraseBoxLabel", Page + 1, (int)Rect.Width, (int)Rect.Height);
}

/// <summary>
/// Erase printed content: draw boxes over text that is already printed on the page (wrong data on a form), see
/// what will go, then write a copy where it is removed for real (<see cref="PdfEraser"/>).
/// </summary>
public sealed partial class EraseToolViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<EraseBox> Boxes { get; } = [];

    [ObservableProperty] private bool _keepColored = true;
    [ObservableProperty] private bool _rules;

    /// <summary>After erasing, put an editable field in each box, holding the text that was printed there.</summary>
    [ObservableProperty] private bool _makeEditable = true;
    [ObservableProperty] private string _planText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanErase))] private int _planned;

    /// <summary>What will go (red) and what stays (amber), for the canvas.</summary>
    public IReadOnlyList<EraseTarget> Targets { get; private set; } = [];
    public IReadOnlyList<EraseKept> Kept { get; private set; } = [];

    /// <summary>Raised when boxes, targets or kept items change (the canvas redraws).</summary>
    public event Action? Changed;

    public bool CanErase => main.HasDocument && Planned > 0;
    public bool HasBoxes => Boxes.Count > 0;

    partial void OnKeepColoredChanged(bool value) => _ = RefreshPlanAsync();
    partial void OnRulesChanged(bool value) => _ = RefreshPlanAsync();

    private EraseOptions Options => new(KeepColored: KeepColored, Rules: Rules);

    public void Reset()
    {
        Boxes.Clear();
        Targets = [];
        Kept = [];
        Planned = 0;
        PlanText = "";
        OnPropertyChanged(nameof(HasBoxes));
        Changed?.Invoke();
    }

    public async Task AddBoxAsync(Washi.Core.PdfRect rect)
    {
        Boxes.Add(new EraseBox(main.PageIndex, rect));
        OnPropertyChanged(nameof(HasBoxes));
        await RefreshPlanAsync();
    }

    [RelayCommand]
    private async Task RemoveBoxAsync(EraseBox? box)
    {
        if (box is null) return;
        Boxes.Remove(box);
        OnPropertyChanged(nameof(HasBoxes));
        await RefreshPlanAsync();
    }

    [RelayCommand]
    private void Clear() => Reset();

    public async Task RefreshPlanAsync()
    {
        if (main.PdfPath is not { } path || Boxes.Count == 0)
        {
            Targets = []; Kept = []; Planned = 0; PlanText = "";
            Changed?.Invoke();
            return;
        }
        var regions = Boxes.Select(b => new EraseRegion(b.Page, b.Rect)).ToList();
        try
        {
            var (targets, kept) = await MainViewModel.Pdfium(() => PdfEraser.Plan(path, regions, Options));
            Targets = targets;
            Kept = kept;
            Planned = targets.Count;
            PlanText = L.F("Ui_ErasePlan", targets.Count(t => t.Kind == EraseKind.Text), targets.Count(t => t.Kind == EraseKind.Image),
                           targets.Count(t => t.Kind == EraseKind.Drawing))
                       + (kept.Count > 0 ? "\n" + L.F("Ui_EraseKeptNote", kept.Count, L.Get("Erase_Kept" + kept[0].Why)) : "");
        }
        catch (Exception e) when (e is EraseException or IOException)
        {
            Targets = []; Kept = []; Planned = 0;
            PlanText = Errors.Text(e);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Writes &lt;name&gt;_erased.pdf (or _erased_2, … : nothing is overwritten) and opens it. With
    /// <see cref="MakeEditable"/>, each box then holds a field with the text that was printed there (read from the PDF,
    /// or by OCR of the erased ink), shown in Preview so it can be edited in place.
    /// </summary>
    [RelayCommand]
    private async Task EraseAsync()
    {
        if (main.PdfPath is not { } path || Boxes.Count == 0) return;
        var (output, replace) = main.WorkingCopy(path);
        var boxes = Boxes.ToList();
        var regions = boxes.Select(b => new EraseRegion(b.Page, b.Rect)).ToList();
        EraseReport? report = null;
        PrintedResult? printed = null;
        await main.Run(async () =>
        {
            try
            {
                if (MakeEditable)
                {
                    var cells = boxes.Select((b, i) => new PrintedCell(i.ToString(System.Globalization.CultureInfo.InvariantCulture), b.Page, b.Rect)).ToList();
                    printed = await PrintedText.MoveIntoFieldsAsync(path, output, cells, main.OcrFactory, main.OcrLanguageOrder, Options);
                    report = printed.Erase;
                }
                else report = await MainViewModel.Pdfium(() => PdfEraser.Erase(path, output, regions, Options));
            }
            catch (EraseException e)
            {
                main.Status = L.F("Ui_EraseFailed", Errors.Text(e));
            }
            catch (OcrException e)
            {
                main.Status = L.F("Ui_EraseFailed", Errors.Text(e));
            }
        });
        if (report is null) return;
        output = main.Commit(output, replace);
        var page = main.PageIndex;
        Reset();
        // fields the user already has (names, values, corrections) stay; only a document without fields is opened anew
        if (main.Fields.Count > 0)
        {
            await main.SwapDocumentAsync(output);
            foreach (var f in main.Fields.Where(f => boxes.Any(b => b.Page == f.Page && b.Rect.Intersect(f.Rect).Area > f.Rect.Area * 0.5)))
                f.Prefilled = "";   // its print is gone
        }
        else
        {
            if (printed is not null) main.ActiveTool = Tool.Form;   // the copy's fields are found, then the boxes join them
            await main.LoadAsync(output);
            if (page < main.PageCount) main.PageIndex = page;
        }
        if (printed is null)
        {
            main.Status = L.F("Ui_Erased", Path.GetFileName(output), report.Removed, report.RemainingChars, report.ImagesDropped);
            return;
        }
        int placed = 0;
        FieldViewModel? last = null;
        foreach (var v in printed.Values.Where(v => v.Text.Length > 0))
        {
            var box = boxes[int.Parse(v.Key, System.Globalization.CultureInfo.InvariantCulture)];
            // a field at that spot takes the value (unless the user already typed one); otherwise a new text field the size of the box
            var (cx, cy) = box.Rect.Center;
            var field = main.Fields.Where(f => f.Page == box.Page && f.Kind != Washi.Masume.FieldKind.Checkbox && f.Rect.Contains(cx, cy))
                                   .OrderByDescending(f => f.Rect.IoU(box.Rect)).FirstOrDefault()
                        ?? main.AddField(box.Rect, box.Page);
            if (field.Value.Length == 0) { field.Value = v.Text; placed++; }
            last = field;
        }
        main.ActiveTool = Tool.Form;
        main.PreviewMode = true;
        if (last is not null) main.SelectedField = last;
        main.Status = L.F("Ui_ErasedEditable", Path.GetFileName(output), report.Removed, placed);
    }

    internal static string FreeName(string input)
    {
        var dir = Path.GetDirectoryName(input)!;
        var stem = Path.GetFileNameWithoutExtension(input);
        var name = Path.Combine(dir, stem + "_erased.pdf");
        for (int n = 2; File.Exists(name); n++) name = Path.Combine(dir, $"{stem}_erased_{n}.pdf");
        return name;
    }
}
