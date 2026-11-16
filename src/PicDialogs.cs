using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>The small themed dialogs of the picture editor share this frame: a title, rows of fields, OK and Cancel.</summary>
    internal class PicDialog : Form
    {
        protected readonly ThemedPanel Body = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Fill };
        protected readonly int Pad = Theme.S(22);
        protected int Y = Theme.S(18);
        protected int Inner;
        protected ThemedButton Ok, CancelButton2;
        protected bool Busy;       // true while fields are being set by code, so their change handlers stay quiet

        protected PicDialog(string title, int width)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            try { if (Theme.AppIcon != null) Icon = Theme.AppIcon; } catch { }
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.UiFont(9.5f);
            ClientSize = new Size(Theme.S(width), Theme.S(200));
            Inner = ClientSize.Width - Pad * 2;
            KeyPreview = true;
            Controls.Add(Body);
        }

        protected Label Caption(string text, int x, int y, int w)
        {
            var l = new Label { Text = text, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Current.Text, BackColor = Theme.Current.Panel, Font = Theme.UiFont(9.5f) };
            l.SetBounds(x, y, w, Theme.S(28));
            Body.Controls.Add(l);
            return l;
        }

        protected void Section(string text)
        {
            var s = new SectionLabel(text);
            s.SetBounds(Pad, Y, Inner, Theme.S(20));
            Body.Controls.Add(s);
            Y += Theme.S(26);
        }

        protected void Note(string text)
        {
            var h = new HintLabel { Text = text };
            h.SetBounds(Pad, Y, Inner, h.MeasureHeight(Inner));
            Body.Controls.Add(h);
            Y += h.Height + Theme.S(8);
        }

        protected NumericUpDown Number(string label, decimal min, decimal max, decimal value, int decimals = 0, string unit = null)
        {
            Caption(label, Pad, Y, Inner / 2);
            var p = Theme.Current;
            var n = new NumericUpDown
            {
                Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals > 0 ? 0.1m : 1m,
                Value = Math.Max(min, Math.Min(max, value)), BackColor = p.InputBg, ForeColor = p.Text, BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.UiFont(10f), TextAlign = HorizontalAlignment.Left
            };
            int unitW = unit != null ? Theme.S(34) : 0;
            n.SetBounds(Pad + Inner / 2, Y + Theme.S(2), Inner / 2 - unitW, Theme.S(26));
            Body.Controls.Add(n);
            if (unit != null) Caption(unit, n.Right + Theme.S(6), Y, unitW - Theme.S(6));
            Y += Theme.S(34);
            return n;
        }

        protected ThemedToggle Toggle(string text, bool on)
        {
            var t = new ThemedToggle { Text = text, Checked = on };
            t.SetBounds(Pad, Y, Inner, Theme.S(30));
            Body.Controls.Add(t);
            Y += Theme.S(34);
            return t;
        }

        protected SliderRow Slider(string name, int min, int max, int value)
        {
            var s = new SliderRow(name, min, max, value);
            s.SetBounds(Pad, Y, Inner, Theme.S(26));
            Body.Controls.Add(s);
            Y += Theme.S(32);
            return s;
        }

        protected void Place(Control c, int height)
        {
            c.SetBounds(Pad, Y, Inner, height);
            Body.Controls.Add(c);
            Y += height + Theme.S(10);
        }

        /// <summary>Adds OK and Cancel under everything placed so far and sizes the window to fit.</summary>
        protected void Finish(string okText = "OK", params ThemedButton[] extra)
        {
            Y += Theme.S(8);
            int bw = Theme.S(104), bh = Theme.S(36), gap = Theme.S(10);
            CancelButton2 = new ThemedButton { Text = "Cancel" };
            CancelButton2.SetBounds(ClientSize.Width - Pad - bw, Y, bw, bh);
            CancelButton2.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Ok = new ThemedButton { Text = okText, Primary = true };
            Ok.SetBounds(CancelButton2.Left - gap - bw, Y, bw, bh);
            Ok.Click += (s, e) => { if (Accept()) { DialogResult = DialogResult.OK; Close(); } };
            Body.Controls.Add(Ok);
            Body.Controls.Add(CancelButton2);
            int x = Pad;
            foreach (var b in extra)
            {
                b.SetBounds(x, Y, Theme.S(92), bh);
                Body.Controls.Add(b);
                x += b.Width + gap;
            }
            ClientSize = new Size(ClientSize.Width, Y + bh + Pad);
            Theme.ApplyTo(this);
        }

        /// <summary>Called when OK is pressed; return false to keep the dialog open.</summary>
        protected virtual bool Accept() { return true; }

        public static string FileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " bytes";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString(bytes < 10240 ? "0.0" : "0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString(bytes < 10L * 1024 * 1024 ? "0.00" : "0.0") + " MB";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (keyData == Keys.Enter && !(ActiveControl is TextBoxBase && ((TextBoxBase)ActiveControl).Multiline))
            {
                // let a number box take what was typed first
                Body.Focus();
                if (Accept()) { DialogResult = DialogResult.OK; Close(); }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------------ one-line prompts

        sealed class TextDialog : PicDialog
        {
            public readonly TextBox Box;
            public TextDialog(string title, string label, string value) : base(title, 380)
            {
                Note(label);
                var p = Theme.Current;
                Box = new TextBox { Text = value ?? "", BackColor = p.InputBg, ForeColor = p.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.UiFont(10.5f) };
                Place(Box, Theme.S(28));
                Finish();
                Shown += (s, e) => { Box.Focus(); Box.SelectAll(); };
            }
        }

        /// <summary>Asks for one line of text. Null when cancelled.</summary>
        public static string AskText(IWin32Window owner, string title, string label, string value)
        {
            using (var d = new TextDialog(title, label, value))
                return d.ShowDialog(owner) == DialogResult.OK ? d.Box.Text : null;
        }

        sealed class NumberDialog : PicDialog
        {
            public readonly NumericUpDown Box;
            public NumberDialog(string title, string label, decimal min, decimal max, decimal value, string unit) : base(title, 360)
            {
                Box = Number(label, min, max, value, 0, unit);
                Finish();
            }
        }

        /// <summary>Asks for a whole number. Null when cancelled.</summary>
        public static int? AskNumber(IWin32Window owner, string title, string label, int min, int max, int value, string unit = null)
        {
            using (var d = new NumberDialog(title, label, min, max, value, unit))
                return d.ShowDialog(owner) == DialogResult.OK ? (int?)(int)d.Box.Value : null;
        }
    }

    /// <summary>Resize: a new size in pixels or percent.</summary>
    internal sealed class ResizeDialog : PicDialog
    {
        readonly Size start;
        readonly NumericUpDown w, h, pct;
        readonly ThemedToggle keep, smooth;

        public Size NewSize { get { return new Size((int)w.Value, (int)h.Value); } }
        public bool Smooth { get { return smooth.Checked; } }

        public ResizeDialog(Size current) : base("Resize picture", 380)
        {
            start = current;
            Note("Now " + current.Width + " × " + current.Height + " pixels. Everything in the picture is scaled to the new size.");
            w = Number("Width", 1, 30000, current.Width, 0, "px");
            h = Number("Height", 1, 30000, current.Height, 0, "px");
            pct = Number("Scale", 1, 1000, 100, 1, "%");
            keep = Toggle("Keep proportions", true);
            smooth = Toggle("Smooth (best for photos; off keeps hard pixels)", true);
            w.ValueChanged += (s, e) => { if (Busy) return; Busy = true; if (keep.Checked) h.Value = Clamp(h, Math.Round(w.Value * start.Height / start.Width)); pct.Value = Clamp(pct, w.Value * 100m / start.Width); Busy = false; };
            h.ValueChanged += (s, e) => { if (Busy) return; Busy = true; if (keep.Checked) { w.Value = Clamp(w, Math.Round(h.Value * start.Width / start.Height)); pct.Value = Clamp(pct, h.Value * 100m / start.Height); } Busy = false; };
            pct.ValueChanged += (s, e) =>
            {
                if (Busy) return; Busy = true;
                w.Value = Clamp(w, Math.Round(start.Width * pct.Value / 100m)); h.Value = Clamp(h, Math.Round(start.Height * pct.Value / 100m));
                Busy = false;
            };
            keep.CheckedChanged += (s, e) => { if (keep.Checked) { Busy = true; h.Value = Clamp(h, Math.Round(w.Value * start.Height / start.Width)); Busy = false; } };
            Finish("Resize");
        }

        static decimal Clamp(NumericUpDown n, decimal v) { return Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }

        protected override bool Accept()
        {
            if ((long)w.Value * (long)h.Value > PicSurface.MaxPixels)
            {
                MessageBox.Show(this, "That is too large. Please choose a smaller size.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }
    }

    /// <summary>Canvas size: more or less room around the picture, and where the picture stays.</summary>
    internal sealed class CanvasDialog : PicDialog
    {
        readonly NumericUpDown w, h;
        readonly ThemedButton[] anchors = new ThemedButton[9];
        int anchor = 4;

        public Size NewSize { get { return new Size((int)w.Value, (int)h.Value); } }
        public float AnchorX { get { return (anchor % 3) / 2f; } }
        public float AnchorY { get { return (anchor / 3) / 2f; } }

        public CanvasDialog(Size current) : base("Canvas size", 380)
        {
            Note("Now " + current.Width + " × " + current.Height + " pixels. A bigger canvas adds empty space; a smaller one cuts the picture.");
            w = Number("Width", 1, 30000, current.Width, 0, "px");
            h = Number("Height", 1, 30000, current.Height, 0, "px");
            Section("Keep the picture at");
            int cell = Theme.S(34), gap = Theme.S(4);
            for (int i = 0; i < 9; i++)
            {
                int index = i;
                var b = new ThemedButton { IconOnly = true, Icon = Ico.None, Tip = new[] { "Top left", "Top", "Top right", "Left", "Middle", "Right", "Bottom left", "Bottom", "Bottom right" }[i] };
                b.SetBounds(Pad + (i % 3) * (cell + gap), Y + (i / 3) * (cell + gap), cell, cell);
                b.Click += (s, e) => { anchor = index; SyncAnchors(); };
                anchors[i] = b;
                Body.Controls.Add(b);
            }
            Y += 3 * (cell + gap) + Theme.S(6);
            SyncAnchors();
            Finish();
        }

        void SyncAnchors() { for (int i = 0; i < 9; i++) { anchors[i].Checked = i == anchor; anchors[i].Icon = i == anchor ? Ico.Picture : Ico.None; } }

        protected override bool Accept()
        {
            if ((long)w.Value * (long)h.Value > PicSurface.MaxPixels)
            {
                MessageBox.Show(this, "That is too large. Please choose a smaller size.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }
    }

    /// <summary>Straighten: turn the picture a few degrees, with a live preview and a grid to level against.</summary>
    internal sealed class StraightenDialog : PicDialog
    {
        sealed class Preview : Control
        {
            public Bitmap Picture;
            public float Angle;
            public bool Crop = true;

            public Preview()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var p = Theme.Current;
                g.Clear(p.PreviewBg);
                if (Picture == null) return;
                float pw = Picture.Width, ph = Picture.Height;
                var inside = Crop ? PicAdjust.LargestInside(pw, ph, Angle) : new SizeF(pw, ph);
                // scale so the kept part (or the whole turned picture) fits the box
                double a = Math.Abs(Angle * Math.PI / 180.0);
                float bw = Crop ? inside.Width : (float)(pw * Math.Cos(a) + ph * Math.Sin(a)), bh = Crop ? inside.Height : (float)(pw * Math.Sin(a) + ph * Math.Cos(a));
                float scale = Math.Min((Width - 16f) / bw, (Height - 16f) / bh);
                var box = new RectangleF((Width - bw * scale) / 2f, (Height - bh * scale) / 2f, bw * scale, bh * scale);
                var st = g.Save();
                if (Crop) g.SetClip(box);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.TranslateTransform(Width / 2f, Height / 2f);
                g.RotateTransform(Angle);
                g.ScaleTransform(scale, scale);
                g.DrawImage(Picture, -pw / 2f, -ph / 2f, pw, ph);
                g.Restore(st);
                using (var grid = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
                {
                    for (int i = 1; i < 6; i++)
                    {
                        float x = box.Left + box.Width * i / 6f, y = box.Top + box.Height * i / 6f;
                        g.DrawLine(grid, x, box.Top, x, box.Bottom);
                        g.DrawLine(grid, box.Left, y, box.Right, y);
                    }
                }
                using (var border = new Pen(p.PreviewBorder)) g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
            }
        }

        readonly Preview view = new Preview();
        readonly SliderRow coarse;
        readonly NumericUpDown exact;
        readonly ThemedToggle crop;

        public double Angle { get { return (double)exact.Value; } }
        public bool CropToFit { get { return crop.Checked; } }

        /// <summary>Takes ownership of the small preview picture.</summary>
        public StraightenDialog(Bitmap small) : base("Straighten", 520)
        {
            view.Picture = small;
            Place(view, Theme.S(300));
            coarse = Slider("Angle", -450, 450, 0);
            coarse.Tenths = true;
            exact = Number("Degrees (clockwise)", -180, 180, 0, 1, "°");
            crop = Toggle("Cut away the empty corners", true);
            coarse.ValueChanged += (s, e) => { if (Busy) return; Busy = true; exact.Value = coarse.Value / 10m; Busy = false; Sync(); };
            exact.ValueChanged += (s, e) => { if (!Busy) { Busy = true; coarse.Value = (int)Math.Max(-450, Math.Min(450, Math.Round(exact.Value * 10))); Busy = false; } Sync(); };
            crop.CheckedChanged += (s, e) => Sync();
            Note("Shapes and text on the picture become part of it when it is turned.");
            Finish("Straighten");
        }

        void Sync() { view.Angle = (float)exact.Value; view.Crop = crop.Checked; view.Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && view.Picture != null) { view.Picture.Dispose(); view.Picture = null; }
            base.Dispose(disposing);
        }
    }

    /// <summary>The brightness graph of a picture, drawn behind the Levels sliders and the curve.</summary>
    internal sealed class HistogramView : Control
    {
        public PicHistogram Data;
        public bool Colours = true;

        public HistogramView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public static void Draw(Graphics g, RectangleF r, PicHistogram h, bool colours)
        {
            if (h == null || h.Count == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (colours)
            {
                Fill(g, r, h.R, Color.FromArgb(110, 235, 60, 60), h);
                Fill(g, r, h.G, Color.FromArgb(110, 60, 200, 80), h);
                Fill(g, r, h.B, Color.FromArgb(110, 70, 110, 245), h);
            }
            Fill(g, r, h.Luma, Color.FromArgb(colours ? 120 : 170, 190, 190, 196), h);
        }

        static void Fill(Graphics g, RectangleF r, int[] ch, Color c, PicHistogram h)
        {
            // scale to the 99.5th percentile bar so one huge spike (a plain background) doesn't flatten everything
            var sorted = (int[])ch.Clone();
            Array.Sort(sorted);
            float max = Math.Max(1, sorted[250]) * 1.15f;
            var pts = new PointF[258];
            pts[0] = new PointF(r.Left, r.Bottom);
            for (int i = 0; i < 256; i++)
                pts[i + 1] = new PointF(r.Left + r.Width * i / 255f, r.Bottom - Math.Min(1f, ch[i] / max) * r.Height);
            pts[257] = new PointF(r.Right, r.Bottom);
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            e.Graphics.Clear(p.InputBg);
            Draw(e.Graphics, new RectangleF(1, 2, Width - 2, Height - 3), Data, Colours);
            using (var pen = new Pen(p.ControlBorder)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Levels: black point, midtones and white point, with the picture's histogram.</summary>
    internal sealed class LevelsDialog : PicDialog
    {
        readonly SliderRow black, mid, white, outBlack, outWhite;
        readonly Action<PicLevels> preview;
        readonly PicHistogram hist;

        public PicLevels Levels
        {
            get
            {
                var lv = new PicLevels { InBlack = black.Value, InWhite = Math.Max(black.Value + 1, white.Value), OutBlack = outBlack.Value, OutWhite = outWhite.Value };
                lv.Gamma = (float)Math.Pow(2.0, mid.Value / 50.0);
                return lv;
            }
        }

        public LevelsDialog(PicHistogram histogram, Action<PicLevels> preview) : base("Levels", 460)
        {
            hist = histogram;
            this.preview = preview;
            Place(new HistogramView { Data = histogram }, Theme.S(120));
            black = Slider("Black point", 0, 254, 0);
            mid = Slider("Midtones", -100, 100, 0);
            white = Slider("White point", 1, 255, 255);
            Section("Output range");
            outBlack = Slider("Darkest", 0, 255, 0);
            outWhite = Slider("Lightest", 0, 255, 255);
            foreach (var s in new[] { black, mid, white, outBlack, outWhite }) s.ValueChanged += (o, e) => { if (!Busy) Changed(); };
            var auto = new ThemedButton { Text = "Auto", Tip = "Set the black and white points from the picture" };
            auto.Click += (s, e) => Show(PicAdjust.AutoLevels(hist));
            var reset = new ThemedButton { Text = "Reset" };
            reset.Click += (s, e) => Show(new PicLevels());
            Finish("OK", auto, reset);
        }

        void Show(PicLevels lv)
        {
            Busy = true;
            black.Value = lv.InBlack; white.Value = lv.InWhite; outBlack.Value = lv.OutBlack; outWhite.Value = lv.OutWhite;
            mid.Value = (int)Math.Round(Math.Log(Math.Max(0.1, lv.Gamma), 2.0) * 50.0);
            Busy = false;
            Changed();
        }

        void Changed() { if (preview != null) preview(Levels); }
    }

    /// <summary>Curves: the brightness curve drawn by hand, for all colours together or one at a time.</summary>
    internal sealed class CurvesDialog : PicDialog
    {
        sealed class Editor : Control
        {
            public List<PointF>[] Channels = { Line(), Line(), Line(), Line() };   // all, red, green, blue
            public int Channel;
            public PicHistogram Hist;
            public event EventHandler Edited;
            int drag = -1;

            static List<PointF> Line() { return new List<PointF> { new PointF(0, 0), new PointF(255, 255) }; }

            public Editor()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Cross;
            }

            public void Reset(bool all)
            {
                if (all) for (int i = 0; i < 4; i++) Channels[i] = Line(); else Channels[Channel] = Line();
                Invalidate();
                var h = Edited; if (h != null) h(this, EventArgs.Empty);
            }

            RectangleF Area { get { float m = Theme.F(8); return new RectangleF(m, m, Width - m * 2, Height - m * 2); } }
            PointF ToScreen(PointF p) { var a = Area; return new PointF(a.Left + p.X / 255f * a.Width, a.Bottom - p.Y / 255f * a.Height); }
            PointF ToCurve(Point p) { var a = Area; return new PointF(Math.Max(0, Math.Min(255, (p.X - a.Left) / a.Width * 255f)), Math.Max(0, Math.Min(255, (a.Bottom - p.Y) / a.Height * 255f))); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                var pts = Channels[Channel];
                int hit = -1;
                for (int i = 0; i < pts.Count; i++)
                {
                    var s = ToScreen(pts[i]);
                    if (Math.Abs(s.X - e.X) <= Theme.F(8) && Math.Abs(s.Y - e.Y) <= Theme.F(8)) { hit = i; break; }
                }
                if (e.Button == MouseButtons.Right)
                {
                    if (hit > 0 && hit < pts.Count - 1) { pts.RemoveAt(hit); Changed(); }
                    return;
                }
                if (e.Button != MouseButtons.Left) return;
                if (hit < 0)
                {
                    var c = ToCurve(e.Location);
                    int at = pts.FindIndex(q => q.X > c.X);
                    if (at < 0) at = pts.Count;
                    if (at == 0) at = 1;
                    if (at >= pts.Count) at = pts.Count - 1;
                    pts.Insert(at, c);
                    hit = at;
                    Changed();
                }
                drag = hit;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (drag < 0) return;
                var pts = Channels[Channel];
                var c = ToCurve(e.Location);
                // a point can't pass its neighbours; the end points only move up and down along their edge
                float lo = drag > 0 ? pts[drag - 1].X + 1 : 0, hi = drag < pts.Count - 1 ? pts[drag + 1].X - 1 : 255;
                if (drag == 0) { lo = 0; hi = Math.Min(hi, 254); }
                if (drag == pts.Count - 1) { hi = 255; lo = Math.Max(lo, 1); }
                c.X = Math.Max(lo, Math.Min(hi, c.X));
                pts[drag] = c;
                Changed();
            }

            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = -1; }

            void Changed() { Invalidate(); var h = Edited; if (h != null) h(this, EventArgs.Empty); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var p = Theme.Current;
                g.Clear(p.InputBg);
                var a = Area;
                HistogramView.Draw(g, a, Hist, false);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var grid = new Pen(p.ControlBorder))
                {
                    for (int i = 0; i <= 4; i++)
                    {
                        float x = a.Left + a.Width * i / 4f, y = a.Top + a.Height * i / 4f;
                        g.DrawLine(grid, x, a.Top, x, a.Bottom);
                        g.DrawLine(grid, a.Left, y, a.Right, y);
                    }
                    g.DrawLine(grid, a.Left, a.Bottom, a.Right, a.Top);
                }
                Color[] colours = { p.Text, Color.FromArgb(240, 80, 80), Color.FromArgb(70, 200, 90), Color.FromArgb(90, 130, 250) };
                for (int ch = 0; ch < 4; ch++)
                {
                    if (ch != Channel && Channels[ch].Count == 2 && Channels[ch][0] == new PointF(0, 0) && Channels[ch][1] == new PointF(255, 255)) continue;
                    var lut = PicAdjust.CurveFromPoints(Channels[ch]);
                    var line = new PointF[256];
                    for (int i = 0; i < 256; i++) line[i] = ToScreen(new PointF(i, lut[i]));
                    using (var pen = new Pen(Color.FromArgb(ch == Channel ? 255 : 110, colours[ch]), ch == Channel ? 2f : 1.2f)) g.DrawLines(pen, line);
                }
                foreach (var pt in Channels[Channel])
                {
                    var s = ToScreen(pt);
                    float r = Theme.F(4.5f);
                    using (var b = new SolidBrush(p.Panel)) g.FillEllipse(b, s.X - r, s.Y - r, r * 2, r * 2);
                    using (var pen = new Pen(colours[Channel], 1.8f)) g.DrawEllipse(pen, s.X - r, s.Y - r, r * 2, r * 2);
                }
            }
        }

        readonly Editor editor = new Editor();
        readonly Action<byte[], byte[], byte[]> preview;

        public byte[] R, G, B;

        public CurvesDialog(PicHistogram histogram, Action<byte[], byte[], byte[]> preview) : base("Curves", 440)
        {
            this.preview = preview;
            editor.Hist = histogram;
            var channel = new SegmentBar(new[] { "All", "Red", "Green", "Blue" }, new[] { Ico.None, Ico.None, Ico.None, Ico.None });
            channel.SelectedChanged += (s, e) => { editor.Channel = channel.Selected; editor.Invalidate(); };
            Place(channel, Theme.S(36));
            Place(editor, Inner);
            Note("Click the line to add a point and drag it. Right-click a point to remove it. Up is brighter; an S shape adds contrast.");
            editor.Edited += (s, e) => Changed();
            var reset = new ThemedButton { Text = "Reset" };
            reset.Click += (s, e) => editor.Reset(true);
            Finish("OK", reset);
            Changed();
        }

        void Changed()
        {
            var all = PicAdjust.CurveFromPoints(editor.Channels[0]);
            var r = PicAdjust.CurveFromPoints(editor.Channels[1]);
            var g = PicAdjust.CurveFromPoints(editor.Channels[2]);
            var b = PicAdjust.CurveFromPoints(editor.Channels[3]);
            R = new byte[256]; G = new byte[256]; B = new byte[256];
            for (int i = 0; i < 256; i++) { R[i] = all[r[i]]; G[i] = all[g[i]]; B[i] = all[b[i]]; }
            if (preview != null) preview(R, G, B);
        }
    }

    /// <summary>Quality for a JPEG or WebP copy, with a live guess of the file size.</summary>
    internal sealed class SaveOptionsDialog : PicDialog
    {
        readonly SliderRow quality;
        readonly ThemedToggle lossless, details;
        readonly Label size;
        readonly Func<int, long> estimate;
        readonly Timer wait = new Timer { Interval = 250 };
        int asked;
        bool closed;

        public int Quality { get { return quality.Value; } }
        public bool Lossless { get { return lossless != null && lossless.Checked; } }
        public bool KeepDetails { get { return details != null && details.Checked; } }

        /// <param name="estimate">Returns the file size in bytes for a quality (run in the background), or null for no guess.</param>
        public SaveOptionsDialog(bool webp, int startQuality, bool startLossless, bool canKeepDetails, bool keepDetails, Func<int, long> estimate)
            : base(webp ? "WebP options" : "JPEG options", 400)
        {
            this.estimate = estimate;
            Note(webp ? "Higher quality keeps more detail and makes a bigger file. 80 to 90 suits most pictures."
                      : "Higher quality keeps more detail and makes a bigger file. 85 to 95 suits most photos.");
            quality = Slider("Quality", 1, 100, Math.Max(1, Math.Min(100, startQuality)));
            if (webp) lossless = Toggle("Keep every pixel exact (lossless, bigger file)", startLossless);
            if (canKeepDetails) details = Toggle("Keep photo details (date, camera, place)", keepDetails);
            size = Caption("", Pad, Y, Inner);
            size.ForeColor = Theme.Current.Muted;
            Y += Theme.S(30);
            if (canKeepDetails) Note("Photo details are left out unless you turn this on. The small preview stored inside them is always removed, so nothing you hid can show through.");
            quality.ValueChanged += (s, e) => Ask();
            if (lossless != null) lossless.CheckedChanged += (s, e) => { quality.Enabled = !lossless.Checked; };
            if (lossless != null) quality.Enabled = !lossless.Checked;
            wait.Tick += (s, e) => { wait.Stop(); Run(); };
            Finish("Save");
            Ask();
        }

        void Ask()
        {
            if (estimate == null) return;
            wait.Stop(); wait.Start();
        }

        void Run()
        {
            int q = quality.Value, mine = ++asked;
            size.Text = "Working out the size…";
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                long bytes = -1;
                try { if (!closed) bytes = estimate(q); } catch { }
                try
                {
                    if (closed || IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)(() =>
                    {
                        if (mine != asked || closed) return;
                        size.Text = bytes > 0 ? "About " + PicDialog.FileSize(bytes) + " at this quality" : "";
                    }));
                }
                catch { }
            });
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { closed = true; wait.Stop(); base.OnFormClosed(e); }

        protected override void Dispose(bool disposing)
        {
            closed = true;
            if (disposing) wait.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>What is known about the picture on screen: file facts, photo details and a histogram.</summary>
    internal sealed class DetailsDialog : PicDialog
    {
        public DetailsDialog(IList<KeyValuePair<string, string>> facts, IList<KeyValuePair<string, string>> photo, PicHistogram hist)
            : base("Picture details", 460)
        {
            Rows(facts);
            if (photo != null && photo.Count > 0)
            {
                Y += Theme.S(6);
                Section("Photo details");
                Rows(photo);
            }
            if (hist != null && hist.Count > 0)
            {
                Y += Theme.S(6);
                Section("Light and colour");
                var view = new HistogramView { Data = hist };
                Place(view, Theme.S(96));
            }
            Finish("Close");
            CancelButton2.Visible = false;
            Ok.Left = CancelButton2.Left;
        }

        void Rows(IList<KeyValuePair<string, string>> rows)
        {
            int nameW = Theme.S(120);
            foreach (var r in rows)
            {
                var name = Caption(r.Key, Pad, Y, nameW);
                name.ForeColor = Theme.Current.Muted;
                name.Height = Theme.S(24);
                var value = Caption(r.Value, Pad + nameW, Y, Inner - nameW);
                value.Height = Theme.S(24);
                value.AutoEllipsis = true;
                Ui.Tip.SetToolTip(value, r.Value);
                Y += Theme.S(24);
            }
        }
    }
}
