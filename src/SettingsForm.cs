using System;
using System.Drawing;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>Settings: appearance and behaviour. Changes apply immediately.</summary>
    internal sealed class SettingsForm : Form
    {
        public sealed class Options
        {
            public bool Dark, Follow, TopMost, StatusBar, PixelGrid, Autoplay, KeepViewed;
        }

        readonly Options o;
        readonly Action<Options> apply;
        readonly ThemedButton dark, light;
        readonly ThemedPanel body = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Fill };

        public SettingsForm(Options options, Action<Options> apply)
        {
            o = options;
            this.apply = apply;
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.S(480), Theme.S(600));
            KeyPreview = true;
            Controls.Add(body);

            int pad = Theme.S(24), w = ClientSize.Width - pad * 2, y = Theme.S(20);
            y = AddTitle("Settings", y, pad, w);

            y = AddSection("Appearance", y, pad, w);
            int half = (w - Theme.S(10)) / 2;
            dark = new ThemedButton { Icon = Ico.Moon, Text = "Dark mode", Tip = "Dark mode (default)" };
            light = new ThemedButton { Icon = Ico.Sun, Text = "Light mode", Tip = "Light mode" };
            dark.SetBounds(pad, y, half, Theme.S(44));
            light.SetBounds(pad + half + Theme.S(10), y, half, Theme.S(44));
            dark.Click += (s, e) => { o.Dark = true; Sync(); apply(o); };
            light.Click += (s, e) => { o.Dark = false; Sync(); apply(o); };
            body.Controls.Add(dark);
            body.Controls.Add(light);
            y += Theme.S(56);

            y = AddSection("File Explorer", y, pad, w);
            y = AddToggle("Follow the selection in File Explorer", o.Follow, v => o.Follow = v, y, pad, w);

            y = AddSection("Pictures", y, pad, w);
            y = AddToggle("Show pixel grid when zoomed in (600%+)", o.PixelGrid, v => o.PixelGrid = v, y, pad, w);

            y = AddSection("Videos", y, pad, w);
            y = AddToggle("Start playing when a video is selected", o.Autoplay, v => o.Autoplay = v, y, pad, w);
            y = AddToggle("Keep videos I view in the video editor's Media", o.KeepViewed, v => o.KeepViewed = v, y, pad, w);

            y = AddSection("Window", y, pad, w);
            y = AddToggle("Always on top", o.TopMost, v => o.TopMost = v, y, pad, w);
            y = AddToggle("Show status bar", o.StatusBar, v => o.StatusBar = v, y, pad, w);

            var close = new ThemedButton { Text = "Done", Primary = true };
            close.SetBounds(ClientSize.Width - pad - Theme.S(120), y + Theme.S(16), Theme.S(120), Theme.S(40));
            close.Click += (s, e) => Close();
            body.Controls.Add(close);
            ClientSize = new Size(ClientSize.Width, close.Bottom + pad);
            AcceptButtonClose = close;
            Sync();
            Theme.Changed += OnTheme;
        }

        ThemedButton AcceptButtonClose;

        void OnTheme(object s, EventArgs e) { Theme.ApplyTo(this); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Theme.Changed -= OnTheme;
            base.Dispose(disposing);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTo(this);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter) Close();
        }

        void Sync()
        {
            dark.Checked = o.Dark;
            light.Checked = !o.Dark;
        }

        int AddTitle(string text, int y, int x, int w)
        {
            var t = new Heading(text, 15f) { };
            t.SetBounds(x, y, w, Theme.S(36));
            body.Controls.Add(t);
            return y + Theme.S(44);
        }

        int AddSection(string text, int y, int x, int w)
        {
            var l = new SectionLabel(text);
            l.SetBounds(x, y + Theme.S(6), w, Theme.S(22));
            body.Controls.Add(l);
            return y + Theme.S(34);
        }

        int AddToggle(string text, bool value, Action<bool> set, int y, int x, int w)
        {
            var t = new ThemedToggle { Text = text };
            t.Checked = value;
            t.CheckedChanged += (s, e) => { set(t.Checked); apply(o); };
            t.SetBounds(x, y, w, Theme.S(36));
            body.Controls.Add(t);
            return y + Theme.S(40);
        }

        sealed class Heading : Control
        {
            readonly float size;
            public Heading(string text, float size)
            {
                this.size = size;
                Text = text;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = Theme.Current;
                e.Graphics.Clear(Parent != null ? Parent.BackColor : p.Panel);
                TextRenderer.DrawText(e.Graphics, Text, Theme.DisplayFont(size), ClientRectangle, p.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
    }
}
