// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>A link already in the file, which can be marked for removal.</summary>
public sealed partial class LinkRow(LinkInfo link, Action changed) : ObservableObject
{
    public LinkInfo Link { get; } = link;
    [ObservableProperty] private bool _remove;
    partial void OnRemoveChanged(bool value) => changed();
    public string Where => L.F("Ui_LinkWhere", Link.Page + 1);
    public string Target => Link.Kind switch
    {
        "page" => L.F("Ui_LinkToPage", Link.Target),
        "uri" => Link.Target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? Link.Target[7..] : Link.Target,
        _ => L.Get("Ui_LinkOther"),
    };
}

/// <summary>A link to add.</summary>
public sealed record PendingLink(NewLink Link)
{
    public string Where => L.F("Ui_LinkWhere", Link.Page + 1);
    public string Target => PdfLinks.Parse(Link.Target) is ("page", var p) ? L.F("Ui_LinkToPage", p) : Link.Target;
}

/// <summary>
/// Links: the links of the file, outlined on the page; drag a box and type where it goes (a web or mail address, or a
/// page number), or let Washi turn the addresses written in the text into links; mark links to remove; save a copy
/// (<see cref="PdfLinks"/>).
/// </summary>
public sealed partial class LinksToolViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<LinkRow> Existing { get; } = [];
    public ObservableCollection<PendingLink> Pending { get; } = [];

    /// <summary>The box just drawn, waiting for its target.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDraft))] private (int Page, PdfRect Rect)? _draft;
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private string _message = "";

    public event Action? Changed;

    public bool HasDraft => Draft is not null;
    public bool HasExisting => Existing.Count > 0;
    public bool HasPending => Pending.Count > 0;
    public int ChangeCount => Pending.Count + Existing.Count(e => e.Remove);
    public bool CanSave => main.HasDocument && ChangeCount > 0;
    public string SaveText => L.F("Ui_LinksSave", ChangeCount);

    private int _loaded;

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasExisting));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveText));
        Changed?.Invoke();
    }

    public void Reset()
    {
        Existing.Clear();
        Pending.Clear();
        Draft = null;
        Target = "";
        Message = "";
        Refresh();
    }

    /// <summary>When the tool opens or a document loads: the file's links.</summary>
    public async Task PrepareAsync()
    {
        int version = ++_loaded;
        if (main.PdfPath is not { } path) { Reset(); return; }
        IReadOnlyList<LinkInfo> links;
        try { links = await Task.Run(() => PdfLinks.List(path)); }
        catch (Exception e) when (e is IOException or PdfSharp.Pdf.IO.PdfReaderException or InvalidOperationException) { links = []; }
        if (version != _loaded || main.PdfPath != path) return;
        Existing.Clear();
        foreach (var l in links) Existing.Add(new LinkRow(l, Refresh));
        Pending.Clear();
        Refresh();
    }

    /// <summary>A box drawn on the page: it waits for its target.</summary>
    public void Draw(PdfRect rect)
    {
        if (rect.Width < 4 || rect.Height < 4) return;
        Draft = (main.PageIndex, rect);
        Message = "";
        Changed?.Invoke();
    }

    [RelayCommand]
    private void Keep()
    {
        if (Draft is not { } d) return;
        try { PdfLinks.Parse(Target); }
        catch (PageAssemblyException e) { Message = Errors.Text(e); return; }
        if (int.TryParse(Target.Trim(), out var page) && page > main.PageCount) { Message = L.F("Err_RangeOutside", page, main.PageCount); return; }
        Pending.Add(new PendingLink(new NewLink(d.Page, d.Rect, Target.Trim())));
        Draft = null;
        Target = "";
        Message = "";
        Refresh();
    }

    [RelayCommand]
    private void Cancel()
    {
        Draft = null;
        Target = "";
        Message = "";
        Changed?.Invoke();
    }

    [RelayCommand]
    private async Task FindAddressesAsync()
    {
        if (main.PdfPath is not { } path) return;
        IReadOnlyList<NewLink>? found = null;
        await main.Run(async () => found = await MainViewModel.Pdfium(() => PdfLinks.FindAddresses(path)));
        if (found is null) return;
        int added = 0;
        foreach (var l in found.Where(l => !Pending.Any(p => p.Link.Page == l.Page && p.Link.Rect.Intersect(l.Rect).Area > 0)))
        {
            Pending.Add(new PendingLink(l));
            added++;
        }
        Message = added == 0 ? L.Get("Ui_LinksNoneFound") : L.F("Ui_LinksFound", added);
        Refresh();
    }

    [RelayCommand]
    private void RemovePending(PendingLink? link)
    {
        if (link is null) return;
        Pending.Remove(link);
        Refresh();
    }

    [RelayCommand]
    private void Show(object? row)
    {
        int? page = row switch { LinkRow r => r.Link.Page, PendingLink p => p.Link.Page, _ => null };
        if (page is { } p0) main.PageIndex = p0;
    }

    /// <summary>Saves &lt;name&gt;_links.pdf (the user picks the name) and opens it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (main.PdfPath is not { } pdf || !CanSave) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_links.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        var add = Pending.Select(p => p.Link).ToList();
        var remove = Existing.Where(e => e.Remove).Select(e => e.Link.Id).ToList();
        LinkResult? result = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                result = await MainViewModel.Pdfium(() => PdfLinks.Apply(pdf, tmp, add, remove));
                File.Move(tmp, output, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (result is null) return;
        var page = main.PageIndex;
        await main.LoadAsync(output);
        if (page < main.PageCount) main.PageIndex = page;
        main.Status = L.F("Ui_LinksSaved", Path.GetFileName(output), result.Added, result.Removed);
    }
}
