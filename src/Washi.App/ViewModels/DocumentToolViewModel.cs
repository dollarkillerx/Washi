// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core.Forms;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>An attachment of the open document, with a box to tick for removing.</summary>
public sealed partial class AttachmentRow(AttachmentInfo info) : ObservableObject
{
    public AttachmentInfo Info { get; } = info;
    [ObservableProperty] private bool _selected;
    public string Text => Info.Name + (Info.Size >= 0 ? "  ·  " + CompressFormat.Size(Info.Size) : "")
                          + (Info.Page >= 0 ? "  ·  " + L.F("Ui_AttachOnPage", Info.Page + 1) : "");
    public string Details => Info.Description;
    public bool HasDetails => Info.Description.Length > 0;
}

/// <summary>A layer of the open document; ticked when it shows.</summary>
public sealed partial class LayerRow(LayerInfo info) : ObservableObject
{
    public LayerInfo Info { get; } = info;
    [ObservableProperty] private bool _visible = info.Visible;
    public string Text => Info.Name + (Info.Locked ? "  🔒" : "")
                          + (Info.Pages.Count > 0 ? "  ·  " + L.F("Ui_LayerPages", string.Join(", ", Info.Pages.Take(8).Select(p => p + 1)) + (Info.Pages.Count > 8 ? ", …" : "")) : "");
}

