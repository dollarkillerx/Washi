// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Washi.Core.Fonts;
using Washi.Core.Pages;
using Washi.Core.Signatures;

namespace Washi.Core.Forms;

/// <summary>
/// Fills a form that is protected with a permissions password but allows filling in, as Acrobat does: without that
/// password and without lifting any protection. The file is opened with its open password (usually none), the fields
/// are filled in memory as usual, and only the objects that changed are appended as an incremental update, encrypted
/// with the file's own key (standard security handler: RC4, AES-128 or AES-256). Everything before stays as it was,
/// the restrictions included.
/// </summary>
internal static partial class RestrictedFill
{
    /// <summary>PDF 32000 7.6.4.3: the padding a password is completed with.</summary>
    private static readonly byte[] Pad =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private const int FillFormsBit = 1 << 8, AnnotationsBit = 1 << 5;

    public static FillResult Fill(string input, string output, IReadOnlyDictionary<string, string> values, OpenTypeFont? embedFont)
    {
        var bytes = File.ReadAllBytes(input);
        var password = PdfPasswords.For(input) ?? "";
        var sec = Security.Read(bytes, password)
            ?? throw new PageAssemblyException($"{Path.GetFileName(input)}: the permissions password is needed for this", "Err_NeedsPermissionsPassword", Path.GetFileName(input));
        if ((sec.P & (FillFormsBit | AnnotationsBit)) == 0)
            throw new PageAssemblyException($"{Path.GetFileName(input)} does not allow filling in its form", "Err_FormLocked", Path.GetFileName(input));

        FillResult result;
        var update = new IncrementalUpdate(bytes, allowEncrypted: true);
        var changed = new List<(int Number, int Generation, byte[] Body)>();
        using (var doc = PdfReader.Open(new MemoryStream(bytes), password, PdfDocumentOpenMode.Import))
        {
            var before = new Dictionary<int, (string Canon, byte[] Stream)>();
            foreach (var o in doc.Internals.GetAllObjects())
                if (o.Reference is { } r) before[r.ObjectNumber] = (Revisions.Canon(o), (o as PdfDictionary)?.Stream?.Value ?? []);
            result = AcroFormFields.Fill(doc, values, embedFont);
            foreach (var o in doc.Internals.GetAllObjects())
            {
                if (o.Reference is not { } r) continue;
                var stream = (o as PdfDictionary)?.Stream?.Value ?? [];
                if (before.TryGetValue(r.ObjectNumber, out var was) && was.Canon == Revisions.Canon(o) && was.Stream.AsSpan().SequenceEqual(stream)) continue;
                changed.Add((r.ObjectNumber, r.GenerationNumber, Encrypted(o, r.ObjectNumber, r.GenerationNumber, sec)));
            }
        }
        if (changed.Count == 0) throw new PageAssemblyException("nothing in the form changed", "Err_FormDataNothingFilled");
        foreach (var (n, g, body) in changed) update.Put(n, g, body);
        if (!changed.Any(c => c.Number == sec.Handler.Number)) update.Put(sec.Handler.Number, sec.Handler.Generation, sec.Handler.Dictionary);
        var filled = update.Build();

        // proof: the copy opens with the same password, keeps its protection, and holds the values
        bool ok;
        try
        {
            using var check = PdfReader.Open(new MemoryStream(filled), password, PdfDocumentOpenMode.Import);
            using var want = PdfReader.Open(new MemoryStream(bytes), password, PdfDocumentOpenMode.Import);
            AcroFormFields.Fill(want, values, embedFont);
            var got = AcroFormFields.List(check).ToDictionary(f => f.Name, f => f.Value);
            ok = AcroFormFields.List(want).All(f => got.TryGetValue(f.Name, out var v) && v == f.Value)
                 && Security.Read(filled, password) is { } after && after.P == sec.P;
        }
        catch (PdfReaderException) { ok = false; }
        if (!ok) throw new PageAssemblyException("the filled copy did not read back as filled; it was not kept", "Err_FormFillCheck");
        File.WriteAllBytes(output, filled);
        return result;
    }

    /// <summary>An object as PDF syntax, its strings and its stream encrypted for its number.</summary>
    private static byte[] Encrypted(PdfObject o, int number, int generation, Security sec)
    {
        var value = new PdfLexer(Encoding.Latin1.GetBytes(PdfSyntax.Write(o)), 0).ReadObject();
        string Text(object? v) => PdfLexer.Write(v, s => sec.Encrypt(s, number, generation, stream: false));
        if (o is PdfDictionary { Stream: { } st } && value is Dictionary<string, object?> dict)
        {
            var data = sec.Encrypt(st.Value ?? [], number, generation, stream: true);
            dict["/Length"] = (long)data.Length;
            return [.. Encoding.Latin1.GetBytes(Text(dict) + "\nstream\n"), .. data, .. "\nendstream"u8];
        }
        return Encoding.Latin1.GetBytes(Text(value));
    }

    /// <summary>The standard security handler of a file, opened with a user password: its key and how it encrypts.</summary>
    private sealed record Security(byte[] Key, int V, int R, int P, string StreamMethod, string StringMethod)
    {
        /// <summary>The /Encrypt object as it is (its number, generation and dictionary), repeated in the update: PDFsharp
        /// cannot find a security handler that the newest section does not hold.</summary>
        public (int Number, int Generation, string Dictionary) Handler { get; init; }

