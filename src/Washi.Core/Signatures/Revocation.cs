// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Washi.Core.Signatures;

internal enum RevocationState { Unknown, Good, Revoked }

/// <summary>One certificate's status in an OCSP response.</summary>
internal sealed record OcspSingle(string HashOid, byte[] NameHash, byte[] KeyHash, string Serial, RevocationState Status,
                                  DateTimeOffset? RevokedAt, DateTimeOffset ThisUpdate, DateTimeOffset? NextUpdate);

/// <summary>An OCSP response (RFC 6960): the whole OCSPResponse as it is kept in a file, and its signed part.</summary>
internal sealed record OcspResponse(byte[] Der, byte[] Tbs, string SigAlg, byte[]? SigParams, byte[] Signature,
                                    IReadOnlyList<X509Certificate2> Certs, DateTimeOffset ProducedAt, IReadOnlyList<OcspSingle> Responses)
{
    public static OcspResponse? Parse(byte[] der)
    {
        try
        {
            var resp = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            var status = resp.ReadEnumeratedBytes().Span;
            if (status.Length != 1 || status[0] != 0 || !resp.HasData) return null;   // not "successful"
            var bytes = resp.ReadSequence(Asn.Ctx(0, true)).ReadSequence();
            if (bytes.ReadObjectIdentifier() != "1.3.6.1.5.5.7.48.1.1") return null;   // id-pkix-ocsp-basic
            var basic = new AsnReader(bytes.ReadOctetString(), AsnEncodingRules.BER).ReadSequence();
            var tbs = basic.ReadEncodedValue().ToArray();
            var (alg, prm) = Asn.AlgId(basic);
            var sig = basic.ReadBitString(out _);
            var certs = new List<X509Certificate2>();
            if (basic.HasData && basic.PeekTag().HasSameClassAndValue(Asn.Ctx(0, true)))
            {
                var list = basic.ReadSequence(Asn.Ctx(0, true)).ReadSequence();
                while (list.HasData) certs.Add(X509CertificateLoader.LoadCertificate(list.ReadEncodedValue().ToArray()));
            }

            var t = new AsnReader(tbs, AsnEncodingRules.BER).ReadSequence();
            if (t.PeekTag().HasSameClassAndValue(Asn.Ctx(0, true))) t.ReadEncodedValue();   // version
            t.ReadEncodedValue();                                                            // responderID
            var produced = t.ReadGeneralizedTime();
            var singles = new List<OcspSingle>();
            var rs = t.ReadSequence();
            while (rs.HasData)
            {
                var s = rs.ReadSequence();
                var (hash, nameHash, keyHash, serial) = Asn.CertId(s.ReadSequence());
                var tag = s.PeekTag();
                RevocationState state;
                DateTimeOffset? revokedAt = null;
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 0) { s.ReadEncodedValue(); state = RevocationState.Good; }
                else if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 1)
                {
                    revokedAt = s.ReadSequence(Asn.Ctx(1, true)).ReadGeneralizedTime();
                    state = RevocationState.Revoked;
                }
                else { s.ReadEncodedValue(); state = RevocationState.Unknown; }
                var thisUpdate = s.ReadGeneralizedTime();
                DateTimeOffset? next = null;
                if (s.HasData && s.PeekTag().HasSameClassAndValue(Asn.Ctx(0, true))) next = s.ReadSequence(Asn.Ctx(0, true)).ReadGeneralizedTime();
                singles.Add(new OcspSingle(hash, nameHash, keyHash, serial, state, revokedAt, thisUpdate, next));
            }
            return new OcspResponse(der, tbs, alg, prm, sig, certs, produced, singles);
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException or ArgumentException) { return null; }
    }

    /// <summary>The status this response gives <paramref name="cert"/>, if it is about it.</summary>
    public OcspSingle? About(X509Certificate2 cert, X509Certificate2 issuer)
    {
        foreach (var s in Responses)
        {
            if (!string.Equals(s.Serial, cert.SerialNumber, StringComparison.OrdinalIgnoreCase)) continue;
            if (Asn.Hash(s.HashOid, cert.IssuerName.RawData) is not { } n || !n.AsSpan().SequenceEqual(s.NameHash)) continue;
            if (Asn.Hash(s.HashOid, issuer.PublicKey.EncodedKeyValue.RawData) is not { } k || !k.AsSpan().SequenceEqual(s.KeyHash)) continue;
            return s;
        }
        return null;
    }

    /// <summary>Who signed this response for <paramref name="issuer"/>: the issuer itself, or a responder it gave an
    /// OCSP-signing certificate to (RFC 6960 4.2.2.2). Null when neither signed it.</summary>
    public X509Certificate2? Responder(X509Certificate2 issuer, IEnumerable<X509Certificate2> pool)
    {
        if (Asn.SignedBy(issuer, SigAlg, SigParams, Tbs, Signature)) return issuer;
        foreach (var c in Certs.Concat(pool))
            if (Asn.CertSignedBy(c, issuer) && Asn.HasEku(c, "1.3.6.1.5.5.7.3.9")
                && c.NotBefore <= ProducedAt.UtcDateTime && ProducedAt.UtcDateTime <= c.NotAfter
                && Asn.SignedBy(c, SigAlg, SigParams, Tbs, Signature))
                return c;
        return null;
    }
}

