// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Washi.Core.Signatures;

/// <summary>
/// An incremental update: new and changed objects appended after the file's last byte, with their own
/// cross-reference section pointing back to the previous one (/Prev). Nothing before the end of the file changes, so
/// signatures already in it stay valid. The section is a table or a stream, as the file's last one is.
/// </summary>
internal sealed partial class IncrementalUpdate
{
    private readonly byte[] _original;
    private readonly List<(int Number, int Generation, byte[] Body)> _objects = [];
    private int _next;

    public string Root { get; }
    public string? Info { get; }
    public string? Id { get; }
    public long PreviousXref { get; }
    public bool XrefStream { get; }

    /// <summary>The trailer's security handler (a reference), carried into the update; only with allowEncrypted.</summary>
    public string? Encrypt { get; set; }

    /// <param name="allowEncrypted">Add to an encrypted file (whose new objects must then need no encryption).</param>
    public IncrementalUpdate(byte[] original, bool allowEncrypted = false)
    {
        _original = original;
        var tail = Encoding.Latin1.GetString(original, Math.Max(0, original.Length - 2048), Math.Min(2048, original.Length));
        var sx = StartXrefRx().Matches(tail);
        if (sx.Count == 0) throw new Pages.PageAssemblyException("the file has no cross-reference table to add to", "Err_SignDamaged");
        PreviousXref = long.Parse(sx[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        if (PreviousXref <= 0 || PreviousXref >= original.Length) throw new Pages.PageAssemblyException("the file's cross-reference offset is wrong", "Err_SignDamaged");
        var at = Encoding.Latin1.GetString(original, (int)PreviousXref, (int)(original.Length - PreviousXref));
        string dict;
        if (at.StartsWith("xref", StringComparison.Ordinal))
        {
            int t = at.IndexOf("trailer", StringComparison.Ordinal);
            int e = at.IndexOf("startxref", t < 0 ? 0 : t, StringComparison.Ordinal);
            if (t < 0 || e < 0) throw new Pages.PageAssemblyException("the file's trailer is missing", "Err_SignDamaged");
            dict = at[(t + 7)..e];
        }
        else
        {
            int s = at.IndexOf("stream", StringComparison.Ordinal);
            if (s < 0) throw new Pages.PageAssemblyException("the file's cross-reference stream is unreadable", "Err_SignDamaged");
            dict = at[..s];
            XrefStream = true;
        }
        if (dict.Contains("/Encrypt", StringComparison.Ordinal))
        {
            if (!allowEncrypted) throw new Pages.PageAssemblyException("protected files cannot be signed by Washi", "Err_SignProtected");
            Encrypt = EncryptRx().Match(dict) is { Success: true } en ? en.Groups[1].Value : null;
        }
        Root = RootRx().Match(dict) is { Success: true } r ? r.Groups[1].Value : throw new Pages.PageAssemblyException("the file has no catalog", "Err_SignDamaged");
        Info = InfoRx().Match(dict) is { Success: true } i ? i.Groups[1].Value : null;
        Id = IdRx().Match(dict) is { Success: true } d ? d.Value : null;
        _next = SizeRx().Match(dict) is { Success: true } z ? int.Parse(z.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Reserves the number of a new object.</summary>
    public int NewNumber(int highest)
    {
        _next = Math.Max(_next, highest + 1);
        return _next++;
    }

    public void Put(int number, int generation, string body) => _objects.Add((number, generation, Encoding.Latin1.GetBytes(body)));
    public void Put(int number, int generation, byte[] body) => _objects.Add((number, generation, body));

    /// <summary>The whole file: the original, then the update.</summary>
    public byte[] Build()
    {
        using var o = new MemoryStream(_original.Length + 64 * 1024);
        o.Write(_original);
        if (_original.Length > 0 && _original[^1] is not (10 or 13)) o.WriteByte(10);
        var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
        foreach (var (number, generation, body) in _objects)
        {
            offsets[number] = (o.Position, generation);
            Write(o, $"{number} {generation} obj\n");
            o.Write(body);
            Write(o, "\nendobj\n");
        }
        int size = Math.Max(_next, offsets.Count == 0 ? 0 : offsets.Keys.Max() + 1);
        var extra = new StringBuilder($"/Root {Root}");
        if (Info is not null) extra.Append($"/Info {Info}");
        if (Id is not null) extra.Append(Id);
        if (Encrypt is not null) extra.Append($"/Encrypt {Encrypt}");
        extra.Append(CultureInfo.InvariantCulture, $"/Prev {PreviousXref}");
        long xref = o.Position;
        if (!XrefStream)
        {
            var sb = new StringBuilder("xref\n");
            foreach (var run in Runs(offsets.Keys))
            {
                sb.Append(CultureInfo.InvariantCulture, $"{run.First} {run.Count}\n");
                for (int n = run.First; n < run.First + run.Count; n++)
                    sb.Append(offsets[n].Offset.ToString("D10", CultureInfo.InvariantCulture)).Append(' ')
                      .Append(offsets[n].Generation.ToString("D5", CultureInfo.InvariantCulture)).Append(" n \n");
            }
            sb.Append(CultureInfo.InvariantCulture, $"trailer\n<</Size {size}{extra}>>\nstartxref\n{xref}\n%%EOF\n");
            Write(o, sb.ToString());
        }
        else
        {
            int self = size++;
            offsets[self] = (xref, 0);
            var rows = new MemoryStream();
            var index = new StringBuilder();
            foreach (var run in Runs(offsets.Keys))
            {
                index.Append(CultureInfo.InvariantCulture, $"{run.First} {run.Count} ");
                for (int n = run.First; n < run.First + run.Count; n++)
                {
                    long a = offsets[n].Offset;
                    rows.WriteByte(1);
                    rows.WriteByte((byte)(a >> 24)); rows.WriteByte((byte)(a >> 16)); rows.WriteByte((byte)(a >> 8)); rows.WriteByte((byte)a);
                    rows.WriteByte((byte)(offsets[n].Generation >> 8)); rows.WriteByte((byte)offsets[n].Generation);
                }
            }
            var data = rows.ToArray();
            Write(o, $"{self} 0 obj\n<</Type/XRef/Size {size}/W[1 4 2]/Index[{index.ToString().Trim()}]{extra}/Length {data.Length}>>\nstream\n");
            o.Write(data);
            Write(o, $"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n");
        }
        return o.ToArray();

        static void Write(Stream s, string t) => s.Write(Encoding.Latin1.GetBytes(t));
    }

    private static IEnumerable<(int First, int Count)> Runs(IEnumerable<int> numbers)
    {
        int first = -1, count = 0;
        foreach (var n in numbers)
        {
            if (count > 0 && n == first + count) { count++; continue; }
            if (count > 0) yield return (first, count);
            first = n; count = 1;
        }
        if (count > 0) yield return (first, count);
    }

    [GeneratedRegex(@"startxref\s+(\d+)")]
    private static partial Regex StartXrefRx();
    [GeneratedRegex(@"/Root\s+(\d+\s+\d+\s+R)")]
    private static partial Regex RootRx();
    [GeneratedRegex(@"/Info\s+(\d+\s+\d+\s+R)")]
    private static partial Regex InfoRx();
    [GeneratedRegex(@"/ID\s*\[[^\]]*\]")]
    private static partial Regex IdRx();
    [GeneratedRegex(@"/Encrypt\s+(\d+\s+\d+\s+R)")]
    private static partial Regex EncryptRx();
    [GeneratedRegex(@"/Size\s+(\d+)")]
    private static partial Regex SizeRx();
}

/// <summary>PDF syntax for objects PDFsharp read: what an incremental update writes again with a change.</summary>
internal static class PdfSyntax
{
    public static string Write(PdfItem? item)
    {
        var sb = new StringBuilder();
        Write(item, sb, 0);
        return sb.ToString();
    }

    private static void Write(PdfItem? item, StringBuilder sb, int depth)
    {
        if (depth > 64) { sb.Append("null"); return; }
        if (depth > 0 && item is PdfObject { Reference: { } own })
        {
            sb.Append(CultureInfo.InvariantCulture, $"{own.ObjectNumber} {own.GenerationNumber} R");
            return;
        }
        switch (item)
        {
            case PdfRectangle rc:
                sb.Append(CultureInfo.InvariantCulture, $"[{Num(rc.X1)} {Num(rc.Y1)} {Num(rc.X2)} {Num(rc.Y2)}]");
                break;
            case PdfDate date:
                sb.Append(Text(PdfDigitalSigner.PdfDate(new DateTimeOffset(date.Value))));
                break;
            case PdfStringObject so: sb.Append(Text(so.Value)); break;
            case PdfIntegerObject io: sb.Append(io.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfRealObject ro: sb.Append(ro.Value.ToString("0.######", CultureInfo.InvariantCulture)); break;
            case PdfBooleanObject bo: sb.Append(bo.Value ? "true" : "false"); break;
            case PdfNameObject no: sb.Append(Name(no.Value)); break;
            case null or PdfNull: sb.Append("null"); break;
            case PdfReference r: sb.Append(CultureInfo.InvariantCulture, $"{r.ObjectNumber} {r.GenerationNumber} R"); break;
            case PdfLiteral lit: sb.Append(lit.Value); break;
            case PdfName n: sb.Append(Name(n.Value)); break;
            case PdfInteger i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfLongInteger l: sb.Append(l.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfReal f: sb.Append(f.Value.ToString("0.######", CultureInfo.InvariantCulture)); break;
            case PdfBoolean b: sb.Append(b.Value ? "true" : "false"); break;
            case PdfString s: sb.Append(Text(s.Value)); break;
            case PdfArray a:
                sb.Append('[');
                foreach (var e in a.Elements) { Write(e, sb, depth + 1); sb.Append(' '); }
                sb.Append(']');
                break;
            case PdfDictionary d:
                if (d.Reference is { } dr && depth > 0) { sb.Append(CultureInfo.InvariantCulture, $"{dr.ObjectNumber} {dr.GenerationNumber} R"); break; }
                sb.Append("<<");
                foreach (var k in d.Elements.Keys)
                {
                    sb.Append(Name(k)).Append(' ');
                    Write(d.Elements[k], sb, depth + 1);
                    sb.Append(' ');
                }
                sb.Append(">>");
                break;
            default: sb.Append("null"); break;
        }
    }

    /// <summary>A name with the characters PDF does not allow in names written as #hh.</summary>
    public static string Name(string name)
    {
        var sb = new StringBuilder("/");
        foreach (var b in Encoding.UTF8.GetBytes(name.TrimStart('/')))
            if (b is > 0x20 and < 0x7F && "()<>[]{}/%#".IndexOf((char)b) < 0) sb.Append((char)b);
            else sb.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>A text string as hex: Latin-1 bytes when it fits, otherwise UTF-16BE with a byte-order mark.</summary>
    public static string Text(string s) =>
        s.All(c => c <= 0xFF)
            ? "<" + Convert.ToHexString(Encoding.Latin1.GetBytes(s)) + ">"
            : "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";

    public static string Num(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
