// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Washi.Core.Pages;

/// <summary>
/// Reads PDF objects from bytes as plain values, for the places PDFsharp cannot go (a file encrypted for
/// certificates, which it cannot open): numbers (long, or <see cref="Real"/> as written), bool, null,
/// <see cref="Name"/> (as written, with its slash), strings (byte[]), arrays (List), dictionaries (Dictionary, in the
/// file's order) and <see cref="Ref"/>. Writes them back.
/// </summary>
internal sealed class PdfLexer(byte[] bytes, int position)
{
    public sealed record Name(string Value);
    public sealed record Ref(int Number, int Generation);
    public sealed record Real(string Text);

    private int _p = position;

    public (int Number, int Generation) ReadObjectHeader()
    {
        Skip();
        long n = (long)ReadNumber()!;
        Skip();
        long g = (long)ReadNumber()!;
        Skip();
        Expect("obj");
        return ((int)n, (int)g);
    }

    /// <summary>After an object: whether a stream follows, and where its data begins.</summary>
    public bool AtStream(out int start)
    {
        Skip();
        start = 0;
        if (!At("stream")) return false;
        _p += 6;
        if (_p < bytes.Length && bytes[_p] == '\r') _p++;
        if (_p < bytes.Length && bytes[_p] == '\n') _p++;
        start = _p;
        return true;
    }

    public object? ReadObject()
    {
        Skip();
        if (_p >= bytes.Length) throw new FormatException("unexpected end of the file");
        byte c = bytes[_p];
        if (c == '<' && Peek(1) == '<')
        {
            _p += 2;
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                Skip();
                if (At(">>")) { _p += 2; return d; }
                if (bytes[_p] != '/') throw new FormatException("a dictionary key is not a name");
                var key = ReadName().Value;
                d[key] = ReadObject();
            }
        }
        if (c == '<') return ReadHex();
        if (c == '(') return ReadLiteral();
        if (c == '[')
        {
            _p++;
            var a = new List<object?>();
            while (true)
            {
                Skip();
                if (bytes[_p] == ']') { _p++; return a; }
                a.Add(ReadObject());
            }
        }
        if (c == '/') return ReadName();
        if (c is (byte)'-' or (byte)'+' or (byte)'.' || c is >= (byte)'0' and <= (byte)'9')
        {
            var n = ReadNumber();
            if (n is long num && num >= 0)
            {
                int save = _p;
                Skip();
                if (_p < bytes.Length && bytes[_p] is >= (byte)'0' and <= (byte)'9' && ReadNumber() is long gen)
                {
                    Skip();
                    if (At("R") && (Peek(1) is 0 || IsDelimiter(Peek(1)) || IsSpace(Peek(1)))) { _p++; return new Ref((int)num, (int)gen); }
                }
                _p = save;
            }
            return n;
        }
        var word = ReadWord();
        return word switch
        {
            "true" => true,
            "false" => false,
            "null" => null,
            _ => throw new FormatException($"unexpected '{word}'"),
        };
    }

    private Name ReadName()
    {
        int start = _p++;
        while (_p < bytes.Length && !IsSpace(bytes[_p]) && !IsDelimiter(bytes[_p])) _p++;
        return new Name(Encoding.Latin1.GetString(bytes, start, _p - start));
    }

    private object? ReadNumber()
    {
        int start = _p;
        while (_p < bytes.Length && bytes[_p] is (byte)'-' or (byte)'+' or (byte)'.' or >= (byte)'0' and <= (byte)'9') _p++;
        var t = Encoding.ASCII.GetString(bytes, start, _p - start);
        return long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? l : new Real(t);
    }

    private string ReadWord()
    {
        int start = _p;
        while (_p < bytes.Length && !IsSpace(bytes[_p]) && !IsDelimiter(bytes[_p])) _p++;
        return Encoding.Latin1.GetString(bytes, start, _p - start);
    }

    private byte[] ReadHex()
    {
        _p++;
        var digits = new StringBuilder();
        while (_p < bytes.Length && bytes[_p] != '>')
        {
            if (!IsSpace(bytes[_p])) digits.Append((char)bytes[_p]);
            _p++;
        }
        _p++;
        if (digits.Length % 2 == 1) digits.Append('0');
        return Convert.FromHexString(digits.ToString());
    }

    private byte[] ReadLiteral()
    {
        _p++;
        var o = new List<byte>();
        int depth = 1;
        while (_p < bytes.Length)
        {
            byte c = bytes[_p++];
            if (c == '\\')
            {
                if (_p >= bytes.Length) break;
                byte e = bytes[_p++];
                switch (e)
                {
                    case (byte)'n': o.Add(10); break;
                    case (byte)'r': o.Add(13); break;
                    case (byte)'t': o.Add(9); break;
                    case (byte)'b': o.Add(8); break;
                    case (byte)'f': o.Add(12); break;
                    case (byte)'\r': if (_p < bytes.Length && bytes[_p] == '\n') _p++; break;   // a line continued
                    case (byte)'\n': break;
                    case >= (byte)'0' and <= (byte)'7':
                    {
                        int v = e - '0';
                        for (int i = 0; i < 2 && _p < bytes.Length && bytes[_p] is >= (byte)'0' and <= (byte)'7'; i++) v = v * 8 + bytes[_p++] - '0';
                        o.Add((byte)v);
                        break;
                    }
                    default: o.Add(e); break;
                }
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) break;
            if (c == '\r')
            {
                if (_p < bytes.Length && bytes[_p] == '\n') _p++;
                o.Add(10);
                continue;
            }
            o.Add(c);
        }
        return [.. o];
    }

    private void Skip()
    {
        while (_p < bytes.Length)
        {
            if (IsSpace(bytes[_p])) { _p++; continue; }
            if (bytes[_p] == '%') { while (_p < bytes.Length && bytes[_p] is not ((byte)'\n' or (byte)'\r')) _p++; continue; }
            break;
        }
    }

    private bool At(string s)
    {
        if (_p + s.Length > bytes.Length) return false;
        for (int i = 0; i < s.Length; i++) if (bytes[_p + i] != s[i]) return false;
        return true;
    }

    private void Expect(string s)
    {
        if (!At(s)) throw new FormatException($"'{s}' expected");
        _p += s.Length;
    }

    private byte Peek(int k) => _p + k < bytes.Length ? bytes[_p + k] : (byte)0;

    private static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    /// <summary>A value as PDF; strings as hex, each first through <paramref name="strings"/> when given.</summary>
    public static string Write(object? value, Func<byte[], byte[]>? strings)
    {
        var sb = new StringBuilder();
        Write(value, strings, sb);
        return sb.ToString();
    }

    private static void Write(object? value, Func<byte[], byte[]>? strings, StringBuilder sb)
    {
        switch (value)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case Real r: sb.Append(r.Text); break;
            case Name n: sb.Append(n.Value); break;
            case Ref r: sb.Append(CultureInfo.InvariantCulture, $"{r.Number} {r.Generation} R"); break;
            case byte[] s: sb.Append('<').Append(Convert.ToHexString(strings is null ? s : strings(s))).Append('>'); break;
            case List<object?> a:
                sb.Append('[');
                for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(' '); Write(a[i], strings, sb); }
                sb.Append(']');
                break;
            case Dictionary<string, object?> d:
                sb.Append("<<");
                foreach (var (k, v) in d) { sb.Append(k).Append(' '); Write(v, strings, sb); sb.Append(' '); }
                sb.Append(">>");
                break;
            default: throw new ArgumentException($"cannot write {value.GetType().Name}");
        }
    }
}
