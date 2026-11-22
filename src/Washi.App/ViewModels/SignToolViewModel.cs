// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Core.Pages;
using Washi.Masume;

namespace Washi.App.ViewModels;

/// <summary>A signature placed on the page (0-based page, upright page points).</summary>
public sealed record PlacedSignature(int Page, PdfRect Rect)
{
    public string Label => L.F("Ui_SignPlaced", Page + 1);
}

/// <summary>A signature field to sign into: one the form tool found or drew, or one already in the PDF.</summary>
public sealed record SignatureSpot(int Page, PdfRect Rect, string Name, bool InFile = false)
{
    public string Label => L.F("Ui_SignSpot", Name, Page + 1);
}

/// <summary>
/// Fill &amp; Sign: draw a signature once (it is kept on this computer if wanted), drag boxes on the page or pick a
/// signature field to place it, then write a copy with the signature drawn into the page (<see cref="PdfSigner"/>).
/// </summary>
public sealed partial class SignToolViewModel(MainViewModel main) : ObservableObject
{
    /// <summary>Where a remembered signature is kept; tests point it elsewhere.</summary>
    public string StorePath { get; set; } = Path.Combine(
        Path.GetDirectoryName(Washi.App.Services.AppSettings.Path)!, "signature.txt");

    /// <summary>The signature to place: drawn on the pad, or the remembered one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInk), nameof(CanSave))] private Ink? _ink;

    /// <summary>Keep the signature on this computer for next time (a file next to the settings, never sent anywhere).</summary>
    [ObservableProperty] private bool _remember = true;

    public ObservableCollection<PlacedSignature> Placed { get; } = [];
    public ObservableCollection<SignatureSpot> Spots { get; } = [];

    public bool HasInk => Ink is { IsEmpty: false };
    public bool HasPlaced => Placed.Count > 0;
    public bool CanSave => HasInk && Placed.Count > 0 && main.HasDocument;

    /// <summary>Redraws the page when placements change.</summary>
    public event Action? Changed;

    private bool _loaded;

    /// <summary>When the tool opens: the remembered signature, and the document's signature fields.</summary>
    public async Task PrepareAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            try { if (File.Exists(StorePath)) Ink = Ink.Parse(await File.ReadAllTextAsync(StorePath)); }
            catch (IOException) { }
        }
        Spots.Clear();
        foreach (var f in main.Fields.Where(f => f.Kind == FieldKind.Signature))
            Spots.Add(new SignatureSpot(f.Page, f.Rect, f.Label.Length > 0 ? f.Label : f.Name));
        if (main.PdfPath is { } pdf)
        {
            var existing = await Task.Run(() =>
            {
                try { return Washi.Core.Forms.AcroFormFields.List(pdf).Where(f => f.Type == "signature" && f.Page >= 0).ToList(); }
                catch (Exception e) when (e is IOException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException) { return []; }
            });
            foreach (var f in existing.Where(f => !Spots.Any(s => s.Page == f.Page && Near(s.Rect, f.Rect))))
                Spots.Add(new SignatureSpot(f.Page, f.Rect, f.Name, InFile: true));
        }
        await Digital.PrepareAsync();
        // a field that already holds a digital signature is not a place to draw (redrawing the page would break it)
        var signed = Digital.SignedFields;
        foreach (var spot in Spots.Where(s => s.InFile && signed.Contains(s.Name)).ToList()) Spots.Remove(spot);
        Digital.RefreshPlaces();
    }

    /// <summary>Signing with a digital ID, and the signatures the file already has.</summary>
    public DigitalSignViewModel Digital { get; } = new(main);

    private static bool Near(PdfRect a, PdfRect b) => Math.Abs(a.X0 - b.X0) < 2 && Math.Abs(a.Y0 - b.Y0) < 2;

    /// <summary>The pad finished a stroke: this is now the signature (and is remembered if wanted).</summary>
    public void SetInk(Ink? ink)
    {
        Ink = ink is { IsEmpty: false } ? ink : null;
        if (Remember) Store();
    }

    partial void OnRememberChanged(bool value)
    {
        if (value) Store();
        else Forget();
    }

    private void Store()
    {
        try
        {
            if (Ink is null) { Forget(); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, Ink.Serialize());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Forget()
    {
        try { if (File.Exists(StorePath)) File.Delete(StorePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [RelayCommand]
    private void ClearInk()
    {
        Ink = null;
        if (Remember) Forget();
    }

    /// <summary>A box dragged on the page.</summary>
    public void Place(PdfRect rect)
    {
        if (!HasInk) { main.Status = L.Get("Ui_SignDrawFirst"); return; }
        Placed.Add(new PlacedSignature(main.PageIndex, rect));
        Refresh();
    }

    [RelayCommand]
    private void SignSpot(SignatureSpot? spot)
    {
        if (spot is null) return;
        if (!HasInk) { main.Status = L.Get("Ui_SignDrawFirst"); return; }
        Placed.Add(new PlacedSignature(spot.Page, spot.Rect));
        main.PageIndex = spot.Page;
        Refresh();
    }

    [RelayCommand]
    private void RemovePlaced(PlacedSignature? p)
    {
        if (p is null) return;
        Placed.Remove(p);
        Refresh();
    }

    public void Reset()
    {
        Placed.Clear();
        Spots.Clear();
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasPlaced));
        OnPropertyChanged(nameof(CanSave));
        Digital.RefreshPlaces();
        Changed?.Invoke();
    }

    /// <summary>Writes &lt;name&gt;_signed.pdf with every placed signature and opens it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave || main.PdfPath is not { } pdf || Ink is not { } ink) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_signed.pdf") is not { } output) return;
        var stamps = Placed.Select(p => new SignatureStamp(p.Page, p.Rect, ink)).ToList();
        string? written = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            try
            {
                int n = await Task.Run(() => PdfSigner.Sign(pdf, tmp, stamps));
                File.Move(tmp, output, overwrite: true);
                written = output;
                main.Status = L.F("Ui_Signed", Path.GetFileName(output), n);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (written is not null)
        {
            var status = main.Status;
            Placed.Clear();
            Refresh();
            await main.LoadAsync(written);
            main.Status = status;
        }
    }
}
