using System;
using System.Collections.Generic;
using System.Drawing;

namespace StillsAndReels
{
    /// <summary>How a new selection joins the one already there.</summary>
    internal enum PicSelMode { Replace, Add, Subtract, Intersect }

    /// <summary>One straight piece of a selection's outline, on pixel edges.</summary>
    internal struct PicEdge
    {
        public int X0, Y0, X1, Y1;
    }

    /// <summary>
    /// A selected part of the picture: how much of each pixel is selected (0..255), kept only for the box
    /// around the selection. Never changes once made.
    /// </summary>
    internal sealed class PicSelection
    {
        /// <summary>The box around the selected pixels, in picture pixels.</summary>
        public readonly Rectangle Bounds;
        readonly byte[] mask;       // Bounds.Width * Bounds.Height
        PicEdge[] edges;

        PicSelection(Rectangle bounds, byte[] mask)
        {
            Bounds = bounds;
            this.mask = mask;
        }

        /// <summary>How much of a pixel is selected, 0 (not at all) to 255 (fully).</summary>
        public byte At(int x, int y)
        {
            x -= Bounds.X; y -= Bounds.Y;
            if ((uint)x >= (uint)Bounds.Width || (uint)y >= (uint)Bounds.Height) return 0;
            return mask[y * Bounds.Width + x];
        }

        /// <summary>The mask bytes of one row (index with x - Bounds.X).</summary>
        internal byte[] Mask { get { return mask; } }
        internal int RowStart(int y) { return (y - Bounds.Y) * Bounds.Width; }

        public long PixelCount
        {
            get { long n = 0; for (int i = 0; i < mask.Length; i++) if (mask[i] >= 128) n++; return n; }
        }

        /// <summary>True when the selection is just a plain box.</summary>
        public bool IsRectangle
        {
            get { for (int i = 0; i < mask.Length; i++) if (mask[i] != 255) return false; return true; }
        }

        // ---------------------------------------------------------------- making selections

        /// <summary>Trims a full-box mask to its selected part. Null when nothing is selected.</summary>
        static PicSelection Tight(Rectangle box, byte[] m)
        {
            int w = box.Width, h = box.Height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int first = -1, last = -1;
                for (int x = 0; x < w; x++) if (m[row + x] != 0) { first = x; break; }
                if (first < 0) continue;
                for (int x = w - 1; x >= first; x--) if (m[row + x] != 0) { last = x; break; }
                if (first < minX) minX = first;
                if (last > maxX) maxX = last;
                if (minY == h) minY = y;
                maxY = y;
            }
            if (maxX < 0) return null;
            if (minX == 0 && minY == 0 && maxX == w - 1 && maxY == h - 1) return new PicSelection(box, m);
            int nw = maxX - minX + 1, nh = maxY - minY + 1;
            var t = new byte[nw * nh];
            for (int y = 0; y < nh; y++) Buffer.BlockCopy(m, (y + minY) * w + minX, t, y * nw, nw);
            return new PicSelection(new Rectangle(box.X + minX, box.Y + minY, nw, nh), t);
        }

        public static PicSelection FromRect(Rectangle r, Size canvas)
        {
            r.Intersect(new Rectangle(Point.Empty, canvas));
            if (r.Width < 1 || r.Height < 1) return null;
            var m = new byte[r.Width * r.Height];
            for (int i = 0; i < m.Length; i++) m[i] = 255;
            return new PicSelection(r, m);
        }

        public static PicSelection All(Size canvas) { return FromRect(new Rectangle(Point.Empty, canvas), canvas); }

