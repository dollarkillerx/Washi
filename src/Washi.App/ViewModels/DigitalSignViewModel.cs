// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Core.Pages;
using Washi.Core.Signatures;

namespace Washi.App.ViewModels;

/// <summary>A signature found in the open PDF, as the panel shows it.</summary>
public sealed record SignatureRow(SignatureCheck Check)
{
    public string Mark => Check.Status switch { "valid" => "✓", "invalid" => "✗", _ => "!" };
    public string Title => L.F("Ui_SigRow", Check.Signer.Length > 0 ? Check.Signer : Check.Field,
                               Check.SignedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture) ?? "?");
    public string StatusText => L.Get("Ui_SigStatus_" + Check.Status)
                                + (Check.Timestamped && Check.Kind != "timestamp" ? " · " + L.Get("Ui_SigTimestamped") : "")
                                + (Check.Ltv && Check.Trusted ? " · " + L.Get("Ui_SigLtv") : "");
    public string Details => string.Join("\n", new[]
        {
            Check.Kind == "timestamp" ? L.Get("Ui_SigDocTimestamp") : "",
            Check.Kind == "certification" ? L.F("Ui_SigCertified", L.Get("Ui_SigPerm_" + Check.Permissions)) : "",
            Check.Reason.Length > 0 ? L.F("Sig_Reason", Check.Reason) : "",
            Check.Location.Length > 0 ? L.F("Sig_Location", Check.Location) : "",
            Check.LaterChanges.Count > 0 ? L.F("Ui_SigChanges", string.Join(", ", Check.LaterChanges.Select(k => L.Get("Ui_SigChange_" + k)))) : "",
        }.Where(s => s.Length > 0).Concat(Check.Problems));
    public bool IsValid => Check.Status == "valid";
    public bool IsInvalid => Check.Status == "invalid";
}

/// <summary>Where a digital signature goes.</summary>
public sealed record SignPlace(string Kind, string Text, SignatureSpot? Spot = null, PlacedSignature? Box = null);

