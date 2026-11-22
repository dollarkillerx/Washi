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
public enum Tool { Form, Ocr, Combine, Split, Organize, Comment, Sign, Protect, Redact, Erase, Ask, Settings }

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
public sealed partial class MainViewModel : ObservableObject, IEditorHost
{
    /// <summary>Pixels per point of the page bitmap (the view scales it by Zoom).</summary>
    public const double RenderScale = 2.0;

    private readonly IDialogs _dialogs;

    private readonly AppServices _services;

    public MainViewModel(IDialogs dialogs) : this(dialogs, AppServices.Instance) { }

    public MainViewModel(IDialogs dialogs, AppServices services)
    {
        _dialogs = dialogs;
        _services = services;
        Fields.CollectionChanged += OnFieldsChanged;
        Status = L.Get("Ui_StartHint");
        Ocr = new OcrToolViewModel(this);
        Combine = new CombineToolViewModel(this);
        Split = new SplitToolViewModel(this);
        Organize = new OrganizeToolViewModel(this);
        Sign = new SignToolViewModel(this);
        Comment = new CommentToolViewModel(this);
        Protect = new ProtectToolViewModel(this);
        Redact = new RedactToolViewModel(this);
        Erase = new EraseToolViewModel(this);
        Assistant = new AssistantViewModel(this, services);
        services.Editor = this;
    }

    internal AppServices Services => _services;

    internal IDialogs Dialogs => _dialogs;

    public OcrToolViewModel Ocr { get; }
    public CombineToolViewModel Combine { get; }
    public SplitToolViewModel Split { get; }
    public OrganizeToolViewModel Organize { get; }
    public SignToolViewModel Sign { get; }
    public CommentToolViewModel Comment { get; }
    public ProtectToolViewModel Protect { get; }
    public RedactToolViewModel Redact { get; }
    public EraseToolViewModel Erase { get; }
    public AssistantViewModel Assistant { get; }

    /// <summary>The Settings page, created when first shown.</summary>
    public SettingsViewModel Settings => _settings ??= CreateSettings();
    private SettingsViewModel? _settings;

    /// <summary>The app rebuilds the window in the new language (set by App).</summary>
    public Action? LanguageChanged { get; set; }

    private SettingsViewModel CreateSettings()
    {
        var vm = new SettingsViewModel(_services.SettingsPath, _services, _dialogs);
        vm.LanguageChanged += () => LanguageChanged?.Invoke();
        vm.AiChanged += Assistant.RefreshModel;
        return vm;
    }

    // ------------------------------------------------------------------ state
    public ObservableCollection<FieldViewModel> Fields { get; } = [];
    public ObservableCollection<FieldViewModel> PageFields { get; } = [];

    /// <summary>
    /// The rows of the field list. Preparing a form: every field of the page. Filling it (Preview): only what has
    /// been filled in, as an overview of what will be saved; a long list of empty fields helps nobody.
    /// </summary>
    public ObservableCollection<FieldViewModel> ListFields { get; } = [];

    public string ListCaption => PreviewMode
        ? L.F("Ui_FilledOnPage", PageFields.Count(f => f.HasValue), PageFields.Count)
        : L.F("Ui_FieldsOnPageCount", PageFields.Count);

    public bool ListEmpty => ListFields.Count == 0 && HasDocument;
    public string ListEmptyText => L.Get(PreviewMode ? "Ui_NothingFilled" : "Ui_NoFieldsOnPage");

    /// <summary>Design-time properties (name, type, alignment) are shown while preparing the form, not while filling it.</summary>
    public bool IsDesignMode => !PreviewMode;

    private void RefreshListFields()
    {
        var want = PreviewMode ? PageFields.Where(f => f.HasValue).ToList() : PageFields.ToList();
        if (!want.SequenceEqual(ListFields))
        {
            var selected = SelectedField;
            ListFields.Clear();
            foreach (var f in want) ListFields.Add(f);
            if (!ReferenceEquals(SelectedField, selected)) SelectedField = selected;   // the list must not drop the selection
        }
        OnPropertyChanged(nameof(ListCaption));
        OnPropertyChanged(nameof(ListEmpty));
        OnPropertyChanged(nameof(ListEmptyText));
    }