        /// <summary>An oval selection with a smooth edge.</summary>
        public static PicSelection FromEllipse(RectangleF r, Size canvas)
        {
            if (r.Width <= 0 || r.Height <= 0) return null;
            var box = Rectangle.Intersect(Rectangle.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom)),
                                          new Rectangle(Point.Empty, canvas));
            if (box.Width < 1 || box.Height < 1) return null;
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f, rx = r.Width / 2f, ry = r.Height / 2f;
            float rmin = Math.Min(rx, ry);
            var m = new byte[box.Width * box.Height];
            for (int y = 0; y < box.Height; y++)
            {
                float ny = (box.Y + y + 0.5f - cy) / ry;
                int row = y * box.Width;
                for (int x = 0; x < box.Width; x++)
                {
                    float nx = (box.X + x + 0.5f - cx) / rx;
                    // distance inside the edge, in pixels (close enough for a one-pixel soft edge)
                    float d = (1f - (float)Math.Sqrt(nx * nx + ny * ny)) * rmin + 0.5f;
                    m[row + x] = d >= 1f ? (byte)255 : d <= 0f ? (byte)0 : (byte)(d * 255f + 0.5f);
                }
            }
            return Tight(box, m);
        }

        /// <summary>A freehand (lasso) selection: everything inside the outline, with a smooth edge.</summary>
        public static PicSelection FromPolygon(PointF[] pts, Size canvas)
        {
            if (pts == null || pts.Length < 3) return null;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            }
            var box = Rectangle.Intersect(Rectangle.FromLTRB((int)Math.Floor(minX), (int)Math.Floor(minY), (int)Math.Ceiling(maxX), (int)Math.Ceiling(maxY)),
                                          new Rectangle(Point.Empty, canvas));
            if (box.Width < 1 || box.Height < 1) return null;
            const int Sub = 4;                       // sample lines per pixel row
            int w = box.Width, n = pts.Length;
            var m = new byte[w * box.Height];
            var cover = new float[w];
            var xs = new float[n];
            var dirs = new int[n];
            for (int y = 0; y < box.Height; y++)
            {
                Array.Clear(cover, 0, w);
                for (int sub = 0; sub < Sub; sub++)
                {
                    float sy = box.Y + y + (sub + 0.5f) / Sub;
                    int count = 0;
                    for (int i = 0; i < n; i++)
                    {
                        var a = pts[i]; var b = pts[(i + 1) % n];
                        if (a.Y == b.Y) continue;
                        bool down = b.Y > a.Y;
                        float top = down ? a.Y : b.Y, bottom = down ? b.Y : a.Y;
                        if (sy < top || sy >= bottom) continue;
                        xs[count] = a.X + (sy - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                        dirs[count] = down ? 1 : -1;
                        count++;
                    }
                    if (count < 2) continue;
                    Array.Sort(xs, dirs, 0, count);
                    int winding = 0;
                    for (int i = 0; i < count - 1; i++)
                    {
                        winding += dirs[i];
                        if (winding == 0) continue;
                        float x0 = Math.Max(0f, xs[i] - box.X), x1 = Math.Min(w, xs[i + 1] - box.X);
                        if (x1 <= x0) continue;
                        int i0 = (int)x0, i1 = (int)x1;
                        if (i0 == i1) { cover[i0] += x1 - x0; continue; }
                        cover[i0] += i0 + 1 - x0;
                        for (int x = i0 + 1; x < i1; x++) cover[x] += 1f;
                        if (i1 < w) cover[i1] += x1 - i1;
                    }
                }
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float c = cover[x] / Sub;
                    m[row + x] = c >= 0.999f ? (byte)255 : c <= 0f ? (byte)0 : (byte)(c * 255f + 0.5f);
                }
            }
            return Tight(box, m);
        }

        static int Diff(uint a, uint b)
        {
            int d0 = Math.Abs((int)(a & 255) - (int)(b & 255));
            int d1 = Math.Abs((int)((a >> 8) & 255) - (int)((b >> 8) & 255));
            int d2 = Math.Abs((int)((a >> 16) & 255) - (int)((b >> 16) & 255));
            int d3 = Math.Abs((int)(a >> 24) - (int)(b >> 24));
            return Math.Max(Math.Max(d0, d1), Math.Max(d2, d3));
        }

        /// <summary>
        /// The magic wand: every pixel close in colour to the one clicked. (x, y) is inside src; (ox, oy) is where
        /// src sits on the canvas. Contiguous stops at edges; otherwise matching pixels anywhere are taken.
        /// </summary>
        public static unsafe PicSelection FromWand(PicSurface src, int x, int y, int tolerance, bool contiguous, int ox, int oy, Size canvas)
        {
            int w = src.Width, h = src.Height;
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return null;
            uint seed = src.Row(y)[x];
            var m = new byte[w * h];
            if (!contiguous)
            {
                PicPar.Rows(0, h, w, (y0, y1) =>
                {
                    for (int yy = y0; yy < y1; yy++)
                    {
                        uint* p = src.Row(yy);
                        int row = yy * w;
                        for (int xx = 0; xx < w; xx++) if (Diff(p[xx], seed) <= tolerance) m[row + xx] = 255;
                    }
                });
            }
            else
            {
                // scanline flood fill
                var stack = new Stack<int>();
                stack.Push(x); stack.Push(y);
                while (stack.Count > 0)
                {
                    int cy = stack.Pop(), cx = stack.Pop();
                    uint* p = src.Row(cy);
                    int row = cy * w;
                    if (m[row + cx] != 0 || Diff(p[cx], seed) > tolerance) continue;
                    int l = cx, r = cx;
                    while (l > 0 && m[row + l - 1] == 0 && Diff(p[l - 1], seed) <= tolerance) l--;
                    while (r < w - 1 && m[row + r + 1] == 0 && Diff(p[r + 1], seed) <= tolerance) r++;
                    for (int i = l; i <= r; i++) m[row + i] = 255;
                    for (int dy = -1; dy <= 1; dy += 2)
                    {
                        int ny = cy + dy;
                        if (ny < 0 || ny >= h) continue;
                        uint* q = src.Row(ny);
                        int nrow = ny * w;
                        bool inRun = false;
                        for (int i = l; i <= r; i++)
                        {
                            bool ok = m[nrow + i] == 0 && Diff(q[i], seed) <= tolerance;
                            if (ok && !inRun) { stack.Push(i); stack.Push(ny); }
                            inRun = ok;
                        }
                    }
                }
            }
            var made = Tight(new Rectangle(ox, oy, w, h), m);
            if (made == null) return null;
            return made.ClipTo(new Rectangle(Point.Empty, canvas));
        }

        PicSelection ClipTo(Rectangle area)
        {
            if (area.Contains(Bounds)) return this;
            var r = Rectangle.Intersect(area, Bounds);
            if (r.Width < 1 || r.Height < 1) return null;
            var m = new byte[r.Width * r.Height];
            for (int y = 0; y < r.Height; y++)
                Buffer.BlockCopy(mask, (y + r.Y - Bounds.Y) * Bounds.Width + (r.X - Bounds.X), m, y * r.Width, r.Width);
            return Tight(r, m);
        }

        /// <summary>Everything that is not selected.</summary>
        public static PicSelection Invert(PicSelection s, Size canvas)
        {
            if (s == null) return All(canvas);
            var box = new Rectangle(Point.Empty, canvas);
            var m = new byte[box.Width * box.Height];
            for (int i = 0; i < m.Length; i++) m[i] = 255;
            for (int y = s.Bounds.Top; y < s.Bounds.Bottom; y++)
            {
                int src = s.RowStart(y), dst = y * box.Width + s.Bounds.X;
                for (int x = 0; x < s.Bounds.Width; x++) m[dst + x] = (byte)(255 - s.mask[src + x]);
            }
            return Tight(box, m);
        }

        /// <summary>Joins two selections. Either may be null (nothing selected); the result is null when it is empty.</summary>
        public static PicSelection Combine(PicSelection a, PicSelection b, PicSelMode mode, Size canvas)
        {
            if (mode == PicSelMode.Replace) return b;
            if (a == null) return mode == PicSelMode.Add ? b : null;
            if (b == null) return mode == PicSelMode.Intersect ? null : a;
            Rectangle box = mode == PicSelMode.Add ? Rectangle.Union(a.Bounds, b.Bounds)
                          : mode == PicSelMode.Intersect ? Rectangle.Intersect(a.Bounds, b.Bounds) : a.Bounds;
            if (box.Width < 1 || box.Height < 1) return null;
            var m = new byte[box.Width * box.Height];
            for (int y = box.Top; y < box.Bottom; y++)
            {
                int row = (y - box.Y) * box.Width;
                for (int x = box.Left; x < box.Right; x++)
                {
                    int va = a.At(x, y), vb = b.At(x, y);
                    int v = mode == PicSelMode.Add ? Math.Max(va, vb) : mode == PicSelMode.Intersect ? Math.Min(va, vb) : va * (255 - vb) / 255;
                    m[row + x - box.X] = (byte)v;
                }
            }
            return Tight(box, m);
        }

        /// <summary>The same selection moved (kept inside the canvas).</summary>
        public PicSelection Offset(int dx, int dy, Size canvas)
        {
            var moved = new PicSelection(new Rectangle(Bounds.X + dx, Bounds.Y + dy, Bounds.Width, Bounds.Height), mask);
            return moved.ClipTo(new Rectangle(Point.Empty, canvas));
        }

        /// <summary>Softens the edge of the selection by a number of pixels.</summary>
        public PicSelection Feather(float radius, Size canvas)
        {
            if (radius < 0.5f) return this;
            int pad = (int)Math.Ceiling(radius * 2) + 1;
            var box = Rectangle.Inflate(Bounds, pad, pad);
            box.Intersect(new Rectangle(Point.Empty, canvas));
            var m = new byte[box.Width * box.Height];
            for (int y = Bounds.Top; y < Bounds.Bottom; y++)
                Buffer.BlockCopy(mask, RowStart(y), m, (y - box.Y) * box.Width + (Bounds.X - box.X), Bounds.Width);
            PicFilters.BlurBytes(m, box.Width, box.Height, radius);
            return Tight(box, m);
        }

        /// <summary>Grows (positive) or shrinks (negative) the selection by a number of pixels.</summary>
        public PicSelection Grow(int by, Size canvas)
        {
            if (by == 0) return this;
            int pad = Math.Max(0, by) + 1;
            var box = Rectangle.Inflate(Bounds, pad, pad);
            box.Intersect(new Rectangle(Point.Empty, canvas));
            int w = box.Width, h = box.Height;
            var m = new byte[w * h];
            for (int y = Bounds.Top; y < Bounds.Bottom; y++)
                Buffer.BlockCopy(mask, RowStart(y), m, (y - box.Y) * w + (Bounds.X - box.X), Bounds.Width);
            bool grow = by > 0;
            int n = Math.Abs(by);
            // a square brush, one pass along rows then one along columns
            var t = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int best = grow ? 0 : 255;
                    for (int k = -n; k <= n; k++)
                    {
                        int xx = x + k;
                        int v = xx < 0 || xx >= w ? 0 : m[y * w + xx];
                        if (grow ? v > best : v < best) best = v;
                    }
                    t[y * w + x] = (byte)best;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int best = grow ? 0 : 255;
                    for (int k = -n; k <= n; k++)
                    {
                        int yy = y + k;
                        int v = yy < 0 || yy >= h ? 0 : t[yy * w + x];
                        if (grow ? v > best : v < best) best = v;
                    }
                    m[y * w + x] = (byte)best;
                }
            return Tight(box, m);
        }

        // ---------------------------------------------------------------- outline

        /// <summary>The outline as straight pieces along pixel edges (for the "marching ants").</summary>
        public PicEdge[] Edges
        {
            get
            {
                if (edges != null) return edges;
                var list = new List<PicEdge>();
                int w = Bounds.Width, h = Bounds.Height, bx = Bounds.X, by = Bounds.Y;
                // horizontal pieces: between each row and the one above it
                for (int y = 0; y <= h; y++)
                {
                    int start = -1;
                    for (int x = 0; x <= w; x++)
                    {
                        bool edge = false;
                        if (x < w)
                        {
                            bool above = y > 0 && mask[(y - 1) * w + x] >= 128;
                            bool below = y < h && mask[y * w + x] >= 128;
                            edge = above != below;
                        }
                        if (edge) { if (start < 0) start = x; }
                        else if (start >= 0) { list.Add(new PicEdge { X0 = bx + start, Y0 = by + y, X1 = bx + x, Y1 = by + y }); start = -1; }
                    }
                }
                // vertical pieces: between each column and the one to its left
                for (int x = 0; x <= w; x++)
                {
                    int start = -1;
                    for (int y = 0; y <= h; y++)
                    {
                        bool edge = false;
                        if (y < h)
                        {
                            bool left = x > 0 && mask[y * w + x - 1] >= 128;
                            bool right = x < w && mask[y * w + x] >= 128;
                            edge = left != right;
                        }
                        if (edge) { if (start < 0) start = y; }
                        else if (start >= 0) { list.Add(new PicEdge { X0 = bx + x, Y0 = by + start, X1 = bx + x, Y1 = by + y }); start = -1; }
                    }
                }
                edges = list.ToArray();
                return edges;
            }
        }
    }

    /// <summary>Direct painting into a layer's pixels: fill, clear and rub out.</summary>
    internal static unsafe class PicPaint
    {
        /// <summary>Paints a colour over the selected pixels. (sx, sy) is where the surface sits on the canvas.</summary>
        public static void FillMask(PicSurface s, int sx, int sy, PicSelection sel, uint pargb)
        {
            var r = Rectangle.Intersect(sel.Bounds, new Rectangle(sx, sy, s.Width, s.Height));
            if (r.Width < 1 || r.Height < 1) return;
            uint ca = pargb >> 24, cr = (pargb >> 16) & 255, cg = (pargb >> 8) & 255, cb = pargb & 255;
            var mask = sel.Mask;
            PicPar.Rows(r.Top, r.Bottom, r.Width, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* p = s.Row(y - sy) + (r.Left - sx);
                    int mi = sel.RowStart(y) + (r.Left - sel.Bounds.X);
                    for (int x = 0; x < r.Width; x++)
                    {
                        uint m = mask[mi + x];
                        if (m == 0) continue;
                        if (m == 255 && ca == 255) { p[x] = pargb; continue; }
                        uint a = (ca * m + 127) / 255, inv = 255 - a;
                        uint d = p[x];
                        uint oa = a + ((d >> 24) * inv + 127) / 255;
                        uint or = (cr * m + 127) / 255 + (((d >> 16) & 255) * inv + 127) / 255;
                        uint og = (cg * m + 127) / 255 + (((d >> 8) & 255) * inv + 127) / 255;
                        uint ob = (cb * m + 127) / 255 + ((d & 255) * inv + 127) / 255;
                        p[x] = (Math.Min(oa, 255u) << 24) | (Math.Min(or, 255u) << 16) | (Math.Min(og, 255u) << 8) | Math.Min(ob, 255u);
                    }
                }
            });
        }

        /// <summary>Makes the selected pixels see-through.</summary>
        public static void ClearMask(PicSurface s, int sx, int sy, PicSelection sel)
        {
            var r = Rectangle.Intersect(sel.Bounds, new Rectangle(sx, sy, s.Width, s.Height));
            if (r.Width < 1 || r.Height < 1) return;
            var mask = sel.Mask;
            PicPar.Rows(r.Top, r.Bottom, r.Width, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* p = s.Row(y - sy) + (r.Left - sx);
                    int mi = sel.RowStart(y) + (r.Left - sel.Bounds.X);
                    for (int x = 0; x < r.Width; x++)
                    {
                        uint m = mask[mi + x];
                        if (m == 0) continue;
                        p[x] = m == 255 ? 0u : Scale(p[x], 255 - m);
                    }
                }
            });
        }

        /// <summary>Multiplies all four channels of a premultiplied pixel by k / 255.</summary>
        public static uint Scale(uint p, uint k)
        {
            uint a = ((p >> 24) * k + 127) / 255, r = (((p >> 16) & 255) * k + 127) / 255, g = (((p >> 8) & 255) * k + 127) / 255, b = ((p & 255) * k + 127) / 255;
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        /// <summary>
        /// Rubs out a round-ended line from a to b (surface coordinates) inside clip, with a soft one-pixel edge.
        /// A selection, if any, limits where pixels can be rubbed out.
        /// </summary>
        public static void EraseSegment(PicSurface s, PointF a, PointF b, float radius, Rectangle clip, PicSelection sel, int sx, int sy)
        {
            clip.Intersect(s.Bounds);
            if (clip.Width < 1 || clip.Height < 1) return;
            float dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            float outer = radius + 0.5f, outer2 = outer * outer, inner = Math.Max(0f, radius - 0.5f), inner2 = inner * inner;
            for (int y = clip.Top; y < clip.Bottom; y++)
            {
                uint* p = s.Row(y);
                float py = y + 0.5f;
                for (int x = clip.Left; x < clip.Right; x++)
                {
                    if (p[x] == 0) continue;
                    float px = x + 0.5f;
                    float t = len2 <= 0.0001f ? 0f : ((px - a.X) * dx + (py - a.Y) * dy) / len2;
                    if (t < 0) t = 0; else if (t > 1) t = 1;
                    float ex = px - (a.X + dx * t), ey = py - (a.Y + dy * t);
                    float d2 = ex * ex + ey * ey;
                    if (d2 >= outer2) continue;
                    float cover = d2 <= inner2 ? 1f : outer - (float)Math.Sqrt(d2);
                    if (cover > 1f) cover = 1f;
                    if (sel != null) cover *= sel.At(x + sx, y + sy) / 255f;
                    if (cover <= 0f) continue;
                    p[x] = cover >= 1f ? 0u : Scale(p[x], (uint)(255 - (int)(cover * 255f + 0.5f)));
                }
            }
        }
    }
}
