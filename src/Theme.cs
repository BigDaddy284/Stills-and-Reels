using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>All colours used by the app's own controls. Two instances exist: dark (default) and light.</summary>
    internal sealed class Palette
    {
        public bool Dark;
        public Color Window;      // form background
        public Color Bar;         // top bar, status bar, toolbars
        public Color BarBorder;   // hairlines between bars and content
        public Color Canvas;      // picture / video area
        public Color Panel;       // sidebars, pop-outs
        public Color Text;
        public Color SubText;
        public Color Muted;
        public Color Hover;
        public Color Pressed;
        public Color Pill;        // inside of the mode toolbar
        public Color PillSep;
        public Color AccentA;     // gradient start (blue)
        public Color AccentB;     // gradient end (purple)
        public Color SelectedText;
        public Color Control;     // slider tracks, inactive fills
        public Color ControlBorder;
        public Color Warning;
        public Color Glow;        // ambient glow on empty states
        public Color VideoBg;
    }

    internal static class Theme
    {
        public static readonly Palette DarkPalette = new Palette
        {
            Dark = true,
            Window = Color.FromArgb(10, 13, 26),
            Bar = Color.FromArgb(13, 17, 33),
            BarBorder = Color.FromArgb(30, 37, 62),
            Canvas = Color.FromArgb(9, 12, 24),
            Panel = Color.FromArgb(14, 19, 37),
            Text = Color.FromArgb(234, 238, 250),
            SubText = Color.FromArgb(172, 182, 210),
            Muted = Color.FromArgb(104, 114, 146),
            Hover = Color.FromArgb(26, 33, 62),
            Pressed = Color.FromArgb(34, 43, 80),
            Pill = Color.FromArgb(12, 18, 44),
            PillSep = Color.FromArgb(62, 74, 128),
            AccentA = Color.FromArgb(38, 104, 255),
            AccentB = Color.FromArgb(176, 72, 255),
            SelectedText = Color.White,
            Control = Color.FromArgb(32, 40, 70),
            ControlBorder = Color.FromArgb(46, 56, 92),
            Warning = Color.FromArgb(255, 190, 110),
            Glow = Color.FromArgb(40, 70, 150),
            VideoBg = Color.FromArgb(5, 7, 14),
        };

        public static readonly Palette LightPalette = new Palette
        {
            Dark = false,
            Window = Color.FromArgb(244, 246, 251),
            Bar = Color.FromArgb(255, 255, 255),
            BarBorder = Color.FromArgb(221, 226, 238),
            Canvas = Color.FromArgb(236, 240, 247),
            Panel = Color.FromArgb(250, 251, 254),
            Text = Color.FromArgb(20, 26, 45),
            SubText = Color.FromArgb(74, 85, 112),
            Muted = Color.FromArgb(138, 147, 168),
            Hover = Color.FromArgb(232, 237, 249),
            Pressed = Color.FromArgb(218, 226, 246),
            Pill = Color.FromArgb(243, 245, 255),
            PillSep = Color.FromArgb(196, 204, 232),
            AccentA = Color.FromArgb(37, 99, 245),
            AccentB = Color.FromArgb(150, 62, 240),
            SelectedText = Color.White,
            Control = Color.FromArgb(222, 228, 242),
            ControlBorder = Color.FromArgb(200, 208, 228),
            Warning = Color.FromArgb(196, 98, 0),
            Glow = Color.FromArgb(160, 185, 255),
            VideoBg = Color.FromArgb(12, 14, 22),
        };

        static Palette current = DarkPalette;
        public static Palette Current { get { return current; } }
        public static event EventHandler Changed;

        public static void Set(bool dark)
        {
            var p = dark ? DarkPalette : LightPalette;
            if (ReferenceEquals(p, current)) return;
            current = p;
            var h = Changed;
            if (h != null) h(null, EventArgs.Empty);
        }

        // ------------------------------------------------------------------ DPI & fonts

        static float scale = -1;
        public static float Scale
        {
            get
            {
                if (scale < 0)
                {
                    try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = Math.Max(1f, g.DpiX / 96f); }
                    catch { scale = 1f; }
                }
                return scale;
            }
        }

        public static int S(float px) { return (int)Math.Round(px * Scale); }
        public static float F(float px) { return px * Scale; }

        static string textFamily, displayFamily;

        static void ResolveFamilies()
        {
            if (textFamily != null) return;
            textFamily = "Segoe UI";
            displayFamily = "Segoe UI Semibold";
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    bool variableText = false, variableDisplay = false, semibold = false, segoe = false;
                    foreach (var f in fonts.Families)
                    {
                        if (f.Name == "Segoe UI Variable Text") variableText = true;
                        else if (f.Name == "Segoe UI Variable Display") variableDisplay = true;
                        else if (f.Name == "Segoe UI Semibold") semibold = true;
                        else if (f.Name == "Segoe UI") segoe = true;
                    }
                    if (variableText) textFamily = "Segoe UI Variable Text";
                    else if (!segoe) textFamily = SystemFonts.MessageBoxFont.FontFamily.Name;
                    if (variableDisplay) displayFamily = "Segoe UI Variable Display";
                    else if (!semibold) displayFamily = textFamily;
                }
            }
            catch { }
        }

        static readonly System.Collections.Generic.Dictionary<string, Font> fontCache =
            new System.Collections.Generic.Dictionary<string, Font>();

        /// <summary>UI font in points (sizes are DPI-independent; GDI+ scales points itself).</summary>
        public static Font UiFont(float points, FontStyle style = FontStyle.Regular)
        {
            ResolveFamilies();
            string key = "t" + points + style;
            Font f;
            if (!fontCache.TryGetValue(key, out f))
            {
                f = new Font(textFamily, points, style);
                fontCache[key] = f;
            }
            return f;
        }

        public static Font DisplayFont(float points)
        {
            ResolveFamilies();
            string key = "d" + points;
            Font f;
            if (!fontCache.TryGetValue(key, out f))
            {
                var style = displayFamily == textFamily ? FontStyle.Bold : FontStyle.Regular;
                if (displayFamily == "Segoe UI Variable Display") style = FontStyle.Bold;
                f = new Font(displayFamily, points, style);
                fontCache[key] = f;
            }
            return f;
        }

        // ------------------------------------------------------------------ drawing helpers

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static LinearGradientBrush Gradient(RectangleF r, Color a, Color b, float angle = 0f)
        {
            if (r.Width < 1) r.Width = 1;
            if (r.Height < 1) r.Height = 1;
            return new LinearGradientBrush(r, a, b, angle);
        }

        public static LinearGradientBrush AccentGradient(RectangleF r)
        {
            return Gradient(r, current.AccentA, current.AccentB);
        }

        /// <summary>Soft glow: several translucent rounded rectangles growing outwards.</summary>
        public static void DrawGlow(Graphics g, RectangleF r, float radius, Color c, float spread, int maxAlpha)
        {
            int steps = 6;
            for (int i = steps; i >= 1; i--)
            {
                float grow = spread * i / steps;
                int a = (int)(maxAlpha * (1f - (float)(i - 1) / steps) / 2.2f);
                var rr = RectangleF.Inflate(r, grow, grow);
                using (var path = Rounded(rr, radius + grow))
                using (var b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, a)), c)))
                    g.FillPath(b, path);
            }
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static void DrawText(Graphics g, string text, Font font, Rectangle r, Color c,
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
        {
            TextRenderer.DrawText(g, text, font, r, c, flags | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        // ------------------------------------------------------------------ empty / message states

        public static void DrawEmptyState(Graphics g, Rectangle r, Ico icon, string title, string detail, bool warning)
        {
            var p = current;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // ambient glow behind the content
            float gw = Math.Min(r.Width, r.Height) * 0.9f;
            if (gw > 20)
            {
                var gr = new RectangleF(r.Left + (r.Width - gw * 1.4f) / 2, r.Top + (r.Height - gw) / 2 - S(30), gw * 1.4f, gw);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(gr);
                    using (var pgb = new PathGradientBrush(path))
                    {
                        pgb.CenterColor = Color.FromArgb(p.Dark ? 26 : 50, p.Glow);
                        pgb.SurroundColors = new[] { Color.FromArgb(0, p.Glow) };
                        g.FillPath(pgb, path);
                    }
                }
            }

            int iconSize = S(88);
            var titleFont = DisplayFont(19f);
            var detailFont = UiFont(10.5f);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            int w = Math.Max(60, r.Width - S(48));
            var ts = TextRenderer.MeasureText(g, title ?? "", titleFont, new Size(w, 0), flags);
            var ds = string.IsNullOrEmpty(detail) ? Size.Empty : TextRenderer.MeasureText(g, detail, detailFont, new Size(w, 0), flags);
            int gap1 = S(26), gap2 = S(12);
            int total = iconSize + gap1 + ts.Height + (ds.IsEmpty ? 0 : gap2 + ds.Height);
            bool showIcon = r.Height > total + S(20);
            if (!showIcon) total -= iconSize + gap1;
            int y = r.Top + (r.Height - total) / 2;

            if (showIcon)
            {
                var ir = new RectangleF(r.Left + (r.Width - iconSize) / 2f, y, iconSize, iconSize);
                Color a = warning ? Color.FromArgb(255, 170, 60) : p.AccentA;
                Color b = warning ? Color.FromArgb(240, 80, 140) : p.AccentB;
                // glow
                for (int i = 3; i >= 1; i--)
                    Icons.DrawGradient(g, icon, ir, Color.FromArgb(28, a), Color.FromArgb(28, b), 1.9f + i * 1.1f);
                Icons.DrawGradient(g, icon, ir, a, b, 1.9f);
                y += iconSize + gap1;
            }

            TextRenderer.DrawText(g, title ?? "", titleFont, new Rectangle(r.Left + (r.Width - w) / 2, y, w, ts.Height),
                warning ? p.Warning : p.Text, flags);
            y += ts.Height + gap2;
            if (!ds.IsEmpty)
                TextRenderer.DrawText(g, detail, detailFont, new Rectangle(r.Left + (r.Width - w) / 2, y, w, ds.Height), p.SubText, flags);
        }

        // ------------------------------------------------------------------ window chrome

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Dark/light title bar (Windows 10 2004+) and matching caption colour (Windows 11).</summary>
        public static void ApplyTitleBar(Form f)
        {
            if (f == null || !f.IsHandleCreated) return;
            try
            {
                int on = current.Dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                int caption = ColorRef(current.Bar);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
                int text = ColorRef(current.Text);
                DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
                int border = ColorRef(current.Dark ? Color.FromArgb(40, 48, 84) : current.BarBorder);
                DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
            }
            catch { }
        }

        static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        /// <summary>Dark scroll bars on Windows 10 1809+ / 11 for scrollable panels.</summary>
        public static void ApplyScrollbars(Control c)
        {
            if (c == null || !c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, current.Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        /// <summary>Applies the palette to a form and every child (ordinary controls get colours; ours repaint).</summary>
        public static void ApplyTo(Control root)
        {
            var p = current;
            ApplyRec(root, p);
            var f = root as Form;
            if (f != null) ApplyTitleBar(f);
        }

        static void ApplyRec(Control c, Palette p)
        {
            if (c is Form) { c.BackColor = p.Window; c.ForeColor = p.Text; }
            var sc = c as ScrollableControl;
            if (sc != null && sc.AutoScroll) ApplyScrollbars(c);
            var ts = c as ToolStrip;
            if (ts != null)
            {
                ts.Renderer = MenuRenderer;
                ts.BackColor = p.Bar;
                ts.ForeColor = p.Text;
                foreach (ToolStripItem it in ts.Items) ApplyItem(it, p);
            }
            c.Invalidate();
            foreach (Control child in c.Controls) ApplyRec(child, p);
        }

        static void ApplyItem(ToolStripItem it, Palette p)
        {
            it.ForeColor = p.Text;
            var dd = it as ToolStripDropDownItem;
            if (dd != null)
            {
                dd.DropDown.Renderer = MenuRenderer;
                dd.DropDown.BackColor = p.Panel;
                foreach (ToolStripItem sub in dd.DropDownItems) ApplyItem(sub, p);
            }
        }

        public static void StyleMenu(ToolStripDropDown menu)
        {
            menu.Renderer = MenuRenderer;
            menu.BackColor = current.Panel;
            foreach (ToolStripItem it in menu.Items) ApplyItem(it, current);
        }

        static ToolStripRenderer renderer;
        public static ToolStripRenderer MenuRenderer
        {
            get { return renderer ?? (renderer = new ThemedMenuRenderer()); }
        }
    }

    internal sealed class ThemedColorTable : ProfessionalColorTable
    {
        static Palette P { get { return Theme.Current; } }
        public override Color MenuItemSelected { get { return P.Hover; } }
        public override Color MenuItemBorder { get { return P.Hover; } }
        public override Color MenuBorder { get { return P.ControlBorder; } }
        public override Color ToolStripDropDownBackground { get { return P.Panel; } }
        public override Color ImageMarginGradientBegin { get { return P.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return P.Panel; } }
        public override Color ImageMarginGradientEnd { get { return P.Panel; } }
        public override Color MenuItemSelectedGradientBegin { get { return P.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return P.Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return P.Pressed; } }
        public override Color MenuItemPressedGradientMiddle { get { return P.Pressed; } }
        public override Color MenuItemPressedGradientEnd { get { return P.Pressed; } }
        public override Color SeparatorDark { get { return P.BarBorder; } }
        public override Color SeparatorLight { get { return P.BarBorder; } }
        public override Color MenuStripGradientBegin { get { return P.Bar; } }
        public override Color MenuStripGradientEnd { get { return P.Bar; } }
        public override Color ToolStripBorder { get { return P.Bar; } }
        public override Color CheckBackground { get { return P.Pressed; } }
        public override Color CheckSelectedBackground { get { return P.Pressed; } }
        public override Color CheckPressedBackground { get { return P.Pressed; } }
        public override Color ButtonSelectedHighlight { get { return P.Hover; } }
        public override Color ButtonSelectedBorder { get { return P.Hover; } }
    }

    internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new ThemedColorTable()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            var p = Theme.Current;
            using (var b = new SolidBrush(e.ToolStrip is ToolStripDropDown ? p.Panel : p.Bar))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (!(e.ToolStrip is ToolStripDropDown)) return;
            var r = new Rectangle(Point.Empty, e.ToolStrip.Size);
            r.Width -= 1; r.Height -= 1;
            using (var pen = new Pen(Theme.Current.ControlBorder)) e.Graphics.DrawRectangle(pen, r);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var p = Theme.Current;
            var item = e.Item;
            bool top = item.Owner is MenuStrip;
            bool active = item.Selected || (top && item.Pressed);
            if (!active || !item.Enabled && !top) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(2, 1, item.Width - 4, item.Height - 2);
            if (top) r = new RectangleF(1, Theme.F(3), item.Width - 2, item.Height - Theme.F(6));
            using (var path = Theme.Rounded(r, Theme.F(5)))
            using (var b = new SolidBrush(item.Pressed ? p.Pressed : p.Hover))
                g.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var p = Theme.Current;
            e.TextColor = e.Item.Enabled ? p.Text : p.Muted;
            if (e.Item.Owner is MenuStrip)
            {
                // Top-level titles (File, Edit, View, Help): centre in the whole button so nothing is clipped.
                e.TextFont = Theme.UiFont(10.5f);
                e.TextRectangle = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
                e.TextFormat = (e.TextFormat & ~(TextFormatFlags.Left | TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.Bottom))
                    | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoClipping | TextFormatFlags.SingleLine;
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item != null && e.Item.Enabled ? Theme.Current.SubText : Theme.Current.Muted;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            if (r.Width <= 0) r = new Rectangle(4, (e.Item.Height - 16) / 2, 16, 16);
            using (var path = Theme.Rounded(r, Theme.F(3)))
            using (var b = Theme.AccentGradient(r))
                g.FillPath(b, path);
            Icons.Draw(g, Ico.Check, RectangleF.Inflate(r, -1, -1), Color.White, 2.4f);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var r = e.Item.ContentRectangle;
            int y = r.Top + r.Height / 2;
            using (var pen = new Pen(Theme.Current.BarBorder))
                e.Graphics.DrawLine(pen, r.Left + Theme.S(28), y, r.Right - 4, y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Current.Panel)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
    }
}