    private void OnFieldPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FieldViewModel.Value)) RefreshListFields();
    }
    public ObservableCollection<PageThumb> Thumbnails { get; } = [];

    // ------------------------------------------------------------------ tools and panels
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsFormTool), nameof(IsOcrTool), nameof(IsCombineTool), nameof(IsSplitTool), nameof(IsOrganizeTool), nameof(IsSignTool), nameof(IsCommentTool), nameof(IsProtectTool), nameof(IsRedactTool),
                                                  nameof(IsAskTool), nameof(IsEraseTool), nameof(IsSettingsTool), nameof(IsDocumentView), nameof(ShowToolPanel), nameof(ToolPanelWidth),
                                                  nameof(ShowFieldsPanel), nameof(ShowSidePanel), nameof(ShowEditTools), nameof(ShowRightRail))]
    private Tool _activeTool = Tool.Form;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPagesPanel), nameof(ShowFieldsPanel), nameof(ShowSidePanel))]
    private SidePanel _sidePanel = SidePanel.Fields;

    /// <summary>Clicking the active tool in the rail again hides its panel (more room for the page).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowToolPanel))] private bool _toolPanelOpen = true;

    public bool IsFormTool { get => ActiveTool == Tool.Form; set { if (value) ActiveTool = Tool.Form; } }
    public bool IsOcrTool { get => ActiveTool == Tool.Ocr; set { if (value) ActiveTool = Tool.Ocr; } }
    public bool IsCombineTool { get => ActiveTool == Tool.Combine; set { if (value) ActiveTool = Tool.Combine; } }
    public bool IsSplitTool { get => ActiveTool == Tool.Split; set { if (value) ActiveTool = Tool.Split; } }
    public bool IsOrganizeTool { get => ActiveTool == Tool.Organize; set { if (value) ActiveTool = Tool.Organize; } }
    public bool IsSignTool { get => ActiveTool == Tool.Sign; set { if (value) ActiveTool = Tool.Sign; } }
    public bool IsCommentTool { get => ActiveTool == Tool.Comment; set { if (value) ActiveTool = Tool.Comment; } }
    public bool IsProtectTool { get => ActiveTool == Tool.Protect; set { if (value) ActiveTool = Tool.Protect; } }
    public bool IsRedactTool { get => ActiveTool == Tool.Redact; set { if (value) ActiveTool = Tool.Redact; } }
    public bool IsAskTool { get => ActiveTool == Tool.Ask; set { if (value) ActiveTool = Tool.Ask; } }
    public bool IsEraseTool { get => ActiveTool == Tool.Erase; set { if (value) ActiveTool = Tool.Erase; } }
    public bool IsSettingsTool { get => ActiveTool == Tool.Settings; set { if (value) ActiveTool = Tool.Settings; } }

    /// <summary>Settings is a page of its own: it takes the document's place (no tool panel, no side rails).</summary>
    public bool IsDocumentView => !IsSettingsTool;
    public bool ShowToolPanel => ToolPanelOpen && !IsSettingsTool;
    public bool ShowRightRail => HasDocument && !IsSettingsTool;

    /// <summary>The chat needs more room than the other tools' panels.</summary>
    public double ToolPanelWidth => IsAskTool ? 380 : 296;

    /// <summary>MCP calls waiting for the user (shown as cards over the page).</summary>
    public System.Collections.ObjectModel.ObservableCollection<ApprovalViewModel> McpApprovals => _services.Approvals.Pending;

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

    public bool ShowSidePanel => HasDocument && !IsSettingsTool && (ShowPagesPanel || ShowFieldsPanel);

    /// <summary>The floating select / draw / delete toolbar belongs to the form tool.</summary>
    public bool ShowEditTools => HasDocument && IsFormTool;

    partial void OnActiveToolChanged(Tool value)
    {
        if (value != Tool.Form) { DrawMode = false; PreviewMode = false; }
        if (value == Tool.Form && PdfPath is { } p && _detectedFor != p) _ = DetectAsync();
        if (value == Tool.Ocr) _ = Ocr.RefreshPlanAsync();
        if (value == Tool.Combine) Combine.OfferCurrentDocument();
        if (value == Tool.Organize) Organize.Prepare();
        if (value == Tool.Sign) _ = Sign.PrepareAsync();
        if (value == Tool.Comment) _ = Comment.PrepareAsync();
        if (value == Tool.Protect) _ = Protect.PrepareAsync();
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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDocument), nameof(Title), nameof(FileName), nameof(ShowSidePanel), nameof(ShowEditTools), nameof(ShowRightRail))]
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

    public bool HasFields => Fields.Count > 0;

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

    /// <summary>Text sizes for a field's value: fitted to the box by default, or a fixed size in points.</summary>
    public static IReadOnlyList<Choice<double>> FontSizes =>
        [new(0, L.Get("Ui_SizeAuto")), .. new double[] { 6, 7, 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 24 }.Select(v => new Choice<double>(v, v.ToString("0.#") + " pt"))];

    public static IReadOnlyList<Choice<string>> FontFaces =>
        [new("", L.Get("Ui_FaceAuto")), new("serif", L.Get("Ui_FaceSerif")), new("sans", L.Get("Ui_FaceSans"))];

    public static IReadOnlyList<Choice<string>> TextColors =>
    [
        new("", L.Get("Ui_ColorBlack")), new("#1F3A93", L.Get("Ui_ColorBlue")), new("#C0392B", L.Get("Ui_ColorRed")),
        new("#1E8449", L.Get("Ui_ColorGreen")), new("#6F6F6F", L.Get("Ui_ColorGrey")),
    ];

    /// <summary>Gives every text field the selected field's size, colour and face.</summary>
    [RelayCommand]
    private void ApplyStyleToAll()
    {
        if (SelectedField is not { } s) return;
        foreach (var f in Fields.Where(f => f.Kind != FieldKind.Checkbox && !ReferenceEquals(f, s)))
        {
            f.FontSize = s.FontSize;
            f.Color = s.Color;
            f.Font = s.Font;
        }
        Status = L.F("Ui_StyleApplied", Fields.Count(f => f.Kind != FieldKind.Checkbox));
    }

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
        OnPropertyChanged(nameof(IsDesignMode));
        RefreshListFields();
    }

    /// <summary>Raised when a text field should get the inline editor (Preview); the view places it.</summary>
    public event Action<FieldViewModel>? EditRequested;

    /// <summary>Preview click on a field: a checkbox toggles, anything else opens the inline editor.</summary>
    public void ActivateField(FieldViewModel f)
    {
        SelectedField = f;
        switch (f.Kind)
        {
            case FieldKind.Checkbox: f.Value = f.IsChecked ? "" : "true"; break;
            case FieldKind.Radio: ChooseRadio(f); break;
            case FieldKind.Choice:
            {
                // each click moves to the next item, then back to empty (the fill panel has the full list)
                var items = f.OptionList;
                int i = items.ToList().IndexOf(f.Value);
                f.Value = items.Count == 0 ? "" : i + 1 < items.Count ? items[i + 1] : "";
                break;
            }
            case FieldKind.Signature: Status = L.Get("Ui_SignHere"); break;
            default: EditRequested?.Invoke(f); break;
        }
    }

    /// <summary>Chooses a radio button: the other buttons of its group are cleared.</summary>
    [RelayCommand]
    private void ChooseRadio(FieldViewModel? f)
    {
        if (f is not { Kind: FieldKind.Radio }) return;
        foreach (var other in Fields.Where(o => o.Kind == FieldKind.Radio && o.Group == f.Group && !ReferenceEquals(o, f)))
            other.Value = "";
        f.Value = "true";
    }

    [RelayCommand] private void TogglePreview() { if (HasDocument && IsFormTool) PreviewMode = !PreviewMode; }

    partial void OnPageIndexChanged(int value)
    {
        RefreshPageFields();
        SelectedField = null;
        foreach (var t in Thumbnails) t.IsCurrent = t.Index == value;
        _ = RenderPageAsync();
        UpdateSnapshot();
    }

    // ------------------------------------------------------------------ IEditorHost (app tools for the assistant and MCP)
    private volatile DocumentSnapshot? _snapshot;

    public DocumentSnapshot? Snapshot => _snapshot;

    private void UpdateSnapshot()
    {
        _snapshot = PdfPath is { } p ? new DocumentSnapshot(p, PageIndex + 1, PageCount, Fields.Count) : null;
        Assistant?.OnDocumentChanged();
    }

    partial void OnPdfPathChanged(string? value) => UpdateSnapshot();
    partial void OnPageCountChanged(int value) => UpdateSnapshot();

    public Task OpenAsync(string path, int? page) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (IsSettingsTool) ActiveTool = Tool.Form;
        if (PdfPath != path) await LoadAsync(path);
        if (page is { } n && n >= 1 && n <= PageCount) PageIndex = n - 1;
    });

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
        bool opened = false, locked = false;
        await Run(async () =>
        {
            int count = await Pdfium(() =>
            {
                if (PdfiumDocument.NeedsPassword(path)) return -1;
                using var d = PdfiumDocument.Open(path);
                return d.PageCount;
            });
            if (count < 0) { locked = true; return; }
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
        if (locked)
        {
            // it cannot be shown without its password: the Protect tool asks for it
            ActiveTool = Tool.Protect;
            Protect.AskPassword(path);
            return;
        }
        if (!opened) return;
        Protect.LockedPath = null;
        _ = RenderThumbnailsAsync(path);
        Split.Reset();
        Erase.Reset();
        Organize.Clear();                       // a reloaded file (even the same path) starts fresh in every tool
        Sign.Reset();
        Comment.Reset();
        Protect.Reset();
        Redact.Reset();
        if (ActiveTool == Tool.Form) await DetectAsync();
        else if (ActiveTool == Tool.Ocr) await Ocr.RefreshPlanAsync();
        else if (ActiveTool == Tool.Combine) Combine.OfferCurrentDocument();
        else if (ActiveTool == Tool.Organize) Organize.Prepare();
        else if (ActiveTool == Tool.Sign) await Sign.PrepareAsync();
        else if (ActiveTool == Tool.Comment) await Comment.PrepareAsync();
        else if (ActiveTool == Tool.Protect) await Protect.PrepareAsync();
    }

    /// <summary>Files dropped on the window: Combine collects them, other tools open the first PDF.</summary>
    public async Task DropAsync(IReadOnlyList<string> paths)
    {
        var pdfs = paths.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        if (pdfs.Count == 0) return;
        if (ActiveTool == Tool.Combine) await Combine.AddAsync(pdfs);
        else if (ActiveTool == Tool.Organize && HasDocument) await Organize.InsertAsync(pdfs);   // dropped files are inserted
        else await LoadAsync(pdfs[0]);
    }

    [RelayCommand]
    private void CloseDocument()
    {
        Erase.Reset();
        Organize.Clear();
        Sign.Reset();
        Comment.Reset();
        Protect.Reset();
        Redact.Reset();
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
            foreach (var f in Fields.Where(f => f.Value.Length > 0 && f.Kind is not (FieldKind.Radio or FieldKind.Signature))) values[f.Name] = f.Value;
            // a chosen radio button: its group (the PDF field) takes the button's option
            foreach (var f in Fields.Where(f => f.Kind == FieldKind.Radio && f.IsChecked))
            {
                var d = f.ToDetected().ToFormField();
                values[d.Name] = d.Export;
            }
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

    /// <summary>The on-device OCR engine for printed values; tests replace it.</summary>
    public Func<Washi.Core.Ocr.IOcrEngine> OcrFactory { get; set; } = () => Washi.Core.Ocr.OcrEngines.Create("auto");

    /// <summary>
    /// Makes what is already printed in the fields editable: the print under every field is erased for real into
    /// &lt;name&gt;_erased.pdf (the original is not changed), its text (from the PDF, or OCR of the erased ink) becomes
    /// the field's value, and Preview opens so each value can be clicked and edited. The fields are kept as they are.
    /// </summary>
    /// <param name="only">One field (the button in the field panel); null converts every field.</param>
    [RelayCommand]
    private async Task MovePrintedAsync(FieldViewModel? only)
    {
        if (PdfPath is not { } pdf || Fields.Count == 0) return;
        var targets = only is not null ? [only] : Fields.Where(f => f.Kind != FieldKind.Checkbox).ToList();
        var cells = targets.Select((f, i) => new Washi.Core.Pages.PrintedCell(i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            f.Page, f.Rect, f.Kind == FieldKind.Comb)).ToList();
        var (output, replace) = WorkingCopy(pdf);
        var languages = OcrLanguageOrder;
        Washi.Core.Pages.PrintedResult? result = null;
        await Run(async () =>
        {
            Status = L.Get("Ui_MovingPrinted");
            try
            {
                // text and images only: on a black-printed form the boxes of a comb or a checkbox are drawings too,
                // and they are the form, not data (strokes and marks go with the Erase tool, where they are shown first)
                result = await Washi.Core.Pages.PrintedText.MoveIntoFieldsAsync(pdf, output, cells, OcrFactory, languages,
                    new Washi.Core.Pages.EraseOptions(Drawings: false));
            }
            catch (Washi.Core.Pages.EraseException e)
            {
                Status = L.F("Ui_EraseFailed", Errors.Text(e));
            }
        });
        if (result is null) return;
        output = Commit(output, replace);

        // the erased copy replaces the page on screen; the fields stay
        int moved = 0, byOcr = 0;
        foreach (var v in result.Values)
        {
            var f = targets[int.Parse(v.Key, System.Globalization.CultureInfo.InvariantCulture)];
            f.Prefilled = "";
            if (v.Text.Length == 0 || f.Value.Length > 0) continue;   // what the user already typed wins over the old print
            f.Value = f.Kind == FieldKind.Comb && f.MaxLen > 0 && v.Text.Length > f.MaxLen ? v.Text[..f.MaxLen] : v.Text;
            moved++;
            if (v.FromOcr) byOcr++;
        }
        await SwapDocumentAsync(output);
        PreviewMode = true;
        Status = L.F("Ui_MovedPrinted", moved, byOcr, Path.GetFileName(output));
        // one field from its panel: go straight to editing it
        if (only is not null && only.Kind != FieldKind.Checkbox)
        {
            if (only.Page != PageIndex) PageIndex = only.Page;
            ActivateField(only);
        }
    }

    /// <summary>OCR languages for printed values: the form language when chosen, else Japanese first (the forms Washi
    /// is built for), then Chinese and English.</summary>
    internal IReadOnlyList<string> OcrLanguageOrder =>
        FormLanguage.Value is { Length: > 0 } lang && lang != "auto" ? [lang, "en"] : ["ja", "zh-Hans", "en"];

    /// <summary>The copy Washi wrote in this session (erased); later erases update it instead of copying it again.</summary>
    internal string? OwnCopy { get; set; }

    /// <summary>
    /// Where an erase writes: the open file is the original → a new &lt;name&gt;_erased.pdf; it is Washi's own
    /// copy → a temporary file that then replaces that copy (the original is never touched).
    /// </summary>
    internal (string Output, bool Replace) WorkingCopy(string pdf) =>
        pdf == OwnCopy && File.Exists(pdf) ? (Path.Combine(Path.GetDirectoryName(pdf)!, $".{Path.GetFileNameWithoutExtension(pdf)}.{Guid.NewGuid():N}.pdf"), true)
                                           : (EraseToolViewModel.FreeName(pdf), false);

    internal string Commit(string output, bool replace)
    {
        if (!replace) { OwnCopy = output; return output; }
        File.Move(output, OwnCopy!, overwrite: true);
        return OwnCopy!;
    }

    /// <summary>Shows another file of the same form (an erased copy) without touching the fields.</summary>
    internal async Task SwapDocumentAsync(string path)
    {
        PdfPath = path;   // (the same path again when Washi's own copy was rewritten: the page is rendered anew below)
        _detectedFor = path;
        await RenderPageAsync();
        _ = RenderThumbnailsAsync(path);
    }

    /// <summary>Builds the AI model; tests replace it.</summary>
    public Func<Washi.Ai.IAiModel> ModelFactory { get; set; } = () => Washi.Ai.AiSettings.Load().CreateModel();

    [RelayCommand]
    private async Task NameWithAiAsync()
    {
        if (PdfPath is not { } pdf || Fields.Count == 0) return;
        Washi.Ai.IAiModel model;
        try { model = ModelFactory(); }
        catch (Washi.Ai.AiException e) { Status = L.F("Ai_Error", UiErrors.Text(e)); return; }

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
                Status = L.F("Ai_Error", UiErrors.Text(e));
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
    public FieldViewModel AddField(Washi.Core.PdfRect rect, int? page = null)
    {
        int p = page ?? PageIndex;
        int n = Fields.Count(f => f.Page == p) + 1;
        string name;
        do name = $"p{p + 1}_text_{n++:000}"; while (Fields.Any(f => f.Name == name));
        var field = new FieldViewModel { Page = p, Kind = FieldKind.Text, Name = name };
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
        foreach (FieldViewModel f in e.OldItems ?? Array.Empty<FieldViewModel>()) f.PropertyChanged -= OnFieldPropertyChanged;
        foreach (FieldViewModel f in e.NewItems ?? Array.Empty<FieldViewModel>()) f.PropertyChanged += OnFieldPropertyChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var f in Fields) { f.PropertyChanged -= OnFieldPropertyChanged; f.PropertyChanged += OnFieldPropertyChanged; }
        UpdateSnapshot();
        OnPropertyChanged(nameof(HasFields));
        RefreshPageFields();
        var summary = Summary();
        FieldSummary = Fields.Count == 0 ? "" : L.F("Ui_FieldTotal", Fields.Count, summary);
    }

    private void RefreshPageFields()
    {
        PageFields.Clear();
        foreach (var f in Fields.Where(f => f.Page == PageIndex).OrderBy(f => Math.Round(f.Y0 / 6)).ThenBy(f => f.X0))
            PageFields.Add(f);
        RefreshListFields();
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

    /// <summary>
    /// PDFium is process-global: one call at a time, off the UI thread. The lock is taken and released on the same
    /// pool thread, never after a hop back to the UI thread: a render still running when its dispatcher stops (the
    /// app closing, or a headless test ending) would otherwise keep the lock forever and hang every later PDFium call.
    /// </summary>
    internal static Task<T> Pdfium<T>(Func<T> work) => Task.Run(async () =>
    {
        await PdfiumDocument.Gate.WaitAsync().ConfigureAwait(false);
        try { return work(); }
        finally { PdfiumDocument.Gate.Release(); }
    });

    internal async Task Run(Func<Task> work)
    {
        Busy = true;
        try { await work(); }
        catch (FontNotUsableException e) when (e.Reason == "license")
        {
            Status = L.F("Ui_Error", L.F("FontNotAllowed", Path.GetFileName(EmbedFontPath ?? ""), $"0x{e.FsType:X4}"));
        }
        catch (FontNotUsableException) { Status = L.F("Ui_Error", L.F("FontNotSupported", "")); }
        catch (PdfSharp.Pdf.IO.PdfReaderException e) when (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            Status = L.F("Ui_Error", L.F("ProtectedPdf", Path.GetFileName(PdfPath ?? "")));
        }
        catch (Exception e) when (e is Washi.Core.Pages.PageAssemblyException or Washi.Core.Ocr.OcrException)
        {
            Status = L.F("Ui_Error", Errors.Text(e));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or PdfSharp.Pdf.IO.PdfReaderException)
        {
            Status = L.F("Ui_Error", Errors.Text(e));
        }
        finally { Busy = false; }
    }
}
