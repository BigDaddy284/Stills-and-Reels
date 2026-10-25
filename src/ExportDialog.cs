using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>Export settings, progress and result. Never writes over a file the project uses.</summary>
    internal sealed class ExportForm : Form
    {
        readonly Project project;
        readonly Dictionary<int, MediaItem> media;
        readonly List<string> sources;
        readonly ThemedPanel body = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Fill };
        readonly TextBox pathBox;
        readonly Segmented format, resolution, fps, quality;
        readonly HintLabel info;
        readonly ThemedButton browse, go, cancel;
        readonly ProgressLine progress = new ProgressLine();
        readonly HintLabel stage = new HintLabel();
        readonly ThemedButton show, play;
        ExportJob job;
        bool finished;

        public ExportForm(Project p, Dictionary<int, MediaItem> media, string projectName, List<string> sources)
        {
            project = p;
            this.media = media;
            this.sources = sources;
            Text = "Export";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.UiFont(9.5f);
            KeyPreview = true;
            Controls.Add(body);
            int pad = Theme.S(24), w = Theme.S(500), y = Theme.S(18);
            ClientSize = new Size(w + pad * 2, Theme.S(600));

            var title = new PanelTitle("Export video", Tc(p.Duration) + " · " + p.Width + " × " + p.Height + " project");
            title.SetBounds(pad, y, w, Theme.S(46));
            body.Controls.Add(title);
            y += Theme.S(56);

            y = Section("Save as", y, pad, w);
            string dir;
            try { dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); } catch { dir = ""; }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string name = projectName == "Untitled project" ? "My video" : projectName;
            pathBox = new TextBox
            {
                Text = Unique(Path.Combine(dir, name + ".mp4")), BorderStyle = BorderStyle.FixedSingle, Font = Theme.UiFont(10f),
                BackColor = Theme.Current.Dark ? Color.FromArgb(24, 30, 56) : Color.White, ForeColor = Theme.Current.Text
            };
            pathBox.SetBounds(pad, y + Theme.S(4), w - Theme.S(112), Theme.S(30));
            body.Controls.Add(pathBox);
            browse = new ThemedButton { Text = "Browse…", Icon = Ico.Folder };
            browse.SetBounds(pad + w - Theme.S(104), y, Theme.S(104), Theme.S(36));
            browse.Click += (s, e) => Browse();
            body.Controls.Add(browse);
            y += Theme.S(48);

            y = Section("Format", y, pad, w);
            format = new Segmented();
            format.Add("mp4", "MP4 video (H.264)").Add("mp3", "MP3 sound only");
            format.Selected = "mp4";
            format.Picked += v => { SwapExtension(v == "mp3" ? ".mp3" : ".mp4"); Sync(); };
            y = Put(format, y, pad, w, Theme.S(34));

            y = Section("Resolution", y, pad, w);
            resolution = new Segmented();
            // "canvas" keeps the project's exact size, so nothing is scaled (e.g. a 1366 × 768 screen recording)
            resolution.Add("canvas", (p.Width & ~1) + " × " + (p.Height & ~1));
            resolution.Add("480", "480p").Add("720", "720p").Add("1080", "1080p").Add("1440", "1440p").Add("2160", "4K");
            resolution.Selected = "canvas";
            resolution.Picked += v => Sync();
            y = Put(resolution, y, pad, w, Theme.S(34));

            y = Section("Frame rate", y, pad, w);
            fps = new Segmented();
            foreach (var f in new[] { "24", "25", "30", "50", "60" }) fps.Add(f, f + " fps");
            // the project's own rate is offered too (29.97, 15, 23.976 …) so frames are not dropped or repeated
            string own = Math.Round(p.Fps, 3).ToString("0.###", CultureInfo.InvariantCulture);
            if (own != "24" && own != "25" && own != "30" && own != "50" && own != "60") fps.Add(own, own + " fps");
            fps.Selected = own;
            fps.Picked += v => Sync();
            y = Put(fps, y, pad, w, Theme.S(34));

            y = Section("Quality", y, pad, w);
            quality = new Segmented();
            quality.Add("28", "Smaller file").Add("23", "Standard").Add("18", "High").Add("14", "Best");
            quality.Selected = "18";
            quality.Picked += v => Sync();
            y = Put(quality, y, pad, w, Theme.S(34));

            info = new HintLabel();
            y = Put(info, y + Theme.S(4), pad, w, Theme.S(40));

            progress.SetBounds(pad, y + Theme.S(4), w, Theme.S(10));
            progress.Visible = false;
            body.Controls.Add(progress);
            stage.SetBounds(pad, y + Theme.S(20), w, Theme.S(40));
            stage.Visible = false;
            body.Controls.Add(stage);
            y += Theme.S(64);

            show = new ThemedButton { Text = "Show in File Explorer", Icon = Ico.Folder, Visible = false };
            show.SetBounds(pad, y, Theme.S(200), Theme.S(40));
            show.Click += (s, e) => { try { Process.Start("explorer.exe", "/select,\"" + pathBox.Text + "\""); } catch { } };
            body.Controls.Add(show);
            play = new ThemedButton { Text = "Play", Icon = Ico.Play, Visible = false };
            play.SetBounds(pad + Theme.S(210), y, Theme.S(100), Theme.S(40));
            play.Click += (s, e) => { try { Process.Start(new ProcessStartInfo(pathBox.Text) { UseShellExecute = true }); } catch { } };
            body.Controls.Add(play);

            cancel = new ThemedButton { Text = "Cancel" };
            cancel.SetBounds(pad + w - Theme.S(250), y, Theme.S(120), Theme.S(40));
            cancel.Click += (s, e) => OnCancel();
            body.Controls.Add(cancel);
            go = new ThemedButton { Text = "Export", Icon = Ico.Export, Primary = true };
            go.SetBounds(pad + w - Theme.S(120), y, Theme.S(120), Theme.S(40));
            go.Click += (s, e) => Start();
            body.Controls.Add(go);
            ClientSize = new Size(w + pad * 2, y + Theme.S(40) + pad);
            Sync();
        }

        static string Tc(double t) { return VideoControls.FormatTime(t, t); }

        int Section(string text, int y, int x, int w)
        {
            var l = new SectionLabel(text);
            l.SetBounds(x, y, w, Theme.S(20));
            body.Controls.Add(l);
            return y + Theme.S(24);
        }

        int Put(Control c, int y, int x, int w, int h)
        {
            c.SetBounds(x, y, w, h);
            body.Controls.Add(c);
            return y + h + Theme.S(12);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTo(this);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) OnCancel();
        }

        static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
            return path;
        }

        void SwapExtension(string ext)
        {
            try { pathBox.Text = Unique(Path.ChangeExtension(pathBox.Text, ext)); } catch { }
        }

        void Browse()
        {
            bool mp3 = format.Selected == "mp3";
            using (var dlg = new SaveFileDialog { Title = "Export to", Filter = mp3 ? "MP3 sound (*.mp3)|*.mp3" : "MP4 video (*.mp4)|*.mp4", AddExtension = true, DefaultExt = mp3 ? "mp3" : "mp4", OverwritePrompt = true })
            {
                try { dlg.InitialDirectory = Path.GetDirectoryName(pathBox.Text); dlg.FileName = Path.GetFileName(pathBox.Text); } catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK) pathBox.Text = dlg.FileName;
            }
        }

        Size OutSize()
        {
            if (resolution.Selected == "canvas") return new Size(project.Width & ~1, project.Height & ~1);
            int shortSide = int.Parse(resolution.Selected, CultureInfo.InvariantCulture);
            double r = (double)project.Width / project.Height;
            int w, h;
            if (r >= 1) { h = shortSide; w = (int)Math.Round(h * r); }
            else { w = shortSide; h = (int)Math.Round(w / r); }
            return new Size(w & ~1, h & ~1);
        }

        void Sync()
        {
            bool mp3 = format.Selected == "mp3";
            resolution.Enabled = fps.Enabled = quality.Enabled = !mp3;
            resolution.Invalidate(); fps.Invalidate(); quality.Invalidate();
            if (mp3) { info.Text = "Saves only the mixed sound of the timeline as an MP3 file (192 kbps)."; return; }
            var s = OutSize();
            int crf = int.Parse(quality.Selected, CultureInfo.InvariantCulture);
            // rough size estimate: bits per pixel per frame by quality
            double bpp = crf <= 14 ? 0.22 : crf <= 18 ? 0.13 : crf <= 23 ? 0.065 : 0.035;
            double fr = double.Parse(fps.Selected, CultureInfo.InvariantCulture);
            double mb = (s.Width * (double)s.Height * fr * bpp + 192000) * project.Duration / 8 / 1048576;
            info.Text = s.Width + " × " + s.Height + ", " + fps.Selected + " fps, H.264 with AAC sound. Estimated size about " +
                (mb < 1 ? "1 MB" : mb < 1024 ? Math.Round(mb) + " MB" : (mb / 1024).ToString("0.0") + " GB") + " (it varies with the content).";
            if (Math.Min(s.Width, s.Height) > Math.Min(project.Width, project.Height))
                info.Text += " This is bigger than the project canvas, so pictures are scaled up.";
        }

        void Start()
        {
            if (job != null) return;
            if (finished) { Close(); return; }
            string path = pathBox.Text.Trim();
            bool mp3 = format.Selected == "mp3";
            try
            {
                if (string.IsNullOrEmpty(path)) throw new IOException("Choose where to save the file.");
                path = Path.GetFullPath(path);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (mp3 && ext != ".mp3") path += ".mp3";
                if (!mp3 && ext != ".mp4") path += ".mp4";
                foreach (var src in sources)
                    if (MediaLibrary.SamePath(src, path)) throw new IOException("That file is used by the project. Stills & Reels never overwrites your originals – choose another name.");
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) throw new IOException("The folder doesn't exist: " + dir);
                if (File.Exists(path))
                {
                    var r = MessageBox.Show(this, Path.GetFileName(path) + " already exists. Replace it?", "Export", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes) return;
                }
                pathBox.Text = path;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var size = OutSize();
            var settings = new ExportSettings
            {
                OutPath = path, Width = size.Width, Height = size.Height,
                Fps = double.Parse(fps.Selected, CultureInfo.InvariantCulture),
                Crf = int.Parse(quality.Selected, CultureInfo.InvariantCulture),
                Preset = "medium", AudioOnly = mp3,
            };
            foreach (Control c in new Control[] { pathBox, browse, format, resolution, fps, quality }) c.Enabled = false;
            go.Enabled = false;
            progress.Visible = stage.Visible = true;
            progress.Value = 0;
            stage.Text = "Starting…";
            job = new ExportJob(project, media, settings);
            job.Progress += (p, s) => Post(() => { progress.Value = p; stage.Text = s; });
            job.Completed += err => Post(() => Done(err));
            job.Start();
        }

        void Post(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        void Done(Exception err)
        {
            job = null;
            if (err == null)
            {
                finished = true;
                progress.Value = 1;
                stage.Text = "Saved " + Path.GetFileName(pathBox.Text);
                show.Visible = play.Visible = true;
                go.Text = "Close";
                go.Icon = Ico.Check;
                go.Enabled = true;
                cancel.Visible = false;
                return;
            }
            foreach (Control c in new Control[] { pathBox, browse, format, resolution, fps, quality }) c.Enabled = true;
            go.Enabled = true;
            progress.Visible = false;
            if (err is OperationCanceledException) { stage.Text = "Export cancelled."; return; }
            stage.Text = "The export failed: " + err.Message;
            MessageBox.Show(this, "The export failed:\n" + err.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        void OnCancel()
        {
            if (job != null)
            {
                var r = MessageBox.Show(this, "Stop the export?", "Export", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes) job.Cancel();
                return;
            }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (job != null)
            {
                e.Cancel = true;
                OnCancel();
                return;
            }
            base.OnFormClosing(e);
        }

        sealed class ProgressLine : Control
        {
            double v;
            public ProgressLine() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
            public double Value { get { return v; } set { v = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var p = Theme.Current;
                g.Clear(Parent != null ? Parent.BackColor : p.Panel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(0, 0, Width - 1, Height - 1);
                using (var path = Theme.Rounded(r, r.Height / 2)) using (var b = new SolidBrush(p.Control)) g.FillPath(b, path);
                if (v > 0.001)
                    using (var path = Theme.Rounded(new RectangleF(0, 0, Math.Max(r.Height, (float)(r.Width * v)), r.Height), r.Height / 2))
                    using (var b = Theme.AccentGradient(r)) g.FillPath(b, path);
            }
        }
    }
}
