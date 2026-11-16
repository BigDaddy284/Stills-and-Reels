using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace StillsAndReels
{
    internal enum PicBlend { Normal, Multiply, Screen, Overlay, SoftLight, HardLight, Darken, Lighten, Difference, Add }

    /// <summary>
    /// One layer of a picture, as it was at one moment: its pixels (which may be smaller than the canvas and sit
    /// anywhere on it), the shapes and text drawn on top of them, and how it mixes with the layers below.
    /// A layer value never changes; an edit makes a new one that shares whatever it didn't touch.
    /// </summary>
    internal sealed class PicLayer
    {
        static int nextId = 1;
        static readonly PicObject[] NoObjects = new PicObject[0];

        public readonly int Id;
        public readonly string Name;
        public readonly bool Visible;
        public readonly float Opacity;       // 0 to 1
        public readonly PicBlend Blend;
        public readonly PicSurface Pixels;   // null: no pixels yet
        public readonly int X, Y;            // where the pixels sit on the canvas
        public readonly PicObject[] Objects;

        PicLayer(int id, string name, bool visible, float opacity, PicBlend blend, PicSurface pixels, int x, int y, PicObject[] objects)
        {
            Id = id; Name = name; Visible = visible; Opacity = Math.Max(0f, Math.Min(1f, opacity)); Blend = blend;
            Pixels = pixels; X = x; Y = y; Objects = objects ?? NoObjects;
        }

        public static PicLayer Create(string name, PicSurface pixels, int x = 0, int y = 0)
        {
            return new PicLayer(System.Threading.Interlocked.Increment(ref nextId), name, true, 1f, PicBlend.Normal, pixels, x, y, NoObjects);
        }

        public PicLayer WithPixels(PicSurface pixels, int x, int y) { return new PicLayer(Id, Name, Visible, Opacity, Blend, pixels, x, y, Objects); }
        public PicLayer WithObjects(PicObject[] objects) { return new PicLayer(Id, Name, Visible, Opacity, Blend, Pixels, X, Y, objects); }
        public PicLayer WithName(string name) { return new PicLayer(Id, name, Visible, Opacity, Blend, Pixels, X, Y, Objects); }
        public PicLayer WithVisible(bool v) { return new PicLayer(Id, Name, v, Opacity, Blend, Pixels, X, Y, Objects); }
        public PicLayer WithOpacity(float o) { return new PicLayer(Id, Name, Visible, o, Blend, Pixels, X, Y, Objects); }
        public PicLayer WithBlend(PicBlend b) { return new PicLayer(Id, Name, Visible, Opacity, b, Pixels, X, Y, Objects); }
        public PicLayer WithPosition(int x, int y) { return new PicLayer(Id, Name, Visible, Opacity, Blend, Pixels, x, y, Objects); }
        /// <summary>A copy with a new identity (duplicate layer). The pixels must already be a separate copy.</summary>
        public PicLayer AsNew(string name, PicSurface pixels) { return new PicLayer(System.Threading.Interlocked.Increment(ref nextId), name, Visible, Opacity, Blend, pixels, X, Y, Objects); }

        public Rectangle PixelBounds { get { return Pixels == null ? Rectangle.Empty : new Rectangle(X, Y, Pixels.Width, Pixels.Height); } }

        /// <summary>Everything the layer paints: its pixels and its objects.</summary>
        public Rectangle Bounds
        {
            get
            {
                var r = PixelBounds;
                foreach (var o in Objects) r = r.IsEmpty ? o.PixelBounds : Rectangle.Union(r, o.PixelBounds);
                return r;
            }
        }

        public bool HasObjects { get { return Objects.Length > 0; } }
    }

    /// <summary>A whole picture at one moment: the canvas size and the layers from bottom to top.</summary>
    internal sealed class PicState
    {
        public readonly Size Size;
        public readonly PicLayer[] Layers;

        public PicState(Size size, PicLayer[] layers)
        {
            Size = size; Layers = layers;
        }

        public Rectangle Bounds { get { return new Rectangle(Point.Empty, Size); } }

        public PicState WithLayer(int index, PicLayer layer)
        {
            var a = (PicLayer[])Layers.Clone();
            a[index] = layer;
            return new PicState(Size, a);
        }

        public PicState WithLayers(PicLayer[] layers) { return new PicState(Size, layers); }

        public int IndexOf(int layerId)
        {
            for (int i = 0; i < Layers.Length; i++) if (Layers[i].Id == layerId) return i;
            return -1;
        }

        public bool HasObjects
        {
            get { foreach (var l in Layers) if (l.HasObjects) return true; return false; }
        }

        /// <summary>The canvas area that looks different between two states (the whole canvas when unsure).</summary>
        public static Rectangle Diff(PicState a, PicState b)
        {
            if (a.Size != b.Size || a.Layers.Length != b.Layers.Length) return Rectangle.Union(a.Bounds, b.Bounds);
            var r = Rectangle.Empty;
            for (int i = 0; i < a.Layers.Length; i++)
            {
                PicLayer x = a.Layers[i], y = b.Layers[i];
                if (ReferenceEquals(x, y)) continue;
                if (x.Id != y.Id) return a.Bounds;
                if (x.Visible != y.Visible || x.Opacity != y.Opacity || x.Blend != y.Blend || !ReferenceEquals(x.Pixels, y.Pixels) || x.X != y.X || x.Y != y.Y)
                {
                    r = Union(r, Rectangle.Union(x.Bounds, y.Bounds));
                    continue;
                }
                if (!ReferenceEquals(x.Objects, y.Objects))
                {
                    var seen = new HashSet<PicObject>(x.Objects);
                    foreach (var o in y.Objects) if (!seen.Remove(o)) r = Union(r, o.PixelBounds);
                    foreach (var o in seen) r = Union(r, o.PixelBounds);
                    // a change of order alone repaints where the objects overlap; repainting them all is simplest
                    if (x.Objects.Length == y.Objects.Length && r.IsEmpty)
                        for (int k = 0; k < x.Objects.Length; k++)
                            if (!ReferenceEquals(x.Objects[k], y.Objects[k])) r = Union(r, x.Objects[k].PixelBounds);
                }
            }
            r.Intersect(a.Bounds);
            return r;
        }

        static Rectangle Union(Rectangle a, Rectangle b) { return a.IsEmpty ? b : b.IsEmpty ? a : Rectangle.Union(a, b); }
    }

    /// <summary>
    /// Keeps the finished picture (all visible layers mixed together) up to date, plus its half-size copies for
    /// fast zoomed-out drawing. Only the part of the canvas that changed is mixed again.
    /// </summary>
    internal sealed unsafe class PicCompositor : IDisposable
    {
        PicSurface own;          // the mixed picture, when it can't simply be the single layer's pixels
        PicSurface shown;        // what the canvas draws: own, or the layer's own pixels
        PicState state;
        readonly PicMips mips = new PicMips();
        bool mipsStale = true;

        // a stand-in for one layer's pixels while an adjustment is being previewed
        int previewLayerId;
        PicSurface previewPixels;

        // objects and one layer left out while they are being dragged (the canvas draws them itself meanwhile)
        HashSet<PicObject> hidden;
        int hiddenLayerId;

        /// <summary>The finished picture. Mixing the layers is put off until this is asked for.</summary>
        public PicSurface Picture { get { Flush(); return shown; } }
        public PicState State { get { return state; } }

        public PicMips Mips
        {
            get
            {
                Flush();
                if (mipsStale && shown != null) { mips.Build(shown); mipsStale = false; }
                return mips;
            }
        }

        /// <summary>True when the layer can be shown as it is, with no mixing and no extra copy in memory.</summary>
        bool CanAlias(PicState s)
        {
            if (s.Layers.Length != 1) return false;
            var l = s.Layers[0];
            var px = EffectivePixels(l);
            return l.Id != hiddenLayerId && l.Visible && l.Opacity >= 1f && l.Blend == PicBlend.Normal && !l.HasObjects && px != null &&
                   l.X == 0 && l.Y == 0 && px.Width == s.Size.Width && px.Height == s.Size.Height;
        }

        PicSurface EffectivePixels(PicLayer l) { return previewPixels != null && l.Id == previewLayerId ? previewPixels : l.Pixels; }

        bool pendingAll;        // the whole picture needs mixing again
        Rectangle pending;      // or just this part

        /// <summary>Shows a new state. <paramref name="dirty"/> is the canvas area that changed (null: everything).</summary>
        public void Update(PicState s, Rectangle? dirty)
        {
            bool sizeChanged = state == null || state.Size != s.Size;
            state = s;
            if (CanAlias(s))
            {
                var px = EffectivePixels(s.Layers[0]);
                if (own != null) { own.Dispose(); own = null; }
                if (!ReferenceEquals(shown, px) || sizeChanged || !dirty.HasValue) { shown = px; mipsStale = true; }
                else if (!mipsStale) mips.Update(dirty.Value);
                pendingAll = false; pending = Rectangle.Empty;
                return;
            }
            bool fresh = own == null || own.Width != s.Size.Width || own.Height != s.Size.Height;
            if (fresh)
            {
                if (own != null) own.Dispose();
                own = new PicSurface(s.Size.Width, s.Size.Height);
            }
            if (fresh || sizeChanged || !dirty.HasValue || !ReferenceEquals(shown, own)) pendingAll = true;
            else
            {
                var r = Rectangle.Intersect(dirty.Value, s.Bounds);
                if (r.Width > 0 && r.Height > 0) pending = pending.IsEmpty ? r : Rectangle.Union(pending, r);
            }
            shown = own;
        }

        /// <summary>Mixes whatever has changed since the picture was last asked for.</summary>
        void Flush()
        {
            if (state == null || own == null || !ReferenceEquals(shown, own)) return;
            if (!pendingAll && pending.IsEmpty) return;
            bool all = pendingAll;
            var r = all ? state.Bounds : pending;
            pendingAll = false; pending = Rectangle.Empty;
            Render(state, own, r, true);
            if (all) mipsStale = true; else if (!mipsStale) mips.Update(r);
        }

        public void SetPreview(int layerId, PicSurface pixels, Rectangle? dirty)
        {
            previewLayerId = layerId; previewPixels = pixels;
            if (state != null) Update(state, dirty);
        }

        public void ClearPreview()
        {
            if (previewPixels == null) return;
            previewPixels = null;
            if (state != null) Update(state, null);
        }

        public bool HasPreview { get { return previewPixels != null; } }

        /// <summary>True when the picture shown is exactly the state: no preview, nothing hidden for a drag in progress.</summary>
        public bool ShowsStateAsIs { get { return previewPixels == null && hidden == null && hiddenLayerId == 0; } }

        public void SetHidden(HashSet<PicObject> objects, Rectangle dirty)
        {
            hidden = objects != null && objects.Count > 0 ? objects : null;
            if (state != null) Update(state, dirty);
        }

        /// <summary>Leaves one layer out of the picture (0: none) while the canvas shows it being resized.</summary>
        public void HideLayer(int layerId)
        {
            if (hiddenLayerId == layerId) return;
            hiddenLayerId = layerId;
            if (state != null) Update(state, null);
        }

        /// <summary>Mixes the visible layers of a state into a rectangle of a canvas-sized surface.</summary>
        void Render(PicState s, PicSurface target, Rectangle r, bool clear)
        {
            if (clear) target.Fill(r, 0);
            PicSurface temp = null;
            try
            {
                foreach (var l in s.Layers)
                {
                    if (!l.Visible || l.Opacity <= 0f || l.Id == hiddenLayerId) continue;
                    var px = EffectivePixels(l);
                    bool plain = l.Opacity >= 1f && l.Blend == PicBlend.Normal;
                    if (!l.HasObjects)
                    {
                        if (px != null) PicBlendOps.Composite(target, r, px, l.X, l.Y, l.Opacity, l.Blend);
                        continue;
                    }
                    if (plain)
                    {
                        if (px != null) PicBlendOps.Composite(target, r, px, l.X, l.Y, 1f, PicBlend.Normal);
                        DrawObjects(target, l, r, 0, 0);
                        continue;
                    }
                    // a see-through or blended layer with shapes: draw pixels and shapes together first, then mix once
                    if (temp == null) temp = new PicSurface(r.Width, r.Height); else temp.Clear();
                    if (px != null) temp.CopyFrom(px, new Rectangle(r.X - l.X, r.Y - l.Y, r.Width, r.Height), Point.Empty);
                    DrawObjects(temp, l, r, r.X, r.Y);
                    PicBlendOps.Composite(target, r, temp, r.X, r.Y, l.Opacity, l.Blend);
                }
            }
            finally { if (temp != null) temp.Dispose(); }
        }

        void DrawObjects(PicSurface target, PicLayer l, Rectangle r, int originX, int originY)
        {
            using (var g = target.CreateGraphics())
            {
                g.SetClip(new Rectangle(r.X - originX, r.Y - originY, r.Width, r.Height));
                g.TranslateTransform(-originX, -originY);
                foreach (var o in l.Objects)
                {
                    if (hidden != null && hidden.Contains(o)) continue;
                    if (!o.PixelBounds.IntersectsWith(r)) continue;
                    o.Draw(g);
                }
            }
        }

        /// <summary>The finished picture of any state as a new surface (used for saving, copying and flattening).</summary>
        public static PicSurface Flatten(PicState s)
        {
            var target = new PicSurface(s.Size.Width, s.Size.Height);
            using (var c = new PicCompositor()) c.Render(s, target, s.Bounds, false);
            return target;
        }

        /// <summary>One layer alone (pixels and shapes at full strength) on a transparent canvas-sized surface.</summary>
        public static PicSurface RenderLayer(PicState s, PicLayer l)
        {
            var target = new PicSurface(s.Size.Width, s.Size.Height);
            if (l.Pixels != null) target.CopyFrom(l.Pixels, l.Pixels.Bounds, new Point(l.X, l.Y));
            if (l.HasObjects) using (var c = new PicCompositor()) c.DrawObjects(target, l, s.Bounds, 0, 0);
            return target;
        }

        public void Dispose()
        {
            mips.Dispose();
            if (own != null) { own.Dispose(); own = null; }
            shown = null;
        }
    }

    /// <summary>Mixing one surface onto another, pixel by pixel, in premultiplied colour.</summary>
    internal static unsafe class PicBlendOps
    {
        /// <summary>
        /// Mixes <paramref name="src"/> (placed at sx, sy on the canvas) onto <paramref name="dst"/> (canvas-sized)
        /// inside the canvas rectangle <paramref name="r"/>.
        /// </summary>
        public static void Composite(PicSurface dst, Rectangle r, PicSurface src, int sx, int sy, float opacity, PicBlend mode)
        {
            r.Intersect(dst.Bounds);
            r.Intersect(new Rectangle(sx, sy, src.Width, src.Height));
            if (r.Width <= 0 || r.Height <= 0) return;
            uint op = (uint)Math.Round(Math.Max(0f, Math.Min(1f, opacity)) * 256f);
            if (op == 0) return;
            int w = r.Width, left = r.Left;
            PicPar.Rows(r.Top, r.Bottom, w, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++)
                {
                    uint* d = dst.Row(y) + left;
                    uint* s = src.Row(y - sy) + (left - sx);
                    if (mode == PicBlend.Normal) NormalRow(d, s, w, op);
                    else BlendRow(d, s, w, op, mode);
                }
            });
        }

        static void NormalRow(uint* d, uint* s, int w, uint op)
        {
            for (int x = 0; x < w; x++)
            {
                uint sp = s[x];
                if (sp == 0) continue;
                if (op < 256)
                {
                    uint rb0 = (sp & 0x00FF00FF) * op >> 8 & 0x00FF00FF;
                    uint ag0 = (sp >> 8 & 0x00FF00FF) * op >> 8 & 0x00FF00FF;
                    sp = ag0 << 8 | rb0;
                    if (sp == 0) continue;
                }
                uint sa = sp >> 24;
                if (sa == 255) { d[x] = sp; continue; }
                uint dp = d[x];
                if (dp == 0) { d[x] = sp; continue; }
                uint inv = 255 - sa;
                uint rb = (dp & 0x00FF00FF) * inv + 0x00800080;
                rb = (rb + (rb >> 8 & 0x00FF00FF)) >> 8 & 0x00FF00FF;
                uint ag = (dp >> 8 & 0x00FF00FF) * inv + 0x00800080;
                ag = (ag + (ag >> 8 & 0x00FF00FF)) >> 8 & 0x00FF00FF;
                d[x] = sp + (ag << 8 | rb);
            }
        }

        static void BlendRow(uint* d, uint* s, int w, uint op, PicBlend mode)
        {
            for (int x = 0; x < w; x++)
            {
                uint sp = s[x];
                if (sp == 0) continue;
                uint sa = (sp >> 24) * op >> 8;
                if (sa == 0) continue;
                uint dp = d[x];
                uint da = dp >> 24;
                if (da == 0)
                {
                    // nothing underneath: the layer shows as it is
                    uint rb0 = (sp & 0x00FF00FF) * op >> 8 & 0x00FF00FF;
                    uint ag0 = (sp >> 8 & 0x00FF00FF) * op >> 8 & 0x00FF00FF;
                    d[x] = ag0 << 8 | rb0;
                    continue;
                }
                uint srcA = sp >> 24;
                // straight (not premultiplied) colours, 0..255
                uint sr = (sp >> 16 & 255) * 255 / srcA, sg = (sp >> 8 & 255) * 255 / srcA, sb = (sp & 255) * 255 / srcA;
                uint dr = (dp >> 16 & 255) * 255 / da, dg = (dp >> 8 & 255) * 255 / da, db = (dp & 255) * 255 / da;
                if (sr > 255) sr = 255; if (sg > 255) sg = 255; if (sb > 255) sb = 255;
                if (dr > 255) dr = 255; if (dg > 255) dg = 255; if (db > 255) db = 255;
                uint br = Mix(dr, sr, mode), bg = Mix(dg, sg, mode), bb = Mix(db, sb, mode);
                // result = (1 - sa)·dst + sa·((1 - da)·src + da·blend), all premultiplied
                uint outA = sa + da - Div255(sa * da);
                uint r = Channel(dr, sr, br, sa, da), g = Channel(dg, sg, bg, sa, da), b = Channel(db, sb, bb, sa, da);
                if (r > outA) r = outA; if (g > outA) g = outA; if (b > outA) b = outA;
                d[x] = outA << 24 | r << 16 | g << 8 | b;
            }
        }

        static uint Div255(uint v) { v += 128; return (v + (v >> 8)) >> 8; }

        static uint Channel(uint d, uint s, uint blend, uint sa, uint da)
        {
            // premultiplied output channel
            uint both = Div255(sa * da);              // where layer and backdrop overlap
            uint onlyS = sa - both, onlyD = da - both;
            return Div255(onlyD * d + onlyS * s + both * blend);
        }

        public static uint Mix(uint d, uint s, PicBlend mode)
        {
            switch (mode)
            {
                case PicBlend.Multiply: return Div255(d * s);
                case PicBlend.Screen: return 255 - Div255((255 - d) * (255 - s));
                case PicBlend.Overlay: return d < 128 ? Div255(2 * d * s) : 255 - Div255(2 * (255 - d) * (255 - s));
                case PicBlend.HardLight: return s < 128 ? Div255(2 * d * s) : 255 - Div255(2 * (255 - d) * (255 - s));
                case PicBlend.SoftLight:
                    {
                        // the W3C soft-light formula
                        float cb = d / 255f, cs = s / 255f, res;
                        if (cs <= 0.5f) res = cb - (1 - 2 * cs) * cb * (1 - cb);
                        else
                        {
                            float dd = cb <= 0.25f ? ((16 * cb - 12) * cb + 4) * cb : (float)Math.Sqrt(cb);
                            res = cb + (2 * cs - 1) * (dd - cb);
                        }
                        return (uint)Math.Max(0, Math.Min(255, (int)Math.Round(res * 255f)));
                    }
                case PicBlend.Darken: return Math.Min(d, s);
                case PicBlend.Lighten: return Math.Max(d, s);
                case PicBlend.Difference: return d > s ? d - s : s - d;
                case PicBlend.Add: return Math.Min(255, d + s);
                default: return s;
            }
        }

        public static string Name(PicBlend b)
        {
            switch (b)
            {
                case PicBlend.SoftLight: return "Soft light";
                case PicBlend.HardLight: return "Hard light";
                default: return b.ToString();
            }
        }
    }
}
