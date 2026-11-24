// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Core.Signatures;

namespace Washi.Core.Pages;

/// <summary>Who a file encrypted for certificates can be opened by: each recipient's certificate issuer and serial number.</summary>
public sealed record CertificateRecipient(string Issuer, string Serial);

/// <summary>
/// Certificate security (PDF 32000-2 7.6.5, Adobe.PubSec, adbe.pkcs7.s5, AES-256): a copy that only the holders of
/// the given certificates can open, with their private key (a digital ID), instead of a shared password. The file key
/// is wrapped for each recipient in a CMS envelope (RSA, AES-256), as Acrobat writes and reads it. Washi opens such a
/// file with a digital ID by writing a copy without the protection.
/// </summary>
public static partial class PdfCertificateSecurity
{
    /// <summary>
    /// What the recipients may do, in their envelopes: everything, including changing the encryption (bit 2, which in
    /// a public-key handler means full access) and bit 1 as Acrobat sets it (PDF 32000-2, table 24).
    /// </summary>
    private const uint Permissions = 0xFFFFFFFF;

    /// <summary>/P of the standard handler used to open the file: everything (its bits 1 and 2 must be 0).</summary>
    private const int StandardP = -4;

    /// <summary>
    /// Writes a copy of <paramref name="input"/> that opens only with the private key of one of
    /// <paramref name="recipients"/> (RSA certificates). Include your own certificate to be able to open it yourself.
    /// <paramref name="password"/> opens an input that is protected with a password. Signed files are refused.
    /// </summary>
    public static void Encrypt(string input, string output, IReadOnlyList<X509Certificate2> recipients, string? password = null)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        if (recipients.Count == 0) throw new PageAssemblyException("name at least one certificate that may open the file", "Err_CertNoRecipients");
        foreach (var c in recipients)
            if (c.GetRSAPublicKey() is null)
                throw new PageAssemblyException($"the certificate of {Name(c)} is not an RSA certificate, which certificate security needs", "Err_CertNotRsa", Name(c));
        if (IsEncryptedForCertificates(input)) throw new PageAssemblyException($"{Path.GetFileName(input)} is already encrypted for certificates", "Err_CertAlready", Path.GetFileName(input));

        byte[] plain;
        using (var d = PdfSecurity.OpenForChanges(input, password ?? PdfPasswords.For(input)))
        {
            if (PdfSignatureVerifier.SignatureFields(d).Any(f => f.Field.Elements["/V"] is not null))
                throw new PageAssemblyException("the file is signed: encrypting it would break its signatures", "Err_CertSigned");
            if (d.SecurityHandler.Elements.ContainsKey("/Filter")) d.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
            using var ms = new MemoryStream();
            d.Save(ms);
            plain = ms.ToArray();
        }

