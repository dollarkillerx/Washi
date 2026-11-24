// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Washi.Core.Pages;

namespace Washi.Core.Forms;

/// <summary>A file of form data: FDF (PDF 32000 12.7.8), XFDF (ISO 19444-1), CSV (a header row of field names) or
/// JSON (an object of field name → value).</summary>
public enum FormDataFormat { Fdf, Xfdf, Csv, Json }

/// <summary>One copy filled from a row of a table.</summary>
/// <param name="Row">The row's number in the table (1 is the first row after the header).</param>
public sealed record FilledRow(int Row, string Output, int Filled, IReadOnlyList<string> Notes);

/// <summary>What filling from a table did: the copies, and the table's columns that are no field of the form.</summary>
public sealed record FillManyResult(IReadOnlyList<FilledRow> Rows, IReadOnlyList<string> UnknownColumns);

/// <summary>
/// Form data in and out of a PDF form: export the values to FDF, XFDF, CSV or JSON (as Acrobat and other programs read
/// them), import them into the form (with the form's own formats, ranges and calculations, as when filling), fill one
/// copy per row of a table, and collect many filled forms into one table.
/// </summary>
public static class PdfFormData
{
    private const string XfdfNs = "http://ns.adobe.com/xfdf/";

    public static FormDataFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".fdf" => FormDataFormat.Fdf,
        ".xfdf" => FormDataFormat.Xfdf,
        ".csv" or ".txt" => FormDataFormat.Csv,
        ".json" => FormDataFormat.Json,
        var e => throw new PageAssemblyException($"'{e}' is not a form data file (.fdf, .xfdf, .csv or .json)", "Err_FormDataKind", e),
    };

    /// <summary>The values of the fields that hold data (text, check boxes, radio buttons, choices), in the form's order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Values(string pdf)
    {
        var fields = AcroFormFields.List(pdf).Where(f => f.Type is "text" or "checkbox" or "radio" or "choice").ToList();
        if (fields.Count == 0) throw new PageAssemblyException($"{Path.GetFileName(pdf)} has no form fields", "Err_FormDataNoFields", Path.GetFileName(pdf));
        // a box that is not ticked is Off, as FDF and Acrobat write it
        return [.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Type is "checkbox" or "radio" && f.Value.Length == 0 ? "Off" : f.Value))];
    }

    // ------------------------------------------------------------------ export

    /// <summary>Writes the form's values to <paramref name="output"/>, in the format its extension names.</summary>
    public static int Export(string pdf, string output)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        var format = FormatOf(output);
        var values = Values(pdf);
        var kinds = AcroFormFields.List(pdf).ToDictionary(f => f.Name, f => f.Type, StringComparer.Ordinal);
        // FDF and XFDF carry a button's state name (as the PDF stores it); CSV and JSON its label, for people
        var states = AcroFormFields.ButtonStateMap(pdf);
        var stored = values.Select(v => states.TryGetValue(v.Key, out var st) && st.FirstOrDefault(x => x.Label == v.Value) is { State: { } s } && s.Length > 0
            ? new KeyValuePair<string, string>(v.Key, s) : v).ToList();
        byte[] bytes = format switch
        {
            FormDataFormat.Fdf => Fdf(stored, kinds, Path.GetFileName(pdf)),
            FormDataFormat.Xfdf => Xfdf(stored, Path.GetFileName(pdf)),
            FormDataFormat.Csv => Csv([[.. values.Select(v => v.Key)], [.. values.Select(v => v.Value)]]),
            _ => JsonSerializer.SerializeToUtf8Bytes(values.ToDictionary(v => v.Key, v => v.Value),
                                                     new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        };
        File.WriteAllBytes(output, bytes);

        // proof: the file reads back to the same values
        if (format is FormDataFormat.Fdf or FormDataFormat.Xfdf) values = stored;
        var back = Read(output);
        static string Lines(string v) => v.Replace("\r\n", "\n").Replace('\r', '\n');
        if (back.Count != values.Count || back.Zip(values).Any(p => p.First.Key != p.Second.Key || Lines(p.First.Value) != Lines(p.Second.Value)))
        {
            File.Delete(output);
            throw new PageAssemblyException("the form data did not read back as written; it was not kept", "Err_FormDataCheck");
        }
        return values.Count;
    }

    /// <summary>FDF: the fields as a tree of partial names, as Acrobat writes them; check boxes and radio buttons as names.</summary>
    private static byte[] Fdf(IReadOnlyList<KeyValuePair<string, string>> values, IReadOnlyDictionary<string, string> kinds, string file)
    {
        var root = new Node("");
        foreach (var (name, value) in values) root.Add(name.Split('.'), 0, value, kinds.GetValueOrDefault(name) is "checkbox" or "radio");
        var sb = new StringBuilder("%FDF-1.2\n%âãÏÓ\n1 0 obj\n<</FDF<</F ");
        sb.Append(Text(file)).Append("/Fields[");
        foreach (var k in root.Kids) Write(k, sb);
        sb.Append("]>>/Type/Catalog>>\nendobj\ntrailer\n<</Root 1 0 R>>\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());

        static void Write(Node n, StringBuilder sb)
        {
            sb.Append("<</T ").Append(Text(n.Name));
            if (n.Value is { } v)
                sb.Append("/V ").Append(n.IsName ? Signatures.PdfSyntax.Name(v.Length == 0 ? "Off" : v) : Text(v));
            if (n.Kids.Count > 0)
            {
                sb.Append("/Kids[");
                foreach (var k in n.Kids) Write(k, sb);
                sb.Append(']');
            }
            sb.Append(">>");
        }
    }

    private sealed class Node(string name)
    {
        public string Name { get; } = name;
        public string? Value { get; set; }
        public bool IsName { get; set; }
        public List<Node> Kids { get; } = [];

        public void Add(string[] parts, int at, string value, bool isName)
        {
            var kid = Kids.FirstOrDefault(k => k.Name == parts[at]);
            if (kid is null) Kids.Add(kid = new Node(parts[at]));
            if (at == parts.Length - 1) { kid.Value = value; kid.IsName = isName; }
            else kid.Add(parts, at + 1, value, isName);
        }
    }

    /// <summary>A PDF text string: ASCII as a literal, anything else as UTF-16BE with a byte-order mark, in hex.</summary>
    private static string Text(string s) =>
        s.All(c => c is >= ' ' and < (char)0x7F)
            ? "(" + s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")"
            : "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";

    private static byte[] Xfdf(IReadOnlyList<KeyValuePair<string, string>> values, string file)
    {
        XNamespace ns = XfdfNs;
        var fields = new XElement(ns + "fields");
        foreach (var (name, value) in values)
        {
            var parent = fields;
            var parts = name.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                var el = parent.Elements(ns + "field").FirstOrDefault(e => (string?)e.Attribute("name") == parts[i]);
                if (el is null) parent.Add(el = new XElement(ns + "field", new XAttribute("name", parts[i])));
                if (i == parts.Length - 1) el.AddFirst(new XElement(ns + "value", value));
                parent = el;
            }
        }
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "xfdf", new XAttribute(XNamespace.Xml + "space", "preserve"),
                new XElement(ns + "f", new XAttribute("href", file)), fields));
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            doc.Save(w);
        return ms.ToArray();
    }

    /// <summary>CSV (RFC 4180), UTF-8 with a byte-order mark so that spreadsheets read Japanese as such.</summary>
    internal static byte[] Csv(IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(string.Join(",", row.Select(c => c.IndexOfAny([',', '"', '\n', '\r']) >= 0 || c != c.Trim() ? "\"" + c.Replace("\"", "\"\"") + "\"" : c))).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    // ------------------------------------------------------------------ reading

    /// <summary>The values a form data file holds (name → value), in its order. A CSV's first data row.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Read(string path)
    {
        if (!File.Exists(path)) throw new PageAssemblyException($"{path} does not exist", "Err_NotFound", path);
        try
        {
            switch (FormatOf(path))
            {
                case FormDataFormat.Fdf: return ReadFdf(File.ReadAllBytes(path));
                case FormDataFormat.Xfdf: return ReadXfdf(path);
                case FormDataFormat.Csv:
                {
                    var table = ReadCsv(path);
                    if (table.Count < 2) throw new PageAssemblyException($"{Path.GetFileName(path)} has no row of values under its header", "Err_FormDataEmpty", Path.GetFileName(path));
                    return [.. table[0].Select((h, i) => new KeyValuePair<string, string>(h, i < table[1].Count ? table[1][i] : "")).Where(p => p.Key.Length > 0)];
                }
                default:
                {
                    using var j = JsonDocument.Parse(File.ReadAllBytes(path));
                    if (j.RootElement.ValueKind != JsonValueKind.Object) throw new PageAssemblyException($"{Path.GetFileName(path)} is not an object of field names and values", "Err_FormDataUnreadable", Path.GetFileName(path));
                    return [.. j.RootElement.EnumerateObject().Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString()!,
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => "",
                        _ => p.Value.GetRawText(),
                    }))];
                }
            }
        }
        catch (Exception e) when (e is FormatException or XmlException or JsonException or ArgumentException or InvalidCastException)
        {
            throw new PageAssemblyException($"{Path.GetFileName(path)} could not be read: {e.Message}", "Err_FormDataUnreadable", Path.GetFileName(path));
        }
    }

    private static List<KeyValuePair<string, string>> ReadFdf(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        if (!text.StartsWith("%FDF-", StringComparison.Ordinal)) throw new FormatException("not an FDF file");
        object? Resolve(object? v)
        {
            for (int depth = 0; v is PdfLexer.Ref r && depth < 16; depth++)
            {
                var m = Regex.Matches(text, $@"(?<![0-9]){r.Number}\s+{r.Generation}\s+obj");
                if (m.Count == 0) return null;
                v = new PdfLexer(bytes, m[^1].Index + m[^1].Length).ReadObject();
            }
            return v;
        }
        var rootRef = Regex.Match(text, @"/Root\s+(\d+)\s+(\d+)\s+R");
        object? root = rootRef.Success ? Resolve(new PdfLexer.Ref(int.Parse(rootRef.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(rootRef.Groups[2].Value, CultureInfo.InvariantCulture)))
                                       : Resolve(new PdfLexer.Ref(1, 0));
        if (root is not Dictionary<string, object?> catalog || Resolve(catalog.GetValueOrDefault("/FDF")) is not Dictionary<string, object?> fdf)
            throw new FormatException("no /FDF dictionary");
        var list = new List<KeyValuePair<string, string>>();
        if (Resolve(fdf.GetValueOrDefault("/Fields")) is List<object?> fields)
            foreach (var f in fields) Walk(Resolve(f), "", 0);
        return list;

        void Walk(object? node, string parent, int depth)
        {
            if (node is not Dictionary<string, object?> d || depth > 32) return;
            var t = Resolve(d.GetValueOrDefault("/T")) is byte[] tb ? TextString(tb) : "";
            var name = t.Length == 0 ? parent : parent.Length == 0 ? t : parent + "." + t;
            if (d.ContainsKey("/V"))
                list.Add(new(name, Resolve(d["/V"]) switch
                {
                    byte[] s => TextString(s),
                    PdfLexer.Name n => Unescape(n.Value),
                    List<object?> a => string.Join(", ", a.Select(Resolve).OfType<byte[]>().Select(TextString)),
                    long l => l.ToString(CultureInfo.InvariantCulture),
                    PdfLexer.Real r => r.Text,
                    _ => "",
                }));
            if (Resolve(d.GetValueOrDefault("/Kids")) is List<object?> kids)
                foreach (var k in kids) Walk(Resolve(k), name, depth + 1);
        }
    }

    /// <summary>A name without its slash, #hh escapes decoded (UTF-8).</summary>
    private static string Unescape(string name)
    {
        var raw = name.TrimStart('/');
        var bytes = new List<byte>();
        for (int i = 0; i < raw.Length; i++)
            if (raw[i] == '#' && i + 2 < raw.Length && Uri.IsHexDigit(raw[i + 1]) && Uri.IsHexDigit(raw[i + 2])) { bytes.Add(Convert.ToByte(raw.Substring(i + 1, 2), 16)); i += 2; }
            else bytes.Add((byte)raw[i]);
        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>A PDF text string: UTF-16BE (with its mark), UTF-8 (with its mark, PDF 2.0), else PDFDocEncoding (as
    /// Latin-1); bytes that are valid UTF-8 without the mark, as careless programs write them, are read as UTF-8.</summary>
    private static string TextString(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Any(x => x >= 0x80))
            try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b); }
            catch (DecoderFallbackException) { }
        return Encoding.Latin1.GetString(b);
    }

    private static List<KeyValuePair<string, string>> ReadXfdf(string path)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var list = new List<KeyValuePair<string, string>>();
        var fields = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "fields");
        if (fields is null) throw new FormatException("no <fields>");
        foreach (var f in fields.Elements().Where(e => e.Name.LocalName == "field")) Walk(f, "");
        return list;

        void Walk(XElement f, string parent)
        {
            var name = (string?)f.Attribute("name") ?? "";
            var full = parent.Length == 0 ? name : parent + "." + name;
            var values = f.Elements().Where(e => e.Name.LocalName == "value").Select(e => e.Value).ToList();
            if (values.Count > 0) list.Add(new(full, string.Join(", ", values)));
            foreach (var k in f.Elements().Where(e => e.Name.LocalName == "field")) Walk(k, full);
        }
    }

    /// <summary>A CSV file's rows (RFC 4180: quoted cells may hold commas, quotes and line breaks). UTF-8, or Shift_JIS
    /// as Japanese spreadsheets save it.</summary>
    public static List<List<string>> ReadCsv(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(932).GetString(bytes);
        }
        text = text.TrimStart('﻿');
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"' when cell.Length == 0: quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        rows.RemoveAll(r => r.All(c => c.Length == 0));
        if (rows.Count > 0) rows[0] = [.. rows[0].Select(h => h.Trim())];
        return rows;
    }

    // ------------------------------------------------------------------ import

    /// <summary>Fills a copy of <paramref name="pdf"/> with the values in <paramref name="data"/> (FDF, XFDF, CSV or JSON).</summary>
    public static FillResult Import(string pdf, string data, string output)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        var values = Read(data);
        var names = Values(pdf).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        if (!values.Any(v => names.Contains(v.Key)))
            throw new PageAssemblyException($"none of the names in {Path.GetFileName(data)} is a field of {Path.GetFileName(pdf)}", "Err_FormDataNoMatch", Path.GetFileName(data), Path.GetFileName(pdf));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in values) map[k] = v;
        return AcroFormFields.Fill(pdf, output, map);
    }

    /// <summary>
    /// One filled copy of <paramref name="pdf"/> per row of <paramref name="table"/> (a CSV whose header names the
    /// fields), into <paramref name="folder"/>. Each copy is named after <paramref name="nameColumn"/> when given (and
    /// the row has a value there), else &lt;form&gt;_001.pdf and so on; existing files are never replaced. Empty cells
    /// leave their field as the form has it.
    /// </summary>
    public static FillManyResult FillMany(string pdf, string table, string folder, string? nameColumn = null)
    {
        var rows = ReadCsv(table);
        if (rows.Count < 2) throw new PageAssemblyException($"{Path.GetFileName(table)} has no row of values under its header", "Err_FormDataEmpty", Path.GetFileName(table));
        var header = rows[0];
        var names = Values(pdf).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        var unknown = header.Where(h => h.Length > 0 && !names.Contains(h) && h != nameColumn).Distinct().ToList();
        if (!header.Any(names.Contains))
            throw new PageAssemblyException($"none of the names in {Path.GetFileName(table)} is a field of {Path.GetFileName(pdf)}", "Err_FormDataNoMatch", Path.GetFileName(table), Path.GetFileName(pdf));
        int nameAt = nameColumn is null ? -1 : header.IndexOf(nameColumn);
        if (nameColumn is not null && nameAt < 0)
            throw new PageAssemblyException($"{Path.GetFileName(table)} has no column '{nameColumn}'", "Err_FormDataNoColumn", Path.GetFileName(table), nameColumn);
        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(pdf);
        var done = new List<FilledRow>();
        for (int r = 1; r < rows.Count; r++)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int c = 0; c < header.Count && c < rows[r].Count; c++)
                if (names.Contains(header[c]) && rows[r][c].Length > 0) values[header[c]] = rows[r][c];
            var baseName = nameAt >= 0 && nameAt < rows[r].Count && rows[r][nameAt].Trim().Length > 0
                ? PdfAttachments.SafeName(rows[r][nameAt].Trim())
                : $"{stem}_{r.ToString("D3", CultureInfo.InvariantCulture)}";
            var output = Free(Path.Combine(folder, baseName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? baseName : baseName + ".pdf"));
            var result = AcroFormFields.Fill(pdf, output, values);
            done.Add(new FilledRow(r, output, result.Filled, result.Notes));
        }
        return new FillManyResult(done, unknown);
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

    // ------------------------------------------------------------------ collect

    /// <summary>
    /// One CSV of many filled forms: a row per PDF (its file name first), a column per field, in the order the fields
    /// first appear. Returns the number of rows.
    /// </summary>
    public static int Collect(IReadOnlyList<string> pdfs, string output)
    {
        if (File.Exists(output)) throw new PageAssemblyException($"{Path.GetFileName(output)} already exists; Washi never overwrites files", "Err_Exists", Path.GetFileName(output));
        if (FormatOf(output) != FormDataFormat.Csv) throw new PageAssemblyException("collected form data is written as CSV", "Err_FormDataKind", Path.GetExtension(output));
        if (pdfs.Count == 0) throw new PageAssemblyException("give the filled forms to collect", "Err_FormDataNothing");
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<(string File, Dictionary<string, string> Values)>();
        foreach (var pdf in pdfs)
        {
            var values = Values(pdf);
            foreach (var (k, _) in values) if (seen.Add(k)) columns.Add(k);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in values) map[k] = v;
            rows.Add((Path.GetFileName(pdf), map));
        }
        var fileColumn = seen.Contains("file") ? "(file)" : "file";
        File.WriteAllBytes(output, Csv(new[] { (IReadOnlyList<string>)[fileColumn, .. columns] }
            .Concat(rows.Select(r => (IReadOnlyList<string>)[r.File, .. columns.Select(c => r.Values.GetValueOrDefault(c, ""))]))));
        return rows.Count;
    }
}
