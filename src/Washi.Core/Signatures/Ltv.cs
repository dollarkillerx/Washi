// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Pages;

namespace Washi.Core.Signatures;

/// <summary>What adding long-term validation did.</summary>
/// <param name="Signatures">Signatures in the file before.</param>
/// <param name="Certificates">Certificates added to the file.</param>
/// <param name="Ocsps">OCSP responses added.</param>
/// <param name="Crls">Revocation lists added.</param>
/// <param name="Timestamped">A document time-stamp was added.</param>
/// <param name="Ltv">The signatures (field names) that can now be checked long-term.</param>
/// <param name="NotLtv">The ones that cannot (something could not be fetched: see <paramref name="Notes"/>).</param>
public sealed record LtvResult(int Signatures, int Certificates, int Ocsps, int Crls, bool Timestamped,
                               IReadOnlyList<string> Ltv, IReadOnlyList<string> NotLtv, IReadOnlyList<string> Notes);

/// <summary>
/// Long-term validation (PAdES B-LT and B-LTA): adds to a signed PDF, as an incremental update that leaves the
/// signatures as they are, a Document Security Store (DSS) with every certificate of each signer's chain (and of the
/// time-stamping authorities') and an OCSP response or revocation list for each, fetched from the services the
/// certificates name. Optionally a document time-stamp follows, which keeps all of it provable after the algorithms
/// and certificates age; adding one again later renews it. Only certificate identifiers are sent to those services;
/// to a time-stamping service, only the hash of the file.
/// </summary>
public static class PdfLtv
{
    public static LtvResult AddValidation(string input, string output, Uri? timestampServer = null, IEnumerable<X509Certificate2>? trust = null)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        var (bytes, result) = Extend(File.ReadAllBytes(input), timestampServer, trust);
        File.WriteAllBytes(output, bytes);
        return result;
    }

    internal static (byte[] Bytes, LtvResult Result) Extend(byte[] bytes, Uri? tsa, IEnumerable<X509Certificate2>? trust)
    {
        var roots = trust?.ToList();
        var before = PdfSignatureVerifier.Verify(bytes, roots);
        if (before.Count == 0) throw new PageAssemblyException("the file has no digital signatures", "Err_LtvNone");
        if (before.FirstOrDefault(c => !c.Intact) is { } bad)
            throw new PageAssemblyException($"the signature '{bad.Field}' does not hold, so no validation data is added for it", "Err_LtvInvalid", bad.Field);

        var notes = new List<string>();
        var counts = new int[3];
        var (b, added) = WithDss(bytes, notes, counts, roots ?? []);
        bool stamped = false;
        if (tsa is not null)
        {
            b = PdfDigitalSigner.TimestampDocument(b, tsa);
            stamped = true;
            (b, _) = WithDss(b, notes, counts, roots ?? []);   // and the time-stamp's own chain
        }
        if (!added && !stamped) throw new PageAssemblyException("the file already holds all the validation data that could be found", "Err_LtvNothing");

        var after = PdfSignatureVerifier.Verify(b, roots);
        var old = before.ToDictionary(c => c.Field);
        var broke = after.FirstOrDefault(c => !c.Intact || old.TryGetValue(c.Field, out var o) && o.ChangesAllowed && !c.ChangesAllowed);
        var lost = before.FirstOrDefault(c => !after.Any(a => a.Field == c.Field));
        if (broke is not null || lost is not null)
            throw new PageAssemblyException("the signatures did not hold after adding validation data; nothing was kept ("
                                            + (broke is not null ? $"{broke.Field}: {string.Join(", ", broke.LaterChanges)}; {string.Join("; ", broke.Problems)}" : $"{lost!.Field} is gone")
                                            + ")", "Err_LtvCheck");
        var signatures = after.Where(c => old.ContainsKey(c.Field) || c.Kind == "timestamp").ToList();
        return (b, new LtvResult(before.Count, counts[0], counts[1], counts[2], stamped,
                                 [.. signatures.Where(c => c.Ltv).Select(c => c.Field)], [.. signatures.Where(c => !c.Ltv).Select(c => c.Field)],
                                 [.. notes.Distinct()]));
    }

    /// <summary>The file with a DSS holding what is still missing for its signatures (the same bytes when nothing is).</summary>
    private static (byte[] Bytes, bool Added) WithDss(byte[] bytes, List<string> notes, int[] counts, List<X509Certificate2> roots)
    {
        using var doc = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        var found = PdfSignatureVerifier.Read(doc, bytes);
        var have = Dss.Read(doc);
        var known = new ValidationData();
        foreach (var c in have.Certs) known.Add(c);
        foreach (var o in have.Ocsps) known.Add(o);
        foreach (var c in have.Crls) known.Add(c);
        foreach (var f in found) f.AddTo(known);
        var pool = known.Certs.ToList();
        var anchors = roots.Concat(ValidationData.SystemRoots()).ToList();

        var certs = new List<X509Certificate2>();
        var ocsps = new List<OcspResponse>();
        var crls = new List<Crl>();
        void Cert(X509Certificate2 c)
        {
            if (!have.Certs.Concat(certs).Any(x => x.RawData.AsSpan().SequenceEqual(c.RawData))) certs.Add(c);
            if (!pool.Any(x => x.RawData.AsSpan().SequenceEqual(c.RawData))) pool.Add(c);
        }

        foreach (var f in found.Where(f => f.Intact))
        {
            var leaves = new List<(X509Certificate2 Cert, DateTimeOffset At)>();
            if ((f.Timestamp ? f.TsaCert : f.Cert) is { } signer) leaves.Add((signer, f.At ?? DateTimeOffset.Now));
            if (!f.Timestamp && f.Stamped && f.TsaCert is { } tsa && f.Token is { } token) leaves.Add((tsa, token.TokenInfo.Timestamp));
            foreach (var (leaf, at) in leaves)
            {
                var chain = Chain(leaf, pool, anchors);
                for (int i = 0; i < chain.Count; i++)
                {
                    var c = chain[i];
                    Cert(c);
                    if (Asn.SelfSigned(c) || anchors.Any(a => a.RawData.AsSpan().SequenceEqual(c.RawData))) break;
                    if (i + 1 >= chain.Count) { notes.Add($"the issuer of {Name(c)} could not be found"); break; }
                    var issuer = chain[i + 1];
                    if (Asn.NoCheck(c)) continue;
                    var known2 = Merge(known, ocsps, crls);
                    if (known2.AnsweredSince(c, issuer, at)) continue;   // answered already, after the time it vouches for
                    if (RevocationClient.Ocsp(c, issuer, notes) is { } o)
                    {
                        if (!known2.Ocsps.Any(x => x.Der.AsSpan().SequenceEqual(o.Der))) ocsps.Add(o);
                        foreach (var rc in o.Certs) Cert(rc);
                        // a responder of its own needs no check when its certificate says so; otherwise its issuer's list
                        if (o.Responder(issuer, pool) is { } responder && !ReferenceEquals(responder, issuer) && !Asn.NoCheck(responder)
                            && !known2.AnsweredSince(responder, issuer, at)
                            && RevocationClient.Crl(responder, issuer, notes) is { } rcrl && !known2.Crls.Any(x => x.Der.AsSpan().SequenceEqual(rcrl.Der)))
                            crls.Add(rcrl);
                        continue;
                    }
                    if (RevocationClient.Crl(c, issuer, notes) is { } crl)
                    {
                        if (!known2.Crls.Any(x => x.Der.AsSpan().SequenceEqual(crl.Der))) crls.Add(crl);
                        continue;
                    }
                    if (known2.Status(c, issuer, at).State == RevocationState.Unknown) notes.Add($"no revocation information could be fetched for {Name(c)}");
                }
            }
        }
        if (certs.Count + ocsps.Count + crls.Count == 0) return (bytes, false);

        // the update: one stream per item, the DSS (added to the one already there), the catalog pointing at it
        var update = new IncrementalUpdate(bytes);
        int highest = doc.Internals.GetAllObjects().Select(o => o.Reference?.ObjectNumber ?? 0).DefaultIfEmpty(0).Max();
        int Stream(byte[] der)
        {
            int n = update.NewNumber(highest);
            var head = Encoding.ASCII.GetBytes($"<</Length {der.Length}>>\nstream\n");
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            update.Put(n, 0, [.. head, .. der, .. tail]);
            return n;
        }
        var newCerts = certs.Select(c => Stream(c.RawData)).ToList();
        var newOcsps = ocsps.Select(o => Stream(o.Der)).ToList();
        var newCrls = crls.Select(c => Stream(c.Der)).ToList();
        counts[0] += certs.Count; counts[1] += ocsps.Count; counts[2] += crls.Count;

        var catalog = doc.Internals.Catalog;
        var old = Deref(catalog.Elements["/DSS"]) as PdfDictionary;
        var dss = new StringBuilder("<</Type/DSS");
        void List(string key, List<int> added)
        {
            var items = (Deref(old?.Elements[key]) as PdfArray)?.Elements.Select(PdfSyntax.Write).ToList() ?? [];
            items.AddRange(added.Select(n => $"{n} 0 R"));
            if (items.Count > 0) dss.Append(key).Append('[').Append(string.Join(" ", items)).Append(']');
        }
        List("/Certs", newCerts);
        List("/OCSPs", newOcsps);
        List("/CRLs", newCrls);
        if (old is not null)
            foreach (var key in old.Elements.Keys.Where(k => k is not ("/Type" or "/Certs" or "/OCSPs" or "/CRLs")))
                dss.Append(PdfSyntax.Name(key)).Append(' ').Append(PdfSyntax.Write(old.Elements[key]));
        dss.Append(">>");
        if (catalog.Elements["/DSS"] is PdfReference dr) update.Put(dr.ObjectNumber, dr.GenerationNumber, dss.ToString());
        else
        {
            int n = update.NewNumber(highest);
            update.Put(n, 0, dss.ToString());
            catalog.Elements["/DSS"] = new PdfLiteral($"{n} 0 R");
            // ETSI's extension level for PDF 1.7 readers (PAdES); PDF 2.0 has DSS of its own
            if (doc.Version < 20 && !catalog.Elements.ContainsKey("/Extensions"))
                catalog.Elements["/Extensions"] = new PdfLiteral("<</ESIC<</BaseVersion/1.7/ExtensionLevel 5>>>>");
            var r = catalog.Reference ?? throw new PageAssemblyException("the file has no catalog", "Err_SignDamaged");
            update.Put(r.ObjectNumber, r.GenerationNumber, PdfSyntax.Write(catalog));
        }
        return (update.Build(), true);
    }

    private static ValidationData Merge(ValidationData known, List<OcspResponse> ocsps, List<Crl> crls)
    {
        var v = new ValidationData();
        foreach (var c in known.Certs) v.Add(c);
        foreach (var o in known.Ocsps.Concat(ocsps)) v.Add(o);
        foreach (var c in known.Crls.Concat(crls)) v.Add(c);
        return v;
    }

    /// <summary>The chain from the certificates at hand, and failing that as the system builds it (which may fetch
    /// missing intermediate certificates from the addresses the certificates name).</summary>
    private static List<X509Certificate2> Chain(X509Certificate2 leaf, List<X509Certificate2> pool, IReadOnlyList<X509Certificate2> anchors)
    {
        var own = ValidationData.Chain(leaf, pool, anchors, out bool complete);
        if (complete) return own;
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.ChainPolicy.ExtraStore.AddRange(pool.ToArray());
        chain.Build(leaf);
        var system = chain.ChainElements.Select(e => e.Certificate).ToList();
        return system.Count > own.Count ? system : own;
    }

    private static string Name(X509Certificate2 c) => c.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } n ? n : c.Subject;

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}