/// <summary>
/// The Sign tool's digital signature: check the signatures a PDF has, and sign with a digital ID (from this computer's
/// certificate store, a .p12 file, or one made here) as a PAdES signature added after everything already in the file
/// (<see cref="PdfDigitalSigner"/>, <see cref="PdfSignatureVerifier"/>).
/// </summary>
public sealed partial class DigitalSignViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<SignatureRow> Signatures { get; } = [];
    public ObservableCollection<DigitalId> Ids { get; } = [];
    public ObservableCollection<SignPlace> Places { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSign))] private DigitalId? _id;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSign))] private SignPlace? _place;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _timestampServer = "";
    [ObservableProperty] private bool _includeInk = true;

    /// <summary>0: an ordinary signature; 1-3: certify, allowing no changes / form filling and signing / also comments.</summary>
    [ObservableProperty] private int _certifyLevel;
    [ObservableProperty] private bool _addLtv;

    // opening a .p12
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIdFile))] private string? _idFile;
    [ObservableProperty] private string _idPassword = "";
    public bool HasIdFile => IdFile is not null;

    // making a digital ID
    [ObservableProperty] private bool _creating;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newEmail = "";
    [ObservableProperty] private string _newPassword = "";

    public bool HasSignatures => Signatures.Count > 0;
    public bool NoIds => Ids.Count == 0;
    public bool CanSign => main.HasDocument && Id is not null && Place is not null;

    private bool _storeRead;
    private int _prepared;

    /// <summary>The names of the fields that already hold a digital signature.</summary>
    public IReadOnlySet<string> SignedFields => Signatures.Select(s => s.Check.Field).ToHashSet(StringComparer.Ordinal);

    /// <summary>The digital IDs of this computer's certificate store, read once.</summary>
    public async Task EnsureIdsAsync()
    {
        if (_storeRead) return;
        _storeRead = true;
        foreach (var id in await Task.Run(DigitalIds.FromStore)) Ids.Add(id);
        Id ??= Ids.FirstOrDefault();
        OnPropertyChanged(nameof(NoIds));
    }

    /// <summary>When the Sign tool opens or a document loads: the file's signatures, the places to sign, the IDs.</summary>
    public async Task PrepareAsync()
    {
        await EnsureIdsAsync();
        int version = ++_prepared;
        IReadOnlyList<SignatureCheck> found = [];
        if (main.PdfPath is { } pdf)
        {
            found = await Task.Run(() =>
            {
                try { return PdfSignatureVerifier.Verify(pdf); }
                catch (Exception e) when (e is IOException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException) { return []; }
            });
            if (main.PdfPath != pdf) return;
        }
        if (version != _prepared) return;   // a newer call won
        Signatures.Clear();
        foreach (var s in found) Signatures.Add(new SignatureRow(s));
        OnPropertyChanged(nameof(HasSignatures));
        RefreshPlaces();
    }

    /// <summary>Invisible, each empty signature field of the file, and the last box drawn on the page.</summary>
    public void RefreshPlaces()
    {
        var keep = Place;
        Places.Clear();
        Places.Add(new SignPlace("invisible", L.Get("Ui_SigInvisible")));
        var signed = Signatures.Select(s => s.Check.Field).ToHashSet(StringComparer.Ordinal);
        foreach (var spot in main.Sign.Spots.Where(s => s.InFile && !signed.Contains(s.Name)))
            Places.Add(new SignPlace("field", L.F("Ui_SigInField", spot.Name, spot.Page + 1), Spot: spot));
        var box = main.Sign.Placed.LastOrDefault();
        if (box is not null) Places.Add(new SignPlace("box", L.F("Ui_SigInBox", box.Page + 1), Box: box));
        // a box just drawn is where the user means to sign; otherwise the choice stays
        bool newBox = box is not null && box != _lastBox;
        _lastBox = box;
        Place = newBox ? Places[^1]
            : Places.FirstOrDefault(p => p.Kind == keep?.Kind && p.Spot == keep?.Spot && p.Box == keep?.Box)
              ?? Places.LastOrDefault(p => p.Kind != "invisible") ?? Places[0];
    }

    private PlacedSignature? _lastBox;

    [RelayCommand]
    private async Task OpenIdFileAsync()
    {
        if (await main.Dialogs.OpenDigitalIdAsync() is { } path) { IdFile = path; IdPassword = ""; }
    }

    [RelayCommand]
    private void UnlockIdFile()
    {
        if (IdFile is not { } path) return;
        try
        {
            var id = DigitalIds.FromFile(path, IdPassword);
            Ids.Add(id);
            Id = id;
            IdFile = null;
            IdPassword = "";
            OnPropertyChanged(nameof(NoIds));
        }
        catch (PageAssemblyException e) { main.Status = Errors.Text(e); }
    }

    [RelayCommand]
    private async Task CreateIdAsync()
    {
        if (NewName.Trim().Length == 0) { main.Status = L.Get("Err_IdName"); return; }
        if (NewPassword.Length < 6) { main.Status = L.Get("Err_IdPassword"); return; }
        if (await main.Dialogs.SaveDigitalIdAsync(NewName.Trim() + ".p12") is not { } path) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);   // the save dialog already asked about replacing it
            var id = await Task.Run(() => DigitalIds.Create(NewName, NewEmail, path, NewPassword));
            Ids.Add(id);
            Id = id;
            Creating = false;
            NewPassword = "";
            OnPropertyChanged(nameof(NoIds));
            main.Status = L.F("Ui_IdCreated", Path.GetFileName(path));
        }
        catch (PageAssemblyException e) { main.Status = Errors.Text(e); }
    }

    /// <summary>Signs into &lt;name&gt;_signed.pdf (the user picks the name) and opens it.</summary>
    [RelayCommand]
    private async Task SignAsync()
    {
        if (main.PdfPath is not { } pdf || Id is not { } id || Place is not { } place) return;
        Uri? tsa = null;
        if (TimestampServer.Trim().Length > 0 && !Uri.TryCreate(TimestampServer.Trim(), UriKind.Absolute, out tsa))
        {
            main.Status = L.Get("Ui_SigBadTimestamp");
            return;
        }
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_signed.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        var request = new SignRequest(id, place.Spot?.Name, place.Spot?.Page ?? place.Box?.Page ?? main.PageIndex,
                                      place.Box?.Rect, Reason, Location, IncludeInk && place.Kind != "invisible" ? main.Sign.Ink : null, tsa,
                                      CertifyLevel, AddLtv);
        SignResult? result = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                result = await Task.Run(() => PdfDigitalSigner.Sign(pdf, tmp, request));
                File.Move(tmp, output, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (result is null) return;
        if (place.Box is { } used) main.Sign.Placed.Remove(used);   // this box became the digital signature
        await main.LoadAsync(output);
        main.Status = L.F("Ui_SigSigned", Path.GetFileName(output), result.Signer)
                      + (!AddLtv ? "" : " " + (result.Ltv ? L.Get("Ui_SigLtvDone") : L.F("Ui_SigLtvMissing", result.Field)));
    }

    /// <summary>Adds long-term validation data (and, with a time-stamping service, a document time-stamp) to the
    /// signatures in the file, into &lt;name&gt;_ltv.pdf (the user picks the name), and opens it.</summary>
    [RelayCommand]
    private async Task AddLtvAsync()
    {
        if (main.PdfPath is not { } pdf || !HasSignatures) return;
        Uri? tsa = null;
        if (TimestampServer.Trim().Length > 0 && !Uri.TryCreate(TimestampServer.Trim(), UriKind.Absolute, out tsa))
        {
            main.Status = L.Get("Ui_SigBadTimestamp");
            return;
        }
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_ltv.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(pdf)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        LtvResult? result = null;
        await main.Run(async () =>
        {
            main.Status = L.Get("Ui_SigAddingLtv");
            var tmp = output + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            try
            {
                result = await Task.Run(() => PdfLtv.AddValidation(pdf, tmp, tsa));
                File.Move(tmp, output, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (result is null) return;
        await main.LoadAsync(output);
        main.Status = L.F("Ui_SigLtvAdded", Path.GetFileName(output), result.Certificates, result.Ocsps, result.Crls)
                      + (result.Timestamped ? " " + L.Get("Ui_SigLtvStamped") : "")
                      + (result.NotLtv.Count > 0 ? " " + L.F("Ui_SigLtvMissing", string.Join(", ", result.NotLtv)) : "");
    }
}
