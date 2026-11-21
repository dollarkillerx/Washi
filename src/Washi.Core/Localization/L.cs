// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Resources;

namespace Washi.Core.Localization;

/// <summary>
/// UI strings in English, Japanese, Simplified and Traditional Chinese, Korean, French and
/// German. The language follows the system UI culture; the WASHI_LANG environment variable
/// (e.g. "ja", "zh-Hant", "de") overrides it. Strings live in strings.py → Strings*.resx.
/// </summary>
public static class L
{
    public static readonly IReadOnlyList<string> Supported = ["en", "ja", "zh-Hans", "zh-Hant", "ko", "fr", "de"];

    private static readonly ResourceManager Resources = new("Washi.Core.Localization.Strings", typeof(L).Assembly);

    public static CultureInfo Culture { get; set; } = Resolve(Environment.GetEnvironmentVariable("WASHI_LANG"));

    public static CultureInfo Resolve(string? overrideTag)
    {
        if (!string.IsNullOrWhiteSpace(overrideTag))
        {
            try
            {
                // .NET accepts almost any tag as a custom culture, so only take the ones Washi speaks.
                var c = CultureInfo.GetCultureInfo(overrideTag.Trim());
                var lang = c.TwoLetterISOLanguageName;
                if (lang == "zh" && !c.Name.Contains('-')) return CultureInfo.GetCultureInfo("zh-Hans");
                if (Supported.Any(s => s.Split('-')[0] == lang)) return c;
            }
            catch (CultureNotFoundException) { /* fall through to the system language */ }
        }
        return CultureInfo.CurrentUICulture;
    }

    public static string Get(string key) => Get(key, Culture);

    public static string Get(string key, CultureInfo culture) => Resources.GetString(key, culture) ?? key;

    public static string F(string key, params object[] args) => string.Format(Culture, Get(key), args);
}
