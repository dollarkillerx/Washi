// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Washi.Ai;

/// <summary>
/// Replaces long numbers (マイナンバー, bank and card numbers, phone numbers: 8+ digits, spaces
/// and hyphens allowed) with placeholders before text goes to a model, and puts them back in
/// the model's answer. The model only learns "a 12-digit number", enough to pick a 12-box
/// field. Dates are left alone: forms ask for birthdays.
/// </summary>
public sealed partial class SecretMasker
{
    private readonly Dictionary<string, string> _byValue = new();
    private readonly Dictionary<string, string> _byPlaceholder = new();

    [GeneratedRegex(@"(?<![\d])(?:\d[ \-]?){7,}\d(?![\d])")]
    private static partial Regex LongNumber();

    [GeneratedRegex(@"^\d{4}[\-/.]\d{1,2}[\-/.]\d{1,2}$|^\d{1,2}[\-/.]\d{1,2}[\-/.]\d{4}$")]
    private static partial Regex DateLike();

    [GeneratedRegex(@"\{\{SECRET_\d+\}\}")]
    private static partial Regex Placeholder();

    public int Count => _byPlaceholder.Count;

    /// <summary>"{{SECRET_1}}: 12 digits" lines for the model.</summary>
    public IEnumerable<string> Hints =>
        _byPlaceholder.Select(kv => $"{kv.Key}: a {kv.Value.Count(char.IsAsciiDigit)}-digit number");

    public string Mask(string text) => LongNumber().Replace(text, m =>
    {
        var value = m.Value.Trim();
        if (DateLike().IsMatch(value)) return m.Value;
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());   // "1234 5678 9012" == "123456789012"
        if (!_byValue.TryGetValue(digits, out var ph))
        {
            ph = $"{{{{SECRET_{_byValue.Count + 1}}}}}";
            _byValue[digits] = ph;
            _byPlaceholder[ph] = value;
        }
        return m.Value.StartsWith(' ') ? " " + ph : ph;
    });

    /// <summary>Puts the real numbers back. Digits only when the field takes digits (comb fields).</summary>
    public string Unmask(string text, bool digitsOnly = false) => Placeholder().Replace(text, m =>
        _byPlaceholder.TryGetValue(m.Value, out var v) ? (digitsOnly ? new string(v.Where(char.IsAsciiDigit).ToArray()) : v) : m.Value);

    public bool HasPlaceholder(string text) => Placeholder().IsMatch(text);
}