/// <summary>A certificate revocation list (RFC 5280 5).</summary>
internal sealed record Crl(byte[] Der, byte[] Tbs, string SigAlg, byte[]? SigParams, byte[] Signature, byte[] Issuer,
                           DateTimeOffset ThisUpdate, DateTimeOffset? NextUpdate, IReadOnlyDictionary<string, DateTimeOffset> Revoked)
{
    public static Crl? Parse(byte[] der)
    {
        try
        {
            var r = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            var tbs = r.ReadEncodedValue().ToArray();
            var (alg, prm) = Asn.AlgId(r);
            var sig = r.ReadBitString(out _);
            var t = new AsnReader(tbs, AsnEncodingRules.BER).ReadSequence();
            if (t.PeekTag().HasSameClassAndValue(Asn1Tag.Integer)) t.ReadIntegerBytes();
            Asn.AlgId(t);
            var issuer = t.ReadEncodedValue().ToArray();
            var thisUpdate = Asn.Time(t);
            DateTimeOffset? next = null;
            if (t.HasData && (t.PeekTag().HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.UtcTime))
                              || t.PeekTag().HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.GeneralizedTime))))
                next = Asn.Time(t);
            var revoked = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            if (t.HasData && t.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                var list = t.ReadSequence();
                while (list.HasData)
                {
                    var e = list.ReadSequence();
                    revoked[Convert.ToHexString(e.ReadIntegerBytes().Span)] = Asn.Time(e);
                }
            }
            return new Crl(der, tbs, alg, prm, sig, issuer, thisUpdate, next, revoked);
        }
        catch (Exception e) when (e is AsnContentException or CryptographicException or ArgumentException) { return null; }
    }

    public bool IssuedBy(X509Certificate2 issuer) =>
        Issuer.AsSpan().SequenceEqual(issuer.SubjectName.RawData) && Asn.SignedBy(issuer, SigAlg, SigParams, Tbs, Signature);
}

/// <summary>
/// The certificates and revocation answers (OCSP responses, CRLs) a file carries, and what they say about a
/// certificate at a given time, checked offline: each answer must be signed by the certificate's issuer (or its OCSP
/// responder) and be about that very certificate.
/// </summary>
internal sealed class ValidationData
{
    public List<X509Certificate2> Certs { get; } = [];
    public List<OcspResponse> Ocsps { get; } = [];
    public List<Crl> Crls { get; } = [];

    public void Add(X509Certificate2 c) { if (!Certs.Any(x => x.RawData.AsSpan().SequenceEqual(c.RawData))) Certs.Add(c); }
    public void Add(OcspResponse o) { if (!Ocsps.Any(x => x.Der.AsSpan().SequenceEqual(o.Der))) Ocsps.Add(o); }
    public void Add(Crl c) { if (!Crls.Any(x => x.Der.AsSpan().SequenceEqual(c.Der))) Crls.Add(c); }

    /// <summary>
    /// The status of <paramref name="cert"/> from the answers its issuer signed. An answer counts when it was still
    /// current at <paramref name="at"/> (its next update is not before it) and was made while the certificate was
    /// valid. Revoked wins over good.
    /// </summary>
    public (RevocationState State, DateTimeOffset? RevokedAt) Status(X509Certificate2 cert, X509Certificate2 issuer, DateTimeOffset at)
    {
        var state = RevocationState.Unknown;
        DateTimeOffset? revokedAt = null;
        foreach (var o in Ocsps)
        {
            if (o.About(cert, issuer) is not { } s || o.Responder(issuer, Certs) is null) continue;
            if (s.Status == RevocationState.Revoked) return (RevocationState.Revoked, s.RevokedAt);
            if (s.Status == RevocationState.Good && Current(s.ThisUpdate, s.NextUpdate, cert, at)) state = RevocationState.Good;
        }
        foreach (var c in Crls)
        {
            if (!c.IssuedBy(issuer)) continue;
            if (c.Revoked.TryGetValue(cert.SerialNumber, out var when)) return (RevocationState.Revoked, when);
            if (Current(c.ThisUpdate, c.NextUpdate, cert, at)) state = RevocationState.Good;
        }
        return (state, revokedAt);
    }

