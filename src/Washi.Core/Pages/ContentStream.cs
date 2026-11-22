// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Washi.Core.Pages;

/// <summary>
/// A page content stream split into operators, each with the exact byte range it came from, so an edit can drop a
/// few operators and copy every other byte unchanged (no re-serialisation, nothing else on the page can move).
/// </summary>
internal sealed class ContentStream
{
    /// <summary>One operator with its operands. <see cref="Start"/>..<see cref="End"/> covers both.</summary>
    /// <param name="Strings">Literal and hex strings among the operands (top level and inside arrays), decoded.</param>
    /// <param name="Names">Name operands (without the slash).</param>
    internal sealed record Op(string Name, int Start, int End, IReadOnlyList<byte[]> Strings, IReadOnlyList<string> Names, IReadOnlyList<string> Numbers);

    public byte[] Bytes { get; }
    public IReadOnlyList<Op> Ops { get; }

    public ContentStream(byte[] bytes)
    {
        Bytes = bytes;
        Ops = Parse(bytes);
    }

    private static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static List<Op> Parse(byte[] s)
    {
        var ops = new List<Op>();
        int i = 0, opStart = -1;
        var strings = new List<byte[]>();
        var names = new List<string>();
        var numbers = new List<string>();
        void Mark(int at) { if (opStart < 0) opStart = at; }

        while (i < s.Length)
        {
            byte c = s[i];
            if (IsSpace(c)) { i++; continue; }
            if (c == '%') { while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++; continue; }
            int start = i;
            switch (c)
            {
                case (byte)'(':
                    Mark(start); strings.Add(ReadLiteral(s, ref i)); continue;
                case (byte)'<' when i + 1 < s.Length && s[i + 1] == '<':
                    Mark(start); SkipDict(s, ref i); continue;
                case (byte)'<':
                    Mark(start); strings.Add(ReadHex(s, ref i)); continue;
                case (byte)'[':
                    Mark(start); ReadArray(s, ref i, strings, numbers); continue;
                case (byte)'/':
                    Mark(start); i++;
                    int n0 = i;
                    while (i < s.Length && !IsSpace(s[i]) && !IsDelimiter(s[i])) i++;
                    names.Add(Encoding.Latin1.GetString(s, n0, i - n0));
                    continue;
                case (byte)']' or (byte)'>' or (byte)')' or (byte)'{' or (byte)'}':
                    i++; continue;   // stray delimiter: skip (damaged stream)
            }
            // number or keyword
            int t0 = i;
            while (i < s.Length && !IsSpace(s[i]) && !IsDelimiter(s[i])) i++;
            var word = Encoding.Latin1.GetString(s, t0, i - t0);
            if (word.Length == 0) { i++; continue; }
            if (word[0] is >= '0' and <= '9' or '+' or '-' or '.' || word is "true" or "false" or "null")
            {
                Mark(start);
                numbers.Add(word);
                continue;
            }
            // an operator
            Mark(start);
            if (word == "BI") SkipInlineImage(s, ref i);
            ops.Add(new Op(word, opStart, i, [.. strings], [.. names], [.. numbers]));
            opStart = -1;
            strings.Clear(); names.Clear(); numbers.Clear();
        }
        return ops;
    }

