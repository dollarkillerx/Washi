// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Washi.Masume;

/// <summary>
/// The few binary-image operations Masume needs, with the same semantics as the
/// scipy.ndimage calls in the Python prototype (4-connectivity everywhere).
/// Plain managed code: no OpenCV dependency.
/// </summary>
internal static class Bitmaps
{
    /// <summary>One step of binary dilation with the 3x3 cross (scipy's default structure).</summary>
    public static bool[] DilateCross(bool[] m, int w, int h)
    {
        var o = new bool[m.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                o[i] = m[i]
                    || (x > 0 && m[i - 1]) || (x < w - 1 && m[i + 1])
                    || (y > 0 && m[i - w]) || (y < h - 1 && m[i + w]);
            }
        }
        return o;
    }

    public readonly record struct Component(int Label, int X0, int Y0, int X1, int Y1, int Count)
    {
        // X1 / Y1 are exclusive, like Python slices.
        public int Width => X1 - X0;
        public int Height => Y1 - Y0;
    }

    /// <summary>Labels 4-connected regions where <paramref name="fg"/> is true. Labels start at 1.</summary>
    public static (int[] Labels, List<Component> Components) Label(bool[] fg, int w, int h)
    {
        var labels = new int[fg.Length];
        var comps = new List<Component>();
        var stack = new Stack<int>();
        int next = 0;
        for (int start = 0; start < fg.Length; start++)
        {
            if (!fg[start] || labels[start] != 0) continue;
            int lab = ++next, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, count = 0;
            labels[start] = lab;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w;
                count++;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            comps.Add(new Component(lab, x0, y0, x1 + 1, y1 + 1, count));

            void Visit(int j)
            {
                if (fg[j] && labels[j] == 0) { labels[j] = lab; stack.Push(j); }
            }
        }
        return (labels, comps);
    }

    /// <summary>
    /// Inside the component's bounding box: which pixels are holes (not the component, and not
    /// 4-connected to the box border through non-component pixels). Same as
    /// <c>binary_fill_holes(comp) &amp; ~comp</c>.
    /// </summary>
    public static bool[] Holes(int[] labels, int w, Component c)
    {
        int bw = c.Width, bh = c.Height;
        var outside = new bool[bw * bh];
        var stack = new Stack<int>();
        bool IsComp(int bx, int by) => labels[(c.Y0 + by) * w + c.X0 + bx] == c.Label;

        void Seed(int bx, int by)
        {
            int k = by * bw + bx;
            if (!outside[k] && !IsComp(bx, by)) { outside[k] = true; stack.Push(k); }
        }

        for (int bx = 0; bx < bw; bx++) { Seed(bx, 0); Seed(bx, bh - 1); }
        for (int by = 0; by < bh; by++) { Seed(0, by); Seed(bw - 1, by); }
        while (stack.Count > 0)
        {
            int k = stack.Pop(), bx = k % bw, by = k / bw;
            if (bx > 0) Seed(bx - 1, by);
            if (bx < bw - 1) Seed(bx + 1, by);
            if (by > 0) Seed(bx, by - 1);
            if (by < bh - 1) Seed(bx, by + 1);
        }

        var holes = new bool[bw * bh];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int k = by * bw + bx;
                holes[k] = !outside[k] && !IsComp(bx, by);
            }
        return holes;
    }
}
