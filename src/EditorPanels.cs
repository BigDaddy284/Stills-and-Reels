using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

namespace StillsAndReels
{
    // =========================================================================== small building blocks

    /// <summary>Icon-over-label tabs (Media, Text, Transitions, Filters, Effects).</summary>
    internal sealed class TabStrip : Control
    {
        public sealed class Tab { public string Id, Label; public Ico Icon; public RectangleF Bounds; }
        readonly List<Tab> tabs = new List<Tab>();
        string selected, hover;
        public event Action<string> TabChanged;

        public TabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(58);
            Font = Theme.UiFont(8.5f);
        }

        public void Add(string id, string label, Ico icon) { tabs.Add(new Tab { Id = id, Label = label, Icon = icon }); if (selected == null) selected = id; Invalidate(); }

        public string Selected
        {
            get { return selected; }
            set { if (selected == value) return; selected = value; Invalidate(); var h = TabChanged; if (h != null) h(value); }
        }

        void LayoutTabs()
        {
            float w = (Width - Theme.F(8)) / Math.Max(1, tabs.Count);
            for (int i = 0; i < tabs.Count; i++) tabs[i].Bounds = new RectangleF(Theme.F(4) + i * w, Theme.F(4), w, Height - Theme.F(8));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            LayoutTabs();
            var t = tabs.Find(x => x.Bounds.Contains(e.Location));
            string id = t != null ? t.Id : null;
            if (id != hover) { hover = id; Cursor = id != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            LayoutTabs();
            var t = tabs.Find(x => x.Bounds.Contains(e.Location));
            if (t != null) Selected = t.Id;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(p.Bar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(p.BarBorder)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            LayoutTabs();
            foreach (var t in tabs)
            {
                bool sel = t.Id == selected;
                var r = RectangleF.Inflate(t.Bounds, -Theme.F(2), 0);
                if (sel || t.Id == hover)
                    using (var path = Theme.Rounded(r, Theme.F(8)))
                    using (var b = new SolidBrush(sel ? Color.FromArgb(p.Dark ? 60 : 36, p.AccentA) : p.Hover)) g.FillPath(b, path);
                float ic = Theme.F(20);
                var ir = new RectangleF(r.Left + (r.Width - ic) / 2, r.Top + Theme.F(5), ic, ic);
                if (sel) Icons.DrawGradient(g, t.Icon, ir, p.AccentA, p.AccentB, 1.8f);
                else Icons.Draw(g, t.Icon, ir, p.SubText, 1.7f);
                TextRenderer.DrawText(g, t.Label, Font, Rectangle.Round(new RectangleF(r.Left, ir.Bottom + Theme.F(2), r.Width, Theme.F(16))), sel ? p.Text : p.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                if (sel)
                    using (var b = Theme.AccentGradient(new RectangleF(r.Left + r.Width * 0.3f, Height - Theme.F(3), r.Width * 0.4f, Theme.F(3))))
                        g.FillRectangle(b, r.Left + r.Width * 0.3f, Height - Theme.F(3), r.Width * 0.4f, Theme.F(3));
            }
        }
    }

    internal sealed class Tile
    {
        public string Key, Title, Sub, Badge;
        public Bitmap Image;               // drawn covering the picture area
        public object ImageLock;           // lock taken while drawing Image
        public Action<Graphics, RectangleF> Paint;
        public Ico Icon = Ico.None;
        public double Progress = -1;       // 0..1 shows a bar
        public bool Selected, Warn, Used;
        public object Tag;
        public RectangleF Bounds;
    }

    /// <summary>Scrollable grid of picture tiles (media bin, text styles, transitions, filters, effects).</summary>
    internal sealed class TileGrid : Control
    {
        readonly List<Tile> tiles = new List<Tile>();
        int scroll;
        Tile hover, pressed;
        Point downAt;
        bool hoverAdd;
        public int MinTileWidth = 116;
        public bool ShowAddButton;
        public string EmptyText = "";
        public float PictureAspect = 9f / 16f;

        public event Action<Tile> TileClicked, TileDoubleClicked, TileAddClicked, TileDragStarted;
        public event Action<Tile, Point> TileRightClicked;

        public TileGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Font = Theme.UiFont(8.5f);
        }

        public List<Tile> Tiles { get { return tiles; } }

        public void SetTiles(IEnumerable<Tile> list)
        {
            tiles.Clear();
            tiles.AddRange(list);
            if (hover != null && !tiles.Contains(hover)) hover = null;
            ClampScroll();
            Invalidate();
        }

        int Columns { get { return Math.Max(1, (Width - Theme.S(16)) / Theme.S(MinTileWidth)); } }
        float TileW { get { return (Width - Theme.F(16) - (Columns - 1) * Theme.F(8)) / Columns; } }
        float PicH { get { return TileW * PictureAspect; } }
        float TileH { get { return PicH + Theme.F(36); } }

        int ContentHeight
        {
            get
            {
                int rows = (tiles.Count + Columns - 1) / Columns;
                return (int)(rows * (TileH + Theme.F(8)) + Theme.F(12));
            }
        }

        void ClampScroll() { scroll = Math.Max(0, Math.Min(scroll, ContentHeight - Height)); }

        void LayoutTiles()
        {
            int cols = Columns;
            float tw = TileW, th = TileH;
            for (int i = 0; i < tiles.Count; i++)
            {
                int c = i % cols, r = i / cols;
                tiles[i].Bounds = new RectangleF(Theme.F(8) + c * (tw + Theme.F(8)), Theme.F(6) + r * (th + Theme.F(8)) - scroll, tw, th);
            }
        }

        Tile HitTest(Point p)
        {
            LayoutTiles();
            foreach (var t in tiles) if (t.Bounds.Contains(p)) return t;
            return null;
        }

        RectangleF AddRect(Tile t)
        {
            float s = Theme.F(24);
            return new RectangleF(t.Bounds.Right - s - Theme.F(5), t.Bounds.Top + PicH - s - Theme.F(5), s, s);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); ClampScroll(); Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll -= Math.Sign(e.Delta) * Theme.S(60);
            ClampScroll();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var t = HitTest(e.Location);
            bool add = t != null && ShowAddButton && AddRect(t).Contains(e.Location);
            if (t != hover || add != hoverAdd)
            {
                hover = t; hoverAdd = add;
                Cursor = t != null ? Cursors.Hand : Cursors.Default;
                Ui.Tip.SetToolTip(this, t == null ? null : add ? "Add to the timeline at the playhead" : (t.Title + (string.IsNullOrEmpty(t.Sub) ? "" : "\n" + t.Sub)));
                Invalidate();
            }
            if (pressed != null && e.Button == MouseButtons.Left && Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) > Theme.S(6))
            {
                var p = pressed;
                pressed = null;
                var h = TileDragStarted;
                if (h != null) h(p);
            }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            var t = HitTest(e.Location);
            downAt = e.Location;
            if (e.Button == MouseButtons.Left) pressed = t;
            if (e.Button == MouseButtons.Right && t != null) { var h = TileRightClicked; if (h != null) h(t, PointToScreen(e.Location)); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var t = HitTest(e.Location);
            if (pressed != null && t == pressed && e.Button == MouseButtons.Left)
            {
                if (ShowAddButton && AddRect(t).Contains(e.Location)) { var h = TileAddClicked; if (h != null) h(t); }
                else { var h = TileClicked; if (h != null) h(t); }
            }
            pressed = null;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var t = HitTest(e.Location);
            if (t != null && !(ShowAddButton && AddRect(t).Contains(e.Location))) { var h = TileDoubleClicked; if (h != null) h(t); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(Parent != null ? Parent.BackColor : p.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            LayoutTiles();
            if (tiles.Count == 0 && !string.IsNullOrEmpty(EmptyText))
            {
                TextRenderer.DrawText(g, EmptyText, Theme.UiFont(9.5f), new Rectangle(Theme.S(16), Theme.S(16), Width - Theme.S(32), Height - Theme.S(32)), p.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
            foreach (var t in tiles)
            {
                var b = t.Bounds;
                if (b.Bottom < 0 || b.Top > Height) continue;
                var pic = new RectangleF(b.Left, b.Top, b.Width, PicH);
                using (var path = Theme.Rounded(pic, Theme.F(8)))
                {
                    using (var br = new SolidBrush(p.Dark ? Color.FromArgb(22, 28, 52) : Color.FromArgb(226, 231, 244))) g.FillPath(br, path);
                    var st = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    if (t.Image != null)
                    {
                        try
                        {
                            if (t.ImageLock != null) System.Threading.Monitor.Enter(t.ImageLock);
                            float k = Math.Max(pic.Width / t.Image.Width, pic.Height / t.Image.Height);
                            float w = t.Image.Width * k, h = t.Image.Height * k;
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.DrawImage(t.Image, pic.Left + (pic.Width - w) / 2, pic.Top + (pic.Height - h) / 2, w, h);
                        }
                        catch { }
                        finally { if (t.ImageLock != null) System.Threading.Monitor.Exit(t.ImageLock); }
                    }
                    if (t.Paint != null) t.Paint(g, pic);
                    if (t.Image == null && t.Paint == null && t.Icon != Ico.None)
                    {
                        float ic = Math.Min(pic.Height * 0.5f, Theme.F(34));
                        Icons.DrawGradient(g, t.Icon, new RectangleF(pic.Left + (pic.Width - ic) / 2, pic.Top + (pic.Height - ic) / 2, ic, ic), p.AccentA, p.AccentB, 1.7f);
                    }
                    if (t.Progress >= 0 && t.Progress < 1)
                    {
                        var pr = new RectangleF(pic.Left + Theme.F(8), pic.Bottom - Theme.F(10), pic.Width - Theme.F(16), Theme.F(4));
                        using (var bb = new SolidBrush(Color.FromArgb(160, 0, 0, 0))) g.FillRectangle(bb, pr);
                        if (t.Progress > 0) using (var bb = Theme.AccentGradient(pr)) g.FillRectangle(bb, pr.Left, pr.Top, (float)(pr.Width * t.Progress), pr.Height);
                    }
                    if (!string.IsNullOrEmpty(t.Badge))
                    {
                        var f = Theme.UiFont(8f, FontStyle.Bold);
                        var sz = TextRenderer.MeasureText(t.Badge, f);
                        var br2 = new RectangleF(pic.Right - sz.Width - Theme.F(10), pic.Top + Theme.F(5), sz.Width + Theme.F(6), sz.Height + Theme.F(2));
                        using (var bp = Theme.Rounded(br2, Theme.F(4))) using (var bb = new SolidBrush(Color.FromArgb(180, 0, 0, 0))) g.FillPath(bb, bp);
                        TextRenderer.DrawText(g, t.Badge, f, Rectangle.Round(br2), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                    if (t.Used)
                    {
                        var ur = new RectangleF(pic.Left + Theme.F(6), pic.Top + Theme.F(6), Theme.F(16), Theme.F(16));
                        using (var bb = Theme.AccentGradient(ur)) g.FillEllipse(bb, ur);
                        Icons.Draw(g, Ico.Check, RectangleF.Inflate(ur, -Theme.F(2), -Theme.F(2)), Color.White, 2.4f);
                    }
                    if (hover == t)
                    {
                        using (var bb = new SolidBrush(Color.FromArgb(28, 255, 255, 255))) g.FillPath(bb, path);
                        if (ShowAddButton)
                        {
                            var ar = AddRect(t);
                            using (var bb = Theme.AccentGradient(ar)) g.FillEllipse(bb, ar);
                            if (hoverAdd) using (var pen = new Pen(Color.White, Theme.F(1.5f))) g.DrawEllipse(pen, ar);
                            Icons.Draw(g, Ico.Plus, RectangleF.Inflate(ar, -Theme.F(4), -Theme.F(4)), Color.White, 2.4f);
                        }
                    }
                    g.Restore(st);
                    if (t.Selected)
                        using (var bb = Theme.AccentGradient(pic)) using (var pen = new Pen(bb, Theme.F(2.2f))) g.DrawPath(pen, path);
                    else if (t.Warn)
                        using (var pen = new Pen(Color.FromArgb(220, 255, 120, 90), Theme.F(1.6f))) g.DrawPath(pen, path);
                }
                var tr = new Rectangle((int)b.Left + 2, (int)(pic.Bottom + Theme.F(3)), (int)b.Width - 4, Theme.S(16));
                TextRenderer.DrawText(g, t.Title ?? "", Font, tr, t.Warn ? p.Warning : p.Text,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                if (!string.IsNullOrEmpty(t.Sub))
                    TextRenderer.DrawText(g, t.Sub, Theme.UiFont(8f), new Rectangle(tr.Left, tr.Bottom - 1, tr.Width, Theme.S(15)), t.Warn ? p.Warning : p.Muted,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            // scroll indicator
            int ch = ContentHeight;
            if (ch > Height)
            {
                float h = Math.Max(Theme.F(24), Height * (float)Height / ch);
                float y = (Height - h) * scroll / (float)(ch - Height);
                using (var path = Theme.Rounded(new RectangleF(Width - Theme.F(5), y, Theme.F(3), h), Theme.F(1.5f)))
                using (var bb = new SolidBrush(p.ControlBorder)) g.FillPath(bb, path);
            }
        }
    }

    /// <summary>Vertical stack of controls with a scroll bar (properties panel and side panels).</summary>
    internal class StackPanel : ThemedPanel
    {
        public int Pad = 14, Gap = 6;

        public StackPanel()
        {
            AutoScroll = true;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Theme.S(Pad), gap = Theme.S(Gap);
            // always leave room for the vertical scroll bar so a horizontal one never appears
            int w = Width - pad * 2 - SystemInformation.VerticalScrollBarWidth + Theme.S(4);
            int y = Theme.S(10) + AutoScrollPosition.Y;
            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                int h = c.Height;
                var hl = c as HintLabel;
                if (hl != null) h = hl.MeasureHeight(w);
                if (c is SectionLabel) { y += Theme.S(8); h = Theme.S(20); }
                var row = c as ButtonRow;
                if (row != null) h = row.Height;
                c.SetBounds(pad, y, w, h);
                y += h + gap;
            }
            var min = new Size(0, y - AutoScrollPosition.Y + Theme.S(12));
            if (AutoScrollMinSize != min) AutoScrollMinSize = min;
        }
    }

    /// <summary>Several controls side by side with equal widths.</summary>
    internal sealed class ButtonRow : Control
    {
        public ButtonRow(params Control[] items)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Height = Theme.S(34);
            foreach (var c in items) Controls.Add(c);
        }

        protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Current.Panel); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var vis = new List<Control>();
            foreach (Control c in Controls) if (c.Visible) vis.Add(c);
            if (vis.Count == 0) return;
            int gap = Theme.S(6);
            // icon-only buttons stay square; the others share the rest
            int fixedW = 0, flex = 0;
            foreach (var c in vis) { var tb = c as ThemedButton; if (tb != null && tb.IconOnly) fixedW += Height + Theme.S(4); else flex++; }
            if (flex == 0) { fixedW = 0; flex = vis.Count; }
            int w = Math.Max(10, (Width - gap * (vis.Count - 1) - fixedW) / flex);
            int x = 0;
            for (int i = 0; i < vis.Count; i++)
            {
                var tb = vis[i] as ThemedButton;
                int cw = tb != null && tb.IconOnly && fixedW > 0 ? Height + Theme.S(4) : w;
                if (i == vis.Count - 1) cw = Width - x;
                vis[i].SetBounds(x, 0, cw, Height);
                x += cw + gap;
            }
        }
    }

    /// <summary>A labelled slider for one property, with live changes, a value read-out and an optional keyframe button.</summary>
    internal sealed class PropSlider : Control
    {
        double min, max, value, def;
        bool dragging, hover, keyHover, movedInDrag;
        public Func<double, string> Format = v => v.ToString("0", CultureInfo.InvariantCulture);
        public Func<double> Getter;
        public bool Keyable;
        public bool KeyActive;      // a keyframe sits at the playhead
        public bool KeyAnimated;    // the clip has keyframes
        public event Action<double> Changing;
        public event Action Committed;
        public event Action KeyClicked;

        public PropSlider(string label, double min, double max, double def)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Text = label;
            this.min = min; this.max = max; this.def = def; value = def;
            Height = Theme.S(46);
            Font = Theme.UiFont(9f);
        }

        public double Value { get { return value; } }
        public void SetSilently(double v) { value = Math.Max(min, Math.Min(max, v)); Invalidate(); }
        public void Sync() { if (Getter != null && !dragging) SetSilently(Getter()); }

        float Pad { get { return Theme.F(8); } }
        RectangleF Track { get { return new RectangleF(Pad, Theme.F(28), Width - 2 * Pad, Theme.F(4)); } }
        RectangleF KeyRect { get { float s = Theme.F(18); return new RectangleF(Width - s - Theme.F(2), Theme.F(2), s, s); } }

        void Set(double v)
        {
            v = Math.Max(min, Math.Min(max, v));
            if (Math.Abs(v - value) < 1e-9) return;
            value = v;
            movedInDrag = true;
            Invalidate();
            var h = Changing;
            if (h != null) h(v);
        }

        void SetFromX(int x)
        {
            var tr = Track;
            double t = (x - tr.Left) / Math.Max(1.0, tr.Width);
            double v = min + Math.Max(0, Math.Min(1, t)) * (max - min);
            // gentle magnet at the default value
            double tol = (max - min) * 0.012;
            if (Math.Abs(v - def) < tol) v = def;
            Set(v);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            if (Keyable && KeyRect.Contains(e.Location)) { var h = KeyClicked; if (h != null) h(); return; }
            if (e.Y >= Track.Top - Theme.F(10))
            {
                dragging = true;
                movedInDrag = false;
                Capture = true;
                SetFromX(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetFromX(e.X);
            bool kh = Keyable && KeyRect.Contains(e.Location);
            if (kh != keyHover) { keyHover = kh; Invalidate(); Ui.Tip.SetToolTip(this, kh ? (KeyActive ? "Remove the keyframe at the playhead" : "Add a keyframe at the playhead") : "Drag to change. Double-click the name to reset."); }
            Cursor = e.Y >= Track.Top - Theme.F(10) || kh ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Capture = false;
            if (!movedInDrag) return;
            var h = Committed;
            if (h != null) h();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Y < Track.Top - Theme.F(10) && !(Keyable && KeyRect.Contains(e.Location)))
            {
                movedInDrag = false;
                Set(def);
                if (!movedInDrag) return;
                var h = Committed; if (h != null) h();
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; keyHover = false; Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Focused) return;
            Set(value + Math.Sign(e.Delta) * (max - min) / 50);
            var h = Committed; if (h != null) h();
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            double step = (max - min) / 100;
            if (e.KeyCode == Keys.Left) { Set(value - step); var h = Committed; if (h != null) h(); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Set(value + step); var h = Committed; if (h != null) h(); e.Handled = true; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(Parent != null ? Parent.BackColor : p.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool changed = Math.Abs(value - def) > (max - min) * 0.0005;
            TextRenderer.DrawText(g, Text, Font, new Rectangle((int)Pad - 2, 0, Width / 2, Theme.S(22)), Enabled ? p.SubText : p.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int right = Keyable ? (int)KeyRect.Left - Theme.S(4) : Width - (int)Pad + 2;
            TextRenderer.DrawText(g, Format(value), Theme.UiFont(9f, changed ? FontStyle.Bold : FontStyle.Regular), new Rectangle(Width / 2, 0, right - Width / 2, Theme.S(22)),
                changed ? p.Text : p.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Keyable)
            {
                var kr = RectangleF.Inflate(KeyRect, -Theme.F(2), -Theme.F(2));
                var pts = new[] { new PointF(kr.Left + kr.Width / 2, kr.Top), new PointF(kr.Right, kr.Top + kr.Height / 2), new PointF(kr.Left + kr.Width / 2, kr.Bottom), new PointF(kr.Left, kr.Top + kr.Height / 2) };
                if (KeyActive) using (var b = new SolidBrush(Color.FromArgb(255, 255, 200, 70))) g.FillPolygon(b, pts);
                using (var pen = new Pen(KeyActive ? Color.FromArgb(255, 255, 200, 70) : keyHover ? p.Text : KeyAnimated ? p.SubText : p.Muted, Theme.F(1.4f))) g.DrawPolygon(pen, pts);
            }
            var tr = Track;
            using (var path = Theme.Rounded(tr, tr.Height / 2)) using (var b = new SolidBrush(p.Control)) g.FillPath(b, path);
            double z = (Math.Max(min, Math.Min(max, def)) - min) / (max - min);
            double f = (value - min) / (max - min);
            float x0 = tr.Left + (float)(tr.Width * Math.Min(z, f)), x1 = tr.Left + (float)(tr.Width * Math.Max(z, f));
            if (x1 - x0 > 0.5f)
                using (var b = Enabled ? (Brush)Theme.AccentGradient(tr) : new SolidBrush(p.Muted)) g.FillRectangle(b, x0, tr.Top, x1 - x0, tr.Height);
            if (min < def && def < max)
                using (var pen = new Pen(p.Muted)) g.DrawLine(pen, tr.Left + (float)(tr.Width * z), tr.Top - Theme.F(3), tr.Left + (float)(tr.Width * z), tr.Bottom + Theme.F(3));
            float kx = tr.Left + (float)(tr.Width * f);
            float kr2 = Theme.F(hover || dragging ? 7.5f : 6.5f);
            using (var b = new SolidBrush(Enabled ? Color.White : p.Muted)) g.FillEllipse(b, kx - kr2, tr.Top + tr.Height / 2 - kr2, kr2 * 2, kr2 * 2);
            using (var pen = new Pen(Enabled ? p.AccentA : p.ControlBorder, Theme.F(2))) g.DrawEllipse(pen, kx - kr2, tr.Top + tr.Height / 2 - kr2, kr2 * 2, kr2 * 2);
            if (Focused && ShowFocusCues) using (var pen = new Pen(p.AccentA) { DashStyle = DashStyle.Dot }) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>A row of mutually exclusive choices.</summary>
    internal sealed class Segmented : Control
    {
        readonly List<KeyValuePair<string, string>> items = new List<KeyValuePair<string, string>>();
        readonly List<Ico> icons = new List<Ico>();
        string selected, hover;
        public event Action<string> Picked;

        public Segmented()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(32);
            Font = Theme.UiFont(9f);
        }

        public Segmented Add(string id, string label, Ico icon = Ico.None) { items.Add(new KeyValuePair<string, string>(id, label)); icons.Add(icon); return this; }
        public string Selected { get { return selected; } set { selected = value; Invalidate(); } }

        RectangleF Cell(int i) { float w = (Width - 2f) / Math.Max(1, items.Count); return new RectangleF(1 + i * w, 1, w, Height - 2); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string h = null;
            for (int i = 0; i < items.Count; i++) if (Cell(i).Contains(e.Location)) h = items[i].Key;
            if (h != hover) { hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i < items.Count; i++)
                if (Cell(i).Contains(e.Location)) { selected = items[i].Key; Invalidate(); var h = Picked; if (h != null) h(selected); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(Parent != null ? Parent.BackColor : p.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var all = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.Rounded(all, Theme.F(8))) using (var b = new SolidBrush(p.Control)) g.FillPath(b, path);
            for (int i = 0; i < items.Count; i++)
            {
                var r = RectangleF.Inflate(Cell(i), -Theme.F(2), -Theme.F(2));
                bool sel = items[i].Key == selected;
                if (sel && !Enabled)
                    using (var path = Theme.Rounded(r, Theme.F(6))) using (var b = new SolidBrush(p.ControlBorder)) g.FillPath(b, path);
                else if (sel)
                    using (var path = Theme.Rounded(r, Theme.F(6))) using (var b = Theme.AccentGradient(r)) g.FillPath(b, path);
                else if (items[i].Key == hover)
                    using (var path = Theme.Rounded(r, Theme.F(6))) using (var b = new SolidBrush(p.Hover)) g.FillPath(b, path);
                Color fg = !Enabled ? p.Muted : sel ? Color.White : p.Text;
                if (icons[i] != Ico.None && string.IsNullOrEmpty(items[i].Value))
                {
                    float s = Theme.F(18);
                    Icons.Draw(g, icons[i], new RectangleF(r.Left + (r.Width - s) / 2, r.Top + (r.Height - s) / 2, s, s), fg, 1.7f);
                }
                else
                    TextRenderer.DrawText(g, items[i].Value, Font, Rectangle.Round(r), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }

    /// <summary>Title line for a panel.</summary>
    internal sealed class PanelTitle : Control
    {
        public string Sub;
        public PanelTitle(string text, string sub = null)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = text; Sub = sub;
            Height = Theme.S(sub == null ? 30 : 46);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : p.Panel);
            TextRenderer.DrawText(e.Graphics, Text, Theme.DisplayFont(12.5f), new Rectangle(0, 0, Width, Theme.S(28)), p.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (Sub != null)
                TextRenderer.DrawText(e.Graphics, Sub, Theme.UiFont(9f), new Rectangle(0, Theme.S(27), Width, Theme.S(18)), p.SubText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // =========================================================================== preview

    /// <summary>Shows the rendered frame on a letterboxed canvas, with move/scale/rotate handles for the selected clip.</summary>
    internal sealed class PreviewSurface : Control
    {
        readonly VideoEditorView ed;
        readonly object frameLock = new object();
        Bitmap frame;
        int pendingPaint;
        enum Mode { None, Move, Scale, Rotate }
        Mode mode;
        PointF downCanvas;
        Placement downPlacement;
        double downDist, downAngle;
        public string Message;   // shown when there is nothing to preview

        public PreviewSurface(VideoEditorView editor)
        {
            ed = editor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        public RectangleF CanvasRect
        {
            get
            {
                var pr = ed.Project;
                float m = Theme.F(14);
                float aw = Math.Max(10, Width - 2 * m), ah = Math.Max(10, Height - 2 * m);
                float k = Math.Min(aw / pr.Width, ah / pr.Height);
                float w = pr.Width * k, h = pr.Height * k;
                return new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
            }
        }

        /// <summary>Render size for stills: the canvas at screen resolution (capped for speed).</summary>
        public Size RenderSize(int maxWidth)
        {
            var r = CanvasRect;
            var pr = ed.Project;
            double w = Math.Min(Math.Min(r.Width, maxWidth), pr.Width);
            double h = w * pr.Height / pr.Width;
            return new Size(Math.Max(16, (int)w) & ~1, Math.Max(16, (int)h) & ~1);
        }

        public void SetFrame(Bitmap owned)
        {
            lock (frameLock)
            {
                if (frame != null) frame.Dispose();
                frame = owned;
            }
            Invalidate();
        }

        /// <summary>Called from the playback thread with a frame that is only valid during the call.</summary>
        public void PresentCopy(Bitmap src)
        {
            lock (frameLock)
            {
                if (frame == null || frame.Width != src.Width || frame.Height != src.Height)
                {
                    if (frame != null) frame.Dispose();
                    frame = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
                }
                using (var g = Graphics.FromImage(frame))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(src, 0, 0);
                }
            }
            if (System.Threading.Interlocked.Exchange(ref pendingPaint, 1) == 0 && IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke((MethodInvoker)(() => { System.Threading.Interlocked.Exchange(ref pendingPaint, 0); Invalidate(); })); }
                catch { System.Threading.Interlocked.Exchange(ref pendingPaint, 0); }
            }
        }

        public Bitmap CopyFrame()
        {
            lock (frameLock) return frame != null ? new Bitmap(frame) : null;
        }

        PointF ToCanvas(Point p)
        {
            var r = CanvasRect;
            var pr = ed.Project;
            return new PointF((p.X - r.Left) / r.Width * pr.Width, (p.Y - r.Top) / r.Height * pr.Height);
        }

        PointF[] SelectedOutlineScreen()
        {
            var c = ed.SingleSelected;
            if (c == null || !c.IsVisual || ed.IsPlaying) return null;
            if (ed.Playhead < c.Start - 1e-6 || ed.Playhead >= c.End - 1e-6) return null;
            var tr = ed.Project.TrackOf(c);
            if (tr == null || tr.Hidden) return null;
            var pr = ed.Project;
            var comp = new Compositor(pr, id => ed.Library.Find(id));
            var pts = comp.Outline(c, ed.Playhead, pr.Width, pr.Height);
            if (pts == null) return null;
            var r = CanvasRect;
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(r.Left + pts[i].X / pr.Width * r.Width, r.Top + pts[i].Y / pr.Height * r.Height);
            return pts;
        }

        static PointF Mid(PointF a, PointF b) { return new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2); }

        PointF RotateKnob(PointF[] q)
        {
            var top = Mid(q[0], q[1]);
            var centre = Mid(q[0], q[2]);
            float dx = top.X - centre.X, dy = top.Y - centre.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return top;
            return new PointF(top.X + dx / len * Theme.F(26), top.Y + dy / len * Theme.F(26));
        }

        static bool Inside(PointF[] q, PointF p)
        {
            using (var path = new GraphicsPath()) { path.AddPolygon(q); return path.IsVisible(p); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            var q = SelectedOutlineScreen();
            var c = ed.SingleSelected;
            if (q == null)
            {
                // click picks the top-most visual clip under the pointer
                var hit = ed.ClipAtCanvas(ToCanvas(e.Location));
                if (hit != null) { ed.Select(hit, false); q = SelectedOutlineScreen(); c = hit; }
                if (q == null) return;
            }
            var tol = Theme.F(9);
            var knob = RotateKnob(q);
            Mode m = Mode.None;
            if (Dist(e.Location, knob) <= tol + 2) m = Mode.Rotate;
            else
            {
                foreach (var corner in q) if (Dist(e.Location, corner) <= tol) m = Mode.Scale;
                if (m == Mode.None && Inside(q, e.Location)) m = Mode.Move;
            }
            if (m == Mode.None)
            {
                var hit = ed.ClipAtCanvas(ToCanvas(e.Location));
                if (hit != null && !ReferenceEquals(hit, c)) { ed.Select(hit, false); OnMouseDown(e); }
                return;
            }
            mode = m;
            Capture = true;
            downCanvas = ToCanvas(e.Location);
            downPlacement = c.PlacementAt(ed.Playhead);
            var centre = Mid(q[0], q[2]);
            downDist = Dist(e.Location, centre);
            downAngle = Math.Atan2(e.Y - centre.Y, e.X - centre.X);
            downScreen = e.Location;
            changed = false;
        }

        Point downScreen;
        bool changed;

        static double Dist(PointF a, PointF b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (mode == Mode.None)
            {
                var q = SelectedOutlineScreen();
                Cursor = Cursors.Default;
                if (q != null)
                {
                    if (Dist(e.Location, RotateKnob(q)) <= Theme.F(11)) Cursor = Cursors.Hand;
                    else
                    {
                        foreach (var corner in q) if (Dist(e.Location, corner) <= Theme.F(9)) Cursor = Cursors.SizeNWSE;
                        if (Cursor == Cursors.Default && Inside(q, e.Location)) Cursor = Cursors.SizeAll;
                    }
                }
                return;
            }
            var c = ed.SingleSelected;
            if (c == null) return;
            if (!changed)
            {
                if (Math.Abs(e.X - downScreen.X) + Math.Abs(e.Y - downScreen.Y) < Theme.S(3)) return;
                changed = true;
                ed.BeginChange(null);
            }
            var pr = ed.Project;
            var pt = ToCanvas(e.Location);
            var np = downPlacement;
            if (mode == Mode.Move)
            {
                np.X = downPlacement.X + (pt.X - downCanvas.X) / pr.Width;
                np.Y = downPlacement.Y + (pt.Y - downCanvas.Y) / pr.Height;
                // snap to the centre lines
                if (Math.Abs(np.X) < 0.012) np.X = 0;
                if (Math.Abs(np.Y) < 0.012) np.Y = 0;
            }
            else
            {
                var q0 = SelectedOutlineScreen();
                if (q0 == null) return;
                var centre = Mid(q0[0], q0[2]);
                if (mode == Mode.Scale)
                {
                    double d = Dist(e.Location, centre);
                    np.Scale = Math.Max(0.05, Math.Min(10, downPlacement.Scale * d / Math.Max(1, downDist)));
                }
                else
                {
                    double a = Math.Atan2(e.Y - centre.Y, e.X - centre.X);
                    double deg = downPlacement.Rotation + (a - downAngle) * 180 / Math.PI;
                    while (deg > 180) deg -= 360;
                    while (deg < -180) deg += 360;
                    foreach (var snap in new[] { -180.0, -90, 0, 90, 180 }) if (Math.Abs(deg - snap) < 3) deg = snap;
                    np.Rotation = deg;
                }
            }
            ed.SetPlacement(c, np);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (mode == Mode.None) return;
            mode = Mode.None;
            Capture = false;
            if (changed) ed.EndChange();
            changed = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(p.Dark ? Color.FromArgb(6, 8, 16) : Color.FromArgb(214, 220, 234));
            var r = CanvasRect;
            bool drawn = false;
            lock (frameLock)
            {
                if (frame != null)
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(frame, r);
                    drawn = true;
                }
            }
            if (!drawn) using (var b = new SolidBrush(Color.FromArgb(ed.Project.Background))) g.FillRectangle(b, r);
            using (var pen = new Pen(p.Dark ? Color.FromArgb(40, 48, 84) : p.BarBorder)) g.DrawRectangle(pen, r.X - 1, r.Y - 1, r.Width + 1, r.Height + 1);
            if (!string.IsNullOrEmpty(Message))
            {
                var st = g.Save();
                g.SetClip(r);
                Theme.DrawEmptyState(g, Rectangle.Round(r), Ico.Video, Message, ed.EmptyHint, false);
                g.Restore(st);
            }
            var q = SelectedOutlineScreen();
            if (q != null)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = Theme.AccentGradient(new RectangleF(r.Left, r.Top, Math.Max(1, r.Width), Math.Max(1, r.Height))))
                using (var pen = new Pen(b, Theme.F(1.6f))) g.DrawPolygon(pen, q);
                var knob = RotateKnob(q);
                var top = Mid(q[0], q[1]);
                using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), Theme.F(1))) g.DrawLine(pen, top, knob);
                float hs = Theme.F(5);
                foreach (var c in q)
                {
                    g.FillEllipse(Brushes.White, c.X - hs, c.Y - hs, hs * 2, hs * 2);
                    using (var pen = new Pen(p.AccentA, Theme.F(1.6f))) g.DrawEllipse(pen, c.X - hs, c.Y - hs, hs * 2, hs * 2);
                }
                g.FillEllipse(Brushes.White, knob.X - hs, knob.Y - hs, hs * 2, hs * 2);
                Icons.Draw(g, Ico.Refresh, new RectangleF(knob.X - hs, knob.Y - hs, hs * 2, hs * 2), p.AccentA, 2.4f);
                if (mode == Mode.Move)
                    using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255)) { DashStyle = DashStyle.Dash })
                    {
                        g.DrawLine(pen, r.Left + r.Width / 2, r.Top, r.Left + r.Width / 2, r.Bottom);
                        g.DrawLine(pen, r.Left, r.Top + r.Height / 2, r.Right, r.Top + r.Height / 2);
                    }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) lock (frameLock) { if (frame != null) frame.Dispose(); frame = null; }
            base.Dispose(disposing);
        }
    }

    /// <summary>Transport under the preview: play, frame steps, time, aspect ratio, snapshot, preview volume.</summary>
    internal sealed class PreviewBar : Control
    {
        readonly VideoEditorView ed;
        readonly ThemedButton play, prev, next, ratio, snapshot, mute, full;

        public PreviewBar(VideoEditorView editor)
        {
            ed = editor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(50);
            prev = Btn(Ico.ChevronLeft, "Previous frame (,)", () => ed.StepFrames(-1));
            play = new ThemedButton { Icon = Ico.Play, IconOnly = true, Primary = true, Tip = "Play / pause (Space)", IconSize = 18f, Size = new Size(Theme.S(44), Theme.S(36)) };
            play.Click += (s, e) => ed.TogglePlay();
            Controls.Add(play);
            next = Btn(Ico.ChevronRight, "Next frame (.)", () => ed.StepFrames(1));
            ratio = new ThemedButton { Text = "16:9", Tip = "Canvas shape and size", Size = new Size(Theme.S(76), Theme.S(32)), Font = Theme.UiFont(9f, FontStyle.Bold) };
            ratio.Click += (s, e) => ed.ShowAspectMenu(ratio);
            Controls.Add(ratio);
            snapshot = Btn(Ico.Picture, "Save this frame as a picture", () => ed.SaveFrame());
            mute = Btn(Ico.Volume, "Mute the preview (M)", () => ed.TogglePreviewMute());
            full = Btn(Ico.Fullscreen, "Bigger preview: hide the side panels (F)", () => ed.ToggleFocusPreview());
        }

        ThemedButton Btn(Ico icon, string tip, Action a)
        {
            var b = new ThemedButton { Icon = icon, IconOnly = true, Compact = true, Tip = tip, IconSize = 18f, Size = new Size(Theme.S(36), Theme.S(36)) };
            b.Click += (s, e) => a();
            Controls.Add(b);
            return b;
        }

        public void Sync()
        {
            play.Icon = ed.IsPlaying ? Ico.Pause : Ico.Play;
            ratio.Text = ed.AspectLabel;
            mute.Icon = ed.PreviewMuted ? Ico.Mute : Ico.Volume;
            mute.Checked = ed.PreviewMuted;
            full.Icon = ed.FocusPreview ? Ico.ExitFullscreen : Ico.Fullscreen;
            Invalidate(true);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (play == null || full == null) return;
            int y = (Height - play.Height) / 2;
            play.Location = new Point((Width - play.Width) / 2, y);
            prev.Location = new Point(play.Left - Theme.S(6) - prev.Width, (Height - prev.Height) / 2);
            next.Location = new Point(play.Right + Theme.S(6), (Height - next.Height) / 2);
            full.Location = new Point(Width - Theme.S(10) - full.Width, (Height - full.Height) / 2);
            mute.Location = new Point(full.Left - Theme.S(2) - mute.Width, full.Top);
            snapshot.Location = new Point(mute.Left - Theme.S(2) - snapshot.Width, full.Top);
            ratio.Location = new Point(snapshot.Left - Theme.S(8) - ratio.Width, (Height - ratio.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(p.Bar);
            using (var pen = new Pen(p.BarBorder)) g.DrawLine(pen, 0, 0, Width, 0);
            string cur = VideoEditorView.Tc(ed.Playhead), total = VideoEditorView.Tc(ed.Project.Duration);
            var f = Theme.UiFont(10f, FontStyle.Bold);
            var r = new Rectangle(Theme.S(14), 0, Math.Max(10, (prev != null ? prev.Left : Width / 2) - Theme.S(20)), Height);
            TextRenderer.DrawText(g, cur, f, r, p.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int cw = TextRenderer.MeasureText(cur, f).Width;
            TextRenderer.DrawText(g, " / " + total, Theme.UiFont(10f), new Rectangle(r.Left + cw, 0, r.Width - cw, Height), p.SubText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Buttons above the timeline.</summary>
    internal sealed class EditToolbar : Control
    {
        readonly List<Control> left = new List<Control>();
        readonly List<Control> right = new List<Control>();
        public readonly ThemedButton Undo, Redo, Split, Delete, Duplicate, Freeze, Detach, AddText, Snap, Magnet, ZoomOut, ZoomIn, Fit, Keyframe;
        public readonly ThemedSlider Zoom;
        int[] gaps = new int[0];

        public EditToolbar(VideoEditorView ed)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(44);
            Undo = L(Ico.Undo, "Undo (Ctrl+Z)", ed.Undo);
            Redo = L(Ico.Redo, "Redo (Ctrl+Y)", ed.Redo);
            left.Add(null);
            Split = L(Ico.Scissors, "Split at the playhead (Ctrl+B)", ed.SplitSelected);
            Delete = L(Ico.Trash, "Delete (Del). Shift+Del deletes and closes the gap", () => ed.DeleteSelected(false));
            Duplicate = L(Ico.Duplicate, "Duplicate (Ctrl+D)", ed.DuplicateSelected);
            Freeze = L(Ico.Snowflake, "Freeze frame – holds the frame at the playhead for 3 seconds", ed.FreezeFrame);
            Detach = L(Ico.Detach, "Detach audio – moves the sound to its own track", ed.DetachAudio);
            Keyframe = L(Ico.Diamond, "Add or remove a keyframe at the playhead (Ctrl+K)", ed.ToggleKeyframe);
            left.Add(null);
            AddText = L(Ico.Text, "Add text at the playhead (T)", () => ed.AddText("plain"));
            Snap = R(Ico.Magnet, "Snapping – clips and the playhead line up with clip edges", ed.ToggleSnapping);
            Magnet = R(Ico.Link, "Main track magnet – clips on the main track stay joined with no gaps", ed.ToggleMainMagnet);
            right.Add(null);
            ZoomOut = R(Ico.ZoomOut, "Zoom out the timeline (Ctrl+wheel)", () => ed.Timeline.ZoomBy(0.75));
            Zoom = new ThemedSlider { Minimum = 0, Maximum = 1000, Width = Theme.S(110), Height = Theme.S(30) };
            Ui.Tip.SetToolTip(Zoom, "Timeline zoom");
            Controls.Add(Zoom);
            right.Add(Zoom);
            ZoomIn = R(Ico.ZoomIn, "Zoom in the timeline (Ctrl+wheel)", () => ed.Timeline.ZoomBy(1.33));
            Fit = R(Ico.FitWidth, "Fit the whole project in view", () => ed.Timeline.FitAll());
        }

        ThemedButton L(Ico icon, string tip, Action a) { var b = Make(icon, tip, a); left.Add(b); return b; }
        ThemedButton R(Ico icon, string tip, Action a) { var b = Make(icon, tip, a); right.Add(b); return b; }

        ThemedButton Make(Ico icon, string tip, Action a)
        {
            var b = new ThemedButton { Icon = icon, IconOnly = true, Compact = true, Tip = tip, IconSize = 19f, Size = new Size(Theme.S(36), Theme.S(34)) };
            b.Click += (s, e) => a();
            Controls.Add(b);
            return b;
        }

        readonly List<int> separators = new List<int>();

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (left == null || right == null) return;
            separators.Clear();
            int x = Theme.S(10);
            foreach (var c in left)
            {
                if (c == null) { separators.Add(x + Theme.S(4)); x += Theme.S(9); continue; }
                c.Location = new Point(x, (Height - c.Height) / 2);
                x += c.Width + Theme.S(2);
            }
            int rx = Width - Theme.S(10);
            for (int i = right.Count - 1; i >= 0; i--)
            {
                var c = right[i];
                if (c == null) { rx -= Theme.S(9); separators.Add(rx + Theme.S(4)); continue; }
                rx -= c.Width;
                c.Location = new Point(rx, (Height - c.Height) / 2);
                rx -= Theme.S(2);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(p.Bar);
            using (var pen = new Pen(p.BarBorder))
            {
                g.DrawLine(pen, 0, 0, Width, 0);
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
                foreach (var x in separators) g.DrawLine(pen, x, Theme.S(12), x, Height - Theme.S(12));
            }
        }
    }

    /// <summary>Thin draggable bar that resizes the timeline.</summary>
    internal sealed class SplitterBar : Control
    {
        bool dragging;
        int startY, startH;
        public Func<int> GetHeight;
        public Action<int> SetHeight;

        public SplitterBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(6);
            Cursor = Cursors.SizeNS;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            dragging = true; Capture = true;
            startY = Cursor.Position.Y; startH = GetHeight();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetHeight(startH - (Cursor.Position.Y - startY));
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            e.Graphics.Clear(p.Window);
            using (var b = new SolidBrush(p.ControlBorder))
                e.Graphics.FillRectangle(b, Width / 2 - Theme.S(18), Height / 2 - 1, Theme.S(36), 2);
        }
    }
}
