// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>A difference as the panel lists it, seen from the open document.</summary>
public sealed record CompareRow(PdfChange Change, bool OpenIsNew)
{
    /// <summary>The open document's page where it shows (0-based), if it shows there at all.</summary>
    public int? Page => OpenIsNew ? Change.NewPage : Change.OldPage;
    public IReadOnlyList<PdfRect> Boxes => OpenIsNew ? Change.NewBoxes : Change.OldBoxes;

    public string Where => Page is { } p ? L.F("Ui_CmpWhere", p + 1) : "";
    public string Text => Change.Kind switch
    {
        ChangeKind.Changed => L.F("Ui_CmpRowChanged", Short(Change.OldText), Short(Change.NewText)),
        ChangeKind.Inserted => L.F("Ui_CmpRowAdded", Short(Change.NewText)),
        ChangeKind.Deleted => L.F("Ui_CmpRowDeleted", Short(Change.OldText)),
        ChangeKind.Visual => Change.OldText.Length > 0 ? L.F("Cmp_NoteSize", Change.OldText, Change.NewText) : L.Get("Ui_CmpRowVisual"),
        ChangeKind.PageAdded => L.Get("Ui_CmpRowPageAdded"),
        _ => L.F("Ui_CmpRowPageRemoved", Change.OldPage!.Value + 1),
    };
    public string Mark => Change.Kind switch
    {
        ChangeKind.Inserted or ChangeKind.PageAdded => "+",
        ChangeKind.Deleted or ChangeKind.PageRemoved => "−",
        ChangeKind.Visual => "◇",
        _ => "~",
    };

    private static string Short(string s) => s.Length > 60 ? s[..57] + "…" : s;
}

/// <summary>
/// Compare versions: the open document against another version of it (<see cref="PdfComparer"/>). The differences are
/// listed and shown on the page; a copy of the newer version can be saved with each one marked as a comment.
/// </summary>
public sealed partial class CompareToolViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<CompareRow> Rows { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOther), nameof(OtherText), nameof(CanCompare))] private string? _other;
    /// <summary>The other file is the newer version (by default the open one is).</summary>
    [ObservableProperty] private bool _otherIsNewer;
    [ObservableProperty] private string _summary = "";

    private CompareReport? _report;
    private string? _reportFor;

    /// <summary>Raised when the differences change (the canvas redraws).</summary>
    public event Action? Changed;

    public bool HasOther => Other is not null;
    public string OtherText => Other is null ? "" : L.F("Ui_CompareOther", Path.GetFileName(Other));
    public bool CanCompare => main.HasDocument && Other is not null;
    public bool HasRows => Rows.Count > 0;
    public bool CanSave => _report is { Same: false } && main.PdfPath == _reportFor;

    partial void OnOtherIsNewerChanged(bool value) => Clear();

    public void Reset()
    {
        Clear();
        Other = null;
    }

    public void ClearResults() => Clear();

    private void Clear()
    {
        Rows.Clear();
        _report = null;
        Summary = "";
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CanSave));
        Changed?.Invoke();
    }

    /// <summary>The differences on a page of the open document.</summary>
    public IEnumerable<CompareRow> On(int page) => main.PdfPath == _reportFor ? Rows.Where(r => r.Page == page) : [];

    [RelayCommand]
    private async Task ChooseAsync()
    {
        if (await main.Dialogs.OpenPdfAsync() is not { } path) return;
        if (main.PdfPath is { } open && Path.GetFullPath(path) == Path.GetFullPath(open)) { main.Status = L.Get("Ui_CompareSameFile"); return; }
        Clear();
        Other = path;
    }

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (main.PdfPath is not { } open || Other is not { } other) return;
        var (oldPath, newPath) = OtherIsNewer ? (open, other) : (other, open);
        CompareReport? report = null;
        await main.Run(async () => report = await MainViewModel.Pdfium(() => PdfComparer.Compare(oldPath, newPath)));
        if (report is null || main.PdfPath != open) return;
        _report = report;
        _reportFor = open;
        Rows.Clear();
        foreach (var c in report.Changes) Rows.Add(new CompareRow(c, !OtherIsNewer));
        Summary = report.Same ? L.Get("Ui_CompareNone")
            : L.F("Ui_CompareSummary", report.Changes.Count, report.Count(ChangeKind.Changed), report.Count(ChangeKind.Inserted),
                  report.Count(ChangeKind.Deleted), report.Count(ChangeKind.Visual), report.Count(ChangeKind.PageAdded), report.Count(ChangeKind.PageRemoved));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CanSave));
        if (Rows.FirstOrDefault(r => r.Page is not null) is { Page: { } first } && Rows.All(r => r.Page != main.PageIndex)) main.PageIndex = first;
        Changed?.Invoke();
    }

    [RelayCommand]
    private void Show(CompareRow? row)
    {
        if (row?.Page is { } p) main.PageIndex = p;
    }

    /// <summary>Saves a copy of the newer version with every difference marked as a comment, and opens it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_report is not { Same: false } report || main.PdfPath is not { } open || Other is not { } other) return;
        var newPath = OtherIsNewer ? other : open;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(newPath) + "_compared.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(newPath) || Path.GetFullPath(output) == Path.GetFullPath(open)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        int marks = -1;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                marks = await MainViewModel.Pdfium(() => PdfComparer.MarkUp(newPath, tmp, report));
                File.Move(tmp, output, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (marks < 0) return;
        await main.LoadAsync(output);
        main.Status = L.F("Ui_CompareMarked", Path.GetFileName(output), marks);
    }
}
