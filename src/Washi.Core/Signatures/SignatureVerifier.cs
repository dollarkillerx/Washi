// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Pages;

namespace Washi.Core.Signatures;

/// <summary>What a signature in a PDF says, and whether it holds.</summary>
/// <param name="Intact">The signed bytes are exactly what the signer signed (the signature value checks out).</param>
/// <param name="Trusted">The signer's certificate leads to a certificate this computer trusts (and was valid then).</param>
/// <param name="CoversDocument">Nothing was added to the file after this signature.</param>
/// <param name="ChangedAfter">Revisions saved after this signature (0 when it covers the document).</param>
public sealed record SignatureCheck(string Field, string Signer, string Issuer, DateTimeOffset? SignedAt, bool Timestamped, string Reason,
                                    string Location, bool Intact, bool Trusted, bool CoversDocument, int ChangedAfter, string SubFilter,
                                    int Page, PdfRect Rect, IReadOnlyList<string> Problems)
{
    /// <summary>approval (an ordinary signature), certification (the author's, saying which changes may follow), or
    /// timestamp (a document time-stamp: proof the file existed as it is at that time).</summary>
    public string Kind { get; init; } = "approval";

    /// <summary>For a certified document, the changes it allows: 1 none, 2 filling in forms and signing, 3 also
    /// comments. 0 when the document is not certified.</summary>
    public int Permissions { get; init; }

    /// <summary>Long-term validation: the file itself holds the certificates and revocation answers that show the
    /// signer's certificate (and the time-stamp's) was good when it signed, so it can be checked without going online,
    /// after the certificates expire.</summary>
    public bool Ltv { get; init; }

    /// <summary>A certificate of the signer's chain was revoked before the signature can be shown to exist.</summary>
    public bool Revoked { get; init; }

    /// <summary>What later revisions changed: validation (validation data), timestamp, signature, form, metadata,
    /// comments, other.</summary>
    public IReadOnlyList<string> LaterChanges { get; init; } = [];

    /// <summary>The later changes are all of kinds the signature allows (validation data, time-stamps and further
    /// signatures always; form filling and comments as a certification allows).</summary>
    public bool ChangesAllowed { get; init; } = true;

    /// <summary>valid: intact, trusted, and nothing changed after it but what it allows; changed: intact but revised
    /// in a way it does not allow; unverified: intact but the identity is not trusted; invalid: the signed bytes were
    /// altered, the value does not check, or the certificate was revoked.</summary>
    public string Status => !Intact || Revoked ? "invalid" : !Trusted ? "unverified" : !CoversDocument && !ChangesAllowed ? "changed" : "valid";
}

/// <summary>
/// Checks the digital signatures in a PDF: the byte range each covers, the CMS signature over it (PAdES and the
/// older adbe.pkcs7 forms) or the document time-stamp, the signer's certificate chain against this computer's trusted
/// roots, the signing time (from a time-stamp when there is one), the validation data the file holds (long-term
/// validation), a certification's permissions, and what the file's later revisions changed. Nothing is sent anywhere.
/// </summary>
public static class PdfSignatureVerifier
{
    public static IReadOnlyList<SignatureCheck> Verify(string path, IEnumerable<X509Certificate2>? trust = null) => Verify(File.ReadAllBytes(path), trust);

