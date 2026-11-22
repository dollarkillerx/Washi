// SPDX-License-Identifier: AGPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>
/// The find bar over the page (⌘/Ctrl+F), in every tool: searches the open document's text as you type, highlights
/// every hit on the page and steps through them (Enter, Shift+Enter).
/// </summary>
public sealed partial class FindViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText), nameof(NoHits))] private string _query = "";
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _wholeWords;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText), nameof(HasHits), nameof(NoHits))] private int _index = -1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText), nameof(NoHits))] private bool _searching;

    public IReadOnlyList<TextHit> Hits { get; private set; } = [];
    public TextHit? Current => Index >= 0 && Index < Hits.Count ? Hits[Index] : null;
    public bool HasHits => Hits.Count > 0;
    public bool NoHits => !Searching && Hits.Count == 0 && Query.Trim().Length > 0;

    public string CountText => Searching ? "…"
        : Hits.Count == 0 ? (Query.Trim().Length > 0 ? L.Get("Ui_FindNone") : "")
        : L.F("Ui_FindCount", Index + 1, Hits.Count);

    /// <summary>Raised when the bar is opened (again): the window puts the cursor in the search box.</summary>
    public event Action? Opened;

    /// <summary>Raised when hits or the current one change (the canvas redraws; the window scrolls to the current hit).</summary>
    public event Action? Changed;

    private CancellationTokenSource? _pending;
    private string? _searchedPath;

    partial void OnQueryChanged(string value) => _ = SearchSoonAsync();
    partial void OnMatchCaseChanged(bool value) => _ = SearchSoonAsync(0);
    partial void OnWholeWordsChanged(bool value) => _ = SearchSoonAsync(0);

    [RelayCommand]
    private void Open()
    {
        if (!main.HasDocument) return;
        IsOpen = true;
        Opened?.Invoke();
        if (Query.Length > 0 && _searchedPath != main.PdfPath) _ = SearchSoonAsync(0);
        Changed?.Invoke();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        _pending?.Cancel();
        Changed?.Invoke();
    }

    /// <summary>A new or closed document: its hits are gone (the query stays, for the next one).</summary>
    public void Reset()
    {
        _pending?.Cancel();
        _searchedPath = null;
        Hits = [];
        Index = -1;
        Searching = false;
        OnPropertyChanged(nameof(Current));
        if (IsOpen && main.HasDocument && Query.Trim().Length > 0) _ = SearchSoonAsync(0);
        Changed?.Invoke();
    }

    /// <summary>Searches after a short pause in typing; a newer search cancels an older one.</summary>
    public async Task SearchSoonAsync(int delayMs = 250)
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        var query = Query.Trim();
        if (main.PdfPath is not { } path || query.Length == 0)
        {
            Hits = [];
            Index = -1;
            Searching = false;
            OnPropertyChanged(nameof(Current));
            Changed?.Invoke();
            return;
        }
        Searching = true;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
            bool matchCase = MatchCase, whole = WholeWords;
            var hits = await MainViewModel.Pdfium(() => PdfSearch.Find(path, query, matchCase, whole, cancel: cts.Token));
            if (cts.IsCancellationRequested || main.PdfPath != path) return;
            Hits = hits;
            _searchedPath = path;
            Searching = false;
            // start at the first hit on or after the page being read
            int first = 0;
            while (first < hits.Count && hits[first].Page < main.PageIndex) first++;
            Go(hits.Count == 0 ? -1 : first % hits.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is RedactException or IOException or PageAssemblyException)
        {
            if (cts.IsCancellationRequested) return;
            Hits = [];
            Index = -1;
            Searching = false;
            main.Status = Errors.Text(e);
            Changed?.Invoke();
        }
    }

    [RelayCommand]
    private void Next() { if (Hits.Count > 0) Go((Index + 1) % Hits.Count); }

    [RelayCommand]
    private void Previous() { if (Hits.Count > 0) Go((Index - 1 + Hits.Count) % Hits.Count); }

    private void Go(int index)
    {
        Index = index;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(HasHits));
        if (Current is { } hit && main.PageIndex != hit.Page) main.PageIndex = hit.Page;
        Changed?.Invoke();
    }

    /// <summary>The hits' boxes on one page; the current hit's boxes are flagged.</summary>
    public IEnumerable<(PdfRect Box, bool Current)> BoxesOn(int page)
    {
        if (!IsOpen) yield break;
        for (int i = 0; i < Hits.Count; i++)
            if (Hits[i].Page == page)
                foreach (var b in Hits[i].Boxes) yield return (b, i == Index);
    }
}
