// SPDX-License-Identifier: AGPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>
/// Finalize: flatten fields and comments into the page (<see cref="PdfFlattener"/>), check the open PDF against PDF/A-2b
/// and save a PDF/A copy (<see cref="PdfArchive"/>).
/// </summary>
public sealed partial class ArchiveToolViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private bool _flattenFields = true;
    [ObservableProperty] private bool _flattenComments = true;
    [ObservableProperty] private bool _flattenFirst;
    [ObservableProperty] private string _checkText = "";
    [ObservableProperty] private string _result = "";

    public bool CanRun => main.HasDocument;
    public bool CanFlatten => main.HasDocument && (FlattenFields || FlattenComments);

    partial void OnFlattenFieldsChanged(bool value) => OnPropertyChanged(nameof(CanFlatten));
    partial void OnFlattenCommentsChanged(bool value) => OnPropertyChanged(nameof(CanFlatten));

    public void Prepare()
    {
        Result = "";
        CheckText = "";
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanFlatten));
    }

    /// <summary>The kinds, one per line, as the command line prints them.</summary>
    public static string Lines(IEnumerable<ArchiveItem> items) =>
        string.Join("\n", items.Select(i =>
        {
            var name = i.Kind == "Flattened" ? L.Get("Ui_FlattenTitle") : L.Get("Pdfa_" + i.Kind);
            var details = i.Kind == "Fonts"
                ? i.Details.Select(d => d.Split(" → ") is [var a, var b] ? L.F("Ui_PdfaFontReplaced", a, b) : d)
                : i.Details;
            var list = string.Join(", ", details.Take(6)) + (i.Details.Count > 6 ? ", …" : "");
            return "• " + (list.Length > 0 ? $"{name} ({i.Count}): {list}" : $"{name} ({i.Count})");
        }));

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        IReadOnlyList<ArchiveItem>? items = null;
        await main.Run(async () =>
        {
            main.Status = L.Get("Ui_PdfaChecking");
            items = await MainViewModel.Pdfium(() => PdfArchive.Check(pdf));
        });
        if (items is null) return;
        CheckText = items.Count == 0 ? L.Get("Ui_PdfaOk") : L.Get("Ui_PdfaIssues") + "\n" + Lines(items);
        main.Status = items.Count == 0 ? L.Get("Ui_PdfaOk") : L.Get("Ui_PdfaIssues").TrimEnd(':', '：', ' ');
    }

    /// <summary>Writes &lt;name&gt;_flattened.pdf (the user picks the name) and opens it.</summary>
    [RelayCommand]
    private async Task FlattenAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_flattened.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        bool fields = FlattenFields, comments = FlattenComments;
        FlattenResult? r = null;
        await main.Run(async () =>
        {
            main.Status = L.Get("Ui_Flattening");
            r = await Replace(output, tmp => MainViewModel.Pdfium(() => PdfFlattener.Flatten(pdf, tmp, fields, comments)));
        });
        if (r is null) return;
        await main.LoadAsync(output);
        Result = L.F("Ui_Flattened", Path.GetFileName(output), r.Fields, r.Comments);
        main.Status = Result;
    }

    /// <summary>Writes &lt;name&gt;_pdfa.pdf (the user picks the name) and opens it.</summary>
    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_pdfa.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        bool flatten = FlattenFirst;
        ArchiveResult? r = null;
        await main.Run(async () =>
        {
            main.Status = L.Get("Ui_PdfaConverting");
            r = await Replace(output, tmp => MainViewModel.Pdfium(() => PdfArchive.Convert(pdf, tmp, flatten)));
        });
        if (r is null) return;
        await main.LoadAsync(output);
        var saved = L.F("Ui_PdfaSaved", Path.GetFileName(output));
        Result = r.Changes.Count == 0 ? saved : saved + "\n" + L.Get("Ui_PdfaChanges") + "\n" + Lines(r.Changes);
        CheckText = "";
        main.Status = saved;
    }

    /// <summary>The work writes a new file next to <paramref name="output"/>, which then takes its place (the dialog already asked).</summary>
    private static async Task<T> Replace<T>(string output, Func<string, Task<T>> work)
    {
        var tmp = output + ".tmp.pdf";
        if (File.Exists(tmp)) File.Delete(tmp);
        try
        {
            var result = await work(tmp);
            File.Move(tmp, output, overwrite: true);
            return result;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
