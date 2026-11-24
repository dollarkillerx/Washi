// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Washi.Core.Pages;

namespace Washi.Core.Signatures;

/// <summary>
/// What the revisions saved after a signature changed, by kind, from the objects of the signed revision that the
/// file now holds differently:
/// validation (validation data: the DSS), timestamp (a document time-stamp added), signature (a signature field added
/// or signed), form (field values and their appearances), metadata (document properties), comments (comments added,
/// changed or removed) and other (anything else: page content, fields added, …). A certification allows some of these
/// (PDF 32000-2, 12.8.2.2); validation data and time-stamps are always allowed.
/// </summary>
internal static class Revisions
{
    private static readonly string[] Order = ["validation", "timestamp", "signature", "form", "metadata", "comments", "other"];

    /// <summary>The changes after the revision that ends at <paramref name="end"/>, with <paramref name="now"/> the whole file.</summary>
    public static IReadOnlyList<string> Changes(byte[] bytes, int end, PdfDocument now)
    {
        using var then = PdfReader.Open(new MemoryStream(bytes, 0, end, writable: false), PdfDocumentOpenMode.Import);
        return Compare(then, now);
    }

    internal static IReadOnlyList<string> Compare(PdfDocument then, PdfDocument now)
    {
        var kinds = new HashSet<string>();
        var before = Objects(then);
        var after = Objects(now);
        var c = new Context(then, now);
        if (Number(then.Internals.Catalog) != Number(now.Internals.Catalog)) kinds.Add("other");
        foreach (var (n, o) in before)
        {
            // the file's own structure (cross-reference and object streams) is not content
            if (o is PdfDictionary sd && Name(sd, "/Type") is "/XRef" or "/ObjStm") continue;
            if (!after.TryGetValue(n, out var x)) { kinds.Add("other"); continue; }
            if (Same(o, x)) continue;
            foreach (var k in c.Classify(n, o, x)) kinds.Add(k);
        }
        return [.. Order.Where(kinds.Contains)];
    }

    private static Dictionary<int, PdfObject> Objects(PdfDocument doc)
    {
        var map = new Dictionary<int, PdfObject>();
        foreach (var o in doc.Internals.GetAllObjects())
            if (o.Reference is { } r) map[r.ObjectNumber] = o;
        return map;
    }

    private static int Number(PdfObject? o) => o?.Reference?.ObjectNumber ?? -1;

    private static bool Same(PdfItem? a, PdfItem? b)
    {
        if (Canon(a) != Canon(b)) return false;
        var sa = (a as PdfDictionary)?.Stream?.Value;
        var sb = (b as PdfDictionary)?.Stream?.Value;
        return sa is null ? sb is null : sb is not null && sa.AsSpan().SequenceEqual(sb);
    }

    /// <summary>
    /// An object written so that the same content reads the same however PDFsharp happened to load it: numbers in one
    /// format, rectangles (which it turns into PdfRectangle once read as one) as arrays, keys in order, other objects
    /// by number.
    /// </summary>
    internal static string Canon(PdfItem? item)
    {
        var sb = new StringBuilder();
        Canon(item, sb, 0);
        return sb.ToString();
    }

    private static void Canon(PdfItem? item, StringBuilder sb, int depth)
    {
        if (depth > 64) { sb.Append("null"); return; }
        if (depth > 0 && item is PdfObject { Reference: { } own }) { sb.Append(own.ObjectNumber).Append(" R"); return; }
        switch (item)
        {
            case PdfRectangle rc: Rect(sb, rc.X1, rc.Y1, rc.X2, rc.Y2); break;
            case PdfReference r: sb.Append(r.ObjectNumber).Append(" R"); break;
            case PdfIntegerObject io: sb.Append(Num(io.Value)); break;
            case PdfRealObject ro: sb.Append(Num(ro.Value)); break;
            case PdfInteger i: sb.Append(Num(i.Value)); break;
            case PdfLongInteger l: sb.Append(Num(l.Value)); break;
            case PdfReal f: sb.Append(Num(f.Value)); break;
            case PdfArray a when a.Elements.Count == 4 && a.Elements.All(e => e is PdfInteger or PdfReal or PdfLongInteger):
                var v = a.Elements.Select(FontMetrics.Num).ToArray();
                Rect(sb, v[0], v[1], v[2], v[3]);
                break;
            case PdfArray a:
                sb.Append('[');
                foreach (var e in a.Elements) { Canon(e, sb, depth + 1); sb.Append(' '); }
                sb.Append(']');
                break;
            case PdfDictionary d:
                sb.Append("<<");
                foreach (var k in d.Elements.Keys.Order(StringComparer.Ordinal))
                {
                    sb.Append(k).Append(' ');
                    Canon(d.Elements[k], sb, depth + 1);
                    sb.Append(' ');
                }
                sb.Append(">>");
                break;
            default: sb.Append(PdfSyntax.Write(item)); break;
        }
    }

