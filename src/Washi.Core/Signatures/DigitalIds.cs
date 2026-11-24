// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Washi.Core.Signatures;

/// <summary>A certificate with its private key that can sign: from the system's store or a .p12 / .pfx file.</summary>
public sealed record DigitalId(X509Certificate2 Certificate, string Source)
{
    /// <summary>The other certificates that came with it (a .p12 file usually holds the issuing CA's chain); they go
    /// into the signature so that readers can build the chain.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

    public string Name => Certificate.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } n ? n : Certificate.Subject;
    public string Issuer => Certificate.GetNameInfo(X509NameType.SimpleName, true);
    public bool SelfSigned => Certificate.SubjectName.RawData.AsSpan().SequenceEqual(Certificate.IssuerName.RawData);
    public string Label => $"{Name} ({(SelfSigned ? "self-signed" : Issuer)}, until {Certificate.NotAfter:yyyy-MM-dd})";
}

/// <summary>Finds, opens and makes digital IDs.</summary>
public static class DigitalIds
{
    /// <summary>
    /// Certificates in the user's store (the login keychain on macOS, the personal store on Windows) that have a
    /// private key, are valid now and may sign.
    /// </summary>
    public static IReadOnlyList<DigitalId> FromStore()
    {
        var list = new List<DigitalId>();
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (var c in store.Certificates)
                if (c.HasPrivateKey && c.NotBefore <= DateTime.Now && c.NotAfter >= DateTime.Now && CanSign(c))
                    list.Add(new DigitalId(c, "store"));
        }
        catch (CryptographicException) { }
        return list;
    }

    private static bool CanSign(X509Certificate2 c)
    {
        var usage = c.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage is null || usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature) || usage.KeyUsages.HasFlag(X509KeyUsageFlags.NonRepudiation);
    }

    /// <summary>Opens a .p12 / .pfx file.</summary>
    /// <exception cref="Pages.PageAssemblyException">The password is wrong, or the file holds no key that can sign.</exception>
    public static DigitalId FromFile(string path, string password)
    {
        X509Certificate2Collection all;
        // the key stays in memory where the platform allows it (macOS keeps it in a temporary keychain instead)
        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.Exportable : X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet;
        try { all = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password, flags); }
        catch (CryptographicException)
        {
            throw new Pages.PageAssemblyException($"{Path.GetFileName(path)} could not be opened: the password is wrong or it is not a digital ID file",
                                                  "Err_IdOpen", Path.GetFileName(path));
        }
        var cert = all.FirstOrDefault(c => c.HasPrivateKey)
            ?? throw new Pages.PageAssemblyException($"{Path.GetFileName(path)} holds no private key", "Err_IdNoKey", Path.GetFileName(path));
        return new DigitalId(cert, path) { Chain = [.. all.Where(c => !ReferenceEquals(c, cert))] };
    }

    /// <summary>
    /// Makes a self-signed digital ID (RSA 2048, valid five years, as Acrobat makes them) for signing and for opening files
    /// encrypted for it, and saves it as a password-protected .p12 file. Readers see the signer's name, but will say the
    /// identity is not verified until they trust it.
    /// </summary>
    public static DigitalId Create(string name, string email, string path, string password)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new Pages.PageAssemblyException("a digital ID needs a name", "Err_IdName");
        if (password.Length < 6) throw new Pages.PageAssemblyException("the password must be at least 6 characters", "Err_IdPassword");
        if (File.Exists(path)) throw new Pages.PageAssemblyException($"{Path.GetFileName(path)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(path));
        using var key = RSA.Create(2048);
        var dn = new X500DistinguishedNameBuilder();
        dn.AddCommonName(name.Trim());
        if (email.Trim().Length > 0) dn.AddEmailAddress(email.Trim());
        var req = new CertificateRequest(dn.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation
                                                                | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.4"), new Oid("1.2.840.113583.1.1.5")], false));   // e-mail protection, Adobe document signing
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, password));
        return FromFile(path, password);
    }
}