    /// <param name="trust">Root certificates to trust besides this computer's (for example an organisation's own CA).</param>
    public static IReadOnlyList<SignatureCheck> Verify(byte[] bytes, IEnumerable<X509Certificate2>? trust = null)
    {
        var roots = trust?.ToList() ?? [];
        var list = new List<SignatureCheck>();
        using var doc = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        var eofs = Regex.Matches(Encoding.Latin1.GetString(bytes), "%%EOF").Select(m => m.Index + 5).ToList();
        var found = Read(doc, bytes);
        var data = Dss.Read(doc);
        foreach (var f in found) f.AddTo(data);
        int permissions = Certification(doc, out var certifying);
        var anchors = roots.Concat(ValidationData.SystemRoots()).ToList();
        var changes = new Dictionary<int, IReadOnlyList<string>>();

        foreach (var f in found)
        {
            var problems = f.Problems;
            string kind = f.Timestamp ? "timestamp" : ReferenceEquals(f.Sig, certifying) ? "certification" : "approval";
            var leaf = f.Timestamp ? f.TsaCert : f.Cert;
            string signer = leaf?.GetNameInfo(X509NameType.SimpleName, false) ?? Str(f.Sig, "/Name"), issuer = leaf?.GetNameInfo(X509NameType.SimpleName, true) ?? "";

            // the earliest time the signature is shown to have existed: its own time-stamp, or a later document time-stamp
            DateTimeOffset? proof = f.Stamped && !f.Timestamp ? f.At : null;
            foreach (var d in found)
                if (!ReferenceEquals(d, f) && d is { Timestamp: true, Intact: true, At: { } dt } && d.End >= f.End && (proof is null || dt < proof)) proof = dt;
            var at = f.At ?? DateTimeOffset.Now;

            bool trusted = false, ltv = false, revoked = false;
            if (leaf is not null && f.Intact)
            {
                var pool = f.Certificates.Concat(data.Certs).ToList();
                trusted = Trusted(leaf, pool, at, roots, problems);
                ltv = Covered(leaf, pool, anchors, data, at, proof, problems, ref revoked, f.Timestamp ? "time-stamp" : "signer");
                if (!f.Timestamp && f.Stamped && f.TsaCert is { } tsa && f.Token is { } token)
                {
                    var tsaPool = token.AsSignedCms().Certificates.Cast<X509Certificate2>().Concat(pool).ToList();
                    DateTimeOffset? later = found.Where(d => d is { Timestamp: true, Intact: true, At: not null } && d.End >= f.End).Select(d => d.At).Min();
                    bool tsaRevoked = false;
                    ltv &= Covered(tsa, tsaPool, anchors, data, token.TokenInfo.Timestamp, later, problems, ref tsaRevoked, "time-stamp");
                    if (tsaRevoked) problems.Add("the time-stamping authority's certificate was revoked, so the signing time is not proven");
                }
            }

            bool covers = f.Range.Length == 4 && (f.End == bytes.Length || bytes.Length - f.End <= 2 && bytes.AsSpan(f.End).Trim((byte)'\n').Trim((byte)'\r').Length == 0);
            int after = covers ? 0 : eofs.Count(e => e > f.End + 2);
            IReadOnlyList<string> later2 = [];
            bool allowed = true;
            if (!covers && f.Range.Length == 4)
            {
                if (!changes.TryGetValue(f.End, out var k))
                {
                    try { k = Revisions.Changes(bytes, f.End, doc); }
                    catch (Exception e) when (e is PdfReaderException or InvalidOperationException or ArgumentException or IOException) { k = ["other"]; }
                    changes[f.End] = k;
                }
                later2 = k;
                allowed = k.All(x => Allowed(x, permissions));
                if (!allowed) problems.Add($"the document was changed after this signature ({after} later revision(s): {string.Join(", ", k.Where(x => !Allowed(x, permissions)))})");
            }
            list.Add(new SignatureCheck(f.Name, signer, issuer, f.At, f.Stamped, Str(f.Sig, "/Reason"), Str(f.Sig, "/Location"), f.Intact, trusted, covers, after,
                                        f.SubFilter, f.Page, f.Rect, problems)
            {
                Kind = kind, Permissions = permissions, Ltv = ltv, Revoked = revoked, LaterChanges = later2, ChangesAllowed = allowed,
            });
        }
        return list;
    }

    /// <summary>Whether a change of this kind after a signature leaves it standing (PDF 2.0, 12.8.2.2).</summary>
    public static bool Allowed(string kind, int permissions) => kind switch
    {
        "validation" or "timestamp" => true,
        "signature" or "metadata" => permissions != 1,
        "form" => permissions >= 2,
        "comments" => permissions == 3,
        _ => false,
    };

