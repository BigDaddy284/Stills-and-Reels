using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StillsAndReels
{
    internal enum Surface { Window, Bar, Panel, Canvas }

    internal static class Ui
    {
        static ToolTip tip;
        public static ToolTip Tip
        {
            get
            {
                if (tip == null)
                {
                    tip = new ToolTip { OwnerDraw = true, InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 8000 };
                    tip.Popup += (s, e) =>
                    {
                        var t = tip.GetToolTip(e.AssociatedControl);
                        var sz = TextRenderer.MeasureText(t ?? "", Theme.UiFont(9.5f));
                        e.ToolTipSize = new Size(sz.Width + Theme.S(16), sz.Height + Theme.S(10));
                    };
                    tip.Draw += (s, e) =>
                    {
                        var p = Theme.Current;
                        using (var b = new SolidBrush(p.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
                        using (var pen = new Pen(p.ControlBorder))
                            e.Graphics.DrawRectangle(pen, new Rectangle(0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1));
                        TextRenderer.DrawText(e.Graphics, e.ToolTipText, Theme.UiFont(9.5f), e.Bounds, p.Text,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    };
                }
                return tip;
            }
        }

        public static Color SurfaceColor(Surface s)
        {
            var p = Theme.Current;
            switch (s)
            {
                case Surface.Bar: return p.Bar;
                case Surface.Panel: return p.Panel;
                case Surface.Canvas: return p.Canvas;
                default: return p.Window;
            }
        }

        public static void ShowMenuBelow(ContextMenuStrip menu, Control anchor)
        {
            Theme.StyleMenu(menu);
            menu.Show(anchor, new Point(0, anchor.Height + Theme.S(2)));
        }
    }

    /// <summary>A panel whose background follows the theme.</summary>
    internal class ThemedPanel : Panel
    {
        public Surface Surface = Surface.Panel;
        public bool BorderRight, BorderTop, BorderBottom, BorderLeft;

        public ThemedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            Theme.Changed += OnThemeChanged;
            BackColor = Ui.SurfaceColor(Surface);
        }

        void OnThemeChanged(object s, EventArgs e) { BackColor = Ui.SurfaceColor(Surface); if (AutoScroll) Theme.ApplyScrollbars(this); Invalidate(); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (AutoScroll) Theme.ApplyScrollbars(this);
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            BackColor = Ui.SurfaceColor(Surface);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Theme.Changed -= OnThemeChanged;
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Current.BarBorder))
            {
                var r = ClientRectangle;
                if (BorderRight) e.Graphics.DrawLine(pen, r.Right - 1, 0, r.Right - 1, r.Bottom);
                if (BorderLeft) e.Graphics.DrawLine(pen, 0, 0, 0, r.Bottom);
                if (BorderTop) e.Graphics.DrawLine(pen, 0, 0, r.Right, 0);
                if (BorderBottom) e.Graphics.DrawLine(pen, 0, r.Bottom - 1, r.Right, r.Bottom - 1);
            }
        }
    }

    /// <summary>Owner-drawn button: icon and/or text, optional toggle state, optional gradient "primary" look.</summary>
    internal class ThemedButton : Control
    {
        Ico icon;
        bool isChecked, primary, iconOnly;
        bool hover, pressed;
        string tipText;
        public Color? Swatch; // draws a colour chip instead of an icon

        public ThemedButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            Height = Theme.S(36);
            Font = Theme.UiFont(10f);
        }

        public Ico Icon { get { return icon; } set { icon = value; Invalidate(); } }
        public bool Checked { get { return isChecked; } set { if (isChecked != value) { isChecked = value; Invalidate(); } } }
        public bool Primary { get { return primary; } set { primary = value; Invalidate(); } }
        public bool IconOnly { get { return iconOnly; } set { iconOnly = value; Invalidate(); } }
        public float IconSize = 18f;
        public bool Compact; // no background until hovered/checked
        public bool AlignLeft;

        public string Tip
        {
            get { return tipText; }
            set { tipText = value; Ui.Tip.SetToolTip(this, value); }
        }

        public override string Text { get { return base.Text; } set { base.Text = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            var parentColor = Parent != null ? Parent.BackColor : p.Panel;
            g.Clear(parentColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Theme.F(8);
            bool en = Enabled;

            using (var path = Theme.Rounded(r, rad))
            {
                if (primary && en)
                {
                    using (var b = Theme.AccentGradient(r)) g.FillPath(b, path);
                    if (hover) using (var b = new SolidBrush(Color.FromArgb(pressed ? 50 : 28, Color.White))) g.FillPath(b, path);
                }
                else if (isChecked && en)
                {
                    using (var b = Theme.Gradient(r, Color.FromArgb(p.Dark ? 70 : 40, p.AccentA), Color.FromArgb(p.Dark ? 70 : 40, p.AccentB)))
                        g.FillPath(b, path);
                    using (var b = Theme.AccentGradient(r))
                    using (var pen = new Pen(b, Theme.F(1.4f)))
                        g.DrawPath(pen, path);
                }
                else
                {
                    if (!Compact)
                    {
                        using (var b = new SolidBrush(pressed ? p.Pressed : hover && en ? p.Hover : p.Control)) g.FillPath(b, path);
                    }
                    else if (hover && en)
                    {
                        using (var b = new SolidBrush(pressed ? p.Pressed : p.Hover)) g.FillPath(b, path);
                    }
                }
                if (Focused && ShowFocusCues)
                    using (var pen = new Pen(p.AccentA, Theme.F(1.2f)) { DashStyle = DashStyle.Dot }) g.DrawPath(pen, path);
            }

            Color fg = !en ? p.Muted : (primary || isChecked) ? (primary ? Color.White : p.Text) : p.Text;
            float iconPx = Theme.F(IconSize);
            bool hasText = !iconOnly && !string.IsNullOrEmpty(Text);
            bool hasIcon = icon != Ico.None || Swatch.HasValue;
            var font = Font;
            var ts = hasText ? TextRenderer.MeasureText(g, Text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix) : Size.Empty;
            float gap = hasIcon && hasText ? Theme.F(8) : 0;
            float contentW = (hasIcon ? iconPx : 0) + gap + ts.Width;
            float x = AlignLeft && hasText ? Theme.F(12) : (Width - contentW) / 2f;
            if (hasIcon)
            {
                var ir = new RectangleF(x, (Height - iconPx) / 2f, iconPx, iconPx);
                if (Swatch.HasValue)
                {
                    var sr = RectangleF.Inflate(ir, -Theme.F(1), -Theme.F(1));
                    using (var sp = Theme.Rounded(sr, Theme.F(4)))
                    {
                        using (var b = new SolidBrush(en ? Swatch.Value : Theme.Blend(Swatch.Value, p.Muted, 0.6f))) g.FillPath(b, sp);
                        using (var pen = new Pen(p.ControlBorder)) g.DrawPath(pen, sp);
                    }
                }
                else Icons.Draw(g, icon, ir, fg, 1.8f);
                x += iconPx + gap;
            }
            if (hasText)
            {
                var tr = new Rectangle((int)x, 0, Math.Max(1, (int)(Width - x - Theme.F(6))), Height);
                TextRenderer.DrawText(g, Text, font, tr, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }

    /// <summary>Horizontal slider with a gradient fill.</summary>
    internal sealed class ThemedSlider : Control
    {
        int min = 0, max = 100, value = 50;
        bool dragging, hover;
        public event EventHandler ValueChanged;

        public ThemedSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Height = Theme.S(26);
            Cursor = Cursors.Hand;
        }

        public int Minimum { get { return min; } set { min = value; if (this.value < min) Value = min; Invalidate(); } }
        public int Maximum { get { return max; } set { max = Math.Max(min + 1, value); if (this.value > max) Value = max; Invalidate(); } }

        public int Value
        {
            get { return value; }
            set
            {
                int v = Math.Max(min, Math.Min(max, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                var h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public void SetSilently(int v) { value = Math.Max(min, Math.Min(max, v)); Invalidate(); }

        float Pad { get { return Theme.F(8); } }

        void SetFromX(int x)
        {
            float t = (x - Pad) / Math.Max(1f, Width - 2 * Pad);
            Value = (int)Math.Round(min + Math.Max(0, Math.Min(1, t)) * (max - min));
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (e.Button == MouseButtons.Left) { dragging = true; Capture = true; SetFromX(e.X); } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value += Math.Sign(e.Delta) * Math.Max(1, (max - min) / 20);
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) Value -= 1;
            else if (e.KeyCode == Keys.Right) Value += 1;
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(Parent != null ? Parent.BackColor : p.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = Theme.F(4);
            float y = Height / 2f;
            var track = new RectangleF(Pad, y - h / 2, Width - 2 * Pad, h);
            using (var path = Theme.Rounded(track, h / 2))
            using (var b = new SolidBrush(p.Control)) g.FillPath(b, path);
            float t = (float)(value - min) / Math.Max(1, max - min);
            float tx = track.Left + track.Width * t;
            if (tx - track.Left > 1)
            {
                var fill = new RectangleF(track.Left, track.Top, tx - track.Left, h);
                using (var path = Theme.Rounded(fill, h / 2))
                using (var b = Enabled ? (Brush)Theme.AccentGradient(new RectangleF(track.Left, track.Top, track.Width, h)) : new SolidBrush(p.Muted))
                    g.FillPath(b, path);
            }
            float tr = Theme.F(hover || dragging ? 8 : 7);
            using (var b = new SolidBrush(Enabled ? Color.White : p.Muted)) g.FillEllipse(b, tx - tr, y - tr, tr * 2, tr * 2);
            using (var pen = new Pen(Enabled ? p.AccentA : p.ControlBorder, Theme.F(2))) g.DrawEllipse(pen, tx - tr, y - tr, tr * 2, tr * 2);
        }
    }

    /// <summary>On/off switch with a label.</summary>
    internal sealed class ThemedToggle : Control
    {
        bool on;
        public event EventHandler CheckedChanged;

        public ThemedToggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Height = Theme.S(34);
            Cursor = Cursors.Hand;
            Font = Theme.UiFont(10f);
        }

        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); }
        }

        public override string Text { get { return base.Text; } set { base.Text = value; Invalidate(); } }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Checked = !Checked; }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode == Keys.Space) Checked = !Checked; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(Parent != null ? Parent.BackColor : p.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Theme.F(40), h = Theme.F(20);
            var sr = new RectangleF(Width - w - Theme.F(2), (Height - h) / 2, w, h);
            using (var path = Theme.Rounded(sr, h / 2))
            {
                if (on) using (var b = Theme.AccentGradient(sr)) g.FillPath(b, path);
                else
                {
                    using (var b = new SolidBrush(p.Control)) g.FillPath(b, path);
                    using (var pen = new Pen(p.ControlBorder)) g.DrawPath(pen, path);
                }
            }
            float k = h - Theme.F(6);
            float kx = on ? sr.Right - k - Theme.F(3) : sr.Left + Theme.F(3);
            using (var b = new SolidBrush(on ? Color.White : p.SubText)) g.FillEllipse(b, kx, sr.Top + Theme.F(3), k, k);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, (int)(sr.Left - Theme.F(8)), Height), Enabled ? p.Text : p.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(p.AccentA) { DashStyle = DashStyle.Dot }) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Grid of colour chips plus a "custom colour" chip.</summary>
    internal sealed class SwatchPicker : Control
    {
        Color[] colors = new Color[0];
        Color selected;
        int hoverIndex = -1;
        public event EventHandler<ColorEventArgs> ColorPicked;
        public int Columns = 6;

        public SwatchPicker()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public Color[] Colors { get { return colors; } set { colors = value ?? new Color[0]; Height = PreferredHeight; Invalidate(); } }
        public Color Selected { get { return selected; } set { selected = value; Invalidate(); } }

        float Cell { get { return Math.Min(Theme.F(30), (Width - Theme.F(4)) / (float)Columns); } }
        public int PreferredHeight
        {
            get
            {
                int n = colors.Length + 1;
                int rows = (n + Columns - 1) / Columns;
                float c = Width > 0 ? Cell : Theme.F(30);
                return (int)Math.Ceiling(rows * c) + Theme.S(2);
            }
        }

        RectangleF CellRect(int i)
        {
            float c = Cell;
            return new RectangleF((i % Columns) * c, (i / Columns) * c, c, c);
        }

        int HitTest(Point pt)
        {
            for (int i = 0; i <= colors.Length; i++) if (CellRect(i).Contains(pt)) return i;
            return -1;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = HitTest(e.Location); if (h != hoverIndex) { hoverIndex = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverIndex = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = HitTest(e.Location);
            if (i < 0) return;
            Color c;
            if (i == colors.Length)
            {
                using (var dlg = new ColorDialog { Color = selected, FullOpen = true })
                {
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                    c = Color.FromArgb(255, dlg.Color);
                }
            }
            else c = colors[i];
            selected = c;
            Invalidate();
            var h = ColorPicked;
            if (h != null) h(this, new ColorEventArgs(c));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(Parent != null ? Parent.BackColor : p.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float pad = Theme.F(4);
            for (int i = 0; i <= colors.Length; i++)
            {
                var cr = RectangleF.Inflate(CellRect(i), -pad, -pad);
                bool isCustom = i == colors.Length;
                bool sel = !isCustom && colors[i].ToArgb() == selected.ToArgb();
                bool selCustom = isCustom && Array.FindIndex(colors, c => c.ToArgb() == selected.ToArgb()) < 0;
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(cr);
                    if (isCustom)
                    {
                        if (selCustom) using (var b = new SolidBrush(selected)) g.FillPath(b, path);
                        else using (var b = new SolidBrush(p.Control)) g.FillPath(b, path);
                        Icons.Draw(g, Ico.Plus, RectangleF.Inflate(cr, -Theme.F(4), -Theme.F(4)), selCustom ? Contrast(selected) : p.SubText, 2.2f);
                    }
                    else using (var b = new SolidBrush(Enabled ? colors[i] : Theme.Blend(colors[i], p.Muted, 0.6f))) g.FillPath(b, path);
                    using (var pen = new Pen(Color.FromArgb(p.Dark ? 70 : 60, p.Dark ? Color.White : Color.Black))) g.DrawPath(pen, path);
                }
                if (sel || selCustom)
                {
                    var ring = RectangleF.Inflate(cr, Theme.F(2.5f), Theme.F(2.5f));
                    using (var b = Theme.AccentGradient(ring))
                    using (var pen = new Pen(b, Theme.F(2))) g.DrawEllipse(pen, ring);
                }
                else if (i == hoverIndex && Enabled)
                {
                    var ring = RectangleF.Inflate(cr, Theme.F(2), Theme.F(2));
                    using (var pen = new Pen(p.SubText, Theme.F(1))) g.DrawEllipse(pen, ring);
                }
            }
        }

        static Color Contrast(Color c) { return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 150 ? Color.Black : Color.White; }
    }

    internal sealed class ColorEventArgs : EventArgs
    {
        public readonly Color Color;
        public ColorEventArgs(Color c) { Color = c; }
    }

    /// <summary>Small caption above a group of controls.</summary>
    internal sealed class SectionLabel : Control
    {
        public SectionLabel(string text)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = text;
            Height = Theme.S(24);
        }

        public string Value; // optional right-aligned value

        public override string Text { get { return base.Text; } set { base.Text = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : p.Panel);
            var f = Theme.UiFont(8.5f, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, (Text ?? "").ToUpperInvariant(), f, ClientRectangle, p.Muted,
                TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (!string.IsNullOrEmpty(Value))
                TextRenderer.DrawText(e.Graphics, Value, Theme.UiFont(9f), ClientRectangle, p.SubText,
                    TextFormatFlags.Right | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Wrapped multi-line hint text.</summary>
    internal sealed class HintLabel : Control
    {
        public HintLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public override string Text { get { return base.Text; } set { base.Text = value; Invalidate(); } }

        public int MeasureHeight(int width)
        {
            var sz = TextRenderer.MeasureText(Text ?? "", Theme.UiFont(9.5f), new Size(Math.Max(10, width), 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return sz.Height + Theme.S(4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : p.Panel);
            TextRenderer.DrawText(e.Graphics, Text ?? "", Theme.UiFont(9.5f), ClientRectangle, p.SubText,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top);
        }
    }

    // ======================================================================= mode toolbar

    internal enum AppMode { Pictures, PictureEdit, Videos, VideoEdit }

    /// <summary>
    /// The central rounded toolbar: Pictures | Edit | Videos | Edit. The Edit sections are half-width
    /// icon buttons; when their mode is active they expand into Edit and Snip, and the main section
    /// next to them shrinks to half width. Width changes are animated.
    /// </summary>
    internal sealed class ModeBar : Control
    {
        sealed class Cell
        {
            public string Id;
            public Ico Icon;
            public string Label;
            public float W, Target;
            public bool Enabled = true;
            public string Tip;
            public int Group;
            public RectangleF Bounds;
        }

        readonly List<Cell> cells = new List<Cell>();
        readonly Timer anim = new Timer { Interval = 15 };
        AppMode mode = AppMode.Pictures;
        bool snipOpen;
        string hoverId, pressedId;
        bool videoEditAvailable, videoSnipAvailable;

        public event Action<string> CellClicked;   // ids: pictures, picedit, picsnip, videos, videdit, vidsnip

        // Base widths at 100% scaling. Reference sections: Pictures ~118, Videos ~114, Edit ~94 (halved to 47).
        const float MainFull = 118, VideosFull = 114, EditCollapsed = 48, SubExpanded = 84;
        const float Height0 = 42, Margin0 = 9;

        public ModeBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            cells.Add(new Cell { Id = "pictures", Icon = Ico.Picture, Label = "Pictures", Group = 0, Tip = "Pictures – view pictures from File Explorer" });
            cells.Add(new Cell { Id = "picedit", Icon = Ico.Pencil, Label = "Edit", Group = 1, Tip = "Edit picture (Ctrl+E)" });
            cells.Add(new Cell { Id = "picsnip", Icon = Ico.Snip, Label = "Snip", Group = 1, Tip = "Snip – capture part of your screen or picture (Ctrl+Shift+N)" });
            cells.Add(new Cell { Id = "videos", Icon = Ico.Video, Label = "Videos", Group = 2, Tip = "Videos – play videos from File Explorer" });
            cells.Add(new Cell { Id = "videdit", Icon = Ico.Pencil, Label = "Edit", Group = 3, Tip = "Video editor – coming in the next update" });
            cells.Add(new Cell { Id = "vidsnip", Icon = Ico.Snip, Label = "Snip", Group = 3, Tip = "Video snip – coming in the next update" });
            UpdateTargets(true);
            anim.Tick += (s, e) => Step();
            Font = Theme.UiFont(10.5f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }

        public AppMode Mode { get { return mode; } set { if (mode != value) { mode = value; UpdateTargets(false); } } }
        public bool SnipOpen { get { return snipOpen; } set { if (snipOpen != value) { snipOpen = value; Invalidate(); } } }
        bool videoSnipOpen;
        public bool VideoSnipOpen { get { return videoSnipOpen; } set { if (videoSnipOpen != value) { videoSnipOpen = value; Invalidate(); } } }

        public void SetVideoEditing(bool editAvailable, bool snipAvailable)
        {
            videoEditAvailable = editAvailable;
            videoSnipAvailable = snipAvailable;
            Get("videdit").Enabled = editAvailable;
            Get("vidsnip").Enabled = snipAvailable;
            Get("videdit").Tip = editAvailable ? "Edit video" : "Video editor – coming in the next update";
            Get("vidsnip").Tip = snipAvailable ? "Video snip – record part of your screen" : "Video snip – coming in the next update";
            Invalidate();
        }

        Cell Get(string id) { return cells.Find(c => c.Id == id); }

        void UpdateTargets(bool immediate)
        {
            bool picEdit = mode == AppMode.PictureEdit, vidEdit = mode == AppMode.VideoEdit;
            Get("pictures").Target = picEdit ? MainFull / 2 : MainFull;
            Get("picedit").Target = picEdit ? SubExpanded : EditCollapsed;
            Get("picsnip").Target = picEdit ? SubExpanded : 0;
            Get("videos").Target = vidEdit ? VideosFull / 2 : VideosFull;
            Get("videdit").Target = vidEdit ? SubExpanded : EditCollapsed;
            Get("vidsnip").Target = vidEdit ? SubExpanded : 0;
            if (immediate) foreach (var c in cells) c.W = c.Target;
            else anim.Start();
            UpdateSize();
            Invalidate();
        }

        void Step()
        {
            bool moving = false;
            foreach (var c in cells)
            {
                float d = c.Target - c.W;
                if (Math.Abs(d) < 0.6f) c.W = c.Target;
                else { c.W += d * 0.28f; moving = true; }
            }
            if (!moving) anim.Stop();
            UpdateSize();
            Invalidate();
        }

        public float PillWidth
        {
            get
            {
                float w = 0;
                foreach (var c in cells) w += c.W;
                return Theme.F(w + 8);
            }
        }

        void UpdateSize()
        {
            int w = (int)Math.Ceiling(PillWidth + Theme.F(Margin0 * 2));
            int h = Theme.S(Height0 + Margin0 * 2);
            if (Width != w || Height != h)
            {
                Size = new Size(w, h);
                if (Parent != null) Parent.PerformLayout();
            }
        }

        RectangleF PillRect
        {
            get { return new RectangleF(Theme.F(Margin0), Theme.F(Margin0), PillWidth, Theme.F(Height0)); }
        }

        bool IsSelected(Cell c)
        {
            switch (mode)
            {
                case AppMode.Pictures: return c.Id == "pictures";
                case AppMode.PictureEdit: return c.Id == "picedit" || (c.Id == "picsnip" && snipOpen);
                case AppMode.Videos: return c.Id == "videos";
                case AppMode.VideoEdit: return c.Id == "videdit" || (c.Id == "vidsnip" && videoSnipOpen);
            }
            return false;
        }

        void Layout0()
        {
            var pr = PillRect;
            float x = pr.Left + Theme.F(4);
            foreach (var c in cells)
            {
                float w = Theme.F(c.W);
                c.Bounds = new RectangleF(x, pr.Top + Theme.F(4), w, pr.Height - Theme.F(8));
                x += w;
            }
        }

        Cell HitTest(Point p)
        {
            Layout0();
            foreach (var c in cells) if (c.W > 6 && c.Bounds.Contains(p)) return c;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var c = HitTest(e.Location);
            string id = c != null ? c.Id : null;
            if (id != hoverId)
            {
                hoverId = id;
                Ui.Tip.SetToolTip(this, c != null ? c.Tip : null);
                Cursor = c != null && c.Enabled ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverId = null; pressedId = null; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var c = HitTest(e.Location);
            pressedId = c != null && c.Enabled ? c.Id : null;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var c = HitTest(e.Location);
            string id = pressedId;
            pressedId = null;
            Invalidate();
            if (c != null && c.Id == id && c.Enabled && e.Button == MouseButtons.Left)
            {
                var h = CellClicked;
                if (h != null) h(id);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(Parent != null ? Parent.BackColor : p.Bar);
            if (Parent is TopBar)
            {
                // continue the top bar's bottom hairline underneath this control
                int ly = Parent.Height - 1 - Top;
                if (ly >= 0 && ly < Height) using (var pen = new Pen(p.BarBorder)) g.DrawLine(pen, 0, ly, Width, ly);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Layout0();
            var pr = PillRect;
            float rad = pr.Height / 2;

            // outer glow and gradient border
            Theme.DrawGlow(g, pr, rad, Theme.Blend(p.AccentA, p.AccentB, 0.5f), Theme.F(7), p.Dark ? 60 : 30);
            using (var path = Theme.Rounded(pr, rad))
            {
                using (var b = new SolidBrush(p.Pill)) g.FillPath(b, path);
                using (var b = Theme.AccentGradient(pr))
                using (var pen = new Pen(b, Theme.F(1.6f))) g.DrawPath(pen, path);
            }

            // selected cell(s)
            foreach (var c in cells)
            {
                if (c.W < 6 || !IsSelected(c)) continue;
                var sr = c.Bounds;
                float srad = sr.Height / 2;
                if (c.Id == "picsnip" || c.Id == "vidsnip")
                {
                    using (var path = Theme.Rounded(RectangleF.Inflate(sr, -Theme.F(1), 0), srad))
                    using (var b = Theme.AccentGradient(sr))
                    using (var pen = new Pen(b, Theme.F(1.4f))) g.DrawPath(pen, path);
                    continue;
                }
                Theme.DrawGlow(g, sr, srad, p.AccentA, Theme.F(6), p.Dark ? 110 : 60);
                using (var path = Theme.Rounded(sr, srad))
                {
                    using (var b = Theme.Gradient(sr, p.AccentA, Theme.Blend(p.AccentA, Color.FromArgb(90, 150, 255), 0.55f), 90f))
                        g.FillPath(b, path);
                    using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), Theme.F(1))) g.DrawPath(pen, path);
                }
            }

            // separators (between groups solid, inside an expanded group faint)
            for (int i = 1; i < cells.Count; i++)
            {
                var a = cells[i - 1];
                var c = cells[i];
                if (c.W < 6 || a.W < 6) continue;
                if (IsSelected(a) && a.Id != "picsnip" && a.Id != "vidsnip" || IsSelected(c) && c.Id != "picsnip" && c.Id != "vidsnip") continue;
                bool sameGroup = a.Group == c.Group;
                float x = c.Bounds.Left;
                float inset = c.Bounds.Height * (sameGroup ? 0.3f : 0.22f);
                using (var pen = new Pen(sameGroup ? Color.FromArgb(120, p.PillSep) : p.PillSep, Theme.F(1)))
                    g.DrawLine(pen, x, c.Bounds.Top + inset, x, c.Bounds.Bottom - inset);
            }

            // contents
            foreach (var c in cells)
            {
                if (c.W < 10) continue;
                var r = c.Bounds;
                bool sel = IsSelected(c) && c.Id != "picsnip" && c.Id != "vidsnip";
                if (!sel && c.Enabled && (c.Id == hoverId))
                {
                    using (var path = Theme.Rounded(RectangleF.Inflate(r, -Theme.F(2), -Theme.F(1)), r.Height / 2))
                    using (var b = new SolidBrush(c.Id == pressedId ? p.Pressed : p.Hover)) g.FillPath(b, path);
                }
                Color fg = !c.Enabled ? p.Muted : sel ? p.SelectedText : p.Text;
                float icon = Theme.F(21);
                var ts = TextRenderer.MeasureText(g, c.Label, Font, Size.Empty, TextFormatFlags.NoPadding);
                float gap = Theme.F(9);
                bool showText = r.Width >= icon + gap + ts.Width + Theme.F(22);
                float contentW = showText ? icon + gap + ts.Width : icon;
                float x = r.Left + (r.Width - contentW) / 2;
                var state = g.Save();
                g.SetClip(r);
                Icons.Draw(g, c.Icon, new RectangleF(x, r.Top + (r.Height - icon) / 2, icon, icon), fg, 1.7f);
                if (showText)
                    TextRenderer.DrawText(g, c.Label, Font, new Rectangle((int)(x + icon + gap), (int)r.Top, ts.Width + 4, (int)r.Height), fg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                g.Restore(state);
            }
        }
    }

    // ======================================================================= top bar

    internal sealed class TopBar : Control
    {
        public readonly MenuStrip Menu;
        public readonly ModeBar Modes = new ModeBar();
        public readonly List<ThemedButton> RightButtons = new List<ThemedButton>();

        public TopBar(MenuStrip menu)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Menu = menu;
            Dock = DockStyle.Top;
            Height = Theme.S(60);
            menu.Dock = DockStyle.None;
            menu.AutoSize = true;
            menu.GripStyle = ToolStripGripStyle.Hidden;
            menu.Padding = new Padding(Theme.S(6), 0, 0, 0);
            Controls.Add(menu);
            Controls.Add(Modes);
            Theme.Changed += OnThemeChanged;
            BackColor = Theme.Current.Bar;
        }

        void OnThemeChanged(object s, EventArgs e) { BackColor = Theme.Current.Bar; Invalidate(true); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Theme.Changed -= OnThemeChanged;
            base.Dispose(disposing);
        }

        public ThemedButton AddRightButton(Ico icon, string tip)
        {
            var b = new ThemedButton { Icon = icon, IconOnly = true, Compact = true, Tip = tip, IconSize = 22f };
            b.Size = new Size(Theme.S(44), Theme.S(40));
            RightButtons.Add(b);
            Controls.Add(b);
            return b;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Menu == null || Modes == null) return;
            int h = Height;
            var ms = Menu.GetPreferredSize(Size.Empty);
            Menu.Location = new Point(Theme.S(10), (h - ms.Height) / 2);
            Menu.Size = ms;

            int x = Width - Theme.S(14);
            for (int i = RightButtons.Count - 1; i >= 0; i--)
            {
                var b = RightButtons[i];
                if (!b.Visible) continue;
                x -= b.Width;
                b.Location = new Point(x, (h - b.Height) / 2);
                x -= Theme.S(6);
            }
            int rightEdge = x;
            int mx = (Width - Modes.Width) / 2;
            int minX = Menu.Right + Theme.S(8);
            if (mx < minX) mx = minX;
            if (mx + Modes.Width > rightEdge) mx = Math.Max(minX, rightEdge - Modes.Width);
            Modes.Location = new Point(mx, (h - Modes.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            e.Graphics.Clear(p.Bar);
            using (var pen = new Pen(p.BarBorder)) e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }

    // ======================================================================= status bar

    internal sealed class StatusBar : Control
    {
        public readonly ThemedButton ZoomOut, ZoomIn, Grid;
        string left = "", right = "";
        bool leftBusy;

        public StatusBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Bottom;
            Height = Theme.S(46);
            ZoomOut = Make(Ico.Minus, "Zoom out (Ctrl+-)");
            ZoomIn = Make(Ico.Plus, "Zoom in (Ctrl++)");
            Grid = Make(Ico.Grid, "Pixel grid – shows pixel edges when zoomed in to 600% or more");
            Theme.Changed += OnThemeChanged;
            BackColor = Theme.Current.Bar;
        }

        void OnThemeChanged(object s, EventArgs e) { BackColor = Theme.Current.Bar; Invalidate(true); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Theme.Changed -= OnThemeChanged;
            base.Dispose(disposing);
        }

        ThemedButton Make(Ico icon, string tip)
        {
            var b = new ThemedButton { Icon = icon, IconOnly = true, Compact = true, Tip = tip, IconSize = 18f };
            b.Size = new Size(Theme.S(40), Theme.S(34));
            Controls.Add(b);
            return b;
        }

        public string LeftText { get { return left; } set { if (left != value) { left = value ?? ""; Invalidate(); } } }
        public string RightText { get { return right; } set { if (right != value) { right = value ?? ""; Invalidate(); } } }
        public bool Busy { get { return leftBusy; } set { leftBusy = value; Invalidate(); } }

        int DividerRight, DividerLeft;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Grid == null || ZoomIn == null || ZoomOut == null) return;
            int y = (Height - ZoomIn.Height) / 2;
            int x = Width - Theme.S(10) - Grid.Width;
            Grid.Location = new Point(x, y);
            DividerRight = x - Theme.S(8);
            x = DividerRight - Theme.S(8) - ZoomIn.Width;
            ZoomIn.Location = new Point(x, y);
            x -= Theme.S(4) + ZoomOut.Width;
            ZoomOut.Location = new Point(x, y);
            DividerLeft = x - Theme.S(8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(p.Bar);
            using (var pen = new Pen(p.BarBorder))
            {
                g.DrawLine(pen, 0, 0, Width, 0);
                int inset = Theme.S(0);
                g.DrawLine(pen, DividerLeft, inset, DividerLeft, Height);
                g.DrawLine(pen, DividerRight, inset, DividerRight, Height);
            }
            var f = Theme.UiFont(10.5f);
            var rs = TextRenderer.MeasureText(g, right, f);
            int rx = DividerLeft - Theme.S(18) - rs.Width;
            int lx = Theme.S(22);
            Theme.DrawText(g, left, f, new Rectangle(lx, 0, Math.Max(10, rx - lx - Theme.S(12)), Height), leftBusy ? p.SubText : p.Text);
            Theme.DrawText(g, right, f, new Rectangle(rx, 0, rs.Width + Theme.S(4), Height), p.SubText);
        }
    }
}
