// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>A change waiting to be saved: the text in <see cref="Edit"/>'s box becomes its text.</summary>
public sealed record PendingTextEdit(TextEdit Edit, string Old, double Size)
{
    public string Where => L.F("Ui_EditTextOnPage", Edit.Page + 1);
    public string Change => L.F("Ui_EditTextChange", Short(Old), Edit.Text.Length == 0 ? L.Get("Ui_EditTextNothing") : Short(Edit.Text));
    private static string Short(string s) => s.Length > 40 ? s[..37] + "…" : s;
}

/// <summary>
/// Edit text: click a line on the page and type its new text, or find and replace, then save a copy with the changes
/// (<see cref="PdfTextEditor"/>). The original is not changed.
/// </summary>
public sealed partial class EditTextViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<PendingTextEdit> Pending { get; } = [];

    /// <summary>The lines of the page shown, as they can be clicked.</summary>
    public IReadOnlyList<TextSegment> Segments { get; private set; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private TextSegment? _selected;
    [ObservableProperty] private string _text = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanReplace))] private string _find = "";
    [ObservableProperty] private string _replace = "";
    [ObservableProperty] private bool _matchCase = true;
    [ObservableProperty] private bool _wholeWords;
    [ObservableProperty] private string _message = "";

    /// <summary>Raised when the lines, the selection or the changes move (the canvas redraws).</summary>
    public event Action? Changed;

    public bool HasSelection => Selected is not null;
    public bool HasPending => Pending.Count > 0;
    public bool CanReplace => main.HasDocument && Find.Length > 0;
    public bool CanSave => main.HasDocument && Pending.Count > 0;
    public string SaveText => L.F("Ui_EditTextSave", Pending.Count);

    private int _loaded;

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveText));
        Changed?.Invoke();
    }

    public void Reset()
    {
        Pending.Clear();
        Segments = [];
        Selected = null;
        Text = "";
        Message = "";
        Refresh();
    }

    /// <summary>When the tool opens or the page changes: the page's lines.</summary>
    public async Task PrepareAsync()
    {
        int version = ++_loaded;
        Selected = null;
        if (main.PdfPath is not { } path) { Segments = []; Refresh(); return; }
        int page = main.PageIndex;
        IReadOnlyList<TextSegment> found;
        try { found = await MainViewModel.Pdfium(() => PdfTextEditor.Segments(path, page)); }
        catch (Exception e) when (e is IOException or PageAssemblyException or InvalidOperationException) { found = []; }
        if (version != _loaded || main.PdfPath != path || main.PageIndex != page) return;
        Segments = found;
        Refresh();
    }

    /// <summary>The line under a point of the page (page points), if any.</summary>
    public TextSegment? SegmentAt(double x, double y) =>
        Segments.Where(s => s.Box.X0 - 2 <= x && x <= s.Box.X1 + 2 && s.Box.Y0 - 2 <= y && y <= s.Box.Y1 + 2)
                .OrderBy(s => s.Box.Area).FirstOrDefault();

    /// <summary>A click on the page: that line is the one being edited (its new text, if it already has one).</summary>
    public void Select(TextSegment? segment)
    {
        Selected = segment;
        Message = segment is { Horizontal: false } ? L.Get("Err_EditNotHorizontal") : "";
        Text = segment is null ? "" : PendingFor(segment)?.Edit.Text ?? segment.Text;
        Changed?.Invoke();
    }

    private PendingTextEdit? PendingFor(TextSegment s) =>
        Pending.FirstOrDefault(p => p.Edit.Page == s.Page && p.Edit.Box == s.Box);

    /// <summary>Enter: the line's new text is kept as a change (typing the old text back removes the change).</summary>
    [RelayCommand]
    private void Commit()
    {
        if (Selected is not { } s) return;
        if (!s.Horizontal) { Message = L.Get("Err_EditNotHorizontal"); return; }
        var text = Text.Replace("\r", "").Replace("\n", " ");
        if (PendingFor(s) is { } old) Pending.Remove(old);
        if (text != s.Text) Pending.Add(new PendingTextEdit(new TextEdit(s.Page, s.Box, text), s.Text, s.Size));
        Selected = null;
        Text = "";
        Message = "";
        Refresh();
    }

    /// <summary>Esc: the line is left as it was.</summary>
    [RelayCommand]
    private void Cancel()
    {
        Selected = null;
        Text = "";
        Message = "";
        Changed?.Invoke();
    }

    /// <summary>Every place the text to find appears becomes a change.</summary>
    [RelayCommand]
    private async Task ReplaceAllAsync()
    {
        if (main.PdfPath is not { } path || !CanReplace) return;
        var (find, with, matchCase, whole) = (Find, Replace, MatchCase, WholeWords);
        try
        {
            var hits = await MainViewModel.Pdfium(() => PdfSearch.Find(path, find, matchCase, whole));
            if (main.PdfPath != path) return;
            int added = 0;
            foreach (var h in hits)
            {
                var box = h.Boxes.Aggregate((a, b) => new PdfRect(Math.Min(a.X0, b.X0), Math.Min(a.Y0, b.Y0), Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1)));
                if (Pending.Any(p => p.Edit.Page == h.Page && p.Edit.Box.Intersect(box).Area > 0)) continue;
                Pending.Add(new PendingTextEdit(new TextEdit(h.Page, box, with), h.Text, box.Height * 0.8));
                added++;
            }
            Message = hits.Count == 0 ? L.F("Ui_EditTextNotFound", find) : L.F("Ui_EditTextReplaced", added);
            if (hits.Count > 0 && hits.All(h => h.Page != main.PageIndex)) main.PageIndex = hits[0].Page;
        }
        catch (Exception e) when (e is IOException or PageAssemblyException)
        {
            Message = Errors.Text(e);
        }
        Refresh();
    }

    [RelayCommand]
    private void Show(PendingTextEdit? edit)
    {
        if (edit is not null) main.PageIndex = edit.Edit.Page;
    }

    [RelayCommand]
    private void Remove(PendingTextEdit? edit)
    {
        if (edit is null) return;
        Pending.Remove(edit);
        Refresh();
    }

    /// <summary>Saves &lt;name&gt;_edited.pdf (the user picks the name) and opens it; the original is not changed.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (main.PdfPath is not { } pdf || !CanSave) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_edited.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        var edits = Pending.Select(p => p.Edit).ToList();
        IReadOnlyList<TextEditResult>? results = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                results = await MainViewModel.Pdfium(() => PdfTextEditor.Apply(pdf, tmp, edits));
                File.Move(tmp, output, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (results is null) return;
        var page = main.PageIndex;
        await main.LoadAsync(output);
        if (page < main.PageCount) main.PageIndex = page;
        int similar = results.Count(r => !r.KeptFont && r.New.Length > 0);
        main.Status = similar == 0 ? L.F("Ui_TextEdited", Path.GetFileName(output), results.Count)
                                   : L.F("Ui_TextEditedSimilar", Path.GetFileName(output), results.Count, similar);
    }
}
