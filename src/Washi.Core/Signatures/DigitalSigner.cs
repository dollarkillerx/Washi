// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Forms;
using Washi.Core.Localization;
using Washi.Core.Pages;

namespace Washi.Core.Signatures;

/// <summary>What to sign with and where.</summary>
/// <param name="Field">An empty signature field to sign (its name); otherwise a new field is made.</param>
/// <param name="Page">The page of a new visible signature (0-based).</param>
/// <param name="Rect">Where a new visible signature goes (upright top-down points); null for an invisible signature.</param>
/// <param name="Ink">A hand-drawn signature shown in the signature's box, next to the signer's name.</param>
/// <param name="TimestampServer">An RFC 3161 time-stamping service. The signature's hash is sent to it (nothing else).</param>
/// <param name="Certify">0 for an ordinary (approval) signature; 1-3 to certify the document as its author, allowing
/// afterwards 1 no changes, 2 filling in forms and signing, 3 also comments. Only the first signature can certify.</param>
/// <param name="Ltv">Add long-term validation data after signing: the certificates and revocation answers (OCSP, CRL)
/// fetched from the services the certificates name, so the signature can be checked offline for years.</param>
public sealed record SignRequest(DigitalId Id, string? Field = null, int Page = 0, PdfRect? Rect = null, string Reason = "",
                                 string Location = "", Ink? Ink = null, Uri? TimestampServer = null, int Certify = 0, bool Ltv = false);

public sealed record SignResult(string Field, string Signer, DateTimeOffset At, bool Timestamped)
{
    /// <summary>The file holds what it takes to check this signature long-term (when <see cref="SignRequest.Ltv"/> was asked).</summary>
    public bool Ltv { get; init; }

    /// <summary>What could not be fetched for long-term validation.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Signs a PDF with a digital ID: a PAdES signature (CAdES, detached, SHA-256, with the signing certificate bound
/// into it), written as an incremental update so that everything before it, earlier signatures included, stays as it
/// was. The signature covers the whole file except its own value. A visible signature shows the signer's name, the
/// date, the reason and place, and the hand-drawn signature if one is given. The first signature can certify the
/// document (DocMDP), and validation data can follow it (PAdES B-LT).
/// </summary>
public static class PdfDigitalSigner
{
    /// <summary>Bytes kept for the signature value (certificates and a time-stamp included).</summary>
    private const int Reserve = 24 * 1024;
    private const string ByteRangeHole = "/ByteRange [0 0000000000 0000000000 0000000000]";

    public static SignResult Sign(string input, string output, SignRequest r)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        if (!r.Id.Certificate.HasPrivateKey) throw new PageAssemblyException("the digital ID has no private key", "Err_IdNoKey", r.Id.Name);
        if (r.Certify is < 0 or > 3) throw new PageAssemblyException("certification allows 1 (no changes), 2 (forms and signing) or 3 (also comments)", "Err_CertifyLevel");
        var original = File.ReadAllBytes(input);
        var now = DateTimeOffset.Now;

        _ = new IncrementalUpdate(original);   // refuses protected and damaged files
        using (var doc = PdfReader.Open(new MemoryStream(original), PdfDocumentOpenMode.Import))
        {
            if (PdfSignatureVerifier.Certification(doc, out _) == 1)
                throw new PageAssemblyException("the document is certified with no changes allowed, so it cannot be signed again", "Err_SignCertifiedLocked");
            if (r.Certify > 0 && PdfSignatureVerifier.SignatureFields(doc).Any(f => Deref(f.Field.Elements["/V"]) is PdfDictionary))
                throw new PageAssemblyException("only the first signature can certify a document, and this one is already signed", "Err_CertifyNotFirst");
        }

        bool stamped = false;
        var (bytes, fieldName) = Place(original, r.Field, r.Page, r.Rect, "Signature", SignatureDictionary(r, now),
                                       (w, h, rotate) => Appearance(r, w, h, rotate, now), r.Certify,
                                       signed => { var (der, s) = Cms(signed, r.Id, r.TimestampServer); stamped = s; return der; });

        // proof: the copy's signature checks out
        var check = PdfSignatureVerifier.Verify(bytes).LastOrDefault(s => s.Field == fieldName);
        if (check is not { Intact: true, CoversDocument: true })
            throw new PageAssemblyException("the signed copy did not verify; it was not kept", "Err_SignCheck");

