// SPDX-License-Identifier: AGPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>Compress: make a smaller copy of the open PDF at one of three levels (<see cref="PdfCompressor"/>).</summary>
public sealed partial class CompressToolViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private bool _light;
    [ObservableProperty] private bool _standard = true;
    [ObservableProperty] private bool _strong;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _result = "";

    public CompressLevel Level => Light ? CompressLevel.Light : Strong ? CompressLevel.Strong : CompressLevel.Standard;
    public bool CanCompress => main.HasDocument;

    public void Prepare()
    {
        Result = "";
        SizeText = main.PdfPath is { } p && File.Exists(p) ? L.F("Ui_CompressSize", Size(new FileInfo(p).Length)) : "";
        OnPropertyChanged(nameof(CanCompress));
    }

    public static string Size(long bytes) => CompressFormat.Size(bytes);

    /// <summary>Writes &lt;name&gt;_compressed.pdf (the user picks the name) and opens it.</summary>
    [RelayCommand]
    private async Task CompressAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_compressed.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        var level = Level;
        CompressReport? report = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                main.Status = L.Get("Ui_Compressing");
                report = await MainViewModel.Pdfium(() => PdfCompressor.Compress(pdf, tmp, level));
                File.Move(tmp, output, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (report is null) return;
        await main.LoadAsync(output);
        var text = L.F("Ui_Compressed", Path.GetFileName(output), Size(report.Before), Size(report.After), (int)Math.Round(report.Saved * 100));
        Result = text + "\n" + L.F("Ui_CompressDetails", report.Images, report.Downsampled, report.Duplicates, report.Streams)
                 + (report.Notes.Count > 0 ? "\n" + L.Get("Ui_CompressSigned") : "");
        SizeText = L.F("Ui_CompressSize", Size(report.After));
        main.Status = text;
    }
}
