// SPDX-License-Identifier: AGPL-3.0-or-later
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
    /// <summary>valid: intact, trusted and covering the document; changed: intact but revised later; unverified:
    /// intact but the identity is not trusted; invalid: the signed bytes were altered or the value does not check.</summary>
    public string Status => !Intact ? "invalid" : !Trusted ? "unverified" : !CoversDocument ? "changed" : "valid";
}

/// <summary>
/// Checks the digital signatures in a PDF: the byte range each covers, the CMS signature over it (PAdES and the
/// older adbe.pkcs7 forms), the signer's certificate chain against this computer's trusted roots (without going online),
/// the signing time (from a time-stamp when there is one), and whether the file was changed afterwards.
/// </summary>
public static class PdfSignatureVerifier
{
    public static IReadOnlyList<SignatureCheck> Verify(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var list = new List<SignatureCheck>();
        using var doc = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        var eofs = Regex.Matches(Encoding.Latin1.GetString(bytes), "%%EOF").Select(m => m.Index + 5).ToList();
        foreach (var (name, field, widget) in SignatureFields(doc))
        {
            if (Deref(field.Elements["/V"]) is not PdfDictionary sig) continue;
            var problems = new List<string>();
            var subFilter = sig.Elements.GetName("/SubFilter").TrimStart('/');
            string reason = Str(sig, "/Reason"), location = Str(sig, "/Location");
            int page = -1;
            PdfRect rect = default;
            for (int i = 0; i < doc.PageCount && widget is not null; i++)
                if (Deref(doc.Pages[i].Elements["/Annots"]) is PdfArray a && a.Elements.Any(e => ReferenceEquals(Deref(e), widget)))
                {
                    page = i;
                    if (Deref(widget.Elements["/Rect"]) is PdfArray r && r.Elements.Count >= 4)
                    {
                        var down = PdfRedactor.Upright(doc.Pages[i]).Inverse() ?? Mx.Identity;
                        var b = down.Bounds(new Box(Math.Min(FontMetrics.Num(r.Elements[0]), FontMetrics.Num(r.Elements[2])), Math.Min(FontMetrics.Num(r.Elements[1]), FontMetrics.Num(r.Elements[3])),
                                                    Math.Max(FontMetrics.Num(r.Elements[0]), FontMetrics.Num(r.Elements[2])), Math.Max(FontMetrics.Num(r.Elements[1]), FontMetrics.Num(r.Elements[3]))));
                        rect = new PdfRect(b.X0, b.Y0, b.X1, b.Y1);
                    }
                    break;
                }

            // the byte range, and the signature value in the hole it leaves
            long[] range = Deref(sig.Elements["/ByteRange"]) is PdfArray br && br.Elements.Count == 4
                ? [.. br.Elements.Select(e => (long)FontMetrics.Num(e))] : [];
            bool shape = range.Length == 4 && range[0] == 0 && range[1] > 0 && range[2] > range[1] && range[2] + range[3] <= bytes.Length && range[3] >= 0;
            byte[]? der = null;
            if (!shape) problems.Add("its byte range is not well formed");
            else
            {
                var hole = Encoding.Latin1.GetString(bytes, (int)range[1], (int)(range[2] - range[1])).Trim();
                if (!hole.StartsWith('<') || !hole.EndsWith('>')) problems.Add("its byte range does not leave out exactly the signature value");
                else der = Trim(Convert.FromHexString(hole[1..^1].Trim().Length % 2 == 0 ? hole[1..^1].Trim() : hole[1..^1].Trim() + "0"));
            }

            bool intact = false, trusted = false, stamped = false;
            DateTimeOffset? at = ParseDate(Str(sig, "/M"));
            string signer = Str(sig, "/Name"), issuer = "";
            if (der is not null)
            {
                var data = new byte[range[1] + range[3]];
                Buffer.BlockCopy(bytes, 0, data, 0, (int)range[1]);
                Buffer.BlockCopy(bytes, (int)range[2], data, (int)range[1], (int)range[3]);
                try
                {
                    SignedCms cms;
                    if (subFilter == "adbe.pkcs7.sha1")
                    {
                        cms = new SignedCms();
                        cms.Decode(der);
                        cms.CheckSignature(true);
                        intact = cms.ContentInfo.Content.AsSpan().SequenceEqual(SHA1.HashData(data));
                        if (!intact) problems.Add("the document's digest does not match the one signed");
                    }
                    else if (subFilter is "adbe.pkcs7.detached" or "ETSI.CAdES.detached")
                    {
                        cms = new SignedCms(new ContentInfo(data), detached: true);
                        cms.Decode(der);
                        cms.CheckSignature(true);
                        intact = true;
                    }
                    else { problems.Add($"signatures of the kind {subFilter} are not checked by Washi"); cms = null!; }

                    if (cms is not null && cms.SignerInfos.Count > 0)
                    {
                        var info = cms.SignerInfos[0];
                        var cert = info.Certificate;
                        if (cert is not null)
                        {
                            signer = cert.GetNameInfo(X509NameType.SimpleName, false);
                            issuer = cert.GetNameInfo(X509NameType.SimpleName, true);
                        }
                        // the time: a time-stamp's, else the signing-time attribute, else /M
                        foreach (var attr in info.UnsignedAttributes)
                            if (attr.Oid.Value == "1.2.840.113549.1.9.16.2.14" && attr.Values.Count > 0
                                && Rfc3161TimestampToken.TryDecode(attr.Values[0].RawData, out var token, out _))
                            {
                                at = token.TokenInfo.Timestamp;
                                stamped = token.VerifySignatureForSignerInfo(info, out _);
                                if (!stamped) problems.Add("its time-stamp does not match the signature");
                            }
                        if (!stamped)
                            foreach (var attr in info.SignedAttributes)
                                if (attr.Oid.Value == "1.2.840.113549.1.9.5" && attr.Values.Count > 0)
                                    at = new Pkcs9SigningTime(attr.Values[0].RawData).SigningTime;
                        if (cert is not null)
                        {
                            using var chain = new X509Chain();
                            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;   // offline: nothing is sent anywhere
                            chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
                            if (at is { } t) chain.ChainPolicy.VerificationTime = t.LocalDateTime;
                            trusted = chain.Build(cert);
                            if (!trusted)
                                problems.Add(cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData)
                                    ? "the signer's certificate is self-signed: its identity is not vouched for by anyone this computer trusts"
                                    : "the signer's certificate does not lead to a certificate this computer trusts ("
                                      + string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct()) + ")");
                        }
                    }
                }
                catch (CryptographicException e)
                {
                    intact = false;
                    problems.Add("the signature does not match the signed bytes: the document was altered, or the signature is damaged (" + e.Message + ")");
                }
            }