        bool ltv = false;
        IReadOnlyList<string> notes = [];
        if (r.Ltv)
        {
            var (extended, result) = PdfLtv.Extend(bytes, null, null);
            bytes = extended;
            ltv = result.Ltv.Contains(fieldName);
            notes = result.Notes;
        }
        File.WriteAllBytes(output, bytes);
        return new SignResult(fieldName, r.Id.Name, now, stamped) { Ltv = ltv, Notes = notes };
    }

    /// <summary>
    /// Adds a document time-stamp (PAdES, ETSI.RFC3161): the time-stamping service signs the hash of the whole file as
    /// it is, in an invisible signature field of its own.
    /// </summary>
    internal static byte[] TimestampDocument(byte[] original, Uri tsa)
    {
        var dict = new StringBuilder("<</Type/DocTimeStamp/Filter/Adobe.PPKLite/SubFilter/ETSI.RFC3161 ");
        dict.Append(ByteRangeHole).Append("/Contents <").Append('0', Reserve * 2).Append(">>>");
        return Place(original, null, 0, null, "Timestamp", dict.ToString(), null, 0, signed =>
        {
            var request = Rfc3161TimestampRequest.CreateFromData(signed, HashAlgorithmName.SHA256, requestSignerCertificates: true,
                                                                 nonce: RandomNumberGenerator.GetBytes(8));
            return Stamp(request, tsa).AsSignedCms().Encode();
        }).Bytes;
    }

