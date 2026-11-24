// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Security.Cryptography.X509Certificates;
using Washi.App.Services;
using Washi.Core.Localization;
using Washi.Core.Pages;
using Washi.Core.Pdfium;

namespace Washi.App.ViewModels;

/// <summary>A kind of hidden information found in the document, with a box to tick.</summary>
public sealed partial class HiddenChoice(HiddenItem item) : ObservableObject
{
    public HiddenItem Item { get; } = item;
    /// <summary>Earlier versions always go (the file is written anew): shown ticked, not changeable.</summary>
    public bool CanChoose => Item.Kind != HiddenInfo.EarlierVersions;
    [ObservableProperty] private bool _remove = true;

    public string Text => L.F("Ui_Hidden" + Item.Kind, Item.Count);
    public string Details => Item.Details.Count == 0 ? "" : string.Join(", ", Item.Details.Take(6)) + (Item.Details.Count > 6 ? ", …" : "");
    public bool HasDetails => Details.Length > 0;
}

/// <summary>
/// Protect: open a file that needs a password, add an open password and/or restrictions (AES-256), remove protection
/// with the password that allows it, and remove hidden information (<see cref="PdfSecurity"/>, <see cref="PdfCleaner"/>).
/// Each writes a copy and opens it. Passwords stay in memory for this session only.
/// </summary>
public sealed partial class ProtectToolViewModel(MainViewModel main) : ObservableObject
{
    // ------------------------------------------------------------------ a file that needs a password to open
    /// <summary>The file waiting for its password (it could not be opened without one).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLocked), nameof(LockedText))] private string? _lockedPath;
    [ObservableProperty] private string _unlockPassword = "";

    public bool IsLocked => LockedPath is not null;
    public string LockedText => LockedPath is null ? "" : L.F("Ui_ProtectLocked", Path.GetFileName(LockedPath));

    /// <summary>Called when a file cannot be opened without a password.</summary>
    public void AskPassword(string path)
    {
        LockedPath = path;
        UnlockPassword = "";
        main.Status = L.F("Ui_ProtectLocked", Path.GetFileName(path));
    }

    [RelayCommand]
    private async Task OpenLockedAsync()
    {
        if (LockedPath is not { } path || UnlockPassword.Length == 0) return;
        var pw = UnlockPassword;
        var p = await Task.Run(() => PdfSecurity.Inspect(path, pw));
        if (p.NeedsPassword) { main.Status = L.Get("Ui_ProtectWrongPassword"); return; }
        PdfPasswords.Remember(path, pw);
        LockedPath = null;
        UnlockPassword = "";
        await main.LoadAsync(path);
    }

    // ------------------------------------------------------------------ a file encrypted for certificates
    /// <summary>The file waiting for a digital ID (it is encrypted for certificates).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCertLocked), nameof(CertLockedText))] private string? _certLockedPath;
    public ObservableCollection<string> CertRecipients { get; } = [];

    public bool IsCertLocked => CertLockedPath is not null;
    public string CertLockedText => CertLockedPath is null ? "" : L.F("Ui_CertLocked", Path.GetFileName(CertLockedPath));

    /// <summary>Called when a file is encrypted for certificates: it opens with a digital ID, into a copy.</summary>
    public async Task AskCertificateAsync(string path)
    {
        CertLockedPath = path;
        CertRecipients.Clear();
        foreach (var r in await Task.Run(() => PdfCertificateSecurity.RecipientsOf(path))) CertRecipients.Add(L.F("Ui_CertRecipient", r.Issuer, r.Serial));
        await main.Sign.Digital.EnsureIdsAsync();
        main.Status = CertLockedText;
    }

