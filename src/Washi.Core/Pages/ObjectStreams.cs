// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Washi.Core.Pages;

/// <summary>
/// Rewrites a PDF that PDFsharp saved (objects one after another, a classic cross-reference table) with its plain
/// objects packed into compressed object streams and a cross-reference stream instead of the table (PDF 1.5). Stream
/// objects are copied as they are. A form or a document with thousands of small objects shrinks by a third or more.
/// </summary>
internal static partial class ObjectStreams
{
    private const int PerStream = 200;

    /// <summary>The packed file, or null when <paramref name="pdf"/> is not laid out as expected (it is then kept as it is).</summary>
    public static byte[]? Pack(byte[] pdf)
    {
        var latin = Encoding.Latin1;
        var text = latin.GetString(pdf);
        int xrefAt = text.LastIndexOf("\nxref", StringComparison.Ordinal);
        int trailerAt = text.LastIndexOf("trailer", StringComparison.Ordinal);
        if (xrefAt < 0 || trailerAt < xrefAt) return null;
        int startxref = text.IndexOf("startxref", trailerAt, StringComparison.Ordinal);
        if (startxref < 0) return null;
        var trailer = text[(trailerAt + 7)..startxref].Trim();
        if (trailer.Contains("/Encrypt", StringComparison.Ordinal) || trailer.Contains("/Prev", StringComparison.Ordinal)) return null;

        // the objects, from the table
        var offsets = new SortedDictionary<int, int>();
        var lines = text[(xrefAt + 5)..trailerAt].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int next = 0, count = 0;
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) { next = int.Parse(parts[0], CultureInfo.InvariantCulture); count = int.Parse(parts[1], CultureInfo.InvariantCulture); continue; }
            if (parts.Length == 3 && count-- > 0)
            {
                if (parts[2] == "n" && parts[1] == "00000") offsets[next] = int.Parse(parts[0], CultureInfo.InvariantCulture);
                else if (parts[2] == "n") return null;   // a generation above 0
                next++;
            }
        }
        if (offsets.Count == 0) return null;

        var plain = new List<(int Number, string Body)>();
        var streams = new List<(int Number, int Start, int End)>();
        foreach (var (number, offset) in offsets)
        {
            var head = HeadRx().Match(text, offset);
            if (!head.Success || head.Index != offset || int.Parse(head.Groups[1].Value, CultureInfo.InvariantCulture) != number) return null;
            int end = text.IndexOf("endobj", head.Index + head.Length, StringComparison.Ordinal);
            if (end < 0) return null;
            var body = text[(head.Index + head.Length)..end];
            if (body.Contains("endstream", StringComparison.Ordinal)) streams.Add((number, offset, end + 6));
            else plain.Add((number, body.Trim()));
        }
        int size = Math.Max(offsets.Keys.Max() + 1, SizeRx().Match(trailer) is { Success: true } sm ? int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture) : 0);

        using var o = new MemoryStream(pdf.Length);
        var version = VersionRx().Match(text);
        string header = version.Success && string.CompareOrdinal(version.Groups[1].Value, "1.5") >= 0 ? version.Value : "%PDF-1.5";
        Write(o, header + "\n%âãÏÓ\n");
        var where = new Dictionary<int, (int Type, long A, int B)>();   // 1: offset; 2: object stream, index

        // plain objects, packed
        int objStm = size;
        for (int i = 0; i < plain.Count; i += PerStream)
        {
            var group = plain.Skip(i).Take(PerStream).ToList();
            var bodies = new StringBuilder();
            var index = new StringBuilder();
            foreach (var (number, body) in group)
            {
                index.Append(number.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(bodies.Length.ToString(CultureInfo.InvariantCulture)).Append(' ');
                bodies.Append(body).Append('\n');
            }
            var first = index.ToString();
            var data = latin.GetBytes(first + bodies);
            var packed = new PdfSharp.Pdf.Filters.FlateDecode().Encode(data, PdfSharp.Pdf.PdfFlateEncodeMode.BestCompression);
            int number2 = objStm++;
            where[number2] = (1, o.Position, 0);
            Write(o, $"{number2} 0 obj\n<</Type/ObjStm/N {group.Count}/First {latin.GetByteCount(first)}/Filter/FlateDecode/Length {packed.Length}>>\nstream\n");
            o.Write(packed);
            Write(o, "\nendstream\nendobj\n");
            for (int k = 0; k < group.Count; k++) where[group[k].Number] = (2, number2, k);
        }
        // stream objects, as they were
        foreach (var (number, start, end) in streams)
        {
            where[number] = (1, o.Position, 0);
            o.Write(pdf, start, end - start);
            Write(o, "\n");
        }

        // the cross-reference stream
        int xref = objStm;
        where[xref] = (1, o.Position, 0);
        int total = xref + 1;
        var rows = new MemoryStream();
        for (int n = 0; n < total; n++)
        {
            var (type, a, b) = where.TryGetValue(n, out var e) ? e : (0, 0L, n == 0 ? 65535 : 0);
            rows.WriteByte((byte)type);
            rows.WriteByte((byte)(a >> 24)); rows.WriteByte((byte)(a >> 16)); rows.WriteByte((byte)(a >> 8)); rows.WriteByte((byte)a);
            rows.WriteByte((byte)(b >> 8)); rows.WriteByte((byte)b);
        }
        var xdata = new PdfSharp.Pdf.Filters.FlateDecode().Encode(rows.ToArray(), PdfSharp.Pdf.PdfFlateEncodeMode.BestCompression);
        // the trailer's entries (Root, Info, ID) go into the stream's dictionary
        var keep = new StringBuilder();
        foreach (Match m in TrailerEntryRx().Matches(trailer)) keep.Append(m.Value);
        long xrefOffset = o.Position;
        Write(o, $"{xref} 0 obj\n<</Type/XRef/Size {total}/W[1 4 2]{keep}/Filter/FlateDecode/Length {xdata.Length}>>\nstream\n");
        o.Write(xdata);
        Write(o, $"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        if (o.Position > uint.MaxValue) return null;
        return o.ToArray();

        void Write(Stream s, string t) => s.Write(latin.GetBytes(t));
    }

    [GeneratedRegex(@"(\d+) 0 obj\s*")]
    private static partial Regex HeadRx();
    [GeneratedRegex(@"/Size\s+(\d+)")]
    private static partial Regex SizeRx();
    [GeneratedRegex(@"%PDF-(\d\.\d)")]
    private static partial Regex VersionRx();
    [GeneratedRegex(@"/(Root|Info)\s+\d+\s+\d+\s+R|/ID\s*\[[^\]]*\]")]
    private static partial Regex TrailerEntryRx();
}
