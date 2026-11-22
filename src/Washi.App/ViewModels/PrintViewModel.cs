// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core.Localization;
using Washi.Core.Pages;
using Washi.Core.Printing;

namespace Washi.App.ViewModels;

/// <summary>
/// The print card (⌘/Ctrl+P): printer, copies, which pages, two-sided, black and white, fit to the paper. It prints the
/// file as it is saved on disk (CUPS on macOS and Linux, the Windows print system on Windows).
/// </summary>
public sealed partial class PrintViewModel(MainViewModel main) : ObservableObject
{
    /// <summary>The print system (a fake one in tests).</summary>
    internal IPrinting Service { get; set; } = PdfPrinting.Default;

    [ObservableProperty] private bool _isOpen;
    public ObservableCollection<PrinterInfo> Printers { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPrint))] private PrinterInfo? _printer;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPrint))] private decimal? _copies = 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPrint))] private bool _allPages = true;
    [ObservableProperty] private bool _currentPage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPrint))] private bool _somePages;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPrint))] private string _range = "";
    [ObservableProperty] private bool _twoSided;
    [ObservableProperty] private bool _grayscale;
    [ObservableProperty] private bool _fitToPage = true;
    [ObservableProperty] private bool _actualSize;
    [ObservableProperty] private string _error = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPrint))] private bool _busy;

    public bool NoPrinters => Printers.Count == 0;
    public string Note => main.PdfPath is { } p ? L.F("Ui_PrintNote", Path.GetFileName(p)) : "";
    public string CurrentPageText => L.F("Ui_PrintCurrentPage", main.PageIndex + 1);

    public bool CanPrint => !Busy && Printer is not null && Copies is >= 1 and <= 999 && (!SomePages || Range.Trim().Length > 0);

    partial void OnFitToPageChanged(bool value) { if (value) ActualSize = false; }
    partial void OnActualSizeChanged(bool value) { if (value) FitToPage = false; }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (!main.HasDocument) return;
        Error = "";
        IReadOnlyList<PrinterInfo> found = [];
        try { found = await Task.Run(Service.Printers); }
        catch (PrintException e) { Error = Errors.Text(e); }
        Printers.Clear();
        foreach (var p in found) Printers.Add(p);
        Printer = Printers.FirstOrDefault(p => p.Name == Printer?.Name) ?? Printers.FirstOrDefault(p => p.IsDefault) ?? Printers.FirstOrDefault();
        if (Printers.Count == 0 && Error.Length == 0) Error = L.Get("Ui_PrintNoPrinters");
        OnPropertyChanged(nameof(NoPrinters));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(CurrentPageText));
        IsOpen = true;
    }

    [RelayCommand]
    private void Cancel() => IsOpen = false;

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (main.PdfPath is not { } pdf || !CanPrint) return;
        var options = new PrintOptions(Printer!.Name, (int)Copies!.Value,
            CurrentPage ? (main.PageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : SomePages ? Range.Trim() : null,
            TwoSided, Grayscale, FitToPage);
        Error = "";
        Busy = true;
        try
        {
            // Windows renders the pages with PDFium, so the job holds its lock
            var result = await MainViewModel.Pdfium(() => Service.Print(pdf, options));
            IsOpen = false;
            main.Status = L.F("Ui_Printed", result.Pages, Path.GetFileName(pdf), result.Printer);
        }
        catch (Exception e) when (e is PrintException or PageAssemblyException or IOException or InvalidOperationException)
        {
            Error = Errors.Text(e);
        }
        finally { Busy = false; }
    }
}
