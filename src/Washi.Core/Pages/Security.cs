// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Washi.Core.Pages;

/// <summary>What a protected PDF lets its readers do. All true: nothing is restricted.</summary>
/// <param name="Print">Print (at full quality).</param>
/// <param name="Copy">Copy text and images.</param>
/// <param name="Edit">Change the content.</param>
/// <param name="Comment">Add and change comments.</param>
/// <param name="FillForms">Fill in form fields and sign.</param>
/// <param name="Assemble">Insert, delete and turn pages.</param>
public sealed record PdfPermissions(bool Print = true, bool Copy = true, bool Edit = true, bool Comment = true, bool FillForms = true, bool Assemble = true)
{
    public static PdfPermissions All { get; } = new();
    public bool RestrictsAnything => !(Print && Copy && Edit && Comment && FillForms && Assemble);
}

/// <summary>How a PDF is protected.</summary>
/// <param name="Encrypted">It has a password (one to open it, or only one that guards its permissions).</param>
/// <param name="NeedsPassword">It cannot be opened without a password, and none that works was given.</param>
/// <param name="FullAccess">The password given (or none) allows changing or removing the protection.</param>
/// <param name="Permissions">What a reader may do (all true when it is not encrypted, or when it cannot be opened).</param>
/// <param name="Method">"AES-256", "AES-128", "RC4-128" or "RC4-40"; empty when it is not encrypted.</param>
public sealed record PdfProtection(bool Encrypted, bool NeedsPassword, bool FullAccess, PdfPermissions Permissions, string Method);

/// <summary>
/// Passwords the user typed for files in this session, so the viewer (PDFium) can open them. Kept in memory only,
/// never written anywhere.
/// </summary>
public static class PdfPasswords
{
    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.Ordinal);

    public static void Remember(string path, string password) => Known[Path.GetFullPath(path)] = password;
    public static void Forget(string path) => Known.TryRemove(Path.GetFullPath(path), out _);
    public static string? For(string path) => Known.TryGetValue(Path.GetFullPath(path), out var p) ? p : null;
}

/// <summary>
/// Password protection (PDFsharp): add an open password and/or a permissions password with AES-256 (PDF 2.0, opens in
/// Acrobat X and later and every current viewer), and remove protection when the user knows the password that allows
/// it. Washi never removes protection without that password.
/// </summary>
public static class PdfSecurity
{
    /// <summary>How <paramref name="path"/> is protected, opened with <paramref name="password"/> (or none).</summary>
    public static PdfProtection Inspect(string path, string? password = null)
    {
        PdfDocument d;
        try { d = PdfReader.Open(path, password ?? "", PdfDocumentOpenMode.Import); }
        catch (PdfReaderException e) when (IsPasswordProblem(e)) { return new(true, true, false, PdfPermissions.All, ""); }
        using (d)
        {
            var h = d.SecurityHandler.Elements;
            int v = h.GetInteger("/V");
            if (v <= 0 && !h.ContainsKey("/Filter")) return new(false, false, true, PdfPermissions.All, "");
            var s = d.SecuritySettings;
            var perms = new PdfPermissions(s.PermitPrint, s.PermitExtractContent, s.PermitModifyDocument, s.PermitAnnotations,
                                           s.PermitFormsFill, s.PermitAssembleDocument);
            return new(true, false, CanModify(path, password), perms, Method(h));
        }
    }

    private static bool CanModify(string path, string? password)
    {
        try { using var _ = PdfReader.Open(path, password ?? "", PdfDocumentOpenMode.Modify); return true; }
        catch (PdfReaderException e) when (IsPasswordProblem(e)) { return false; }
    }

    private static string Method(PdfDictionary.DictionaryElements h)
    {
        int v = h.GetInteger("/V"), length = h.GetInteger("/Length");
        if (v >= 5) return "AES-256";
        if (v == 4)
        {
            var cfm = (h.GetDictionary("/CF")?.Elements.GetDictionary("/StdCF"))?.Elements.GetName("/CFM");
            return cfm == "/AESV2" ? "AES-128" : "RC4-128";
        }
        return v == 2 && length > 40 ? "RC4-" + length : "RC4-40";
    }

