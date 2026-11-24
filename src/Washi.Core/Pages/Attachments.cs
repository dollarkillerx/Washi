// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Signatures;

namespace Washi.Core.Pages;

/// <summary>A file attached to a PDF: to the document (its list of attachments), or to a comment on
/// <paramref name="Page"/> (0-based; -1 for the document).</summary>
/// <param name="Name">The file name it is saved under.</param>
/// <param name="Size">Its size in bytes (-1 when the file does not say and it cannot be read).</param>
public sealed record AttachmentInfo(string Name, string Description, long Size, DateTimeOffset? Modified, int Page);

/// <summary>
/// Attachments: lists the files a PDF carries (in its attachments list, and on file-attachment comments), saves them
/// out, and writes a copy with files added or removed. Names are kept unique; a saved file never overwrites one that is
/// there, and a name from the PDF is reduced to a plain file name before it is used.
/// </summary>
public static class PdfAttachments
{
    public static IReadOnlyList<AttachmentInfo> List(string path)
    {
        using var d = Open(path);
        return [.. Found(d).Select(f => f.Info)];
    }

    private sealed record Item(AttachmentInfo Info, PdfDictionary Spec, PdfDictionary? Annotation);

    private static List<Item> Found(PdfDocument d)
    {
        var list = new List<Item>();
        foreach (var (name, spec) in Tree(d))
            list.Add(new Item(Describe(spec, name, -1), spec, null));
        for (int p = 0; p < d.PageCount; p++)
        {
            if (Deref(d.Pages[p].Elements["/Annots"]) is not PdfArray annots) continue;
            foreach (var a in annots.Elements.Select(Deref).OfType<PdfDictionary>())
                if (Name(a, "/Subtype") == "/FileAttachment" && Deref(a.Elements["/FS"]) is PdfDictionary fs)
                    list.Add(new Item(Describe(fs, "", p), fs, a));
        }
        return list;
    }

    /// <summary>The document's attachments list (the EmbeddedFiles name tree), in its order.</summary>
    private static List<(string Name, PdfDictionary Spec)> Tree(PdfDocument d)
    {
        var list = new List<(string, PdfDictionary)>();
        if (Deref(d.Internals.Catalog.Elements["/Names"]) is PdfDictionary names && Deref(names.Elements["/EmbeddedFiles"]) is PdfDictionary root)
            Leaves(root, list, 0);
        return list;
    }

    private static void Leaves(PdfDictionary node, List<(string, PdfDictionary)> into, int depth)
    {
        if (depth > 32) return;
        if (Deref(node.Elements["/Names"]) is PdfArray pairs)
            for (int i = 0; i + 1 < pairs.Elements.Count; i += 2)
                if (Deref(pairs.Elements[i]) is PdfString key && Deref(pairs.Elements[i + 1]) is PdfDictionary spec)
                    into.Add((key.Value, spec));
        if (Deref(node.Elements["/Kids"]) is PdfArray kids)
            foreach (var k in kids.Elements)
                if (Deref(k) is PdfDictionary kid) Leaves(kid, into, depth + 1);
    }

    private static AttachmentInfo Describe(PdfDictionary spec, string key, int page)
    {
        string name = Str(spec, "/UF") is { Length: > 0 } uf ? uf : Str(spec, "/F") is { Length: > 0 } f ? f : key;
        long size = -1;
        DateTimeOffset? modified = null;
        if (Stream(spec) is { } ef)
        {
            if (Deref(ef.Elements["/Params"]) is PdfDictionary prm)
            {
                if (Deref(prm.Elements["/Size"]) is PdfInteger sz) size = sz.Value;
                modified = PdfSignatureVerifier.ParseDate(Str(prm, "/ModDate"));
            }
            if (size < 0) size = Bytes(ef)?.LongLength ?? -1;
        }
        return new AttachmentInfo(name.Length > 0 ? name : "attachment", Str(spec, "/Desc"), size, modified, page);
    }

    private static PdfDictionary? Stream(PdfDictionary spec) =>
        Deref(spec.Elements["/EF"]) is PdfDictionary ef ? (Deref(ef.Elements["/UF"]) ?? Deref(ef.Elements["/F"])) as PdfDictionary : null;

