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
    }

    // ------------------------------------------------------------------ state
    public ObservableCollection<FieldViewModel> Fields { get; } = [];
    public ObservableCollection<FieldViewModel> PageFields { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDocument), nameof(Title))] private string? _pdfPath;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PageText))] private int _pageCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PageText))] private int _pageIndex;
    [ObservableProperty] private Bitmap? _pageImage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayWidth))] private double _pageWidth;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayHeight))] private double _pageHeight;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayWidth), nameof(DisplayHeight), nameof(ZoomText))] private double _zoom = 1.0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private FieldViewModel? _selectedField;
    [ObservableProperty] private bool _drawMode;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FontText))] private string? _embedFontPath;
    [ObservableProperty] private Choice<string> _formLanguage = Languages[0];

    public bool HasDocument => PdfPath is not null;
    public bool HasSelection => SelectedField is not null;
    public double DisplayWidth => PageWidth * Zoom;
    public double DisplayHeight => PageHeight * Zoom;
    public string ZoomText => $"{Zoom:P0}";
    public string PageText => PageCount == 0 ? "" : L.F("Ui_PageOf", PageIndex + 1, PageCount);
    public string Title => PdfPath is null ? "Washi" : $"{Path.GetFileName(PdfPath)} — Washi";
    public string FontText => EmbedFontPath is null ? L.Get("Ui_NoFont") : Path.GetFileName(EmbedFontPath);

    public static IReadOnlyList<Choice<string>> Languages { get; } =
    [
        new("auto", L.Get("Ui_LangAuto")), new("ja", "日本語"), new("zh-Hans", "简体中文"), new("zh-Hant", "繁體中文"),
        new("ko", "한국어"), new("en", "English"), new("fr", "Français"), new("de", "Deutsch"),
    ];

    public static IReadOnlyList<Choice<FieldKind>> Kinds { get; } =
        [.. Enum.GetValues<FieldKind>().Select(k => new Choice<FieldKind>(k, L.Get("Kind_" + k.ToString().ToLowerInvariant())))];

    public static IReadOnlyList<Choice<int>> Alignments { get; } =
        [new(0, L.Get("Ui_AlignLeft")), new(1, L.Get("Ui_AlignCenter")), new(2, L.Get("Ui_AlignRight"))];

    public string Hint => L.Get(DrawMode ? "Ui_DrawHint" : "Ui_Hint");

    partial void OnDrawModeChanged(bool value) => OnPropertyChanged(nameof(Hint));

    partial void OnPageIndexChanged(int value)
    {
        RefreshPageFields();
        SelectedField = null;
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
        await Run(async () =>
        {
            int count = await Pdfium(() => { using var d = PdfiumDocument.Open(path); return d.PageCount; });
            PdfPath = path;
            Fields.Clear();
            PageCount = count;
            if (PageIndex != 0) PageIndex = 0; else { RefreshPageFields(); await RenderPageAsync(); }
            Status = L.F("Ui_Opened", Path.GetFileName(path), count);
        });
        await DetectAsync();
    }

    [RelayCommand]
    private async Task DetectAsync()
    {
        if (PdfPath is not { } path) return;
        await Run(async () =>
        {
            Status = L.Get("Ui_Detecting");
            var (fields, _) = await Pdfium(() => FormPipeline.Detect(path));
            ReplaceFields(fields);
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
            var font = EmbedFontPath is { } fp ? await Task.Run(() => OpenTypeFont.Load(fp)) : null;
            await Pdfium(() =>
            {
                var text = FormPipeline.Text(pdf);
                var (script, _) = FormPipeline.ChooseScript(FormLanguage.Value, text, L.Culture);
                var tmp = output + ".tmp";
                AcroFormWriter.Write(pdf, tmp, fields, script, font);
                File.Move(tmp, output, overwrite: true);   // the save dialog already asked about overwriting
                return 0;
            });
            Status = L.F("Ui_Made", Path.GetFileName(output));
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
                                          or Anthropic.Exceptions.AnthropicException or TaskCanceledException)
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
    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(4, Math.Round(Zoom * 1.25, 2));
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.25, Math.Round(Zoom / 1.25, 2));

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

    private void OnFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshPageFields();

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
        if (index != PageIndex) return;   // the user moved on meanwhile
        PageWidth = info.Width;
        PageHeight = info.Height;
        PageImage = Imaging.ToBitmap(img);
    }

    /// <summary>PDFium is process-global: one call at a time, off the UI thread.</summary>
    private static async Task<T> Pdfium<T>(Func<T> work)
    {
        await PdfiumDocument.Gate.WaitAsync();
        try { return await Task.Run(work); }
        finally { PdfiumDocument.Gate.Release(); }
    }

    private async Task Run(Func<Task> work)
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
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or PdfSharp.Pdf.IO.PdfReaderException)
        {
            Status = L.F("Ui_Error", e.Message);
        }
        finally { Busy = false; }
    }
}
