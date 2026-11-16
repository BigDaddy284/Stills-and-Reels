using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace StillsAndReels
{
    // =========================================================================== pixels & colour

    internal static unsafe class Pixels
    {
        public static Bitmap FromBgra(byte[] bgra, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            CopyInto(bgra, b);
            return b;
        }

        public static void CopyInto(byte[] bgra, Bitmap b)
        {
            int w = b.Width, h = b.Height;
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (d.Stride == w * 4) Marshal.Copy(bgra, 0, d.Scan0, w * h * 4);
                else for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
            }
            finally { b.UnlockBits(d); }
        }

        public static void CopyBitmap(Bitmap src, Bitmap dst)
        {
            using (var g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            }
        }

        /// <summary>Copies the bitmap's pixels as BGRA (bottom-up issues avoided by LockBits).</summary>
        public static void ToBgra(Bitmap b, byte[] dest)
        {
            int w = b.Width, h = b.Height;
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (d.Stride == w * 4) Marshal.Copy(d.Scan0, dest, 0, w * h * 4);
                else for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, dest, y * w * 4, w * 4);
            }
            finally { b.UnlockBits(d); }
        }

        // ------------------------------------------------------------------ colour matrix (row-major 4x4, 0..1 space)

        static double[] Identity() { return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }; }

        static double[] Mul(double[] a, double[] b) // a·b
        {
            var r = new double[16];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j];
                    r[i * 4 + j] = s;
                }
            return r;
        }

        static double[] M(double rr, double rg, double rb, double ro, double gr, double gg, double gb, double go, double br, double bg, double bb, double bo)
        {
            return new double[] { rr, rg, rb, ro, gr, gg, gb, go, br, bg, bb, bo, 0, 0, 0, 1 };
        }

        const double LR = 0.2126, LG = 0.7152, LB = 0.0722;

        static double[] Saturation(double s)
        {
            double a = 1 - s;
            return M(a * LR + s, a * LG, a * LB, 0, a * LR, a * LG + s, a * LB, 0, a * LR, a * LG, a * LB + s, 0);
        }

        static double[] ContrastM(double k) { double o = 0.5 * (1 - k); return M(k, 0, 0, o, 0, k, 0, o, 0, 0, k, o); }
        static double[] Gain(double r, double g, double b) { return M(r, 0, 0, 0, 0, g, 0, 0, 0, 0, b, 0); }
        static double[] Offset(double o) { return M(1, 0, 0, o, 0, 1, 0, o, 0, 0, 1, o); }

        static double[] HueRotate(double deg)
        {
            double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            return M(
                LR + c * (1 - LR) + s * (-LR), LG + c * (-LG) + s * (-LG), LB + c * (-LB) + s * (1 - LB), 0,
                LR + c * (-LR) + s * 0.143, LG + c * (1 - LG) + s * 0.140, LB + c * (-LB) + s * (-0.283), 0,
                LR + c * (-LR) + s * (-(1 - LR)), LG + c * (-LG) + s * LG, LB + c * (1 - LB) + s * LB, 0);
        }

        public static double[] FilterMatrix(string id)
        {
            switch (id)
            {
                case "mono": return Saturation(0);
                case "noir": return Mul(ContrastM(1.45), Saturation(0));
                case "sepia": return M(0.393, 0.769, 0.189, 0, 0.349, 0.686, 0.168, 0, 0.272, 0.534, 0.131, 0);
                case "vintage":
                    return Mul(Offset(0.07), Mul(ContrastM(0.82), Mul(Gain(1.14, 1.02, 0.74), Saturation(0.5))));
                case "warm": return Mul(Gain(1.2, 1.05, 0.76), Saturation(1.1));
                case "cool": return Mul(Gain(0.8, 0.98, 1.22), Saturation(0.95));
                case "vivid": return Mul(ContrastM(1.12), Saturation(1.45));
                case "fade": return Mul(Offset(0.07), Mul(ContrastM(0.8), Saturation(0.75)));
                case "cinematic": return Mul(M(1.08, 0, -0.04, 0, -0.02, 1.0, 0.04, 0.01, -0.06, 0.04, 1.06, 0.03), Mul(ContrastM(1.15), Saturation(0.85)));
                case "dramatic": return Mul(ContrastM(1.35), Saturation(0.65));
                case "invert": return M(-1, 0, 0, 1, 0, -1, 0, 1, 0, 0, -1, 1);
                default: return Identity();
            }
        }

        public static double[] ClipMatrix(Clip c)
        {
            var m = Identity();
            if (Math.Abs(c.Exposure) > 0.01) { double k = Math.Pow(2, c.Exposure / 50.0); m = Mul(Gain(k, k, k), m); }
            if (Math.Abs(c.Brightness) > 0.01) m = Mul(Offset(c.Brightness / 250.0), m);
            if (Math.Abs(c.Contrast) > 0.01) m = Mul(ContrastM(c.Contrast >= 0 ? 1 + c.Contrast / 100.0 * 1.2 : 1 + c.Contrast / 100.0 * 0.8), m);
            if (Math.Abs(c.Saturation) > 0.01) m = Mul(Saturation(Math.Max(0, 1 + c.Saturation / 100.0)), m);
            if (Math.Abs(c.Hue) > 0.01) m = Mul(HueRotate(c.Hue), m);
            if (Math.Abs(c.Temperature) > 0.01) { double k = c.Temperature / 100.0; m = Mul(Gain(1 + 0.22 * k, 1 + 0.03 * k, 1 - 0.22 * k), m); }
            if (Math.Abs(c.Tint) > 0.01) { double k = c.Tint / 100.0; m = Mul(Gain(1 + 0.08 * k, 1 - 0.18 * k, 1 + 0.08 * k), m); }
            if (!string.IsNullOrEmpty(c.Filter) && c.FilterAmount > 0.001)
            {
                var f = FilterMatrix(c.Filter);
                double a = Math.Max(0, Math.Min(1, c.FilterAmount));
                var id = Identity();
                var blend = new double[16];
                for (int i = 0; i < 16; i++) blend[i] = id[i] + (f[i] - id[i]) * a;
                m = Mul(blend, m);
            }
            return m;
        }

        // ------------------------------------------------------------------ effects on raw BGRA (rows run across all cores)

        /// <summary>Applies a colour matrix in place (32bpp ARGB bitmap data).</summary>
        public static void ApplyMatrix(BitmapData d, double[] m) { ApplyMatrix(d.Scan0, d.Scan0, d.Width, d.Height, d.Stride, d.Stride, m); }

        /// <summary>Applies a colour matrix, reading from one buffer and writing to another (or the same one).</summary>
        public static void ApplyMatrix(IntPtr src, IntPtr dst, int w, int h, int srcStride, int dstStride, double[] m)
        {
            // fixed point: coefficients × 1024, offsets in 0..255 × 1024
            int r0 = (int)(m[0] * 1024), r1 = (int)(m[1] * 1024), r2 = (int)(m[2] * 1024), r3 = (int)(m[3] * 255 * 1024);
            int g0 = (int)(m[4] * 1024), g1 = (int)(m[5] * 1024), g2 = (int)(m[6] * 1024), g3 = (int)(m[7] * 255 * 1024);
            int b0 = (int)(m[8] * 1024), b1 = (int)(m[9] * 1024), b2 = (int)(m[10] * 1024), b3 = (int)(m[11] * 255 * 1024);
            Raster.Rows(0, h, (long)w * h * 2, (ya, yb) =>
            {
                for (int y = ya; y < yb; y++)
                {
                    byte* s = (byte*)src + (long)y * srcStride;
                    byte* p = (byte*)dst + (long)y * dstStride;
                    for (int x = 0; x < w; x++, p += 4, s += 4)
                    {
                        int b = s[0], g = s[1], r = s[2];
                        int nr = (r0 * r + r1 * g + r2 * b + r3) >> 10;
                        int ng = (g0 * r + g1 * g + g2 * b + g3) >> 10;
                        int nb = (b0 * r + b1 * g + b2 * b + b3) >> 10;
                        p[2] = (byte)(nr < 0 ? 0 : nr > 255 ? 255 : nr);
                        p[1] = (byte)(ng < 0 ? 0 : ng > 255 ? 255 : ng);
                        p[0] = (byte)(nb < 0 ? 0 : nb > 255 ? 255 : nb);
                        p[3] = s[3];
                    }
                }
            });
        }

        /// <summary>Makes pixels close to the key colour transparent (YCbCr distance) and removes colour spill.</summary>
        public static void ChromaKey(IntPtr scan0, int w, int h, int stride, Color key, double similarity, double smoothness)
        {
            // CbCr in fixed point (× 1024); distances compared squared so most pixels never need a square root
            int kcb = (-173 * key.R - 339 * key.G + 512 * key.B), kcr = (512 * key.R - 429 * key.G - 83 * key.B);
            double sim = 4 + similarity / 100.0 * 110;       // CbCr distance in 0..~180 space
            double smo = Math.Max(1, smoothness / 100.0 * 80);
            double far = (sim + smo) * 1024, near = sim * 1024;
            double far2 = far * far;
            bool green = key.G >= key.R && key.G >= key.B;
            bool blue = key.B > key.R && key.B > key.G;
            Raster.Rows(0, h, (long)w * h * 2, (ya, yb) =>
            {
                for (int y = ya; y < yb; y++)
                {
                    byte* p = (byte*)scan0 + (long)y * stride;
                    for (int x = 0; x < w; x++, p += 4)
                    {
                        int b = p[0], g = p[1], r = p[2];
                        int cb = -173 * r - 339 * g + 512 * b - kcb, cr = 512 * r - 429 * g - 83 * b - kcr;
                        double d2 = (double)cb * cb + (double)cr * cr;
                        if (d2 >= far2) continue;
                        double a = (Math.Sqrt(d2) - near) / (far - near);
                        if (a >= 1) continue;
                        if (a < 0) a = 0;
                        p[3] = (byte)(p[3] * a);
                        // spill suppression on the semi-transparent edge
                        if (green) { int lim = Math.Max(r, b); if (g > lim) p[1] = (byte)lim; }
                        else if (blue) { int lim = Math.Max(r, g); if (b > lim) p[0] = (byte)lim; }
                    }
                }
            });
        }

        /// <summary>
        /// Soft blur of all four channels: three box passes, close to a Gaussian. A wide blur has no fine detail
        /// left to keep, so it is done on a half, quarter or eighth size copy and stretched back, which is many times faster.
        /// </summary>
        public static void Blur(IntPtr scan0, int w, int h, int stride, int radius)
        {
            if (radius < 1) return;
            int levels = radius <= 2 ? 0 : radius <= 5 ? 1 : radius <= 12 ? 2 : 3;
            while (levels > 0 && (w >> levels < 16 || h >> levels < 16)) levels--;
            if (levels == 0 || stride != w * 4) { BoxBlur((byte*)scan0, w, h, stride, radius); return; }
            var full = new Frame(w, h, Raster.Rent((long)w * h * 4));
            fixed (byte* fp = full.Data) Buffer.MemoryCopy((void*)scan0, fp, (long)w * h * 4, (long)w * h * 4);
            Frame small = full;
            for (int i = 0; i < levels; i++)
            {
                var next = new Frame((small.Width + 1) / 2, (small.Height + 1) / 2, Raster.Rent((long)((small.Width + 1) / 2) * ((small.Height + 1) / 2) * 4));
                Raster.Halve(small, next);
                Raster.Return(small.Data);
                small = next;
            }
            fixed (byte* sp = small.Data) BoxBlur(sp, small.Width, small.Height, small.Width * 4, Math.Max(1, (int)Math.Round(radius / (double)(1 << levels))));
            Raster.Stretch(small, (byte*)scan0, w, h);
            Raster.Return(small.Data);
        }

        static void BoxBlur(byte* p, int w, int h, int stride, int radius)
        {
            var tmp = Raster.Rent((long)w * h * 4);
            var pin = GCHandle.Alloc(tmp, GCHandleType.Pinned);
            try
            {
                byte* t = (byte*)pin.AddrOfPinnedObject();
                int win = radius * 2 + 1;
                // 1/win as a fixed-point multiply: a divide per pixel is the slow part of a box blur
                int recip = (int)((1L << 24) / win);
                for (int pass = 0; pass < 3; pass++)
                {
                    // across: p -> t
                    Raster.Rows(0, h, (long)w * h * 4, (ya, yb) =>
                    {
                        for (int y = ya; y < yb; y++)
                        {
                            byte* s = p + (long)y * stride, o = t + (long)y * w * 4;
                            for (int c = 0; c < 4; c++)
                            {
                                int sum = 0;
                                for (int i = -radius; i <= radius; i++) sum += s[Clamp(i, w) * 4 + c];
                                for (int i = 0; i < w; i++)
                                {
                                    o[i * 4 + c] = (byte)(((long)sum * recip + (1 << 23)) >> 24);
                                    sum += s[Clamp(i + radius + 1, w) * 4 + c] - s[Clamp(i - radius, w) * 4 + c];
                                }
                            }
                        }
                    });
                    // down: t -> p, a row at a time with a running sum per column (stays in the cache, unlike walking columns)
                    Raster.Rows(0, h, (long)w * h * 4, (ya, yb) =>
                    {
                        var sums = new int[w * 4];
                        fixed (int* sm = sums)
                        {
                            for (int i = -radius; i <= radius; i++)
                            {
                                byte* r = t + (long)Clamp(ya + i, h) * w * 4;
                                for (int x = 0; x < w * 4; x++) sm[x] += r[x];
                            }
                            for (int y = ya; y < yb; y++)
                            {
                                byte* o = p + (long)y * stride;
                                byte* add = t + (long)Clamp(y + radius + 1, h) * w * 4, sub = t + (long)Clamp(y - radius, h) * w * 4;
                                for (int x = 0; x < w * 4; x++)
                                {
                                    int v = sm[x];
                                    o[x] = (byte)(((long)v * recip + (1 << 23)) >> 24);
                                    sm[x] = v + add[x] - sub[x];
                                }
                            }
                        }
                    });
                }
            }
            finally { pin.Free(); Raster.Return(tmp); }
        }

        static int Clamp(int i, int n) { return i < 0 ? 0 : i >= n ? n - 1 : i; }

        public static void Sharpen(IntPtr scan0, int w, int h, int stride, double amount)
        {
            int k = (int)(amount / 100.0 * 2.0 * 256);
            if (k <= 0 || w < 3 || h < 3) return;
            var copy = Raster.Rent((long)stride * h);
            Marshal.Copy(scan0, copy, 0, stride * h);
            int s = stride;
            Raster.Rows(1, h - 1, (long)w * h * 2, (ya, yb) =>
            {
                fixed (byte* src = copy)
                {
                    for (int y = ya; y < yb; y++)
                    {
                        byte* o = (byte*)scan0 + (long)y * s + 4;
                        byte* c = src + (long)y * s + 4;
                        for (int x = 1; x < w - 1; x++, o += 4, c += 4)
                        {
                            for (int ch = 0; ch < 3; ch++)
                            {
                                int center = c[ch];
                                int avg = (c[ch - 4] + c[ch + 4] + c[ch - s] + c[ch + s]) >> 2;
                                int v = center + (((center - avg) * k) >> 8);
                                o[ch] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                            }
                        }
                    }
                }
            });
            Raster.Return(copy);
        }

        public static void Vignette(IntPtr scan0, int w, int h, int stride, double amount)
        {
            double cx = w / 2.0, cy = h / 2.0;
            double strength = amount / 100.0 * 0.85;
            // darkening by squared distance from the centre (0 centre .. 1 corner), looked up instead of computed per pixel
            const int N = 1024;
            var lut = new int[N + 1];
            for (int i = 0; i <= N; i++)
            {
                double r = Math.Sqrt(i / (double)N);
                double f = 1 - strength * Math.Pow(Math.Max(0, (r - 0.35) / 0.65), 1.6);
                lut[i] = (int)(Math.Max(0, f) * 256);
            }
            double inv2 = N / (cx * cx + cy * cy);
            Raster.Rows(0, h, (long)w * h, (ya, yb) =>
            {
                fixed (int* l = lut)
                {
                    for (int y = ya; y < yb; y++)
                    {
                        double dy = y + 0.5 - cy, dy2 = dy * dy;
                        byte* p = (byte*)scan0 + (long)y * stride;
                        for (int x = 0; x < w; x++, p += 4)
                        {
                            double dx = x + 0.5 - cx;
                            int i = (int)((dx * dx + dy2) * inv2);
                            int f = l[i > N ? N : i];
                            if (f >= 256) continue;
                            p[0] = (byte)((p[0] * f) >> 8); p[1] = (byte)((p[1] * f) >> 8); p[2] = (byte)((p[2] * f) >> 8);
                        }
                    }
                }
            });
        }

        public static void Grain(IntPtr scan0, int w, int h, int stride, double amount, int seed)
        {
            int amp = (int)(amount / 100.0 * 48);
            if (amp <= 0) return;
            Raster.Rows(0, h, (long)w * h, (ya, yb) =>
            {
                for (int y = ya; y < yb; y++)
                {
                    // each row has its own sequence, so rows can be done in any order and still give the same picture
                    uint s = (uint)(seed * 2654435761u + (uint)y * 40503u + 12345u) | 1u;
                    s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                    byte* p = (byte*)scan0 + (long)y * stride;
                    for (int x = 0; x < w; x++, p += 4)
                    {
                        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                        int n = (int)(s & 0xFF) - 128;
                        int v = (n * amp) >> 7;
                        int b = p[0] + v, g = p[1] + v, r = p[2] + v;
                        p[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        p[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        p[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                    }
                }
            });
        }

        /// <summary>True when the clip needs a per-pixel pass before it is drawn.</summary>
        public static bool NeedsPass(Clip c)
        {
            return c.HasColorWork || c.ChromaKey || c.Blur > 0.5 || c.Sharpen > 0.5 || c.Vignette > 0.5 || c.Grain > 0.5;
        }

        /// <summary>
        /// Runs all of the clip's pixel effects, reading src (opaque or straight alpha) and writing dst (same size).
        /// dst comes out ready to draw: opaque, or premultiplied when the effects made parts of it transparent.
        /// </summary>
        public static void ApplyEffects(Frame src, Frame dst, Clip c, int frameIndex)
        {
            int w = src.Width, h = src.Height, stride = w * 4;
            fixed (byte* s = src.Data) fixed (byte* d = dst.Data)
            {
                IntPtr sp = (IntPtr)s, dp = (IntPtr)d;
                if (c.HasColorWork) ApplyMatrix(sp, dp, w, h, stride, stride, ClipMatrix(c));
                else Buffer.MemoryCopy(s, d, dst.Data.Length, (long)stride * h);
                dst.Opaque = src.Opaque;
                dst.Premultiplied = src.Opaque;
                if (c.ChromaKey) { ChromaKey(dp, w, h, stride, Color.FromArgb(c.KeyColor), c.KeySimilarity, c.KeySmoothness); dst.Opaque = false; dst.Premultiplied = false; }
                if (c.Blur > 0.5) Blur(dp, w, h, stride, Math.Max(1, (int)Math.Round(c.Blur / 100.0 * Math.Max(w, h) / 45.0)));
                if (c.Sharpen > 0.5) Sharpen(dp, w, h, stride, c.Sharpen);
                if (c.Vignette > 0.5) Vignette(dp, w, h, stride, c.Vignette);
                if (c.Grain > 0.5) Grain(dp, w, h, stride, c.Grain, frameIndex);
            }
            if (!dst.Opaque) Raster.Premultiply(dst);
        }
    }

    // =========================================================================== frame sources

    /// <summary>Supplies source pictures for clips. Implemented for paused preview (single grabs) and playback/export (streams).</summary>
    internal interface IFrameSource : IDisposable
    {
        /// <summary>
        /// The clip's source picture at timeline time t, decoded at the given size. Owned by the source, and only
        /// valid until the next Get for the same clip. Opaque, or straight (not premultiplied) alpha.
        /// </summary>
        Frame Get(Clip c, MediaItem m, double t, Size size);
        void BeginFrame();
        void EndFrame();
        void Prepare(Clip c, MediaItem m, double t, Size size);
    }

    internal static class SourceFilters
    {
        /// <summary>FFmpeg filters for crop and flips (applied before scaling).</summary>
        public static string Pre(Clip c, bool stream)
        {
            var parts = new List<string>();
            if (c.HasCrop)
            {
                double w = Math.Max(0.02, 1 - c.CropL - c.CropR), h = Math.Max(0.02, 1 - c.CropT - c.CropB);
                parts.Add(string.Format(CultureInfo.InvariantCulture, "crop=iw*{0:0.####}:ih*{1:0.####}:iw*{2:0.####}:ih*{3:0.####}", w, h, c.CropL, c.CropT));
            }
            if (c.FlipH) parts.Add("hflip");
            if (c.FlipV) parts.Add("vflip");
            if (stream && c.Stabilize) parts.Insert(0, "deshake");
            return string.Join(",", parts.ToArray());
        }

        /// <summary>Picture of an image clip: cropped, flipped and scaled (cached by the caller).</summary>
        public static Frame ImageFrame(MediaItem m, Clip c, Size size)
        {
            using (var b = ImageBitmap(m, c, size)) return b == null ? null : Frame.FromBitmap(b, false);
        }

        static Bitmap ImageBitmap(MediaItem m, Clip c, Size size)
        {
            Bitmap pic;
            lock (m.Sync) pic = m.Picture;
            if (pic == null) return null;
            var b = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            lock (m.Sync)
            {
                if (m.Picture == null) { b.Dispose(); return null; }
                pic = m.Picture;
                var src = new RectangleF((float)(c.CropL * pic.Width), (float)(c.CropT * pic.Height),
                    (float)(Math.Max(0.02, 1 - c.CropL - c.CropR) * pic.Width), (float)(Math.Max(0.02, 1 - c.CropT - c.CropB) * pic.Height));
                using (var g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    var dest = new RectangleF(0, 0, size.Width, size.Height);
                    if (c.FlipH || c.FlipV)
                    {
                        g.TranslateTransform(c.FlipH ? size.Width : 0, c.FlipV ? size.Height : 0);
                        g.ScaleTransform(c.FlipH ? -1 : 1, c.FlipV ? -1 : 1);
                    }
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(pic, new[] { new PointF(0, 0), new PointF(dest.Width, 0), new PointF(0, dest.Height) }, src, GraphicsUnit.Pixel, ia);
                    }
                }
            }
            return b;
        }
    }

    /// <summary>
    /// Frame grabs for the paused preview and scrubbing, with a cache. Each grab decodes a short run of frames
    /// from the wanted one onwards (see FrameGrabber.GrabRun), so dragging the playhead mostly finds the next
    /// frame already decoded instead of starting FFmpeg again for every pixel the pointer moves.
    /// </summary>
    internal sealed class StillFrames : IFrameSource
    {
        readonly Dictionary<string, Frame> cache = new Dictionary<string, Frame>();
        readonly LinkedList<string> lru = new LinkedList<string>();
        const int MaxEntries = 400;
        const long MaxBytes = 320L * 1024 * 1024;
        const long RunBytes = 36L * 1024 * 1024;   // decoded ahead per grab, at most one second
        long bytes;
        readonly object procLock = new object();
        Process current;
        volatile int cancelGen;

        public Func<bool> IsCancelled = () => false;
        /// <summary>Live scrubbing: fast scaling and frames read ahead; otherwise sharp single frames.</summary>
        public bool Live;
        readonly Dictionary<int, double> lastSrc = new Dictionary<int, double>(); // last source time asked for, per media item
        // keyframes fetched for coarse scrubbing, per media item: time in the file -> cache key, and the stretches
        // of the file those runs covered (a keyframe serves a time only inside the stretch it was fetched for)
        sealed class KeyRuns { public readonly SortedList<double, string> Keys = new SortedList<double, string>(); public readonly List<KeyValuePair<double, double>> Covered = new List<KeyValuePair<double, double>>(); }
        readonly Dictionary<string, KeyRuns> keyRuns = new Dictionary<string, KeyRuns>();

        static string KeyRunId(Clip c, MediaItem m, Size size, string pre) { return m.Id + "|" + size.Width + "x" + size.Height + "|" + pre + "|" + m.SourcePath; }

        /// <summary>The cached keyframe at or before src, if a fetched run covers src; otherwise null.</summary>
        Frame KeyframeFor(Clip c, MediaItem m, double src, Size size, string pre, int gen)
        {
            KeyRuns kr;
            if (!keyRuns.TryGetValue(KeyRunId(c, m, size, pre), out kr)) return null;
            bool covered = false;
            foreach (var iv in kr.Covered) if (src >= iv.Key - 1e-6 && src <= iv.Value + 1e-6) { covered = true; break; }
            if (!covered) return null;
            // the largest keyframe time <= src
            var keys = kr.Keys.Keys;
            int lo = 0, hi = keys.Count - 1, best = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (keys[mid] <= src + 1e-6) { best = mid; lo = mid + 1; } else hi = mid - 1; }
            if (best < 0) return null;
            string k = kr.Keys.Values[best];
            Frame b;
            if (!cache.TryGetValue(k, out b)) { kr.Keys.RemoveAt(best); return null; } // evicted meanwhile: fetch again
            Touch(k);
            return b;
        }

        /// <summary>Decodes a batch of keyframes from 'from' onwards and caches them.</summary>
        void FetchKeyframes(Clip c, MediaItem m, double from, Size size, string pre, int gen)
        {
            string id = KeyRunId(c, m, size, pre);
            KeyRuns kr;
            if (!keyRuns.TryGetValue(id, out kr)) keyRuns[id] = kr = new KeyRuns();
            var got = new List<KeyValuePair<double, Frame>>();
            try
            {
                FrameGrabber.GrabKeyframes(m.SourcePath, from, size, pre, 16, (pt, b) => got.Add(new KeyValuePair<double, Frame>(pt, b)),
                    () => gen != cancelGen, p => { lock (procLock) current = p; }, m.Info);
            }
            catch { }
            lock (procLock) current = null;
            if (got.Count == 0) return;
            got.Sort((x, y) => x.Key.CompareTo(y.Key));
            foreach (var kv in got)
            {
                string k = "key|" + id + "|" + Math.Round(kv.Key * 1000);
                if (cache.ContainsKey(k)) Touch(k);
                else Put(k, kv.Value);
                kr.Keys[kv.Key] = k;
            }
            double first = got[0].Key, last = got[got.Count - 1].Key;
            // the run covers from its first keyframe to just before the keyframe that would have followed the last;
            // a run that ended early (end of file) covers to the end
            double gap = got.Count > 1 ? (last - first) / (got.Count - 1) : 1.0;
            double to = got.Count < 16 && m.Duration > 0 ? Math.Max(last, m.Duration) : last + gap * 0.999;
            kr.Covered.Add(new KeyValuePair<double, double>(Math.Min(from, first), to));
            if (kr.Covered.Count > 64) kr.Covered.RemoveAt(0);
        }

        public void Cancel()
        {
            cancelGen++;
            lock (procLock) { FFmpeg.KillQuietly(current); current = null; }
        }

        static string Key(Clip c, MediaItem m, double src, Size size, bool sharp)
        {
            return m.Id + "|" + Math.Round(src * 1000) + "|" + size.Width + "x" + size.Height + "|" + (sharp ? "s|" : "f|") + SourceFilters.Pre(c, false) + "|" + m.SourcePath;
        }

        public Frame Get(Clip c, MediaItem m, double t, Size size)
        {
            if (m.Kind == MediaKind.Image)
            {
                string ik = "img" + m.Id + "|" + size.Width + "x" + size.Height + "|" + c.CropL + c.CropT + c.CropR + c.CropB + c.FlipH + c.FlipV;
                Frame hit;
                if (cache.TryGetValue(ik, out hit)) { Touch(ik); return hit; }
                var b = SourceFilters.ImageFrame(m, c, size);
                if (b != null) Put(ik, b);
                return b;
            }
            double dur = m.Duration;
            double fd = 1.0 / Math.Max(1, m.Info != null ? m.Info.Fps : 30);
            double src = Math.Max(0, c.SourceTime(t));
            if (dur > 0) src = Math.Min(src, Math.Max(0, dur - fd * 0.5));
            src = Math.Floor(src / fd + 1e-6) * fd; // whole frames
            // how far the pointer moved since the last request (cache hits count too, or a fine drag that leaves
            // the cached stretch would look like a jump)
            double prev;
            bool havePrev = lastSrc.TryGetValue(m.Id, out prev);
            lastSrc[m.Id] = src;
            bool sharp = !Live;
            string key = Key(c, m, src, size, sharp);
            Frame cached;
            if (cache.TryGetValue(key, out cached)) { Touch(key); return cached; }
            // while scrubbing, a sharp frame of the same moment does as well
            int gen = cancelGen;
            Frame frame = null;
            string pre = SourceFilters.Pre(c, false);
            long frameBytes = (long)size.Width * size.Height * 4;
            int run = (int)Math.Max(1, Math.Min(Math.Round(1 / fd), RunBytes / Math.Max(1, frameBytes)));
            if (!Live) run = Math.Min(run, 8); // paused: a few frames ahead for stepping with , and .
            if (Live)
            {
                string alt = Key(c, m, src, size, true);
                if (cache.TryGetValue(alt, out cached)) { Touch(alt); return cached; }
                // A fast drag, or a zoomed-out timeline, moves further between one mouse event and the next than the
                // read-ahead covers (a pixel can be a second of video). Decoding ahead frame by frame is wasted then,
                // and an exact grab decodes every frame from the previous keyframe to src. Instead the keyframe at
                // or before src is shown, fetched in batches: one FFmpeg run decodes the next 16 keyframes (tens of
                // seconds of video) and caches them all, so the following mouse events are served from the cache.
                // Keyframes stand in only while dragging: the exact frame is drawn when the drag ends, and they are
                // cached under their own keys so they are never mistaken for exact frames.
                if (havePrev && Math.Abs(src - prev) > run * fd * 0.5)
                {
                    Frame kf = KeyframeFor(c, m, src, size, pre, gen);
                    if (kf != null || gen != cancelGen || IsCancelled()) return kf;
                    bool backwards = src < prev;
                    FetchKeyframes(c, m, backwards ? Math.Max(0, src - 10) : src, size, pre, gen);
                    kf = KeyframeFor(c, m, src, size, pre, gen);
                    if (kf != null || gen != cancelGen || IsCancelled()) return kf;
                    if (backwards) { FetchKeyframes(c, m, src, size, pre, gen); kf = KeyframeFor(c, m, src, size, pre, gen); }
                    return kf;
                }
            }
            // dragging backwards: decode the run that ends at src, so the frames before it are the ones kept
            double runStart = Live && havePrev && src < prev ? Math.Max(0, src - (run - 1) * fd) : src;
            double[] tries = { runStart, src - fd * 2, src - 0.3, src - 1.0 };
            foreach (var tt in tries)
            {
                if (tt < 0 && tt != runStart) continue;
                if (gen != cancelGen || IsCancelled()) return null;
                try
                {
                    var got = new List<KeyValuePair<double, Frame>>();
                    FrameGrabber.GrabRun(m.SourcePath, Math.Max(0, tt), size, pre, run, 1 / fd, sharp,
                        (st, b) => got.Add(new KeyValuePair<double, Frame>(st, b)),
                        () => gen != cancelGen,
                        p => { lock (procLock) current = p; }, m.Info);
                    foreach (var kv in got)
                    {
                        double ft = Math.Floor(kv.Key / fd + 0.5) * fd;
                        string k = Key(c, m, ft, size, sharp);
                        Frame have;
                        if (cache.TryGetValue(k, out have)) { Touch(k); if (k == key) frame = have; continue; }
                        Put(k, kv.Value);
                        if (k == key) frame = kv.Value;
                    }
                    if (frame == null && got.Count > 0 && tt < src)
                    {
                        // the file ends before src: its last frame stands in
                        frame = got[got.Count - 1].Value;
                        Put(key, frame);
                    }
                }
                catch { frame = null; }
                lock (procLock) current = null;
                if (frame != null || gen != cancelGen) break;
            }
            if (frame == null || gen != cancelGen) return null;
            return frame;
        }

        void Touch(string k) { lru.Remove(k); lru.AddFirst(k); }

        void Put(string k, Frame b)
        {
            Frame old;
            if (cache.TryGetValue(k, out old)) { bytes -= old.Bytes; lru.Remove(k); }
            cache[k] = b;
            bytes += b.Bytes;
            lru.AddFirst(k);
            while (lru.Count > 1 && (lru.Count > MaxEntries || bytes > MaxBytes))
            {
                var last = lru.Last.Value;
                lru.RemoveLast();
                Frame ob;
                if (cache.TryGetValue(last, out ob)) { cache.Remove(last); bytes -= ob.Bytes; }
            }
        }

        public void BeginFrame() { }
        public void EndFrame() { }
        public void Prepare(Clip c, MediaItem m, double t, Size size) { }

        public void Dispose()
        {
            Cancel();
            cache.Clear();
            lru.Clear();
            keyRuns.Clear();
            lastSrc.Clear();
            bytes = 0;
        }
    }

    /// <summary>A running decoder for one clip: frames at the project frame rate, read ahead on a thread.</summary>
    internal sealed class ClipStream : IDisposable
    {
        public readonly string Key;
        public readonly double T0;     // timeline time of frame 0
        readonly double fps;
        readonly int width, height, frameBytes;
        readonly Process proc;
        readonly Queue<KeyValuePair<int, byte[]>> queue = new Queue<KeyValuePair<int, byte[]>>();
        readonly Stack<byte[]> pool = new Stack<byte[]>();
        readonly object sync = new object();
        const int MaxQueue = 4;
        bool eof, disposed;
        byte[] currentBuf;
        int currentIndex = -1;
        Frame current;
        readonly bool opaque;
        public int LastUsed;

        public ClipStream(string key, Clip c, MediaItem m, double t0, double fps, Size size, bool sharp)
        {
            Key = key;
            this.fps = fps;
            width = size.Width; height = size.Height;
            frameBytes = width * height * 4;
            // frames before the start of the source simply repeat the first frame
            double earliest = c.Start - c.In / Math.Max(0.01, c.Speed);
            T0 = Math.Max(t0, earliest);
            double src = Math.Max(0, c.SourceTime(T0));
            string pre = SourceFilters.Pre(c, true);
            string vf = string.Format(CultureInfo.InvariantCulture,
                "{0}setpts=(PTS-STARTPTS)/{1:0.#####},fps={2:0.###},{3}",
                pre.Length > 0 ? pre + "," : "", c.Speed, fps, VideoQuality.ToBgra(width, height, sharp, m.Info));
            string args = FFmpeg.InputArgs(m.SourcePath, src) + "-map 0:v:0 -an -sn -dn -vf \"" + vf + "\" -f rawvideo -pix_fmt bgra pipe:1";
            proc = FFmpeg.Start(args);
            proc.BeginErrorReadLine();
            opaque = m.Info == null || !m.Info.HasAlpha;
            new Thread(Reader) { IsBackground = true, Name = "clip decoder" }.Start();
        }

        void Reader()
        {
            int index = 0;
            try
            {
                var s = proc.StandardOutput.BaseStream;
                while (true)
                {
                    byte[] buf;
                    lock (sync)
                    {
                        while (!disposed && queue.Count >= MaxQueue) Monitor.Wait(sync);
                        if (disposed) return;
                        buf = pool.Count > 0 ? pool.Pop() : new byte[frameBytes];
                    }
                    int off = 0;
                    while (off < frameBytes)
                    {
                        int n = s.Read(buf, off, frameBytes - off);
                        if (n <= 0) break;
                        off += n;
                    }
                    lock (sync)
                    {
                        if (off < frameBytes || disposed) { eof = true; Monitor.PulseAll(sync); return; }
                        queue.Enqueue(new KeyValuePair<int, byte[]>(index++, buf));
                        Monitor.PulseAll(sync);
                    }
                }
            }
            catch { lock (sync) { eof = true; Monitor.PulseAll(sync); } }
        }

        public int IndexFor(double t) { return Math.Max(0, (int)Math.Floor((t - T0) * fps + 0.5)); }

        /// <summary>
        /// The frame for timeline time t. Waits up to waitMs for it (−1 = until it arrives or the stream ends).
        /// Returns null if nothing has been decoded yet.
        /// </summary>
        public Frame GetFrame(double t, int waitMs)
        {
            int k = IndexFor(t);
            var sw = Stopwatch.StartNew();
            lock (sync)
            {
                while (currentIndex < k)
                {
                    if (queue.Count > 0)
                    {
                        var item = queue.Dequeue();
                        if (currentBuf != null) pool.Push(currentBuf);
                        currentBuf = item.Value;
                        currentIndex = item.Key;
                        current = null;
                        Monitor.PulseAll(sync);
                        continue;
                    }
                    if (eof) break;
                    int left = waitMs < 0 ? 1000 : waitMs - (int)sw.ElapsedMilliseconds;
                    if (currentBuf == null && waitMs >= 0) left = Math.Max(left, 1500 - (int)sw.ElapsedMilliseconds); // first frame: allow start-up time
                    if (left <= 0) break;
                    Monitor.Wait(sync, Math.Min(left, 1000));
                }
                if (currentBuf == null) return null;
                // the decoder's own buffer, with no copy: it stays untouched until a later frame is asked for
                if (current == null) current = new Frame(width, height, currentBuf) { Opaque = opaque, Premultiplied = opaque };
                return current;
            }
        }

        public bool Ended { get { lock (sync) return eof && queue.Count == 0; } }
        public int CurrentIndex { get { lock (sync) return Math.Max(0, currentIndex); } }

        public void Dispose()
        {
            lock (sync) { disposed = true; Monitor.PulseAll(sync); }
            FFmpeg.KillQuietly(proc);
        }
    }

    /// <summary>Streaming decoders for continuous rendering (playback and export).</summary>
    internal sealed class StreamFrames : IFrameSource
    {
        readonly double fps;
        readonly bool export;
        readonly Dictionary<int, ClipStream> streams = new Dictionary<int, ClipStream>();
        readonly Dictionary<string, Frame> images = new Dictionary<string, Frame>();
        readonly HashSet<string> usedImages = new HashSet<string>();
        int frame;

        public StreamFrames(double fps, bool export) { this.fps = fps; this.export = export; }

        static string Key(Clip c, MediaItem m, Size size)
        {
            return m.SourcePath + "|" + size.Width + "x" + size.Height + "|" + SourceFilters.Pre(c, true) + "|" + c.In.ToString("R") + "|" + c.Speed.ToString("R") + "|" + c.Start.ToString("R");
        }

        ClipStream Ensure(Clip c, MediaItem m, double t, Size size)
        {
            string key = Key(c, m, size);
            ClipStream s;
            if (streams.TryGetValue(c.Id, out s))
            {
                bool usable = s.Key == key && t >= s.T0 - 1e-6 && s.IndexFor(t) >= 0;
                // a big jump forwards is quicker with a fresh decoder
                if (usable && s.IndexFor(t) - s.CurrentIndex > fps * 3) usable = false;
                if (!usable) { s.Dispose(); streams.Remove(c.Id); s = null; }
            }
            if (s == null)
            {
                s = new ClipStream(key, c, m, t, fps, size, export);
                streams[c.Id] = s;
            }
            s.LastUsed = frame;
            return s;
        }

        public Frame Get(Clip c, MediaItem m, double t, Size size)
        {
            if (m.Kind == MediaKind.Image)
            {
                string ik = m.Id + "|" + size.Width + "x" + size.Height + "|" + c.CropL + c.CropT + c.CropR + c.CropB + c.FlipH + c.FlipV;
                usedImages.Add(ik);
                Frame b;
                if (!images.TryGetValue(ik, out b))
                {
                    b = SourceFilters.ImageFrame(m, c, size);
                    if (b == null) return null;
                    images[ik] = b;
                }
                return b;
            }
            var s = Ensure(c, m, t, size);
            return s.GetFrame(t, export ? -1 : 45);
        }

        public void Prepare(Clip c, MediaItem m, double t, Size size)
        {
            if (m.Kind == MediaKind.Image) return;
            ClipStream s;
            if (streams.TryGetValue(c.Id, out s) && s.Key == Key(c, m, size)) { s.LastUsed = frame; return; }
            Ensure(c, m, t, size);
        }

        public void BeginFrame() { frame++; usedImages.Clear(); }

        public void EndFrame()
        {
            var drop = new List<int>();
            foreach (var kv in streams) if (kv.Value.LastUsed != frame) drop.Add(kv.Key);
            foreach (var id in drop) { streams[id].Dispose(); streams.Remove(id); }
            if (images.Count > usedImages.Count + 4)
            {
                var gone = new List<string>();
                foreach (var k in images.Keys) if (!usedImages.Contains(k)) gone.Add(k);
                foreach (var k in gone) images.Remove(k);
            }
        }

        public void Dispose()
        {
            foreach (var s in streams.Values) s.Dispose();
            streams.Clear();
            images.Clear();
        }
    }

    // =========================================================================== text

    internal static class TextLayout
    {
        // GDI+ objects aren't safe to share between threads, and text is drawn by the preview, playback and export threads
        [ThreadStatic] static Dictionary<string, FontFamily> families;

        public static FontFamily Family(string name)
        {
            if (families == null) families = new Dictionary<string, FontFamily>();
            FontFamily f;
            if (families.TryGetValue(name ?? "", out f)) return f;
            try { f = new FontFamily(string.IsNullOrEmpty(name) ? "Segoe UI" : name); }
            catch
            {
                try { f = new FontFamily("Segoe UI"); }
                catch { f = new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif); }
            }
            families[name ?? ""] = f;
            return f;
        }

        static FontStyle Style(FontFamily f, bool bold, bool italic)
        {
            var s = (bold ? FontStyle.Bold : FontStyle.Regular) | (italic ? FontStyle.Italic : FontStyle.Regular);
            if (f.IsStyleAvailable(s)) return s;
            if (f.IsStyleAvailable(FontStyle.Regular)) return FontStyle.Regular;
            if (f.IsStyleAvailable(FontStyle.Bold)) return FontStyle.Bold;
            return s;
        }

        /// <summary>Text outline as a path centred on (0,0), in canvas pixels for a canvas of the given height.</summary>
        public static GraphicsPath Build(Clip c, float canvasH, out RectangleF box, out float em)
        {
            em = (float)Math.Max(2, c.FontSize * canvasH);
            string text = string.IsNullOrEmpty(c.Text) ? " " : c.Text.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = text.Split('\n');
            var fam = Family(c.Font);
            var path = new GraphicsPath();
            var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            bool lower = c.TextStyle == "lower";
            float y = 0;
            var lineBounds = new List<RectangleF>();
            var linePaths = new List<GraphicsPath>();
            float maxW = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                float size = em * (lower && i > 0 ? 0.62f : 1f);
                bool bold = lower ? i == 0 || c.Bold : c.Bold;
                var lp = new GraphicsPath();
                string line = lines[i].Length == 0 ? " " : lines[i];
                try { lp.AddString(line, fam, (int)Style(fam, bold, c.Italic), size, new PointF(0, y), fmt); }
                catch { using (var ff = new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif)) lp.AddString(line, ff, (int)FontStyle.Regular, size, new PointF(0, y), fmt); }
                var b = lp.GetBounds();
                if (lines[i].Trim().Length == 0) b = new RectangleF(0, y, size * 0.3f, size);
                float w = b.Width;
                lineBounds.Add(new RectangleF(0, y, w, size * 1.18f));
                linePaths.Add(lp);
                maxW = Math.Max(maxW, w);
                y += size * 1.22f;
            }
            float totalH = y - (lines.Length > 0 ? 0.04f * em : 0);
            int align = lower ? 0 : c.Align;
            for (int i = 0; i < linePaths.Count; i++)
            {
                var lp = linePaths[i];
                var b = lp.GetBounds();
                float lineW = lineBounds[i].Width;
                float dx = align == 0 ? 0 : align == 2 ? maxW - lineW : (maxW - lineW) / 2;
                dx -= (b.Width > 0 && lines[i].Trim().Length > 0 ? b.Left : 0);
                using (var m = new Matrix()) { m.Translate(dx - maxW / 2, -totalH / 2); lp.Transform(m); }
                path.AddPath(lp, false);
                lp.Dispose();
            }
            box = new RectangleF(-maxW / 2, -totalH / 2, maxW, totalH);
            fmt.Dispose();
            return path;
        }

        public static void Draw(Graphics g, Clip c, float canvasH, Matrix transform, double opacity)
        {
            float em;
            RectangleF box;
            using (var path = Build(c, canvasH, out box, out em))
            {
                var st = g.Save();
                g.MultiplyTransform(transform);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Func<int, int> A = a => (int)Math.Max(0, Math.Min(255, a * opacity));
                var textColor = Color.FromArgb(c.TextColor);
                var outline = Color.FromArgb(c.OutlineColor);
                bool lower = c.TextStyle == "lower";
                double boxOpacity = c.BoxOpacity;
                if (lower && boxOpacity <= 0.001) boxOpacity = 0.75;
                if (boxOpacity > 0.001)
                {
                    float padX = em * (lower ? 0.55f : 0.35f), padY = em * 0.22f;
                    var br = RectangleF.Inflate(box, padX, padY);
                    if (lower) br = new RectangleF(br.Left - em * 0.12f, br.Top, br.Width + em * 0.12f, br.Height);
                    var bc = Color.FromArgb(c.BoxColor);
                    using (var bp = Theme.Rounded(br, lower ? em * 0.08f : em * 0.18f))
                    using (var b = new SolidBrush(Color.FromArgb(A((int)(255 * boxOpacity)), bc.R, bc.G, bc.B)))
                        g.FillPath(b, bp);
                    if (lower)
                    {
                        var bar = new RectangleF(br.Left, br.Top, em * 0.14f, br.Height);
                        using (var b = new LinearGradientBrush(new RectangleF(bar.X, bar.Y, bar.Width, Math.Max(1, bar.Height)),
                            Color.FromArgb(A(255), outline.R, outline.G, outline.B), Color.FromArgb(A(255), 176, 72, 255), 90f))
                            g.FillRectangle(b, bar);
                    }
                }
                if (c.TextStyle == "neon")
                {
                    float[] widths = { 0.30f, 0.18f, 0.09f };
                    int[] alphas = { 35, 60, 110 };
                    for (int i = 0; i < widths.Length; i++)
                        using (var pen = new Pen(Color.FromArgb(A(alphas[i]), outline.R, outline.G, outline.B), em * widths[i]) { LineJoin = LineJoin.Round })
                            g.DrawPath(pen, path);
                }
                if (c.Shadow)
                {
                    using (var m = new Matrix())
                    using (var sp = (GraphicsPath)path.Clone())
                    {
                        m.Translate(em * 0.05f, em * 0.06f);
                        sp.Transform(m);
                        using (var b = new SolidBrush(Color.FromArgb(A(150), 0, 0, 0))) g.FillPath(b, sp);
                    }
                }
                if (c.OutlineWidth > 0.001 && c.TextStyle != "lower")
                    using (var pen = new Pen(Color.FromArgb(A(255), outline.R, outline.G, outline.B), (float)(c.OutlineWidth * em * 2)) { LineJoin = LineJoin.Round })
                        g.DrawPath(pen, path);
                using (var b = new SolidBrush(Color.FromArgb(A(textColor.A), textColor.R, textColor.G, textColor.B))) g.FillPath(b, path);
                g.Restore(st);
            }
        }
    }

    /// <summary>
    /// Text clips as pictures. Drawing text means building font outlines, which is far slower than copying
    /// pixels, so each text is drawn once and kept until its words or style change. A text that stands still
    /// is drawn exactly where it sits (as crisp as drawing it directly); a text that moves, turns or grows is
    /// drawn once at its largest size and then placed like any other picture.
    /// </summary>
    internal static class TextRaster
    {
        sealed class Entry { public Frame Frame; public int X, Y; public RectangleF Local; public float K; }

        static readonly object gate = new object();
        static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();
        static readonly LinkedList<string> lru = new LinkedList<string>();
        static long bytes;
        const long MaxBytes = 96L * 1024 * 1024;

        static string StyleKey(Clip c, float canvasH)
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Join("\u0001", new[]
            {
                c.Text ?? "", c.Font ?? "", c.FontSize.ToString("R", inv), c.TextColor.ToString(inv), c.Bold ? "b" : "", c.Italic ? "i" : "",
                c.Align.ToString(inv), c.OutlineColor.ToString(inv), c.OutlineWidth.ToString("R", inv), c.Shadow ? "s" : "",
                c.BoxColor.ToString(inv), c.BoxOpacity.ToString("R", inv), c.TextStyle ?? "", canvasH.ToString("R", inv),
            });
        }

        static Entry Find(string key)
        {
            lock (gate)
            {
                Entry e;
                if (!cache.TryGetValue(key, out e)) return null;
                lru.Remove(key); lru.AddFirst(key);
                return e;
            }
        }

        static void Keep(string key, Entry e)
        {
            lock (gate)
            {
                Entry old;
                if (cache.TryGetValue(key, out old)) { bytes -= old.Frame.Bytes; lru.Remove(key); }
                cache[key] = e;
                bytes += e.Frame.Bytes;
                lru.AddFirst(key);
                while (lru.Count > 1 && bytes > MaxBytes)
                {
                    string last = lru.Last.Value;
                    lru.RemoveLast();
                    if (cache.TryGetValue(last, out old)) { cache.Remove(last); bytes -= old.Frame.Bytes; }
                }
            }
        }

        /// <summary>The space the text's drawing can reach around its box: outline, glow, shadow and background box.</summary>
        static RectangleF Reach(RectangleF box, float em) { return RectangleF.Inflate(box, em * 0.8f, em * 0.5f); }

        /// <summary>
        /// Draws a text clip. m places text space (centred on 0,0, in canvas pixels) on the canvas. moving = the
        /// placement changes from frame to frame; maxScale = the largest scale the text reaches while it does.
        /// </summary>
        public static void Draw(Frame canvas, Clip c, Matrix m, double opacity, bool moving, double maxScale, Layer like)
        {
            float canvasH = canvas.Height;
            var e = m.Elements;
            string style = StyleKey(c, canvasH);
            Entry entry;
            if (!moving)
            {
                string key = "s\u0001" + style + "\u0001" + canvas.Width + "\u0001" + string.Join(",", Array.ConvertAll(e, v => Math.Round(v, 3).ToString("R", CultureInfo.InvariantCulture)));
                entry = Find(key);
                if (entry == null)
                {
                    float em; RectangleF box;
                    using (TextLayout.Build(c, canvasH, out box, out em)) { }
                    var r = Reach(box, em);
                    var pts = new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) };
                    m.TransformPoints(pts);
                    float l = Math.Min(Math.Min(pts[0].X, pts[1].X), Math.Min(pts[2].X, pts[3].X)), t = Math.Min(Math.Min(pts[0].Y, pts[1].Y), Math.Min(pts[2].Y, pts[3].Y));
                    float rr = Math.Max(Math.Max(pts[0].X, pts[1].X), Math.Max(pts[2].X, pts[3].X)), b = Math.Max(Math.Max(pts[0].Y, pts[1].Y), Math.Max(pts[2].Y, pts[3].Y));
                    int x0 = Math.Max(0, (int)Math.Floor(l) - 1), y0 = Math.Max(0, (int)Math.Floor(t) - 1);
                    int x1 = Math.Min(canvas.Width, (int)Math.Ceiling(rr) + 1), y1 = Math.Min(canvas.Height, (int)Math.Ceiling(b) + 1);
                    if (x1 <= x0 || y1 <= y0) return;
                    using (var bmp = new Bitmap(x1 - x0, y1 - y0, PixelFormat.Format32bppPArgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.Transparent);
                            g.TranslateTransform(-x0, -y0);
                            TextLayout.Draw(g, c, canvasH, m, 1);
                        }
                        entry = new Entry { Frame = Frame.FromBitmap(bmp, true), X = x0, Y = y0 };
                    }
                    Keep(key, entry);
                }
                Raster.Draw(canvas, new Layer
                {
                    Src = entry.Frame, Tx = entry.X, Ty = entry.Y, Opacity = opacity,
                    Mode = like.Mode, HasClip = like.HasClip, Clip = like.Clip, Alpha = like.Alpha,
                });
                return;
            }

            float k = (float)Math.Max(0.25, Math.Min(4, maxScale));
            string dk = "m\u0001" + style + "\u0001" + k.ToString("R", CultureInfo.InvariantCulture);
            entry = Find(dk);
            if (entry == null)
            {
                float em; RectangleF box;
                using (TextLayout.Build(c, canvasH, out box, out em)) { }
                var r = Reach(box, em);
                // keep very large text within a sensible picture size
                float limit = 4096f / Math.Max(1f, Math.Max(r.Width, r.Height));
                if (k > limit) k = Math.Max(0.1f, limit);
                using (var bmp = new Bitmap(Math.Max(2, (int)Math.Ceiling(r.Width * k) + 2), Math.Max(2, (int)Math.Ceiling(r.Height * k) + 2), PixelFormat.Format32bppPArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    using (var local = new Matrix())
                    {
                        g.Clear(Color.Transparent);
                        local.Scale(k, k);
                        local.Translate(-r.Left, -r.Top);
                        TextLayout.Draw(g, c, canvasH, local, 1);
                    }
                    entry = new Entry { Frame = Frame.FromBitmap(bmp, true), Local = r, K = k };
                }
                Keep(dk, entry);
            }
            // picture pixel (u, v) is text-space point (u / K + Local.Left, v / K + Local.Top), which m places on the canvas
            double kk = entry.K, lx = entry.Local.Left, ly = entry.Local.Top;
            Raster.Draw(canvas, new Layer
            {
                Src = entry.Frame,
                A = e[0] / kk, B = e[1] / kk, C = e[2] / kk, D = e[3] / kk,
                Tx = e[0] * lx + e[2] * ly + e[4], Ty = e[1] * lx + e[3] * ly + e[5],
                Opacity = opacity, Mode = like.Mode, HasClip = like.HasClip, Clip = like.Clip, Alpha = like.Alpha, Sharp = like.Sharp,
            });
        }
    }

    // =========================================================================== compositor

    /// <summary>Draws the project at one moment. Used for the preview, playback and export, so they always match.</summary>
    internal sealed class Compositor
    {
        readonly Project project;
        readonly Func<int, MediaItem> media;
        public bool HighQuality;
        readonly Dictionary<int, Frame> work = new Dictionary<int, Frame>();
        bool cleared;

        public Compositor(Project project, Func<int, MediaItem> media)
        {
            this.project = project;
            this.media = media;
        }

        public Project Project { get { return project; } }

        /// <summary>The size a clip's picture fills at scale 1 (fit inside the canvas, keeping its shape).</summary>
        public static SizeF BaseSize(Clip c, MediaItem m, float cw, float ch)
        {
            var px = m != null ? m.PixelSize : new Size(16, 9);
            double w = Math.Max(1, px.Width * Math.Max(0.02, 1 - c.CropL - c.CropR));
            double h = Math.Max(1, px.Height * Math.Max(0.02, 1 - c.CropT - c.CropB));
            double f = Math.Min(cw / w, ch / h);
            return new SizeF((float)(w * f), (float)(h * f));
        }

        /// <summary>Decode size: enough pixels for the largest scale used, never more than the source has.</summary>
        public static Size DecodeSize(Clip c, MediaItem m, float cw, float ch)
        {
            var bs = BaseSize(c, m, cw, ch);
            var px = m.PixelSize;
            double srcW = Math.Max(2, px.Width * Math.Max(0.02, 1 - c.CropL - c.CropR));
            double k = Math.Min(c.MaxScale, srcW / Math.Max(1, bs.Width));
            k = Math.Max(k, 0.05);
            int w = Math.Max(2, (int)Math.Round(bs.Width * k));
            int h = Math.Max(2, (int)Math.Round(bs.Height * k));
            // limit the cost of very large overlays
            double area = (double)w * h, limit = Math.Max(cw * ch * 2.25, 640 * 360);
            if (area > limit) { double s = Math.Sqrt(limit / area); w = Math.Max(2, (int)(w * s)); h = Math.Max(2, (int)(h * s)); }
            return new Size(w & ~1, h & ~1);
        }

        public static Matrix PlacementMatrix(Placement p, float cw, float ch, PointF offset, float extraScale)
        {
            var m = new Matrix();
            m.Translate((float)(cw / 2 + p.X * cw) + offset.X, (float)(ch / 2 + p.Y * ch) + offset.Y);
            m.Rotate((float)p.Rotation);
            float s = (float)Math.Max(0.001, p.Scale * extraScale);
            m.Scale(s, s);
            return m;
        }

        /// <summary>Outline of a clip on the canvas (four corners), or null for sound clips.</summary>
        public PointF[] Outline(Clip c, double t, float cw, float ch)
        {
            if (!c.IsVisual) return null;
            var p = c.PlacementAt(t);
            RectangleF r;
            if (c.Kind == ClipKind.Text)
            {
                float em;
                using (TextLayout.Build(c, ch, out r, out em)) { }
                r.Inflate(em * 0.2f, em * 0.1f);
            }
            else
            {
                var bs = BaseSize(c, media(c.MediaId), cw, ch);
                r = new RectangleF(-bs.Width / 2, -bs.Height / 2, bs.Width, bs.Height);
            }
            var pts = new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) };
            using (var m = PlacementMatrix(p, cw, ch, PointF.Empty, 1f)) m.TransformPoints(pts);
            return pts;
        }

        /// <summary>Clips (and the transition partner) that draw at time t on a track.</summary>
        static void Active(Track tr, double t, out Clip a, out Clip b, out double progress, out string transition)
        {
            a = null; b = null; progress = 0; transition = null;
            // a transition window wins over the plain clip
            for (int i = 0; i < tr.Clips.Count; i++)
            {
                var c = tr.Clips[i];
                if (string.IsNullOrEmpty(c.Transition) || !c.IsVisual) continue;
                double d = Math.Max(0.1, Math.Min(c.TransitionDuration, c.Duration));
                Clip prev = null;
                for (int j = 0; j < tr.Clips.Count; j++)
                {
                    var o = tr.Clips[j];
                    if (!ReferenceEquals(o, c) && Math.Abs(o.End - c.Start) < 0.02 && o.IsVisual) { prev = o; break; }
                }
                double ws, we;
                if (prev != null)
                {
                    d = Math.Min(d, prev.Duration * 2);
                    ws = c.Start - d / 2; we = c.Start + d / 2;
                }
                else { ws = c.Start; we = c.Start + d; }
                if (t >= ws && t < we)
                {
                    a = prev; b = c;
                    progress = (t - ws) / Math.Max(1e-6, we - ws);
                    transition = c.Transition;
                    return;
                }
            }
            a = tr.ClipAt(t);
        }

        /// <summary>Which clips need pictures at time t (for decoders to start ahead of time).</summary>
        public IEnumerable<KeyValuePair<Clip, double>> Upcoming(double t, double window)
        {
            foreach (var tr in project.Tracks)
            {
                if (tr.Kind != TrackKind.Video || tr.Hidden) continue;
                foreach (var c in tr.Clips)
                {
                    if (c.Kind != ClipKind.Video) continue;
                    double start = c.Start;
                    if (!string.IsNullOrEmpty(c.Transition)) start -= c.TransitionDuration / 2;
                    if (start > t && start <= t + window) yield return new KeyValuePair<Clip, double>(c, start);
                }
            }
        }

        /// <summary>Draws the whole picture at time t onto canvas, whose size sets the scale (it need not be the project's).</summary>
        public void Render(Frame canvas, double t, IFrameSource src)
        {
            float cw = canvas.Width, ch = canvas.Height;
            cleared = false;
            canvas.Opaque = canvas.Premultiplied = true;
            src.BeginFrame();
            try
            {
                foreach (var tr in project.Tracks)
                {
                    if (tr.Kind != TrackKind.Video || tr.Hidden) continue;
                    Clip a, b; double p; string tx;
                    Active(tr, t, out a, out b, out p, out tx);
                    if (b == null)
                    {
                        if (a != null) DrawClip(canvas, a, t, src, 1, PointF.Empty, 1f, null);
                        continue;
                    }
                    DrawTransition(canvas, a, b, t, p, tx, src);
                }
                EnsureCleared(canvas);
                foreach (var kv in Upcoming(t, 1.0))
                {
                    var m = media(kv.Key.MediaId);
                    if (m != null && m.IsUsable) src.Prepare(kv.Key, m, kv.Value, DecodeSize(kv.Key, m, cw, ch));
                }
            }
            finally { src.EndFrame(); }
        }

        /// <summary>The background is only painted when something needs it: a clip that fills the canvas covers every pixel anyway.</summary>
        void EnsureCleared(Frame canvas)
        {
            if (cleared) return;
            Raster.Clear(canvas, Color.FromArgb(project.Background));
            cleared = true;
        }

        static void Tint(Frame canvas, Color col, double k)
        {
            if (k <= 0.002) return;
            var one = new Frame(1, 1);
            one.Data[0] = col.B; one.Data[1] = col.G; one.Data[2] = col.R; one.Data[3] = 255;
            one.Opaque = one.Premultiplied = true;
            Raster.Draw(canvas, new Layer { Src = one, A = canvas.Width, D = canvas.Height, Opacity = Math.Min(1, k) });
        }

        void DrawTransition(Frame canvas, Clip a, Clip b, double t, double p, string kind, IFrameSource src)
        {
            float cw = canvas.Width, ch = canvas.Height;
            p = Math.Max(0, Math.Min(1, p));
            double e = p * p * (3 - 2 * p);
            switch (kind)
            {
                case "black":
                case "white":
                    {
                        var col = kind == "black" ? Color.Black : Color.White;
                        double k;
                        if (a != null)
                        {
                            if (p < 0.5) DrawClip(canvas, a, t, src, 1, PointF.Empty, 1f, null);
                            else DrawClip(canvas, b, t, src, 1, PointF.Empty, 1f, null);
                            k = p < 0.5 ? p * 2 : (1 - p) * 2;
                        }
                        else
                        {
                            DrawClip(canvas, b, t, src, 1, PointF.Empty, 1f, null);
                            k = 1 - p;
                        }
                        EnsureCleared(canvas);
                        Tint(canvas, col, k);
                        break;
                    }
                case "wipeleft":
                case "wiperight":
                case "wipeup":
                case "wipedown":
                    {
                        if (a != null) DrawClip(canvas, a, t, src, 1, PointF.Empty, 1f, null);
                        Rectangle r;
                        if (kind == "wipeleft") r = Rectangle.FromLTRB((int)Math.Round(cw * (1 - e)), 0, (int)cw, (int)ch);
                        else if (kind == "wiperight") r = Rectangle.FromLTRB(0, 0, (int)Math.Round(cw * e), (int)ch);
                        else if (kind == "wipeup") r = Rectangle.FromLTRB(0, (int)Math.Round(ch * (1 - e)), (int)cw, (int)ch);
                        else r = Rectangle.FromLTRB(0, 0, (int)cw, (int)Math.Round(ch * e));
                        DrawClip(canvas, b, t, src, 1, PointF.Empty, 1f, new Layer { HasClip = true, Clip = r });
                        break;
                    }
                case "slideleft":
                case "slideright":
                case "slideup":
                case "slidedown":
                    {
                        float dx = 0, dy = 0;
                        if (kind == "slideleft") dx = -cw;
                        else if (kind == "slideright") dx = cw;
                        else if (kind == "slideup") dy = -ch;
                        else dy = ch;
                        if (a != null) DrawClip(canvas, a, t, src, 1, new PointF((float)(dx * e), (float)(dy * e)), 1f, null);
                        DrawClip(canvas, b, t, src, a == null ? e : 1, new PointF((float)(-dx * (1 - e)), (float)(-dy * (1 - e))), 1f, null);
                        break;
                    }
                case "circle":
                    {
                        if (a != null) DrawClip(canvas, a, t, src, 1, PointF.Empty, 1f, null);
                        double rad = Math.Sqrt(cw * cw + ch * ch) / 2 * e, mx = cw / 2.0, my = ch / 2.0;
                        DrawClip(canvas, b, t, src, 1, PointF.Empty, 1f, new Layer { Alpha = Shapes.Circle(mx, my, rad) });
                        break;
                    }
                case "zoom":
                    {
                        if (a != null) DrawClip(canvas, a, t, src, 1 - e * 0.6, PointF.Empty, (float)(1 + 0.35 * e), null);
                        DrawClip(canvas, b, t, src, e, PointF.Empty, (float)(0.75 + 0.25 * e), null);
                        break;
                    }
                default: // dissolve
                    {
                        if (a != null) DrawClip(canvas, a, t, src, 1, PointF.Empty, 1f, null);
                        DrawClip(canvas, b, t, src, e, PointF.Empty, 1f, null);
                        break;
                    }
            }
        }

        /// <summary>
        /// Draws one clip. extraOpacity, offset and extraScale come from a transition; 'like' carries a transition's
        /// clip rectangle or shape (only its HasClip, Clip and Alpha are read).
        /// </summary>
        public void DrawClip(Frame canvas, Clip c, double t, IFrameSource src, double extraOpacity, PointF offset, float extraScale, Layer like)
        {
            if (!c.IsVisual) return;
            float cw = canvas.Width, ch = canvas.Height;
            var p = c.PlacementAt(t);
            double opacity = Math.Max(0, Math.Min(1, p.Opacity)) * c.FadeAt(t) * extraOpacity;
            if (opacity <= 0.002) return;
            var layer = new Layer { Opacity = opacity, Sharp = HighQuality, Mode = Looks.BlendMode(c.Blend) };
            if (like != null) { layer.HasClip = like.HasClip; layer.Clip = like.Clip; layer.Alpha = like.Alpha; }
            using (var m = PlacementMatrix(p, cw, ch, offset, extraScale))
            {
                if (c.Kind == ClipKind.Text)
                {
                    EnsureCleared(canvas);
                    bool moving = c.HasKeys || extraScale != 1f || offset != PointF.Empty;
                    TextRaster.Draw(canvas, c, m, opacity, moving, c.MaxScale * Math.Max(1f, extraScale), layer);
                    return;
                }
                var mi = media(c.MediaId);
                if (mi == null || !mi.IsUsable) return;
                var size = DecodeSize(c, mi, cw, ch);
                Frame pic;
                try { pic = src.Get(c, mi, t, size); }
                catch { pic = null; }
                if (pic == null) return;
                if (Pixels.NeedsPass(c))
                {
                    Frame wb;
                    if (!work.TryGetValue(c.Id, out wb) || wb.Width != pic.Width || wb.Height != pic.Height)
                    {
                        wb = new Frame(pic.Width, pic.Height);
                        work[c.Id] = wb;
                    }
                    Pixels.ApplyEffects(pic, wb, c, (int)(t * project.Fps));
                    pic = wb;
                }
                else pic = pic.AsPremultiplied();
                var bs = BaseSize(c, mi, cw, ch);
                // picture pixel (u, v) -> the clip's box centred on (0,0) -> rotated, scaled and moved by m
                var e = m.Elements;
                double kx = bs.Width / pic.Width, ky = bs.Height / pic.Height;
                layer.Src = pic;
                layer.A = e[0] * kx; layer.B = e[1] * kx; layer.C = e[2] * ky; layer.D = e[3] * ky;
                layer.Tx = e[4] - (e[0] * bs.Width + e[2] * bs.Height) / 2.0;
                layer.Ty = e[5] - (e[1] * bs.Width + e[3] * bs.Height) / 2.0;
                Snap(layer);
                if (!cleared)
                {
                    if (Covers(layer, canvas)) cleared = true;
                    else EnsureCleared(canvas);
                }
                Raster.Draw(canvas, layer);
            }
        }

        /// <summary>
        /// A picture that lands within a hundredth of a pixel of the pixel grid at its own size is put exactly on it,
        /// so it is copied pixel for pixel instead of resampled (float rounding would otherwise blur a full-frame clip).
        /// </summary>
        static void Snap(Layer l)
        {
            if (Math.Abs(l.B) > 1e-6 || Math.Abs(l.C) > 1e-6) return;
            if (Math.Abs(l.A * l.Src.Width - l.Src.Width) > 0.01 || Math.Abs(l.D * l.Src.Height - l.Src.Height) > 0.01) return;
            if (Math.Abs(l.Tx - Math.Round(l.Tx)) > 0.01 || Math.Abs(l.Ty - Math.Round(l.Ty)) > 0.01) return;
            l.A = 1; l.D = 1; l.B = 0; l.C = 0; l.Tx = Math.Round(l.Tx); l.Ty = Math.Round(l.Ty);
        }

        static bool Covers(Layer l, Frame canvas)
        {
            return l.Src.Opaque && l.Opacity >= 0.999 && l.Mode == Blend.Normal && l.Alpha == null && l.Mask == null && !l.HasClip &&
                   l.A == 1 && l.D == 1 && l.B == 0 && l.C == 0 && l.Tx <= 0 && l.Ty <= 0 &&
                   l.Tx + l.Src.Width >= canvas.Width && l.Ty + l.Src.Height >= canvas.Height;
        }

        /// <summary>Kept for callers that held per-compositor working pictures; they are plain memory now.</summary>
        public void ReleaseWork() { work.Clear(); }
    }

    /// <summary>Shapes used by transitions, as per-row alpha for the renderer.</summary>
    internal static unsafe class Shapes
    {
        /// <summary>A filled circle with a soft one-pixel edge.</summary>
        public static RowAlpha Circle(double cx, double cy, double radius)
        {
            return (y, x, n, a) =>
            {
                double dy = y + 0.5 - cy, dy2 = dy * dy;
                double inner = radius - 1, outer = radius + 1;
                for (int i = 0; i < n; i++)
                {
                    double dx = x + i + 0.5 - cx;
                    double d2 = dx * dx + dy2;
                    if (inner > 0 && d2 <= inner * inner) a[i] = 255;
                    else if (d2 >= outer * outer) a[i] = 0;
                    else
                    {
                        double v = radius - Math.Sqrt(d2) + 0.5;
                        a[i] = (byte)(v <= 0 ? 0 : v >= 1 ? 255 : (int)(v * 255 + 0.5));
                    }
                }
            };
        }
    }
}