    private static byte[]? Bytes(PdfDictionary stream)
    {
        try { return stream.Stream?.UnfilteredValue; }
        catch (Exception e) when (e is InvalidOperationException or NotImplementedException or InvalidDataException) { return null; }
    }

    /// <summary>
    /// Saves the attachments named in <paramref name="names"/> (all when empty) into <paramref name="dir"/> and returns
    /// the paths written. A file already there is not replaced: the new one gets " (2)" and so on.
    /// </summary>
    public static IReadOnlyList<string> Extract(string path, string dir, IReadOnlyCollection<string>? names = null)
    {
        using var d = Open(path);
        var items = Found(d).Where(i => names is null || names.Count == 0 || names.Contains(i.Info.Name)).ToList();
        if (names is { Count: > 0 } && names.FirstOrDefault(n => !items.Any(i => i.Info.Name == n)) is { } missing)
            throw new PageAssemblyException($"there is no attachment '{missing}'", "Err_AttachmentNone", missing);
        if (items.Count == 0) throw new PageAssemblyException("the file has no attachments", "Err_AttachmentsNone");
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        foreach (var i in items)
        {
            var bytes = Stream(i.Spec) is { } s ? Bytes(s) : null;
            if (bytes is null) throw new PageAssemblyException($"the attachment '{i.Info.Name}' cannot be read", "Err_AttachmentUnreadable", i.Info.Name);
            var target = Free(Path.Combine(dir, SafeName(i.Info.Name)));
            File.WriteAllBytes(target, bytes);
            written.Add(target);
        }
        return written;
    }