        // the file key: SHA-256 of a random seed and every recipient's envelope (each holding the seed and the permissions)
        var seed = RandomNumberGenerator.GetBytes(20);
        var content = new byte[24];
        seed.CopyTo(content, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(20), Permissions);
        var envelopes = recipients.Select(c =>
        {
            var env = new EnvelopedCms(new ContentInfo(content), new AlgorithmIdentifier(new Oid("2.16.840.1.101.3.4.1.42")));   // AES-256-CBC
            env.Encrypt(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, c, RSAEncryptionPadding.Pkcs1));
            return env.Encode();
        }).ToList();
        var key = FileKey(seed, envelopes, encryptMetadata: true);

        var dict = new StringBuilder("<</Filter/Adobe.PubSec/SubFilter/adbe.pkcs7.s5/V 5/R 5/Length 256/StmF/DefaultCryptFilter/StrF/DefaultCryptFilter");
        dict.Append("/CF<</DefaultCryptFilter<</Type/CryptFilter/CFM/AESV3/AuthEvent/DocOpen/Length 256/Recipients[");
        foreach (var e in envelopes) dict.Append('<').Append(Convert.ToHexString(e)).Append('>');
        dict.Append("]>>>>>>");
        // packed into object streams as Washi's other copies are (each encrypted whole); failing that, object by object
        var encrypted = ObjectStreams.Pack(plain, data => Aes(key, data), dict.ToString()) ?? Rewrite(plain, data => Aes(key, data), dict.ToString());

        // proof: the copy opens with the file key (as a recipient's key gives it) and shows the same pages
        Check(encrypted, key, plain, encryptMetadata: true);
        File.WriteAllBytes(output, encrypted);
    }

    /// <summary>
    /// Writes a copy of <paramref name="input"/> (encrypted for certificates) without the protection, opened with the
    /// private key of <paramref name="id"/>.
    /// </summary>
    public static void Decrypt(string input, string output, DigitalId id)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        var bytes = File.ReadAllBytes(input);
        var (handler, encryptNumber) = Handler(bytes) ?? throw new PageAssemblyException($"{Path.GetFileName(input)} is not encrypted for certificates", "Err_CertNotEncrypted", Path.GetFileName(input));
        var (envelopes, encryptMetadata) = Recipients(handler);
        using var rsa = id.Certificate.GetRSAPrivateKey()
            ?? throw new PageAssemblyException($"the digital ID {id.Name} has no RSA private key", "Err_CertNotRsa", id.Name);
        byte[]? seed = null;
        foreach (var der in envelopes)
        {
            var env = new EnvelopedCms();
            try { env.Decode(der); } catch (CryptographicException) { continue; }
            foreach (RecipientInfo ri in env.RecipientInfos)
            {
                if (!Matches(ri.RecipientIdentifier, id.Certificate)) continue;
                try
                {
                    env.Decrypt(ri, rsa);
                    var c = env.ContentInfo.Content;
                    if (c.Length >= 20) seed = c[..20];
                }
                catch (CryptographicException) { }
                if (seed is not null) break;
            }
            if (seed is not null) break;
        }
        if (seed is null) throw new PageAssemblyException($"{Path.GetFileName(input)} was not encrypted for the certificate of {id.Name}", "Err_CertNotRecipient", Path.GetFileName(input), id.Name);
        var key = FileKey(seed, envelopes, encryptMetadata);
        var standard = WithStandardHandler(bytes, key, encryptMetadata, encryptNumber);
        using (var d = PdfReader.Open(new MemoryStream(standard), "", PdfDocumentOpenMode.Modify))
        {
            d.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
            d.Save(output);
        }
        if (PdfSecurity.Inspect(output).Encrypted)
        {
            File.Delete(output);
            throw new PageAssemblyException("the copy is still protected; it was not kept", "Err_ProtectCheck");
        }
    }

    public static bool IsEncryptedForCertificates(string path)
    {
        try { return Handler(File.ReadAllBytes(path)) is not null; }
        catch (Exception e) when (e is IOException or PageAssemblyException) { return false; }
    }

    /// <summary>Whom the file was encrypted for (empty when it is not encrypted for certificates).</summary>
    public static IReadOnlyList<CertificateRecipient> RecipientsOf(string path)
    {
        if (Handler(File.ReadAllBytes(path)) is not { } h) return [];
        var list = new List<CertificateRecipient>();
        foreach (var der in Recipients(h.Dict).Envelopes)
        {
            var env = new EnvelopedCms();
            try { env.Decode(der); } catch (CryptographicException) { continue; }
            foreach (RecipientInfo ri in env.RecipientInfos)
                if (ri.RecipientIdentifier.Value is X509IssuerSerial s) list.Add(new CertificateRecipient(Simple(s.IssuerName), s.SerialNumber));
        }
        return list;
    }

    private static string Simple(string dn) => Regex.Match(dn, @"CN=([^,]+)") is { Success: true } m ? m.Groups[1].Value.Trim() : dn;

    /// <summary>The envelope is addressed to this certificate (decrypting is the real test).</summary>
    private static bool Matches(SubjectIdentifier who, X509Certificate2 cert) => who.Value switch
    {
        X509IssuerSerial s => string.Equals(s.SerialNumber, cert.SerialNumber, StringComparison.OrdinalIgnoreCase),
        string ski => cert.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier is { } k && string.Equals(k, ski, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static byte[] FileKey(byte[] seed, IReadOnlyList<byte[]> envelopes, bool encryptMetadata)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(seed);
        foreach (var e in envelopes) sha.AppendData(e);
        if (!encryptMetadata) sha.AppendData([0xFF, 0xFF, 0xFF, 0xFF]);
        return sha.GetHashAndReset();
    }

    /// <summary>AESV3: a random IV, then AES-256-CBC with PKCS#7 padding.</summary>
    private static byte[] Aes(byte[] key, byte[] data)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        var iv = RandomNumberGenerator.GetBytes(16);
        return [.. iv, .. aes.EncryptCbc(data, iv, PaddingMode.PKCS7)];
    }

    private static string Name(X509Certificate2 c) => c.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } n ? n : c.Subject;

    // ------------------------------------------------------------------ the security handler in a file

    /// <summary>The file's /Encrypt dictionary when it is a public-key one, and its object number.</summary>
    private static (Dictionary<string, object?> Dict, int Number)? Handler(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        // the newest trailer (table or stream) names it
        var m = EncryptRef().Matches(text);
        if (m.Count == 0) return null;
        int number = int.Parse(m[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        var obj = Regex.Matches(text, $@"(?<![0-9]){number}\s+{m[^1].Groups[2].Value}\s+obj");
        if (obj.Count == 0) return null;
        var lexer = new PdfLexer(bytes, obj[^1].Index + obj[^1].Length);
        if (lexer.ReadObject() is not Dictionary<string, object?> dict) return null;
        if (dict.GetValueOrDefault("/Filter") is not PdfLexer.Name { Value: "/Adobe.PubSec" }) return null;
        return (dict, number);
    }

    private static (List<byte[]> Envelopes, bool EncryptMetadata) Recipients(Dictionary<string, object?> h)
    {
        int v = h.GetValueOrDefault("/V") is long lv ? (int)lv : 0;
        var sub = (h.GetValueOrDefault("/SubFilter") as PdfLexer.Name)?.Value;
        Dictionary<string, object?>? filter = null;
        if (h.GetValueOrDefault("/CF") is Dictionary<string, object?> cf && h.GetValueOrDefault("/StmF") is PdfLexer.Name stmf)
            filter = cf.GetValueOrDefault(stmf.Value) as Dictionary<string, object?>;
        var cfm = (filter?.GetValueOrDefault("/CFM") as PdfLexer.Name)?.Value;
        if (v != 5 || cfm != "/AESV3")
            throw new PageAssemblyException($"this certificate encryption ({sub}, V {v}, {cfm ?? "no crypt filter"}) is not one Washi opens: only AES-256", "Err_CertUnsupported");
        var list = (filter?.GetValueOrDefault("/Recipients") ?? h.GetValueOrDefault("/Recipients")) switch
        {
            List<object?> a => a.OfType<byte[]>().ToList(),
            byte[] one => [one],
            _ => [],
        };
        bool meta = filter?.GetValueOrDefault("/EncryptMetadata") is not false && h.GetValueOrDefault("/EncryptMetadata") is not false;
        return (list, meta);
    }

    private static (List<byte[]> Envelopes, bool EncryptMetadata) Recipients((Dictionary<string, object?> Dict, int Number) h) => Recipients(h.Dict);

    [GeneratedRegex(@"/Encrypt\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex EncryptRef();

    /// <summary>
    /// The file as it is, plus an incremental update that replaces its public-key handler with a standard one (AES-256,
    /// R6) holding the same file key behind an empty password, so that PDFsharp can open it and write it without
    /// protection. Objects stay as they are: AESV3 encrypts every object with the file key itself.
    /// </summary>
    internal static byte[] WithStandardHandler(byte[] bytes, byte[] key, bool encryptMetadata, int oldNumber)
    {
        // the handler object is defined again in the update (readers resolve the trailer's /Encrypt to its newest version)
        var update = new IncrementalUpdate(bytes, allowEncrypted: true);
        var g = update.Encrypt is { } e ? int.Parse(e.Split(' ')[1], CultureInfo.InvariantCulture) : 0;
        update.Put(oldNumber, g, StandardHandler.Dictionary(key, "", "", StandardP, encryptMetadata));
        return update.Build();
    }

    /// <summary>Opens the encrypted bytes through the standard handler and compares them with the plain file.</summary>
    private static void Check(byte[] encrypted, byte[] key, byte[] plain, bool encryptMetadata)
    {
        var number = int.Parse(EncryptRef().Matches(Encoding.Latin1.GetString(encrypted))[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        var standard = WithStandardHandler(encrypted, key, encryptMetadata, number);
        bool ok;
        try
        {
            using var a = PdfReader.Open(new MemoryStream(standard), "", PdfDocumentOpenMode.Import);
            using var b = PdfReader.Open(new MemoryStream(plain), PdfDocumentOpenMode.Import);
            ok = a.PageCount == b.PageCount;
            var oa = new Dictionary<int, PdfObject>();
            foreach (var o in a.Internals.GetAllObjects()) if (o.Reference is { } r) oa[r.ObjectNumber] = o;
            var ob = b.Internals.GetAllObjects().ToList();
            for (int i = 0; ok && i < ob.Count; i++)
            {
                var other = ob[i].Reference is { } rb ? oa.GetValueOrDefault(rb.ObjectNumber) : null;
                ok = other is not null && NoLength(Revisions.Canon(other)) == NoLength(Revisions.Canon(ob[i]))
                     && ((other as PdfDictionary)?.Stream?.Value ?? []).AsSpan().SequenceEqual((ob[i] as PdfDictionary)?.Stream?.Value ?? []);
            }
        }
        catch (PdfReaderException) { ok = false; }
        if (!ok) throw new PageAssemblyException("the encrypted copy did not read back as the original; it was not kept", "Err_CertCheck");
    }

    /// <summary>A stream's length differs once encrypted; its content is compared on its own.</summary>
    private static string NoLength(string canon) => Regex.Replace(canon, @"/Length [0-9]+( R)? ", "");

    /// <summary>
    /// One stream object of a plain file (at <paramref name="offset"/>) written to <paramref name="o"/> with its
    /// dictionary's strings and its data encrypted. False when it is not laid out as expected.
    /// </summary>
    internal static bool EncryptStreamObject(byte[] plain, int offset, Func<byte[], byte[]> encrypt, Stream o)
    {
        try
        {
            var lx = new PdfLexer(plain, offset);
            var (n, g) = lx.ReadObjectHeader();
            if (lx.ReadObject() is not Dictionary<string, object?> dict || !lx.AtStream(out int start) || dict.GetValueOrDefault("/Length") is not long len) return false;
            var enc = encrypt(plain.AsSpan(start, (int)len).ToArray());
            dict["/Length"] = (long)enc.Length;
            o.Write(Encoding.Latin1.GetBytes($"{n} {g} obj\n" + PdfLexer.Write(dict, encrypt) + "\nstream\n"));
            o.Write(enc);
            o.Write(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));
            return true;
        }
        catch (FormatException) { return false; }
    }

    // ------------------------------------------------------------------ encrypting every string and stream

    /// <summary>
    /// The plain file PDFsharp wrote (objects one by one, a cross-reference table) with every string and stream
    /// encrypted by <paramref name="encrypt"/>, the /Encrypt object appended, and a new table.
    /// </summary>
    private static byte[] Rewrite(byte[] plain, Func<byte[], byte[]> encrypt, string encryptDict)
    {
        var text = Encoding.Latin1.GetString(plain);
        var sx = Regex.Matches(text, @"startxref\s+(\d+)");
        if (sx.Count != 1) throw new PageAssemblyException("the file could not be prepared for encryption", "Err_CertCheck");
        int xref = int.Parse(sx[0].Groups[1].Value, CultureInfo.InvariantCulture);
        if (!text.AsSpan(xref).StartsWith("xref")) throw new PageAssemblyException("the file could not be prepared for encryption", "Err_CertCheck");
        // the table: number → offset
        var offsets = new SortedDictionary<int, int>();
        int at = xref + 4;
        var lines = text[at..text.IndexOf("trailer", at, StringComparison.Ordinal)].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int next = 0;
        foreach (var l in lines)
        {
            var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 2) { next = int.Parse(f[0], CultureInfo.InvariantCulture); continue; }
            if (f.Length == 3 && f[2] == "n") offsets[next] = int.Parse(f[0], CultureInfo.InvariantCulture);
            next++;
        }
        var trailerAt = text.IndexOf("trailer", xref, StringComparison.Ordinal) + 7;
        var trailer = (Dictionary<string, object?>)new PdfLexer(plain, trailerAt).ReadObject()!;
        var lengths = new Dictionary<int, long>();   // lengths kept in objects of their own
        foreach (var (num, off) in offsets)
        {
            var lx = new PdfLexer(plain, off);
            lx.ReadObjectHeader();
            if (lx.ReadObject() is long v) lengths[num] = v;
        }

        using var o = new MemoryStream();
        int firstObj = offsets.Values.Min();
        o.Write(plain, 0, firstObj);   // header and binary comment
        var newOffsets = new SortedDictionary<int, long>();
        foreach (var (num, off) in offsets)
        {
            newOffsets[num] = o.Position;
            var lx = new PdfLexer(plain, off);
            var (n, g) = lx.ReadObjectHeader();
            W(o, $"{n} {g} obj\n");
            var value = lx.ReadObject();
            if (lx.AtStream(out int streamStart))
            {
                var dict = (Dictionary<string, object?>)value!;
                long len = dict.GetValueOrDefault("/Length") switch
                {
                    long l => l,
                    PdfLexer.Ref r when lengths.TryGetValue(r.Number, out var l2) => l2,
                    _ => throw new PageAssemblyException("a stream's length is unknown", "Err_CertCheck"),
                };
                var data = plain.AsSpan(streamStart, (int)len).ToArray();
                bool xrefOrOwnMeta = dict.GetValueOrDefault("/Type") is PdfLexer.Name { Value: "/XRef" };
                var enc = xrefOrOwnMeta ? data : encrypt(data);
                dict["/Length"] = (long)enc.Length;
                W(o, PdfLexer.Write(dict, xrefOrOwnMeta ? null : encrypt));
                W(o, "\nstream\n");
                o.Write(enc);
                W(o, "\nendstream\nendobj\n");
            }
            else
            {
                W(o, PdfLexer.Write(value, encrypt));
                W(o, "\nendobj\n");
            }
        }
        int encNum = offsets.Keys.Max() + 1;
        newOffsets[encNum] = o.Position;
        W(o, $"{encNum} 0 obj\n{encryptDict}\nendobj\n");
        long xrefPos = o.Position;
        var sb = new StringBuilder($"xref\n0 {encNum + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= encNum; i++)
            sb.Append(newOffsets.TryGetValue(i, out var p) ? $"{p:D10} 00000 n \n" : "0000000000 00000 f \n");
        trailer.Remove("/Prev");
        trailer["/Size"] = (long)(encNum + 1);
        trailer["/Encrypt"] = new PdfLexer.Ref(encNum, 0);
        if (!trailer.ContainsKey("/ID"))
        {
            var id = RandomNumberGenerator.GetBytes(16);
            trailer["/ID"] = new List<object?> { id, id };
        }
        sb.Append("trailer\n").Append(PdfLexer.Write(trailer, null)).Append(CultureInfo.InvariantCulture, $"\nstartxref\n{xrefPos}\n%%EOF\n");
        W(o, sb.ToString());
        return o.ToArray();

        static void W(Stream s, string t) => s.Write(Encoding.Latin1.GetBytes(t));
    }
}

/// <summary>
/// The R6 standard security handler (PDF 32000-2 7.6.4.4, algorithms 2.B, 8, 9, 10) for a given file key.
/// </summary>
internal static class StandardHandler
{
    public static string Dictionary(byte[] key, string userPassword, string ownerPassword, int p, bool encryptMetadata)
    {
        var up = Pw(userPassword);
        var op = Pw(ownerPassword);
        byte[] uvs = RandomNumberGenerator.GetBytes(8), uks = RandomNumberGenerator.GetBytes(8);
        byte[] u = [.. Hash(up, uvs, []), .. uvs, .. uks];
        var ue = Wrap(Hash(up, uks, []), key);
        byte[] ovs = RandomNumberGenerator.GetBytes(8), oks = RandomNumberGenerator.GetBytes(8);
        byte[] o = [.. Hash(op, ovs, u), .. ovs, .. oks];
        var oe = Wrap(Hash(op, oks, u), key);
        var perms = new byte[16];
        BitConverter.GetBytes(p).CopyTo(perms, 0);
        if (!BitConverter.IsLittleEndian) Array.Reverse(perms, 0, 4);
        perms[4] = perms[5] = perms[6] = perms[7] = 0xFF;
        perms[8] = (byte)(encryptMetadata ? 'T' : 'F');
        perms[9] = (byte)'a'; perms[10] = (byte)'d'; perms[11] = (byte)'b';
        RandomNumberGenerator.GetBytes(4).CopyTo(perms, 12);
        using var aes = Aes.Create();
        aes.Key = key;
        var permsEnc = aes.EncryptEcb(perms, PaddingMode.None);
        return "<</Filter/Standard/V 5/R 6/Length 256/CF<</StdCF<</AuthEvent/DocOpen/CFM/AESV3/Length 32>>>>/StmF/StdCF/StrF/StdCF"
               + $"/O<{Convert.ToHexString(o)}>/U<{Convert.ToHexString(u)}>/OE<{Convert.ToHexString(oe)}>/UE<{Convert.ToHexString(ue)}>"
               + $"/Perms<{Convert.ToHexString(permsEnc)}>/P {p}" + (encryptMetadata ? "" : "/EncryptMetadata false") + ">>";
    }

    private static byte[] Pw(string s)
    {
        var b = Encoding.UTF8.GetBytes(s.Normalize(NormalizationForm.FormKC));
        return b.Length > 127 ? b[..127] : b;
    }

    private static byte[] Wrap(byte[] kek, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = kek;
        return aes.EncryptCbc(key, new byte[16], PaddingMode.None);
    }

    /// <summary>Algorithm 2.B: SHA-256, then rounds of AES-128 and SHA-256/384/512 until the last byte says stop.</summary>
    internal static byte[] Hash(byte[] password, byte[] salt, byte[] udata)
    {
        byte[] k = SHA256.HashData([.. password, .. salt, .. udata]);
        using var aes = Aes.Create();
        int round = 0;
        byte[] e = [];
        while (round < 64 || e[^1] > round - 32)
        {
            byte[] unit = [.. password, .. k, .. udata];
            var k1 = new byte[unit.Length * 64];
            for (int i = 0; i < 64; i++) unit.CopyTo(k1, i * unit.Length);
            aes.Key = k[..16];
            e = aes.EncryptCbc(k1, k[16..32], PaddingMode.None);
            int mod = 0;
            for (int i = 0; i < 16; i++) mod += e[i];
            k = (mod % 3) switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
            round++;
        }
        return k[..32];
    }
}
