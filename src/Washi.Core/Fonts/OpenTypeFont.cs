// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Washi.Core.Fonts;

public sealed class FontNotUsableException(string reason, ushort fsType = 0) : Exception(reason)
{
    /// <summary>"license", "variable", "format".</summary>
    public string Reason { get; } = reason;
    /// <summary>OS/2 fsType when <see cref="Reason"/> is "license".</summary>
    public ushort FsType { get; } = fsType;
}

/// <summary>
/// The parts of a TrueType / OpenType font needed to embed it whole in a PDF:
/// metrics, the Unicode map, the embedding permission and, for CFF fonts, the glyph → CID map.
/// Reads .ttf, .otf and one face of a .ttc/.otc collection. Variable fonts are rejected.
/// </summary>
public sealed class OpenTypeFont
{
    /// <summary>A standalone sfnt file (a collection face is extracted).</summary>
    public required byte[] Data { get; init; }
    public required bool IsCff { get; init; }
    public required string PostScriptName { get; init; }
    public required int UnitsPerEm { get; init; }
    public required (short XMin, short YMin, short XMax, short YMax) BBox { get; init; }
    public required short Ascender { get; init; }
    public required short Descender { get; init; }
    public required short CapHeight { get; init; }
    public required double ItalicAngle { get; init; }
    public required ushort FsType { get; init; }
    public required ushort[] Advances { get; init; }
    public required IReadOnlyDictionary<int, int> UnicodeToGlyph { get; init; }
    /// <summary>Glyph index → CID. Identity for TrueType and name-keyed CFF.</summary>
    public required int[] GlyphToCid { get; init; }
    /// <summary>(Registry, Ordering, Supplement) of a CID-keyed CFF; null otherwise.</summary>
    public (string Registry, string Ordering, int Supplement)? Ros { get; init; }

    public int GlyphCount => Advances.Length;

    /// <summary>
    /// OS/2 fsType: 0 installable, 8 editable → may be embedded in a form people edit.
    /// 2 restricted, 4 preview-and-print only, 0x200 bitmap only → may not.
    /// </summary>
    public bool EmbeddingAllowedForEditing => (FsType & 0x000F) is 0 or 8 && (FsType & 0x0200) == 0;

    public static OpenTypeFont Load(string path, int faceIndex = 0) => Parse(File.ReadAllBytes(path), faceIndex);

    /// <summary>Throws <see cref="FontNotUsableException"/> for anything that cannot be embedded.</summary>
    public static OpenTypeFont Parse(byte[] file, int faceIndex = 0)
    {
        try
        {
            return ParseUnchecked(file, faceIndex);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new FontNotUsableException("format");   // truncated or not a font at all
        }
    }

    private static readonly string[] SfntTags = ["\0\u0001\0\0", "OTTO", "true", "ttcf"];

    private static OpenTypeFont ParseUnchecked(byte[] file, int faceIndex)
    {
        if (file.Length < 12 || !SfntTags.Contains(Tag(file, 0))) throw new FontNotUsableException("format");
        var sfnt = Tag(file, 0) == "ttcf" ? ExtractFace(file, faceIndex) : file;
        var tables = Directory(sfnt, 0);
        // the licence comes first: a font that may not be embedded is refused whatever its format
        if (tables.TryGetValue("OS/2", out var os2First))
        {
            ushort fs = (ushort)U16(sfnt, os2First.Offset + 8);
            if (!((fs & 0x000F) is 0 or 8 && (fs & 0x0200) == 0)) throw new FontNotUsableException("license", fs);
        }
        if (tables.ContainsKey("fvar") || tables.ContainsKey("CFF2"))
            throw new FontNotUsableException("variable");
        bool cff = tables.ContainsKey("CFF ");
        if (!cff && !(tables.ContainsKey("glyf") && tables.ContainsKey("loca")))
            throw new FontNotUsableException("format");
        foreach (var t in new[] { "head", "hhea", "hmtx", "maxp", "cmap" })
            if (!tables.ContainsKey(t)) throw new FontNotUsableException("format");

        var head = tables["head"].Offset;
        int upm = U16(sfnt, head + 18);
        var bbox = (S16(sfnt, head + 36), S16(sfnt, head + 38), S16(sfnt, head + 40), S16(sfnt, head + 42));
        var hhea = tables["hhea"].Offset;
        int numGlyphs = U16(sfnt, tables["maxp"].Offset + 4);
        int numHMetrics = U16(sfnt, hhea + 34);
        var adv = new ushort[numGlyphs];
        int hmtx = tables["hmtx"].Offset;
        for (int g = 0; g < numGlyphs; g++)
            adv[g] = (ushort)U16(sfnt, hmtx + 4 * Math.Min(g, numHMetrics - 1));

        ushort fsType = 0;
        short cap = (short)(bbox.Item4 * 0.7);
        if (tables.TryGetValue("OS/2", out var os2))
        {
            fsType = (ushort)U16(sfnt, os2.Offset + 8);
            if (U16(sfnt, os2.Offset) >= 2 && os2.Length >= 90) cap = S16(sfnt, os2.Offset + 88);
        }
        double italic = tables.TryGetValue("post", out var post) ? BinaryPrimitives.ReadInt32BigEndian(sfnt.AsSpan(post.Offset + 4)) / 65536.0 : 0;

        var gidToCid = Enumerable.Range(0, numGlyphs).ToArray();
        (string, string, int)? ros = null;
        if (cff) (gidToCid, ros) = CffCharset(sfnt, tables["CFF "].Offset, numGlyphs);

        return new OpenTypeFont
        {
            Data = sfnt,
            IsCff = cff,
            PostScriptName = PsName(sfnt, tables) ?? "EmbeddedFont",
            UnitsPerEm = upm,
            BBox = bbox,
            Ascender = S16(sfnt, hhea + 4),
            Descender = S16(sfnt, hhea + 6),
            CapHeight = cap,
            ItalicAngle = italic,
            FsType = fsType,
            Advances = adv,
            UnicodeToGlyph = Cmap(sfnt, tables["cmap"].Offset),
            GlyphToCid = gidToCid,
            Ros = ros,
        };
    }