    /// <summary>A plain file name: no folders, no characters a file system refuses, never "." or "..".</summary>
    public static string SafeName(string name)
    {
        var last = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var invalid = Path.GetInvalidFileNameChars().Concat([':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string([.. last.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.');
        return clean is "" or "." or ".." ? "attachment" : clean.Length > 200 ? clean[..200] : clean;
    }

    private static string Free(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            var p = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    /// <summary>
    /// Writes a copy with the files in <paramref name="add"/> attached to the document (with
    /// <paramref name="description"/>), and the attachments named in <paramref name="remove"/> taken out (a
    /// file-attachment comment goes with its file). Returns the names added (made unique).
    /// </summary>
    public static IReadOnlyList<string> Edit(string input, string output, IReadOnlyList<string> add, IReadOnlyCollection<string> remove, string description = "")
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        if (add.Count == 0 && remove.Count == 0) throw new PageAssemblyException("nothing to add or remove", "Err_AttachmentNothing");
        foreach (var f in add)
            if (!File.Exists(f)) throw new PageAssemblyException($"{f} does not exist", "Err_NotFound", f);
        var added = new List<string>();
        using (var d = PdfSecurity.OpenForChanges(input, PdfPasswords.For(input)))
        {
            var items = Found(d);
            if (remove.FirstOrDefault(n => !items.Any(i => i.Info.Name == n)) is { } missing)
                throw new PageAssemblyException($"there is no attachment '{missing}'", "Err_AttachmentNone", missing);

            // comments that carry a removed file go with it
            foreach (var i in items.Where(i => i.Annotation is not null && remove.Contains(i.Info.Name)))
                for (int p = 0; p < d.PageCount; p++)
                    if (Deref(d.Pages[p].Elements["/Annots"]) is PdfArray annots)
                        foreach (var e in annots.Elements.ToList())
                            if (ReferenceEquals(Deref(e), i.Annotation)) annots.Elements.Remove(e);

            // the document's list, rebuilt as one sorted leaf
            var keep = Tree(d).Where(t => !remove.Contains(Describe(t.Spec, t.Name, -1).Name)).ToList();
            var taken = keep.Select(t => t.Name).Concat(items.Select(i => i.Info.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var f in add)
            {
                var name = Unique(Path.GetFileName(f), taken);
                taken.Add(name);
                keep.Add((name, Spec(d, f, name, description)));
                added.Add(name);
            }
            var catalog = d.Internals.Catalog;
            var namesDict = Deref(catalog.Elements["/Names"]) as PdfDictionary;
            if (keep.Count == 0) namesDict?.Elements.Remove("/EmbeddedFiles");
            else
            {
                if (namesDict is null)
                {
                    namesDict = new PdfDictionary(d);
                    catalog.Elements["/Names"] = namesDict;
                }
                var pairs = new PdfArray(d);
                foreach (var (name, spec) in keep.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    pairs.Elements.Add(new PdfString(name, PdfStringEncoding.Unicode));
                    pairs.Elements.Add(spec.Reference is { } r ? r : spec);
                }
                var tree = new PdfDictionary(d);
                tree.Elements["/Names"] = pairs;
                namesDict.Elements["/EmbeddedFiles"] = tree;
            }
            if (namesDict is { Elements.Count: 0 }) catalog.Elements.Remove("/Names");
            d.Save(output);
        }

        // proof: the copy lists what it should, and each added file reads back byte for byte
        var after = List(output);
        bool ok = remove.All(n => !after.Any(a => a.Name == n)) && added.All(n => after.Count(a => a.Name == n) == 1);
        if (ok)
            using (var d = Open(output))
            {
                var found = Found(d);
                for (int i = 0; i < add.Count && ok; i++)
                {
                    var item = found.First(x => x.Info.Name == added[i]);
                    ok = Stream(item.Spec) is { } s && Bytes(s) is { } b && b.AsSpan().SequenceEqual(File.ReadAllBytes(add[i]));
                }
            }
        if (!ok)
        {
            File.Delete(output);
            throw new PageAssemblyException("the attachments did not read back as written; the copy was not kept", "Err_AttachmentCheck");
        }
        return added;
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
            if (!taken.Contains($"{stem} ({n}){ext}")) return $"{stem} ({n}){ext}";
    }

    /// <summary>A file specification with the file embedded (compressed, with its size, date and MD5 checksum).</summary>
    private static PdfDictionary Spec(PdfDocument d, string file, string name, string description)
    {
        var bytes = File.ReadAllBytes(file);
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(bytes);
        var stream = new PdfDictionary(d);
        d.Internals.AddObject(stream);
        stream.Elements.SetName("/Type", "/EmbeddedFile");
        stream.Elements.SetName("/Subtype", "/" + Mime(file).Replace("/", "#2F"));
        var prm = new PdfDictionary(d);
        prm.Elements.SetInteger("/Size", bytes.Length);
        prm.Elements["/ModDate"] = new PdfString(PdfDigitalSigner.PdfDate(new DateTimeOffset(File.GetLastWriteTime(file))));
        prm.Elements["/CheckSum"] = new PdfString(System.Text.Encoding.Latin1.GetString(MD5.HashData(bytes)), PdfStringEncoding.RawEncoding);
        stream.Elements["/Params"] = prm;
        stream.Elements.SetName("/Filter", "/FlateDecode");
        stream.CreateStream(packed.ToArray());

        var spec = new PdfDictionary(d);
        d.Internals.AddObject(spec);
        spec.Elements.SetName("/Type", "/Filespec");
        spec.Elements["/F"] = new PdfString(name.All(c => c < 0x80) ? name : SafeAscii(name));
        spec.Elements["/UF"] = new PdfString(name, PdfStringEncoding.Unicode);
        if (description.Trim().Length > 0) spec.Elements["/Desc"] = new PdfString(description.Trim(), PdfStringEncoding.Unicode);
        var ef = new PdfDictionary(d);
        ef.Elements["/F"] = stream.Reference;
        ef.Elements["/UF"] = stream.Reference;
        spec.Elements["/EF"] = ef;
        return spec;
    }

    private static string SafeAscii(string name) =>
        new string([.. name.Select(c => c is >= ' ' and < (char)0x7F ? c : '_')]);

    private static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".txt" => "text/plain", ".csv" => "text/csv", ".xml" => "application/xml",
        ".json" => "application/json", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    private static PdfDocument Open(string path)
    {
        try { return PdfReader.Open(path, PdfPasswords.For(path) ?? "", PdfDocumentOpenMode.Import); }
        catch (PdfReaderException e) when (PdfSecurity.IsPasswordProblem(e))
        {
            throw new PageAssemblyException($"{Path.GetFileName(path)} is protected with a password", "Err_NeedsPassword", Path.GetFileName(path));
        }
    }

    private static string Str(PdfDictionary d, string key) => (Deref(d.Elements[key]) as PdfString)?.Value ?? "";
    private static string Name(PdfDictionary d, string key) => Deref(d.Elements[key]) is PdfName n ? n.Value : "";
    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;
}