    private static void Rect(StringBuilder sb, double x1, double y1, double x2, double y2) =>
        sb.Append('[').Append(Num(Math.Min(x1, x2))).Append(' ').Append(Num(Math.Min(y1, y2))).Append(' ')
          .Append(Num(Math.Max(x1, x2))).Append(' ').Append(Num(Math.Max(y1, y2))).Append(']');

    private static string Num(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static PdfItem? Deref(PdfItem? i) => i is PdfReference r ? r.Value : i;

    private static string Name(PdfDictionary d, string key) => Deref(d.Elements[key]) is PdfName n ? n.Value : "";

    /// <summary>What each object of the signed revision is, to judge a change to it.</summary>
    private sealed class Context
    {
        private readonly int _catalog, _form, _info, _metadata;
        private readonly HashSet<int> _pages = [], _dss = [];
        private readonly Dictionary<int, string> _fields = [];   // field or widget → "sig" or "field"
        private readonly Dictionary<int, string> _annots = [];   // annotation → "comments" or "other"
        private readonly Dictionary<int, string> _looks = [];    // appearance stream → the kind of change it is

        public Context(PdfDocument then, PdfDocument now)
        {
            var cat = then.Internals.Catalog;
            _catalog = Number(cat);
            _form = cat.Elements["/AcroForm"] is PdfReference fr ? fr.ObjectNumber : -1;
            _info = then.Info.Reference?.ObjectNumber ?? -1;
            _metadata = cat.Elements["/Metadata"] is PdfReference mr ? mr.ObjectNumber : -1;
            Reach(cat.Elements["/DSS"], _dss);
            foreach (var p in then.Pages)
            {
                if (p.Reference is { } pr) _pages.Add(pr.ObjectNumber);
                if (Deref(p.Elements["/Annots"]) is not PdfArray annots) continue;
                foreach (var e in annots.Elements)
                {
                    if (e is not PdfReference ar || ar.Value is not PdfDictionary a) continue;
                    string kind = Annotation(a);
                    if (kind != "field") _annots[ar.ObjectNumber] = kind;
                    Looks(a, kind == "field" ? (FieldType(a) == "/Sig" ? "signature" : "form") : kind);
                }
            }
            if (Deref(cat.Elements["/AcroForm"]) is PdfDictionary form && Deref(form.Elements["/Fields"]) is PdfArray fields)
            {
                var stack = new Stack<PdfItem>(fields.Elements);
                while (stack.Count > 0)
                {
                    var item = stack.Pop();
                    if (item is not PdfReference r || r.Value is not PdfDictionary d || _fields.ContainsKey(r.ObjectNumber)) continue;
                    _fields[r.ObjectNumber] = FieldType(d) == "/Sig" ? "sig" : "field";
                    Looks(d, FieldType(d) == "/Sig" ? "signature" : "form");
                    if (Deref(d.Elements["/Kids"]) is PdfArray kids) foreach (var k in kids.Elements) stack.Push(k);
                }
            }
        }

        private void Reach(PdfItem? item, HashSet<int> into, int depth = 0)
        {
            if (depth > 8) return;
            if (item is PdfReference r)
            {
                if (!into.Add(r.ObjectNumber)) return;
                item = r.Value;
            }
            switch (item)
            {
                case PdfDictionary d: foreach (var k in d.Elements.Keys) Reach(d.Elements[k], into, depth + 1); break;
                case PdfArray a: foreach (var e in a.Elements) Reach(e, into, depth + 1); break;
            }
        }

        /// <summary>The appearance streams of an annotation or field (all states).</summary>
        private void Looks(PdfDictionary a, string kind)
        {
            if (Deref(a.Elements["/AP"]) is not PdfDictionary ap) return;
            foreach (var key in ap.Elements.Keys)
            {
                var v = ap.Elements[key];
                if (v is PdfReference r) { if (r.Value is PdfDictionary { Stream: not null }) _looks[r.ObjectNumber] = kind; else if (r.Value is PdfDictionary states) States(states); }
                else if (v is PdfDictionary states) States(states);
            }

            void States(PdfDictionary states)
            {
                foreach (var s in states.Elements.Keys)
                    if (states.Elements[s] is PdfReference sr) _looks[sr.ObjectNumber] = kind;
            }
        }

        public IEnumerable<string> Classify(int n, PdfObject old, PdfObject now)
        {
            if (_dss.Contains(n)) return ["validation"];
            if (n == _info || n == _metadata) return ["metadata"];
            if (n == _catalog) return Catalog((PdfDictionary)old, now as PdfDictionary);
            if (n == _form) return Form(old as PdfDictionary, now as PdfDictionary);
            if (_fields.TryGetValue(n, out var f)) return Field(f, old as PdfDictionary, now as PdfDictionary);
            if (_annots.TryGetValue(n, out var a)) return [a];
            if (_pages.Contains(n)) return Keys(old as PdfDictionary, now as PdfDictionary, k => k == "/Annots" ? null : "other", "/Annots");
            if (_looks.TryGetValue(n, out var l)) return [l];
            if (old is PdfArray oa && now is PdfArray na) return Elements(oa, na);
            return ["other"];
        }

        private IEnumerable<string> Catalog(PdfDictionary old, PdfDictionary? now)
        {
            if (now is null) return ["other"];
            var kinds = new List<string>();
            foreach (var key in old.Elements.Keys.Union(now.Elements.Keys))
            {
                var (a, b) = (old.Elements[key], now.Elements[key]);
                if (Canon(a) == Canon(b)) continue;
                switch (key)
                {
                    case "/DSS" or "/Extensions": kinds.Add("validation"); break;
                    case "/Metadata": kinds.Add("metadata"); break;
                    case "/AcroForm": kinds.AddRange(Form(Deref(a) as PdfDictionary, Deref(b) as PdfDictionary)); break;
                    default: kinds.Add("other"); break;
                }
            }
            return kinds;
        }

        private IEnumerable<string> Form(PdfDictionary? old, PdfDictionary? now)
        {
            if (now is null) return ["other"];
            return Keys(old, now, key => key switch
            {
                "/SigFlags" => "signature",
                "/DR" or "/DA" or "/NeedAppearances" => "form",
                _ => "other",
            }, "/Fields");
        }

        private IEnumerable<string> Field(string kind, PdfDictionary? old, PdfDictionary? now)
        {
            if (old is null || now is null) return ["other"];
            bool sig = kind == "sig";
            string signing = sig && Deref(now.Elements["/V"]) is PdfDictionary v && (Name(v, "/Type") == "/DocTimeStamp" || Name(v, "/SubFilter") == "/ETSI.RFC3161") ? "timestamp" : "signature";
            return Keys(old, now, key => key switch
            {
                "/V" or "/AP" or "/AS" or "/MK" => sig ? signing : "form",
                "/F" or "/Lock" or "/SV" when sig => signing,
                _ => "other",
            }, "/Kids");
        }

        /// <summary>The keys that differ, each judged by <paramref name="judge"/>; <paramref name="list"/> is a key holding
        /// an array of annotations or fields, judged by what was added to it or taken from it.</summary>
        private IEnumerable<string> Keys(PdfDictionary? old, PdfDictionary? now, Func<string, string?> judge, string list)
        {
            if (now is null) return ["other"];
            var kinds = new List<string>();
            var keys = (old?.Elements.Keys ?? []).Union(now.Elements.Keys);
            foreach (var key in keys)
            {
                var (a, b) = (old?.Elements[key], now.Elements[key]);
                if (Canon(a) == Canon(b)) continue;
                if (key == list)
                {
                    if (Deref(b) is PdfArray nb) kinds.AddRange(Elements(Deref(a) as PdfArray, nb));
                    else kinds.Add("other");
                    continue;
                }
                if (judge(key) is { } k) kinds.Add(k);
            }
            return kinds;
        }

        /// <summary>An array of annotations or fields: what was added and what was taken away.</summary>
        private IEnumerable<string> Elements(PdfArray? old, PdfArray now)
        {
            var before = old?.Elements.ToList() ?? [];
            var after = now.Elements.ToList();
            if (before.Any(e => e is not PdfReference) || after.Any(e => e is not PdfReference)) return ["other"];
            var was = before.Cast<PdfReference>().Select(r => r.ObjectNumber).ToHashSet();
            var @is = after.Cast<PdfReference>().Select(r => r.ObjectNumber).ToHashSet();
            var kinds = new List<string>();
            foreach (var r in after.Cast<PdfReference>().Where(r => !was.Contains(r.ObjectNumber)))
                kinds.Add(r.Value is PdfDictionary d ? Added(d) : "other");
            foreach (var r in before.Cast<PdfReference>().Where(r => !@is.Contains(r.ObjectNumber)))
                kinds.Add(_annots.TryGetValue(r.ObjectNumber, out var k) && k == "comments" ? "comments" : "other");
            return kinds;
        }

        /// <summary>Something added to the page or the form: a signature field, a comment, or other.</summary>
        private static string Added(PdfDictionary d)
        {
            if (d.Elements.ContainsKey("/FT") || d.Elements.ContainsKey("/Parent") || Name(d, "/Subtype") == "/Widget")
            {
                if (FieldType(d) != "/Sig") return "other";
                return Deref(d.Elements["/V"]) is PdfDictionary v && (Name(v, "/Type") == "/DocTimeStamp" || Name(v, "/SubFilter") == "/ETSI.RFC3161") ? "timestamp" : "signature";
            }
            return Annotation(d) == "comments" ? "comments" : "other";
        }

        private static string Annotation(PdfDictionary a) => Name(a, "/Subtype") switch
        {
            "/Widget" => "field",
            "/Link" or "/Screen" or "/Movie" or "/Sound" or "/RichMedia" or "/3D" or "/PrinterMark" or "/TrapNet" or "/Watermark" => "other",
            _ => "comments",
        };

        private static string FieldType(PdfDictionary d)
        {
            for (var n = d; n is not null; n = Deref(n.Elements["/Parent"]) as PdfDictionary)
                if (Name(n, "/FT") is { Length: > 0 } ft) return ft;
            return "";
        }
    }
}
