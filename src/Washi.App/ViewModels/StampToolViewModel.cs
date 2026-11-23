// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.App.ViewModels;

/// <summary>
/// Headers, footers, page numbers, Bates numbers and a watermark (<see cref="PdfStamper"/>): typed in the panel,
/// previewed on the page, saved to a copy. The original is not changed.
/// </summary>
public sealed partial class StampToolViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public StampToolViewModel(MainViewModel main)
    {
        _main = main;
        PropertyChanged += OnChanged;
    }

    [ObservableProperty] private string _headerLeft = "";
    [ObservableProperty] private string _headerCenter = "";
    [ObservableProperty] private string _headerRight = "";
    [ObservableProperty] private string _footerLeft = "";
    [ObservableProperty] private string _footerCenter = "";
    [ObservableProperty] private string _footerRight = "";
    [ObservableProperty] private decimal? _fontSize = 9;
    [ObservableProperty] private string _pages = "";
    [ObservableProperty] private string _batesPrefix = "";
    [ObservableProperty] private decimal? _batesStart = 1;
    [ObservableProperty] private decimal? _batesDigits = 6;
    [ObservableProperty] private string _watermarkText = "";
    [ObservableProperty] private double _watermarkOpacity = 30;   // percent
    [ObservableProperty] private decimal? _watermarkAngle = 45;
    [ObservableProperty] private bool _watermarkBehind;
    /// <summary>0 grey, 1 red, 2 blue.</summary>
    [ObservableProperty] private int _watermarkColorIndex;

    /// <summary>Raised when anything shown on the page changes (the canvas redraws its preview).</summary>
    public event Action? Changed;

    public static readonly int[] Colors = [0x808080, 0xCC0000, 0x1F49E0];
    public int WatermarkColor => Colors[Math.Clamp(WatermarkColorIndex, 0, Colors.Length - 1)];

    public bool UsesBates => new[] { HeaderLeft, HeaderCenter, HeaderRight, FooterLeft, FooterCenter, FooterRight }
        .Any(t => t.Contains("{bates}", StringComparison.OrdinalIgnoreCase));
    public bool CanSave => _main.HasDocument && !Options().IsEmpty;
    public string OpacityText => $"{WatermarkOpacity:0}%";

    private void OnChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CanSave) or nameof(UsesBates) or nameof(OpacityText)) return;
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(UsesBates));
        OnPropertyChanged(nameof(OpacityText));
        Changed?.Invoke();
    }

    public void Reset() => Changed?.Invoke();

    /// <summary>What the panel says, as the stamper takes it.</summary>
    public StampOptions Options() => new()
    {
        HeaderLeft = HeaderLeft, HeaderCenter = HeaderCenter, HeaderRight = HeaderRight,
        FooterLeft = FooterLeft, FooterCenter = FooterCenter, FooterRight = FooterRight,
        FontSize = (double)(FontSize ?? 9),
        Pages = Pages.Trim().Length == 0 ? null : Pages.Trim(),
        Bates = UsesBates ? new BatesNumbers(BatesPrefix, (int)(BatesStart ?? 1), (int)(BatesDigits ?? 6)) : null,
        Watermark = WatermarkText.Trim().Length == 0 ? null
            : new Watermark(WatermarkText.Trim(), 0, (double)(WatermarkAngle ?? 45), WatermarkOpacity / 100, WatermarkColor, WatermarkBehind),
    };

    /// <summary>Whether a page (0-based) gets the marks; a range that cannot be read marks none.</summary>
    public bool Marks(int page)
    {
        if (Pages.Trim().Length == 0) return true;
        try { return PageAssembly.ParseRanges(Pages, _main.PageCount).Any(r => page + 1 >= r.From && page + 1 <= r.To); }
        catch (PageAssemblyException) { return false; }
    }

    /// <summary>The Bates number a page (0-based) gets: they count the marked pages.</summary>
    public string? BatesFor(int page)
    {
        if (!UsesBates) return null;
        int before = Enumerable.Range(0, page).Count(Marks);
        return new BatesNumbers(BatesPrefix, (int)(BatesStart ?? 1), (int)(BatesDigits ?? 6)).Format((int)(BatesStart ?? 1) + before);
    }

    [RelayCommand]
    private void PageNumbers()
    {
        FooterCenter = "{page} / {pages}";
    }

    [RelayCommand]
    private void Bates()
    {
        FooterRight = "{bates}";
    }

    /// <summary>Saves &lt;name&gt;_stamped.pdf (the user picks the name) and opens it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_main.PdfPath is not { } pdf || !CanSave) return;
        if (await _main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_stamped.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { _main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        var options = Options() with { Date = DateTime.Now };
        StampResult? result = null;
        await _main.Run(async () =>
        {
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                result = await MainViewModel.Pdfium(() => PdfStamper.Apply(pdf, tmp, options));
                File.Move(tmp, output, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (result is null) return;
        var page = _main.PageIndex;
        await _main.LoadAsync(output);
        if (page < _main.PageCount) _main.PageIndex = page;
        _main.Status = result.FirstBates is { } first
            ? L.F("Ui_StampedBates", Path.GetFileName(output), result.Pages, first, result.LastBates!)
            : L.F("Ui_Stamped", Path.GetFileName(output), result.Pages);
    }
}
