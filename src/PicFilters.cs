using System;
using System.Drawing;

namespace StillsAndReels
{
    /// <summary>Filters that work on an area of a layer's pixels in place: blur, pixelate, sharpen.</summary>
    internal static unsafe class PicFilters
    {
        /// <summary>The three box widths whose repeated blur looks like a Gaussian of the given sigma.</summary>
        static int[] BoxRadii(float sigma)
        {
            const int n = 3;
            double wIdeal = Math.Sqrt(12.0 * sigma * sigma / n + 1);
            int wl = (int)Math.Floor(wIdeal);
            if (wl % 2 == 0) wl--;
            int wu = wl + 2;
            double mIdeal = (12.0 * sigma * sigma - n * wl * wl - 4.0 * n * wl - 3.0 * n) / (-4.0 * wl - 4.0);
            int m = (int)Math.Round(mIdeal);
            var r = new int[n];
            for (int i = 0; i < n; i++) r[i] = Math.Max(0, ((i < m ? wl : wu) - 1) / 2);
            return r;
        }

        /// <summary>Blurs an area. Pixels outside the area are not looked at, so nothing leaks in or out.</summary>
        public static void GaussianBlur(PicSurface s, Rectangle area, float sigma)
        {
            area.Intersect(s.Bounds);
            if (area.Width < 1 || area.Height < 1 || sigma < 0.3f) return;
            foreach (int radius in BoxRadii(sigma))
            {
                if (radius < 1) continue;
                BoxRows(s, area, radius);
                BoxColumns(s, area, radius);
            }
        }

        static void BoxRows(PicSurface s, Rectangle a, int radius)
        {
            int w = a.Width;
            PicPar.Rows(a.Top, a.Bottom, w, (y0, y1) =>
            {
                var line = new uint[w];
                for (int y = y0; y < y1; y++)
                {
                    uint* p = s.Row(y) + a.Left;
                    for (int x = 0; x < w; x++) line[x] = p[x];
                    fixed (uint* src = line) BoxLine(src, 1, p, 1, w, radius);
                }
            });
        }

        static void BoxColumns(PicSurface s, Rectangle a, int radius)
        {
            int h = a.Height;
            int step = s.Stride / 4;
            // columns are split into bands the same way rows are
            PicPar.Rows(a.Left, a.Right, h, (x0, x1) =>
            {
                var line = new uint[h];
                for (int x = x0; x < x1; x++)
                {
                    uint* p = s.Row(a.Top) + x;
                    for (int y = 0; y < h; y++) line[y] = p[y * step];
                    fixed (uint* src = line) BoxLine(src, 1, p, step, h, radius);
                }
            });
        }

        /// <summary>One sliding-window average along a line, with the ends repeated.</summary>
        static void BoxLine(uint* src, int srcStep, uint* dst, int dstStep, int n, int radius)
        {
            int win = radius * 2 + 1;
            uint sa = 0, sr = 0, sg = 0, sb = 0;
            for (int i = -radius; i <= radius; i++)
            {
                uint v = src[(i < 0 ? 0 : i >= n ? n - 1 : i) * srcStep];
                sa += v >> 24; sr += (v >> 16) & 255; sg += (v >> 8) & 255; sb += v & 255;
            }
            uint half = (uint)win / 2;
            for (int i = 0; i < n; i++)
            {
                dst[i * dstStep] = (((sa + half) / (uint)win) << 24) | (((sr + half) / (uint)win) << 16) | (((sg + half) / (uint)win) << 8) | ((sb + half) / (uint)win);
                int outI = i - radius, inI = i + radius + 1;
                uint vo = src[(outI < 0 ? 0 : outI) * srcStep];
                uint vi = src[(inI >= n ? n - 1 : inI) * srcStep];
                sa += (vi >> 24) - (vo >> 24);
                sr += ((vi >> 16) & 255) - ((vo >> 16) & 255);
                sg += ((vi >> 8) & 255) - ((vo >> 8) & 255);
                sb += (vi & 255) - (vo & 255);
            }
        }

