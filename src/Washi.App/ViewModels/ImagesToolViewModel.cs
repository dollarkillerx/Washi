// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>An image on a page, with what will happen to it.</summary>
public sealed partial class ImageRow(PageImage image, Action changed) : ObservableObject
{
    public PageImage Image { get; } = image;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(State))] private bool _remove;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(State))] private string? _replaceWith;
    partial void OnRemoveChanged(bool value) { if (value) ReplaceWith = null; changed(); }
    partial void OnReplaceWithChanged(string? value) { if (value is not null) Remove = false; changed(); }

    public string Where => L.F("Ui_ImageWhere", Image.Page + 1, Image.PixelWidth, Image.PixelHeight);
    public string State => Remove ? L.Get("Ui_ImageWillGo") : ReplaceWith is { } f ? L.F("Ui_ImageWillBe", Path.GetFileName(f)) : "";
}

/// <summary>An image file to add, in a box drawn on the page.</summary>
public sealed record PendingImage(AddImage Add)
{
    public string Text => L.F("Ui_ImageAdd", Path.GetFileName(Add.File), Add.Page + 1);
}

/// <summary>
/// Images on the page: the images the page draws, outlined; click one to replace it with an image file (it keeps the
/// new image's shape in the old place) or remove it; choose an image file and drag a box to add it; save a copy
/// (<see cref="PdfPageImageEdit"/>).
/// </summary>
public sealed partial class ImagesToolViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<ImageRow> Images { get; } = [];
    public ObservableCollection<PendingImage> Added { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private ImageRow? _selected;
    /// <summary>An image file chosen to add, waiting for its box.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Placing), nameof(PlacingText))] private string? _toPlace;

    public event Action? Changed;

    public bool HasSelection => Selected is not null;
    public bool Placing => ToPlace is not null;
    public string PlacingText => ToPlace is { } f ? L.F("Ui_ImagePlace", Path.GetFileName(f)) : "";
    public bool HasImages => Images.Count > 0;
    public bool HasAdded => Added.Count > 0;
    public int ChangeCount => Added.Count + Images.Count(i => i.Remove || i.ReplaceWith is not null);
    public bool CanSave => main.HasDocument && ChangeCount > 0;
    public string SaveText => L.F("Ui_ImagesSave", ChangeCount);

    private int _loaded;

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(HasAdded));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveText));
        Changed?.Invoke();
    }

    public void Reset()
    {
        Images.Clear();
        Added.Clear();
        Selected = null;
        ToPlace = null;
        Refresh();
    }

    public async Task PrepareAsync()
    {
        int version = ++_loaded;
        if (main.PdfPath is not { } path) { Reset(); return; }
        IReadOnlyList<PageImage> found;
        try { found = await Task.Run(() => PdfPageImageEdit.List(path)); }
        catch (Exception e) when (e is IOException or PdfSharp.Pdf.IO.PdfReaderException or InvalidOperationException) { found = []; }
        if (version != _loaded || main.PdfPath != path) return;
        Images.Clear();
        foreach (var i in found) Images.Add(new ImageRow(i, Refresh));
        Added.Clear();
        Selected = null;
        Refresh();
    }

    /// <summary>A click on the page: the image under it (the smallest, when they overlap).</summary>
    public void SelectAt(double x, double y)
    {
        Selected = Images.Where(i => i.Image.Page == main.PageIndex && i.Image.Rect.X0 <= x && x <= i.Image.Rect.X1 && i.Image.Rect.Y0 <= y && y <= i.Image.Rect.Y1)
                         .OrderBy(i => i.Image.Rect.Area).FirstOrDefault();
        Changed?.Invoke();
    }

    /// <summary>A box drawn on the page: where the chosen image file goes.</summary>
    public void Draw(PdfRect rect)
    {
        if (ToPlace is not { } file) { SelectAt(rect.Center.X, rect.Center.Y); return; }
        Added.Add(new PendingImage(new AddImage(main.PageIndex, rect, file)));
        ToPlace = null;
        Refresh();
    }

    [RelayCommand]
    private async Task ChooseToAddAsync()
    {
        if (await main.Dialogs.OpenImageAsync() is { } file) ToPlace = file;
    }

    [RelayCommand]
    private async Task ReplaceAsync()
    {
        if (Selected is not { } row) return;
        if (await main.Dialogs.OpenImageAsync() is { } file) row.ReplaceWith = file;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected is { } row) row.Remove = !row.Remove;
    }

    [RelayCommand]
    private void Undo(object? item)
    {
        switch (item)
        {
            case ImageRow r: r.Remove = false; r.ReplaceWith = null; break;
            case PendingImage p: Added.Remove(p); Refresh(); break;
        }
    }

    [RelayCommand]
    private void Show(ImageRow? row)
    {
        if (row is null) return;
        main.PageIndex = row.Image.Page;
        Selected = row;
        Changed?.Invoke();
    }

    /// <summary>Saves &lt;name&gt;_images.pdf (the user picks the name) and opens it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (main.PdfPath is not { } pdf || !CanSave) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_images.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        var add = Added.Select(a => a.Add).ToList();
        var replace = Images.Where(i => i.ReplaceWith is not null).ToDictionary(i => i.Image.Id, i => i.ReplaceWith!);
        var remove = Images.Where(i => i.Remove).Select(i => i.Image.Id).ToList();
        ImageEditResult? result = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                result = await MainViewModel.Pdfium(() => PdfPageImageEdit.Apply(pdf, tmp, add, replace, remove));
                File.Move(tmp, output, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (result is null) return;
        var page = main.PageIndex;
        await main.LoadAsync(output);
        if (page < main.PageCount) main.PageIndex = page;
        main.Status = L.F("Ui_ImagesSaved", Path.GetFileName(output), result.Added, result.Replaced, result.Removed);
    }
}
