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

/// <summary>
/// A comment the PDF already has, with the changes made to it in this session (its text, colour and place, removal, new
/// replies). They are written when the copy is saved.
/// </summary>
public sealed partial class ExistingComment : ObservableObject
{
    public ExistingComment(CommentInfo info, IEnumerable<CommentInfo> replies)
    {
        Info = info;
        _text = info.Text;
        _color = info.Color;
        _rect = info.Rect;
        Thread = [.. replies];
    }

    public CommentInfo Info { get; }
    /// <summary>The replies it already has, oldest first.</summary>
    public IReadOnlyList<CommentInfo> Thread { get; }
    public ObservableCollection<string> NewReplies { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged), nameof(Label))] private string _text;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged), nameof(Label))] private string _color;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged), nameof(Label))] private PdfRect _rect;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsChanged), nameof(Label))] private bool _deleted;

    public bool IsChanged => Deleted || Text != Info.Text || !string.Equals(Color, Info.Color, StringComparison.OrdinalIgnoreCase)
                             || Rect != Info.Rect || NewReplies.Count > 0;
    public bool Moved => Rect != Info.Rect;

    public string Kind => L.Get("Annot_" + Info.Type) is var k && k != "Annot_" + Info.Type ? k : Info.Type;

    public string Label => (Deleted ? "✕ " : IsChanged ? "✎ " : "") + L.F("Ui_CommentItem", Kind, Info.Page + 1)
                           + (Info.Author.Length > 0 ? " · " + Info.Author : "")
                           + (Text.Length > 0 ? " · " + (Text.Length > 30 ? Text[..30] + "…" : Text) : "")
                           + (Thread.Count + NewReplies.Count > 0 ? " · " + L.F("Ui_CommentReplies", Thread.Count + NewReplies.Count) : "");

    public string ThreadText => string.Join("\n", Thread.Select(r => (r.Author.Length > 0 ? r.Author + ": " : "") + r.Text)
                                                     .Concat(NewReplies.Select(r => "→ " + r)));
    public bool HasThread => Thread.Count + NewReplies.Count > 0;

    public void AddReply(string text)
    {
        NewReplies.Add(text);
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ThreadText));
        OnPropertyChanged(nameof(HasThread));
    }

    /// <summary>What to ask <see cref="PdfComments.Change"/> for.</summary>
    public IEnumerable<CommentChange> Changes(string author)
    {
        if (Deleted) { yield return new CommentChange(Info.Id, Delete: true); yield break; }
        bool edited = Text != Info.Text || !string.Equals(Color, Info.Color, StringComparison.OrdinalIgnoreCase) || Moved;
        if (edited)
            yield return new CommentChange(Info.Id, Text != Info.Text ? Text : null,
                                           !string.Equals(Color, Info.Color, StringComparison.OrdinalIgnoreCase) ? Color : null, Moved ? Rect : null);
        foreach (var r in NewReplies) yield return new CommentChange(Info.Id, Reply: r, Author: author);
    }
}

/// <summary>A colour to give a comment.</summary>
public sealed record CommentSwatch(string Hex)
{
    public Avalonia.Media.IBrush Brush { get; } = Avalonia.Media.Brush.Parse(Hex);
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

    /// <summary>The existing comment being looked at or changed (chosen in the list or clicked on the page).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedExisting))] private ExistingComment? _selectedExisting;
    [ObservableProperty] private string _replyDraft = "";

    public bool HasSelectedExisting => SelectedExisting is not null;
    public bool NeedsText => Mode is CommentKind.Note or CommentKind.TextBox;
    public string Hint => L.Get("Ui_CommentHint" + Mode);
    public bool HasPending => Pending.Count > 0;
    public bool HasChanges => Existing.Any(e => e.IsChanged);
    public bool CanSave => (HasPending || HasChanges) && main.HasDocument;

    /// <summary>Colours an existing comment can take.</summary>
    public static IReadOnlyList<CommentSwatch> Colors { get; } =
        [.. new[] { "#FFE14D", "#DA1E28", "#0F62FE", "#24A148", "#8A3FFC", "#161616" }.Select(h => new CommentSwatch(h))];

    partial void OnSelectedExistingChanged(ExistingComment? value)
    {
        ReplyDraft = "";
        if (value is not null && value.Info.Page != main.PageIndex && value.Info.Page < main.PageCount) main.PageIndex = value.Info.Page;
        Changed?.Invoke();
    }

