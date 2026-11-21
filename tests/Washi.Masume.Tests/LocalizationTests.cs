// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Washi.Core.Localization;

namespace Washi.Masume.Tests;

public partial class LocalizationTests
{
    private static readonly ResourceManager Rm = new("Washi.Core.Localization.Strings", typeof(L).Assembly);

    // English is the neutral language: it lives in the main assembly, not in an "en" satellite.
    private static Dictionary<string, string> Strings(string culture) =>
        Rm.GetResourceSet(culture == "en" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(culture),
                          createIfNotExists: true, tryParents: false)!
          .Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    [GeneratedRegex(@"\{(\d+)(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    public static TheoryData<string> Cultures => [.. L.Supported.Where(c => c != "en")];

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Every_language_has_every_string_with_the_same_placeholders(string culture)
    {
        var en = Strings("en");
        var tr = Strings(culture);
        Assert.Equal(en.Keys.Order(), tr.Keys.Order());
        foreach (var (key, english) in en)
        {
            var want = Placeholder().Matches(english).Select(m => m.Groups[1].Value).Distinct().Order();
            var got = Placeholder().Matches(tr[key]).Select(m => m.Groups[1].Value).Distinct().Order();
            Assert.True(want.SequenceEqual(got), $"{culture}/{key}");
        }
        Assert.NotEqual(en["Usage"], tr["Usage"]);   // actually translated, not copied
    }

    [Theory]
    [InlineData("ja", "記入欄")]
    [InlineData("zh-CN", "填写栏")]
    [InlineData("zh-TW", "填寫欄")]
    [InlineData("ko", "입력란")]
    [InlineData("fr-FR", "champs")]
    [InlineData("de-DE", "Felder")]
    public void System_cultures_fall_back_to_their_language(string culture, string expected)
    {
        var text = string.Format(CultureInfo.GetCultureInfo(culture),
            L.Get("DetectSummary", CultureInfo.GetCultureInfo(culture)), "x", 3, "", "out", "1.0");
        Assert.Contains(expected, text);
    }

    [Theory]
    [InlineData("ko", "ko")]
    [InlineData("de-AT", "de-AT")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-TW", "zh-TW")]
    public void Washi_lang_overrides_the_system(string tag, string expected) =>
        Assert.Equal(expected, L.Resolve(tag).Name);

    [Fact]
    public void Unknown_override_falls_back_to_the_system() =>
        Assert.Equal(CultureInfo.CurrentUICulture, L.Resolve("not-a-language-xx-yy-zz"));
}