    // ------------------------------------------------------------------ sfnt structure
    private readonly record struct Table(int Offset, int Length);

    private static Dictionary<string, Table> Directory(byte[] b, int at)
    {
        int n = U16(b, at + 4);
        var d = new Dictionary<string, Table>();
        for (int i = 0; i < n; i++)
        {
            int r = at + 12 + 16 * i;
            d[Tag(b, r)] = new Table((int)U32(b, r + 8), (int)U32(b, r + 12));
        }
        return d;
    }

    /// <summary>Copies one face of a collection into a standalone sfnt.</summary>
    private static byte[] ExtractFace(byte[] ttc, int index)
    {
        int count = (int)U32(ttc, 8);
        if (index < 0 || index >= count) throw new FontNotUsableException("format");
        int at = (int)U32(ttc, 12 + 4 * index);
        int n = U16(ttc, at + 4);
        var entries = Enumerable.Range(0, n).Select(i => at + 12 + 16 * i).ToList();
        var outp = new MemoryStream();
        outp.Write(ttc, at, 12);
        outp.Write(new byte[16 * n]);
        var records = new byte[16 * n];
        for (int i = 0; i < n; i++)
        {
            int r = entries[i];
            int off = (int)U32(ttc, r + 8), len = (int)U32(ttc, r + 12);
            int newOff = (int)outp.Position;
            outp.Write(ttc, off, len);
            while (outp.Position % 4 != 0) outp.WriteByte(0);
            Array.Copy(ttc, r, records, 16 * i, 8);   // tag + checksum
            BinaryPrimitives.WriteUInt32BigEndian(records.AsSpan(16 * i + 8), (uint)newOff);
            BinaryPrimitives.WriteUInt32BigEndian(records.AsSpan(16 * i + 12), (uint)len);
        }
        var result = outp.ToArray();
        Array.Copy(records, 0, result, 12, records.Length);
        return result;
    }

    private static string? PsName(byte[] b, Dictionary<string, Table> t)
    {
        if (!t.TryGetValue("name", out var name)) return null;
        int at = name.Offset, count = U16(b, at + 2), strings = at + U16(b, at + 4);
        string? mac = null;
        for (int i = 0; i < count; i++)
        {
            int r = at + 6 + 12 * i;
            int platform = U16(b, r), enc = U16(b, r + 2), id = U16(b, r + 6), len = U16(b, r + 8), off = U16(b, r + 10);
            if (id != 6) continue;
            if (platform == 3 && enc == 1) return Clean(Encoding.BigEndianUnicode.GetString(b, strings + off, len));
            if (platform == 1) mac = Clean(Encoding.Latin1.GetString(b, strings + off, len));
        }
        return mac;

        static string Clean(string s) => new(s.Where(c => c > 32 && c < 127 && "[](){}<>/%#".IndexOf(c) < 0).ToArray());
    }

