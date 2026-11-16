using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace StillsAndReels
{
    /// <summary>
    /// The pixels of one picture or layer: 32-bit premultiplied BGRA in memory outside the .NET heap, with a GDI+
    /// bitmap over the same memory. The picture code reads and writes the pixels directly (fast loops on every
    /// core) and still draws on them with Graphics. Nothing here touches a file.
    /// </summary>
    internal sealed unsafe class PicSurface : IDisposable
    {
        /// <summary>The largest picture the editor will hold in memory (width × height).</summary>
        public const long MaxPixels = 400L * 1000 * 1000;

        public readonly int Width, Height, Stride;
        IntPtr mem;
        Bitmap bmp;
        readonly long bytes;

        public PicSurface(int width, int height) : this(width, height, true) { }

        PicSurface(int width, int height, bool clear)
        {
            if (width < 1 || height < 1) throw new ArgumentException("A picture needs at least one pixel.");
            if ((long)width * height > MaxPixels) throw new OutOfMemoryException("This picture is too large to edit.");
            Width = width; Height = height; Stride = width * 4;
            bytes = (long)Stride * height;
            mem = Marshal.AllocHGlobal(new IntPtr(bytes));
            GC.AddMemoryPressure(bytes);
            if (clear) Clear();
        }

        ~PicSurface() { Free(); }

        public void Dispose() { Free(); GC.SuppressFinalize(this); }

        void Free()
        {
            if (mem == IntPtr.Zero) return;
            if (bmp != null) { try { bmp.Dispose(); } catch { } bmp = null; }
            Marshal.FreeHGlobal(mem);
            mem = IntPtr.Zero;
            GC.RemoveMemoryPressure(bytes);
        }

        public bool IsDisposed { get { return mem == IntPtr.Zero; } }
        public long Bytes { get { return bytes; } }
        public Size Size { get { return new Size(Width, Height); } }
        public Rectangle Bounds { get { return new Rectangle(0, 0, Width, Height); } }
        public uint* Ptr { get { return (uint*)mem; } }
        public uint* Row(int y) { return (uint*)((byte*)mem + (long)y * Stride); }

        /// <summary>A GDI+ bitmap over these pixels (not a copy). It lives as long as the surface.</summary>
        public Bitmap Bitmap
        {
            get
            {
                if (bmp == null) bmp = new Bitmap(Width, Height, Stride, PixelFormat.Format32bppPArgb, mem);
                return bmp;
            }
        }

        /// <summary>A Graphics that draws straight onto the pixels, set up for smooth vector drawing.</summary>
        public Graphics CreateGraphics()
        {
            var g = Graphics.FromImage(Bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return g;
        }

        // ------------------------------------------------------------------ whole-surface operations

        public void Clear()
        {
            long n = bytes / 8;
            ulong* p = (ulong*)mem;
            for (long i = 0; i < n; i++) p[i] = 0;
            if ((bytes & 4) != 0) ((uint*)mem)[bytes / 4 - 1] = 0;
        }

        public PicSurface Clone()
        {
            var s = new PicSurface(Width, Height, false);
            Buffer.MemoryCopy((void*)mem, (void*)s.mem, bytes, bytes);
            return s;
        }

        /// <summary>A copy of part of the surface. The rectangle must lie inside it.</summary>
        public PicSurface Clone(Rectangle r)
        {
            r.Intersect(Bounds);
            if (r.Width < 1 || r.Height < 1) throw new ArgumentException("Nothing to copy.");
            var s = new PicSurface(r.Width, r.Height, false);
            s.CopyFrom(this, r, Point.Empty);
            return s;
        }

        /// <summary>Copies pixels as they are (no blending). Both rectangles are clipped to their surfaces.</summary>
        public void CopyFrom(PicSurface src, Rectangle srcRect, Point dst)
        {
            // clip against the source, then against this surface
            int sx = srcRect.X, sy = srcRect.Y, w = srcRect.Width, h = srcRect.Height, dx = dst.X, dy = dst.Y;
            if (sx < 0) { dx -= sx; w += sx; sx = 0; }
            if (sy < 0) { dy -= sy; h += sy; sy = 0; }
            if (dx < 0) { sx -= dx; w += dx; dx = 0; }
            if (dy < 0) { sy -= dy; h += dy; dy = 0; }
            w = Math.Min(w, Math.Min(src.Width - sx, Width - dx));
            h = Math.Min(h, Math.Min(src.Height - sy, Height - dy));
            if (w <= 0 || h <= 0) return;
            long rowBytes = (long)w * 4;
            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src.Row(sy + y) + sx, Row(dy + y) + dx, rowBytes, rowBytes);
        }

        public void Fill(Rectangle r, uint pargb)
        {
            r.Intersect(Bounds);
            if (r.Width <= 0 || r.Height <= 0) return;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                uint* p = Row(y) + r.Left;
                for (int x = 0; x < r.Width; x++) p[x] = pargb;
            }
        }

        public uint GetPixel(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
            return Row(y)[x];
        }

        public Color GetColor(int x, int y) { return ToColor(GetPixel(x, y)); }

        /// <summary>True when every pixel is fully opaque (no checkerboard is needed behind it).</summary>
        public bool IsOpaque()
        {
            bool opaque = true;
            PicPar.Rows(0, Height, Width, (y0, y1) =>
            {
                for (int y = y0; y < y1 && opaque; y++)
                {
                    uint* p = Row(y);
                    uint and = 0xFF000000;
                    for (int x = 0; x < Width; x++) and &= p[x];
                    if (and != 0xFF000000) opaque = false;
                }
            });
            return opaque;
        }

        /// <summary>The smallest rectangle that holds every pixel that is not fully transparent (empty if none).</summary>
        public Rectangle ContentBounds()
        {
            int top = -1, bottom = -1, left = Width, right = -1;
            for (int y = 0; y < Height; y++)
            {
                uint* p = Row(y);
                int l = -1, r = -1;
                for (int x = 0; x < Width; x++) if (p[x] >> 24 != 0) { l = x; break; }
                if (l < 0) continue;
                for (int x = Width - 1; x >= l; x--) if (p[x] >> 24 != 0) { r = x; break; }
                if (top < 0) top = y;
                bottom = y;
                if (l < left) left = l;
                if (r > right) right = r;
            }
            return top < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }

        // ------------------------------------------------------------------ to and from GDI+ images

        /// <summary>Copies any GDI+ image into a new surface.</summary>
        public static PicSurface FromImage(Image img)
        {
            var s = new PicSurface(img.Width, img.Height, false);
            var b = img as Bitmap;
            if (b != null)
            {
                BitmapData d = null;
                try
                {
                    // GDI+ converts to premultiplied BGRA while locking, which is the fastest copy there is
                    d = b.LockBits(new Rectangle(0, 0, s.Width, s.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    long rowBytes = (long)s.Width * 4;
                    for (int y = 0; y < s.Height; y++)
                        Buffer.MemoryCopy((byte*)d.Scan0 + (long)y * d.Stride, s.Row(y), rowBytes, rowBytes);
                    return s;
                }
                catch { /* some formats can't be locked this way: draw it instead */ }
                finally { if (d != null) try { b.UnlockBits(d); } catch { } }
            }
            s.Clear();
            using (var g = Graphics.FromImage(s.Bitmap))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(img, new Rectangle(0, 0, s.Width, s.Height));
            }
            return s;
        }

        /// <summary>A normal (not premultiplied) 32-bit ARGB bitmap copy, for saving and for the clipboard.</summary>
        public Bitmap ToBitmap() { return ToBitmap(Bounds); }

        public Bitmap ToBitmap(Rectangle r)
        {
            r.Intersect(Bounds);
            if (r.Width < 1 || r.Height < 1) throw new ArgumentException("Nothing to copy.");
            var b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            var d = b.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int w = r.Width, rx = r.X, ry = r.Y, stride = d.Stride;
                byte* scan = (byte*)d.Scan0;
                PicPar.Rows(0, r.Height, w, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        uint* sp = Row(ry + y) + rx;
                        uint* dp = (uint*)(scan + (long)y * stride);
                        for (int x = 0; x < w; x++) dp[x] = Unpremultiply(sp[x]);
                    }
                });
            }
            finally { b.UnlockBits(d); }
            return b;
        }

        // ------------------------------------------------------------------ rotate, flip, crop

        public PicSurface RotateFlip(RotateFlipType type)
        {
            int w = Width, h = Height;
            PicSurface d;
            switch (type)
            {
                case RotateFlipType.Rotate90FlipNone:   // (x, y) -> (h - 1 - y, x)
                    d = new PicSurface(h, w, false);
                    PicPar.Rows(0, w, h, (y0, y1) =>
                    {
                        for (int dy = y0; dy < y1; dy++) { uint* dp = d.Row(dy); for (int dx = 0; dx < h; dx++) dp[dx] = Row(h - 1 - dx)[dy]; }
                    });
                    return d;
                case RotateFlipType.Rotate270FlipNone:  // (x, y) -> (y, w - 1 - x)
                    d = new PicSurface(h, w, false);
                    PicPar.Rows(0, w, h, (y0, y1) =>
                    {
                        for (int dy = y0; dy < y1; dy++) { uint* dp = d.Row(dy); int sx = w - 1 - dy; for (int dx = 0; dx < h; dx++) dp[dx] = Row(dx)[sx]; }
                    });
                    return d;
                case RotateFlipType.Rotate180FlipNone:
                    d = new PicSurface(w, h, false);
                    PicPar.Rows(0, h, w, (y0, y1) =>
                    {
                        for (int dy = y0; dy < y1; dy++) { uint* dp = d.Row(dy); uint* sp = Row(h - 1 - dy); for (int dx = 0; dx < w; dx++) dp[dx] = sp[w - 1 - dx]; }
                    });
                    return d;
                case RotateFlipType.RotateNoneFlipX:
                    d = new PicSurface(w, h, false);
                    PicPar.Rows(0, h, w, (y0, y1) =>
                    {
                        for (int dy = y0; dy < y1; dy++) { uint* dp = d.Row(dy); uint* sp = Row(dy); for (int dx = 0; dx < w; dx++) dp[dx] = sp[w - 1 - dx]; }
                    });
                    return d;
                case RotateFlipType.RotateNoneFlipY:
                    d = new PicSurface(w, h, false);
                    long rowBytes = (long)w * 4;
                    for (int dy = 0; dy < h; dy++) Buffer.MemoryCopy(Row(h - 1 - dy), d.Row(dy), rowBytes, rowBytes);
                    return d;
                default:
                    throw new ArgumentException("Unsupported rotation.");
            }
        }

        /// <summary>A resampled copy at a new size. High quality: big reductions are averaged down first.</summary>
        public PicSurface Resize(int newW, int newH, bool smooth = true)
        {
            newW = Math.Max(1, newW); newH = Math.Max(1, newH);
            if (newW == Width && newH == Height) return Clone();
            PicSurface src = this;
            PicSurface owned = null;
            try
            {
                // halve while the target is under half the size, so every source pixel counts (no aliasing)
                while (smooth && src.Width >= newW * 2 && src.Height >= newH * 2 && src.Width > 1 && src.Height > 1)
                {
                    var half = new PicSurface((src.Width + 1) / 2, (src.Height + 1) / 2, false);
                    PicMips.Downsample(src, half, half.Bounds);
                    if (owned != null) owned.Dispose();
                    owned = src = half;
                }
                var d = new PicSurface(newW, newH, true);
                using (var g = Graphics.FromImage(d.Bitmap))
                using (var attr = new ImageAttributes())
                {
                    attr.SetWrapMode(WrapMode.TileFlipXY);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = smooth ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(src.Bitmap, new Rectangle(0, 0, newW, newH), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attr);
                }
                return d;
            }
            finally { if (owned != null) owned.Dispose(); }
        }

        // ------------------------------------------------------------------ colour helpers

        public static uint Premultiply(Color c)
        {
            uint a = c.A;
            if (a == 255) return (uint)c.ToArgb();
            uint r = (c.R * a + 127) / 255, g = (c.G * a + 127) / 255, b = (c.B * a + 127) / 255;
            return a << 24 | r << 16 | g << 8 | b;
        }

        public static uint Unpremultiply(uint p)
        {
            uint a = p >> 24;
            if (a == 255) return p;
            if (a == 0) return 0;
            uint r = ((p >> 16 & 255) * 255 + a / 2) / a, g = ((p >> 8 & 255) * 255 + a / 2) / a, b = ((p & 255) * 255 + a / 2) / a;
            if (r > 255) r = 255; if (g > 255) g = 255; if (b > 255) b = 255;
            return a << 24 | r << 16 | g << 8 | b;
        }

        public static Color ToColor(uint pargb) { return Color.FromArgb(unchecked((int)Unpremultiply(pargb))); }
    }

    /// <summary>Runs a loop over picture rows on every core when the area is big enough to be worth it.</summary>
    internal static class PicPar
    {
        public static void Rows(int y0, int y1, int width, Action<int, int> band)
        {
            int rows = y1 - y0;
            if (rows <= 0) return;
            long area = (long)rows * Math.Max(1, width);
            int cores = Environment.ProcessorCount;
            if (cores < 2 || area < 200000 || rows < 8) { band(y0, y1); return; }
            int parts = Math.Min(rows / 4, cores * 4);
            int step = (rows + parts - 1) / parts;
            Parallel.For(0, parts, i =>
            {
                int a = y0 + i * step, b = Math.Min(y1, a + step);
                if (a < b) band(a, b);
            });
        }
    }

    /// <summary>
    /// Half-size copies of a picture (a half, a quarter, an eighth, ...). When a big picture is shown zoomed out, the
    /// canvas draws from the copy nearest the size on screen, so zooming never has to resample the full picture.
    /// </summary>
    internal sealed unsafe class PicMips : IDisposable
    {
        const int SmallestSide = 256;
        PicSurface top;
        PicSurface[] levels = new PicSurface[0];   // levels[0] is the half-size copy

        public int Count { get { return levels.Length + 1; } }

        /// <summary>Level 0 is the picture itself, 1 is half size, 2 a quarter, and so on.</summary>
        public PicSurface Level(int i) { return i <= 0 ? top : levels[Math.Min(i, levels.Length) - 1]; }

        /// <summary>The level to draw from for a zoom factor: the smallest copy that is still at least as big as the picture on screen.</summary>
        public int LevelFor(float zoom)
        {
            int l = 0;
            float z = zoom;
            while (z <= 0.5f && l < levels.Length) { z *= 2; l++; }
            return l;
        }

        /// <summary>Builds every level from the full picture. Safe to call from a background thread before the surface is shared.</summary>
        public void Build(PicSurface full)
        {
            DisposeLevels();
            top = full;
            var list = new System.Collections.Generic.List<PicSurface>();
            var src = full;
            while (Math.Max(src.Width, src.Height) > SmallestSide && src.Width > 1 && src.Height > 1)
            {
                var half = new PicSurface((src.Width + 1) / 2, (src.Height + 1) / 2);
                Downsample(src, half, half.Bounds);
                list.Add(half);
                src = half;
            }
            levels = list.ToArray();
        }

        /// <summary>Refreshes every level for a changed part of the full picture.</summary>
        public void Update(Rectangle changed)
        {
            if (top == null) return;
            changed.Intersect(top.Bounds);
            if (changed.Width <= 0 || changed.Height <= 0) return;
            var src = top;
            var r = changed;
            for (int i = 0; i < levels.Length; i++)
            {
                r = Rectangle.FromLTRB(r.Left / 2, r.Top / 2, (r.Right + 1) / 2, (r.Bottom + 1) / 2);
                r.Intersect(levels[i].Bounds);
                if (r.Width <= 0 || r.Height <= 0) break;
                Downsample(src, levels[i], r);
                src = levels[i];
            }
        }

        /// <summary>Fills part of a half-size surface with the average of each 2×2 block of the source.</summary>
        public static void Downsample(PicSurface src, PicSurface dst, Rectangle dstRect)
        {
            dstRect.Intersect(dst.Bounds);
            if (dstRect.Width <= 0 || dstRect.Height <= 0) return;
            int sw = src.Width, sh = src.Height;
            PicPar.Rows(dstRect.Top, dstRect.Bottom, dstRect.Width, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    int sy0 = Math.Min(sh - 1, y * 2), sy1 = Math.Min(sh - 1, y * 2 + 1);
                    uint* a = src.Row(sy0), b = src.Row(sy1);
                    uint* d = dst.Row(y);
                    for (int x = dstRect.Left; x < dstRect.Right; x++)
                    {
                        int x0 = x * 2, x1 = x0 + 1;
                        if (x1 >= sw) x1 = sw - 1;
                        if (x0 >= sw) x0 = sw - 1;
                        uint p0 = a[x0], p1 = a[x1], p2 = b[x0], p3 = b[x1];
                        uint rb = ((p0 & 0x00FF00FF) + (p1 & 0x00FF00FF) + (p2 & 0x00FF00FF) + (p3 & 0x00FF00FF) + 0x00020002) >> 2 & 0x00FF00FF;
                        uint ag = ((p0 >> 8 & 0x00FF00FF) + (p1 >> 8 & 0x00FF00FF) + (p2 >> 8 & 0x00FF00FF) + (p3 >> 8 & 0x00FF00FF) + 0x00020002) >> 2 & 0x00FF00FF;
                        d[x] = ag << 8 | rb;
                    }
                }
            });
        }

        void DisposeLevels()
        {
            foreach (var l in levels) l.Dispose();
            levels = new PicSurface[0];
        }

        public void Dispose() { DisposeLevels(); top = null; }
    }
}
