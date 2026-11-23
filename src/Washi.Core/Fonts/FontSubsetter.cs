// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;

namespace Washi.Core.Fonts;

/// <summary>
/// Font subsets for embedding a few characters of a large CJK font: glyph numbers are kept (the codes in the PDF stay
/// valid), only the outlines of the glyphs not used are left out. TrueType: a new glyf and loca with only the glyphs
/// used (and the parts of composite glyphs); CID-keyed CFF: the CharStrings of unused glyphs become a bare endchar and
/// the CFF is laid out again around them.
/// </summary>
internal static class FontSubsetter
{
    /// <summary>A TrueType font (sfnt) with only <paramref name="keep"/>'s outlines (glyph 0 is always kept).</summary>
    public static byte[] TrueType(byte[] sfnt, ISet<int> keep)
    {
        var tables = Tables(sfnt);
        var head = tables["head"];
        bool longLoca = BinaryPrimitives.ReadInt16BigEndian(sfnt.AsSpan(head.Offset + 50)) == 1;
        int numGlyphs = U16(sfnt, tables["maxp"].Offset + 4);
        var loca = tables["loca"];
        var glyf = tables["glyf"];
        int Loc(int g) => longLoca ? (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(loca.Offset + 4 * g))
                                   : U16(sfnt, loca.Offset + 2 * g) * 2;

        // the glyphs used, with the parts composite glyphs are made of
        var all = new HashSet<int>(keep.Where(g => g >= 0 && g < numGlyphs)) { 0 };
        var todo = new Stack<int>(all);
        while (todo.Count > 0)
        {
            int g = todo.Pop();
            int start = glyf.Offset + Loc(g), end = glyf.Offset + Loc(g + 1);
            if (end - start < 10 || BinaryPrimitives.ReadInt16BigEndian(sfnt.AsSpan(start)) >= 0) continue;
            int p = start + 10;
            while (true)
            {
                int flags = U16(sfnt, p), part = U16(sfnt, p + 2);
                if (part < numGlyphs && all.Add(part)) todo.Push(part);
                p += 4 + ((flags & 0x1) != 0 ? 4 : 2) + ((flags & 0x8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0);
                if ((flags & 0x20) == 0) break;
            }
        }

        // new glyf and loca (long offsets)
        var newGlyf = new MemoryStream();
        var newLoca = new byte[4 * (numGlyphs + 1)];
        for (int g = 0; g < numGlyphs; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(4 * g), (uint)newGlyf.Length);
            if (!all.Contains(g)) continue;
            int start = glyf.Offset + Loc(g), end = glyf.Offset + Loc(g + 1);
            newGlyf.Write(sfnt, start, end - start);
            while (newGlyf.Length % 4 != 0) newGlyf.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(4 * numGlyphs), (uint)newGlyf.Length);

        var headCopy = sfnt.AsSpan(head.Offset, head.Length).ToArray();
        BinaryPrimitives.WriteInt16BigEndian(headCopy.AsSpan(50), 1);   // long loca
        BinaryPrimitives.WriteUInt32BigEndian(headCopy.AsSpan(8), 0);   // checksum adjustment: not kept

        var output = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = headCopy, ["loca"] = newLoca, ["glyf"] = newGlyf.ToArray(),
        };
        // the codes in the PDF are glyph numbers: no cmap is needed, and the glyph names go (a short post table)
        foreach (var tag in new[] { "hhea", "hmtx", "maxp", "cvt ", "fpgm", "prep", "OS/2", "name" })
            if (tables.TryGetValue(tag, out var t)) output[tag] = sfnt.AsSpan(t.Offset, t.Length).ToArray();
        if (tables.TryGetValue("post", out var post) && post.Length >= 32)
        {
            var p3 = sfnt.AsSpan(post.Offset, 32).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(p3, 0x00030000);
            output["post"] = p3;
        }
        return Sfnt(0x00010000, output);
    }

    /// <summary>A CID-keyed (or name-keyed) CFF with the CharStrings of glyphs not in <paramref name="keep"/> emptied.</summary>
    public static byte[] Cff(byte[] cff, ISet<int> keep)
    {
        int p = cff[2];
        var header = cff.AsSpan(0, p).ToArray();
        int namesStart = p;
        Index(cff, ref p);
        var nameIndex = cff.AsSpan(namesStart, p - namesStart).ToArray();
        var topItems = Index(cff, ref p);
        int stringsStart = p;
        Index(cff, ref p);
        var stringIndex = cff.AsSpan(stringsStart, p - stringsStart).ToArray();
        var gsubrs = Index(cff, ref p);

        var top = DictEntries(cff, topItems[0].Start, topItems[0].End);
        int Op(int op) => (int)Operands(cff, top, op)[^1];
        int csOffset = Op(17);
        int q = csOffset;
        var charStrings = Index(cff, ref q);
        int numGlyphs = charStrings.Count;
        bool cid = top.Any(e => e.Op == 1230);

        // the pieces copied as they are
        byte[]? charset = null, encoding = null, fdSelect = null;
        if (top.Any(e => e.Op == 15) && Op(15) > 2) charset = cff.AsSpan(Op(15), CharsetLength(cff, Op(15), numGlyphs)).ToArray();
        if (top.Any(e => e.Op == 16) && Op(16) > 1) encoding = cff.AsSpan(Op(16), EncodingLength(cff, Op(16))).ToArray();
        if (cid) fdSelect = cff.AsSpan(Op(1237), FdSelectLength(cff, Op(1237), numGlyphs)).ToArray();

        // which font DICT each glyph uses, and each one's local subroutines
        var fdOf = new int[numGlyphs];
        if (cid)
        {
            int fs = Op(1237);
            if (cff[fs] == 0) for (int g = 0; g < numGlyphs; g++) fdOf[g] = cff[fs + 1 + g];
            else
            {
                int ranges = U16(cff, fs + 1);
                for (int r = 0; r < ranges; r++)
                {
                    int first = U16(cff, fs + 3 + 3 * r), fd = cff[fs + 3 + 3 * r + 2];
                    int next = r + 1 < ranges ? U16(cff, fs + 3 + 3 * (r + 1)) : U16(cff, fs + 3 + 3 * ranges);
                    for (int g = first; g < next && g < numGlyphs; g++) fdOf[g] = fd;
                }
            }
        }
        var privates = new List<(int Size, int Offset)>();
        if (cid)
        {
            int fa0 = Op(1236);
            foreach (var fd in Index(cff, ref fa0))
            {
                var pr = Operands(cff, DictEntries(cff, fd.Start, fd.End), 18);
                privates.Add(((int)pr[0], (int)pr[1]));
            }
        }
        else if (top.Any(e => e.Op == 18)) { var pr = Operands(cff, top, 18); privates.Add(((int)pr[0], (int)pr[1])); }
        var locals = privates.Select(pv =>
        {
            var entries = DictEntries(cff, pv.Offset, pv.Offset + pv.Size);
            if (!entries.Any(e => e.Op == 19)) return new List<(int Start, int End)>();
            int at = pv.Offset + (int)Operands(cff, entries, 19)[0];
            return Index(cff, ref at);
        }).ToList();

        // the subroutines the kept glyphs call (directly or through other subroutines)
        var usedGlobal = new HashSet<int>();
        var usedLocal = locals.Select(_ => new HashSet<int>()).ToList();
        for (int g = 0; g < numGlyphs; g++)
            if (g == 0 || keep.Contains(g))
            {
                int fd = locals.Count == 0 ? -1 : Math.Min(fdOf[g], locals.Count - 1);
                int stems = 0;
                var stack = new List<double>();
                Scan(cff, charStrings[g], fd < 0 ? [] : locals[fd], gsubrs, fd < 0 ? new HashSet<int>() : usedLocal[fd], usedGlobal, stack, ref stems, 0);
            }
        // unused subroutines become a bare return (their numbers stay, so no glyph needs rewriting)
        byte[] Pruned(List<(int Start, int End)> items, HashSet<int> used) =>
            WriteIndex([.. items.Select((it, i) => used.Contains(i) ? cff.AsSpan(it.Start, it.End - it.Start).ToArray() : new byte[] { 11 })]);
        var gsubrIndex = Pruned(gsubrs, usedGlobal);
        int privateNo = 0;

        // the private dictionaries, each with its local subroutines right after it
        byte[] PrivateBlock(int size, int offset)
        {
            var entries = DictEntries(cff, offset, offset + size);
            byte[]? subrs = null;
            int me = privateNo++;
            if (entries.Any(e => e.Op == 19)) subrs = Pruned(locals[me], usedLocal[me]);
            // Subrs written as a 5-byte number, so the dictionary's size is known before its value is
            var dict = WriteDict(cff, entries, new Dictionary<int, int[]> { [19] = [0] });
            if (subrs is null) return dict;
            dict = WriteDict(cff, entries, new Dictionary<int, int[]> { [19] = [dict.Length] });
            return [.. dict, .. subrs];
        }

        // new CharStrings: the glyphs not kept become endchar
        var kept = new List<byte[]>(numGlyphs);
        for (int g = 0; g < numGlyphs; g++)
            kept.Add(g == 0 || keep.Contains(g) ? cff.AsSpan(charStrings[g].Start, charStrings[g].End - charStrings[g].Start).ToArray() : [0x0E]);
        var csIndex = WriteIndex(kept);

        // the font dictionaries (CID) or the one private dictionary (name-keyed)
        List<(List<Entry> Entries, byte[] Private)> fds = [];
        byte[]? topPrivate = null;
        if (cid)
        {
            int fa = Op(1236);
            foreach (var fd in Index(cff, ref fa))
            {
                var entries = DictEntries(cff, fd.Start, fd.End);
                var pr = Operands(cff, entries, 18);
                fds.Add((entries, PrivateBlock((int)pr[0], (int)pr[1])));
            }
        }
        else if (top.Any(e => e.Op == 18))
        {
            var pr = Operands(cff, top, 18);
            topPrivate = PrivateBlock((int)pr[0], (int)pr[1]);
        }

        // lay it out: the Top DICT's offsets are 5-byte numbers, so its size does not depend on them
        byte[] TopDict(Dictionary<int, int[]> offsets) => WriteIndex([WriteDict(cff, top, offsets)]);
        var zero = new Dictionary<int, int[]> { [17] = [0] };
        if (charset is not null) zero[15] = [0];
        if (encoding is not null) zero[16] = [0];
        if (cid) { zero[1236] = [0]; zero[1237] = [0]; }
        if (topPrivate is not null) zero[18] = [0, 0];
        int topSize = TopDict(zero).Length;

        int at0 = header.Length + nameIndex.Length + topSize + stringIndex.Length + gsubrIndex.Length;
        var offsets = new Dictionary<int, int[]>();
        int pos = at0;
        if (charset is not null) { offsets[15] = [pos]; pos += charset.Length; }
        if (encoding is not null) { offsets[16] = [pos]; pos += encoding.Length; }
        if (fdSelect is not null) { offsets[1237] = [pos]; pos += fdSelect.Length; }
        offsets[17] = [pos];
        pos += csIndex.Length;
        byte[]? fdArray = null;
        if (cid)
        {
            // the font DICTs point at their private blocks, which follow the FDArray
            var dicts = fds.Select(f => WriteDict(cff, f.Entries, new Dictionary<int, int[]> { [18] = [f.Private.Length, 0] })).ToList();
            int fdArraySize = WriteIndex(dicts).Length;
            int privateAt = pos + fdArraySize;
            dicts.Clear();
            foreach (var f in fds)
            {
                dicts.Add(WriteDict(cff, f.Entries, new Dictionary<int, int[]> { [18] = [f.Private.Length, privateAt] }));
                privateAt += f.Private.Length;
            }
            fdArray = WriteIndex(dicts);
            offsets[1236] = [pos];
            pos += fdArray.Length;
        }
        if (topPrivate is not null) offsets[18] = [topPrivate.Length, pos];

        var o = new MemoryStream();
        o.Write(header);
        o.Write(nameIndex);
        o.Write(TopDict(offsets));
        o.Write(stringIndex);
        o.Write(gsubrIndex);
        if (charset is not null) o.Write(charset);
        if (encoding is not null) o.Write(encoding);
        if (fdSelect is not null) o.Write(fdSelect);
        o.Write(csIndex);
        if (fdArray is not null) { o.Write(fdArray); foreach (var f in fds) o.Write(f.Private); }
        if (topPrivate is not null) o.Write(topPrivate);
        return o.ToArray();
    }

    /// <summary>
    /// Walks a Type 2 charstring for the subroutines it calls: numbers go on the stack, callsubr/callgsubr take the top
    /// one (plus the bias); hint operators count stems, since hintmask and cntrmask are followed by one bit per stem.
    /// </summary>
    private static void Scan(byte[] b, (int Start, int End) cs, List<(int Start, int End)> locals, List<(int Start, int End)> globals,
                             HashSet<int> usedLocal, HashSet<int> usedGlobal, List<double> stack, ref int stems, int depth)
    {
        if (depth > 10) return;
        int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;
        int i = cs.Start;
        while (i < cs.End)
        {
            int b0 = b[i];
            if (b0 == 28) { stack.Add(BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(i + 1))); i += 3; continue; }
            if (b0 >= 32 && b0 <= 246) { stack.Add(b0 - 139); i++; continue; }
            if (b0 >= 247 && b0 <= 250) { stack.Add((b0 - 247) * 256 + b[i + 1] + 108); i += 2; continue; }
            if (b0 >= 251 && b0 <= 254) { stack.Add(-(b0 - 251) * 256 - b[i + 1] - 108); i += 2; continue; }
            if (b0 == 255) { stack.Add(BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(i + 1)) / 65536.0); i += 5; continue; }
            switch (b0)
            {
                case 1 or 3 or 18 or 23:                 // hstem, vstem, hstemhm, vstemhm
                    stems += stack.Count / 2;
                    stack.Clear();
                    i++;
                    break;
                case 19 or 20:                           // hintmask, cntrmask (after an implicit vstem)
                    stems += stack.Count / 2;
                    stack.Clear();
                    i += 1 + (stems + 7) / 8;
                    break;
                case 10 or 29:                           // callsubr, callgsubr
                {
                    var list = b0 == 10 ? locals : globals;
                    if (stack.Count == 0 || list.Count == 0) { i++; break; }
                    int n = (int)stack[^1] + Bias(list.Count);
                    stack.RemoveAt(stack.Count - 1);
                    i++;
                    if (n < 0 || n >= list.Count) break;
                    var used = b0 == 10 ? usedLocal : usedGlobal;
                    used.Add(n);
                    Scan(b, list[n], locals, globals, usedLocal, usedGlobal, stack, ref stems, depth + 1);
                    break;
                }
                case 11: return;                         // return
                case 14: stack.Clear(); return;          // endchar
                case 12: stack.Clear(); i += 2; break;   // escape: two-byte operators
                default: stack.Clear(); i++; break;
            }
        }
    }

    // ------------------------------------------------------------------ CFF pieces

    private sealed record Entry(int Op, int Start, int End);   // the operands' bytes

    private static List<Entry> DictEntries(byte[] b, int p, int end)
    {
        var list = new List<Entry>();
        int start = p;
        while (p < end)
        {
            int b0 = b[p];
            if (b0 <= 21)
            {
                int op = b0 == 12 ? 1200 + b[p + 1] : b0;
                list.Add(new Entry(op, start, p));
                p += b0 == 12 ? 2 : 1;
                start = p;
            }
            else if (b0 == 28) p += 3;
            else if (b0 == 29) p += 5;
            else if (b0 == 30) { p++; while (p < end && (b[p] & 0x0F) != 0x0F && (b[p] >> 4) != 0x0F) p++; p++; }
            else if (b0 <= 246) p++;
            else if (b0 <= 254) p += 2;
            else p++;
        }
        return list;
    }

    private static List<double> Operands(byte[] b, List<Entry> entries, int op)
    {
        var e = entries.First(x => x.Op == op);
        var list = new List<double>();
        int p = e.Start;
        while (p < e.End)
        {
            int b0 = b[p];
            if (b0 == 28) { list.Add(BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(p + 1))); p += 3; }
            else if (b0 == 29) { list.Add(BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p + 1))); p += 5; }
            else if (b0 == 30) { p++; while (p < e.End && (b[p] & 0x0F) != 0x0F && (b[p] >> 4) != 0x0F) p++; p++; list.Add(0); }
            else if (b0 <= 246) { list.Add(b0 - 139); p++; }
            else if (b0 <= 250) { list.Add((b0 - 247) * 256 + b[p + 1] + 108); p += 2; }
            else if (b0 <= 254) { list.Add(-(b0 - 251) * 256 - b[p + 1] - 108); p += 2; }
            else p++;
        }
        return list;
    }

    /// <summary>The dictionary again: its entries as they were, except those in <paramref name="replace"/>, written as 5-byte numbers.</summary>
    private static byte[] WriteDict(byte[] b, List<Entry> entries, Dictionary<int, int[]> replace)
    {
        var o = new MemoryStream();
        foreach (var e in entries)
        {
            if (replace.TryGetValue(e.Op, out var values))
                foreach (var v in values) { o.WriteByte(29); Span<byte> n = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(n, v); o.Write(n); }
            else o.Write(b, e.Start, e.End - e.Start);
            if (e.Op >= 1200) { o.WriteByte(12); o.WriteByte((byte)(e.Op - 1200)); }
            else o.WriteByte((byte)e.Op);
        }
        return o.ToArray();
    }

    private static byte[] WriteIndex(List<byte[]> items)
    {
        if (items.Count == 0) return [0, 0];
        long total = items.Sum(i => (long)i.Length) + 1;
        int offSize = total < 0x100 ? 1 : total < 0x10000 ? 2 : total < 0x1000000 ? 3 : 4;
        var o = new MemoryStream();
        o.WriteByte((byte)(items.Count >> 8)); o.WriteByte((byte)items.Count); o.WriteByte((byte)offSize);
        long off = 1;
        void Off(long v) { for (int k = offSize - 1; k >= 0; k--) o.WriteByte((byte)(v >> (8 * k))); }
        Off(off);
        foreach (var i in items) { off += i.Length; Off(off); }
        foreach (var i in items) o.Write(i);
        return o.ToArray();
    }

    private static List<(int Start, int End)> Index(byte[] b, ref int p)
    {
        int count = U16(b, p);
        if (count == 0) { p += 2; return []; }
        int offSize = b[p + 2], offs = p + 3, data = offs + (count + 1) * offSize - 1;
        int Off(int i) { int v = 0; for (int k = 0; k < offSize; k++) v = (v << 8) | b[offs + i * offSize + k]; return v; }
        var items = Enumerable.Range(0, count).Select(i => (data + Off(i), data + Off(i + 1))).ToList();
        p = data + Off(count);
        return items;
    }

    private static int CharsetLength(byte[] b, int at, int numGlyphs)
    {
        int format = b[at], p = at + 1, gid = 1;
        if (format == 0) return 1 + 2 * (numGlyphs - 1);
        while (gid < numGlyphs)
        {
            int left = format == 1 ? b[p + 2] : U16(b, p + 2);
            p += format == 1 ? 3 : 4;
            gid += left + 1;
        }
        return p - at;
    }

    private static int EncodingLength(byte[] b, int at)
    {
        int format = b[at] & 0x7F, n = b[at + 1];
        int len = 2 + (format == 0 ? n : 2 * n);
        if ((b[at] & 0x80) != 0) len += 1 + 3 * b[at + len];
        return len;
    }

    private static int FdSelectLength(byte[] b, int at, int numGlyphs) =>
        b[at] == 0 ? 1 + numGlyphs : 1 + 2 + 3 * U16(b, at + 1) + 2;

    // ------------------------------------------------------------------ sfnt

    private readonly record struct Table(int Offset, int Length);

    private static Dictionary<string, Table> Tables(byte[] b)
    {
        int n = U16(b, 4);
        var d = new Dictionary<string, Table>();
        for (int i = 0; i < n; i++)
        {
            int r = 12 + 16 * i;
            d[System.Text.Encoding.ASCII.GetString(b, r, 4)] = new Table((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(r + 8)),
                                                                        (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(r + 12)));
        }
        return d;
    }

    /// <summary>The CFF table of an OpenType font.</summary>
    public static byte[] CffTable(byte[] sfnt) => Tables(sfnt) is var t && t.TryGetValue("CFF ", out var c) ? sfnt.AsSpan(c.Offset, c.Length).ToArray() : [];

    private static byte[] Sfnt(uint version, SortedDictionary<string, byte[]> tables)
    {
        int n = tables.Count;
        var o = new MemoryStream();
        void U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(s, v); o.Write(s); }
        void W16(int v) { o.WriteByte((byte)(v >> 8)); o.WriteByte((byte)v); }
        int pow = 1, log = 0;
        while (pow * 2 <= n) { pow *= 2; log++; }
        U32(version); W16(n); W16(pow * 16); W16(log); W16(n * 16 - pow * 16);
        long offset = 12 + 16 * n;
        foreach (var (tag, data) in tables)
        {
            o.Write(System.Text.Encoding.ASCII.GetBytes(tag));
            U32(Checksum(data)); U32((uint)offset); U32((uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }
        foreach (var data in tables.Values)
        {
            o.Write(data);
            while (o.Length % 4 != 0) o.WriteByte(0);
        }
        return o.ToArray();
    }

    private static uint Checksum(byte[] d)
    {
        uint sum = 0;
        for (int i = 0; i < d.Length; i += 4)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (i + k < d.Length ? d[i + k] : 0u);
            sum += v;
        }
        return sum;
    }

    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
}