    private static Dictionary<int, int> Cmap(byte[] b, int at)
    {
        int n = U16(b, at + 2);
        int best = -1, bestScore = -1;
        for (int i = 0; i < n; i++)
        {
            int r = at + 4 + 8 * i;
            int platform = U16(b, r), enc = U16(b, r + 2), off = at + (int)U32(b, r + 4);
            int format = U16(b, off);
            int score = (platform, enc, format) switch
            {
                (3, 10, 12) => 4,
                (0, _, 12) => 3,
                (3, 1, 4) => 2,
                (0, _, 4) => 1,
                _ => -1,
            };
            if (score > bestScore) { best = off; bestScore = score; }
        }
        var map = new Dictionary<int, int>();
        if (best < 0) return map;
        if (U16(b, best) == 12)
        {
            uint groups = U32(b, best + 12);
            for (uint g = 0; g < groups; g++)
            {
                int r = best + 16 + 12 * (int)g;
                uint start = U32(b, r), end = U32(b, r + 4), gid = U32(b, r + 8);
                for (uint c = start; c <= end && c <= 0x10FFFF; c++) map.TryAdd((int)c, (int)(gid + c - start));
            }
            return map;
        }
        int segX2 = U16(b, best + 6), seg = segX2 / 2;
        int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
        for (int s = 0; s < seg; s++)
        {
            int end = U16(b, ends + 2 * s), start = U16(b, starts + 2 * s);
            short delta = S16(b, deltas + 2 * s);
            int ro = U16(b, ranges + 2 * s);
            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int gid = ro == 0 ? (c + delta) & 0xFFFF
                    : U16(b, ranges + 2 * s + ro + 2 * (c - start)) is var g and > 0 ? (g + delta) & 0xFFFF : 0;
                if (gid != 0) map.TryAdd(c, gid);
            }
        }
        return map;
    }

    // ------------------------------------------------------------------ CFF
    private static (int[] GidToCid, (string, string, int)? Ros) CffCharset(byte[] b, int at, int numGlyphs)
    {
        int p = at + b[at + 2];                 // header size
        var names = Index(b, ref p);
        var topDicts = Index(b, ref p);
        var strings = Index(b, ref p);
        _ = names;
        var top = Dict(b, topDicts[0].Start, topDicts[0].End);
        var map = Enumerable.Range(0, numGlyphs).ToArray();
        if (!top.TryGetValue(1230, out var rosOps)) return (map, null);      // name-keyed: CID = GID

        string Sid(double sid) => sid < 391 ? "Adobe" : Encoding.ASCII.GetString(b, strings[(int)sid - 391].Start, strings[(int)sid - 391].End - strings[(int)sid - 391].Start);
        var ros = (Sid(rosOps[0]), Sid(rosOps[1]), (int)rosOps[2]);
        int cs = top.TryGetValue(15, out var csOps) ? (int)csOps[0] : 0;
        if (cs <= 2) return (map, ros);
        int q = at + cs, format = b[q++];
        int gid = 1;
        if (format == 0)
            for (; gid < numGlyphs; gid++, q += 2) map[gid] = U16(b, q);
        else
            while (gid < numGlyphs)
            {
                int first = U16(b, q);
                int left = format == 1 ? b[q + 2] : U16(b, q + 2);
                q += format == 1 ? 3 : 4;
                for (int k = 0; k <= left && gid < numGlyphs; k++) map[gid++] = first + k;
            }
        map[0] = 0;
        return (map, ros);
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

    private static Dictionary<int, List<double>> Dict(byte[] b, int p, int end)
    {
        var d = new Dictionary<int, List<double>>();
        var ops = new List<double>();
        while (p < end)
        {
            int b0 = b[p];
            if (b0 <= 21)
            {
                int op = b0 == 12 ? 1200 + b[p + 1] : b0;
                p += b0 == 12 ? 2 : 1;
                d[op] = ops;
                ops = [];
            }
            else if (b0 == 28) { ops.Add(S16(b, p + 1)); p += 3; }
            else if (b0 == 29) { ops.Add(BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p + 1))); p += 5; }
            else if (b0 == 30) { p++; while (p < end && (b[p] & 0x0F) != 0x0F && (b[p] >> 4) != 0x0F) p++; p++; ops.Add(0); }
            else if (b0 <= 246) { ops.Add(b0 - 139); p++; }
            else if (b0 <= 250) { ops.Add((b0 - 247) * 256 + b[p + 1] + 108); p += 2; }
            else if (b0 <= 254) { ops.Add(-(b0 - 251) * 256 - b[p + 1] - 108); p += 2; }
            else p++;
        }
        return d;
    }

    // ------------------------------------------------------------------ bytes
    private static string Tag(byte[] b, int at) => Encoding.ASCII.GetString(b, at, 4);
    private static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
    private static short S16(byte[] b, int at) => BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(at));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
}