    /// <summary>
    /// Adds a signature field (or fills an empty one) whose value is <paramref name="value"/> of the bytes it covers:
    /// the update is built with a hole for the value, the byte range around the hole is filled in, and the value
    /// computed over everything else is written into the hole.
    /// </summary>
    private static (byte[] Bytes, string Field) Place(byte[] original, string? wanted, int page, PdfRect? box, string prefix, string sigDict,
                                                      Func<double, double, int, string>? appearance, int certify, Func<byte[], byte[]> value)
    {
        var update = new IncrementalUpdate(original);
        string fieldName;

        using (var doc = PdfReader.Open(new MemoryStream(original), PdfDocumentOpenMode.Import))
        {
            int highest = doc.Internals.GetAllObjects().Select(o => o.Reference?.ObjectNumber ?? 0).DefaultIfEmpty(0).Max();
            int New() => update.NewNumber(highest);
            int sig = New();
            update.Put(sig, 0, sigDict);
            var catalog = doc.Internals.Catalog;
            bool catalogChanged = false;

            // where it goes: an empty field, or a new one
            PdfDictionary? field = null, widget = null;
            var form = Deref(catalog.Elements["/AcroForm"]) as PdfDictionary;
            if (wanted is { Length: > 0 })
            {
                (field, widget) = FindField(doc, wanted) ?? throw new PageAssemblyException($"there is no empty signature field '{wanted}'", "Err_SignNoField", wanted);
                if (Deref(field.Elements["/V"]) is not null) throw new PageAssemblyException($"the field '{wanted}' is already signed", "Err_SignFieldSigned", wanted);
                fieldName = wanted;
                page = PageOf(doc, widget) ?? 0;
            }
            else
            {
                if (page < 0 || page >= doc.PageCount) throw new PageAssemblyException($"page {page + 1} is outside 1-{doc.PageCount}", "Err_RangeOutside", (page + 1).ToString(CultureInfo.InvariantCulture), doc.PageCount);
                var names = new HashSet<string>(AcroFormFields.List(doc).Select(f => f.Name), StringComparer.Ordinal);
                int n = 1;
                while (names.Contains(prefix + n)) n++;
                fieldName = prefix + n;
            }

            // the appearance
            var pageDict = doc.Pages[page];
            PdfRect pdfRect = default;
            int? ap = null;
            var rectItem = widget is not null ? Deref(widget.Elements["/Rect"]) as PdfArray : null;
            if (rectItem is { Elements.Count: >= 4 })
                pdfRect = new PdfRect(Math.Min(FontMetricsNum(rectItem, 0), FontMetricsNum(rectItem, 2)), Math.Min(FontMetricsNum(rectItem, 1), FontMetricsNum(rectItem, 3)),
                                      Math.Max(FontMetricsNum(rectItem, 0), FontMetricsNum(rectItem, 2)), Math.Max(FontMetricsNum(rectItem, 1), FontMetricsNum(rectItem, 3)));
            else if (box is { } up)
            {
                var b = PdfRedactor.Upright(pageDict).Bounds(new Box(up.X0, up.Y0, up.X1, up.Y1));
                pdfRect = new PdfRect(b.X0, b.Y0, b.X1, b.Y1);
            }
            if (appearance is not null && pdfRect.Width >= 4 && pdfRect.Height >= 4)
            {
                ap = New();
                int rotate = ((pageDict.Rotate % 360) + 360) % 360;
                double w = rotate is 90 or 270 ? pdfRect.Height : pdfRect.Width, h = rotate is 90 or 270 ? pdfRect.Width : pdfRect.Height;
                update.Put(ap.Value, 0, appearance(w, h, rotate));
            }

            string rectText = $"[{PdfSyntax.Num(pdfRect.X0)} {PdfSyntax.Num(pdfRect.Y0)} {PdfSyntax.Num(pdfRect.X1)} {PdfSyntax.Num(pdfRect.Y1)}]";
            string apText = ap is { } a ? $"/AP<</N {a} 0 R>>" : "";
            if (field is not null)
            {
                // the empty field takes the signature (and its widget the appearance)
                field.Elements["/V"] = new PdfLiteral($"{sig} 0 R");
                if (ReferenceEquals(field, widget))
                {
                    if (ap is { } a2) widget.Elements["/AP"] = new PdfLiteral($"<</N {a2} 0 R>>");
                    widget.Elements["/F"] = new PdfInteger(4);
                    Rewrite(update, field);
                }
                else
                {
                    Rewrite(update, field);
                    if (ap is { } a3) { widget!.Elements["/AP"] = new PdfLiteral($"<</N {a3} 0 R>>"); Rewrite(update, widget); }
                }
            }
            else
            {
                int w = New();
                update.Put(w, 0, $"<</Type/Annot/Subtype/Widget/FT/Sig/T {PdfSyntax.Text(fieldName)}/F 4/P {Ref(pageDict)}/Rect {rectText}/V {sig} 0 R{apText}>>");
                // on its page
                if (pageDict.Elements["/Annots"] is PdfReference annotsRef && annotsRef.Value is PdfArray annotsArr)
                {
                    annotsArr.Elements.Add(new PdfLiteral($"{w} 0 R"));
                    update.Put(annotsRef.ObjectNumber, annotsRef.GenerationNumber, PdfSyntax.Write(annotsArr));
                }
                else
                {
                    var annots = pageDict.Elements["/Annots"] as PdfArray ?? new PdfArray(doc);
                    annots.Elements.Add(new PdfLiteral($"{w} 0 R"));
                    pageDict.Elements["/Annots"] = annots;
                    Rewrite(update, pageDict);
                }
                // in the form
                if (form is null)
                {
                    int f = New();
                    update.Put(f, 0, $"<</Fields[{w} 0 R]/SigFlags 3>>");
                    catalog.Elements["/AcroForm"] = new PdfLiteral($"{f} 0 R");
                    catalogChanged = true;
                }
                else if (form.Elements["/Fields"] is PdfReference fr && fr.Value is PdfArray fieldsArr)
                {
                    fieldsArr.Elements.Add(new PdfLiteral($"{w} 0 R"));
                    update.Put(fr.ObjectNumber, fr.GenerationNumber, PdfSyntax.Write(fieldsArr));
                }
                else
                {
                    var fields = form.Elements["/Fields"] as PdfArray ?? new PdfArray(doc);
                    fields.Elements.Add(new PdfLiteral($"{w} 0 R"));
                    form.Elements["/Fields"] = fields;
                }
            }
            if (form is not null)
            {
                form.Elements["/SigFlags"] = new PdfInteger(3);
                if (form.Reference is not null) Rewrite(update, form);
                else catalogChanged = true;
            }
            if (certify > 0)
            {
                // the document's permissions point at this signature
                var perms = Deref(catalog.Elements["/Perms"]) as PdfDictionary;
                var keep = perms is null ? "" : string.Concat(perms.Elements.Keys.Where(k => k != "/DocMDP").Select(k => PdfSyntax.Name(k) + " " + PdfSyntax.Write(perms.Elements[k]) + " "));
                catalog.Elements["/Perms"] = new PdfLiteral($"<<{keep}/DocMDP {sig} 0 R>>");
                catalogChanged = true;
            }
            if (catalogChanged) Rewrite(update, catalog);
        }

        // the file with the hole for the value, then the value over everything else
        var bytes = update.Build();
        var text = Encoding.Latin1.GetString(bytes);
        int br = text.LastIndexOf(ByteRangeHole, StringComparison.Ordinal);
        int open = text.LastIndexOf("/Contents <" + new string('0', 64), StringComparison.Ordinal);
        if (br < original.Length || open < original.Length) throw new PageAssemblyException("the signature could not be placed", "Err_SignDamaged");
        int start = open + "/Contents ".Length, end = text.IndexOf('>', start) + 1;
        long[] range = [0, start, end, bytes.Length - end];
        var rangeText = $"/ByteRange [0 {range[1]:D10} {range[2]:D10} {range[3]:D10}]";
        if (rangeText.Length != ByteRangeHole.Length) throw new PageAssemblyException("the file is too large to sign", "Err_SignDamaged");
        Encoding.ASCII.GetBytes(rangeText).CopyTo(bytes, br);
        var signed = new byte[start + (bytes.Length - end)];
        Buffer.BlockCopy(bytes, 0, signed, 0, start);
        Buffer.BlockCopy(bytes, end, signed, start, bytes.Length - end);

        var hex = Convert.ToHexString(value(signed));
        if (hex.Length > end - start - 2) throw new PageAssemblyException("the signature is larger than the room kept for it", "Err_SignDamaged");
        Encoding.ASCII.GetBytes(hex).CopyTo(bytes, start + 1);
        return (bytes, fieldName);
    }

