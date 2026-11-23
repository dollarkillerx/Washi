// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Washi.Core.Forms;

/// <summary>
/// The writing system people will type into a form. It decides which font the form
/// fields ask the viewer for: CJK fonts are not interchangeable (a Japanese font has no
/// Hangul, a Korean font no kana, and the simplified and traditional Chinese fonts differ).
/// </summary>
public enum FormScript
{
    /// <summary>English, French, German and other Latin-script languages.</summary>
    Latin,
    Japanese,
    ChineseSimplified,
    ChineseTraditional,
    Korean,
}

public static class FormScripts
{
    /// <summary>Maps a language tag (ja, zh-Hans, zh-TW, ko, en, fr, de, ...) to a script.</summary>
    public static FormScript FromLanguage(string tag)
    {
        var t = tag.Trim().ToLowerInvariant().Replace('_', '-');
        return t switch
        {
            _ when t.StartsWith("ja") => FormScript.Japanese,
            _ when t.StartsWith("ko") => FormScript.Korean,
            "zh-hant" or "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant-tw" or "zh-hant-hk" => FormScript.ChineseTraditional,
            _ when t.StartsWith("zh") => FormScript.ChineseSimplified,
            _ => FormScript.Latin,
        };
    }

    public static string LanguageTag(FormScript s) => s switch
    {
        FormScript.Japanese => "ja",
        FormScript.ChineseSimplified => "zh-Hans",
        FormScript.ChineseTraditional => "zh-Hant",
        FormScript.Korean => "ko",
        _ => "en",
    };

    // Characters that exist in only one of the two Chinese standards (common words on forms).
    /// <summary>A character written only in Simplified Chinese.</summary>
    internal static bool IsSimplifiedOnly(char c) => SimplifiedOnly.Contains(c);

    private const string SimplifiedOnly = "们这个来时为说国会对发经过动后种进实现学问题员机关长东门车书见边电马鸟语认让议记设许证请调谁读账单户费贷额税务权议签录务";
    private const string TraditionalOnly = "們這個來時為說國會對發經過動後種進實現學問題員機關長東門車書見邊電馬鳥語認讓議記設許證請調誰讀帳單戶費貸額稅務權簽錄";

    /// <summary>
    /// Guesses the script from the page text. Returns null when the text layer gives no
    /// usable hint (no text, or garbled text from fonts without a Unicode mapping).
    /// </summary>
    public static FormScript? Detect(string text)
    {
        int kana = 0, hangul = 0, han = 0, latin = 0, simp = 0, trad = 0;
        foreach (var r in text.EnumerateRunes())
        {
            int v = r.Value;
            if (v is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF) kana++;
            else if (v is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F) hangul++;
            else if (v is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF)
            {
                han++;
                if (SimplifiedOnly.Contains((char)v)) simp++;
                if (TraditionalOnly.Contains((char)v)) trad++;
            }
            else if (Rune.IsLetter(r) && v < 0x250) latin++;
        }
        int cjk = kana + hangul + han;
        if (cjk == 0) return latin > 0 ? FormScript.Latin : null;
        if (hangul > cjk / 4) return FormScript.Korean;
        if (kana > 0) return FormScript.Japanese;
        if (han > 0) return trad > simp ? FormScript.ChineseTraditional : FormScript.ChineseSimplified;
        return null;
    }

    /// <summary>Fallback when the page text cannot tell: the user's UI language.</summary>
    public static FormScript FromCulture(CultureInfo culture) => FromLanguage(culture.Name);
}