        /// <summary>Turns an area into big square blocks, each the average colour of what was under it.</summary>
        public static void Pixelate(PicSurface s, Rectangle area, int block)
        {
            area.Intersect(s.Bounds);
            if (area.Width < 1 || area.Height < 1 || block < 2) return;
            int rows = (area.Height + block - 1) / block;
            PicPar.Rows(0, rows, area.Width * block, (r0, r1) =>
            {
                for (int by = r0; by < r1; by++)
                {
                    int y0 = area.Top + by * block, y1 = Math.Min(area.Bottom, y0 + block);
                    for (int x0 = area.Left; x0 < area.Right; x0 += block)
                    {
                        int x1 = Math.Min(area.Right, x0 + block);
                        ulong sa = 0, sr = 0, sg = 0, sb = 0;
                        for (int y = y0; y < y1; y++)
                        {
                            uint* p = s.Row(y);
                            for (int x = x0; x < x1; x++)
                            {
                                uint v = p[x];
                                sa += v >> 24; sr += (v >> 16) & 255; sg += (v >> 8) & 255; sb += v & 255;
                            }
                        }
                        ulong n = (ulong)((x1 - x0) * (y1 - y0));
                        uint c = ((uint)((sa + n / 2) / n) << 24) | ((uint)((sr + n / 2) / n) << 16) | ((uint)((sg + n / 2) / n) << 8) | (uint)((sb + n / 2) / n);
                        for (int y = y0; y < y1; y++)
                        {
                            uint* p = s.Row(y);
                            for (int x = x0; x < x1; x++) p[x] = c;
                        }
                    }
                }
            });
        }

        /// <summary>Sharpens an area (unsharp mask): amount 0..3, radius in pixels.</summary>
        public static void Sharpen(PicSurface s, Rectangle area, float amount, float radius)
        {
            area.Intersect(s.Bounds);
            if (area.Width < 1 || area.Height < 1 || amount <= 0) return;
            using (var soft = s.Clone(area))
            {
                GaussianBlur(soft, soft.Bounds, Math.Max(0.4f, radius));
                int k = (int)Math.Round(amount * 256);
                PicPar.Rows(area.Top, area.Bottom, area.Width, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        uint* p = s.Row(y) + area.Left;
                        uint* q = soft.Row(y - area.Top);
                        for (int x = 0; x < area.Width; x++)
                        {
                            uint v = p[x], b = q[x];
                            int a = (int)(v >> 24);
                            if (a == 0) continue;
                            int r = (int)((v >> 16) & 255), g = (int)((v >> 8) & 255), bl = (int)(v & 255);
                            r += (r - (int)((b >> 16) & 255)) * k >> 8;
                            g += (g - (int)((b >> 8) & 255)) * k >> 8;
                            bl += (bl - (int)(b & 255)) * k >> 8;
                            r = r < 0 ? 0 : r > a ? a : r;
                            g = g < 0 ? 0 : g > a ? a : g;
                            bl = bl < 0 ? 0 : bl > a ? a : bl;
                            p[x] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)bl;
                        }
                    }
                });
            }
        }

        /// <summary>Blurs a one-byte-per-pixel picture (a selection being feathered).</summary>
        public static void BlurBytes(byte[] m, int w, int h, float sigma)
        {
            var line = new int[Math.Max(w, h)];
            foreach (int radius in BoxRadii(sigma))
            {
                if (radius < 1) continue;
                int win = radius * 2 + 1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w, sum = 0;
                    for (int x = 0; x < w; x++) line[x] = m[row + x];
                    for (int i = -radius; i <= radius; i++) sum += line[i < 0 ? 0 : i >= w ? w - 1 : i];
                    for (int x = 0; x < w; x++)
                    {
                        m[row + x] = (byte)((sum + win / 2) / win);
                        int o = x - radius, n = x + radius + 1;
                        sum += line[n >= w ? w - 1 : n] - line[o < 0 ? 0 : o];
                    }
                }
                for (int x = 0; x < w; x++)
                {
                    int sum = 0;
                    for (int y = 0; y < h; y++) line[y] = m[y * w + x];
                    for (int i = -radius; i <= radius; i++) sum += line[i < 0 ? 0 : i >= h ? h - 1 : i];
                    for (int y = 0; y < h; y++)
                    {
                        m[y * w + x] = (byte)((sum + win / 2) / win);
                        int o = y - radius, n = y + radius + 1;
                        sum += line[n >= h ? h - 1 : n] - line[o < 0 ? 0 : o];
                    }
                }
            }
        }
    }
}
