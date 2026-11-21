// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Washi.Core;

public static class TextQuality
{
    private static readonly (int Lo, int Hi)[] OkBlocks =
        [(0x20, 0x7E), (0xA0, 0x24F), (0x1E00, 0x1EFF), (0x2010, 0x2BFF),
         (0x3000, 0x30FF), (0x3100, 0x312F), (0x3130, 0x318F), (0x31F0, 0x31FF),
         (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xF900, 0xFAFF),
         (0x1100, 0x11FF), (0xAC00, 0xD7AF), (0xFF00, 0xFFEF)];

    /// <summary>False when the text is empty or decodes to garbage (fonts without a Unicode mapping).</summary>
    public static bool Readable(string text)
    {
        var cs = text.EnumerateRunes().Where(r => !Rune.IsWhiteSpace(r)).ToList();
        if (cs.Count == 0) return false;
        int ok = cs.Count(r => OkBlocks.Any(b => r.Value >= b.Lo && r.Value <= b.Hi));
        return ok / (double)cs.Count > 0.9;
    }
}