    [RelayCommand]
    private void SetColor(string? color)
    {
        if (SelectedExisting is not { Info.Redrawable: true } c || color is null) return;
        c.Color = color;
        Refresh();
    }

    [RelayCommand]
    private void ToggleDelete()
    {
        if (SelectedExisting is not { } c) return;
        c.Deleted = !c.Deleted;
        Refresh();
    }

    [RelayCommand]
    private void Reply()
    {
        if (SelectedExisting is not { Deleted: false } c || ReplyDraft.Trim().Length == 0) return;
        c.AddReply(ReplyDraft.Trim());
        ReplyDraft = "";
        Refresh();
    }

    [RelayCommand]
    private void Revert()
    {
        if (SelectedExisting is not { } c) return;
        var fresh = Watch(new ExistingComment(c.Info, c.Thread));
        Existing[Existing.IndexOf(c)] = fresh;
        SelectedExisting = fresh;
        Refresh();
    }

    /// <summary>The existing comment under a point of the current page (the smallest, so a note on a box wins).</summary>
    public ExistingComment? HitExisting(double x, double y) =>
        Existing.Where(e => e.Info.Page == main.PageIndex && new PdfRect(e.Rect.X0 - 2, e.Rect.Y0 - 2, e.Rect.X1 + 2, e.Rect.Y1 + 2).Contains(x, y))
                .OrderBy(e => e.Rect.Area).FirstOrDefault();

    /// <summary>A comment dragged on the page (it must be one that can move).</summary>
    public void MoveExisting(ExistingComment c, PdfRect to)
    {
        if (!c.Info.Movable || c.Deleted) return;
        c.Rect = to;
        Refresh();
    }

    /// <summary>Redraws the page.</summary>
    public event Action? Changed;

    private readonly Dictionary<int, IReadOnlyList<PdfChar>> _chars = [];
    private int _prepared;

    /// <summary>When the tool opens or a document loads: the comments already in the file.</summary>
    public async Task PrepareAsync()
    {
        int version = ++_prepared;
        if (main.PdfPath is not { } pdf) { Existing.Clear(); return; }
        var list = await Task.Run(() =>
        {
            try { return PdfComments.List(pdf); }
            catch (Exception e) when (e is IOException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException) { return []; }
        });
        if (version != _prepared || main.PdfPath != pdf) return;   // a newer call (or another file) won
        SelectedExisting = null;
        Existing.Clear();
        // threads: each comment with the replies to it (replies are not listed on their own)
        var replies = list.Where(c => c.ReplyTo is not null).GroupBy(c => c.ReplyTo!).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var c in list.Where(c => c.ReplyTo is null))
            Existing.Add(Watch(new ExistingComment(c, replies.GetValueOrDefault(c.Id) ?? [])));
        Refresh();
    }

    private ExistingComment Watch(ExistingComment c)
    {
        c.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ExistingComment.IsChanged)) Refresh(); };
        return c;
    }

    public void Reset()
    {
        Pending.Clear();
        Existing.Clear();
        SelectedExisting = null;
        _chars.Clear();
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(HasChanges));
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

    /// <summary>Writes &lt;name&gt;_commented.pdf with the changes to existing comments and the new ones, and opens it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave || main.PdfPath is not { } pdf) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_commented.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        var specs = Pending.Select(p => p.Spec).ToList();
        var changes = Existing.SelectMany(e => e.Changes(Author.Trim())).ToList();
        string? written = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            var tmp2 = output + ".changed.tmp";
            foreach (var t in new[] { tmp, tmp2 }) if (File.Exists(t)) File.Delete(t);
            try
            {
                // existing comments are changed first: new ones are added after them, so their ids stay valid
                var source = pdf;
                (int Changed, int Removed, int Replies) r = default;
                if (changes.Count > 0)
                {
                    r = await Task.Run(() => PdfComments.Change(pdf, tmp2, changes));
                    source = tmp2;
                }
                int n = 0;
                if (specs.Count > 0) n = await Task.Run(() => PdfComments.Add(source, tmp, specs));
                else File.Move(tmp2, tmp);
                File.Move(tmp, output, overwrite: true);
                written = output;
                main.Status = L.F("Ui_CommentsSaved", Path.GetFileName(output), n, r.Changed, r.Removed, r.Replies);
            }
            finally { foreach (var t in new[] { tmp, tmp2 }) if (File.Exists(t)) File.Delete(t); }
        });
        if (written is not null)
        {
            var status = main.Status;
            await main.LoadAsync(written);
            main.Status = status;
        }
    }
}