    internal static byte[] ReadLiteral(byte[] s, ref int i)
    {
        var buf = new List<byte>();
        int depth = 0;
        i++; // (
        while (i < s.Length)
        {
            byte c = s[i++];
            if (c == '\\' && i < s.Length)
            {
                byte e = s[i++];
                switch (e)
                {
                    case (byte)'n': buf.Add(10); break;
                    case (byte)'r': buf.Add(13); break;
                    case (byte)'t': buf.Add(9); break;
                    case (byte)'b': buf.Add(8); break;
                    case (byte)'f': buf.Add(12); break;
                    case (byte)'\r': if (i < s.Length && s[i] == '\n') i++; break;   // line continuation
                    case (byte)'\n': break;
                    case >= (byte)'0' and <= (byte)'7':
                        int v = e - '0';
                        for (int k = 0; k < 2 && i < s.Length && s[i] is >= (byte)'0' and <= (byte)'7'; k++) v = v * 8 + (s[i++] - '0');
                        buf.Add((byte)v);
                        break;
                    default: buf.Add(e); break;
                }
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') { if (depth == 0) break; depth--; }
            buf.Add(c);
        }
        return [.. buf];
    }

    internal static byte[] ReadHex(byte[] s, ref int i)
    {
        var digits = new StringBuilder();
        i++; // <
        while (i < s.Length && s[i] != '>') { if (Uri.IsHexDigit((char)s[i])) digits.Append((char)s[i]); i++; }
        i++; // >
        if (digits.Length % 2 == 1) digits.Append('0');
        return Convert.FromHexString(digits.ToString());
    }

    private static void SkipDict(byte[] s, ref int i)
    {
        int depth = 0;
        while (i < s.Length)
        {
            if (s[i] == '(') { ReadLiteral(s, ref i); continue; }
            if (s[i] == '<' && i + 1 < s.Length && s[i + 1] == '<') { depth++; i += 2; continue; }
            if (s[i] == '>' && i + 1 < s.Length && s[i + 1] == '>') { depth--; i += 2; if (depth == 0) return; continue; }
            i++;
        }
    }

    private static void ReadArray(byte[] s, ref int i, List<byte[]> strings, List<string> numbers)
    {
        i++; // [
        while (i < s.Length)
        {
            byte c = s[i];
            if (IsSpace(c)) { i++; continue; }
            if (c == ']') { i++; return; }
            if (c == '(') { strings.Add(ReadLiteral(s, ref i)); continue; }
            if (c == '<' && i + 1 < s.Length && s[i + 1] == '<') { SkipDict(s, ref i); continue; }
            if (c == '<') { strings.Add(ReadHex(s, ref i)); continue; }
            if (c == '[') { ReadArray(s, ref i, strings, numbers); continue; }
            int t0 = i;
            i++;
            while (i < s.Length && !IsSpace(s[i]) && !IsDelimiter(s[i])) i++;
            numbers.Add(Encoding.Latin1.GetString(s, t0, i - t0));
        }
    }

    /// <summary>
    /// The operands of <paramref name="op"/> in order: numbers (double), strings (byte[]), names (string, with the
    /// slash), arrays (List&lt;object&gt;) and dictionaries (Dictionary&lt;string, object&gt;).
    /// </summary>
    internal List<object> Operands(Op op)
    {
        var list = new List<object>();
        int i = op.Start;
        while (i < op.End)
        {
            var v = ReadValue(Bytes, ref i, op.End);
            if (v is null) break;
            list.Add(v);
        }
        if (list.Count > 0 && list[^1] is string last && last == op.Name) list.RemoveAt(list.Count - 1);
        return list;
    }

    private static object? ReadValue(byte[] s, ref int i, int end)
    {
        while (i < end)
        {
            byte c = s[i];
            if (IsSpace(c)) { i++; continue; }
            if (c == '%') { while (i < end && s[i] != '\n' && s[i] != '\r') i++; continue; }
            switch (c)
            {
                case (byte)'(': return ReadLiteral(s, ref i);
                case (byte)'<' when i + 1 < end && s[i + 1] == '<':
                {
                    i += 2;
                    var d = new Dictionary<string, object>(StringComparer.Ordinal);
                    while (i < end)
                    {
                        while (i < end && IsSpace(s[i])) i++;
                        if (i + 1 < end && s[i] == '>' && s[i + 1] == '>') { i += 2; break; }
                        if (ReadValue(s, ref i, end) is not string key || !key.StartsWith('/')) break;
                        if (ReadValue(s, ref i, end) is { } value) d[key] = value;
                    }
                    return d;
                }
                case (byte)'<': return ReadHex(s, ref i);
                case (byte)'[':
                {
                    i++;
                    var a = new List<object>();
                    while (i < end)
                    {
                        while (i < end && IsSpace(s[i])) i++;
                        if (i < end && s[i] == ']') { i++; break; }
                        if (ReadValue(s, ref i, end) is not { } v) break;
                        a.Add(v);
                    }
                    return a;
                }
                case (byte)'/':
                {
                    int n0 = i++;
                    while (i < end && !IsSpace(s[i]) && !IsDelimiter(s[i])) i++;
                    return Encoding.Latin1.GetString(s, n0, i - n0);
                }
                case (byte)']' or (byte)'>' or (byte)')' or (byte)'{' or (byte)'}': i++; continue;
            }
            int t0 = i;
            while (i < end && !IsSpace(s[i]) && !IsDelimiter(s[i])) i++;
            var word = Encoding.Latin1.GetString(s, t0, i - t0);
            if (word.Length == 0) { i++; continue; }
            if (double.TryParse(word, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num)) return num;
            return word;   // the operator itself, true/false/null
        }
        return null;
    }

    /// <summary>After BI: the dictionary, ID, one white-space byte, the data, then EI (white-space delimited).</summary>
    private static void SkipInlineImage(byte[] s, ref int i)
    {
        // find the ID keyword
        while (i + 1 < s.Length && !(s[i] == 'I' && s[i + 1] == 'D' && (i == 0 || IsSpace(s[i - 1])) && (i + 2 >= s.Length || IsSpace(s[i + 2]))))
        {
            if (s[i] == '(') { ReadLiteral(s, ref i); continue; }
            i++;
        }
        i += 3;
        while (i + 1 < s.Length)
        {
            if (s[i] == 'E' && s[i + 1] == 'I' && IsSpace(s[i - 1]) && (i + 2 >= s.Length || IsSpace(s[i + 2]) || IsDelimiter(s[i + 2])))
            {
                i += 2;
                return;
            }
            i++;
        }
        i = s.Length;
    }
}
