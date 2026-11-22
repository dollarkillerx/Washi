// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>One bookmark in the panel: the outline is shown as a flat list, each row indented by its level.</summary>
public sealed partial class BookmarkRow(int level, string title, int page, double? top, bool open = false, int? source = null) : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Indent))] private int _level = level;
    [ObservableProperty] private string _title = title;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PageLabel))] private int _page = page;
    public double? Top { get; set; } = top;
    public bool Open { get; } = open;
    public int? Source { get; } = source;

    public Avalonia.Thickness Indent => new(Level * 14, 0, 0, 0);
    public string PageLabel => Page >= 0 ? (Page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "↗";
}

/// <summary>
/// The Bookmarks panel (right rail): the document's outline. A click opens the bookmark's page at its place. Bookmarks
/// can be added for the page being read, renamed, removed, moved and nested, then saved into a copy
/// (<see cref="PdfBookmarks"/>).
/// </summary>
public sealed partial class BookmarksViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<BookmarkRow> Rows { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(UpCommand), nameof(DownCommand), nameof(IndentCommand), nameof(OutdentCommand), nameof(UseThisPageCommand))]
    private BookmarkRow? _selected;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSave))] private bool _changed;
    [ObservableProperty] private string _note = "";

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Rows.Count == 0;
    public bool CanSave => Changed && main.HasDocument;

    /// <summary>Raised to show a place: the window scrolls there once the page is drawn.</summary>
    public event Action<int, double?>? Navigate;

    private string? _loadedFor;

    /// <summary>Reads the open document's outline (when the panel is shown or a document opens).</summary>
    public async Task LoadAsync()
    {
        if (main.PdfPath is not { } path) { Clear(); return; }
        if (_loadedFor == path && !Changed) return;
        try
        {
            var outline = await Task.Run(() => PdfBookmarks.Read(path));
            if (main.PdfPath != path) return;
            Rows.Clear();
            foreach (var b in outline) Add(b, 0);
            _loadedFor = path;
            Note = "";
        }
        catch (Exception e) when (e is PageAssemblyException or IOException or PdfSharp.Pdf.IO.PdfReaderException)
        {
            Rows.Clear();
            Note = Errors.Text(e);
        }
        Selected = null;
        Changed = false;
        OnPropertyChanged(nameof(IsEmpty));

        void Add(Bookmark b, int level)
        {
            Rows.Add(Watch(new BookmarkRow(level, b.Title, b.Page, b.Top, b.Open, b.Source)));
            foreach (var c in b.Children) Add(c, level + 1);
        }
    }

    public void Clear()
    {
        Rows.Clear();
        Selected = null;
        Changed = false;
        Note = "";
        _loadedFor = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Opens the bookmark's page at its place.</summary>
    public void Go(BookmarkRow row)
    {
        Selected = row;
        if (row.Page < 0 || row.Page >= main.PageCount) return;
        main.PageIndex = row.Page;
        Navigate?.Invoke(row.Page, row.Top);
    }

    private void Edited()
    {
        Changed = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>A typed title counts as a change.</summary>
    private BookmarkRow Watch(BookmarkRow row)
    {
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BookmarkRow.Title)) Edited(); };
        return row;
    }

    // ------------------------------------------------------------------ editing (a row moves with the rows nested under it)

    /// <summary>The rows from <paramref name="i"/> to the end of its subtree (exclusive end).</summary>
    private int End(int i)
    {
        int j = i + 1;
        while (j < Rows.Count && Rows[j].Level > Rows[i].Level) j++;
        return j;
    }

    /// <summary>Adds a bookmark for the page being read, after the selected one (at its level), titled with the
    /// page's first line of text.</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (main.PdfPath is not { } path) return;
        int page = main.PageIndex;
        string title = L.F("Ui_BookmarkPage", page + 1);
        try
        {
            var text = await MainViewModel.Pdfium(() =>
            {
                using var d = Washi.Core.Pdfium.PdfiumDocument.Open(path);
                return d.GetPageText(page);
            });
            var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length >= 2);
            if (line is not null) title = line.Length > 60 ? line[..60].TrimEnd() + "…" : line;
        }
        catch (IOException) { }
        int at = Selected is { } s ? End(Rows.IndexOf(s)) : Rows.Count;
        var row = Watch(new BookmarkRow(Selected?.Level ?? 0, title, page, null));
        Rows.Insert(at, row);
        Selected = row;
        Edited();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (Selected is not { } s) return;
        int i = Rows.IndexOf(s), end = End(i);
        for (int k = end - 1; k >= i; k--) Rows.RemoveAt(k);
        Selected = i < Rows.Count ? Rows[i] : Rows.LastOrDefault();
        Edited();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Up()
    {
        if (Selected is not { } s) return;
        int i = Rows.IndexOf(s);
        int prev = i - 1;
        while (prev >= 0 && Rows[prev].Level > s.Level) prev--;
        if (prev < 0 || Rows[prev].Level != s.Level) return;   // first among its siblings
        Move(i, End(i), prev);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Down()
    {
        if (Selected is not { } s) return;
        int i = Rows.IndexOf(s), end = End(i);
        if (end >= Rows.Count || Rows[end].Level != s.Level) return;   // last among its siblings
        int nextEnd = End(end);
        Move(end, nextEnd, i);                                         // the next sibling goes before it
        Selected = s;
    }

    /// <summary>Moves rows [from, to) so they start at <paramref name="at"/> (at &lt; from).</summary>
    private void Move(int from, int to, int at)
    {
        var block = Rows.Skip(from).Take(to - from).ToList();
        var keep = Selected;
        for (int k = to - 1; k >= from; k--) Rows.RemoveAt(k);
        for (int k = 0; k < block.Count; k++) Rows.Insert(at + k, block[k]);
        Selected = keep;
        Edited();
    }

    /// <summary>Nests the selected bookmark under the one above it.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Indent()
    {
        if (Selected is not { } s) return;
        int i = Rows.IndexOf(s);
        if (i == 0 || Rows[i - 1].Level < s.Level) return;
        for (int k = i; k < End(i); k++) Rows[k].Level++;
        Edited();
    }

    /// <summary>Takes the selected bookmark out of its parent: it follows the parent's other bookmarks.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Outdent()
    {
        if (Selected is not { } s || s.Level == 0) return;
        int i = Rows.IndexOf(s), end = End(i);
        int parent = i - 1;
        while (Rows[parent].Level >= s.Level) parent--;
        int parentEnd = End(parent);
        var block = Rows.Skip(i).Take(end - i).ToList();
        foreach (var r in block) r.Level--;
        for (int k = end - 1; k >= i; k--) Rows.RemoveAt(k);
        int at = parentEnd - block.Count;
        for (int k = 0; k < block.Count; k++) Rows.Insert(at + k, block[k]);
        Selected = s;
        Edited();
    }

    /// <summary>Points the selected bookmark at the page being read.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void UseThisPage()
    {
        if (Selected is not { } s) return;
        s.Page = main.PageIndex;
        s.Top = null;
        Edited();
    }

    // ------------------------------------------------------------------ saving

    /// <summary>The rows as an outline tree.</summary>
    public IReadOnlyList<Bookmark> Outline()
    {
        int i = 0;
        return Level(0);

        List<Bookmark> Level(int level)
        {
            var list = new List<Bookmark>();
            while (i < Rows.Count && Rows[i].Level >= level)
            {
                var r = Rows[i++];
                var children = Level(r.Level + 1);
                list.Add(new Bookmark(r.Title.Trim(), r.Page, r.Top, children, r.Open, r.Source));
            }
            return list;
        }
    }

    /// <summary>Saves a copy with these bookmarks (&lt;name&gt;_bookmarks.pdf) and opens it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (main.PdfPath is not { } pdf || !CanSave) return;
        if (Rows.Any(r => string.IsNullOrWhiteSpace(r.Title))) { main.Status = L.Get("Err_BookmarkTitle"); return; }
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_bookmarks.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        var outline = Outline();
        string? written = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                await Task.Run(() => PdfBookmarks.Write(pdf, tmp, outline));
                File.Move(tmp, output, overwrite: true);
                written = output;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (written is null) return;
        int page = main.PageIndex;
        await main.LoadAsync(written);
        if (page < main.PageCount) main.PageIndex = page;
        main.Status = L.F("Ui_BookmarksSaved", Path.GetFileName(written), outline.Sum(b => b.Flatten().Count()));
    }
}
