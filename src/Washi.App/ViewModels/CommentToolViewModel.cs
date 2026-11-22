// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.App.ViewModels;

/// <summary>A comment added in this session, not yet saved.</summary>
public sealed record PendingComment(CommentSpec Spec)
{
    public string Label => L.F("Ui_CommentItem", L.Get("Comment_" + Spec.Kind), Spec.Page + 1)
                           + (Spec.Text.Length > 0 ? " · " + (Spec.Text.Length > 30 ? Spec.Text[..30] + "…" : Spec.Text) : "");
}

/// <summary>A comment the PDF already has (shown, not changed).</summary>
public sealed record ExistingComment(CommentInfo Info)
{
    public string Label => L.F("Ui_CommentItem", Info.Type, Info.Page + 1)
                           + (Info.Author.Length > 0 ? " · " + Info.Author : "")
                           + (Info.Text.Length > 0 ? " · " + (Info.Text.Length > 30 ? Info.Text[..30] + "…" : Info.Text) : "");
}

/// <summary>A choice in the comment toolbar.</summary>
public sealed record CommentMode(CommentKind Kind)
{
    public string Text => L.Get("Comment_" + Kind);
}

/// <summary>
/// Comment: highlight, underline or strike out text (drag over it), add sticky notes (click) and text boxes (drag), draw
/// with a pen or draw boxes, then save a copy with the comments as standard PDF annotations (<see cref="PdfComments"/>).
/// </summary>
public sealed partial class CommentToolViewModel(MainViewModel main) : ObservableObject
{
    public static IReadOnlyList<CommentMode> Modes { get; } =
    [
        new(CommentKind.Highlight), new(CommentKind.Underline), new(CommentKind.StrikeOut),
        new(CommentKind.Note), new(CommentKind.TextBox), new(CommentKind.Pen), new(CommentKind.Box),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsText), nameof(Hint))] private CommentKind _mode = CommentKind.Highlight;

    /// <summary>The toolbar's selection (the chips in the panel).</summary>
    [ObservableProperty] private CommentMode? _selectedMode = Modes[0];

    partial void OnSelectedModeChanged(CommentMode? value) { if (value is not null) Mode = value.Kind; }
    partial void OnModeChanged(CommentKind value) { if (SelectedMode?.Kind != value) SelectedMode = Modes.First(m => m.Kind == value); }

    /// <summary>The words of the next note or text box.</summary>
    [ObservableProperty] private string _draft = "";

    /// <summary>Shown as the comments' author; empty leaves it out.</summary>
    [ObservableProperty] private string _author = "";

    public ObservableCollection<PendingComment> Pending { get; } = [];
    public ObservableCollection<ExistingComment> Existing { get; } = [];

    public bool NeedsText => Mode is CommentKind.Note or CommentKind.TextBox;
    public string Hint => L.Get("Ui_CommentHint" + Mode);
    public bool HasPending => Pending.Count > 0;
    public bool CanSave => HasPending && main.HasDocument;

    /// <summary>Redraws the page.</summary>
    public event Action? Changed;

    private readonly Dictionary<int, IReadOnlyList<PdfChar>> _chars = [];

    /// <summary>When the tool opens or a document loads: the comments already in the file.</summary>
    public async Task PrepareAsync()
    {
        Existing.Clear();
        if (main.PdfPath is not { } pdf) return;
        var list = await Task.Run(() =>
        {
            try { return PdfComments.List(pdf); }
            catch (Exception e) when (e is IOException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException) { return []; }
        });
        foreach (var c in list) Existing.Add(new ExistingComment(c));
    }

    public void Reset()
    {
        Pending.Clear();
        Existing.Clear();
        _chars.Clear();
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(CanSave));
        Changed?.Invoke();
    }

    private void Add(CommentSpec spec)
    {
        Pending.Add(new PendingComment(spec with { Author = Author.Trim() }));
        Refresh();
    }

    /// <summary>A box dragged on the page (or a click, as a tiny box): what it makes depends on the mode.</summary>
    public async Task PlaceAsync(PdfRect box)
    {
        int page = main.PageIndex;
        switch (Mode)
        {
            case CommentKind.Highlight or CommentKind.Underline or CommentKind.StrikeOut:
            {
                var lines = Lines(await CharsAsync(page), box);
                if (lines.Count == 0) { main.Status = L.Get("Ui_CommentNoText"); return; }
                Add(new CommentSpec(page, Mode, Union(lines), Lines: lines));
                break;
            }
            case CommentKind.Note:
                if (Draft.Trim().Length == 0) { main.Status = L.Get("Ui_CommentTypeFirst"); return; }
                Add(new CommentSpec(page, CommentKind.Note, new PdfRect(box.X0, box.Y0, box.X0 + 18, box.Y0 + 18), Draft.Trim()));
                Draft = "";
                break;
            case CommentKind.TextBox:
                if (Draft.Trim().Length == 0) { main.Status = L.Get("Ui_CommentTypeFirst"); return; }
                if (box.Width < 20 || box.Height < 10) box = new PdfRect(box.X0, box.Y0, box.X0 + 160, box.Y0 + 36);   // a click
                Add(new CommentSpec(page, CommentKind.TextBox, box, Draft.Trim()));
                Draft = "";
                break;
            case CommentKind.Box:
                if (box.Width < 4 || box.Height < 4) return;
                Add(new CommentSpec(page, CommentKind.Box, box));
                break;
        }
    }

    /// <summary>A pen stroke drawn on the page (page points).</summary>
    public void AddStroke(IReadOnlyList<(double X, double Y)> stroke)
    {
        if (stroke.Count == 0) return;
        var b = new PdfRect(stroke.Min(p => p.X), stroke.Min(p => p.Y), stroke.Max(p => p.X), stroke.Max(p => p.Y));
        Add(new CommentSpec(main.PageIndex, CommentKind.Pen, b, Strokes: [stroke]));
    }

    [RelayCommand]
    private void RemovePending(PendingComment? c)
    {
        if (c is null) return;
        Pending.Remove(c);
        Refresh();
    }

    private async Task<IReadOnlyList<PdfChar>> CharsAsync(int page)
    {
        if (_chars.TryGetValue(page, out var c)) return c;
        if (main.PdfPath is not { } pdf) return [];
        c = await MainViewModel.Pdfium(() => { using var d = PdfiumDocument.Open(pdf); return d.ReadChars(page); });
        return _chars[page] = c;
    }

    /// <summary>The text lines under a dragged box: chars whose centres are inside, one rect per line.</summary>
    internal static List<PdfRect> Lines(IReadOnlyList<PdfChar> chars, PdfRect box)
    {
        var inside = chars.Where(c => !string.IsNullOrWhiteSpace(c.Text) && c.Rect.Width > 0
                                      && box.Contains(c.Rect.Center.X, c.Rect.Center.Y)).OrderBy(c => c.Rect.Center.Y).ToList();
        var lines = new List<PdfRect>();
        foreach (var c in inside)
        {
            int i = lines.FindIndex(l => Math.Abs(l.Center.Y - c.Rect.Center.Y) < 0.5 * Math.Max(l.Height, c.Rect.Height));
            if (i < 0) lines.Add(c.Rect);
            else lines[i] = new PdfRect(Math.Min(lines[i].X0, c.Rect.X0), Math.Min(lines[i].Y0, c.Rect.Y0),
                                        Math.Max(lines[i].X1, c.Rect.X1), Math.Max(lines[i].Y1, c.Rect.Y1));
        }
        return lines;
    }

    private static PdfRect Union(List<PdfRect> rs) => new(rs.Min(r => r.X0), rs.Min(r => r.Y0), rs.Max(r => r.X1), rs.Max(r => r.Y1));

    /// <summary>Writes &lt;name&gt;_commented.pdf with the new comments and opens it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave || main.PdfPath is not { } pdf) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_commented.pdf") is not { } output) return;
        var specs = Pending.Select(p => p.Spec).ToList();
        string? written = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            try
            {
                int n = await Task.Run(() => PdfComments.Add(pdf, tmp, specs));
                File.Move(tmp, output, overwrite: true);
                written = output;
                main.Status = L.F("Ui_Commented", Path.GetFileName(output), n);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (written is not null)
        {
            var status = main.Status;
            await main.LoadAsync(written);
            main.Status = status;
        }
    }
}
