// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Washi.Core.Signatures;

namespace Washi.Masume.Tests;

/// <summary>
/// A small certificate authority for the tests, served on this computer only: a root (ECDSA), an intermediate (RSA)
/// that issues the signer's and the time-stamping authority's certificates, an OCSP responder (the intermediate
/// itself), revocation lists for both CAs, and an RFC 3161 time-stamping service. Nothing leaves 127.0.0.1.
/// </summary>
internal sealed class TestPki : IDisposable
{
    private readonly HttpListener _http = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly X509Certificate2 _rootKey, _intermediateKey, _tsaKey;

    public X509Certificate2 Root { get; }
    public X509Certificate2 Intermediate { get; }
    public X509Certificate2 Tsa { get; }
    public Uri Base { get; }
    public Uri TsaUrl => new(Base, "tsa");

    /// <summary>Certificates the intermediate has revoked (serial → when).</summary>
    public Dictionary<string, DateTimeOffset> Revoked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int OcspRequests, CrlRequests, TsaRequests;
    private long _crlNumber;
    public bool Down { get; set; }

    public TestPki()
    {
        int port;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Base = new Uri($"http://127.0.0.1:{port}/");

        var now = DateTimeOffset.UtcNow;
        using var rootEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootReq = new CertificateRequest("CN=Washi Test Root, O=Washi Tests", rootEc, HashAlgorithmName.SHA256);
        Ca(rootReq);
        _rootKey = rootReq.CreateSelfSigned(now.AddYears(-2), now.AddYears(20));
        Root = X509CertificateLoader.LoadCertificate(_rootKey.RawData);

        using var intRsa = RSA.Create(2048);
        var intReq = new CertificateRequest("CN=Washi Test Issuing CA, O=Washi Tests", intRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Ca(intReq);
        intReq.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([new Uri(Base, "root.crl").ToString()]));
        Intermediate = Issue(intReq, _rootKey, now.AddYears(-1), now.AddYears(10));
        _intermediateKey = Intermediate.CopyWithPrivateKey(intRsa);

        using var tsaEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tsaReq = new CertificateRequest(new X500DistinguishedName("CN=Washi Test Time-Stamping, O=Washi Tests"), new PublicKey(tsaEc), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Leaf(tsaReq, new Oid("1.3.6.1.5.5.7.3.8"), critical: true);
        Tsa = Issue(tsaReq, _intermediateKey, now.AddDays(-30), now.AddYears(5));
        _tsaKey = Pfx(Tsa.CopyWithPrivateKey(tsaEc));

        _http.Prefixes.Add(Base.ToString());
        _http.Start();
        _loop = Task.Run(Serve);
    }

    /// <summary>A signer's digital ID issued by the intermediate, saved as a .p12 with the chain (as a CA delivers it) and opened.</summary>
    public DigitalId Id(string dir, string name = "山田 太郎")
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var dn = new X500DistinguishedNameBuilder();
        dn.AddCommonName(name);
        dn.AddOrganizationName("Washi Tests");
        var req = new CertificateRequest(dn.Build(), new PublicKey(ec), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Leaf(req, new Oid("1.3.6.1.5.5.7.3.4"), critical: false);
        var now = DateTimeOffset.UtcNow;
        using var cert = Issue(req, _intermediateKey, now.AddDays(-1), now.AddYears(1));
        using var withKey = cert.CopyWithPrivateKey(ec);
        var path = Path.Combine(dir, $"{name}-{cert.SerialNumber}.p12");
        File.WriteAllBytes(path, new X509Certificate2Collection { withKey, Intermediate, Root }.Export(X509ContentType.Pkcs12, "secret-1")!);
        return DigitalIds.FromFile(path, "secret-1");
    }

    private void Leaf(CertificateRequest req, Oid eku, bool critical)
    {
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([eku], critical));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension([new Uri(Base, "ocsp").ToString()], null));
        req.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([new Uri(Base, "issuing.crl").ToString()]));
    }

    private static void Ca(CertificateRequest req)
    {
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
    }

    private static X509Certificate2 Issue(CertificateRequest req, X509Certificate2 issuer, DateTimeOffset from, DateTimeOffset until)
    {
        var generator = issuer.GetECDsaPrivateKey() is { } ec ? X509SignatureGenerator.CreateForECDsa(ec)
                                                               : X509SignatureGenerator.CreateForRSA(issuer.GetRSAPrivateKey()!, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        return req.Create(issuer.SubjectName, generator, from, until, Serial());
    }

    private static byte[] Serial()
    {
        var s = RandomNumberGenerator.GetBytes(12);
        s[0] = (byte)(s[0] & 0x7F | 0x10);
        return s;
    }

    private static X509Certificate2 Pfx(X509Certificate2 c)
    {
        using (c) return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.Exportable);
    }

    // ------------------------------------------------------------------ the services

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            try
            {
                using var body = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(body);
                (byte[] Data, string Type)? answer = Down ? null : ctx.Request.Url!.AbsolutePath switch
                {
                    "/ocsp" => (Ocsp(body.ToArray()), "application/ocsp-response"),
                    "/root.crl" => (Crl(_rootKey, new()), "application/pkix-crl"),
                    "/issuing.crl" => (Crl(_intermediateKey, Revoked), "application/pkix-crl"),
                    "/tsa" => (Stamp(body.ToArray()), "application/timestamp-reply"),
                    _ => null,
                };
                if (answer is null) ctx.Response.StatusCode = Down ? 503 : 404;
                else
                {
                    ctx.Response.ContentType = answer.Value.Type;
                    ctx.Response.ContentLength64 = answer.Value.Data.Length;
                    await ctx.Response.OutputStream.WriteAsync(answer.Value.Data);
                }
            }
            catch (Exception) { ctx.Response.StatusCode = 500; }
            finally { ctx.Response.Close(); }
        }
    }

    private byte[] Crl(X509Certificate2 issuer, Dictionary<string, DateTimeOffset> revoked)
    {
        Interlocked.Increment(ref CrlRequests);
        var b = new CertificateRevocationListBuilder();
        foreach (var (serial, when) in revoked) b.AddEntry(Convert.FromHexString(serial), when);
        var now = DateTimeOffset.UtcNow;
        return issuer.GetRSAPrivateKey() is not null
            ? b.Build(issuer, Interlocked.Increment(ref _crlNumber), now.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, now)
            : b.Build(issuer, Interlocked.Increment(ref _crlNumber), now.AddDays(7), HashAlgorithmName.SHA256, null, now);
    }

    /// <summary>An OCSP response from the intermediate for every certificate asked about.</summary>
    private byte[] Ocsp(byte[] request)
    {
        Interlocked.Increment(ref OcspRequests);
        var tbsReq = new AsnReader(request, AsnEncodingRules.DER).ReadSequence().ReadSequence();
        while (tbsReq.PeekTag().TagClass == TagClass.ContextSpecific) tbsReq.ReadEncodedValue();
        var list = tbsReq.ReadSequence();
        var ids = new List<byte[]>();
        while (list.HasData) ids.Add(list.ReadSequence().ReadEncodedValue().ToArray());

        var now = DateTimeOffset.UtcNow;
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, true)))
                w.WriteOctetString(SHA1.HashData(Intermediate.PublicKey.EncodedKeyValue.RawData));
            w.WriteGeneralizedTime(now, omitFractionalSeconds: true);
            using (w.PushSequence())
                foreach (var id in ids)
                    using (w.PushSequence())
                    {
                        w.WriteEncodedValue(id);
                        var serial = Asn.CertId(new AsnReader(id, AsnEncodingRules.DER).ReadSequence()).Serial;
                        if (Revoked.TryGetValue(serial, out var when))
                            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true)))
                                w.WriteGeneralizedTime(when, omitFractionalSeconds: true);
                        else w.WriteNull(new Asn1Tag(TagClass.ContextSpecific, 0));
                        w.WriteGeneralizedTime(now, omitFractionalSeconds: true);   // thisUpdate: a live responder
                        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                            w.WriteGeneralizedTime(now.AddDays(7), omitFractionalSeconds: true);
                    }
        }
        var tbs = w.Encode();
        using var key = _intermediateKey.GetRSAPrivateKey()!;
        var sig = key.SignData(tbs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var basic = new AsnWriter(AsnEncodingRules.DER);
        using (basic.PushSequence())
        {
            basic.WriteEncodedValue(tbs);
            using (basic.PushSequence()) { basic.WriteObjectIdentifier("1.2.840.113549.1.1.11"); basic.WriteNull(); }
            basic.WriteBitString(sig);
        }
        var resp = new AsnWriter(AsnEncodingRules.DER);
        using (resp.PushSequence())
        {
            resp.WriteEncodedValue([0x0A, 0x01, 0x00]);   // successful
            using (resp.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (resp.PushSequence())
            {
                resp.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1.1");
                resp.WriteOctetString(basic.Encode());
            }
        }
        return resp.Encode();
    }

    /// <summary>An RFC 3161 time-stamp response.</summary>
    private byte[] Stamp(byte[] body)
    {
        Interlocked.Increment(ref TsaRequests);
        if (!Rfc3161TimestampRequest.TryDecode(body, out var req, out _)) throw new InvalidDataException("not a time-stamp request");
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.3.6.1.4.1.99999.1"), req.HashAlgorithmId, req.GetMessageHash(), Serial(),
                                                 DateTimeOffset.UtcNow, nonce: req.GetNonce());
        var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _tsaKey)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = req.RequestSignerCertificate ? X509IncludeOption.EndCertOnly : X509IncludeOption.None,
        };
        if (req.RequestSignerCertificate) { signer.Certificates.Add(Intermediate); signer.Certificates.Add(Root); }
        var v2 = new AsnWriter(AsnEncodingRules.DER);
        using (v2.PushSequence())
        using (v2.PushSequence())
        using (v2.PushSequence())
            v2.WriteOctetString(SHA256.HashData(Tsa.RawData));
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), v2.Encode()));
        cms.ComputeSignature(signer, silent: true);

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence()) w.WriteInteger(0);   // granted
            w.WriteEncodedValue(cms.Encode());
        }
        return w.Encode();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _http.Stop(); _http.Close(); } catch (ObjectDisposedException) { }
        try { _loop.Wait(2000); } catch (AggregateException) { }
        _rootKey.Dispose();
        _intermediateKey.Dispose();
        _tsaKey.Dispose();
    }
}
