using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StillsAndReels
{
    internal enum SnipShape { Rectangle, Freeform, Window, FullScreen }

    /// <summary>
    /// Lets the user drag a rectangle or draw a freeform outline over a picture. Used full-screen over a
    /// frozen screenshot (screen snips) or as a dialog over the current picture.
    /// </summary>
    internal sealed class SnipSelector : Form
    {
        readonly Bitmap source;
        readonly SnipShape shape;
        readonly bool fullScreen;
        RectangleF imageRect;     // where the source is drawn, in client coords
        float scale = 1f;
        bool dragging;
        Point start, now;
        readonly List<PointF> lasso = new List<PointF>();
        public Bitmap Result { get; private set; }
        public Rectangle ResultRect { get; private set; }   // in source pixels (rectangle snips)
        public string Purpose;                              // hint shown before dragging
        /// <summary>Window snips: the windows on screen, front first, in source pixels.</summary>
        public List<Rectangle> Windows = new List<Rectangle>();
        readonly Point screenOrigin;                        // where the source's top-left is on the desktop
        bool mouseInside;

        public SnipSelector(Bitmap source, SnipShape shape, bool fullScreen, Rectangle screenBounds)
        {
            this.source = source;
            this.shape = shape;
            this.fullScreen = fullScreen;
            screenOrigin = screenBounds.Location;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            KeyPreview = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;
            if (fullScreen)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Bounds = screenBounds;
                TopMost = true;
            }
            else
            {
                Text = "Select the part of the picture to snip";
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterParent;
                var wa = Screen.PrimaryScreen.WorkingArea;
                Size = new Size((int)(wa.Width * 0.8), (int)(wa.Height * 0.8));
                BackColor = Theme.Current.Canvas;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!fullScreen) Theme.ApplyTitleBar(this);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (fullScreen)
            {
                imageRect = new RectangleF(0, 0, source.Width, source.Height);
                scale = 1f;
            }
            else
            {
                float m = Theme.F(24);
                float sx = (ClientSize.Width - 2 * m) / source.Width, sy = (ClientSize.Height - 2 * m) / source.Height;
                scale = Math.Max(0.01f, Math.Min(1f, Math.Min(sx, sy)));
                float w = source.Width * scale, h = source.Height * scale;
                imageRect = new RectangleF((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
            }
            Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            OnResize(e);
            Activate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return; }
            // arrow keys move the pointer one pixel at a time (Shift: ten), for an exact edge
            int step = e.Shift ? 10 : 1, dx = 0, dy = 0;
            if (e.KeyCode == Keys.Left) dx = -step;
            else if (e.KeyCode == Keys.Right) dx = step;
            else if (e.KeyCode == Keys.Up) dy = -step;
            else if (e.KeyCode == Keys.Down) dy = step;
            if (dx != 0 || dy != 0)
            {
                try { var c = Cursor.Position; Cursor.Position = new Point(c.X + dx, c.Y + dy); } catch { }
                e.Handled = true;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); mouseInside = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); mouseInside = false; Invalidate(); }

        /// <summary>Window snips: the window under a point (the front-most one), or the whole screen it is on.</summary>
        Rectangle WindowAt(Point client)
        {
            var ip = Point.Truncate(ToImage(client));
            var all = new Rectangle(0, 0, source.Width, source.Height);
            foreach (var w in Windows)
            {
                if (!w.Contains(ip)) continue;
                var r = w; r.Intersect(all);
                if (r.Width >= 3 && r.Height >= 3) return r;
            }
            try
            {
                var scr = Screen.FromPoint(new Point(ip.X + screenOrigin.X, ip.Y + screenOrigin.Y)).Bounds;
                scr.Offset(-screenOrigin.X, -screenOrigin.Y);
                scr.Intersect(all);
                if (scr.Width >= 3 && scr.Height >= 3) return scr;
            }
            catch { }
            return all;
        }

        RectangleF ToClient(Rectangle imagePixels)
        {
            return new RectangleF(imageRect.X + imagePixels.X * scale, imageRect.Y + imagePixels.Y * scale, imagePixels.Width * scale, imagePixels.Height * scale);
        }

        PointF ToImage(Point p)
        {
            return new PointF(
                Math.Max(0, Math.Min(source.Width, (p.X - imageRect.X) / scale)),
                Math.Max(0, Math.Min(source.Height, (p.Y - imageRect.Y) / scale)));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; Close(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (shape == SnipShape.Window)
            {
                var r = WindowAt(e.Location);
                ResultRect = r;
                Result = source.Clone(r, PixelFormat.Format32bppArgb);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            dragging = true;
            start = now = e.Location;
            lasso.Clear();
            lasso.Add(ToImage(e.Location));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            now = e.Location;
            mouseInside = true;
            if (dragging && shape == SnipShape.Freeform)
            {
                var ip = ToImage(e.Location);
                var last = lasso[lasso.Count - 1];
                if (Math.Abs(ip.X - last.X) + Math.Abs(ip.Y - last.Y) >= 1.5f / scale) lasso.Add(ip);
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging || e.Button != MouseButtons.Left) return;
            dragging = false;
            Result = shape == SnipShape.Rectangle ? CutRectangle() : CutFreeform();
            if (Result != null) { DialogResult = DialogResult.OK; Close(); }
            else Invalidate();
        }

        Bitmap CutRectangle()
        {
            var a = ToImage(start);
            var b = ToImage(now);
            var r = Rectangle.FromLTRB((int)Math.Floor(Math.Min(a.X, b.X)), (int)Math.Floor(Math.Min(a.Y, b.Y)),
                                       (int)Math.Ceiling(Math.Max(a.X, b.X)), (int)Math.Ceiling(Math.Max(a.Y, b.Y)));
            r.Intersect(new Rectangle(0, 0, source.Width, source.Height));
            if (r.Width < 3 || r.Height < 3) return null;
            ResultRect = r;
            return source.Clone(r, PixelFormat.Format32bppArgb);
        }

        Bitmap CutFreeform()
        {
            if (lasso.Count < 3) return null;
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(lasso.ToArray());
                var bounds = Rectangle.Ceiling(path.GetBounds());
                bounds.Intersect(new Rectangle(0, 0, source.Width, source.Height));
                if (bounds.Width < 3 || bounds.Height < 3) return null;
                var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TranslateTransform(-bounds.X, -bounds.Y);
                    using (var tb = new TextureBrush(source, WrapMode.Clamp))
                        g.FillPath(tb, path);
                }
                return bmp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(fullScreen ? Color.Black : Theme.Current.Canvas);
            g.InterpolationMode = scale < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
            g.DrawImage(source, imageRect);

            if (shape == SnipShape.Window)
            {
                PaintWindowPick(g);
                return;
            }

            using (var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            using (var region = new Region(imageRect))
            {
                if (dragging)
                {
                    if (shape == SnipShape.Rectangle)
                        region.Exclude(Rectangle.FromLTRB(Math.Min(start.X, now.X), Math.Min(start.Y, now.Y), Math.Max(start.X, now.X), Math.Max(start.Y, now.Y)));
                    else if (lasso.Count >= 3)
                        using (var path = ScreenPath()) region.Exclude(path);
                }
                g.FillRegion(shade, region);
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Theme.Current;
            if (dragging)
            {
                using (var b = new LinearGradientBrush(ClientRectangle, p.AccentA, p.AccentB, 0f))
                using (var pen = new Pen(b, Theme.F(2)))
                {
                    if (shape == SnipShape.Rectangle)
                        g.DrawRectangle(pen, Rectangle.FromLTRB(Math.Min(start.X, now.X), Math.Min(start.Y, now.Y), Math.Max(start.X, now.X), Math.Max(start.Y, now.Y)));
                    else if (lasso.Count >= 2)
                        using (var path = ScreenPath()) g.DrawPath(pen, path);
                }
                if (shape == SnipShape.Rectangle)
                {
                    var a = ToImage(start); var b2 = ToImage(now);
                    string label = (int)Math.Abs(b2.X - a.X) + " × " + (int)Math.Abs(b2.Y - a.Y);
                    DrawBadge(g, label, new Point(Math.Min(start.X, now.X), Math.Min(start.Y, now.Y) - Theme.S(28)));
                }
            }
            else
            {
                string msg = Purpose ?? (shape == SnipShape.Rectangle ? "Drag to snip a rectangle" : "Draw around the area to snip");
                DrawBadge(g, msg + "   ·   Esc to cancel", new Point(-1, Theme.S(24)));
            }
            if (fullScreen && mouseInside && shape == SnipShape.Rectangle) DrawLoupe(g);
        }

        void PaintWindowPick(Graphics g)
        {
            var p = Theme.Current;
            RectangleF pick = mouseInside ? ToClient(WindowAt(now)) : RectangleF.Empty;
            using (var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            using (var region = new Region(imageRect))
            {
                if (!pick.IsEmpty) region.Exclude(pick);
                g.FillRegion(shade, region);
            }
            if (!pick.IsEmpty)
            {
                g.SmoothingMode = SmoothingMode.None;
                float t = Theme.F(3);
                using (var b = new LinearGradientBrush(ClientRectangle, p.AccentA, p.AccentB, 0f))
                using (var pen = new Pen(b, t) { Alignment = PenAlignment.Inset })
                    g.DrawRectangle(pen, pick.X, pick.Y, pick.Width - 1, pick.Height - 1);
                var r = WindowAt(now);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawBadge(g, r.Width + " × " + r.Height, new Point((int)pick.X + Theme.S(10), (int)pick.Y + Theme.S(10)));
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawBadge(g, (Purpose ?? "Click a window to snip it") + "   ·   Esc to cancel", new Point(-1, Theme.S(24)));
        }

        /// <summary>A magnified view of the pixels under the pointer, for placing an edge exactly.</summary>
        void DrawLoupe(Graphics g)
        {
            const int zoom = 6;
            int cells = (Theme.S(126) / zoom) | 1;           // an odd number, so one pixel sits in the middle
            int size = cells * zoom;
            var ip = ToImage(now);
            int cx = Math.Min(source.Width - 1, (int)ip.X), cy = Math.Min(source.Height - 1, (int)ip.Y);
            int gap = Theme.S(22), textH = Theme.S(22);
            int x = now.X + gap, y = now.Y + gap;
            if (x + size > ClientSize.Width - 4) x = now.X - gap - size;
            if (y + size + textH > ClientSize.Height - 4) y = now.Y - gap - size - textH;
            var box = new Rectangle(x, y, size, size);

            var state = g.Save();
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (var back = new SolidBrush(Color.Black)) g.FillRectangle(back, box);
            // only the part of the neighbourhood that is inside the picture
            var want = new Rectangle(cx - cells / 2, cy - cells / 2, cells, cells);
            var have = want; have.Intersect(new Rectangle(0, 0, source.Width, source.Height));
            if (have.Width > 0 && have.Height > 0)
            {
                var dst = new Rectangle(box.X + (have.X - want.X) * zoom, box.Y + (have.Y - want.Y) * zoom, have.Width * zoom, have.Height * zoom);
                g.DrawImage(source, dst, have, GraphicsUnit.Pixel);
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;
            int mid = cells / 2 * zoom;
            using (var line = new Pen(Color.FromArgb(150, 255, 255, 255), 1f))
            {
                g.DrawLine(line, box.X, box.Y + mid + zoom / 2, box.X + mid - 1, box.Y + mid + zoom / 2);
                g.DrawLine(line, box.X + mid + zoom, box.Y + mid + zoom / 2, box.Right - 1, box.Y + mid + zoom / 2);
                g.DrawLine(line, box.X + mid + zoom / 2, box.Y, box.X + mid + zoom / 2, box.Y + mid - 1);
                g.DrawLine(line, box.X + mid + zoom / 2, box.Y + mid + zoom, box.X + mid + zoom / 2, box.Bottom - 1);
            }
            using (var cell = new Pen(Color.White, 1f)) g.DrawRectangle(cell, box.X + mid, box.Y + mid, zoom - 1, zoom - 1);
            using (var b = new LinearGradientBrush(box, Theme.Current.AccentA, Theme.Current.AccentB, 0f))
            using (var pen = new Pen(b, Theme.F(2))) g.DrawRectangle(pen, box.X - 1, box.Y - 1, box.Width + 1, box.Height + 1);
            g.Restore(state);

            var f = Theme.UiFont(9f);
            var label = new Rectangle(box.X - 1, box.Bottom + 1, box.Width + 2, textH);
            using (var b = new SolidBrush(Color.FromArgb(225, Theme.Current.OverlayBg))) g.FillRectangle(b, label);
            TextRenderer.DrawText(g, (cx + screenOrigin.X) + ", " + (cy + screenOrigin.Y), f, label, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        GraphicsPath ScreenPath()
        {
            var pts = new PointF[lasso.Count];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new PointF(imageRect.X + lasso[i].X * scale, imageRect.Y + lasso[i].Y * scale);
            var path = new GraphicsPath();
            if (pts.Length >= 3) path.AddPolygon(pts); else path.AddLines(pts);
            return path;
        }

        void DrawBadge(Graphics g, string text, Point at)
        {
            var f = Theme.UiFont(10.5f);
            var sz = TextRenderer.MeasureText(g, text, f);
            var r = new Rectangle(at.X < 0 ? (ClientSize.Width - sz.Width) / 2 - Theme.S(14) : at.X, Math.Max(Theme.S(4), at.Y), sz.Width + Theme.S(28), sz.Height + Theme.S(12));
            using (var path = Theme.Rounded(r, r.Height / 2f))
            {
                using (var b = new SolidBrush(Color.FromArgb(225, Theme.Current.OverlayBg))) g.FillPath(b, path);
                using (var b = new LinearGradientBrush(r, Theme.Current.AccentA, Theme.Current.AccentB, 0f))
                using (var pen = new Pen(b, Theme.F(1.2f))) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, text, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// Snipping-Tool-style pop-out for still pictures: capture part of the screen or of the current picture,
    /// annotate it, then copy or save. Annotation reuses ImageCanvas and EditSession.
    /// </summary>
    internal sealed class SnipForm : Form
    {
        readonly Func<Bitmap> getPicture;
        readonly Action<Bitmap> sendToEditor;
        readonly Form host;
        readonly Settings settings;
        int delay;                // seconds to wait before a screen snip, like Snip & Sketch's timer
        static readonly int[] Delays = { 0, 3, 5, 10 };
        readonly System.Windows.Forms.Timer delayTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        int delayLeft;
        double delayOpacity;
        DelayBadge badge;
        readonly ImageCanvas canvas = new ImageCanvas();
        readonly ThemedPanel bar = new ThemedPanel { Surface = Surface.Bar, BorderBottom = true };
        readonly FlowLayoutPanel flow = new FlowLayoutPanel();
        EditSession session;
        SnipShape shape = SnipShape.Rectangle;
        bool fromScreen = true;
        EditTool tool = EditTool.Pen;
        readonly Dictionary<EditTool, Color> colors = new Dictionary<EditTool, Color>();
        readonly Dictionary<EditTool, int> sizes = new Dictionary<EditTool, int>();

        ThemedButton bNew, bRect, bFree, bWin, bFull, bScreen, bPicture, bDelay, bMove, bPen, bHigh, bArrow, bBox, bText, bStep, bHide, bErase, bColor, bUndo, bRedo, bClear, bCopy, bSave, bSend;
        ThemedSlider sizeSlider;

        public SnipForm(Form host, Settings settings, Func<Bitmap> getPicture, Action<Bitmap> sendToEditor)
        {
            this.host = host;
            this.settings = settings;
            this.getPicture = getPicture;
            this.sendToEditor = sendToEditor;
            if (settings != null) delay = Array.IndexOf(Delays, settings.GetInt("Snip.Delay", 0)) >= 0 ? settings.GetInt("Snip.Delay", 0) : 0;
            if (settings != null)
            {
                int m = settings.GetInt("Snip.Mode", 0);
                if (Enum.IsDefined(typeof(SnipShape), m)) shape = (SnipShape)m;
                canvas.Options.RedactPixelate = settings.GetBool("HidePixelate", true);
                canvas.Options.FontName = settings.GetString("TextFont", "Segoe UI");
            }
            Text = "Snip";
            // its own window in the taskbar: minimise and maximise it without touching the main window
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.UiFont(9.5f);
            KeyPreview = true;
            MinimumSize = new Size(Theme.S(560), Theme.S(380));
            try { if (Theme.AppIcon != null) Icon = Theme.AppIcon; } catch { }

            colors[EditTool.Pen] = Color.FromArgb(255, 232, 17, 35);
            colors[EditTool.Highlighter] = Color.FromArgb(255, 255, 255, 0);
            colors[EditTool.Line] = colors[EditTool.Text] = colors[EditTool.Step] = Color.FromArgb(255, 232, 17, 35);
            sizes[EditTool.Pen] = 4; sizes[EditTool.Highlighter] = 18; sizes[EditTool.Eraser] = 16;
            sizes[EditTool.Line] = 4; sizes[EditTool.Text] = 22; sizes[EditTool.Step] = 14; sizes[EditTool.Redact] = 12; sizes[EditTool.Select] = 4;

            flow.Dock = DockStyle.Fill;
            flow.WrapContents = true;
            flow.Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(10), Theme.S(4));
            flow.BackColor = Theme.Current.Bar;
            bar.Controls.Add(flow);
            bar.Dock = DockStyle.Top;

            bNew = Btn(Ico.Plus, "New snip", "Capture a new snip (Ctrl+N)", (s, e) => NewSnip(), 112);
            bNew.Primary = true;
            bRect = Btn(Ico.RectSelect, null, "Rectangle snip: drag a box. A magnifier shows the exact pixels; arrow keys move one pixel at a time", (s, e) => SetShape(SnipShape.Rectangle), 40);
            bFree = Btn(Ico.Lasso, null, "Freeform snip: draw around any shape", (s, e) => SetShape(SnipShape.Freeform), 40);
            bWin = Btn(Ico.Window, null, "Window snip: click a window to snip just that window", (s, e) => SetShape(SnipShape.Window), 40);
            bFull = Btn(Ico.Screen, null, "Full-screen snip: the whole of the screen the pointer is on, straight away", (s, e) => SetShape(SnipShape.FullScreen), 40);
            Sep();
            bScreen = Btn(Ico.Screen, "Screen", "Snip from your screen", (s, e) => { fromScreen = true; Sync(); }, 92);
            bPicture = Btn(Ico.Picture, "Picture", "Snip from the picture open in Stills & Reels", (s, e) => { fromScreen = false; Sync(); }, 96);
            bDelay = Btn(Ico.Timer, "No delay", "Wait before snipping the screen, so you can open a menu or tooltip first", (s, e) => ShowDelays(), 104);
            Sep();
            bMove = Btn(Ico.Pointer, null, "Select: click an arrow, box, number or text to move, resize or delete it", (s, e) => SelectTool(EditTool.Select), 40);
            bPen = Btn(Ico.Pen, null, "Pen", (s, e) => SelectTool(EditTool.Pen), 40);
            bHigh = Btn(Ico.Highlighter, null, "Highlighter", (s, e) => SelectTool(EditTool.Highlighter), 40);
            bArrow = Btn(Ico.ArrowTool, null, "Arrow: drag from the tail to the point", (s, e) => SelectTool(EditTool.Arrow), 40);
            bBox = Btn(Ico.RectTool, null, "Box: drag a rectangle around something", (s, e) => SelectTool(EditTool.Rect), 40);
            bText = Btn(Ico.Text, null, "Text: click where it should go and type", (s, e) => SelectTool(EditTool.Text), 40);
            bStep = Btn(Ico.StepTool, null, "Numbered step: each click places the next number", (s, e) => SelectTool(EditTool.Step), 40);
            bHide = Btn(Ico.Redact, null, "Hide: drag over something private to pixelate it for good", (s, e) => SelectTool(EditTool.Redact), 40);
            bErase = Btn(Ico.Eraser, null, "Eraser – removes whole strokes", (s, e) => SelectTool(EditTool.Eraser), 40);
            bColor = Btn(Ico.None, null, "Ink colour", (s, e) => ShowColors(), 40);
            sizeSlider = new ThemedSlider { Minimum = 1, Maximum = 30, Width = Theme.S(96), Height = Theme.S(36), Margin = new Padding(Theme.S(2), Theme.S(2), Theme.S(2), Theme.S(2)) };
            sizeSlider.ValueChanged += (s, e) => { sizes[Key(tool)] = sizeSlider.Value; ApplyTool(); };
            Ui.Tip.SetToolTip(sizeSlider, "Size");
            flow.Controls.Add(sizeSlider);
            Sep();
            bUndo = Btn(Ico.Undo, null, "Undo (Ctrl+Z)", (s, e) => { if (session != null) session.Undo(); }, 40);
            bRedo = Btn(Ico.Redo, null, "Redo (Ctrl+Y)", (s, e) => { if (session != null) session.Redo(); }, 40);
            bClear = Btn(Ico.Trash, null, "Clear all annotations", (s, e) => { if (session != null) session.ClearInk(); }, 40);
            Sep();
            bCopy = Btn(Ico.Copy, "Copy", "Copy the snip (Ctrl+C)", (s, e) => CopySnip(), 86);
            bSave = Btn(Ico.Save, "Save", "Save the snip as a picture (Ctrl+S)", (s, e) => SaveSnip(), 80);
            bSend = Btn(Ico.Paste, "Paste into editor", "Paste the snip into the picture editor as a layer you can move and resize (Ctrl+E)", (s, e) => SendToEditor(), 150);
            bSend.Primary = true;

            canvas.Dock = DockStyle.Fill;
            canvas.EmptyIcon = Ico.Snip;
            canvas.EmptyTitle = "Snip part of your screen or picture";
            canvas.EmptyDetail = "Choose Rectangle, Freeform, Window or Full screen, pick Screen or Picture, then press New snip.";
            Controls.Add(canvas);
            Controls.Add(bar);

            Bounds = SnipWindows.PlaceBeside(host, Theme.S(980), Theme.S(640));

            delayTimer.Tick += (s, e) => DelayTick();
            Theme.Changed += OnTheme;
            Sync();
        }

        void OnTheme(object s, EventArgs e)
        {
            flow.BackColor = Theme.Current.Bar;
            Theme.ApplyTo(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTo(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnTheme;
                CancelDelay();
                delayTimer.Dispose();
                canvas.Edit = null;
                if (session != null) { session.Dispose(); session = null; }
            }
            base.Dispose(disposing);
        }

        ThemedButton Btn(Ico icon, string text, string tip, EventHandler click, int width)
        {
            var b = new ThemedButton { Icon = icon, Text = text, IconOnly = text == null, Tip = tip, Compact = text == null, IconSize = 19f };
            b.Size = new Size(Theme.S(width), Theme.S(36));
            b.Margin = new Padding(Theme.S(2));
            b.Click += click;
            flow.Controls.Add(b);
            return b;
        }

        void Sep()
        {
            var sp = new Divider { Width = Theme.S(13), Height = Theme.S(36), Margin = new Padding(0, Theme.S(2), 0, Theme.S(2)) };
            flow.Controls.Add(sp);
        }

        sealed class Divider : Control
        {
            public Divider() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Current.Bar);
                using (var pen = new Pen(Theme.Current.BarBorder)) e.Graphics.DrawLine(pen, Width / 2, Theme.S(6), Width / 2, Height - Theme.S(6));
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            // grow the toolbar when the buttons wrap onto a second row
            if (flow != null && bar != null && flow.Controls.Count > 0)
            {
                int bottom = 0;
                foreach (Control c in flow.Controls) if (c.Visible) bottom = Math.Max(bottom, c.Bottom + c.Margin.Bottom);
                int want = bottom + flow.Padding.Bottom + Theme.S(2);
                if (want > Theme.S(30) && bar.Height != want) bar.Height = want;
            }
            base.OnLayout(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PerformLayout();
        }

        public bool HasSnip { get { return session != null; } }

        /// <summary>Tools that share one colour and size (all the shapes).</summary>
        static EditTool Key(EditTool t) { return t == EditTool.Arrow || t == EditTool.Rect || t == EditTool.RoundRect || t == EditTool.Ellipse ? EditTool.Line : t; }

        void SetShape(SnipShape sh)
        {
            shape = sh;
            if (settings != null) { settings.Set("Snip.Mode", (int)shape); try { settings.Save(); } catch { } }
            Sync();
        }

        void Sync()
        {
            bRect.Checked = shape == SnipShape.Rectangle;
            bFree.Checked = shape == SnipShape.Freeform;
            bWin.Checked = shape == SnipShape.Window;
            bFull.Checked = shape == SnipShape.FullScreen;
            bWin.Enabled = fromScreen;      // a picture has no windows in it; it falls back to a rectangle
            bScreen.Checked = fromScreen;
            bPicture.Checked = !fromScreen;
            bDelay.Enabled = fromScreen;
            bDelay.Text = delay == 0 ? "No delay" : delay + " s delay";
            bDelay.Checked = fromScreen && delay > 0;
            bool has = session != null;
            bool sizeMatters = tool != EditTool.Select;
            bool colourMatters = tool != EditTool.Eraser && tool != EditTool.Redact && tool != EditTool.Select;
            bMove.Enabled = bPen.Enabled = bHigh.Enabled = bArrow.Enabled = bBox.Enabled = bText.Enabled = bStep.Enabled = bHide.Enabled = bErase.Enabled = has;
            bColor.Enabled = has;
            sizeSlider.Enabled = has && sizeMatters;
            bMove.Checked = has && tool == EditTool.Select;
            bPen.Checked = has && tool == EditTool.Pen;
            bHigh.Checked = has && tool == EditTool.Highlighter;
            bArrow.Checked = has && tool == EditTool.Arrow;
            bBox.Checked = has && tool == EditTool.Rect;
            bText.Checked = has && tool == EditTool.Text;
            bStep.Checked = has && tool == EditTool.Step;
            bHide.Checked = has && tool == EditTool.Redact;
            bErase.Checked = has && tool == EditTool.Eraser;
            bColor.Visible = colourMatters;
            Color c;
            bColor.Swatch = colors.TryGetValue(Key(tool), out c) ? c : (Color?)null;
            bColor.Invalidate();
            bUndo.Enabled = has && session.CanUndo;
            bRedo.Enabled = has && session.CanRedo;
            bClear.Enabled = has && session.HasInk;
            bCopy.Enabled = bSave.Enabled = bSend.Enabled = has;
            sizeSlider.Minimum = tool == EditTool.Text ? 8 : tool == EditTool.Redact ? 3 : tool == EditTool.Step ? 6 : 1;
            sizeSlider.Maximum = tool == EditTool.Text ? 96 : tool == EditTool.Pen || Key(tool) == EditTool.Line ? 30 : 60;
            sizeSlider.SetSilently(Math.Max(sizeSlider.Minimum, Math.Min(sizeSlider.Maximum, sizes[Key(tool)])));
            Ui.Tip.SetToolTip(sizeSlider, tool == EditTool.Text ? "Text size" : tool == EditTool.Redact ? "How strongly it is hidden" : tool == EditTool.Step ? "Size of the number" : "Line thickness");
            Text = has ? "Snip – " + session.Size.Width + " × " + session.Size.Height + (session.IsDirty ? " *" : "") : "Snip";
        }

        void SelectTool(EditTool t)
        {
            if (canvas.IsEditingText) canvas.EndTextEdit(true);
            tool = t;
            ApplyTool();
            Sync();
        }

        void ApplyTool()
        {
            canvas.Tool = tool;
            Color c;
            canvas.ToolColor = colors.TryGetValue(Key(tool), out c) ? c : Color.Black;
            int size = sizes[Key(tool)];
            if (tool == EditTool.Redact) canvas.Options.RedactStrength = Math.Max(3, Math.Min(60, size));
            canvas.ToolSize = size * Theme.Scale;
            if (canvas.IsEditingText) canvas.RestyleTextBox();
            canvas.RefreshTool();
        }

        void ShowColors()
        {
            if (tool == EditTool.Eraser || tool == EditTool.Redact || tool == EditTool.Select) return;
            EditTool key = Key(tool);
            var menu = new ContextMenuStrip();
            var palette = tool == EditTool.Highlighter ? Palettes.Highlighter : Palettes.Ink;
            foreach (var kv in palette)
            {
                var col = kv.Value;
                var mi = new ToolStripMenuItem(kv.Key, Palettes.SwatchImage(col, Theme.S(16))) { Checked = col.ToArgb() == colors[key].ToArgb() };
                mi.Click += (s, e) => { colors[key] = col; ApplyTool(); Sync(); };
                menu.Items.Add(mi);
            }
            menu.Items.Add(new ToolStripSeparator());
            var more = new ToolStripMenuItem("More colours…");
            more.Click += (s, e) =>
            {
                using (var dlg = new ColorDialog { Color = colors[key], FullOpen = true })
                    if (dlg.ShowDialog(this) == DialogResult.OK) { colors[key] = Color.FromArgb(255, dlg.Color); ApplyTool(); Sync(); }
            };
            menu.Items.Add(more);
            menu.Closed += (s, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            Ui.ShowMenuBelow(menu, bColor);
        }

        void ShowDelays()
        {
            var menu = new ContextMenuStrip();
            foreach (int d in Delays)
            {
                int sec = d;
                var mi = new ToolStripMenuItem(d == 0 ? "No delay" : d + " seconds") { Checked = d == delay };
                mi.Click += (s, e) =>
                {
                    delay = sec;
                    if (settings != null) { settings.Set("Snip.Delay", delay); try { settings.Save(); } catch { } }
                    Sync();
                };
                menu.Items.Add(mi);
            }
            menu.Closed += (s, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            Ui.ShowMenuBelow(menu, bDelay);
        }

        public bool Waiting { get { return delayTimer.Enabled; } }

        /// <summary>Hides the Snip window and counts down; the screen is captured when it reaches zero.</summary>
        void StartDelay()
        {
            delayOpacity = Opacity;
            Opacity = 0;    // like a screen snip: only the Snip window gets out of the way
            delayLeft = delay;
            badge = new DelayBadge();
            badge.Clicked += (s, e) => CancelDelay();
            badge.ShowCount(delayLeft);
            delayTimer.Start();
        }

        void DelayTick()
        {
            delayLeft--;
            if (delayLeft > 0) { if (badge != null) badge.ShowCount(delayLeft); return; }
            delayTimer.Stop();
            CloseBadge();
            var result = CaptureScreen(delayOpacity);
            if (result != null) TakeSnip(result);
        }

        void CancelDelay()
        {
            if (!delayTimer.Enabled) return;
            delayTimer.Stop();
            CloseBadge();
            if (!IsDisposed) { Opacity = delayOpacity; Activate(); }
        }

        void CloseBadge()
        {
            if (badge == null) return;
            badge.Close();
            badge.Dispose();
            badge = null;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Waiting)
            {
                // while the timer runs the window is invisible: Esc cancels, other keys are left alone
                if (keyData == Keys.Escape) { CancelDelay(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            // while words are being typed onto the snip, the text box gets its own editing keys
            if (canvas.IsEditingText && keyData != (Keys.Control | Keys.S) && keyData != (Keys.Control | Keys.E) && keyData != (Keys.Control | Keys.N))
                return base.ProcessCmdKey(ref msg, keyData);
            switch (keyData)
            {
                case Keys.Control | Keys.N: NewSnip(); return true;
                case Keys.Control | Keys.C: if (session != null) { CopySnip(); return true; } break;
                case Keys.Control | Keys.S: if (session != null) { SaveSnip(); return true; } break;
                case Keys.Control | Keys.E: if (session != null) { SendToEditor(); return true; } break;
                case Keys.Control | Keys.Z: if (session != null) session.Undo(); return true;
                case Keys.Control | Keys.Y: if (session != null) session.Redo(); return true;
                case Keys.Control | Keys.Shift | Keys.Z: if (session != null) session.Redo(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        bool ConfirmDiscard()
        {
            if (session == null || !session.IsDirty) return true;
            var r = MessageBox.Show(this, "Save the current snip first?", "Snip", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return SaveSnip();
            return true;
        }

        public void NewSnip()
        {
            if (Waiting) return;
            if (!ConfirmDiscard()) return;
            if (fromScreen && delay > 0) { StartDelay(); return; }
            Bitmap result = null;
            if (fromScreen) result = CaptureScreen(Opacity);
            else
            {
                var pic = getPicture != null ? getPicture() : null;
                if (pic == null)
                {
                    MessageBox.Show(this, "There's no picture open in Stills & Reels to snip from.\nChoose Screen, or open a picture first.",
                        "Snip", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (shape == SnipShape.FullScreen) result = pic;    // the whole picture
                else
                    using (pic)
                    using (var sel = new SnipSelector(pic, shape == SnipShape.Freeform ? SnipShape.Freeform : SnipShape.Rectangle, false, Rectangle.Empty))
                    {
                        if (sel.ShowDialog(this) == DialogResult.OK) result = sel.Result;
                    }
            }
            if (result == null) return;
            TakeSnip(result);
        }

        void TakeSnip(Bitmap result)
        {
            SetSnip(result);
            // like the Windows Snipping Tool: a new snip is on the clipboard straight away
            if (CopySnip(true)) Text = "Snip – " + session.Size.Width + " × " + session.Size.Height + " – copied to the clipboard";
        }

        void SendToEditor()
        {
            if (session == null || sendToEditor == null) return;
            Settle();
            sendToEditor(session.Flatten());
        }

        /// <param name="myOpacity">the opacity to give the Snip window back afterwards</param>
        Bitmap CaptureScreen(double myOpacity)
        {
            var vs = SystemInformation.VirtualScreen;
            Bitmap shot = null;
            try
            {
                // hide only the Snip window; the main window stays visible so it can be snipped
                Opacity = 0;
                Application.DoEvents();
                Thread.Sleep(220);
                shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
            }
            catch (Exception ex)
            {
                if (shot != null) shot.Dispose();
                Opacity = myOpacity;
                MessageBox.Show(this, "Couldn't capture the screen: " + ex.Message, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            try
            {
                using (shot)
                {
                    if (shape == SnipShape.FullScreen)
                    {
                        // the whole of the screen the pointer is on, with nothing to drag
                        var scr = Screen.FromPoint(Cursor.Position).Bounds;
                        scr.Offset(-vs.X, -vs.Y);
                        scr.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
                        if (scr.Width < 1 || scr.Height < 1) scr = new Rectangle(0, 0, shot.Width, shot.Height);
                        return shot.Clone(scr, PixelFormat.Format32bppArgb);
                    }
                    using (var sel = new SnipSelector(shot, shape, true, vs))
                    {
                        if (shape == SnipShape.Window)
                            foreach (var w in ScreenWindows.List(Handle))
                                sel.Windows.Add(new Rectangle(w.X - vs.X, w.Y - vs.Y, w.Width, w.Height));
                        return sel.ShowDialog() == DialogResult.OK ? sel.Result : null;
                    }
                }
            }
            finally
            {
                Opacity = myOpacity;
                Activate();
            }
        }

        /// <summary>Finishes anything half-done on the snip (text being typed) before it is copied, saved or sent.</summary>
        void Settle()
        {
            if (canvas.IsEditingText) canvas.EndTextEdit(true);
            canvas.CommitPaste();
        }

        void SetSnip(Bitmap bmp)
        {
            canvas.Edit = null;
            if (session != null) session.Dispose();
            session = new EditSession(bmp, null);
            bmp.Dispose();
            session.Changed += (s, e) => Sync();
            canvas.ShowImage(null, false);
            canvas.Edit = session;
            canvas.FitToWindow();
            SelectTool(tool == EditTool.Eraser ? EditTool.Pen : tool);
            Sync();
            canvas.Focus();
        }

        bool CopySnip(bool quiet = false)
        {
            if (session == null) return false;
            Settle();
            using (var bmp = session.Flatten())
            {
                try
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.Bitmap, true, bmp);
                    var png = new MemoryStream();
                    bmp.Save(png, ImageFormat.Png);
                    data.SetData("PNG", false, png);
                    Clipboard.SetDataObject(data, true);
                    return true;
                }
                catch (Exception ex)
                {
                    if (!quiet) MessageBox.Show(this, "Couldn't copy the snip: " + ex.Message, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
        }

        bool SaveSnip()
        {
            if (session == null) return false;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Save snip";
                dlg.Filter = FileTypes.SaveFilter;
                dlg.FilterIndex = 1;
                dlg.AddExtension = true;
                dlg.DefaultExt = "png";
                dlg.OverwritePrompt = true;
                try { dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); } catch { }
                dlg.FileName = "Snip " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".png";
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                try
                {
                    Settle();
                    if (PicProject.IsProject(dlg.FileName)) PicProject.Save(session.State, dlg.FileName);   // keeps arrows and text editable
                    else using (var bmp = session.Flatten()) ImageSaver.Save(bmp, dlg.FileName);
                    session.MarkSaved();
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't save the snip:\n" + ex.Message, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CancelDelay();
            if (e.CloseReason == CloseReason.UserClosing && !ConfirmDiscard()) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
    }

    /// <summary>The windows on the desktop, for window snips. Empty when not on Windows.</summary>
    internal static class ScreenWindows
    {
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetWindowAttributeInt(IntPtr hWnd, int attribute, out int value, int size);

        /// <summary>Visible windows, front first, as desktop rectangles (the frame you see, without its invisible resize border).</summary>
        public static List<Rectangle> List(IntPtr skip)
        {
            var found = new List<Rectangle>();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return found;
            try
            {
                EnumWindows((h, l) =>
                {
                    try
                    {
                        if (h == skip || !IsWindowVisible(h) || IsIconic(h)) return true;
                        int ex = GetWindowLong(h, -20);
                        if ((ex & 0x20) != 0) return true;                       // click-through overlays
                        int cloaked;
                        if (DwmGetWindowAttributeInt(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;   // hidden by Windows (other desktops, suspended apps)
                        var name = new System.Text.StringBuilder(64);
                        GetClassName(h, name, 64);
                        string cls = name.ToString();
                        if (cls == "Progman" || cls == "WorkerW") return true;   // the desktop itself: handled as "the whole screen"
                        RECT r;
                        if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0 && !GetWindowRect(h, out r)) return true;
                        var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                        if (rect.Width >= 8 && rect.Height >= 8) found.Add(rect);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }
    }

    internal static class SnipWindows
    {
        /// <summary>
        /// Where a snip window opens: beside the main window, or in the middle of the screen when the main window
        /// is minimised (started from the taskbar's Pic Snip / Vid Snip).
        /// </summary>
        public static Rectangle PlaceBeside(Form host, int w, int h)
        {
            Rectangle hb = host == null || host.WindowState == FormWindowState.Minimized ? Rectangle.Empty : host.Bounds;
            if (hb.Width <= 0)
            {
                var sa = Screen.FromPoint(Cursor.Position).WorkingArea;
                w = Math.Min(w, sa.Width); h = Math.Min(h, sa.Height);
                return new Rectangle(sa.Left + (sa.Width - w) / 2, sa.Top + (sa.Height - h) / 2, w, h);
            }
            var wa = Screen.FromRectangle(hb).WorkingArea;
            int x = hb.Right + Theme.S(8);
            if (x + w > wa.Right) x = Math.Max(wa.Left, wa.Right - w - Theme.S(16));
            int y = Math.Max(wa.Top, Math.Min(hb.Top + Theme.S(60), wa.Bottom - h));
            return new Rectangle(x, y, w, h);
        }
    }

    /// <summary>
    /// "Snip in 3" in the corner of the screen while the snip timer runs. It never takes the focus (so an open menu
    /// in another app stays open), is kept out of screen captures, and cancels the timer when clicked.
    /// </summary>
    internal sealed class DelayBadge : Form
    {
        int seconds;
        public event EventHandler Clicked;

        public DelayBadge()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Current.OverlayBg;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(Theme.S(290), Theme.S(48));
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - Width - Theme.S(16), wa.Bottom - Height - Theme.S(16));
            using (var path = Theme.Rounded(new RectangleF(0, 0, Width, Height), Theme.F(12))) Region = new Region(path);
        }

        public void ShowCount(int secondsLeft)
        {
            seconds = secondsLeft;
            if (!Visible) Show(); else Invalidate();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000; // tool window, no activate
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); CaptureExclusion.Apply(this); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var h = Clicked;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.F(12)))
            using (var b = new LinearGradientBrush(ClientRectangle, Theme.Current.OverlayA, Theme.Current.OverlayB, 0f))
            using (var pen = new Pen(b, Theme.F(1.5f))) g.DrawPath(pen, path);
            float m = Theme.F(12), sz = Height - 2 * m;
            Icons.Draw(g, Ico.Timer, new RectangleF(Theme.F(14), m, sz, sz), Color.White);
            var text = new Rectangle((int)(Theme.F(14) + sz + Theme.F(10)), 0, Width - (int)(Theme.F(14) + sz + Theme.F(10)) - Theme.S(8), Height);
            TextRenderer.DrawText(g, "Snip in " + seconds + "  ·  click to cancel", Theme.UiFont(10f, FontStyle.Bold), text, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    internal static class Palettes
    {
        public static readonly KeyValuePair<string, Color>[] Ink =
        {
            P("Black", 0x000000), P("White", 0xFFFFFF), P("Grey", 0x7A7574), P("Red", 0xE81123), P("Orange", 0xF7630C),
            P("Yellow", 0xFFB900), P("Green", 0x16C60C), P("Blue", 0x0078D7), P("Purple", 0x886CE4), P("Pink", 0xE3008C), P("Teal", 0x00B7C3)
        };

        public static readonly KeyValuePair<string, Color>[] Highlighter =
        {
            P("Yellow", 0xFFFF00), P("Green", 0x66FF00), P("Aqua", 0x00FFFF), P("Pink", 0xFF00FF),
            P("Orange", 0xFFA500), P("Red", 0xFF4040), P("Blue", 0x3399FF)
        };

        static KeyValuePair<string, Color> P(string n, int rgb) { return new KeyValuePair<string, Color>(n, Color.FromArgb(255, Color.FromArgb(rgb))); }

        public static Color[] Colors(KeyValuePair<string, Color>[] pal)
        {
            var a = new Color[pal.Length];
            for (int i = 0; i < a.Length; i++) a[i] = pal[i].Value;
            return a;
        }

        public static Bitmap SwatchImage(Color c, int size)
        {
            var b = new Bitmap(size, size);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var br = new SolidBrush(c)) g.FillEllipse(br, 1, 1, size - 3, size - 3);
                using (var p = new Pen(Color.FromArgb(120, 128, 128, 128))) g.DrawEllipse(p, 1, 1, size - 3, size - 3);
            }
            return b;
        }
    }
}