    /// <summary>
    /// Whether an answer about <paramref name="cert"/> was issued at or after <paramref name="at"/>, or says it is
    /// revoked. Strict validators (ETSI EN 319 102-1) only take answers issued after the time they vouch for, so an
    /// older one is fetched again.
    /// </summary>
    public bool AnsweredSince(X509Certificate2 cert, X509Certificate2 issuer, DateTimeOffset at)
    {
        foreach (var o in Ocsps)
            if (o.About(cert, issuer) is { } s && o.Responder(issuer, Certs) is not null
                && (s.Status == RevocationState.Revoked || s.Status == RevocationState.Good && s.ThisUpdate >= at.AddSeconds(-1)))
                return true;
        foreach (var c in Crls)
            if (c.IssuedBy(issuer) && (c.Revoked.ContainsKey(cert.SerialNumber) || c.ThisUpdate >= at.AddSeconds(-1)))
                return true;
        return false;
    }

    private static bool Current(DateTimeOffset thisUpdate, DateTimeOffset? next, X509Certificate2 cert, DateTimeOffset at) =>
        thisUpdate.UtcDateTime >= cert.NotBefore.ToUniversalTime().AddMinutes(-5) && (next is null || next.Value >= at);

    /// <summary>
    /// The chain from <paramref name="leaf"/> to a self-signed certificate or to one of <paramref name="anchors"/>,
    /// built only from the certificates at hand (each link checked by its signature). <paramref name="complete"/>
    /// says whether it reached one.
    /// </summary>
    public static List<X509Certificate2> Chain(X509Certificate2 leaf, IEnumerable<X509Certificate2> pool, IEnumerable<X509Certificate2> anchors, out bool complete)
    {
        var all = pool.ToList();
        var roots = anchors.ToList();
        var chain = new List<X509Certificate2> { leaf };
        complete = false;
        var cur = leaf;
        for (int depth = 0; depth < 12; depth++)
        {
            if (Asn.SelfSigned(cur)) { complete = true; break; }
            var issuer = all.Concat(roots).FirstOrDefault(c => !c.RawData.AsSpan().SequenceEqual(cur.RawData) && Asn.CertSignedBy(cur, c));
            if (issuer is null) break;
            chain.Add(issuer);
            if (roots.Any(r => r.RawData.AsSpan().SequenceEqual(issuer.RawData))) { complete = true; break; }
            cur = issuer;
        }
        return chain;
    }

    /// <summary>The root certificates this computer trusts.</summary>
    public static IReadOnlyList<X509Certificate2> SystemRoots()
    {
        if (_roots is { } r) return r;
        var list = new List<X509Certificate2>();
        foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            try
            {
                using var store = new X509Store(StoreName.Root, loc);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                list.AddRange(store.Certificates);
            }
            catch (CryptographicException) { }
        return _roots = list;
    }

    private static IReadOnlyList<X509Certificate2>? _roots;
}

/// <summary>Fetches revocation answers from the services a certificate names (its OCSP responder, its CRL
/// distribution points). Only the certificate's identifiers are sent, nothing of the document.</summary>
internal static class RevocationClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const int MaxCrl = 40 * 1024 * 1024;

    public static OcspResponse? Ocsp(X509Certificate2 cert, X509Certificate2 issuer, List<string> notes)
    {
        foreach (var url in Asn.OcspUrls(cert))
        {
            try
            {
                using var content = new ByteArrayContent(Asn.OcspRequest(cert, issuer));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ocsp-request");
                using var response = Http.Send(new HttpRequestMessage(HttpMethod.Post, url) { Content = content });
                if (!response.IsSuccessStatusCode) { notes.Add($"{Host(url)}: HTTP {(int)response.StatusCode}"); continue; }
                var der = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (OcspResponse.Parse(der) is not { } o || o.About(cert, issuer) is null) { notes.Add($"{Host(url)}: no usable OCSP answer"); continue; }
                if (o.Responder(issuer, []) is null) { notes.Add($"{Host(url)}: the OCSP answer is not signed by the issuer"); continue; }
                return o;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException) { notes.Add($"{Host(url)}: {e.Message}"); }
        }
        return null;
    }

    public static Crl? Crl(X509Certificate2 cert, X509Certificate2 issuer, List<string> notes)
    {
        foreach (var url in Asn.CrlUrls(cert))
        {
            try
            {
                using var response = Http.Send(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) { notes.Add($"{Host(url)}: HTTP {(int)response.StatusCode}"); continue; }
                if (response.Content.Headers.ContentLength > MaxCrl) { notes.Add($"{Host(url)}: the revocation list is too large"); continue; }
                var der = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (der.Length > 0 && der[0] != 0x30) der = Pem(der);
                if (Signatures.Crl.Parse(der) is not { } c || !c.IssuedBy(issuer)) { notes.Add($"{Host(url)}: no usable revocation list"); continue; }
                return c;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException) { notes.Add($"{Host(url)}: {e.Message}"); }
        }
        return null;
    }

    private static byte[] Pem(byte[] text)
    {
        var s = System.Text.Encoding.ASCII.GetString(text);
        int a = s.IndexOf("-----BEGIN", StringComparison.Ordinal), b = a < 0 ? -1 : s.IndexOf('\n', a), e = s.IndexOf("-----END", StringComparison.Ordinal);
        if (a < 0 || b < 0 || e < b) return text;
        try { return Convert.FromBase64String(s[b..e].Replace("\r", "").Replace("\n", "")); }
        catch (FormatException) { return text; }
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}