    // ------------------------------------------------------------------ pieces

    private static string SignatureDictionary(SignRequest r, DateTimeOffset now)
    {
        var sb = new StringBuilder("<</Type/Sig/Filter/Adobe.PPKLite/SubFilter/ETSI.CAdES.detached ");
        sb.Append(ByteRangeHole);
        sb.Append("/Contents <").Append('0', Reserve * 2).Append('>');
        sb.Append("/M ").Append(PdfSyntax.Text(PdfDate(now)));
        sb.Append("/Name ").Append(PdfSyntax.Text(r.Id.Name));
        if (r.Reason.Trim().Length > 0) sb.Append("/Reason ").Append(PdfSyntax.Text(r.Reason.Trim()));
        if (r.Location.Trim().Length > 0) sb.Append("/Location ").Append(PdfSyntax.Text(r.Location.Trim()));
        if (r.Certify > 0)
            sb.Append(CultureInfo.InvariantCulture, $"/Reference[<</Type/SigRef/TransformMethod/DocMDP/TransformParams<</Type/TransformParams/P {r.Certify}/V/1.2>>>>]");
        sb.Append("/Prop_Build<</App<</Name/Washi>>>>>>");
        return sb.ToString();
    }

    internal static string PdfDate(DateTimeOffset t)
    {
        var o = t.Offset;
        return "D:" + t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
               + (o == TimeSpan.Zero ? "Z" : (o < TimeSpan.Zero ? "-" : "+") + $"{Math.Abs(o.Hours):D2}'{Math.Abs(o.Minutes):D2}'");
    }