/// <summary>Reads a file's Document Security Store: the certificates, OCSP responses and revocation lists it keeps.</summary>
internal static class Dss
{
    public static ValidationData Read(PdfDocument doc)
    {
        var data = new ValidationData();
        if (Deref(doc.Internals.Catalog.Elements["/DSS"]) is not PdfDictionary dss) return data;
        var dicts = new List<PdfDictionary> { dss };
        if (Deref(dss.Elements["/VRI"]) is PdfDictionary vri)
            dicts.AddRange(vri.Elements.Keys.Select(k => Deref(vri.Elements[k])).OfType<PdfDictionary>());
        foreach (var d in dicts)
        {
            foreach (var der in Streams(d, "/Certs").Concat(Streams(d, "/Cert")))
                try { data.Add(X509CertificateLoader.LoadCertificate(der)); }
                catch (System.Security.Cryptography.CryptographicException) { }
            foreach (var der in Streams(d, "/OCSPs").Concat(Streams(d, "/OCSP")))
                if (OcspResponse.Parse(der) is { } o) data.Add(o);
            foreach (var der in Streams(d, "/CRLs").Concat(Streams(d, "/CRL")))
                if (Crl.Parse(der) is { } c) data.Add(c);
        }
        return data;
    }

    private static IEnumerable<byte[]> Streams(PdfDictionary d, string key)
    {
        if (Deref(d.Elements[key]) is not PdfArray a) yield break;
        foreach (var e in a.Elements)
            if (Deref(e) is PdfDictionary { Stream: { } s })
            {
                byte[]? bytes;
                try { bytes = s.UnfilteredValue; }
                catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException) { bytes = null; }
                if (bytes is { Length: > 0 }) yield return bytes;
            }
    }

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
