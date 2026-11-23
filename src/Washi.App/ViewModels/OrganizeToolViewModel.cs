// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.Core.Localization;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.App.ViewModels;

/// <summary>One page in the Organize grid: page <see cref="Page"/> (1-based) of <see cref="Source"/>.</summary>
public sealed partial class OrganizeItem(string source, int page, bool inserted) : ObservableObject
{
    public string Source { get; } = source;
    public int Page { get; } = page;
    /// <summary>Taken from another file (not the open document).</summary>
    public bool Inserted { get; } = inserted;

    [ObservableProperty] private Bitmap? _image;

    /// <summary>Extra clockwise turn in degrees: 0, 90, 180 or 270.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RotateText), nameof(Turn))] private int _rotate;

    /// <summary>The thumbnail's turn (a transform is not in the visual tree, so it cannot bind to Rotate itself).</summary>
    public Avalonia.Media.ITransform Turn => new Avalonia.Media.RotateTransform(Rotate);

    public string Label => Inserted ? L.F("Ui_OrganizeFrom", Page, Path.GetFileName(Source))
                                    : Page.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string RotateText => Rotate == 0 ? "" : $"↻ {Rotate}°";

    public PagePick ToPick() => new(Source, Page, Rotate);
}

/// <summary>
/// Organize pages: reorder, turn, remove and insert pages of the open document, then write a new PDF with
/// <see cref="PageAssembly.Organize"/> (form fields kept) and open it.
/// </summary>
public sealed partial class OrganizeToolViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string? _for;
    private readonly List<Action> _unhook = [];

    public OrganizeToolViewModel(MainViewModel main)
    {
        _main = main;
        Selected.CollectionChanged += (_, _) =>
        {
            NotifySelection();
            OnPropertyChanged(nameof(SelectedRange));
            OnPropertyChanged(nameof(ScopeText));
            // one page of the open document selected: show it in the page view
            if (Selected is [{ Inserted: false } only] && only.Source == _main.PdfPath) _main.PageIndex = only.Page - 1;
        };
    }

    public ObservableCollection<OrganizeItem> Pages { get; } = [];
    /// <summary>Bound to the grid's SelectedItems.</summary>
    public ObservableCollection<OrganizeItem> Selected { get; } = [];

    public bool HasSelection => Selected.Count > 0;
    public bool Changed => _for is not null && Pages.Count > 0 &&
                           (Pages.Count != _main.PageCount || Pages.Select((p, i) => p.Inserted || p.Page != i + 1 || p.Rotate != 0).Any(x => x));
    public bool CanSave => Changed;

    public string Summary
    {
        get
        {
            if (_for is null) return "";
            int kept = Pages.Count(p => !p.Inserted);
            return L.F("Ui_OrganizeSummary", Pages.Count, _main.PageCount - kept, Pages.Count(p => p.Rotate != 0), Pages.Count(p => p.Inserted));
        }
    }

    /// <summary>Called when the tool opens or a document loads: start from the open document's pages.</summary>
    public void Prepare()
    {
        if (_main.PdfPath != _for || Pages.Count == 0) Reset();
    }

    /// <summary>The document closed: nothing to organize.</summary>
    public void Clear()
    {
        Selected.Clear();
        Pages.Clear();
        _unhook.ForEach(u => u());
        _unhook.Clear();
        _for = null;
        Refresh();
    }

    [RelayCommand]
    private void Reset()
    {
        Selected.Clear();
        Pages.Clear();
        _unhook.ForEach(u => u());
        _unhook.Clear();
        _for = _main.PdfPath;
        if (_for is { } path)
            foreach (var thumb in _main.Thumbnails)
            {
                var item = new OrganizeItem(path, thumb.Index + 1, false) { Image = thumb.Image };
                // thumbnails render in the background: pick the image up when it arrives
                System.ComponentModel.PropertyChangedEventHandler h = (_, e) => { if (e.PropertyName == nameof(PageThumb.Image)) item.Image = thumb.Image; };
                thumb.PropertyChanged += h;
                _unhook.Add(() => thumb.PropertyChanged -= h);
                Pages.Add(item);
            }
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Changed));
        OnPropertyChanged(nameof(CanSave));
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(HasSelection));
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))] private void RotateLeft() => Turn(-90);
    [RelayCommand(CanExecute = nameof(HasSelection))] private void RotateRight() => Turn(90);

    private void Turn(int degrees)
    {
        foreach (var p in Selected) p.Rotate = ((p.Rotate + degrees) % 360 + 360) % 360;
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (Selected.Count == 0) return;
        int first = Selected.Min(Pages.IndexOf);
        foreach (var p in Selected.ToList()) Pages.Remove(p);
        Selected.Clear();
        if (Pages.Count > 0) Selected.Add(Pages[Math.Min(first, Pages.Count - 1)]);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))] private void MoveUp() => Move(-1);
    [RelayCommand(CanExecute = nameof(HasSelection))] private void MoveDown() => Move(1);

    /// <summary>Moves the selected pages one place, as a block; stops at either end.</summary>
    private void Move(int delta)
    {
        var idx = Selected.Select(Pages.IndexOf).Where(i => i >= 0).Order().ToList();
        if (idx.Count == 0 || (delta < 0 && idx[0] == 0) || (delta > 0 && idx[^1] == Pages.Count - 1)) return;
        var keep = Selected.ToList();
        foreach (int i in delta < 0 ? idx : Enumerable.Reverse(idx)) Pages.Move(i, i + delta);
        Selected.Clear();
        foreach (var p in keep) Selected.Add(p);
        Refresh();
    }

    [RelayCommand]
    private async Task InsertFilesAsync() => await InsertAsync(await _main.Dialogs.OpenPdfsAsync());

    /// <summary>Adds every page of <paramref name="paths"/> after the last selected page (or at the end).</summary>
    public async Task InsertAsync(IEnumerable<string> paths)
    {
        if (_for is null) return;
        var added = new List<OrganizeItem>();
        await _main.Run(async () =>
        {
            int at = Selected.Count > 0 ? Selected.Max(Pages.IndexOf) + 1 : Pages.Count;
            foreach (var path in paths)
            {
                var (count, mayAssemble) = await Task.Run(() => PageAssembly.Inspect(path));
                if (!mayAssemble) { _main.Status = L.F("Ui_ProtectedNote", Path.GetFileName(path)); continue; }
                for (int p = 1; p <= count; p++)
                {
                    var item = new OrganizeItem(path, p, true);
                    Pages.Insert(at++, item);
                    added.Add(item);
                }
            }
        });
        if (added.Count == 0) return;
        Selected.Clear();
        foreach (var p in added) Selected.Add(p);
        Refresh();
        foreach (var item in added)
        {
            try
            {
                var img = await MainViewModel.Pdfium(() =>
                {
                    using var d = PdfiumDocument.Open(item.Source);
                    var info = d.GetPageInfo(item.Page - 1);
                    return d.RenderPage(item.Page - 1, 220.0 / Math.Max(info.Width, info.Height));
                });
                item.Image = Imaging.ToBitmap(img);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException) { return; }
        }
    }

    /// <summary>Writes the pages as a new PDF and opens it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave || _for is not { } source) return;
        if (await _main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(source) + "_organized.pdf") is not { } output) return;
        var picks = Pages.Select(p => p.ToPick()).ToList();
        string? written = null;
        await _main.Run(async () =>
        {
            var tmp = output + ".tmp";
            try
            {
                int pages = await Task.Run(() => PageAssembly.Organize(picks, tmp));
                File.Move(tmp, output, overwrite: true);
                written = output;
                _main.Status = L.F("Pdf_Organized", Path.GetFileName(output), pages, Washi.Core.Forms.AcroFormFields.List(output).Count);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (written is not null)
        {
            var status = _main.Status;
            await _main.LoadAsync(written);     // Prepare() runs from LoadAsync: the grid shows the new file
            _main.Status = status;
        }
    }

    // ------------------------------------------------------------------ crop, and pages as images

    [ObservableProperty] private decimal? _cropTop = 10;
    [ObservableProperty] private decimal? _cropRight = 10;
    [ObservableProperty] private decimal? _cropBottom = 10;
    [ObservableProperty] private decimal? _cropLeft = 10;
    /// <summary>Trim the white paper around the content instead of cutting margins.</summary>
    [ObservableProperty] private bool _cropTrim;
    /// <summary>0 PNG, 1 JPEG.</summary>
    [ObservableProperty] private int _exportFormatIndex;
    /// <summary>0 72 dpi, 1 150 dpi, 2 300 dpi.</summary>
    [ObservableProperty] private int _exportDpiIndex = 1;

    /// <summary>The open document's selected pages as a range ("2,5"), or null for all of them.</summary>
    public string? SelectedRange
    {
        get
        {
            var own = Selected.Where(p => !p.Inserted && p.Source == _main.PdfPath).Select(p => p.Page).Distinct().Order().ToList();
            return own.Count == 0 ? null : string.Join(",", own);
        }
    }

    public string ScopeText => SelectedRange is { } r ? L.F("Ui_PagesScopeSome", r) : L.Get("Ui_PagesScopeAll");

    private static double Mm(decimal? v) => (double)(v ?? 0) * 72 / 25.4;

    /// <summary>Saves &lt;name&gt;_cropped.pdf (the user picks the name) and opens it.</summary>
    [RelayCommand]
    private async Task CropAsync()
    {
        if (_main.PdfPath is not { } pdf) return;
        if (await _main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_cropped.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { _main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        var options = new CropOptions(Mm(CropTop), Mm(CropRight), Mm(CropBottom), Mm(CropLeft), CropTrim, 6, SelectedRange);
        int pages = -1;
        await _main.Run(async () =>
        {
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                pages = await MainViewModel.Pdfium(() => PdfPageImages.Crop(pdf, tmp, options));
                File.Move(tmp, output, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (pages < 0) return;
        var at = _main.PageIndex;
        await _main.LoadAsync(output);
        if (at < _main.PageCount) _main.PageIndex = at;
        _main.Status = L.F("Ui_Cropped", Path.GetFileName(output), pages);
    }

    /// <summary>Writes the pages as images into a folder the user picks.</summary>
    [RelayCommand]
    private async Task ExportImagesAsync()
    {
        if (_main.PdfPath is not { } pdf) return;
        if (await _main.Dialogs.PickFolderAsync() is not { } folder) return;
        var options = new ExportOptions(ExportFormatIndex == 1 ? "jpeg" : "png", ExportDpiIndex switch { 0 => 72, 2 => 300, _ => 150 }, SelectedRange);
        IReadOnlyList<string>? files = null;
        await _main.Run(async () => files = await MainViewModel.Pdfium(() => PdfPageImages.Export(pdf, folder, options)));
        if (files is not null) _main.Status = L.F("Ui_Exported", files.Count, folder);
    }
}
