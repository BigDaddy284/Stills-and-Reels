using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace StillsAndReels
{
    /// <summary>The photo fixes of the Adjust panel. Every slider runs from -100 to 100 (0 changes nothing) unless noted.</summary>
    internal sealed class PicAdjustments
    {
        public int Exposure, Brightness, Contrast, Highlights, Shadows, Saturation, Vibrance, Temperature, Tint;
        public int Sharpen;      // 0..100
        public int Blur;         // 0..100
        public int Vignette;     // -100 (lighter corners) .. 100 (darker corners)
        public int Sepia;        // 0..100
        public int Fade;         // 0..100: lifts the blacks for a washed, film-like look
        public bool Grayscale, Invert;
        /// <summary>Tone curves made elsewhere (levels, curves, auto fix): 256 entries each, or null.</summary>
        public byte[] CurveR, CurveG, CurveB;

        public bool IsIdentity
        {
            get
            {
                return Exposure == 0 && Brightness == 0 && Contrast == 0 && Highlights == 0 && Shadows == 0 && Saturation == 0 && Vibrance == 0 &&
                       Temperature == 0 && Tint == 0 && Sharpen == 0 && Blur == 0 && Vignette == 0 && Sepia == 0 && Fade == 0 && !Grayscale && !Invert &&
                       CurveR == null && CurveG == null && CurveB == null;
            }
        }

        public PicAdjustments Clone() { return (PicAdjustments)MemberwiseClone(); }

        /// <summary>The one-click looks of the Filters list.</summary>
        public static readonly KeyValuePair<string, PicAdjustments>[] Looks =
        {
            new KeyValuePair<string, PicAdjustments>("Black and white", new PicAdjustments { Grayscale = true, Contrast = 8 }),
            new KeyValuePair<string, PicAdjustments>("Sepia", new PicAdjustments { Sepia = 100, Contrast = 5 }),
            new KeyValuePair<string, PicAdjustments>("Vivid", new PicAdjustments { Vibrance = 45, Contrast = 14, Saturation = 8 }),
            new KeyValuePair<string, PicAdjustments>("Warm", new PicAdjustments { Temperature = 30, Saturation = 6 }),
            new KeyValuePair<string, PicAdjustments>("Cool", new PicAdjustments { Temperature = -30, Saturation = 4 }),
            new KeyValuePair<string, PicAdjustments>("Faded", new PicAdjustments { Fade = 55, Saturation = -18, Contrast = -6 }),
            new KeyValuePair<string, PicAdjustments>("Dramatic", new PicAdjustments { Contrast = 38, Shadows = -18, Highlights = -14, Vibrance = 12, Vignette = 28 }),
            new KeyValuePair<string, PicAdjustments>("Soft focus", new PicAdjustments { Blur = 10, Brightness = 4 }),
            new KeyValuePair<string, PicAdjustments>("Sharpen", new PicAdjustments { Sharpen = 55 }),
            new KeyValuePair<string, PicAdjustments>("Vignette", new PicAdjustments { Vignette = 55 }),
            new KeyValuePair<string, PicAdjustments>("Invert colours", new PicAdjustments { Invert = true }),
        };
    }

    /// <summary>Levels: where black and white sit, and the midtone brightness. Input and output values are 0..255.</summary>
    internal sealed class PicLevels
    {
        public int InBlack = 0, InWhite = 255, OutBlack = 0, OutWhite = 255;
        public float Gamma = 1f;         // 0.1 .. 9.99, above 1 brightens the midtones

        public bool IsIdentity { get { return InBlack == 0 && InWhite == 255 && OutBlack == 0 && OutWhite == 255 && Math.Abs(Gamma - 1f) < 0.005f; } }

        public byte[] ToCurve()
        {
            var lut = new byte[256];
            float range = Math.Max(1, InWhite - InBlack);
            double inv = 1.0 / Math.Max(0.1, Gamma);
            for (int i = 0; i < 256; i++)
            {
                double v = Math.Max(0.0, Math.Min(1.0, (i - InBlack) / range));
                v = Math.Pow(v, inv);
                lut[i] = PicAdjust.Clamp(OutBlack + v * (OutWhite - OutBlack));
            }
            return lut;
        }
    }

    /// <summary>Counts of each brightness in a picture, for the histogram display and for auto fix.</summary>
    internal sealed class PicHistogram
    {
        public readonly int[] R = new int[256], G = new int[256], B = new int[256], Luma = new int[256];
        public long Count;

        public int Max(int[] channel)
        {
            int m = 1;
            for (int i = 0; i < 256; i++) if (channel[i] > m) m = channel[i];
            return m;
        }

        /// <summary>The level below which a fraction of the pixels lie.</summary>
        public int Percentile(int[] channel, double fraction)
        {
            long target = (long)(Count * fraction), sum = 0;
            for (int i = 0; i < 256; i++) { sum += channel[i]; if (sum > target) return i; }
            return 255;
        }
    }

    /// <summary>Photo fixes on a layer's pixels: tone, colour, sharpness, looks, levels, curves and free rotation.</summary>
    internal static unsafe class PicAdjust
    {
        public static byte Clamp(double v) { return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5); }

        // ------------------------------------------------------------------ histogram, auto fix

        /// <summary>Counts brightness levels over (at most about a million of) the picture's pixels.</summary>
        public static PicHistogram Histogram(PicSurface s, PicSelection sel = null, int ox = 0, int oy = 0)
        {
            var h = new PicHistogram();
            int step = Math.Max(1, (int)Math.Sqrt((double)s.Width * s.Height / 1000000.0));
            for (int y = 0; y < s.Height; y += step)
            {
                uint* p = s.Row(y);
                for (int x = 0; x < s.Width; x += step)
                {
                    uint v = p[x];
                    uint a = v >> 24;
                    if (a == 0) continue;
                    if (sel != null && sel.At(x + ox, y + oy) < 128) continue;
                    if (a != 255) v = PicSurface.Unpremultiply(v);
                    int r = (int)((v >> 16) & 255), g = (int)((v >> 8) & 255), b = (int)(v & 255);
                    h.R[r]++; h.G[g]++; h.B[b]++;
                    h.Luma[(r * 54 + g * 183 + b * 19) >> 8]++;
                    h.Count++;
                }
            }
            return h;
        }

        /// <summary>
        /// Auto fix: stretches the picture so its darkest parts are black and its brightest white, and brings the
        /// midtones toward the middle. One curve for all three colours, so colours don't shift.
        /// </summary>
        public static PicLevels AutoLevels(PicHistogram h)
        {
            var lv = new PicLevels();
            if (h.Count < 16) return lv;
            int lo = h.Percentile(h.Luma, 0.004), hi = h.Percentile(h.Luma, 0.996);
            if (hi - lo < 24) { int mid = (lo + hi) / 2; lo = Math.Max(0, mid - 12); hi = Math.Min(255, mid + 12); }
            // a gentle fix: a flat, low-contrast picture is opened up, never stretched to pure black and white
            lo = Math.Min(lo, 60); hi = Math.Max(hi, 195);
            lv.InBlack = lo; lv.InWhite = hi;
            // where the median lands after the stretch, and the gamma that moves it part of the way to the middle
            double median = (h.Percentile(h.Luma, 0.5) - lo) / (double)Math.Max(1, hi - lo);
            median = Math.Max(0.05, Math.Min(0.95, median));
            double target = median + (0.46 - median) * 0.5;
            double gamma = Math.Log(median) / Math.Log(target);
            lv.Gamma = (float)Math.Max(0.6, Math.Min(1.7, gamma));
            return lv;
        }

        /// <summary>Auto white balance (grey world): gains for red and blue that make the average colour neutral.</summary>
        public static void AutoWhiteBalance(PicHistogram h, out byte[] r, out byte[] g, out byte[] b)
        {
            double sr = 0, sg = 0, sb = 0;
            for (int i = 0; i < 256; i++) { sr += (double)i * h.R[i]; sg += (double)i * h.G[i]; sb += (double)i * h.B[i]; }
            double n = Math.Max(1, h.Count);
            sr /= n; sg /= n; sb /= n;
            double grey = (sr + sg + sb) / 3.0;
            double kr = Math.Max(0.6, Math.Min(1.6, grey / Math.Max(1, sr))), kg = Math.Max(0.6, Math.Min(1.6, grey / Math.Max(1, sg))), kb = Math.Max(0.6, Math.Min(1.6, grey / Math.Max(1, sb)));
            r = new byte[256]; g = new byte[256]; b = new byte[256];
            for (int i = 0; i < 256; i++) { r[i] = Clamp(i * kr); g[i] = Clamp(i * kg); b[i] = Clamp(i * kb); }
        }

        // ------------------------------------------------------------------ curves

        /// <summary>
        /// A smooth tone curve through control points (x and y 0..255, x rising), as a 256-entry table.
        /// Never overshoots between points.
        /// </summary>
        public static byte[] CurveFromPoints(IList<PointF> pts)
        {
            var lut = new byte[256];
            int n = pts.Count;
            if (n == 0) { for (int i = 0; i < 256; i++) lut[i] = (byte)i; return lut; }
            if (n == 1) { for (int i = 0; i < 256; i++) lut[i] = Clamp(pts[0].Y); return lut; }
            // monotone cubic (Fritsch-Carlson)
            var d = new double[n - 1];
            var m = new double[n];
            for (int i = 0; i < n - 1; i++) d[i] = (pts[i + 1].Y - pts[i].Y) / Math.Max(0.0001, pts[i + 1].X - pts[i].X);
            m[0] = d[0]; m[n - 1] = d[n - 2];
            for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
            for (int i = 0; i < n - 1; i++)
            {
                if (Math.Abs(d[i]) < 1e-9) { m[i] = 0; m[i + 1] = 0; continue; }
                double a = m[i] / d[i], b = m[i + 1] / d[i];
                double s = a * a + b * b;
                if (s > 9) { double t = 3 / Math.Sqrt(s); m[i] = t * a * d[i]; m[i + 1] = t * b * d[i]; }
            }
            int seg = 0;
            for (int x = 0; x < 256; x++)
            {
                if (x <= pts[0].X) { lut[x] = Clamp(pts[0].Y); continue; }
                if (x >= pts[n - 1].X) { lut[x] = Clamp(pts[n - 1].Y); continue; }
                while (seg < n - 2 && x > pts[seg + 1].X) seg++;
                double h = pts[seg + 1].X - pts[seg].X, t = (x - pts[seg].X) / h;
                double t2 = t * t, t3 = t2 * t;
                double y = (2 * t3 - 3 * t2 + 1) * pts[seg].Y + (t3 - 2 * t2 + t) * h * m[seg] + (-2 * t3 + 3 * t2) * pts[seg + 1].Y + (t3 - t2) * h * m[seg + 1];
                lut[x] = Clamp(y);
            }
            return lut;
        }

        static byte[] Compose(byte[] first, byte[] then)
        {
            if (first == null) return then;
            if (then == null) return first;
            var r = new byte[256];
            for (int i = 0; i < 256; i++) r[i] = then[first[i]];
            return r;
        }

        /// <summary>The tone and white-balance sliders as one lookup table per colour.</summary>
        public static void BuildCurves(PicAdjustments a, out byte[] r, out byte[] g, out byte[] b)
        {
            r = new byte[256]; g = new byte[256]; b = new byte[256];
            double exposure = Math.Pow(2.0, a.Exposure / 50.0);              // +-2 stops
            double brightness = a.Brightness / 100.0 * 0.5;                  // midtone lift
            double contrast = a.Contrast >= 0 ? 1 + a.Contrast / 100.0 * 1.5 : 1 + a.Contrast / 100.0 * 0.75;
            double hi = a.Highlights / 100.0, sh = a.Shadows / 100.0;
            double fade = a.Fade / 100.0;
            // white balance: warm adds red and takes blue; tint moves between green and magenta
            double t = a.Temperature / 100.0, ti = a.Tint / 100.0;
            double kr = 1 + 0.30 * t + 0.10 * ti, kg = 1 - 0.18 * ti, kb = 1 - 0.30 * t + 0.10 * ti;
            for (int i = 0; i < 256; i++)
            {
                double v = i / 255.0;
                v *= exposure;
                if (v > 1) v = 1;
                if (brightness != 0)
                {
                    // a gamma-style lift keeps black black and white white
                    double gma = brightness >= 0 ? 1.0 / (1.0 + brightness * 2.0) : 1.0 - brightness * 2.0;
                    v = Math.Pow(v, gma);
                }
                if (sh != 0)
                {
                    double w = (1 - v) * (1 - v) * (1 - v);                  // strongest in the darks
                    v += sh * 0.5 * w * (sh > 0 ? (1 - v) * v * 4 + v * 0.3 + 0.15 : v);
                }
                if (hi != 0)
                {
                    double w = v * v * v;                                     // strongest in the lights
                    v += hi * 0.5 * w * (hi < 0 ? v : (1 - v) * 2 + 0.15);
                }
                if (a.Contrast != 0)
                {
                    // an S-curve around the middle that never clips
                    double x = Math.Max(0, Math.Min(1, v));
                    double s = x < 0.5 ? 0.5 * Math.Pow(2 * x, contrast) : 1 - 0.5 * Math.Pow(2 * (1 - x), contrast);
                    v = s;
                }
                if (fade > 0) v = v * (1 - fade * 0.22) + fade * 0.16;
                v = Math.Max(0, Math.Min(1, v));
                r[i] = Clamp(v * kr * 255); g[i] = Clamp(v * kg * 255); b[i] = Clamp(v * kb * 255);
            }
            r = Compose(a.CurveR, r); g = Compose(a.CurveG, g); b = Compose(a.CurveB, b);
            if (a.Invert) for (int i = 0; i < 256; i++) { r[i] = (byte)(255 - r[i]); g[i] = (byte)(255 - g[i]); b[i] = (byte)(255 - b[i]); }
        }

        // ------------------------------------------------------------------ applying

        /// <summary>
        /// Writes the adjusted version of src into dst (same size; may be the same surface). A selection, if any,
        /// limits the change; (ox, oy) is where the surfaces sit on the canvas.
        /// </summary>
        public static void Apply(PicSurface src, PicSurface dst, PicAdjustments a, PicSelection sel = null, int ox = 0, int oy = 0)
        {
            if (src.Size != dst.Size) throw new ArgumentException("Sizes differ.");
            var area = src.Bounds;
            if (sel != null)
            {
                if (!ReferenceEquals(src, dst)) dst.CopyFrom(src, src.Bounds, Point.Empty);
                area.Intersect(new Rectangle(sel.Bounds.X - ox, sel.Bounds.Y - oy, sel.Bounds.Width, sel.Bounds.Height));
                if (area.Width < 1 || area.Height < 1) return;
            }
            if (a.IsIdentity) { if (!ReferenceEquals(src, dst) && sel == null) dst.CopyFrom(src, src.Bounds, Point.Empty); return; }

            PicSurface from = src;
            PicSurface soft = null;
            try
            {
                // blur and sharpen work on neighbours, so they come first, on a copy
                if (a.Blur > 0 || a.Sharpen > 0)
                {
                    soft = src.Clone();
                    float scale = Math.Max(1f, Math.Max(src.Width, src.Height) / 2000f);
                    if (a.Blur > 0) PicFilters.GaussianBlur(soft, area, a.Blur / 100f * 12f * scale);
                    if (a.Sharpen > 0) PicFilters.Sharpen(soft, area, a.Sharpen / 100f * 1.6f, 1.1f * scale);
                    from = soft;
                }
                byte[] lr, lg, lb;
                BuildCurves(a, out lr, out lg, out lb);
                int sat = (int)Math.Round((1 + a.Saturation / 100.0) * 256);
                int vib = (int)Math.Round(a.Vibrance / 100.0 * 256);
                bool gray = a.Grayscale;
                int sepia = (int)Math.Round(a.Sepia / 100.0 * 256);
                bool colour = sat != 256 || vib != 0 || gray || sepia != 0;
                int vig = (int)Math.Round(a.Vignette / 100.0 * 256);
                // the vignette is measured on the whole canvas-sized layer, so a selection doesn't move its centre
                float cx = src.Width / 2f, cy = src.Height / 2f;
                float invR2 = 1f / (cx * cx + cy * cy);
                PicSurface s0 = from;
                var maskBytes = sel != null ? sel.Mask : null;

                PicPar.Rows(area.Top, area.Bottom, area.Width, (y0, y1) =>
                {
                    for (int y = y0; y < y1; y++)
                    {
                        uint* sp = s0.Row(y);
                        uint* dp = dst.Row(y);
                        uint* op = src.Row(y);
                        int mi = sel != null ? sel.RowStart(y + oy) - sel.Bounds.X + ox : 0;
                        float dy = y + 0.5f - cy, dy2 = dy * dy;
                        for (int x = area.Left; x < area.Right; x++)
                        {
                            int m = 255;
                            if (maskBytes != null) { m = maskBytes[mi + x]; if (m == 0) continue; }
                            uint v = sp[x];
                            uint al = v >> 24;
                            if (al == 0) { if (m == 255) dp[x] = v; continue; }
                            int r, g, b;
                            if (al == 255) { r = (int)((v >> 16) & 255); g = (int)((v >> 8) & 255); b = (int)(v & 255); }
                            else { r = (int)(((v >> 16) & 255) * 255 / al); g = (int)(((v >> 8) & 255) * 255 / al); b = (int)((v & 255) * 255 / al); if (r > 255) r = 255; if (g > 255) g = 255; if (b > 255) b = 255; }

                            r = lr[r]; g = lg[g]; b = lb[b];

                            if (colour)
                            {
                                int l = (r * 54 + g * 183 + b * 19) >> 8;
                                if (gray) { r = g = b = l; }
                                else
                                {
                                    int k = sat;
                                    if (vib != 0)
                                    {
                                        int mx = r > g ? (r > b ? r : b) : (g > b ? g : b), mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
                                        // dull colours get more of the boost than vivid ones
                                        k += vib * (255 - (mx - mn)) / 255;
                                        if (k < 0) k = 0;
                                    }
                                    if (k != 256)
                                    {
                                        r = l + ((r - l) * k >> 8); g = l + ((g - l) * k >> 8); b = l + ((b - l) * k >> 8);
                                        r = r < 0 ? 0 : r > 255 ? 255 : r; g = g < 0 ? 0 : g > 255 ? 255 : g; b = b < 0 ? 0 : b > 255 ? 255 : b;
                                    }
                                }
                                if (sepia != 0)
                                {
                                    int sr = l * 276 >> 8, sg = l * 244 >> 8, sb = l * 190 >> 8;
                                    if (sr > 255) sr = 255;
                                    r += (sr - r) * sepia >> 8; g += (sg - g) * sepia >> 8; b += (sb - b) * sepia >> 8;
                                }
                            }
                            if (vig != 0)
                            {
                                float dx = x + 0.5f - cx;
                                float d = (dx * dx + dy2) * invR2;       // 0 in the middle, 1 in the corners
                                float f = d * d * (vig / 256f) * 0.9f;
                                if (f > 0) { float kf = 1f - f; if (kf < 0) kf = 0; r = (int)(r * kf); g = (int)(g * kf); b = (int)(b * kf); }
                                else { r += (int)((255 - r) * -f); g += (int)((255 - g) * -f); b += (int)((255 - b) * -f); }
                            }

                            uint outp;
                            if (al == 255) outp = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
                            else outp = (al << 24) | ((uint)(r * al + 127) / 255 << 16) | ((uint)(g * al + 127) / 255 << 8) | (uint)(b * al + 127) / 255;
                            if (m == 255) dp[x] = outp;
                            else
                            {
                                // part-selected: mix between the old pixel and the new one
                                uint o = op[x];
                                uint im = (uint)(255 - m), um = (uint)m;
                                uint oa = ((o >> 24) * im + (outp >> 24) * um + 127) / 255;
                                uint or = (((o >> 16) & 255) * im + ((outp >> 16) & 255) * um + 127) / 255;
                                uint og = (((o >> 8) & 255) * im + ((outp >> 8) & 255) * um + 127) / 255;
                                uint ob = ((o & 255) * im + (outp & 255) * um + 127) / 255;
                                dp[x] = (oa << 24) | (or << 16) | (og << 8) | ob;
                            }
                        }
                    }
                });
            }
            finally { if (soft != null) soft.Dispose(); }
        }

        // ------------------------------------------------------------------ free rotation (straighten)

        /// <summary>
        /// The largest upright rectangle that fits inside a w × h rectangle turned by an angle, centred on it.
        /// </summary>
        public static SizeF LargestInside(float w, float h, double degrees)
        {
            double a = Math.Abs(degrees * Math.PI / 180.0);
            a %= Math.PI;
            if (a > Math.PI / 2) a = Math.PI - a;
            if (a < 1e-6) return new SizeF(w, h);
            bool wide = w >= h;
            double longSide = wide ? w : h, shortSide = wide ? h : w;
            double sin = Math.Sin(a), cos = Math.Cos(a);
            double rw, rh;
            if (shortSide <= 2.0 * sin * cos * longSide || Math.Abs(sin - cos) < 1e-10)
            {
                double x = 0.5 * shortSide;
                if (wide) { rw = x / sin; rh = x / cos; } else { rw = x / cos; rh = x / sin; }
            }
            else
            {
                double c2 = cos * cos - sin * sin;
                rw = (w * cos - h * sin) / c2; rh = (h * cos - w * sin) / c2;
            }
            return new SizeF((float)Math.Max(1, rw), (float)Math.Max(1, rh));
        }

        /// <summary>
        /// A canvas-sized picture turned about its middle by an angle (degrees, clockwise), then cropped to
        /// <paramref name="outSize"/> around the middle.
        /// </summary>
        public static PicSurface Rotated(PicSurface canvasSized, double degrees, Size outSize)
        {
            var d = new PicSurface(outSize.Width, outSize.Height);
            using (var g = d.CreateGraphics())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.CompositingMode = CompositingMode.SourceOver;
                g.TranslateTransform(outSize.Width / 2f, outSize.Height / 2f);
                g.RotateTransform((float)degrees);
                g.TranslateTransform(-canvasSized.Width / 2f, -canvasSized.Height / 2f);
                g.DrawImage(canvasSized.Bitmap, new Rectangle(0, 0, canvasSized.Width, canvasSized.Height), 0, 0, canvasSized.Width, canvasSized.Height, GraphicsUnit.Pixel);
            }
            return d;
        }

        /// <summary>The area left after trimming plain borders (see-through, or one flat colour) off a picture.</summary>
        public static Rectangle TrimBounds(PicSurface s, int tolerance = 10)
        {
            var content = s.ContentBounds();
            if (content.IsEmpty) return s.Bounds;
            if (content != s.Bounds) return content;
            uint edge = s.GetPixel(0, 0);
            int l = 0, t = 0, r = s.Width, b = s.Height;
            Func<int, bool> rowPlain = y => { uint* p = s.Row(y); for (int x = 0; x < s.Width; x++) if (Far(p[x], edge, tolerance)) return false; return true; };
            Func<int, int, int, bool> colPlain = (x, y0, y1) => { for (int y = y0; y < y1; y++) if (Far(s.Row(y)[x], edge, tolerance)) return false; return true; };
            while (t < b - 1 && rowPlain(t)) t++;
            if (t >= b - 1 && rowPlain(t)) return s.Bounds;   // one flat colour all over: nothing to trim to
            while (b > t + 1 && rowPlain(b - 1)) b--;
            while (l < r - 1 && colPlain(l, t, b)) l++;
            while (r > l + 1 && colPlain(r - 1, t, b)) r--;
            var found = Rectangle.FromLTRB(l, t, r, b);
            return found.Width < 1 || found.Height < 1 ? s.Bounds : found;
        }

        static bool Far(uint a, uint b, int tol)
        {
            return Math.Abs((int)(a & 255) - (int)(b & 255)) > tol || Math.Abs((int)((a >> 8) & 255) - (int)((b >> 8) & 255)) > tol ||
                   Math.Abs((int)((a >> 16) & 255) - (int)((b >> 16) & 255)) > tol || Math.Abs((int)(a >> 24) - (int)(b >> 24)) > tol;
        }
    }
}
