// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core.Fonts;
using Washi.Core.Forms;
using Washi.Core.Localization;
using Washi.Core.Pdfium;
using Washi.Masume;
using Washi.Tools;

namespace Washi.App.ViewModels;

public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>The tools in the left rail, as in Acrobat's "All tools".</summary>
public enum Tool { Form, Ocr, Combine, Split, Ask }

/// <summary>What the right side panel shows.</summary>
public enum SidePanel { None, Pages, Fields }

/// <summary>A page thumbnail in the Pages panel.</summary>
public sealed partial class PageThumb(int index) : ObservableObject
{
    public int Index { get; } = index;
    public string Label => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// The field review editor: open a PDF, let Masume suggest fields, let a person correct them,
/// then save fields.json or write the fillable PDF.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>Pixels per point of the page bitmap (the view scales it by Zoom).</summary>
    public const double RenderScale = 2.0;

    private readonly IDialogs _dialogs;

    public MainViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;
        Fields.CollectionChanged += OnFieldsChanged;
        Status = L.Get("Ui_StartHint");
        Ocr = new OcrToolViewModel(this);
        Combine = new CombineToolViewModel(this);
        Split = new SplitToolViewModel(this);
        Ask = new AskToolViewModel(this);
    }

    internal IDialogs Dialogs => _dialogs;

    public OcrToolViewModel Ocr { get; }
    public CombineToolViewModel Combine { get; }
    public SplitToolViewModel Split { get; }
    public AskToolViewModel Ask { get; }

    // ------------------------------------------------------------------ state
    public ObservableCollection<FieldViewModel> Fields { get; } = [];
    public ObservableCollection<FieldViewModel> PageFields { get; } = [];
    public ObservableCollection<PageThumb> Thumbnails { get; } = [];

    // ------------------------------------------------------------------ tools and panels
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsFormTool), nameof(IsOcrTool), nameof(IsCombineTool), nameof(IsSplitTool),
                                                  nameof(IsAskTool), nameof(ShowFieldsPanel), nameof(ShowSidePanel), nameof(ShowEditTools))]
    private Tool _activeTool = Tool.Form;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPagesPanel), nameof(ShowFieldsPanel), nameof(ShowSidePanel))]
    private SidePanel _sidePanel = SidePanel.Fields;

    /// <summary>Clicking the active tool in the rail again hides its panel (more room for the page).</summary>
    [ObservableProperty] private bool _toolPanelOpen = true;

    public bool IsFormTool { get => ActiveTool == Tool.Form; set { if (value) ActiveTool = Tool.Form; } }
    public bool IsOcrTool { get => ActiveTool == Tool.Ocr; set { if (value) ActiveTool = Tool.Ocr; } }
    public bool IsCombineTool { get => ActiveTool == Tool.Combine; set { if (value) ActiveTool = Tool.Combine; } }
    public bool IsSplitTool { get => ActiveTool == Tool.Split; set { if (value) ActiveTool = Tool.Split; } }
    public bool IsAskTool { get => ActiveTool == Tool.Ask; set { if (value) ActiveTool = Tool.Ask; } }

    /// <summary>Toggle buttons in the right rail: one panel at a time, a second click closes it.</summary>
    public bool ShowPagesPanel
    {
        get => SidePanel == SidePanel.Pages;
        set => SidePanel = value ? SidePanel.Pages : SidePanel == SidePanel.Pages ? SidePanel.None : SidePanel;
    }

    public bool ShowFieldsPanel
    {
        get => SidePanel == SidePanel.Fields && IsFormTool;
        set => SidePanel = value ? SidePanel.Fields : SidePanel == SidePanel.Fields ? SidePanel.None : SidePanel;
    }

    public bool ShowSidePanel => HasDocument && (ShowPagesPanel || ShowFieldsPanel);

    /// <summary>The floating select / draw / delete toolbar belongs to the form tool.</summary>
    public bool ShowEditTools => HasDocument && IsFormTool;

    partial void OnActiveToolChanged(Tool value)
    {
        if (value != Tool.Form) { DrawMode = false; PreviewMode = false; }
        if (value == Tool.Form && PdfPath is { } p && _detectedFor != p) _ = DetectAsync();
        if (value == Tool.Ocr) _ = Ocr.RefreshPlanAsync();
        if (value == Tool.Combine) Combine.OfferCurrentDocument();
    }

    private string? _detectedFor;

    // ------------------------------------------------------------------ viewport
    /// <summary>Size of the document area, set by the view; used to fit the page.</summary>
    public double ViewportWidth { get => _viewportWidth; set { _viewportWidth = value; if (_fitWidthMode) FitWidth(); } }
    public double ViewportHeight { get; set; }
    private double _viewportWidth;

    /// <summary>After "fit width" the page follows the window and panels until the user zooms by hand.</summary>
    private bool _fitWidthMode = true;
    private const double PageMargin = 56;   // the page's margin on both sides, in the view
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDocument), nameof(Title), nameof(FileName), nameof(ShowSidePanel), nameof(ShowEditTools))]
    private string? _pdfPath;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PageText))] private int _pageCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PageText))] private int _pageIndex;
    [ObservableProperty] private Bitmap? _pageImage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayWidth))] private double _pageWidth;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayHeight))] private double _pageHeight;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayWidth), nameof(DisplayHeight), nameof(ZoomText))] private double _zoom = 1.0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private FieldViewModel? _selectedField;
    [ObservableProperty] private bool _drawMode;

    /// <summary>Preview: try the form as a viewer would. Click a field to type into it, a checkbox to tick it.</summary>
    [ObservableProperty] private bool _previewMode;

    public int FilledCount => Fields.Count(f => f.Value.Length > 0);

    public bool IsSelectMode => !DrawMode && !PreviewMode;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FontText))] private string? _embedFontPath;
    [ObservableProperty] private Choice<string> _formLanguage = Languages[0];
    [ObservableProperty] private string _fieldSummary = "";

    public bool HasDocument => PdfPath is not null;
    public bool HasSelection => SelectedField is not null;
    public double DisplayWidth => PageWidth * Zoom;
    public double DisplayHeight => PageHeight * Zoom;
    public string ZoomText => $"{Zoom:P0}";
    public string PageText => PageCount == 0 ? "" : L.F("Ui_PageOf", PageIndex + 1, PageCount);
    public string Title => PdfPath is null ? "Washi" : $"{Path.GetFileName(PdfPath)} — Washi";
    public string FileName => PdfPath is null ? "" : Path.GetFileName(PdfPath);
    public string FontText => EmbedFontPath is null ? L.Get("Ui_NoFont") : Path.GetFileName(EmbedFontPath);

    public static IReadOnlyList<Choice<string>> Languages =>
    [
        new("auto", L.Get("Ui_LangAuto")), new("ja", "日本語"), new("zh-Hans", "简体中文"), new("zh-Hant", "繁體中文"),
        new("ko", "한국어"), new("en", "English"), new("fr", "Français"), new("de", "Deutsch"),
    ];

    public static IReadOnlyList<Choice<FieldKind>> Kinds =>
        [.. Enum.GetValues<FieldKind>().Select(k => new Choice<FieldKind>(k, L.Get("Kind_" + k.ToString().ToLowerInvariant())))];

    public static IReadOnlyList<Choice<int>> Alignments =>
        [new(0, L.Get("Ui_AlignLeft")), new(1, L.Get("Ui_AlignCenter")), new(2, L.Get("Ui_AlignRight"))];

    public string Hint => L.Get(PreviewMode ? "Ui_PreviewHint" : DrawMode ? "Ui_DrawHint" : "Ui_Hint");

    partial void OnDrawModeChanged(bool value)
    {
        if (value) PreviewMode = false;
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(IsSelectMode));
    }

    partial void OnPreviewModeChanged(bool value)
    {
        if (value) DrawMode = false;
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(IsSelectMode));
    }

    /// <summary>Raised when a text field should get the inline editor (Preview); the view places it.</summary>
    public event Action<FieldViewModel>? EditRequested;

    /// <summary>Preview click on a field: a checkbox toggles, anything else opens the inline editor.</summary>
    public void ActivateField(FieldViewModel f)
    {
        SelectedField = f;
        if (f.Kind == FieldKind.Checkbox) f.Value = f.IsChecked ? "" : "true";
        else EditRequested?.Invoke(f);
    }

    [RelayCommand] private void TogglePreview() { if (HasDocument && IsFormTool) PreviewMode = !PreviewMode; }

    partial void OnPageIndexChanged(int value)
    {
        RefreshPageFields();
        SelectedField = null;
        foreach (var t in Thumbnails) t.IsCurrent = t.Index == value;
        _ = RenderPageAsync();
    }

    // ------------------------------------------------------------------ commands
    [RelayCommand]
    private async Task OpenAsync()
    {
        if (await _dialogs.OpenPdfAsync() is not { } path) return;
        await LoadAsync(path);
    }

    /// <summary>Opens a PDF and detects its fields.</summary>
    public async Task LoadAsync(string path)
    {
        bool opened = false;
        await Run(async () =>
        {
            int count = await Pdfium(() => { using var d = PdfiumDocument.Open(path); return d.PageCount; });
            PdfPath = path;
            _detectedFor = null;
            Fields.Clear();
            FieldSummary = "";
            PageCount = count;
            Thumbnails.Clear();
            for (int i = 0; i < count; i++) Thumbnails.Add(new PageThumb(i) { IsCurrent = i == 0 });
            _fitPending = true;
            if (PageIndex != 0) PageIndex = 0; else { RefreshPageFields(); await RenderPageAsync(); }
            Status = L.F("Ui_Opened", Path.GetFileName(path), count);
            opened = true;
        });
        if (!opened) return;
        _ = RenderThumbnailsAsync(path);
        Split.Reset();
        Ask.Reset();
        if (ActiveTool == Tool.Form) await DetectAsync();
        else if (ActiveTool == Tool.Ocr) await Ocr.RefreshPlanAsync();
        else if (ActiveTool == Tool.Combine) Combine.OfferCurrentDocument();
    }

    /// <summary>Files dropped on the window: Combine collects them, other tools open the first PDF.</summary>
    public async Task DropAsync(IReadOnlyList<string> paths)
    {
        var pdfs = paths.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        if (pdfs.Count == 0) return;
        if (ActiveTool == Tool.Combine) await Combine.AddAsync(pdfs);
        else await LoadAsync(pdfs[0]);
    }

    [RelayCommand]
    private void CloseDocument()
    {
        PdfPath = null;
        Fields.Clear();
        Thumbnails.Clear();
        PageCount = 0;
        PageImage = null;
        FieldSummary = "";
        SelectedField = null;
        DrawMode = false;
        PreviewMode = false;
        Status = L.Get("Ui_StartHint");
    }

    [RelayCommand] private void ShowTool(Tool tool) => ActiveTool = tool;

    [RelayCommand]
    private async Task DetectAsync()
    {
        if (PdfPath is not { } path) return;
        await Run(async () =>
        {
            Status = L.Get("Ui_Detecting");
            var (fields, _) = await Pdfium(() => FormPipeline.Detect(path));
            if (PdfPath != path) return;   // another file was opened meanwhile
            ReplaceFields(fields);
            _detectedFor = path;
            Status = L.F("Ui_Detected", Fields.Count, Summary());
        });
    }

    [RelayCommand]
    private async Task LoadFieldsAsync()
    {
        if (PdfPath is null || await _dialogs.OpenFieldsAsync() is not { } json) return;
        await Run(async () =>
        {
            ReplaceFields(FieldsJson.Parse(await File.ReadAllTextAsync(json)));
            Status = L.F("Ui_Loaded", Fields.Count, Path.GetFileName(json));
        });
    }

    [RelayCommand]
    private async Task SaveFieldsAsync()
    {
        if (PdfPath is not { } pdf) return;
        if (await _dialogs.SaveFieldsAsync(Path.GetFileNameWithoutExtension(pdf) + "_fields.json") is not { } json) return;
        await Run(async () =>
        {
            await File.WriteAllTextAsync(json, FieldsJson.Serialize(Fields.Select(f => f.ToDetected())));
            Status = L.F("Ui_Saved", Path.GetFileName(json));
        });
    }

    [RelayCommand]
    private async Task ChooseFontAsync()
    {
        var font = await _dialogs.OpenFontAsync();
        if (font is null) { EmbedFontPath = null; return; }
        await Run(async () =>
        {
            var f = await Task.Run(() => OpenTypeFont.Load(font));
            if (!f.EmbeddingAllowedForEditing) throw new FontNotUsableException("license", f.FsType);
            EmbedFontPath = font;
        });
    }

    [RelayCommand]
    private void ClearFont() => EmbedFontPath = null;

    [RelayCommand]
    private async Task MakeFillableAsync()
    {
        if (PdfPath is not { } pdf) return;
        if (await _dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_fillable.pdf") is not { } output) return;
        await Run(async () =>
        {
            var fields = Fields.Select(f => f.ToDetected().ToFormField()).ToList();
            // what was typed in Preview goes into the file (by field name)
            var values = new Dictionary<string, string>();
            foreach (var f in Fields.Where(f => f.Value.Length > 0)) values[f.Name] = f.Value;
            var font = EmbedFontPath is { } fp ? await Task.Run(() => OpenTypeFont.Load(fp)) : null;
            int filled = await Pdfium(() =>
            {
                var text = FormPipeline.Text(pdf);
                var (script, _) = FormPipeline.ChooseScript(FormLanguage.Value, text, L.Culture);
                var tmp = output + ".tmp";
                var tmp2 = output + ".fill.tmp";
                try
                {
                    AcroFormWriter.Write(pdf, tmp, fields, script, font);
                    int n = 0;
                    if (values.Count > 0)
                    {
                        n = AcroFormFields.Fill(tmp, tmp2, values, font).Filled;
                        File.Move(tmp2, tmp, overwrite: true);
                    }
                    File.Move(tmp, output, overwrite: true);   // the save dialog already asked about overwriting
                    return n;
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    if (File.Exists(tmp2)) File.Delete(tmp2);
                }
            });
            Status = filled > 0 ? L.F("Ui_MadeFilled", Path.GetFileName(output), filled) : L.F("Ui_Made", Path.GetFileName(output));
        });
    }

    /// <summary>Builds the AI model; tests replace it.</summary>
    public Func<Washi.Ai.IAiModel> ModelFactory { get; set; } = () => Washi.Ai.AiSettings.Load().CreateModel();

    [RelayCommand]
    private async Task NameWithAiAsync()
    {
        if (PdfPath is not { } pdf || Fields.Count == 0) return;
        Washi.Ai.IAiModel model;
        try { model = ModelFactory(); }
        catch (Washi.Ai.AiException e) { Status = L.F("Ai_Error", e.Message); return; }

        var fields = Fields.Select(f => f.ToDetected()).ToList();
        var payload = Washi.Ai.AiFeatures.DescribeNaming(model, fields);
        if (payload.OffDevice && !await _dialogs.ConfirmAsync(L.Get("Ui_AiConfirmTitle"),
                L.F("Ai_WillSend", payload.Model, payload.Summary()), L.Get("Ui_Send"), L.Get("Ui_Cancel")))
        {
            Status = L.Get("Ai_Cancelled");
            return;
        }
        await Run(async () =>
        {
            Status = L.F("Ai_Working", model.Describe);
            try
            {
                var r = await Washi.Ai.AiFeatures.NameFieldsAsync(model, pdf, fields);
                ReplaceFields(r.Fields);
                Status = L.F("Ai_Named", Path.GetFileName(pdf), r.Fields.Count, r.Renamed, r.KindsChanged, model.Describe);
            }
            catch (Exception e) when (e is Washi.Ai.AiException or HttpRequestException or System.ClientModel.ClientResultException
                                          or Anthropic.Exceptions.AnthropicException or OperationCanceledException or TimeoutException)
            {
                Status = L.F("Ai_Error", e.Message);
            }
        });
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedField is not { } f) return;
        Fields.Remove(f);
        SelectedField = null;
    }

    [RelayCommand] private void NextPage() { if (PageIndex + 1 < PageCount) PageIndex++; }
    [RelayCommand] private void PreviousPage() { if (PageIndex > 0) PageIndex--; }
    [RelayCommand] private void GoToPage(PageThumb? t) { if (t is not null) PageIndex = t.Index; }
    /// <summary>Free zoom (pinch, trackpad): clamped, and it ends "fit width" mode.</summary>
    public void SetZoom(double zoom)
    {
        _fitWidthMode = false;
        Zoom = Math.Clamp(Math.Round(zoom, 3), 0.25, 4);
    }

    [RelayCommand] private void ZoomIn() { _fitWidthMode = false; Zoom = Math.Min(4, Math.Round(Zoom * 1.25, 2)); }
    [RelayCommand] private void ZoomOut() { _fitWidthMode = false; Zoom = Math.Max(0.25, Math.Round(Zoom / 1.25, 2)); }

    [RelayCommand]
    private void FitWidth()
    {
        _fitWidthMode = true;
        if (PageWidth <= 0 || ViewportWidth <= PageMargin * 2) return;
        Zoom = Math.Clamp(Math.Floor((ViewportWidth - PageMargin * 2) / PageWidth * 100) / 100, 0.25, 4);
    }

    [RelayCommand]
    private void FitPage()
    {
        _fitWidthMode = false;
        if (PageWidth <= 0 || PageHeight <= 0 || ViewportWidth <= PageMargin * 2 || ViewportHeight <= PageMargin * 2) return;
        double k = Math.Min((ViewportWidth - PageMargin * 2) / PageWidth, (ViewportHeight - PageMargin * 2) / PageHeight);
        Zoom = Math.Clamp(Math.Floor(k * 100) / 100, 0.25, 4);
    }

    [RelayCommand] private void SelectMode() { DrawMode = false; PreviewMode = false; }
    [RelayCommand] private void DrawFieldMode() { if (HasDocument && IsFormTool) DrawMode = true; }
    [RelayCommand] private void PreviewModeOn() { if (HasDocument && IsFormTool) PreviewMode = true; }

    /// <summary>Tab / Shift+Tab: the next field in reading order on this page.</summary>
    public void SelectNextField(bool backwards)
    {
        if (PageFields.Count == 0) return;
        int i = SelectedField is { } f ? PageFields.IndexOf(f) : -1;
        int next = backwards ? (i <= 0 ? PageFields.Count - 1 : i - 1) : (i + 1) % PageFields.Count;
        SelectedField = PageFields[next];
    }

    private bool _fitPending;

    /// <summary>A new field drawn by the user on the current page.</summary>
    public FieldViewModel AddField(Washi.Core.PdfRect rect)
    {
        int n = Fields.Count(f => f.Page == PageIndex) + 1;
        string name;
        do name = $"p{PageIndex + 1}_text_{n++:000}"; while (Fields.Any(f => f.Name == name));
        var field = new FieldViewModel { Page = PageIndex, Kind = FieldKind.Text, Name = name };
        field.SetRect(rect);
        Fields.Add(field);
        SelectedField = field;
        return field;
    }

    // ------------------------------------------------------------------ helpers
    private void ReplaceFields(IEnumerable<DetectedField> fields)
    {
        SelectedField = null;
        Fields.Clear();
        foreach (var f in fields) Fields.Add(FieldViewModel.From(f));
    }

    private void OnFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshPageFields();
        var summary = Summary();
        FieldSummary = Fields.Count == 0 ? "" : L.F("Ui_FieldTotal", Fields.Count, summary);
    }

    private void RefreshPageFields()
    {
        PageFields.Clear();
        foreach (var f in Fields.Where(f => f.Page == PageIndex).OrderBy(f => Math.Round(f.Y0 / 6)).ThenBy(f => f.X0))
            PageFields.Add(f);
    }

    private string Summary() => string.Join(", ", Fields.GroupBy(f => f.Kind).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.First().KindText}"));

    private async Task RenderPageAsync()
    {
        if (PdfPath is not { } path || PageCount == 0) return;
        int index = PageIndex;
        var (img, info) = await Pdfium(() =>
        {
            using var d = PdfiumDocument.Open(path);
            return (d.RenderPage(index, RenderScale), d.GetPageInfo(index));
        });
        if (index != PageIndex || path != PdfPath) return;   // the user moved on meanwhile
        PageWidth = info.Width;
        PageHeight = info.Height;
        PageImage = Imaging.ToBitmap(img);
        if (_fitPending) { _fitPending = false; FitWidth(); }
    }

    private async Task RenderThumbnailsAsync(string path)
    {
        foreach (var thumb in Thumbnails.ToList())
        {
            if (path != PdfPath) return;
            int index = thumb.Index;
            try
            {
                var img = await Pdfium(() =>
                {
                    using var d = PdfiumDocument.Open(path);
                    var info = d.GetPageInfo(index);
                    return d.RenderPage(index, 220.0 / Math.Max(info.Width, info.Height));   // about 220 px on the long side
                });
                if (path != PdfPath) return;
                thumb.Image = Imaging.ToBitmap(img);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException) { return; }
        }
    }

    /// <summary>PDFium is process-global: one call at a time, off the UI thread.</summary>
    internal static async Task<T> Pdfium<T>(Func<T> work)
    {
        await PdfiumDocument.Gate.WaitAsync();
        try { return await Task.Run(work); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    internal async Task Run(Func<Task> work)
    {
        Busy = true;
        try { await work(); }
        catch (FontNotUsableException e) when (e.Reason == "license")
        {
            Status = L.F("Ui_Error", L.F("FontNotAllowed", Path.GetFileName(EmbedFontPath ?? ""), $"0x{e.FsType:X4}"));
        }
        catch (FontNotUsableException) { Status = L.F("Ui_Error", L.F("FontNotSupported", "")); }
        catch (PdfSharp.Pdf.IO.PdfReaderException e) when (e.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase))
        {
            Status = L.F("Ui_Error", L.F("ProtectedPdf", Path.GetFileName(PdfPath ?? "")));
        }
        catch (Exception e) when (e is Washi.Core.Pages.PageAssemblyException or Washi.Core.Ocr.OcrException)
        {
            Status = L.F("Ui_Error", e.Message);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or PdfSharp.Pdf.IO.PdfReaderException)
        {
            Status = L.F("Ui_Error", e.Message);
        }
        finally { Busy = false; }
    }
}