            int end = shape ? (int)(range[2] + range[3]) : 0;
            bool covers = shape && end == bytes.Length || shape && bytes.Length - end <= 2 && bytes.AsSpan(end).Trim((byte)'\n').Trim((byte)'\r').Length == 0;
            int after = covers ? 0 : eofs.Count(e => e > end + 2);
            if (!covers) problems.Add($"the document was changed after this signature ({after} later revision(s))");
            list.Add(new SignatureCheck(name, signer, issuer, at, stamped, reason, location, intact, trusted, covers, after, subFilter, page, rect, problems));
        }
        return list;
    }

    /// <summary>A DER value without the zeros that fill the rest of the space kept for it.</summary>
    private static byte[] Trim(byte[] b)
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

    private static IEnumerable<(string Name, PdfDictionary Field, PdfDictionary? Widget)> SignatureFields(PdfDocument doc)
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
            ft = node.Elements.GetName("/FT") is { Length: > 0 } own ? own : ft;
            var kids = (Deref(node.Elements["/Kids"]) as PdfArray)?.Elements.Select(Deref).OfType<PdfDictionary>().ToList() ?? [];
            if (kids.Any(k => k.Elements.ContainsKey("/T"))) { for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], full, ft)); continue; }
            if (ft == "/Sig") yield return (full, node, kids.Count > 0 ? kids[0] : node.Elements.ContainsKey("/Rect") ? node : null);
        }
    }

    private static string Str(PdfDictionary d, string key) => (Deref(d.Elements[key]) as PdfString)?.Value ?? "";

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

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