    /// <summary>Writes a copy without the protection, opened with the chosen digital ID, and opens it.</summary>
    [RelayCommand]
    private async Task OpenWithIdAsync()
    {
        if (CertLockedPath is not { } path || main.Sign.Digital.Id is not { } id) { main.Status = L.Get("Ui_CertChooseId"); return; }
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(path) + "_opened.pdf") is not { } output) return;
        if (Path.GetFullPath(output) == Path.GetFullPath(path)) { main.Status = L.Get("Ui_BookmarksNotOriginal"); return; }
        if (await Write(output, tmp => PdfCertificateSecurity.Decrypt(path, tmp, id), L.F("Ui_CertOpened", Path.GetFileName(output), id.Name)))
            CertLockedPath = null;
    }

    // ------------------------------------------------------------------ encrypting for certificates
    public ObservableCollection<X509Certificate2> Recipients { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEncryptForCertificates))] private bool _includeMe = true;
    public bool HasRecipients => Recipients.Count > 0;
    public bool CanEncryptForCertificates => CanAdd && (Recipients.Count > 0 || IncludeMe && main.Sign.Digital.Id is not null);

    [RelayCommand]
    private async Task AddRecipientAsync()
    {
        if (await main.Dialogs.OpenCertificateAsync() is not { } file) return;
        try
        {
            var cert = X509CertificateLoader.LoadCertificateFromFile(file);
            if (!Recipients.Any(r => r.Thumbprint == cert.Thumbprint)) Recipients.Add(cert);
        }
        catch (System.Security.Cryptography.CryptographicException) { main.Status = L.F("Ui_CertUnreadable", Path.GetFileName(file)); }
        OnPropertyChanged(nameof(HasRecipients));
        OnPropertyChanged(nameof(CanEncryptForCertificates));
    }

    [RelayCommand]
    private void RemoveRecipient(X509Certificate2 cert)
    {
        Recipients.Remove(cert);
        OnPropertyChanged(nameof(HasRecipients));
        OnPropertyChanged(nameof(CanEncryptForCertificates));
    }

    [RelayCommand]
    private async Task EncryptForCertificatesAsync()
    {
        if (main.PdfPath is not { } pdf) return;
        var list = Recipients.ToList();
        if (IncludeMe && main.Sign.Digital.Id is { } me && !list.Any(r => r.Thumbprint == me.Certificate.Thumbprint))
            list.Add(X509CertificateLoader.LoadCertificate(me.Certificate.RawData));
        if (list.Count == 0) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_sealed.pdf") is not { } output) return;
        var current = CurrentPassword.Length > 0 ? CurrentPassword : PdfPasswords.For(pdf);
        await Write(output, tmp => PdfCertificateSecurity.Encrypt(pdf, tmp, list, current), null);
        main.Status = L.F("Ui_CertSealed", Path.GetFileName(output), list.Count);
    }

    // ------------------------------------------------------------------ the open document's protection
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEncrypted), nameof(StateText), nameof(PermissionsText), nameof(CanAdd),
                                                 nameof(CanRemoveProtection), nameof(CanClean), nameof(CleanBlockedText), nameof(CanProtect), nameof(NothingHidden))]
    private PdfProtection? _current;

    public bool IsEncrypted => Current?.Encrypted == true;

    /// <summary>The open document asks for a password when it is opened.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))] private bool _needsOpenPassword;

    public string StateText => Current switch
    {
        null => "",
        { Encrypted: false } => L.Get("Ui_ProtectNone"),
        var c => L.F(NeedsOpenPassword ? "Ui_ProtectStateOpen" : "Ui_ProtectStateRestricted", c.Method),
    };

    public string PermissionsText => Current is { Encrypted: true, Permissions: var p } && p.RestrictsAnything
        ? L.F("Ui_ProtectNotAllowed", string.Join(", ", new[]
          {
              (p.Print, "Ui_PermPrint"), (p.Copy, "Ui_PermCopy"), (p.Edit, "Ui_PermEdit"), (p.Comment, "Ui_PermComment"),
              (p.FillForms, "Ui_PermFill"), (p.Assemble, "Ui_PermAssemble"),
          }.Where(x => !x.Item1).Select(x => L.Get(x.Item2))))
        : "";

    /// <summary>The permissions password, to remove or change protection (prefilled when the open password allows it).</summary>
    [ObservableProperty] private string _currentPassword = "";

    public bool CanRemoveProtection => IsEncrypted && main.HasDocument;

    /// <summary>When the tool opens or a document loads.</summary>
    public async Task PrepareAsync()
    {
        await main.Sign.Digital.EnsureIdsAsync();
        OnPropertyChanged(nameof(CanEncryptForCertificates));
        if (main.PdfPath is not { } path) { Current = null; Hidden.Clear(); return; }
        var known = PdfPasswords.For(path);
        var (protection, needsOpen, hidden) = await Task.Run(() =>
        {
            var p = PdfSecurity.Inspect(path, known);
            bool open = p.Encrypted && PdfSecurity.Inspect(path).NeedsPassword;
            IReadOnlyList<HiddenItem> h = [];
            if (!p.Encrypted)
                try { h = PdfCleaner.Inspect(path); }
                catch (Exception e) when (e is PageAssemblyException or IOException or PdfSharp.Pdf.IO.PdfReaderException) { }
            return (p, open, h);
        });
        if (main.PdfPath != path) return;
        NeedsOpenPassword = needsOpen;
        Current = protection;
        CurrentPassword = protection.FullAccess && known is not null ? known : "";
        Hidden.Clear();
        foreach (var h in hidden) Hidden.Add(new HiddenChoice(h));
        OnPropertyChanged(nameof(NothingHidden));
    }

    public void Reset()
    {
        Current = null;
        CurrentPassword = "";
        Hidden.Clear();
        OpenPassword = PermissionsPassword = "";
        OnPropertyChanged(nameof(NothingHidden));
    }

    [RelayCommand]
    private async Task RemoveProtectionAsync()
    {
        if (main.PdfPath is not { } pdf || !IsEncrypted) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_unlocked.pdf") is not { } output) return;
        var pw = CurrentPassword.Length > 0 ? CurrentPassword : PdfPasswords.For(pdf);
        await Write(output, tmp => PdfSecurity.Unlock(pdf, tmp, pw), L.F("Ui_ProtectRemoved", Path.GetFileName(output)));
    }

    // ------------------------------------------------------------------ adding protection
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanProtect))] private string _openPassword = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanProtect))] private bool _restrict;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanProtect))] private string _permissionsPassword = "";
    [ObservableProperty] private bool _showPasswords;
    [ObservableProperty] private bool _allowPrint = true;
    [ObservableProperty] private bool _allowCopy;
    [ObservableProperty] private bool _allowEdit;
    [ObservableProperty] private bool _allowComment;
    [ObservableProperty] private bool _allowFill = true;
    [ObservableProperty] private bool _allowAssemble;

    /// <summary>The password boxes hide what is typed unless "show passwords" is ticked.</summary>
    public char Mask => ShowPasswords ? '\0' : '•';
    partial void OnShowPasswordsChanged(bool value) => OnPropertyChanged(nameof(Mask));

    /// <summary>A protected file can be protected anew only with full access (its permissions password).</summary>
    public bool CanAdd => main.HasDocument && (!IsEncrypted || Current?.FullAccess == true || CurrentPassword.Length > 0);
    partial void OnCurrentPasswordChanged(string value) { OnPropertyChanged(nameof(CanAdd)); OnPropertyChanged(nameof(CanProtect)); }

    public bool CanProtect => CanAdd && (Restrict ? PermissionsPassword.Length > 0 && PermissionsPassword != OpenPassword : OpenPassword.Length > 0);

    private PdfPermissions Permissions => Restrict
        ? new PdfPermissions(AllowPrint, AllowCopy, AllowEdit, AllowComment, AllowFill, AllowAssemble)
        : PdfPermissions.All;

    [RelayCommand]
    private async Task ProtectAsync()
    {
        if (main.PdfPath is not { } pdf || !CanProtect) return;
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_protected.pdf") is not { } output) return;
        string? open = OpenPassword.Length > 0 ? OpenPassword : null, perms = Restrict ? PermissionsPassword : null;
        var current = CurrentPassword.Length > 0 ? CurrentPassword : PdfPasswords.For(pdf);
        var permissions = Permissions;
        var done = await Write(output, tmp => PdfSecurity.Protect(pdf, tmp, open, perms, permissions, current),
                               L.F("Ui_Protected", Path.GetFileName(output)), open);
        if (done) OpenPassword = PermissionsPassword = "";
    }

    // ------------------------------------------------------------------ hidden information
    public ObservableCollection<HiddenChoice> Hidden { get; } = [];
    public bool NothingHidden => main.HasDocument && !IsEncrypted && Hidden.Count == 0;
    public bool CanClean => main.HasDocument && !IsEncrypted;
    public string CleanBlockedText => IsEncrypted ? L.Get("Ui_HiddenProtected") : "";

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (main.PdfPath is not { } pdf || !CanClean) return;
        var remove = Hidden.Where(h => h.Remove || !h.CanChoose).Select(h => h.Item.Kind).ToHashSet();
        if (remove.Count == 0) remove.Add(HiddenInfo.EarlierVersions);   // nothing found: still written anew (old versions gone)
        if (await main.Dialogs.SavePdfAsync(Path.GetFileNameWithoutExtension(pdf) + "_cleaned.pdf") is not { } output) return;
        int count = 0;
        await Write(output, tmp => count = PdfCleaner.Clean(pdf, tmp, remove).Sum(r => r.Count), null);
        if (main.PdfPath == output) main.Status = L.F("Ui_Cleaned", Path.GetFileName(output), count);
    }

    // ------------------------------------------------------------------ writing
    /// <summary>Writes through a temporary file (nothing half-written is left), then opens the copy.</summary>
    private async Task<bool> Write(string output, Action<string> write, string? status, string? openPassword = null)
    {
        string? written = null;
        await main.Run(async () =>
        {
            var tmp = output + ".tmp";
            try
            {
                await Task.Run(() => write(tmp));
                File.Move(tmp, output, overwrite: true);
                written = output;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        if (written is null) return false;
        if (PdfCertificateSecurity.IsEncryptedForCertificates(written))
        {
            // it cannot be shown without a recipient's key: it waits for one, like a file that needs a password
            await AskCertificateAsync(written);
            if (status is not null) main.Status = status;
            return true;
        }
        if (openPassword is not null) PdfPasswords.Remember(written, openPassword);
        else PdfPasswords.Forget(written);
        await main.LoadAsync(written);
        if (status is not null) main.Status = status;
        return true;
    }
}
