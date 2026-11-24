// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;

namespace Washi.Core.Pages;

/// <summary>
/// FlateDecode data: a complete zlib stream (header, deflate data ending in a final block, Adler-32). PDFsharp's own
/// encoder leaves its streams without the final block and checksum, which PDFium and PDFsharp read but strict readers
/// (pyHanko, zlib itself) refuse.
/// </summary>
internal static class Zlib
{
    public static byte[] Compress(byte[] data, bool best = true)
    {
        using var o = new MemoryStream(data.Length / 2 + 64);
        using (var z = new ZLibStream(o, best ? CompressionLevel.SmallestSize : CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return o.ToArray();
    }
}
