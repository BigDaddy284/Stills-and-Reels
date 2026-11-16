using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace StillsAndReels
{
    /// <summary>
    /// A picture held as plain BGRA bytes (4 per pixel, rows top to bottom, no padding). Video frames arrive
    /// from FFmpeg in exactly this layout and leave for the encoder in it, so the video editor composites on
    /// these buffers directly instead of going through GDI+ bitmaps.
    /// </summary>
    internal sealed class Frame
    {
        public readonly int Width, Height;
        public readonly byte[] Data;
        /// <summary>Every pixel is fully opaque (alpha 255).</summary>
        public bool Opaque;
        /// <summary>Colour is already multiplied by alpha. Opaque frames count as both.</summary>
        public bool Premultiplied;
        Frame premultiplied;

        public Frame(int width, int height) : this(width, height, new byte[Math.Max(1, width) * Math.Max(1, height) * 4]) { }

        public Frame(int width, int height, byte[] data)
        {
            Width = Math.Max(1, width); Height = Math.Max(1, height);
            if (data == null || data.Length < Width * Height * 4) throw new ArgumentException("buffer too small for the frame");
            Data = data;
        }

        public int Stride { get { return Width * 4; } }
        public long Bytes { get { return (long)Width * Height * 4; } }

        /// <summary>Copies a bitmap's pixels. premultiplied chooses which form the copy holds.</summary>
        public static Frame FromBitmap(Bitmap b, bool premultiplied)
        {
            var f = new Frame(b.Width, b.Height);
            int w = f.Width, h = f.Height;
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, premultiplied ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb);
            try
            {
                if (d.Stride == w * 4) Marshal.Copy(d.Scan0, f.Data, 0, w * h * 4);
                else for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, f.Data, y * w * 4, w * 4);
            }
            finally { b.UnlockBits(d); }
            f.Premultiplied = premultiplied;
            f.Opaque = Raster.IsOpaque(f);
            if (f.Opaque) f.Premultiplied = true;
            return f;
        }

        public Bitmap ToBitmap()
        {
            var b = new Bitmap(Width, Height, Opaque || Premultiplied ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb);
            CopyTo(b);
            return b;
        }

        /// <summary>Copies the pixels into a bitmap of the same size.</summary>
        public void CopyTo(Bitmap b)
        {
            int w = Math.Min(Width, b.Width), h = Math.Min(Height, b.Height);
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, Opaque || Premultiplied ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb);
            try
            {
                if (d.Stride == Width * 4 && w == Width) Marshal.Copy(Data, 0, d.Scan0, w * h * 4);
                else for (int y = 0; y < h; y++) Marshal.Copy(Data, y * Width * 4, d.Scan0 + y * d.Stride, w * 4);
            }
            finally { b.UnlockBits(d); }
        }

        /// <summary>This frame with colour multiplied by alpha (itself when it already is). Kept for reuse.</summary>
        public Frame AsPremultiplied()
        {
            if (Premultiplied || Opaque) return this;
            var p = premultiplied;
            if (p == null)
            {
                p = new Frame(Width, Height);
                Buffer.BlockCopy(Data, 0, p.Data, 0, Width * Height * 4);
                Raster.Premultiply(p);
                premultiplied = p;
            }
            return p;
        }
    }

    internal enum Blend { Normal, Screen, Multiply, Overlay, Add, Darken, Lighten, SoftLight, HardLight, Difference }

    /// <summary>Fills count alpha values (0..255) for canvas row y starting at column x. Used for transition shapes.</summary>
    internal unsafe delegate void RowAlpha(int y, int x, int count, byte* alpha);

    /// <summary>A soft-edged shape in a clip's own space that limits where the clip shows.</summary>
    internal sealed class MaskShape
    {
        public int Kind;            // 1 rectangle, 2 ellipse, 3 split (a straight edge)
        public double X, Y;         // centre, as a fraction of the picture (0 = middle, ±0.5 = edges)
        public double W = 0.6, H = 0.6; // size as a fraction of the picture
        public double Rotation;     // degrees
        public double Feather;      // 0..1, as a fraction of the smaller side
        public double Round;        // rectangle corner rounding 0..1
        public bool Invert;
    }

    /// <summary>One picture to draw onto a canvas: where it goes and how it is mixed in.</summary>
    internal sealed class Layer
    {
        public Frame Src;
        /// <summary>Source pixel (u, v) lands at canvas x = A·u + C·v + Tx, y = B·u + D·v + Ty.</summary>
        public double A = 1, B, C, D = 1, Tx, Ty;
        public double Opacity = 1;
        public Blend Mode;
        public bool HasClip;
        public Rectangle Clip;      // canvas pixels
        public RowAlpha Alpha;      // optional canvas-space alpha
        public MaskShape Mask;      // optional clip-space mask
        public bool Sharp;          // high-quality resampling (export, paused preview)

        /// <summary>The same layer drawing a different (smaller) copy of its picture.</summary>
        public Layer CopyFor(Frame src) { var l = (Layer)MemberwiseClone(); l.Src = src; return l; }
    }

    /// <summary>The video editor's software renderer: placement, scaling, rotation and blending on raw BGRA, across all cores.</summary>
    internal static unsafe class Raster
    {
        static readonly int Cores = Math.Max(1, Environment.ProcessorCount);
        const int MinParallelPixels = 96 * 1024;

        /// <summary>Runs body(first row, end row) over [y0, y1), split across cores when the work is large enough.</summary>
        public static void Rows(int y0, int y1, long pixels, Action<int, int> body)
        {
            int n = y1 - y0;
            if (n <= 0) return;
            int bands = pixels < MinParallelPixels ? 1 : Math.Min(Cores, Math.Max(1, n / 16));
            if (bands <= 1) { body(y0, y1); return; }
            Parallel.For(0, bands, b =>
            {
                int a = y0 + (int)((long)n * b / bands), e = y0 + (int)((long)n * (b + 1) / bands);
                if (e > a) body(a, e);
            });
        }

        // ------------------------------------------------------------------ whole-frame helpers

        public static bool IsOpaque(Frame f)
        {
            int n = f.Width * f.Height;
            fixed (byte* p0 = f.Data)
            {
                byte* p = p0 + 3;
                for (int i = 0; i < n; i++, p += 4) if (*p != 255) return false;
            }
            return true;
        }

        public static void Premultiply(Frame f)
        {
            if (f.Premultiplied || f.Opaque) { f.Premultiplied = true; return; }
            int w = f.Width;
            var data = f.Data;
            Rows(0, f.Height, (long)w * f.Height, (ya, yb) =>
            {
                fixed (byte* p0 = data)
                {
                    byte* p = p0 + (long)ya * w * 4, end = p0 + (long)yb * w * 4;
                    for (; p < end; p += 4)
                    {
                        int a = p[3];
                        if (a == 255) continue;
                        if (a == 0) { p[0] = p[1] = p[2] = 0; continue; }
                        p[0] = (byte)Div255(p[0] * a); p[1] = (byte)Div255(p[1] * a); p[2] = (byte)Div255(p[2] * a);
                    }
                }
            });
            f.Premultiplied = true;
        }

        public static void Clear(Frame f, Color c)
        {
            uint v = (uint)(255 << 24 | c.R << 16 | c.G << 8 | c.B);
            int w = f.Width;
            var data = f.Data;
            Rows(0, f.Height, (long)w * f.Height, (ya, yb) =>
            {
                fixed (byte* p0 = data)
                {
                    uint* p = (uint*)(p0 + (long)ya * w * 4), end = (uint*)(p0 + (long)yb * w * 4);
                    for (; p < end; p++) *p = v;
                }
            });
            f.Opaque = true; f.Premultiplied = true;
        }

        public static void Copy(Frame src, Frame dst)
        {
            Buffer.BlockCopy(src.Data, 0, dst.Data, 0, Math.Min(src.Data.Length, dst.Data.Length));
            dst.Opaque = src.Opaque; dst.Premultiplied = src.Premultiplied;
        }

        /// <summary>Half-size copy (each pixel the average of a 2 × 2 block). Used before shrinking by more than half.</summary>
        public static void Halve(Frame src, Frame dst)
        {
            int sw = src.Width, dw = dst.Width, dh = dst.Height;
            var s = src.Data; var d = dst.Data;
            int sh = src.Height;
            Rows(0, dh, (long)dw * dh * 4, (ya, yb) =>
            {
                fixed (byte* s0 = s) fixed (byte* d0 = d)
                {
                    for (int y = ya; y < yb; y++)
                    {
                        byte* a = s0 + (long)Math.Min(sh - 1, y * 2) * sw * 4;
                        byte* b = s0 + (long)Math.Min(sh - 1, y * 2 + 1) * sw * 4;
                        byte* o = d0 + (long)y * dw * 4;
                        for (int x = 0; x < dw; x++, o += 4)
                        {
                            int x0 = Math.Min(sw - 1, x * 2) * 4, x1 = Math.Min(sw - 1, x * 2 + 1) * 4;
                            o[0] = (byte)((a[x0] + a[x1] + b[x0] + b[x1] + 2) >> 2);
                            o[1] = (byte)((a[x0 + 1] + a[x1 + 1] + b[x0 + 1] + b[x1 + 1] + 2) >> 2);
                            o[2] = (byte)((a[x0 + 2] + a[x1 + 2] + b[x0 + 2] + b[x1 + 2] + 2) >> 2);
                            o[3] = (byte)((a[x0 + 3] + a[x1 + 3] + b[x0 + 3] + b[x1 + 3] + 2) >> 2);
                        }
                    }
                }
            });
            dst.Opaque = src.Opaque; dst.Premultiplied = true;
        }

        /// <summary>Stretches src over a w × h BGRA buffer (two-by-two sampling, all four channels). For smooth pictures such as blurs.</summary>
        public static void Stretch(Frame src, byte* dst, int w, int h)
        {
            int sw = src.Width, sh = src.Height;
            var sd = src.Data;
            var xi = new int[w * 2]; var xf = new int[w];
            for (int i = 0; i < w; i++)
            {
                double u = (i + 0.5) * sw / w - 0.5;
                int iu = (int)Math.Floor(u);
                xf[i] = (int)((u - iu) * 256);
                xi[i * 2] = (iu < 0 ? 0 : iu >= sw ? sw - 1 : iu) * 4; xi[i * 2 + 1] = (iu + 1 < 0 ? 0 : iu + 1 >= sw ? sw - 1 : iu + 1) * 4;
            }
            Rows(0, h, (long)w * h * 2, (ya, yb) =>
            {
                fixed (byte* s0 = sd) fixed (int* pxi = xi) fixed (int* pxf = xf)
                {
                    for (int y = ya; y < yb; y++)
                    {
                        double v = (y + 0.5) * sh / h - 0.5;
                        int iv = (int)Math.Floor(v), fy = (int)((v - iv) * 256), ify = 256 - fy;
                        byte* a = s0 + (long)(iv < 0 ? 0 : iv >= sh ? sh - 1 : iv) * sw * 4, b = s0 + (long)(iv + 1 < 0 ? 0 : iv + 1 >= sh ? sh - 1 : iv + 1) * sw * 4;
                        byte* o = dst + (long)y * w * 4;
                        for (int i = 0; i < w; i++, o += 4)
                        {
                            int o0 = pxi[i * 2], o1 = pxi[i * 2 + 1], fx = pxf[i], ifx = 256 - fx;
                            o[0] = (byte)(((a[o0] * ifx + a[o1] * fx) * ify + (b[o0] * ifx + b[o1] * fx) * fy + 32768) >> 16);
                            o[1] = (byte)(((a[o0 + 1] * ifx + a[o1 + 1] * fx) * ify + (b[o0 + 1] * ifx + b[o1 + 1] * fx) * fy + 32768) >> 16);
                            o[2] = (byte)(((a[o0 + 2] * ifx + a[o1 + 2] * fx) * ify + (b[o0 + 2] * ifx + b[o1 + 2] * fx) * fy + 32768) >> 16);
                            o[3] = (byte)(((a[o0 + 3] * ifx + a[o1 + 3] * fx) * ify + (b[o0 + 3] * ifx + b[o1 + 3] * fx) * fy + 32768) >> 16);
                        }
                    }
                }
            });
        }

        static int Div255(int x) { x += 128; return (x + (x >> 8)) >> 8; }

        // ------------------------------------------------------------------ drawing a layer

        /// <summary>
        /// Draws a layer onto an opaque canvas. The source must be opaque or premultiplied (see Frame.AsPremultiplied).
        /// </summary>
        public static void Draw(Frame dst, Layer l)
        {
            var src = l.Src;
            if (src == null) return;
            int op = (int)Math.Round(Math.Max(0, Math.Min(1, l.Opacity)) * 256);
            if (op <= 0) return;
            double det = l.A * l.D - l.B * l.C;
            if (Math.Abs(det) < 1e-12) return;
            int sw = src.Width, sh = src.Height, dw = dst.Width, dh = dst.Height;

            // where the picture's four corners land
            double x1 = l.Tx, y1 = l.Ty, x2 = l.A * sw + l.Tx, y2 = l.B * sw + l.Ty;
            double x3 = l.C * sh + l.Tx, y3 = l.D * sh + l.Ty, x4 = l.A * sw + l.C * sh + l.Tx, y4 = l.B * sw + l.D * sh + l.Ty;
            int bx0 = (int)Math.Floor(Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)) - 0.5);
            int bx1 = (int)Math.Ceiling(Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)) + 0.5);
            int by0 = (int)Math.Floor(Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)) - 0.5);
            int by1 = (int)Math.Ceiling(Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)) + 0.5);
            int cx0 = 0, cy0 = 0, cx1 = dw, cy1 = dh;
            if (l.HasClip)
            {
                cx0 = Math.Max(cx0, l.Clip.Left); cy0 = Math.Max(cy0, l.Clip.Top);
                cx1 = Math.Min(cx1, l.Clip.Right); cy1 = Math.Min(cy1, l.Clip.Bottom);
            }
            bx0 = Math.Max(bx0, cx0); bx1 = Math.Min(bx1, cx1); by0 = Math.Max(by0, cy0); by1 = Math.Min(by1, cy1);
            if (bx1 <= bx0 || by1 <= by0) return;

            bool axis = Math.Abs(l.B) < 1e-7 && Math.Abs(l.C) < 1e-7 && l.A > 0 && l.D > 0;
            bool exact = axis && Math.Abs(l.A - 1) < 1e-5 && Math.Abs(l.D - 1) < 1e-5 &&
                         Math.Abs(l.Tx - Math.Round(l.Tx)) < 0.004 && Math.Abs(l.Ty - Math.Round(l.Ty)) < 0.004;
            if (exact) { DrawExact(dst, l, src, (int)Math.Round(l.Tx), (int)Math.Round(l.Ty), bx0, by0, bx1, by1, op); return; }
            if (axis && l.Sharp)
            {
                // resample once onto the canvas grid with a sharp filter, then place the result pixel for pixel
                int rx0 = Math.Max(cx0, (int)Math.Floor(l.Tx)), ry0 = Math.Max(cy0, (int)Math.Floor(l.Ty));
                int rx1 = Math.Min(cx1, (int)Math.Ceiling(l.Tx + l.A * sw)), ry1 = Math.Min(cy1, (int)Math.Ceiling(l.Ty + l.D * sh));
                if (rx1 <= rx0 || ry1 <= ry0) return;
                var tmp = Resample(src, l.A, l.D, l.Tx, l.Ty, rx0, ry0, rx1 - rx0, ry1 - ry0);
                DrawExact(dst, l, tmp, rx0, ry0, rx0, ry0, rx1, ry1, op);
                Return(tmp.Data);
                return;
            }
            // Two-by-two sampling skips pixels when a picture is shrunk to less than half, which shimmers in motion:
            // halve it first (each step averages), then sample the smaller copy.
            double shrink = Math.Min(Math.Sqrt(l.A * l.A + l.B * l.B), Math.Sqrt(l.C * l.C + l.D * l.D));
            Layer small = null;
            while (shrink < 0.5 && (small ?? l).Src.Width >= 4 && (small ?? l).Src.Height >= 4)
            {
                var from = (small ?? l).Src;
                int hw = (from.Width + 1) / 2, hh = (from.Height + 1) / 2;
                var half = new Frame(hw, hh, Rent((long)hw * hh * 4));
                Halve(from, half);
                double kx = (double)from.Width / hw, ky = (double)from.Height / hh;
                var next = (small ?? l).CopyFor(half);
                next.A *= kx; next.B *= kx; next.C *= ky; next.D *= ky;
                if (small != null) Return(small.Src.Data);
                small = next;
                shrink *= 2;
            }
            var use = small ?? l;
            if (axis) DrawAxis(dst, use, bx0, by0, bx1, by1, op);
            else DrawAffine(dst, use, use.A * use.D - use.B * use.C, bx0, by0, bx1, by1, op);
            if (small != null) Return(small.Src.Data);
        }

        // ------------------------------------------------------------------ working buffers

        // Scaling and halving need a frame-sized buffer for every frame drawn; reusing a few keeps the large-object
        // heap quiet during playback and export.
        static readonly object poolGate = new object();
        static readonly List<byte[]> pool = new List<byte[]>();

        internal static byte[] Rent(long bytes)
        {
            lock (poolGate)
                for (int i = pool.Count - 1; i >= 0; i--)
                    if (pool[i].LongLength == bytes) { var b = pool[i]; pool.RemoveAt(i); return b; }
            return new byte[bytes];
        }

        internal static void Return(byte[] b)
        {
            if (b == null || b.Length < 64 * 1024) return;
            lock (poolGate)
            {
                if (pool.Count >= 8) pool.RemoveAt(0);
                pool.Add(b);
            }
        }

        /// <summary>Per-thread working rows.</summary>
        sealed class Work
        {
            public byte[] Row, Alpha, Alpha2;
            public Work(int width) { Row = new byte[width * 4 + 16]; Alpha = new byte[width + 4]; Alpha2 = new byte[width + 4]; }
        }

        static void DrawExact(Frame dst, Layer l, Frame src, int tx, int ty, int bx0, int by0, int bx1, int by1, int op)
        {
            int sw = src.Width, sh = src.Height, dw = dst.Width;
            bx0 = Math.Max(bx0, tx); by0 = Math.Max(by0, ty); bx1 = Math.Min(bx1, tx + sw); by1 = Math.Min(by1, ty + sh);
            int n = bx1 - bx0;
            if (n <= 0 || by1 <= by0) return;
            var sd = src.Data; var dd = dst.Data;
            bool plain = src.Opaque && op >= 256 && l.Mode == Blend.Normal && l.Alpha == null && l.Mask == null;
            var inv = Inverse(l);
            Rows(by0, by1, (long)n * (by1 - by0), (ya, yb) =>
            {
                Work wk = plain ? null : new Work(n);
                fixed (byte* s0 = sd) fixed (byte* d0 = dd) fixed (byte* al = plain ? null : wk.Alpha)
                {
                    for (int y = ya; y < yb; y++)
                    {
                        byte* s = s0 + ((long)(y - ty) * sw + (bx0 - tx)) * 4;
                        byte* d = d0 + ((long)y * dw + bx0) * 4;
                        if (plain) { Buffer.MemoryCopy(s, d, (long)n * 4, (long)n * 4); continue; }
                        byte* a = FillAlpha(l, inv, y, bx0, n, al);
                        BlendRow(d, s, n, op, a, l.Mode, src.Opaque);
                    }
                }
            });
        }

        struct Inv { public double A, B, C, D, Tx, Ty; public int W, H; }

        static Inv Inverse(Layer l)
        {
            double det = l.A * l.D - l.B * l.C;
            if (Math.Abs(det) < 1e-12) det = 1e-12;
            return new Inv { A = l.D / det, B = -l.B / det, C = -l.C / det, D = l.A / det, Tx = l.Tx, Ty = l.Ty, W = l.Src.Width, H = l.Src.Height };
        }

        /// <summary>Canvas-space alpha and the clip's mask for one row; null when the layer has neither.</summary>
        static byte* FillAlpha(Layer l, Inv inv, int y, int x, int n, byte* buf)
        {
            if (l.Alpha == null && l.Mask == null) return null;
            if (l.Alpha != null) l.Alpha(y, x, n, buf);
            else for (int i = 0; i < n; i++) buf[i] = 255;
            if (l.Mask != null) MaskRow(l.Mask, inv, y, x, n, buf);
            return buf;
        }

        static void MaskRow(MaskShape m, Inv inv, int y, int x, int n, byte* buf)
        {
            // picture-space position of each canvas pixel, as a fraction of the picture from its middle
            double py = y + 0.5 - inv.Ty;
            double w = inv.W, h = inv.H;
            double aspect = w / Math.Max(1.0, h);
            double rot = -m.Rotation * Math.PI / 180, cs = Math.Cos(rot), sn = Math.Sin(rot);
            double hw = Math.Max(0.001, m.W / 2), hh = Math.Max(0.001, m.H / 2);
            // work in units of the picture's height so shapes aren't squashed by the aspect ratio
            double hwU = hw * aspect, hhU = hh;
            double feather = Math.Max(0.0005, m.Feather * 0.5 * Math.Min(hwU, hhU) * 2);
            double round = Math.Max(0, Math.Min(1, m.Round)) * Math.Min(hwU, hhU);
            for (int i = 0; i < n; i++)
            {
                double px = x + i + 0.5 - inv.Tx;
                double u = inv.A * px + inv.C * py, v = inv.B * px + inv.D * py;
                double fx = (u / w - 0.5 - m.X) * aspect, fy = v / h - 0.5 - m.Y;
                double rx = fx * cs - fy * sn, ry = fx * sn + fy * cs;
                double dist; // signed distance to the shape's edge, negative inside
                if (m.Kind == 2)
                {
                    double k = Math.Sqrt(rx * rx / (hwU * hwU) + ry * ry / (hhU * hhU));
                    dist = (k - 1) * Math.Min(hwU, hhU);
                }
                else if (m.Kind == 3) dist = ry;
                else
                {
                    double qx = Math.Abs(rx) - hwU + round, qy = Math.Abs(ry) - hhU + round;
                    double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                    dist = Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - round;
                }
                double a = 0.5 - dist / feather;
                if (a < 0) a = 0; else if (a > 1) a = 1;
                if (m.Invert) a = 1 - a;
                buf[i] = (byte)(buf[i] * a + 0.5);
            }
        }

        static void DrawAxis(Frame dst, Layer l, int bx0, int by0, int bx1, int by1, int op)
        {
            var src = l.Src;
            int sw = src.Width, sh = src.Height, dw = dst.Width;
            // pixels wholly or partly inside the picture's rectangle
            double left = l.Tx, right = l.Tx + l.A * sw, top = l.Ty, bottom = l.Ty + l.D * sh;
            bx0 = Math.Max(bx0, (int)Math.Floor(left)); bx1 = Math.Min(bx1, (int)Math.Ceiling(right));
            by0 = Math.Max(by0, (int)Math.Floor(top)); by1 = Math.Min(by1, (int)Math.Ceiling(bottom));
            int n = bx1 - bx0;
            if (n <= 0 || by1 <= by0) return;
            // per-column sample positions
            var xi = new int[n * 2];
            var xf = new int[n];
            var cov = new int[n];
            for (int i = 0; i < n; i++)
            {
                double u = (bx0 + i + 0.5 - l.Tx) / l.A - 0.5;
                int iu = (int)Math.Floor(u);
                int f = (int)((u - iu) * 256);
                int a = iu < 0 ? 0 : iu >= sw ? sw - 1 : iu, b = iu + 1 < 0 ? 0 : iu + 1 >= sw ? sw - 1 : iu + 1;
                xi[i * 2] = a * 4; xi[i * 2 + 1] = b * 4; xf[i] = f;
                double c = Math.Min(bx0 + i + 1, right) - Math.Max(bx0 + i, left);
                cov[i] = (int)(Math.Max(0, Math.Min(1, c)) * 256 + 0.5);
            }
            bool edgeX = cov[0] < 256 || cov[n - 1] < 256;
            var sd = src.Data; var dd = dst.Data;
            var inv = Inverse(l);
            Rows(by0, by1, (long)n * (by1 - by0) * 2, (ya, yb) =>
            {
                var wk = new Work(n);
                fixed (byte* s0 = sd) fixed (byte* d0 = dd) fixed (byte* row = wk.Row) fixed (byte* al = wk.Alpha)
                fixed (int* pxi = xi) fixed (int* pxf = xf) fixed (int* pcov = cov)
                {
                    for (int y = ya; y < yb; y++)
                    {
                        double v = (y + 0.5 - l.Ty) / l.D - 0.5;
                        int iv = (int)Math.Floor(v);
                        int fy = (int)((v - iv) * 256);
                        int r0 = iv < 0 ? 0 : iv >= sh ? sh - 1 : iv, r1 = iv + 1 < 0 ? 0 : iv + 1 >= sh ? sh - 1 : iv + 1;
                        byte* a = s0 + (long)r0 * sw * 4, b = s0 + (long)r1 * sw * 4;
                        byte* o = row;
                        int ify = 256 - fy;
                        for (int i = 0; i < n; i++, o += 4)
                        {
                            int o0 = pxi[i * 2], o1 = pxi[i * 2 + 1], fx = pxf[i], ifx = 256 - fx;
                            o[0] = (byte)(((a[o0] * ifx + a[o1] * fx) * ify + (b[o0] * ifx + b[o1] * fx) * fy + 32768) >> 16);
                            o[1] = (byte)(((a[o0 + 1] * ifx + a[o1 + 1] * fx) * ify + (b[o0 + 1] * ifx + b[o1 + 1] * fx) * fy + 32768) >> 16);
                            o[2] = (byte)(((a[o0 + 2] * ifx + a[o1 + 2] * fx) * ify + (b[o0 + 2] * ifx + b[o1 + 2] * fx) * fy + 32768) >> 16);
                            o[3] = (byte)(((a[o0 + 3] * ifx + a[o1 + 3] * fx) * ify + (b[o0 + 3] * ifx + b[o1 + 3] * fx) * fy + 32768) >> 16);
                        }
                        double cy = Math.Min(y + 1, bottom) - Math.Max(y, top);
                        int covY = (int)(Math.Max(0, Math.Min(1, cy)) * 256 + 0.5);
                        byte* alpha = FillAlpha(l, inv, y, bx0, n, al);
                        bool opaqueRow = src.Opaque;
                        if (covY < 256 || edgeX)
                        {
                            // partly covered edge pixels: fold the coverage into the alpha row
                            if (alpha == null) { alpha = al; for (int i = 0; i < n; i++) al[i] = 255; }
                            for (int i = 0; i < n; i++)
                            {
                                int c = (pcov[i] * covY) >> 8;
                                if (c < 256) alpha[i] = (byte)((alpha[i] * c) >> 8);
                            }
                        }
                        BlendRow(d0 + ((long)y * dw + bx0) * 4, row, n, op, alpha, l.Mode, opaqueRow);
                    }
                }
            });
        }

        static void DrawAffine(Frame dst, Layer l, double det, int bx0, int by0, int bx1, int by1, int op)
        {
            var src = l.Src;
            int sw = src.Width, sh = src.Height, dw = dst.Width;
            var inv = Inverse(l);
            // how many canvas pixels one source pixel covers along each source axis (for soft edges)
            double su = Math.Sqrt(l.A * l.A + l.B * l.B), sv = Math.Sqrt(l.C * l.C + l.D * l.D);
            double mu = 0.5 / Math.Max(1e-6, su), mv = 0.5 / Math.Max(1e-6, sv);
            var sd = src.Data; var dd = dst.Data;
            int width = bx1 - bx0;
            Rows(by0, by1, (long)width * (by1 - by0) * 3, (ya, yb) =>
            {
                var wk = new Work(width);
                fixed (byte* s0 = sd) fixed (byte* d0 = dd) fixed (byte* row = wk.Row) fixed (byte* al = wk.Alpha) fixed (byte* t = wk.Alpha2)
                {
                    for (int y = ya; y < yb; y++)
                    {
                        double py = y + 0.5 - inv.Ty;
                        // u = inv.A·px + inv.C·py and v = inv.B·px + inv.D·py; find the px range that stays inside the picture
                        double lo = bx0 + 0.5 - inv.Tx, hi = bx1 - 0.5 - inv.Tx;
                        if (!Limit(inv.A, inv.C * py, -mu, sw + mu, ref lo, ref hi)) continue;
                        if (!Limit(inv.B, inv.D * py, -mv, sh + mv, ref lo, ref hi)) continue;
                        int xa = Math.Max(bx0, (int)Math.Ceiling(lo + inv.Tx - 0.5)), xb = Math.Min(bx1, (int)Math.Floor(hi + inv.Tx - 0.5) + 1);
                        int n = xb - xa;
                        if (n <= 0) continue;
                        // The stretch of the row that is well inside the picture needs no edge softening or clamping and is
                        // stepped in fixed point; only the few pixels at each end take the careful path.
                        int ia = xb, ib = xb;
                        if (sw >= 2 && sh >= 2)
                        {
                            double ilo = bx0 + 0.5 - inv.Tx, ihi = bx1 - 0.5 - inv.Tx;
                            double eu = Math.Max(mu, 0.5), ev = Math.Max(mv, 0.5);
                            if (Limit(inv.A, inv.C * py, eu, sw - eu - 0.002, ref ilo, ref ihi) && Limit(inv.B, inv.D * py, ev, sh - ev - 0.002, ref ilo, ref ihi))
                            {
                                ia = Math.Max(xa, (int)Math.Ceiling(ilo + inv.Tx - 0.5) + 1);
                                ib = Math.Min(xb, (int)Math.Floor(ihi + inv.Tx - 0.5));
                                if (ib <= ia) ia = ib = xb;
                            }
                        }
                        bool soft = SlowSpan(s0, sw, sh, inv, py, su, sv, xa, ia - xa, row, al);
                        if (ib > ia)
                        {
                            double px0 = ia + 0.5 - inv.Tx;
                            long U = (long)((inv.A * px0 + inv.C * py - 0.5) * 4294967296.0), V = (long)((inv.B * px0 + inv.D * py - 0.5) * 4294967296.0);
                            long dU = (long)(inv.A * 4294967296.0), dV = (long)(inv.B * 4294967296.0);
                            byte* o = row + (long)(ia - xa) * 4;
                            long rowBytes = (long)sw * 4;
                            for (int i = ia; i < ib; i++, o += 4, U += dU, V += dV)
                            {
                                int fx = (int)((U >> 24) & 255), fy = (int)((V >> 24) & 255), ifx = 256 - fx, ify = 256 - fy;
                                byte* a = s0 + (V >> 32) * rowBytes + (U >> 32) * 4;
                                byte* c = a + rowBytes;
                                o[0] = (byte)(((a[0] * ifx + a[4] * fx) * ify + (c[0] * ifx + c[4] * fx) * fy + 32768) >> 16);
                                o[1] = (byte)(((a[1] * ifx + a[5] * fx) * ify + (c[1] * ifx + c[5] * fx) * fy + 32768) >> 16);
                                o[2] = (byte)(((a[2] * ifx + a[6] * fx) * ify + (c[2] * ifx + c[6] * fx) * fy + 32768) >> 16);
                                o[3] = (byte)(((a[3] * ifx + a[7] * fx) * ify + (c[3] * ifx + c[7] * fx) * fy + 32768) >> 16);
                            }
                            soft |= SlowSpan(s0, sw, sh, inv, py, su, sv, ib, xb - ib, row + (long)(ib - xa) * 4, al + (ib - xa));
                            if (soft || l.Alpha != null || l.Mask != null) for (int i = ia - xa; i < ib - xa; i++) al[i] = 255;
                        }
                        byte* alpha = null;
                        if (l.Alpha != null || l.Mask != null)
                        {
                            // combine the edge coverage with the layer's own alpha
                            FillAlpha(l, inv, y, xa, n, t);
                            for (int i = 0; i < n; i++) al[i] = (byte)Div255(al[i] * t[i]);
                            alpha = al;
                        }
                        else if (soft) alpha = al;
                        BlendRow(d0 + ((long)y * dw + xa) * 4, row, n, op, alpha, l.Mode, src.Opaque);
                    }
                }
            });
        }

        /// <summary>Samples count pixels starting at canvas column x with edge coverage; true if any was partly covered.</summary>
        static bool SlowSpan(byte* s0, int sw, int sh, Inv inv, double py, double su, double sv, int x, int count, byte* o, byte* al)
        {
            bool soft = false;
            for (int i = 0; i < count; i++, o += 4)
            {
                double px = x + i + 0.5 - inv.Tx;
                double u = inv.A * px + inv.C * py, v = inv.B * px + inv.D * py;
                double cu = Math.Min(u * su + 0.5, (sw - u) * su + 0.5), cv = Math.Min(v * sv + 0.5, (sh - v) * sv + 0.5);
                double cover = (cu >= 1 ? 1 : cu <= 0 ? 0 : cu) * (cv >= 1 ? 1 : cv <= 0 ? 0 : cv);
                double uu = u - 0.5, vv = v - 0.5;
                int iu = (int)Math.Floor(uu), iv = (int)Math.Floor(vv);
                int fx = (int)((uu - iu) * 256), fy = (int)((vv - iv) * 256), ifx = 256 - fx, ify = 256 - fy;
                int c0 = iu < 0 ? 0 : iu >= sw ? sw - 1 : iu, c1 = iu + 1 < 0 ? 0 : iu + 1 >= sw ? sw - 1 : iu + 1;
                int r0 = iv < 0 ? 0 : iv >= sh ? sh - 1 : iv, r1 = iv + 1 < 0 ? 0 : iv + 1 >= sh ? sh - 1 : iv + 1;
                byte* a = s0 + ((long)r0 * sw + c0) * 4, b = s0 + ((long)r0 * sw + c1) * 4;
                byte* c = s0 + ((long)r1 * sw + c0) * 4, d = s0 + ((long)r1 * sw + c1) * 4;
                o[0] = (byte)(((a[0] * ifx + b[0] * fx) * ify + (c[0] * ifx + d[0] * fx) * fy + 32768) >> 16);
                o[1] = (byte)(((a[1] * ifx + b[1] * fx) * ify + (c[1] * ifx + d[1] * fx) * fy + 32768) >> 16);
                o[2] = (byte)(((a[2] * ifx + b[2] * fx) * ify + (c[2] * ifx + d[2] * fx) * fy + 32768) >> 16);
                o[3] = (byte)(((a[3] * ifx + b[3] * fx) * ify + (c[3] * ifx + d[3] * fx) * fy + 32768) >> 16);
                if (cover < 0.999) { al[i] = (byte)(cover * 255 + 0.5); soft = true; } else al[i] = 255;
            }
            return soft;
        }

        /// <summary>Narrows [lo, hi] to where k·x + c stays within [min, max]. False when nothing is left.</summary>
        static bool Limit(double k, double c, double min, double max, ref double lo, ref double hi)
        {
            if (Math.Abs(k) < 1e-12) return c >= min && c <= max;
            double a = (min - c) / k, b = (max - c) / k;
            if (a > b) { double t = a; a = b; b = t; }
            if (a > lo) lo = a;
            if (b < hi) hi = b;
            return hi >= lo;
        }

        // ------------------------------------------------------------------ blending

        /// <summary>
        /// Mixes n premultiplied source pixels into an opaque destination row. op is the layer opacity (0..256),
        /// alpha an optional per-pixel factor (0..255).
        /// </summary>
        static void BlendRow(byte* d, byte* s, int n, int op, byte* alpha, Blend mode, bool srcOpaque)
        {
            if (mode == Blend.Normal)
            {
                if (alpha == null && op >= 256)
                {
                    if (srcOpaque) { Buffer.MemoryCopy(s, d, (long)n * 4, (long)n * 4); return; }
                    for (int i = 0; i < n; i++, d += 4, s += 4)
                    {
                        int sa = s[3];
                        if (sa == 0) continue;
                        if (sa == 255) { *(uint*)d = *(uint*)s; continue; }
                        int inv = 255 - sa;
                        d[0] = (byte)(s[0] + Div255(d[0] * inv));
                        d[1] = (byte)(s[1] + Div255(d[1] * inv));
                        d[2] = (byte)(s[2] + Div255(d[2] * inv));
                        d[3] = 255;
                    }
                    return;
                }
                for (int i = 0; i < n; i++, d += 4, s += 4)
                {
                    int k = alpha == null ? op : (op * alpha[i] + 127) / 255;   // 0..256
                    if (k <= 0) continue;
                    int sa = (s[3] * k) >> 8;
                    if (sa <= 0) continue;
                    int inv = 255 - sa;
                    int b = ((s[0] * k) >> 8) + Div255(d[0] * inv), g = ((s[1] * k) >> 8) + Div255(d[1] * inv), r = ((s[2] * k) >> 8) + Div255(d[2] * inv);
                    d[0] = (byte)(b > 255 ? 255 : b); d[1] = (byte)(g > 255 ? 255 : g); d[2] = (byte)(r > 255 ? 255 : r);
                    d[3] = 255;
                }
                return;
            }
            for (int i = 0; i < n; i++, d += 4, s += 4)
            {
                int k = alpha == null ? op : (op * alpha[i] + 127) / 255;
                if (k <= 0) continue;
                int sa = s[3];
                if (sa == 0) continue;
                int a = (sa * k) >> 8;          // how much of the blended colour shows (0..255)
                if (a <= 0) continue;
                int sb = s[0], sg = s[1], sr = s[2];
                if (sa < 255) { sb = Math.Min(255, sb * 255 / sa); sg = Math.Min(255, sg * 255 / sa); sr = Math.Min(255, sr * 255 / sa); }
                int nb = Mix(mode, d[0], sb), ng = Mix(mode, d[1], sg), nr = Mix(mode, d[2], sr);
                d[0] = (byte)(d[0] + Div255Signed((nb - d[0]) * a));
                d[1] = (byte)(d[1] + Div255Signed((ng - d[1]) * a));
                d[2] = (byte)(d[2] + Div255Signed((nr - d[2]) * a));
                d[3] = 255;
            }
        }

        static int Div255Signed(int x) { return x >= 0 ? Div255(x) : -Div255(-x); }

        /// <summary>The blended colour of one channel (backdrop b, source s, both 0..255).</summary>
        static int Mix(Blend mode, int b, int s)
        {
            switch (mode)
            {
                case Blend.Screen: return 255 - Div255((255 - b) * (255 - s));
                case Blend.Multiply: return Div255(b * s);
                case Blend.Overlay: return b < 128 ? Div255(2 * b * s) : 255 - Div255(2 * (255 - b) * (255 - s));
                case Blend.HardLight: return s < 128 ? Div255(2 * b * s) : 255 - Div255(2 * (255 - b) * (255 - s));
                case Blend.Add: { int v = b + s; return v > 255 ? 255 : v; }
                case Blend.Darken: return b < s ? b : s;
                case Blend.Lighten: return b > s ? b : s;
                case Blend.Difference: return b > s ? b - s : s - b;
                case Blend.SoftLight:
                    {
                        // the "pegtop" formula: smooth, no branch at mid-grey
                        int m = Div255(b * s);
                        int sc = 255 - Div255((255 - b) * (255 - s));
                        return Div255((255 - b) * m + b * sc);
                    }
                default: return s;
            }
        }

        // ------------------------------------------------------------------ sharp resampling

        sealed class Taps { public int[] Start; public int[] Weights; public int Count; }

        static double Cubic(double x)
        {
            // Catmull-Rom: sharp without ringing much; widened for shrinking so it also averages
            x = Math.Abs(x);
            if (x < 1) return 1.5 * x * x * x - 2.5 * x * x + 1;
            if (x < 2) return -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2;
            return 0;
        }

        /// <summary>Filter taps for n destination pixels starting at d0, source scaled by 'scale' and offset by 'offset'.</summary>
        static Taps MakeTaps(int n, int d0, double scale, double offset, int srcLen)
        {
            double fscale = Math.Max(1.0, 1.0 / scale);
            double support = 2.0 * fscale;
            bool four = fscale == 1.0;      // enlarging: exactly the four nearest pixels count
            int count = four ? 4 : (int)Math.Ceiling(support * 2) + 1;
            var t = new Taps { Start = new int[n], Weights = new int[n * count], Count = count };
            var w = new double[count];
            for (int i = 0; i < n; i++)
            {
                double centre = (d0 + i + 0.5 - offset) / scale;   // in source pixels, centres at k + 0.5
                int first = four ? (int)Math.Floor(centre - 0.5) - 1 : (int)Math.Floor(centre - support);
                double sum = 0;
                for (int k = 0; k < count; k++) { w[k] = Cubic((first + k + 0.5 - centre) / fscale); sum += w[k]; }
                if (Math.Abs(sum) < 1e-9) { sum = 1; w[count / 2] = 1; }
                int total = 0, best = 0;
                for (int k = 0; k < count; k++)
                {
                    int v = (int)Math.Round(w[k] / sum * 16384);
                    t.Weights[i * count + k] = v; total += v;
                    if (Math.Abs(v) > Math.Abs(t.Weights[i * count + best])) best = k;
                }
                t.Weights[i * count + best] += 16384 - total;   // weights add up to exactly 1
                t.Start[i] = first;
            }
            return t;
        }

        /// <summary>
        /// Scales src (by sx, sy, placed at tx, ty on the canvas) onto the w × h block of canvas pixels at (x0, y0).
        /// The result is premultiplied, with partly covered edge pixels faded by their coverage.
        /// </summary>
        public static Frame Resample(Frame src, double sx, double sy, double tx, double ty, int x0, int y0, int w, int h)
        {
            int sw = src.Width, sh = src.Height;
            var tapsX = MakeTaps(w, x0, sx, tx, sw);
            var tapsY = MakeTaps(h, y0, sy, ty, sh);
            // source rows the vertical pass needs
            int rowA = sh, rowB = -1;
            for (int j = 0; j < h; j++)
            {
                rowA = Math.Min(rowA, tapsY.Start[j]); rowB = Math.Max(rowB, tapsY.Start[j] + tapsY.Count - 1);
            }
            rowA = Math.Max(0, rowA); rowB = Math.Min(sh - 1, rowB);
            int midH = Math.Max(1, rowB - rowA + 1);
            var mid = Rent((long)w * midH * 4);
            var sd = src.Data;
            int cx = tapsX.Count;
            Rows(0, midH, (long)w * midH * cx, (ya, yb) =>
            {
                fixed (byte* s0 = sd) fixed (byte* m0 = mid) fixed (int* st = tapsX.Start) fixed (int* wt = tapsX.Weights)
                {
                    for (int y = ya; y < yb; y++)
                    {
                        byte* s = s0 + (long)(y + rowA) * sw * 4;
                        byte* o = m0 + (long)y * w * 4;
                        for (int i = 0; i < w; i++, o += 4)
                        {
                            int b = 0, g = 0, r = 0, a = 0, first = st[i];
                            int* wp = wt + i * cx;
                            if (cx == 4 && first >= 0 && first + 3 < sw)
                            {
                                byte* p = s + first * 4;
                                int w0 = wp[0], w1 = wp[1], w2 = wp[2], w3 = wp[3];
                                b = p[0] * w0 + p[4] * w1 + p[8] * w2 + p[12] * w3;
                                g = p[1] * w0 + p[5] * w1 + p[9] * w2 + p[13] * w3;
                                r = p[2] * w0 + p[6] * w1 + p[10] * w2 + p[14] * w3;
                                a = p[3] * w0 + p[7] * w1 + p[11] * w2 + p[15] * w3;
                            }
                            else for (int k = 0; k < cx; k++)
                            {
                                int xx = first + k;
                                if (xx < 0) xx = 0; else if (xx >= sw) xx = sw - 1;
                                byte* p = s + xx * 4;
                                int wv = wp[k];
                                b += p[0] * wv; g += p[1] * wv; r += p[2] * wv; a += p[3] * wv;
                            }
                            o[0] = Clamp8((b + 8192) >> 14); o[1] = Clamp8((g + 8192) >> 14); o[2] = Clamp8((r + 8192) >> 14); o[3] = Clamp8((a + 8192) >> 14);
                        }
                    }
                }
            });
            var dst = new Frame(w, h, Rent((long)w * h * 4));
            var dd = dst.Data;
            int cy = tapsY.Count;
            double left = tx, right = tx + sx * sw, top = ty, bottom = ty + sy * sh;
            bool opaque = src.Opaque;
            bool edge = left > x0 + 1e-6 || right < x0 + w - 1e-6 || top > y0 + 1e-6 || bottom < y0 + h - 1e-6;
            Rows(0, h, (long)w * h * cy, (ya, yb) =>
            {
                fixed (byte* m0 = mid) fixed (byte* d0 = dd) fixed (int* st = tapsY.Start) fixed (int* wt = tapsY.Weights)
                {
                    var rows = stackalloc byte*[cy];
                    for (int y = ya; y < yb; y++)
                    {
                        int first = st[y];
                        for (int k = 0; k < cy; k++)
                        {
                            int yy = first + k;
                            if (yy < rowA) yy = rowA; else if (yy > rowB) yy = rowB;
                            rows[k] = m0 + (long)(yy - rowA) * w * 4;
                        }
                        int* wp = wt + y * cy;
                        byte* o = d0 + (long)y * w * 4;
                        double covY = Math.Max(0, Math.Min(1, Math.Min(y0 + y + 1, bottom) - Math.Max(y0 + y, top)));
                        for (int i = 0; i < w; i++, o += 4)
                        {
                            int b = 0, g = 0, r = 0, a = 0, off = i * 4;
                            if (cy == 4)
                            {
                                byte* p0 = rows[0] + off, p1 = rows[1] + off, p2 = rows[2] + off, p3 = rows[3] + off;
                                int w0 = wp[0], w1 = wp[1], w2 = wp[2], w3 = wp[3];
                                b = p0[0] * w0 + p1[0] * w1 + p2[0] * w2 + p3[0] * w3;
                                g = p0[1] * w0 + p1[1] * w1 + p2[1] * w2 + p3[1] * w3;
                                r = p0[2] * w0 + p1[2] * w1 + p2[2] * w2 + p3[2] * w3;
                                a = p0[3] * w0 + p1[3] * w1 + p2[3] * w2 + p3[3] * w3;
                            }
                            else for (int k = 0; k < cy; k++)
                            {
                                byte* p = rows[k] + off;
                                int wv = wp[k];
                                b += p[0] * wv; g += p[1] * wv; r += p[2] * wv; a += p[3] * wv;
                            }
                            int ob = Clamp8((b + 8192) >> 14), og = Clamp8((g + 8192) >> 14), or = Clamp8((r + 8192) >> 14), oa = opaque ? 255 : Clamp8((a + 8192) >> 14);
                            if (!opaque)
                            {
                                // sharpening can push a colour past its alpha; premultiplied colour never exceeds it
                                if (ob > oa) ob = oa; if (og > oa) og = oa; if (or > oa) or = oa;
                            }
                            if (edge)
                            {
                                double c = covY * Math.Max(0, Math.Min(1, Math.Min(x0 + i + 1, right) - Math.Max(x0 + i, left)));
                                if (c < 0.999)
                                {
                                    int ci = (int)(c * 256 + 0.5);
                                    ob = (ob * ci) >> 8; og = (og * ci) >> 8; or = (or * ci) >> 8; oa = (oa * ci) >> 8;
                                }
                            }
                            o[0] = (byte)ob; o[1] = (byte)og; o[2] = (byte)or; o[3] = (byte)oa;
                        }
                    }
                }
            });
            Return(mid);
            dst.Premultiplied = true;
            dst.Opaque = opaque && !edge;
            return dst;
        }

        static byte Clamp8(int v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }
    }
}