    /// <summary>The certificate leads to a root this computer trusts, or one of <paramref name="roots"/>, and was valid at <paramref name="at"/>.</summary>
    private static bool Trusted(X509Certificate2 cert, List<X509Certificate2> pool, DateTimeOffset at, List<X509Certificate2> roots, List<string> problems)
    {
        X509ChainStatus[] status;
        using (var chain = Chain(pool, at))
        {
            if (chain.Build(cert)) return true;
            status = chain.ChainStatus;
        }
        if (roots.Count > 0)
        {
            using var custom = Chain(pool, at);
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.AddRange(roots.ToArray());
            if (custom.Build(cert)) return true;
            status = custom.ChainStatus;
        }
        problems.Add(cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData)
            ? "the signer's certificate is self-signed: its identity is not vouched for by anyone this computer trusts"
            : "the signer's certificate does not lead to a certificate this computer trusts ("
              + string.Join(", ", status.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct()) + ")");
        return false;

        static X509Chain Chain(List<X509Certificate2> pool, DateTimeOffset at)
        {
            var c = new X509Chain();
            c.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;   // offline: nothing is sent anywhere
            c.ChainPolicy.DisableCertificateDownloads = true;
            c.ChainPolicy.ExtraStore.AddRange(pool.ToArray());
            c.ChainPolicy.VerificationTime = at.LocalDateTime;
            return c;
        }
    }

    /// <summary>
    /// Whether the file holds what it takes to check <paramref name="leaf"/>'s chain on its own: every certificate up
    /// to a root, and for each (but the root) a revocation answer from its issuer current at <paramref name="at"/>.
    /// A certificate revoked before <paramref name="proof"/> (or at all, without proof of the time) is reported.
    /// </summary>
    private static bool Covered(X509Certificate2 leaf, List<X509Certificate2> pool, List<X509Certificate2> anchors, ValidationData data,
                                DateTimeOffset at, DateTimeOffset? proof, List<string> problems, ref bool revoked, string whose)
    {
        var chain = ValidationData.Chain(leaf, pool, anchors, out bool complete);
        bool ok = complete;
        for (int i = 0; i < chain.Count - 1; i++)
        {
            var (c, issuer) = (chain[i], chain[i + 1]);
            if (Asn.NoCheck(c)) continue;
            var (state, when) = data.Status(c, issuer, at);
            if (state == RevocationState.Revoked)
            {
                if (proof is null || when is null || when <= proof)
                {
                    revoked = true;
                    problems.Add((i == 0 ? $"the {whose}'s certificate" : $"{c.GetNameInfo(X509NameType.SimpleName, false)}, a certificate in the {whose}'s chain,") + " was revoked"
                                 + (when is { } w ? $" on {w:yyyy-MM-dd HH:mm} UTC" : "")
                                 + (proof is null ? ", and the signature has no time-stamp to show it was made before" : ", before the signature is shown to exist"));
                }
            }
            else if (state != RevocationState.Good) ok = false;
        }
        if (!complete && chain.Count > 0) ok = false;
        return ok;
    }

    /// <summary>The permissions of the document's certification (its DocMDP signature), or 0.</summary>
    internal static int Certification(PdfDocument doc, out PdfDictionary? sig)
    {
        sig = Deref(doc.Internals.Catalog.Elements["/Perms"]) is PdfDictionary perms ? Deref(perms.Elements["/DocMDP"]) as PdfDictionary : null;
        if (sig is null) return 0;
        if (Deref(sig.Elements["/Reference"]) is PdfArray refs)
            foreach (var r in refs.Elements)
                if (Deref(r) is PdfDictionary sr && Name(sr, "/TransformMethod") == "/DocMDP")
                    return Deref(sr.Elements["/TransformParams"]) is PdfDictionary tp && Deref(tp.Elements["/P"]) is PdfInteger p && p.Value is >= 1 and <= 3 ? p.Value : 2;
        return 2;
    }

    // ------------------------------------------------------------------ reading the signatures

    /// <summary>A signature as found in the file, with what its value says.</summary>
    internal sealed class Found
    {
        public required string Name { get; init; }
        public required PdfDictionary Sig { get; init; }
        public required string SubFilter { get; init; }
        public long[] Range { get; set; } = [];
        public byte[]? Contents { get; set; }
        public int End => Range.Length == 4 ? (int)(Range[2] + Range[3]) : 0;
        public bool Timestamp { get; set; }
        public bool Intact { get; set; }
        public bool Stamped { get; set; }
        public DateTimeOffset? At { get; set; }
        public X509Certificate2? Cert { get; set; }
        public X509Certificate2? TsaCert { get; set; }
        public Rfc3161TimestampToken? Token { get; set; }
        public List<X509Certificate2> Certificates { get; } = [];
        public List<byte[]> ArchivedRevocation { get; } = [];
        public List<string> Problems { get; } = [];
        public int Page { get; set; } = -1;
        public PdfRect Rect { get; set; }

        /// <summary>The revocation answers the signer put inside the signature (Adobe's revocation archive attribute).</summary>
        public void AddTo(ValidationData data)
        {
            foreach (var c in Certificates) data.Add(c);
            foreach (var der in ArchivedRevocation)
            {
                if (OcspResponse.Parse(der) is { } o) data.Add(o);
                else if (Crl.Parse(der) is { } c) data.Add(c);
            }
        }
    }

    internal static List<Found> Read(PdfDocument doc, byte[] bytes)
    {
        var list = new List<Found>();
        foreach (var (name, field, widget) in SignatureFields(doc))
        {
            if (Deref(field.Elements["/V"]) is not PdfDictionary sig) continue;
            var f = new Found { Name = name, Sig = sig, SubFilter = Name(sig, "/SubFilter").TrimStart('/') };
            f.Timestamp = f.SubFilter == "ETSI.RFC3161" || Name(sig, "/Type") == "/DocTimeStamp";
            for (int i = 0; i < doc.PageCount && widget is not null; i++)
                if (Deref(doc.Pages[i].Elements["/Annots"]) is PdfArray a && a.Elements.Any(e => ReferenceEquals(Deref(e), widget)))
                {
                    f.Page = i;
                    if (Deref(widget.Elements["/Rect"]) is PdfArray r && r.Elements.Count >= 4)
                    {
                        var down = PdfRedactor.Upright(doc.Pages[i]).Inverse() ?? Mx.Identity;
                        var b = down.Bounds(new Box(Math.Min(FontMetrics.Num(r.Elements[0]), FontMetrics.Num(r.Elements[2])), Math.Min(FontMetrics.Num(r.Elements[1]), FontMetrics.Num(r.Elements[3])),
                                                    Math.Max(FontMetrics.Num(r.Elements[0]), FontMetrics.Num(r.Elements[2])), Math.Max(FontMetrics.Num(r.Elements[1]), FontMetrics.Num(r.Elements[3]))));
                        f.Rect = new PdfRect(b.X0, b.Y0, b.X1, b.Y1);
                    }
                    break;
                }
            f.At = ParseDate(Str(sig, "/M"));
            list.Add(f);

            // the byte range, and the signature value in the hole it leaves
            long[] range = Deref(sig.Elements["/ByteRange"]) is PdfArray br && br.Elements.Count == 4
                ? [.. br.Elements.Select(e => (long)FontMetrics.Num(e))] : [];
            bool shape = range.Length == 4 && range[0] == 0 && range[1] > 0 && range[2] > range[1] && range[2] + range[3] <= bytes.Length && range[3] >= 0;
            if (!shape) { f.Problems.Add("its byte range is not well formed"); continue; }
            f.Range = range;
            var hole = Encoding.Latin1.GetString(bytes, (int)range[1], (int)(range[2] - range[1])).Trim();
            if (!hole.StartsWith('<') || !hole.EndsWith('>')) { f.Problems.Add("its byte range does not leave out exactly the signature value"); continue; }
            var hex = Regex.Replace(hole[1..^1], @"\s", "");
            try { f.Contents = Convert.FromHexString(hex.Length % 2 == 0 ? hex : hex + "0"); }
            catch (FormatException) { f.Problems.Add("its signature value is not hexadecimal"); continue; }
            var der = Trim(f.Contents);
            var data = new byte[range[1] + range[3]];
            Buffer.BlockCopy(bytes, 0, data, 0, (int)range[1]);
            Buffer.BlockCopy(bytes, (int)range[2], data, (int)range[1], (int)range[3]);

            try
            {
                if (f.Timestamp)
                {
                    // a document time-stamp: an RFC 3161 token over the byte range
                    if (!Rfc3161TimestampToken.TryDecode(der, out var token, out _)) { f.Problems.Add("its value is not a time-stamp"); continue; }
                    f.Token = token;
                    f.Certificates.AddRange(token.AsSignedCms().Certificates.Cast<X509Certificate2>());
                    f.Intact = token.VerifySignatureForData(data, out var tsa);
                    f.TsaCert = tsa ?? token.AsSignedCms().SignerInfos.Cast<SignerInfo>().FirstOrDefault()?.Certificate;
                    f.At = token.TokenInfo.Timestamp;
                    f.Stamped = f.Intact;
                    if (!f.Intact) f.Problems.Add("the time-stamp does not match the time-stamped bytes: the document was altered, or the time-stamp is damaged");
                    continue;
                }

                SignedCms cms;
                if (f.SubFilter == "adbe.pkcs7.sha1")
                {
                    cms = new SignedCms();
                    cms.Decode(der);
                    cms.CheckSignature(true);
                    f.Intact = cms.ContentInfo.Content.AsSpan().SequenceEqual(SHA1.HashData(data));
                    if (!f.Intact) f.Problems.Add("the document's digest does not match the one signed");
                }
                else if (f.SubFilter is "adbe.pkcs7.detached" or "ETSI.CAdES.detached")
                {
                    cms = new SignedCms(new ContentInfo(data), detached: true);
                    cms.Decode(der);
                    cms.CheckSignature(true);
                    f.Intact = true;
                }
                else { f.Problems.Add($"signatures of the kind {f.SubFilter} are not checked by Washi"); continue; }

                f.Certificates.AddRange(cms.Certificates.Cast<X509Certificate2>());
                if (cms.SignerInfos.Count == 0) continue;
                var info = cms.SignerInfos[0];
                f.Cert = info.Certificate;
                // the time: a time-stamp's, else the signing-time attribute, else /M
                foreach (var attr in info.UnsignedAttributes)
                    if (attr.Oid.Value == "1.2.840.113549.1.9.16.2.14" && attr.Values.Count > 0
                        && Rfc3161TimestampToken.TryDecode(attr.Values[0].RawData, out var token, out _))
                    {
                        f.Token = token;
                        f.At = token.TokenInfo.Timestamp;
                        f.Stamped = token.VerifySignatureForSignerInfo(info, out var tsa);
                        f.TsaCert = tsa;
                        if (!f.Stamped) f.Problems.Add("its time-stamp does not match the signature");
                    }
                foreach (var attr in info.SignedAttributes)
                {
                    if (!f.Stamped && attr.Oid.Value == "1.2.840.113549.1.9.5" && attr.Values.Count > 0)
                        f.At = new Pkcs9SigningTime(attr.Values[0].RawData).SigningTime;
                    if (attr.Oid.Value == "1.2.840.113583.1.1.8" && attr.Values.Count > 0)
                        f.ArchivedRevocation.AddRange(Archived(attr.Values[0].RawData));
                }
            }
            catch (CryptographicException e)
            {
                f.Intact = false;
                f.Problems.Add("the signature does not match the signed bytes: the document was altered, or the signature is damaged (" + e.Message + ")");
            }
        }
        return list;
    }

    /// <summary>RevocationInfoArchival ::= SEQUENCE { crl [0] EXPLICIT SEQUENCE OF CRLs, ocsp [1] EXPLICIT SEQUENCE OF OCSPResponse, … }.</summary>
    private static IEnumerable<byte[]> Archived(byte[] der)
    {
        var list = new List<byte[]>();
        try
        {
            var s = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            while (s.HasData)
            {
                var tag = s.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue > 1) { s.ReadEncodedValue(); continue; }
                var items = s.ReadSequence(tag).ReadSequence();
                while (items.HasData) list.Add(items.ReadEncodedValue().ToArray());
            }
        }
        catch (AsnContentException) { }
        return list;
    }

    /// <summary>A DER value without the zeros that fill the rest of the space kept for it.</summary>
    internal static byte[] Trim(byte[] b)
    {
        if (b.Length < 2 || b[0] != 0x30) return b;
        int len, at;
        if (b[1] < 0x80) { len = b[1]; at = 2; }
        else
        {
            int n = b[1] & 0x7F;
            if (n is 0 or > 4 || b.Length < 2 + n) return b;
            len = 0;
            for (int i = 0; i < n; i++) len = len << 8 | b[2 + i];
            at = 2 + n;
        }
        return at + len <= b.Length ? b[..(at + len)] : b;
    }

    internal static IEnumerable<(string Name, PdfDictionary Field, PdfDictionary? Widget)> SignatureFields(PdfDocument doc)
    {
        if (Deref(doc.Internals.Catalog.Elements["/AcroForm"]) is not PdfDictionary form || Deref(form.Elements["/Fields"]) is not PdfArray roots) yield break;
        var stack = new Stack<(PdfDictionary, string, string?)>();
        foreach (var r in roots.Elements.Reverse()) if (Deref(r) is PdfDictionary d) stack.Push((d, "", null));
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var (node, parent, ft) = stack.Pop();
            if (!seen.Add(node)) continue;
            var t = (Deref(node.Elements["/T"]) as PdfString)?.Value ?? "";
            var full = t.Length == 0 ? parent : parent.Length == 0 ? t : parent + "." + t;
            ft = Name(node, "/FT") is { Length: > 0 } own ? own : ft;
            var kids = (Deref(node.Elements["/Kids"]) as PdfArray)?.Elements.Select(Deref).OfType<PdfDictionary>().ToList() ?? [];
            if (kids.Any(k => k.Elements.ContainsKey("/T"))) { for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], full, ft)); continue; }
            if (ft == "/Sig") yield return (full, node, kids.Count > 0 ? kids[0] : node.Elements.ContainsKey("/Rect") ? node : null);
        }
    }

    private static string Str(PdfDictionary d, string key) => (Deref(d.Elements[key]) as PdfString)?.Value ?? "";

    internal static string Name(PdfDictionary d, string key) => Deref(d.Elements[key]) is PdfName n ? n.Value : "";

    /// <summary>A PDF date (D:20261123140512+09'00').</summary>
    public static DateTimeOffset? ParseDate(string s)
    {
        var m = Regex.Match(s, @"D:(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?([Z+\-])?(\d{2})?'?(\d{2})?");
        if (!m.Success) return null;
        int G(int i, int d) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : d;
        var offset = m.Groups[7].Value is "+" or "-" ? new TimeSpan(G(8, 0), G(9, 0), 0) * (m.Groups[7].Value == "-" ? -1 : 1) : TimeSpan.Zero;
        try { return new DateTimeOffset(G(1, 1), G(2, 1), G(3, 1), G(4, 0), G(5, 0), G(6, 0), offset); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