        public static Security? Read(byte[] bytes, string password)
        {
            var text = Encoding.Latin1.GetString(bytes);
            var refs = EncryptRx().Matches(text);
            if (refs.Count == 0) return null;
            var obj = Regex.Matches(text, $@"(?<![0-9]){refs[^1].Groups[1].Value}\s+{refs[^1].Groups[2].Value}\s+obj");
            if (obj.Count == 0) return null;
            if (new PdfLexer(bytes, obj[^1].Index + obj[^1].Length).ReadObject() is not Dictionary<string, object?> d) return null;
            if (d.GetValueOrDefault("/Filter") is not PdfLexer.Name { Value: "/Standard" }) return null;
            int v = (int)(d.GetValueOrDefault("/V") as long? ?? 0), r = (int)(d.GetValueOrDefault("/R") as long? ?? 0), p = (int)(d.GetValueOrDefault("/P") as long? ?? 0);
            var o = d.GetValueOrDefault("/O") as byte[] ?? [];
            var u = d.GetValueOrDefault("/U") as byte[] ?? [];
            bool encryptMetadata = d.GetValueOrDefault("/EncryptMetadata") is not false;
            string Method(string key)
            {
                if (v < 4) return "RC4";
                var name = (d.GetValueOrDefault(key) as PdfLexer.Name)?.Value ?? "/Identity";
                if (name == "/Identity") return "None";
                var cf = d.GetValueOrDefault("/CF") is Dictionary<string, object?> c ? c.GetValueOrDefault(name) as Dictionary<string, object?> : null;
                return (cf?.GetValueOrDefault("/CFM") as PdfLexer.Name)?.Value switch { "/AESV2" => "AESV2", "/AESV3" => "AESV3", "/V2" => "RC4", _ => "None" };
            }
            var pw = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
            byte[]? key;
            if (r >= 5)
            {
                if (u.Length < 48 || d.GetValueOrDefault("/UE") is not byte[] ue) return null;
                var pw127 = pw.Length > 127 ? pw[..127] : pw;
                if (!StandardHandler.Hash(pw127, u[32..40], []).AsSpan().SequenceEqual(u.AsSpan(0, 32))) return null;
                using var aes = Aes.Create();
                aes.Key = StandardHandler.Hash(pw127, u[40..48], []);
                key = aes.DecryptCbc(ue, new byte[16], PaddingMode.None);
            }
            else
            {
                var id = IdRx().Match(text) is { Success: true } m ? new PdfLexer(Encoding.Latin1.GetBytes(m.Groups[1].Value), 0).ReadObject() as byte[] ?? [] : [];
                int n = r == 2 ? 5 : d.GetValueOrDefault("/Length") is long bits && bits >= 40 ? (int)(bits / 8) : 16;
                var latin = Encoding.Latin1.GetBytes(password);   // PDFDocEncoding for R2-4
                byte[] padded = [.. latin.Take(32), .. Pad.Take(32 - Math.Min(32, latin.Length))];
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                md5.AppendData(padded);
                md5.AppendData(o.Length >= 32 ? o[..32] : o);
                var pb = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(pb, p);
                md5.AppendData(pb);
                md5.AppendData(id);
                if (r >= 4 && !encryptMetadata) md5.AppendData([0xFF, 0xFF, 0xFF, 0xFF]);
                var h = md5.GetHashAndReset();
                if (r >= 3) for (int i = 0; i < 50; i++) h = MD5.HashData(h.AsSpan(0, n));
                key = h[..n];
                // the user password is right when U comes out as the file says (algorithms 4 and 5)
                byte[] check;
                if (r == 2) check = Rc4(key, Pad);
                else
                {
                    check = Rc4(key, MD5.HashData([.. Pad, .. id]));
                    for (byte i = 1; i <= 19; i++) check = Rc4([.. key.Select(k => (byte)(k ^ i))], check);
                }
                int len = r == 2 ? 32 : 16;
                if (u.Length < len || !check.AsSpan(0, len).SequenceEqual(u.AsSpan(0, len))) return null;
            }
            return new Security(key, v, r, p, Method("/StmF"), Method("/StrF"))
            {
                Handler = (int.Parse(refs[^1].Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(refs[^1].Groups[2].Value, CultureInfo.InvariantCulture), PdfLexer.Write(d, null)),
            };
        }

        public byte[] Encrypt(byte[] data, int number, int generation, bool stream)
        {
            var method = stream ? StreamMethod : StringMethod;
            switch (method)
            {
                case "None": return data;
                case "AESV3":
                {
                    using var aes = Aes.Create();
                    aes.Key = Key;
                    var iv = RandomNumberGenerator.GetBytes(16);
                    return [.. iv, .. aes.EncryptCbc(data, iv, PaddingMode.PKCS7)];
                }
                default:
                {
                    // algorithm 1: the object's own key from the file key, its number and generation
                    byte[] salt = method == "AESV2" ? "sAlT"u8.ToArray() : [];
                    byte[] seed = [.. Key, (byte)number, (byte)(number >> 8), (byte)(number >> 16), (byte)generation, (byte)(generation >> 8), .. salt];
                    var objectKey = MD5.HashData(seed)[..Math.Min(16, Key.Length + 5)];
                    if (method == "RC4") return Rc4(objectKey, data);
                    using var aes = Aes.Create();
                    aes.Key = objectKey;
                    var iv = RandomNumberGenerator.GetBytes(16);
                    return [.. iv, .. aes.EncryptCbc(data, iv, PaddingMode.PKCS7)];
                }
            }
        }
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var o = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            o[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return o;
    }

    [GeneratedRegex(@"/Encrypt\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex EncryptRx();
    [GeneratedRegex(@"/ID\s*\[\s*(<[0-9A-Fa-f\s]*>|\((?:[^()\\]|\\.)*\))")]
    private static partial Regex IdRx();
}