    internal static bool IsPasswordProblem(PdfReaderException e) => e.Message.Contains("password", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens <paramref name="path"/> for writing a changed copy. A protected file needs the password that allows changes
    /// (its permissions password, or its open password when it has no separate one).
    /// </summary>
    internal static PdfDocument OpenForChanges(string path, string? password)
    {
        try { return PdfReader.Open(path, password ?? "", PdfDocumentOpenMode.Modify); }
        catch (PdfReaderException e) when (e.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase))
        {
            throw new PageAssemblyException($"{Path.GetFileName(path)}: the permissions password is needed for this", "Err_NeedsPermissionsPassword", Path.GetFileName(path));
        }
        catch (PdfReaderException e) when (IsPasswordProblem(e))
        {
            throw string.IsNullOrEmpty(password)
                ? new PageAssemblyException($"{Path.GetFileName(path)} is protected with a password", "Err_NeedsPassword", Path.GetFileName(path))
                : new PageAssemblyException($"the password for {Path.GetFileName(path)} is not right", "Err_WrongPassword", Path.GetFileName(path));
        }
    }

    /// <summary>
    /// Writes a protected copy. <paramref name="openPassword"/> is asked for when the file is opened;
    /// <paramref name="permissionsPassword"/> guards <paramref name="permissions"/> (and the protection itself), so it
    /// is required, and must differ from the open password, whenever something is restricted.
    /// <paramref name="currentPassword"/> opens an input that is already protected.
    /// </summary>
    public static void Protect(string input, string output, string? openPassword, string? permissionsPassword,
                               PdfPermissions permissions, string? currentPassword = null)
    {
        openPassword = string.IsNullOrEmpty(openPassword) ? null : openPassword;
        permissionsPassword = string.IsNullOrEmpty(permissionsPassword) ? null : permissionsPassword;
        if (openPassword is null && permissionsPassword is null)
            throw new PageAssemblyException("give an open password, a permissions password, or both", "Err_ProtectNoPassword");
        if (permissions.RestrictsAnything && (permissionsPassword is null || permissionsPassword == openPassword))
            throw new PageAssemblyException("restrictions need a permissions password that differs from the open password", "Err_ProtectPermissionsPassword");

        using (var d = OpenForChanges(input, currentPassword))
        {
            var s = d.SecuritySettings;
            s.UserPassword = openPassword ?? "";
            s.OwnerPassword = permissionsPassword ?? openPassword!;
            s.PermitPrint = s.PermitFullQualityPrint = permissions.Print;
            s.PermitExtractContent = permissions.Copy;
            s.PermitModifyDocument = permissions.Edit;
            s.PermitAnnotations = permissions.Comment;
            s.PermitFormsFill = permissions.FillForms;
            s.PermitAssembleDocument = permissions.Assemble;
            d.SecurityHandler.SetEncryptionToV5();
            d.Save(output);
        }

        // proof: the copy asks for what it should, and only that
        var check = Inspect(output, openPassword);
        bool ok = check is { Encrypted: true, NeedsPassword: false, Method: "AES-256" } && check.Permissions == permissions
                  && (openPassword is null || Inspect(output).NeedsPassword)
                  && Inspect(output, permissionsPassword ?? openPassword).FullAccess;
        if (!ok)
        {
            File.Delete(output);
            throw new PageAssemblyException("the protected copy did not check out; it was not kept", "Err_ProtectCheck");
        }
    }

    /// <summary>Writes a copy without any password. <paramref name="password"/> must allow changes (see <see cref="OpenForChanges"/>).</summary>
    public static void Unlock(string input, string output, string? password)
    {
        using (var d = OpenForChanges(input, password))
        {
            if (!Inspect(input, password).Encrypted)
                throw new PageAssemblyException($"{Path.GetFileName(input)} is not protected", "Err_NotProtected", Path.GetFileName(input));
            d.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
            d.Save(output);
        }
        if (Inspect(output).Encrypted)
        {
            File.Delete(output);
            throw new PageAssemblyException("the copy is still protected; it was not kept", "Err_ProtectCheck");
        }
    }
}
