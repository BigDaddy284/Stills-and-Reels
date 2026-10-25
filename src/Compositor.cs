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

        /// <summary>Applies a colour matrix in place (32bpp ARGB bitmap data).</summary>
        public static void ApplyMatrix(BitmapData d, double[] m)
        {
            // fixed point: coefficients × 1024, offsets in 0..255 × 1024
            int r0 = (int)(m[0] * 1024), r1 = (int)(m[1] * 1024), r2 = (int)(m[2] * 1024), r3 = (int)(m[3] * 255 * 1024);
            int g0 = (int)(m[4] * 1024), g1 = (int)(m[5] * 1024), g2 = (int)(m[6] * 1024), g3 = (int)(m[7] * 255 * 1024);
            int b0 = (int)(m[8] * 1024), b1 = (int)(m[9] * 1024), b2 = (int)(m[10] * 1024), b3 = (int)(m[11] * 255 * 1024);
            int w = d.Width, h = d.Height;
            for (int y = 0; y < h; y++)
            {
                byte* p = (byte*)d.Scan0 + y * d.Stride;
                for (int x = 0; x < w; x++, p += 4)
                {
                    int b = p[0], g = p[1], r = p[2];
                    int nr = (r0 * r + r1 * g + r2 * b + r3) >> 10;
                    int ng = (g0 * r + g1 * g + g2 * b + g3) >> 10;
                    int nb = (b0 * r + b1 * g + b2 * b + b3) >> 10;
                    p[2] = (byte)(nr < 0 ? 0 : nr > 255 ? 255 : nr);
                    p[1] = (byte)(ng < 0 ? 0 : ng > 255 ? 255 : ng);
                    p[0] = (byte)(nb < 0 ? 0 : nb > 255 ? 255 : nb);
                }
            }
        }

        /// <summary>Makes pixels close to the key colour transparent (YCbCr distance) and removes colour spill.</summary>
        public static void ChromaKey(BitmapData d, Color key, double similarity, double smoothness)
        {
            double kcb = -0.168736 * key.R - 0.331264 * key.G + 0.5 * key.B;
            double kcr = 0.5 * key.R - 0.418688 * key.G - 0.081312 * key.B;
            double sim = 4 + similarity / 100.0 * 110;       // CbCr distance in 0..~180 space
            double smo = Math.Max(1, smoothness / 100.0 * 80);
            bool green = key.G >= key.R && key.G >= key.B;
            bool blue = key.B > key.R && key.B > key.G;
            int w = d.Width, h = d.Height;
            for (int y = 0; y < h; y++)
            {
                byte* p = (byte*)d.Scan0 + y * d.Stride;
                for (int x = 0; x < w; x++, p += 4)
                {
                    int b = p[0], g = p[1], r = p[2];
                    double cb = -0.168736 * r - 0.331264 * g + 0.5 * b;
                    double cr = 0.5 * r - 0.418688 * g - 0.081312 * b;
                    double dist = Math.Sqrt((cb - kcb) * (cb - kcb) + (cr - kcr) * (cr - kcr));
                    double a = (dist - sim) / smo;
                    if (a >= 1) continue;
                    if (a < 0) a = 0;
                    p[3] = (byte)(p[3] * a);
                    // spill suppression on the semi-transparent edge
                    if (green) { int lim = Math.Max(r, b); if (g > lim) p[1] = (byte)lim; }
                    else if (blue) { int lim = Math.Max(r, g); if (b > lim) p[0] = (byte)lim; }
                }
            }
        }

        public static void MultiplyAlpha(BitmapData d, double k)
        {
            int f = (int)(Math.Max(0, Math.Min(1, k)) * 256);
            int w = d.Width, h = d.Height;
            for (int y = 0; y < h; y++)
            {
                byte* p = (byte*)d.Scan0 + y * d.Stride + 3;
                for (int x = 0; x < w; x++, p += 4) *p = (byte)((*p * f) >> 8);
            }
        }

        /// <summary>Box blur, run three times (close to a Gaussian), all four channels.</summary>
        public static void Blur(BitmapData d, int radius)
        {
            if (radius < 1) return;
            int w = d.Width, h = d.Height;
            var tmp = new int[Math.Max(w, h) * 4];
            for (int pass = 0; pass < 3; pass++)
            {
                for (int y = 0; y < h; y++) BoxLine((byte*)d.Scan0 + y * d.Stride, 4, w, radius, tmp);
                for (int x = 0; x < w; x++) BoxLine((byte*)d.Scan0 + x * 4, d.Stride, h, radius, tmp);
            }
        }

        static void BoxLine(byte* start, int step, int n, int r, int[] tmp)
        {
            int win = r * 2 + 1;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int i = -r; i <= r; i++) sum += start[Clamp(i, n) * step + c];
                for (int i = 0; i < n; i++)
                {
                    tmp[i * 4 + c] = sum / win;
                    sum += start[Clamp(i + r + 1, n) * step + c] - start[Clamp(i - r, n) * step + c];
                }
            }
            for (int i = 0; i < n; i++)
            {
                byte* p = start + i * step;
                p[0] = (byte)tmp[i * 4]; p[1] = (byte)tmp[i * 4 + 1]; p[2] = (byte)tmp[i * 4 + 2]; p[3] = (byte)tmp[i * 4 + 3];
            }
        }

        static int Clamp(int i, int n) { return i < 0 ? 0 : i >= n ? n - 1 : i; }

        public static void Sharpen(BitmapData d, double amount)
        {
            int w = d.Width, h = d.Height;
            int k = (int)(amount / 100.0 * 2.0 * 256);
            if (k <= 0 || w < 3 || h < 3) return;
            var copy = new byte[d.Stride * h];
            Marshal.Copy(d.Scan0, copy, 0, copy.Length);
            int s = d.Stride;
            fixed (byte* src = copy)
            {
                for (int y = 1; y < h - 1; y++)
                {
                    byte* o = (byte*)d.Scan0 + y * s + 4;
                    byte* c = src + y * s + 4;
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
        }

        public static void Vignette(BitmapData d, double amount)
        {
            int w = d.Width, h = d.Height;
            double cx = w / 2.0, cy = h / 2.0;
            double strength = amount / 100.0 * 0.85;
            double inv = 1.0 / Math.Sqrt(cx * cx + cy * cy);
            var row = new int[w];
            for (int y = 0; y < h; y++)
            {
                double dy = (y - cy);
                for (int x = 0; x < w; x++)
                {
                    double dx = x - cx;
                    double r = Math.Sqrt(dx * dx + dy * dy) * inv; // 0 centre .. 1 corner
                    double f = 1 - strength * Math.Pow(Math.Max(0, (r - 0.35) / 0.65), 1.6);
                    row[x] = (int)(Math.Max(0, f) * 256);
                }
                byte* p = (byte*)d.Scan0 + y * d.Stride;
                for (int x = 0; x < w; x++, p += 4)
                {
                    int f = row[x];
                    p[0] = (byte)((p[0] * f) >> 8); p[1] = (byte)((p[1] * f) >> 8); p[2] = (byte)((p[2] * f) >> 8);
                }
            }
        }

        public static void Grain(BitmapData d, double amount, int seed)
        {
            int w = d.Width, h = d.Height;
            int amp = (int)(amount / 100.0 * 48);
            if (amp <= 0) return;
            uint s = (uint)(seed * 2654435761u + 12345u) | 1u;
            for (int y = 0; y < h; y++)
            {
                byte* p = (byte*)d.Scan0 + y * d.Stride;
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
        }

        /// <summary>True when the clip needs a per-pixel pass.</summary>
        public static bool NeedsPass(Clip c, double opacity)
        {
            return c.HasColorWork || c.ChromaKey || c.Blur > 0.5 || c.Sharpen > 0.5 || c.Vignette > 0.5 || c.Grain > 0.5 || opacity < 0.999;
        }

        /// <summary>Runs all of the clip's pixel effects on bitmap b (in place).</summary>
        public static void ApplyEffects(Bitmap b, Clip c, double opacity, int frameIndex)
        {
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                if (c.HasColorWork) ApplyMatrix(d, ClipMatrix(c));
                if (c.ChromaKey) ChromaKey(d, Color.FromArgb(c.KeyColor), c.KeySimilarity, c.KeySmoothness);
                if (c.Blur > 0.5) Blur(d, Math.Max(1, (int)Math.Round(c.Blur / 100.0 * Math.Max(b.Width, b.Height) / 45.0)));
                if (c.Sharpen > 0.5) Sharpen(d, c.Sharpen);
                if (c.Vignette > 0.5) Vignette(d, c.Vignette);
                if (c.Grain > 0.5) Grain(d, c.Grain, frameIndex);
                if (opacity < 0.999) MultiplyAlpha(d, opacity);
            }
            finally { b.UnlockBits(d); }
        }
    }

    // =========================================================================== frame sources

    /// <summary>Supplies source pictures for clips. Implemented for paused preview (single grabs) and playback/export (streams).</summary>
    internal interface IFrameSource : IDisposable
    {
        /// <summary>The clip's source picture at timeline time t, decoded at the given size. Owned by the source.</summary>
        Bitmap Get(Clip c, MediaItem m, double t, Size size);
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
        public static Bitmap ImageFrame(MediaItem m, Clip c, Size size)
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

    /// <summary>Single-frame grabs with a small cache (paused preview and scrubbing).</summary>
    internal sealed class StillFrames : IFrameSource
    {
        readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        readonly LinkedList<string> lru = new LinkedList<string>();
        const int MaxEntries = 28;
        const long MaxBytes = 140L * 1024 * 1024;
        long bytes;
        readonly object procLock = new object();
        Process current;
        volatile int cancelGen;

        public Func<bool> IsCancelled = () => false;

        public void Cancel()
        {
            cancelGen++;
            lock (procLock) { FFmpeg.KillQuietly(current); current = null; }
        }

        static string Key(Clip c, MediaItem m, double src, Size size)
        {
            return m.Id + "|" + Math.Round(src * 1000) + "|" + size.Width + "x" + size.Height + "|" + SourceFilters.Pre(c, false) + "|" + m.SourcePath;
        }

        public Bitmap Get(Clip c, MediaItem m, double t, Size size)
        {
            if (m.Kind == MediaKind.Image)
            {
                string ik = "img" + m.Id + "|" + size.Width + "x" + size.Height + "|" + c.CropL + c.CropT + c.CropR + c.CropB + c.FlipH + c.FlipV;
                Bitmap hit;
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
            string key = Key(c, m, src, size);
            Bitmap cached;
            if (cache.TryGetValue(key, out cached)) { Touch(key); return cached; }
            int gen = cancelGen;
            Bitmap frame = null;
            double[] tries = { src, src - fd * 2, src - 0.3, src - 1.0 };
            foreach (var tt in tries)
            {
                if (tt < 0 && tt != src) continue;
                if (gen != cancelGen || IsCancelled()) return null;
                try
                {
                    frame = FrameGrabber.Grab(m.SourcePath, Math.Max(0, tt), size, SourceFilters.Pre(c, false), p => { lock (procLock) current = p; }, m.Info);
                }
                catch { frame = null; }
                lock (procLock) current = null;
                if (frame != null || gen != cancelGen) break;
            }
            if (frame == null || gen != cancelGen) { if (frame != null) frame.Dispose(); return null; }
            Put(key, frame);
            return frame;
        }

        void Touch(string k) { lru.Remove(k); lru.AddFirst(k); }

        void Put(string k, Bitmap b)
        {
            Bitmap old;
            if (cache.TryGetValue(k, out old)) { bytes -= (long)old.Width * old.Height * 4; old.Dispose(); lru.Remove(k); }
            cache[k] = b;
            bytes += (long)b.Width * b.Height * 4;
            lru.AddFirst(k);
            while (lru.Count > 1 && (lru.Count > MaxEntries || bytes > MaxBytes))
            {
                var last = lru.Last.Value;
                lru.RemoveLast();
                Bitmap ob;
                if (cache.TryGetValue(last, out ob)) { cache.Remove(last); bytes -= (long)ob.Width * ob.Height * 4; ob.Dispose(); }
            }
        }

        public void BeginFrame() { }
        public void EndFrame() { }
        public void Prepare(Clip c, MediaItem m, double t, Size size) { }

        public void Dispose()
        {
            Cancel();
            foreach (var b in cache.Values) b.Dispose();
            cache.Clear();
            lru.Clear();
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
        int shownIndex = -2;
        public readonly Bitmap Bitmap;
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
            Bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
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
        public Bitmap GetFrame(double t, int waitMs)
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
                if (shownIndex != currentIndex)
                {
                    Pixels.CopyInto(currentBuf, Bitmap);
                    shownIndex = currentIndex;
                }
            }
            return Bitmap;
        }

        public bool Ended { get { lock (sync) return eof && queue.Count == 0; } }
        public int CurrentIndex { get { lock (sync) return Math.Max(0, currentIndex); } }

        public void Dispose()
        {
            lock (sync) { disposed = true; Monitor.PulseAll(sync); }
            FFmpeg.KillQuietly(proc);
            Bitmap.Dispose();
        }
    }

    /// <summary>Streaming decoders for continuous rendering (playback and export).</summary>
    internal sealed class StreamFrames : IFrameSource
    {
        readonly double fps;
        readonly bool export;
        readonly Dictionary<int, ClipStream> streams = new Dictionary<int, ClipStream>();
        readonly Dictionary<string, Bitmap> images = new Dictionary<string, Bitmap>();
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

        public Bitmap Get(Clip c, MediaItem m, double t, Size size)
        {
            if (m.Kind == MediaKind.Image)
            {
                string ik = m.Id + "|" + size.Width + "x" + size.Height + "|" + c.CropL + c.CropT + c.CropR + c.CropB + c.FlipH + c.FlipV;
                usedImages.Add(ik);
                Bitmap b;
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
                foreach (var k in gone) { images[k].Dispose(); images.Remove(k); }
            }
        }

        public void Dispose()
        {
            foreach (var s in streams.Values) s.Dispose();
            streams.Clear();
            foreach (var b in images.Values) b.Dispose();
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

    // =========================================================================== compositor

    /// <summary>Draws the project at one moment. Used for the preview, playback and export, so they always match.</summary>
    internal sealed class Compositor
    {
        readonly Project project;
        readonly Func<int, MediaItem> media;
        public bool HighQuality;
        readonly Dictionary<int, Bitmap> work = new Dictionary<int, Bitmap>();

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

        public void Render(Graphics g, float cw, float ch, double t, IFrameSource src)
        {
            g.Clear(Color.FromArgb(project.Background));
            g.InterpolationMode = HighQuality ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingQuality = HighQuality ? CompositingQuality.HighQuality : CompositingQuality.HighSpeed;
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
                        if (a != null) DrawClip(g, a, t, cw, ch, src, 1, PointF.Empty, 1f);
                        continue;
                    }
                    DrawTransition(g, a, b, t, p, tx, cw, ch, src);
                }
                foreach (var kv in Upcoming(t, 1.0))
                {
                    var m = media(kv.Key.MediaId);
                    if (m != null && m.IsUsable) src.Prepare(kv.Key, m, kv.Value, DecodeSize(kv.Key, m, cw, ch));
                }
            }
            finally { src.EndFrame(); }
        }

        void DrawTransition(Graphics g, Clip a, Clip b, double t, double p, string kind, float cw, float ch, IFrameSource src)
        {
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
                            if (p < 0.5) DrawClip(g, a, t, cw, ch, src, 1, PointF.Empty, 1f);
                            else DrawClip(g, b, t, cw, ch, src, 1, PointF.Empty, 1f);
                            k = p < 0.5 ? p * 2 : (1 - p) * 2;
                        }
                        else
                        {
                            DrawClip(g, b, t, cw, ch, src, 1, PointF.Empty, 1f);
                            k = 1 - p;
                        }
                        using (var br = new SolidBrush(Color.FromArgb((int)(255 * Math.Max(0, Math.Min(1, k))), col))) g.FillRectangle(br, 0, 0, cw, ch);
                        break;
                    }
                case "wipeleft":
                case "wiperight":
                case "wipeup":
                case "wipedown":
                    {
                        if (a != null) DrawClip(g, a, t, cw, ch, src, 1, PointF.Empty, 1f);
                        RectangleF r;
                        if (kind == "wipeleft") r = new RectangleF((float)(cw * (1 - e)), 0, (float)(cw * e) + 1, ch);
                        else if (kind == "wiperight") r = new RectangleF(0, 0, (float)(cw * e), ch);
                        else if (kind == "wipeup") r = new RectangleF(0, (float)(ch * (1 - e)), cw, (float)(ch * e) + 1);
                        else r = new RectangleF(0, 0, cw, (float)(ch * e));
                        var st = g.Save();
                        g.SetClip(r);
                        DrawClip(g, b, t, cw, ch, src, 1, PointF.Empty, 1f);
                        g.Restore(st);
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
                        if (a != null) DrawClip(g, a, t, cw, ch, src, 1, new PointF((float)(dx * e), (float)(dy * e)), 1f);
                        DrawClip(g, b, t, cw, ch, src, a == null ? e : 1, new PointF((float)(-dx * (1 - e)), (float)(-dy * (1 - e))), 1f);
                        break;
                    }
                case "circle":
                    {
                        if (a != null) DrawClip(g, a, t, cw, ch, src, 1, PointF.Empty, 1f);
                        float rad = (float)(Math.Sqrt(cw * cw + ch * ch) / 2 * e);
                        using (var path = new GraphicsPath())
                        {
                            path.AddEllipse(cw / 2 - rad, ch / 2 - rad, rad * 2, rad * 2);
                            var st = g.Save();
                            g.SetClip(path);
                            DrawClip(g, b, t, cw, ch, src, 1, PointF.Empty, 1f);
                            g.Restore(st);
                        }
                        break;
                    }
                case "zoom":
                    {
                        if (a != null) DrawClip(g, a, t, cw, ch, src, 1 - e * 0.6, PointF.Empty, (float)(1 + 0.35 * e));
                        DrawClip(g, b, t, cw, ch, src, e, PointF.Empty, (float)(0.75 + 0.25 * e));
                        break;
                    }
                default: // dissolve
                    {
                        if (a != null) DrawClip(g, a, t, cw, ch, src, 1, PointF.Empty, 1f);
                        DrawClip(g, b, t, cw, ch, src, e, PointF.Empty, 1f);
                        break;
                    }
            }
        }

        public void DrawClip(Graphics g, Clip c, double t, float cw, float ch, IFrameSource src, double extraOpacity, PointF offset, float extraScale)
        {
            if (!c.IsVisual) return;
            var p = c.PlacementAt(t);
            double opacity = Math.Max(0, Math.Min(1, p.Opacity)) * c.FadeAt(t) * extraOpacity;
            if (opacity <= 0.002) return;
            using (var m = PlacementMatrix(p, cw, ch, offset, extraScale))
            {
                if (c.Kind == ClipKind.Text)
                {
                    TextLayout.Draw(g, c, ch, m, opacity);
                    return;
                }
                var mi = media(c.MediaId);
                if (mi == null || !mi.IsUsable) return;
                var size = DecodeSize(c, mi, cw, ch);
                Bitmap pic;
                try { pic = src.Get(c, mi, t, size); }
                catch { pic = null; }
                if (pic == null) return;
                Bitmap draw = pic;
                if (Pixels.NeedsPass(c, opacity))
                {
                    Bitmap wb;
                    if (!work.TryGetValue(c.Id, out wb) || wb.Width != pic.Width || wb.Height != pic.Height)
                    {
                        if (wb != null) wb.Dispose();
                        wb = new Bitmap(pic.Width, pic.Height, PixelFormat.Format32bppArgb);
                        work[c.Id] = wb;
                    }
                    Pixels.CopyBitmap(pic, wb);
                    Pixels.ApplyEffects(wb, c, opacity, (int)(t * project.Fps));
                    draw = wb;
                }
                var bs = BaseSize(c, mi, cw, ch);
                var st = g.Save();
                g.MultiplyTransform(m);
                // a frame that lands pixel for pixel on the canvas is copied exactly instead of resampled
                if (IsPixelExact(g.Transform, bs, draw)) g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.TranslateTransform(-bs.Width / 2, -bs.Height / 2);
                g.ScaleTransform(bs.Width / draw.Width, bs.Height / draw.Height);
                // mirrored edges stop the resampler from blending the picture's border with transparency
                edgeAttrs.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(draw, new Rectangle(0, 0, draw.Width, draw.Height), 0, 0, draw.Width, draw.Height, GraphicsUnit.Pixel, edgeAttrs);
                g.Restore(st);
            }
        }

        readonly ImageAttributes edgeAttrs = new ImageAttributes();

        static bool IsPixelExact(Matrix t, SizeF bs, Bitmap pic)
        {
            if (Math.Abs(bs.Width - pic.Width) > 0.01f || Math.Abs(bs.Height - pic.Height) > 0.01f) return false;
            using (t)
            {
                var e = t.Elements;
                if (Math.Abs(e[0] - 1) > 1e-4f || Math.Abs(e[3] - 1) > 1e-4f || Math.Abs(e[1]) > 1e-4f || Math.Abs(e[2]) > 1e-4f) return false;
                float x = e[4] - bs.Width / 2, y = e[5] - bs.Height / 2;
                return Math.Abs(x - Math.Round(x)) < 0.01 && Math.Abs(y - Math.Round(y)) < 0.01;
            }
        }

        public void ReleaseWork()
        {
            foreach (var b in work.Values) b.Dispose();
            work.Clear();
        }
    }
}
