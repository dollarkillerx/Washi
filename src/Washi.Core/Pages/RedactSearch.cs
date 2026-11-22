// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;

namespace Washi.Core.Pages;

/// <summary>Kinds of personal data redaction can look for without being told the exact words.</summary>
public enum RedactPattern
{
    /// <summary>E-mail addresses.</summary>
    Email,
    /// <summary>Telephone numbers (03-1234-5678, 090 1234 5678, +81 3 1234 5678, (555) 123-4567).</summary>
    Phone,
    /// <summary>Long numbers such as ID, account and card numbers (7 digits or more, spaces and hyphens allowed).</summary>
    Number,
}

/// <summary>What to look for. Words match ignoring case (unless <see cref="MatchCase"/>), full-width and
/// half-width forms, and spaces: "山田太郎" also finds "山田 太郎".</summary>
/// <param name="Words">Words or phrases.</param>
/// <param name="Patterns">Kinds of data (e-mail addresses, phone numbers, long numbers).</param>
/// <param name="Regexes">.NET regular expressions, matched against the page text with single spaces between words.</param>
/// <param name="WholeWords">A word must not be part of a longer word (letters or digits right before or after it).</param>
public sealed record RedactSearch(IReadOnlyList<string> Words, IReadOnlyList<RedactPattern>? Patterns = null,
                                  IReadOnlyList<string>? Regexes = null, bool MatchCase = false, bool WholeWords = false)
{
    public bool IsEmpty => Words.All(string.IsNullOrWhiteSpace) && (Patterns?.Count ?? 0) == 0 && (Regexes?.All(string.IsNullOrWhiteSpace) ?? true);
}

/// <summary>
/// Finds <see cref="RedactSearch"/> matches in a sequence of units (one character each; null for a gap between words,
/// such as a space the text layer added). Each match is the list of unit indexes it covers.
/// </summary>
internal sealed class Matcher
{
    private readonly RedactSearch _search;
    private readonly List<string> _words;
    private readonly List<(Regex Rx, int MinDigits)> _regexes = [];

    public Matcher(RedactSearch search)
    {
        _search = search;
        _words = [.. search.Words.Select(w => Tight(w)).Where(w => w.Length > 0).Distinct()];
        var options = RegexOptions.CultureInvariant | (search.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        foreach (var p in search.Patterns ?? [])
            _regexes.Add((new Regex(p switch
            {
                RedactPattern.Email => @"[a-z0-9._%+\-]+@[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.[a-z]{2,}",
                // groups of digits split by spaces, hyphens, dots or brackets; at least 9 digits in all (no version numbers)
                RedactPattern.Phone => @"(?<![\w\-.+])(?:\+\d{1,3}[\s\-.]?)?(?:\(\d{1,5}\)[\s\-.]?|\d{1,5}[\s\-.])\d{1,4}[\s\-.]\d{3,4}(?![\w\-])|(?<!\d)0[5-9]0\d{8}(?!\d)",
                _ => @"(?<![\d\-])\d(?:[\s\-]?\d){6,}(?![\d\-])",
            }, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)), p == RedactPattern.Phone ? 9 : 0));
        foreach (var r in search.Regexes ?? [])
            if (!string.IsNullOrWhiteSpace(r))
                try { _regexes.Add((new Regex(r, options, TimeSpan.FromSeconds(2)), 0)); }
                catch (ArgumentException e) { throw new RedactException($"not a valid regular expression: {r} ({e.Message})", "Err_RedactRegex", r); }
    }

    /// <summary>One character's search form: compatibility forms folded (ｱ→ア, １→1), and lower case unless case matters.</summary>
    private string Norm(string unit)
    {
        var s = unit.Normalize(NormalizationForm.FormKC);
        return _search.MatchCase ? s : s.ToLowerInvariant();
    }

    private string Tight(string text)
    {
        var sb = new StringBuilder();
        foreach (var u in Units(text)) if (u is not null) sb.Append(Norm(u));
        return sb.ToString();
    }

    /// <summary>The characters of a string as units (white space as gaps).</summary>
    public static List<string?> Units(string text)
    {
        var list = new List<string?>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            string u = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? text.Substring(i++, 2) : text[i].ToString();
            list.Add(string.IsNullOrWhiteSpace(u) || char.IsControl(u, 0) ? null : u);
        }
        return list;
    }

    public List<List<int>> Find(IReadOnlyList<string?> units)
    {
        // the text twice: with single spaces between words (for patterns), and without spaces (for words)
        var spaced = new StringBuilder();
        var spacedAt = new List<int>();
        var tight = new StringBuilder();
        var tightAt = new List<int>();
        var unitSpaced = new int[units.Count];
        for (int i = 0; i < units.Count; i++)
        {
            unitSpaced[i] = spaced.Length;
            if (units[i] is not { } u)
            {
                if (spaced.Length > 0 && spaced[^1] != ' ') { spaced.Append(' '); spacedAt.Add(-1); }
                continue;
            }
            var n = Norm(u);
            foreach (var c in n) { spaced.Append(c); spacedAt.Add(i); tight.Append(c); tightAt.Add(i); }
        }
        var found = new List<List<int>>();
        var t = tight.ToString();
        foreach (var w in _words)
            for (int at = t.IndexOf(w, StringComparison.Ordinal); at >= 0; at = t.IndexOf(w, at + Math.Max(1, w.Length), StringComparison.Ordinal))
            {
                var idx = tightAt.GetRange(at, w.Length).Distinct().ToList();
                if (_search.WholeWords && !Whole(units, idx[0], idx[^1])) continue;
                found.Add(idx);
            }
        var s = spaced.ToString();
        foreach (var (rx, minDigits) in _regexes)
            try
            {
                foreach (Match m in rx.Matches(s))
                {
                    if (minDigits > 0 && m.Value.Count(char.IsAsciiDigit) < minDigits) continue;
                    var idx = spacedAt.GetRange(m.Index, m.Length).Where(i => i >= 0).Distinct().ToList();
                    if (idx.Count > 0) found.Add(idx);
                }
            }
            catch (RegexMatchTimeoutException) { throw new RedactException("the regular expression took too long", "Err_RedactRegexSlow"); }
        // overlapping matches become one
        found.Sort((a, b) => a[0].CompareTo(b[0]));
        var merged = new List<List<int>>();
        foreach (var f in found)
        {
            if (merged.Count > 0 && f[0] <= merged[^1].Max()) { merged[^1] = [.. merged[^1].Union(f).Order()]; continue; }
            merged.Add(f);
        }
        return merged;
    }

    private static bool Whole(IReadOnlyList<string?> units, int first, int last)
    {
        static bool Word(string? u) => u is not null && (char.IsLetterOrDigit(u, 0));
        return !(first > 0 && Word(units[first - 1]) && Word(units[first])) && !(last + 1 < units.Count && Word(units[last + 1]) && Word(units[last]));
    }

    public bool Matches(string text) => Find(Units(text)).Count > 0;

    /// <summary><paramref name="text"/> with every matched character replaced by ■.</summary>
    public string Mask(string text)
    {
        var units = Units(text);
        var hits = Find(units).SelectMany(m => m).ToHashSet();
        if (hits.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        int ui = 0;
        for (int i = 0; i < text.Length; i++, ui++)
        {
            bool pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length;
            if (hits.Contains(ui)) sb.Append('■');
            else sb.Append(pair ? text.AsSpan(i, 2) : text.AsSpan(i, 1));
            if (pair) i++;
        }
        return sb.ToString();
    }
}