/// <summary>The bits of ASN.1 the revocation answers, certificates and requests need.</summary>
internal static class Asn
{
    public static Asn1Tag Ctx(int n, bool constructed) => new(TagClass.ContextSpecific, n, constructed);

    public static (string Oid, byte[]? Params) AlgId(AsnReader r)
    {
        var a = r.ReadSequence();
        var oid = a.ReadObjectIdentifier();
        return (oid, a.HasData ? a.ReadEncodedValue().ToArray() : null);
    }

    public static (string Hash, byte[] NameHash, byte[] KeyHash, string Serial) CertId(AsnReader c)
    {
        var (hash, _) = AlgId(c);
        var name = c.ReadOctetString();
        var key = c.ReadOctetString();
        return (hash, name, key, Convert.ToHexString(c.ReadIntegerBytes().Span));
    }

    public static DateTimeOffset Time(AsnReader r) =>
        r.PeekTag().HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.UtcTime)) ? r.ReadUtcTime() : r.ReadGeneralizedTime();

    public static byte[]? Hash(string oid, byte[] data) => oid switch
    {
        "1.3.14.3.2.26" => SHA1.HashData(data),
        "2.16.840.1.101.3.4.2.1" => SHA256.HashData(data),
        "2.16.840.1.101.3.4.2.2" => SHA384.HashData(data),
        "2.16.840.1.101.3.4.2.3" => SHA512.HashData(data),
        _ => null,
    };

    private static HashAlgorithmName? HashName(string oid) => oid switch
    {
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        _ => null,
    };

    /// <summary>Whether <paramref name="signature"/> over <paramref name="data"/> was made with the key of <paramref name="signer"/>.</summary>
    public static bool SignedBy(X509Certificate2 signer, string alg, byte[]? prm, byte[] data, byte[] signature)
    {
        try
        {
            switch (alg)
            {
                case "1.2.840.113549.1.1.5": return Rsa(HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
                case "1.2.840.113549.1.1.11": return Rsa(HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                case "1.2.840.113549.1.1.12": return Rsa(HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1);
                case "1.2.840.113549.1.1.13": return Rsa(HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
                case "1.2.840.113549.1.1.10":
                {
                    var hash = HashAlgorithmName.SHA1;
                    if (prm is not null)
                    {
                        var p = new AsnReader(prm, AsnEncodingRules.BER).ReadSequence();
                        if (p.HasData && p.PeekTag().HasSameClassAndValue(Ctx(0, true)) && HashName(AlgId(p.ReadSequence(Ctx(0, true))).Oid) is { } h) hash = h;
                    }
                    return Rsa(hash, RSASignaturePadding.Pss);
                }
                case "1.2.840.10045.4.1": return Ec(HashAlgorithmName.SHA1);
                case "1.2.840.10045.4.3.2": return Ec(HashAlgorithmName.SHA256);
                case "1.2.840.10045.4.3.3": return Ec(HashAlgorithmName.SHA384);
                case "1.2.840.10045.4.3.4": return Ec(HashAlgorithmName.SHA512);
                default: return false;
            }
        }
        catch (Exception e) when (e is CryptographicException or AsnContentException or ArgumentException) { return false; }

        bool Rsa(HashAlgorithmName h, RSASignaturePadding pad)
        {
            using var k = signer.GetRSAPublicKey();
            return k is not null && k.VerifyData(data, signature, h, pad);
        }
        bool Ec(HashAlgorithmName h)
        {
            using var k = signer.GetECDsaPublicKey();
            return k is not null && k.VerifyData(data, signature, h, DSASignatureFormat.Rfc3279DerSequence);
        }
    }

    /// <summary>Whether <paramref name="child"/> was issued (signed) by <paramref name="issuer"/>.</summary>
    public static bool CertSignedBy(X509Certificate2 child, X509Certificate2 issuer)
    {
        if (!child.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData)) return false;
        try
        {
            var r = new AsnReader(child.RawData, AsnEncodingRules.DER).ReadSequence();
            var tbs = r.ReadEncodedValue().ToArray();
            var (alg, prm) = AlgId(r);
            return SignedBy(issuer, alg, prm, tbs, r.ReadBitString(out _));
        }
        catch (AsnContentException) { return false; }
    }

    public static bool SelfSigned(X509Certificate2 c) =>
        c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData) && CertSignedBy(c, c);

    public static bool HasEku(X509Certificate2 c, string oid) =>
        c.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == oid));

    /// <summary>An OCSP responder's own certificate, which needs no revocation check (id-pkix-ocsp-nocheck).</summary>
    public static bool NoCheck(X509Certificate2 c) => c.Extensions.Cast<X509Extension>().Any(e => e.Oid?.Value == "1.3.6.1.5.5.7.48.1.5");

    public static IEnumerable<string> OcspUrls(X509Certificate2 c) => AccessUrls(c, "1.3.6.1.5.5.7.48.1");

    public static IEnumerable<string> IssuerUrls(X509Certificate2 c) => AccessUrls(c, "1.3.6.1.5.5.7.48.2");

    private static IEnumerable<string> AccessUrls(X509Certificate2 c, string method)
    {
        var list = new List<string>();
        foreach (X509Extension e in c.Extensions)
        {
            if (e.Oid?.Value != "1.3.6.1.5.5.7.1.1") continue;
            try
            {
                var s = new AsnReader(e.RawData, AsnEncodingRules.BER).ReadSequence();
                while (s.HasData)
                {
                    var d = s.ReadSequence();
                    var m = d.ReadObjectIdentifier();
                    if (m == method && d.PeekTag().HasSameClassAndValue(Ctx(6, false)) && Http(d.ReadCharacterString(UniversalTagNumber.IA5String, Ctx(6, false))) is { } u) list.Add(u);
                }
            }
            catch (AsnContentException) { }
        }
        return list;
    }

    public static IEnumerable<string> CrlUrls(X509Certificate2 c)
    {
        var list = new List<string>();
        foreach (X509Extension e in c.Extensions)
        {
            if (e.Oid?.Value != "2.5.29.31") continue;
            try
            {
                var points = new AsnReader(e.RawData, AsnEncodingRules.BER).ReadSequence();
                while (points.HasData)
                {
                    var dp = points.ReadSequence();
                    if (!dp.HasData || !dp.PeekTag().HasSameClassAndValue(Ctx(0, true))) continue;
                    var name = dp.ReadSequence(Ctx(0, true));
                    if (!name.HasData || !name.PeekTag().HasSameClassAndValue(Ctx(0, true))) continue;
                    var full = name.ReadSequence(Ctx(0, true));
                    while (full.HasData)
                    {
                        if (full.PeekTag().HasSameClassAndValue(Ctx(6, false)) && Http(full.ReadCharacterString(UniversalTagNumber.IA5String, Ctx(6, false))) is { } u) list.Add(u);
                        else full.ReadEncodedValue();
                    }
                }
            }
            catch (AsnContentException) { }
        }
        return list;
    }

    private static string? Http(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? url : null;

    /// <summary>An OCSP request for one certificate (SHA-1 CertID, as every responder understands).</summary>
    public static byte[] OcspRequest(X509Certificate2 cert, X509Certificate2 issuer)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())            // OCSPRequest
        using (w.PushSequence())            // TBSRequest
        using (w.PushSequence())            // requestList
        using (w.PushSequence())            // Request
        {
            WriteCertId(w, cert, issuer);
        }
        return w.Encode();
    }

    public static void WriteCertId(AsnWriter w, X509Certificate2 cert, X509Certificate2 issuer)
    {
        using (w.PushSequence())
        {
            using (w.PushSequence()) { w.WriteObjectIdentifier("1.3.14.3.2.26"); w.WriteNull(); }
            w.WriteOctetString(SHA1.HashData(cert.IssuerName.RawData));
            w.WriteOctetString(SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData));
            w.WriteInteger(cert.SerialNumberBytes.Span);
        }
    }
}
