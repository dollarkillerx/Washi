// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>A match in the Redact tool, with a box to tick (ticked: it will be redacted).</summary>
public sealed partial class RedactChoice(RedactMatch match, Action changed) : ObservableObject
{
    public RedactMatch Match { get; } = match;
    [ObservableProperty] private bool _redact = true;
    partial void OnRedactChanged(bool value) => changed();

    public string Where => L.F(Match.Source switch
    {
        RedactSource.Field => "Ui_RedactInField",
        RedactSource.Comment => "Ui_RedactInComment",
        _ => "Ui_RedactOnPage",
    }, Match.Page + 1);

    public string Context => Match.Context.Length > 60 ? Match.Context[..57] + "…" : Match.Context;
}

/// <summary>An area drawn on the page in the Redact tool (0-based page, page points).</summary>
public sealed record RedactBox(int Page, PdfRect Rect)
{
    public string Label => L.F("Ui_EraseBoxLabel", Page + 1, (int)Rect.Width, (int)Rect.Height);
}

/// <summary>
/// Redact: find words and kinds of personal data (or drag areas on the page), tick what goes, then save a copy where
/// it is removed from the file for real and covered in black (<see cref="PdfRedactor"/>).
/// </summary>
public sealed partial class RedactToolViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFind))] private string _words = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFind))] private bool _emails;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFind))] private bool _phones;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFind))] private bool _numbers;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _wholeWords;
    [ObservableProperty] private string _foundText = "";

    public ObservableCollection<RedactChoice> Matches { get; } = [];
    public ObservableCollection<RedactBox> Areas { get; } = [];

    /// <summary>Raised when matches, ticks or areas change (the canvas redraws).</summary>
    public event Action? Changed;

    private RedactSearch? _searched;

    public bool CanFind => main.HasDocument && !Search.IsEmpty;
    public bool HasMatches => Matches.Count > 0;
    public bool HasAreas => Areas.Count > 0;
    public int Chosen => Matches.Count(m => m.Redact) + Areas.Count;
    public bool CanRedact => main.HasDocument && Chosen > 0;
    public string RunText => L.F("Ui_RedactRun", Chosen);

    private RedactSearch Search
    {
        get
        {
            var patterns = new List<RedactPattern>();
            if (Emails) patterns.Add(RedactPattern.Email);
            if (Phones) patterns.Add(RedactPattern.Phone);
            if (Numbers) patterns.Add(RedactPattern.Number);
            var words = Words.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new RedactSearch(words, patterns, MatchCase: MatchCase, WholeWords: WholeWords);
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasMatches));
        OnPropertyChanged(nameof(HasAreas));
        OnPropertyChanged(nameof(Chosen));
        OnPropertyChanged(nameof(CanRedact));
        OnPropertyChanged(nameof(RunText));
        Changed?.Invoke();
    }

    public void Reset()
    {
        Matches.Clear();
        Areas.Clear();
        FoundText = "";
        _searched = null;
        Refresh();
    }

    [RelayCommand]
    private async Task FindAsync()
    {
        if (main.PdfPath is not { } path || !CanFind) return;
        var search = Search;
        try
        {
            var found = await MainViewModel.Pdfium(() => PdfRedactor.Find(path, search));
            if (main.PdfPath != path) return;
            Matches.Clear();
            foreach (var m in found) Matches.Add(new RedactChoice(m, Refresh));
            _searched = search;
            FoundText = found.Count == 0 ? L.Get("Ui_RedactNoneFound")
                : L.F("Ui_RedactFound", found.Count, found.Select(m => m.Page).Distinct().Count());
            if (found.Count > 0 && found.All(m => m.Page != main.PageIndex)) main.PageIndex = found[0].Page;
        }
        catch (Exception e) when (e is RedactException or PageAssemblyException or IOException)
        {
            FoundText = Errors.Text(e);
        }
        Refresh();
    }

    [RelayCommand]
    private void Show(RedactChoice? choice)
    {
        if (choice is not null) main.PageIndex = choice.Match.Page;
    }

    [RelayCommand]
    private void SelectAll(bool value)
    {
        foreach (var m in Matches) m.Redact = value;
    }

    public void AddArea(PdfRect rect)
    {
        Areas.Add(new RedactBox(main.PageIndex, rect));
        Refresh();
    }

    [RelayCommand]
    private void RemoveArea(RedactBox? box)
    {
        if (box is null) return;
        Areas.Remove(box);
        Refresh();
    }

    /// <summary>Saves &lt;name&gt;_redacted.pdf (the user picks the name) and opens it; the original is not changed.</summary>
    [RelayCommand]
    private async Task RedactAsync()
    {
        if (main.PdfPath is not { } pdf || !CanRedact) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_redacted.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf))
        {
            main.Status = L.Get("Ui_RedactNotOriginal");
            return;
        }
        var areas = PdfRedactor.Areas(Matches.Where(m => m.Redact).Select(m => m.Match))
                               .Concat(Areas.Select(a => new RedactArea(a.Page, a.Rect))).ToList();
        // what is masked outside the pages and searched for again: all of the search when every match goes, otherwise
        // only the texts of the ticked matches (an unticked one stays everywhere)
        var ticked = Matches.Where(m => m.Redact).ToList();
        var search = ticked.Count == 0 || _searched is null ? null
            : ticked.Count == Matches.Count ? _searched
            : new RedactSearch([.. ticked.Select(m => m.Match.Text).Distinct()], MatchCase: _searched.MatchCase, WholeWords: _searched.WholeWords);
        RedactReport? report = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                report = await MainViewModel.Pdfium(() => PdfRedactor.Redact(pdf, tmp, areas, search));
                File.Move(tmp, output, overwrite: true);
            }
            catch (RedactException e)
            {
                main.Status = L.F("Ui_RedactFailed", Errors.Text(e));
                report = null;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (report is null) return;
        PdfPasswords.Forget(output);
        var page = main.PageIndex;
        await main.LoadAsync(output);
        if (page < main.PageCount) main.PageIndex = page;
        main.Status = L.F("Ui_Redacted", Path.GetFileName(output), report.Glyphs, report.Images, report.Paths, report.Annotations + report.Strings);
    }
}