/// <summary>
/// Document: page labels (<see cref="PdfPageLabels"/>), attachments (<see cref="PdfAttachments"/>) and layers
/// (<see cref="PdfLayers"/>) of the open PDF. Each change writes a copy and opens it.
/// </summary>
public sealed partial class DocumentToolViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<AttachmentRow> Attachments { get; } = [];
    public ObservableCollection<LayerRow> Layers { get; } = [];

    /// <summary>The labels as written in the box: page:style[:prefix[:first]], comma-separated.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LabelPreview))] private string _labels = "";
    [ObservableProperty] private string _attachDescription = "";
    private IReadOnlyList<PageLabelRange> _fileLabels = [];

    public bool HasAttachments => Attachments.Count > 0;
    public bool HasLayers => Layers.Count > 0;
    public bool CanRun => main.HasDocument;
    public bool CanRemoveAttachments => Attachments.Any(a => a.Selected);
    public bool LayersChanged => Layers.Any(l => l.Visible != l.Info.Visible);

    /// <summary>The first labels the box gives, or what is wrong with it.</summary>
    public string LabelPreview
    {
        get
        {
            if (Labels.Trim().Length == 0) return L.Get("Ui_LabelsNone");
            try
            {
                var ranges = PdfPageLabels.Parse(Labels);
                int n = Math.Min(Math.Max(main.PageCount, 1), 12);
                return string.Join(", ", Enumerable.Range(0, n).Select(i => PdfPageLabels.Label(ranges, i))) + (main.PageCount > n ? ", …" : "");
            }
            catch (PageAssemblyException e) { return Errors.Text(e); }
        }
    }

    public async Task PrepareAsync()
    {
        Attachments.Clear();
        Layers.Clear();
        if (main.PdfPath is not { } pdf) { Notify(); return; }
        var (labels, attachments, layers) = await Task.Run(() =>
        {
            IReadOnlyList<PageLabelRange> l = []; IReadOnlyList<AttachmentInfo> a = []; IReadOnlyList<LayerInfo> y = [];
            try { l = PdfPageLabels.Read(pdf); a = PdfAttachments.List(pdf); y = PdfLayers.List(pdf); }
            catch (Exception e) when (e is PageAssemblyException or IOException or PdfSharp.Pdf.IO.PdfReaderException or InvalidOperationException) { }
            return (l, a, y);
        });
        if (main.PdfPath != pdf) return;
        _fileLabels = labels;
        Labels = PdfPageLabels.Format(labels);
        foreach (var a in attachments)
        {
            var row = new AttachmentRow(a);
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanRemoveAttachments));
            Attachments.Add(row);
        }
        foreach (var y in layers)
        {
            var row = new LayerRow(y);
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(LayersChanged));
            Layers.Add(row);
        }
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(HasLayers));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanRemoveAttachments));
        OnPropertyChanged(nameof(LayersChanged));
        OnPropertyChanged(nameof(LabelPreview));
    }

    // ------------------------------------------------------------------ page labels

    [RelayCommand]
    private async Task SaveLabelsAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        IReadOnlyList<PageLabelRange> ranges;
        try { ranges = PdfPageLabels.Parse(Labels); }
        catch (PageAssemblyException e) { main.Status = Errors.Text(e); return; }
        await Write("_labels", tmp => PdfPageLabels.Write(pdf, tmp, ranges),
                    o => ranges.Count == 0 ? L.F("Ui_LabelsRemoved", o) : L.F("Ui_LabelsSaved", o, ranges.Count));
    }

    // ------------------------------------------------------------------ attachments

    [RelayCommand]
    private async Task AttachAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        var files = await main.Dialogs.OpenFilesAsync();
        if (files.Count == 0) return;
        var description = AttachDescription;
        int count = 0;
        await Write("_attached", tmp => count = PdfAttachments.Edit(pdf, tmp, files, [], description).Count, o => L.F("Ui_Attached", o, count));
        AttachDescription = "";
    }

    [RelayCommand]
    private async Task RemoveAttachmentsAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        var names = Attachments.Where(a => a.Selected).Select(a => a.Info.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0) return;
        await Write("_detached", tmp => PdfAttachments.Edit(pdf, tmp, [], names), o => L.F("Ui_Detached", o, names.Count));
    }

    /// <summary>Saves the ticked attachments (all when none is ticked) into a folder the user picks.</summary>
    [RelayCommand]
    private async Task SaveAttachmentsAsync()
    {
        if (main.PdfPath is not { } pdf || Attachments.Count == 0) return;
        if (await main.Dialogs.PickFolderAsync() is not { } dir) return;
        var names = Attachments.Where(a => a.Selected).Select(a => a.Info.Name).ToList();
        IReadOnlyList<string>? written = null;
        await main.Run(async () => written = await Task.Run(() => PdfAttachments.Extract(pdf, dir, names)));
        if (written is not null) main.Status = L.F("Ui_AttachmentsSaved", written.Count, dir);
    }

    // ------------------------------------------------------------------ layers

    [RelayCommand]
    private async Task SaveLayersAsync()
    {
        if (main.PdfPath is not { } pdf || !LayersChanged) return;
        var ids = (bool v) => Layers.Where(l => l.Visible == v && l.Visible != l.Info.Visible)
                                    .Select(l => l.Info.Id.ToString(CultureInfo.InvariantCulture)).ToList();
        List<string> show = ids(true), hide = ids(false);
        await Write("_layers", tmp => PdfLayers.SetVisible(pdf, tmp, show, hide), o => L.F("Ui_LayersSaved", o));
    }

    // ------------------------------------------------------------------ form data

    /// <summary>A column of the table whose value names each copy (optional).</summary>
    [ObservableProperty] private string _nameColumn = "";
    [ObservableProperty] private string _formDataResult = "";

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        if (await main.Dialogs.SaveFormDataAsync(Path.GetFileNameWithoutExtension(pdf) + ".xfdf") is not { } output) return;
        int n = 0;
        if (await Run(() =>
        {
            if (File.Exists(output)) File.Delete(output);   // the save dialog already asked about replacing it
            n = PdfFormData.Export(pdf, output);
        }))
            main.Status = FormDataResult = L.F("Ui_FormDataExported", Path.GetFileName(output), n);
    }

    [RelayCommand]
    private async Task ImportDataAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        if (await main.Dialogs.OpenFormDataAsync() is not { } data) return;
        FillResult? r = null;
        await Write("_filled", tmp => r = PdfFormData.Import(pdf, data, tmp), o => L.F("Ui_FormDataImported", o, r!.Filled));
        if (r is not null)
            FormDataResult = string.Join("\n", new[] { main.Status }.Concat(r.UnknownNames.Select(n => L.F("Ui_FormDataUnknown", n))).Concat(r.Notes));
    }

    /// <summary>One filled copy per row of a CSV, into a folder the user picks.</summary>
    [RelayCommand]
    private async Task FillFromTableAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        if (await main.Dialogs.OpenFormDataAsync() is not { } table) return;
        if (await main.Dialogs.PickFolderAsync() is not { } folder) return;
        var column = NameColumn.Trim().Length > 0 ? NameColumn.Trim() : null;
        FillManyResult? r = null;
        if (await Run(() => r = PdfFormData.FillMany(pdf, table, folder, column)) && r is not null)
        {
            main.Status = L.F("Ui_FormDataFilledMany", r.Rows.Count, folder);
            FormDataResult = string.Join("\n", new[] { main.Status }
                .Concat(r.UnknownColumns.Select(c => L.F("Ui_FormDataUnknown", c)))
                .Concat(r.Rows.SelectMany(x => x.Notes.Select(n => $"{Path.GetFileName(x.Output)}: {n}"))));
        }
    }

    /// <summary>Many filled forms into one CSV.</summary>
    [RelayCommand]
    private async Task CollectAsync()
    {
        var files = await main.Dialogs.OpenPdfsAsync();
        var pdfs = files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pdfs.Count == 0) return;
        if (await main.Dialogs.SaveFormDataAsync(L.Get("Ui_FormDataCollectName") + ".csv") is not { } output) return;
        int n = 0;
        if (await Run(() =>
        {
            if (File.Exists(output)) File.Delete(output);
            n = PdfFormData.Collect(pdfs, output);
        }))
            main.Status = FormDataResult = L.F("Ui_FormDataCollected", Path.GetFileName(output), n);
    }

    private async Task<bool> Run(Action work)
    {
        bool ok = false;
        await main.Run(async () => { await Task.Run(work); ok = true; });
        return ok;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Asks where to save &lt;name&gt;&lt;suffix&gt;.pdf, writes through a temporary file, opens the copy.</summary>
    private async Task Write(string suffix, Action<string> write, Func<string, string> status)
    {
        if (main.PdfPath is not { } pdf) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + suffix + ".pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        string? written = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp.pdf";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                await Task.Run(() => write(tmp));
                File.Move(tmp, output, overwrite: true);
                written = output;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (written is null) return;
        await main.LoadAsync(written);
        main.Status = status(Path.GetFileName(written));
    }
}