    /// <summary>The signature value: CMS SignedData over <paramref name="data"/>, detached, SHA-256, with the
    /// ESS signing-certificate-v2 attribute (PAdES), optionally time-stamped.</summary>
    private static (byte[] Der, bool Stamped) Cms(byte[] data, DigitalId id, Uri? tsa)
    {
        var cert = id.Certificate;
        var cms = new SignedCms(new ContentInfo(data), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),   // SHA-256
            IncludeOption = X509IncludeOption.WholeChain,
        };
        foreach (var c in id.Chain)
            if (!signer.Certificates.Contains(c) && !c.RawData.AsSpan().SequenceEqual(cert.RawData)) signer.Certificates.Add(c);
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), SigningCertificateV2(cert)));
        cms.ComputeSignature(signer, silent: true);
        bool stamped = false;
        if (tsa is not null)
        {
            var info = cms.SignerInfos[0];
            var request = Rfc3161TimestampRequest.CreateFromSignerInfo(info, HashAlgorithmName.SHA256, requestSignerCertificates: true,
                                                                       nonce: RandomNumberGenerator.GetBytes(8));
            info.AddUnsignedAttribute(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.14"), Stamp(request, tsa).AsSignedCms().Encode()));
            stamped = true;
        }
        return (cms.Encode(), stamped);
    }

    /// <summary>Asks the time-stamping service for a token (RFC 3161 over HTTP); only the hash in the request is sent.</summary>
    private static Rfc3161TimestampToken Stamp(Rfc3161TimestampRequest request, Uri tsa)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var content = new ByteArrayContent(request.Encode());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
        byte[] body;
        try
        {
            using var response = http.Send(new HttpRequestMessage(HttpMethod.Post, tsa) { Content = content });
            body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new PageAssemblyException($"the time-stamping service could not be reached: {e.Message}", "Err_SignTimestamp", tsa.Host);
        }
        try { return request.ProcessResponse(body, out _); }
        catch (CryptographicException e) { throw new PageAssemblyException($"the time-stamping service gave no valid time-stamp: {e.Message}", "Err_SignTimestamp", tsa.Host); }
    }

    /// <summary>SigningCertificateV2 ::= SEQUENCE { certs SEQUENCE OF ESSCertIDv2 { certHash (SHA-256) } } (RFC 5035).</summary>
    private static byte[] SigningCertificateV2(X509Certificate2 cert)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        using (w.PushSequence())
        using (w.PushSequence())
            w.WriteOctetString(SHA256.HashData(cert.RawData));
        return w.Encode();
    }

    /// <summary>
    /// The visible signature, drawn upright in a w × h box (turned with the page): the hand-drawn signature on the left
    /// when there is one, then the signer's name and "signed by … / date / reason / location".
    /// </summary>
    private static string Appearance(SignRequest r, double w, double h, int rotate, DateTimeOffset now)
    {
        var lines = new List<(string Text, bool Big)> { (r.Id.Name, true), (L.F("Sig_SignedBy", r.Id.Name), false),
                                                        (L.F("Sig_Date", now.ToString("yyyy.MM.dd HH:mm:ss zzz", CultureInfo.InvariantCulture)), false) };
        if (r.Reason.Trim().Length > 0) lines.Add((L.F("Sig_Reason", r.Reason.Trim()), false));
        if (r.Location.Trim().Length > 0) lines.Add((L.F("Sig_Location", r.Location.Trim()), false));
        var cjk = FormScripts.Detect(string.Join(" ", lines.Select(l => l.Text))) is { } s && s != FormScript.Latin ? s : (FormScript?)null;
        bool anyWide = lines.Any(l => l.Text.Any(c => c > 0xFF));
        var script = cjk ?? (anyWide ? FormScript.Japanese : FormScript.Latin);

        var body = new StringBuilder();
        double textX = 2;
        if (r.Ink is { IsEmpty: false } ink)
        {
            // the hand-drawn signature fills the left part (drawn top-down, as Fill & Sign does)
            double iw = w * 0.42;
            body.Append($"q 1 0 0 -1 0 {PdfSyntax.Num(h)} cm\n");
            PdfSigner.Draw(body, new SignatureStamp(0, new PdfRect(2, 2, iw - 2, h - 2), ink));
            body.Append("Q\n");
            textX = iw + 2;
        }
        double room = Math.Max(10, w - textX - 2);
        double big = Math.Clamp(h / (lines.Count + 1.2), 4, 16), small = Math.Clamp(big * 0.62, 3.5, 9);
        double Em(string t, double size) => t.Sum(c => c > 0xFF ? 1.0 : c is >= 'A' and <= 'Z' ? 0.67 : 0.52) * size;
        // shrink until every line fits its width
        foreach (var (t, b) in lines)
        {
            double size = b ? big : small, width = Em(t, size);
            if (width > room) { double k = room / width; big *= b ? k : 1; small *= b ? 1 : k; }
        }
        double y = h - 2 - big;
        body.Append("BT 0 g\n");
        foreach (var (t, b) in lines)
        {
            double size = b ? big : small;
            bool wide = t.Any(c => c > 0xFF);
            string font = wide ? "/F2" : "/F1";
            string str = wide ? "<" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(t)) + ">"
                              : "<" + Convert.ToHexString(Encoding.Latin1.GetBytes(t)) + ">";
            body.Append(CultureInfo.InvariantCulture, $"{font} {PdfSyntax.Num(size)} Tf 1 0 0 1 {PdfSyntax.Num(textX)} {PdfSyntax.Num(Math.Max(1, y))} Tm {str} Tj\n");
            y -= (b ? big : small) * 1.25;
        }
        body.Append("ET");

        var (cidFont, encoding, ordering, supplement) = AcroFormWriter.CidSpec(script == FormScript.Latin ? FormScript.Japanese : script);
        string matrix = rotate switch { 90 => "[0 1 -1 0 0 0]", 180 => "[-1 0 0 -1 0 0]", 270 => "[0 -1 1 0 0 0]", _ => "[1 0 0 1 0 0]" };
        var content = Encoding.Latin1.GetBytes(body.ToString());
        string fonts = "<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>"
                       + $"/F2<</Type/Font/Subtype/Type0/BaseFont/{cidFont}/Encoding/{encoding}/DescendantFonts[<</Type/Font/Subtype/CIDFontType0/BaseFont/{cidFont}"
                       + $"/CIDSystemInfo<</Registry(Adobe)/Ordering({ordering})/Supplement {supplement}>>/DW 1000/W[1 95 500]"
                       + $"/FontDescriptor<</Type/FontDescriptor/FontName/{cidFont}/Flags 6/FontBBox[-200 -331 1100 1000]/ItalicAngle 0/Ascent 880/Descent -120/CapHeight 740/StemV 80>>>>]>>>>";
        return $"<</Type/XObject/Subtype/Form/BBox[0 0 {PdfSyntax.Num(w)} {PdfSyntax.Num(h)}]/Matrix {matrix}/Resources<</Font {fonts}>>/Length {content.Length}>>\nstream\n"
               + Encoding.Latin1.GetString(content) + "\nendstream";
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An empty signature field named <paramref name="name"/>: the field and its (first) widget.</summary>
    private static (PdfDictionary Field, PdfDictionary Widget)? FindField(PdfDocument doc, string name)
    {
        if (Deref(doc.Internals.Catalog.Elements["/AcroForm"]) is not PdfDictionary form || Deref(form.Elements["/Fields"]) is not PdfArray roots) return null;
        var stack = new Stack<(PdfDictionary, string)>();
        foreach (var r in roots.Elements) if (Deref(r) is PdfDictionary d) stack.Push((d, ""));
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var (node, parent) = stack.Pop();
            if (!seen.Add(node)) continue;
            var t = (Deref(node.Elements["/T"]) as PdfString)?.Value ?? "";
            var full = t.Length == 0 ? parent : parent.Length == 0 ? t : parent + "." + t;
            var kids = (Deref(node.Elements["/Kids"]) as PdfArray)?.Elements.Select(Deref).OfType<PdfDictionary>().ToList() ?? [];
            if (kids.Any(k => k.Elements.ContainsKey("/T"))) { foreach (var k in kids) stack.Push((k, full)); continue; }
            if (full != name) continue;
            for (var n = node; n is not null; n = Deref(n.Elements["/Parent"]) as PdfDictionary)
                if (n.Elements.GetName("/FT") is { Length: > 0 } ft)
                {
                    if (ft != "/Sig") return null;
                    break;
                }
            return (node, kids.Count > 0 ? kids[0] : node);
        }
        return null;
    }

    private static int? PageOf(PdfDocument doc, PdfDictionary widget)
    {
        for (int i = 0; i < doc.PageCount; i++)
            if (Deref(doc.Pages[i].Elements["/Annots"]) is PdfArray a && a.Elements.Any(e => ReferenceEquals(Deref(e), widget))) return i;
        return null;
    }

    private static void Rewrite(IncrementalUpdate update, PdfDictionary d)
    {
        var r = d.Reference ?? throw new PageAssemblyException("an object to change is not an object of its own", "Err_SignDamaged");
        update.Put(r.ObjectNumber, r.GenerationNumber, PdfSyntax.Write(d));
    }

    private static string Ref(PdfDictionary d) => d.Reference is { } r ? $"{r.ObjectNumber} {r.GenerationNumber} R" : "null";

    private static double FontMetricsNum(PdfArray a, int i) => FontMetrics.Num(a.Elements[i]);

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
