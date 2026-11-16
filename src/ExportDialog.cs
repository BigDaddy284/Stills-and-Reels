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
        readonly Segmented kind, videoFormat, soundFormat, resolution, gifSize, fps, gifFps, quality, part;
        readonly ThemedToggle card;
        readonly HintLabel info, gifNote;
        readonly ThemedButton browse, go, cancel, presets;
        readonly ProgressLine progress = new ProgressLine();
        readonly HintLabel stage = new HintLabel();
        readonly ThemedButton show, play;
        ExportJob job;
        bool finished;

        // the choices of the last export are offered again, in this session and the next
        static string lastKind = "video", lastVideo = "mp4", lastSound = "mp3", lastQuality = "18";
        static bool lastCard, choicesRead;

        static string ChoicesFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StillsAndReels", "export-choices.txt"); }
        }

        static void ReadChoices()
        {
            if (choicesRead) return;
            choicesRead = true;
            try
            {
                if (!File.Exists(ChoicesFile)) return;
                foreach (var line in File.ReadAllLines(ChoicesFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    // only values the window offers: anything else keeps the default
                    if (key == "kind" && (v == "video" || v == "gif" || v == "sound")) lastKind = v;
                    else if (key == "video" && Encoders.IsVideo(v)) lastVideo = v;
                    else if (key == "sound" && Encoders.IsAudio(v)) lastSound = v;
                    else if (key == "quality" && (v == "14" || v == "18" || v == "23" || v == "28")) lastQuality = v;
                    else if (key == "card") lastCard = v == "1";
                }
            }
            catch { }
        }

        static void WriteChoices()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ChoicesFile));
                File.WriteAllLines(ChoicesFile, new[] { "kind=" + lastKind, "video=" + lastVideo, "sound=" + lastSound, "quality=" + lastQuality, "card=" + (lastCard ? "1" : "0") });
            }
            catch { }
        }

        public ExportForm(Project p, Dictionary<int, MediaItem> media, string projectName, List<string> sources)
        {
            ReadChoices();
            project = p;
            this.media = media;
            this.sources = sources;
            Text = "Export";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            try { if (Theme.AppIcon != null) Icon = Theme.AppIcon; } catch { }
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.UiFont(9.5f);
            KeyPreview = true;
            Controls.Add(body);
            int pad = Theme.S(24), w = Theme.S(600), y = Theme.S(18);
            ClientSize = new Size(w + pad * 2, Theme.S(600));

            var title = new PanelTitle("Export", Tc(p.Duration) + " · " + p.Width + " × " + p.Height + " project");
            title.SetBounds(pad, y, w - Theme.S(130), Theme.S(46));
            body.Controls.Add(title);
            presets = new ThemedButton { Text = "Presets", Icon = Ico.More, Tip = "Ready-made settings for common uses" };
            presets.SetBounds(pad + w - Theme.S(120), y + Theme.S(4), Theme.S(120), Theme.S(36));
            presets.Click += (s, e) => ShowPresets();
            body.Controls.Add(presets);
            y += Theme.S(58);

            string dir;
            try { dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); } catch { dir = ""; }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string name = projectName == "Untitled project" ? "My video" : projectName;
            pathBox = new TextBox
            {
                Text = Path.Combine(dir, name + ".mp4"), BorderStyle = BorderStyle.FixedSingle, Font = Theme.UiFont(10f),
                BackColor = Theme.Current.InputBg, ForeColor = Theme.Current.Text
            };
            int lw = Theme.S(108);
            Label("Save as", y, pad, lw);
            pathBox.SetBounds(pad + lw, y + Theme.S(4), w - lw - Theme.S(112), Theme.S(30));
            body.Controls.Add(pathBox);
            browse = new ThemedButton { Text = "Browse…", Icon = Ico.Folder };
            browse.SetBounds(pad + w - Theme.S(104), y, Theme.S(104), Theme.S(36));
            browse.Click += (s, e) => Browse();
            body.Controls.Add(browse);
            y += Theme.S(46);

            kind = new Segmented();
            kind.Add("video", "Video").Add("gif", "Animated GIF").Add("sound", "Sound only");
            kind.Selected = lastKind;
            kind.Picked += v => { FixExtension(); Sync(); };
            y = Row("Export", kind, y, pad, lw, w);

            videoFormat = new Segmented();
            videoFormat.Add("mp4", "MP4 · H.264").Add("hevc", "MP4 · HEVC (smaller)").Add("webm", "WebM · VP9");
            videoFormat.Selected = lastVideo;
            videoFormat.Picked += v => { FixExtension(); Sync(); };
            soundFormat = new Segmented();
            soundFormat.Add("mp3", "MP3").Add("m4a", "M4A (AAC)").Add("wav", "WAV (uncompressed)");
            soundFormat.Selected = lastSound;
            soundFormat.Picked += v => { FixExtension(); Sync(); };
            gifNote = new HintLabel { Text = "256 colours, plays in a loop, no sound. Best kept short and small." };
            Overlay(soundFormat, y, pad, lw, w);
            Overlay(gifNote, y + Theme.S(7), pad, lw, w);
            y = Row("Format", videoFormat, y, pad, lw, w);

            resolution = new Segmented();
            // "canvas" keeps the project's exact size, so nothing is scaled (e.g. a 1366 × 768 screen recording)
            resolution.Add("canvas", (p.Width & ~1) + " × " + (p.Height & ~1));
            resolution.Add("480", "480p").Add("720", "720p").Add("1080", "1080p").Add("1440", "1440p").Add("2160", "4K");
            resolution.Selected = "canvas";
            resolution.Picked += v => Sync();
            gifSize = new Segmented();
            gifSize.Add("240", "Small").Add("360", "Medium").Add("480", "Large").Add("720", "Very large");
            gifSize.Selected = "360";
            gifSize.Picked += v => Sync();
            Overlay(gifSize, y, pad, lw, w);
            y = Row("Size", resolution, y, pad, lw, w);

            fps = new Segmented();
            foreach (var f in new[] { "24", "25", "30", "50", "60" }) fps.Add(f, f + " fps");
            // the project's own rate is offered too (29.97, 15, 23.976 …) so frames are not dropped or repeated
            string own = Math.Round(p.Fps, 3).ToString("0.###", CultureInfo.InvariantCulture);
            if (own != "24" && own != "25" && own != "30" && own != "50" && own != "60") fps.Add(own, own + " fps");
            fps.Selected = own;
            fps.Picked += v => Sync();
            gifFps = new Segmented();
            foreach (var f in new[] { "10", "15", "20", "25" }) gifFps.Add(f, f + " fps");
            gifFps.Selected = "15";
            gifFps.Picked += v => Sync();
            Overlay(gifFps, y, pad, lw, w);
            y = Row("Frame rate", fps, y, pad, lw, w);

            quality = new Segmented();
            quality.Add("28", "Smaller file").Add("23", "Standard").Add("18", "High").Add("14", "Best");
            quality.Selected = lastQuality;
            quality.Picked += v => Sync();
            y = Row("Quality", quality, y, pad, lw, w);

            part = new Segmented();
            part.Add("all", "Whole video · " + Tc(p.Duration));
            if (p.HasRange)
            {
                part.Add("range", "Marked range · " + Tc(p.MarkIn) + " to " + Tc(Math.Min(p.Duration, p.MarkOut)));
                part.Selected = "range";
                part.Picked += v => Sync();
                y = Row("Part", part, y, pad, lw, w);
            }
            else part.Selected = "all";

            card = new ThemedToggle { Text = "Checking the graphics card…", Checked = false, Enabled = false };
            card.CheckedChanged += (s, e) => Sync();
            y = Row("Speed", card, y, pad, lw, w);

            info = new HintLabel();
            info.SetBounds(pad, y + Theme.S(2), w, Theme.S(42));
            body.Controls.Add(info);
            y += Theme.S(50);

            progress.SetBounds(pad, y, w, Theme.S(10));
            progress.Visible = false;
            body.Controls.Add(progress);
            stage.SetBounds(pad, y + Theme.S(16), w, Theme.S(40));
            stage.Visible = false;
            body.Controls.Add(stage);
            y += Theme.S(60);

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
            FixExtension();
            Sync();
        }

        static string Tc(double t) { return VideoControls.FormatTime(t, t); }

        void Label(string text, int y, int x, int lw)
        {
            var l = new HintLabel { Text = text };
            l.SetBounds(x, y + Theme.S(7), lw - Theme.S(8), Theme.S(24));
            body.Controls.Add(l);
        }

        /// <summary>A labelled row: the name on the left, the control filling the rest. Returns the y of the next row.</summary>
        int Row(string label, Control c, int y, int x, int lw, int w)
        {
            Label(label, y, x, lw);
            c.SetBounds(x + lw, y, w - lw, Theme.S(34));
            body.Controls.Add(c);
            return y + Theme.S(42);
        }

        /// <summary>A control that takes the place of a row's own control for another kind of export.</summary>
        void Overlay(Control c, int y, int x, int lw, int w)
        {
            c.SetBounds(x + lw, y, w - lw, c is HintLabel ? Theme.S(24) : Theme.S(34));
            c.Visible = false;
            body.Controls.Add(c);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTo(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // which graphics card encoders work is found by trying them, off the UI thread (a few seconds the first time)
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Encoders.Detect("mp4"); Encoders.Detect("hevc"); } catch { }
                Post(() => { if (lastCard) card.Checked = true; Sync(); });
            });
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

        /// <summary>The chosen format: mp4, hevc, webm, gif, mp3, m4a or wav.</summary>
        string Format { get { return kind.Selected == "gif" ? "gif" : kind.Selected == "sound" ? soundFormat.Selected : videoFormat.Selected; } }

        void FixExtension()
        {
            string ext = Encoders.Extension(Format);
            try
            {
                string t = pathBox.Text.Trim();
                if (t.Length == 0 || string.Equals(Path.GetExtension(t), ext, StringComparison.OrdinalIgnoreCase)) { if (t.Length > 0) pathBox.Text = Unique(t); return; }
                pathBox.Text = Unique(Path.ChangeExtension(t, ext));
            }
            catch { }
        }

        void Browse()
        {
            string ext = Encoders.Extension(Format).TrimStart('.');
            using (var dlg = new SaveFileDialog { Title = "Export to", Filter = ext.ToUpperInvariant() + " file (*." + ext + ")|*." + ext, AddExtension = true, DefaultExt = ext, OverwritePrompt = true })
            {
                try { dlg.InitialDirectory = Path.GetDirectoryName(pathBox.Text); dlg.FileName = Path.GetFileName(pathBox.Text); } catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK) pathBox.Text = dlg.FileName;
            }
        }

        void ShowPresets()
        {
            var menu = new ContextMenuStrip();
            Action<string, string, string, string, string, string> add = (text, k, f, res, rate, q) =>
                menu.Items.Add(new ToolStripMenuItem(text, null, (s, e) =>
                {
                    kind.Selected = k;
                    if (k == "video") { videoFormat.Selected = f; resolution.Selected = res; if (rate != null) fps.Selected = rate; quality.Selected = q; }
                    else if (k == "gif") { gifSize.Selected = res; gifFps.Selected = rate; }
                    else { soundFormat.Selected = f; quality.Selected = q; }
                    FixExtension();
                    Sync();
                }));
            add("Best for this project (its own size and frame rate)", "video", "mp4", "canvas", Math.Round(project.Fps, 3).ToString("0.###", CultureInfo.InvariantCulture), "18");
            add("YouTube, TV and computers · 1080p", "video", "mp4", "1080", null, "18");
            add("4K", "video", "mp4", "2160", null, "18");
            add("Phones and social apps · 1080p, 30 fps", "video", "mp4", "1080", "30", "23");
            add("Small, for email or chat · 720p", "video", "mp4", "720", "30", "28");
            add("Smallest file that still looks good · HEVC", "video", "hevc", "1080", null, "23");
            add("For websites · WebM", "video", "webm", "1080", null, "23");
            menu.Items.Add(new ToolStripSeparator());
            add("Animated GIF · medium", "gif", null, "360", "15", null);
            add("Animated GIF · small and light", "gif", null, "240", "10", null);
            menu.Items.Add(new ToolStripSeparator());
            add("Sound only · MP3", "sound", "mp3", null, null, "23");
            add("Sound only · WAV, for more editing", "sound", "wav", null, null, "18");
            Theme.StyleMenu(menu);
            menu.Closed += (s, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            Ui.ShowMenuBelow(menu, presets);
        }

        Size OutSize()
        {
            bool gif = kind.Selected == "gif";
            if (!gif && resolution.Selected == "canvas") return new Size(project.Width & ~1, project.Height & ~1);
            int shortSide = int.Parse(gif ? gifSize.Selected : resolution.Selected, CultureInfo.InvariantCulture);
            double r = (double)project.Width / project.Height;
            int w, h;
            if (r >= 1) { h = shortSide; w = (int)Math.Round(h * r); }
            else { w = shortSide; h = (int)Math.Round(w / r); }
            return new Size(w & ~1, h & ~1);
        }

        double OutFps { get { return double.Parse(kind.Selected == "gif" ? gifFps.Selected : fps.Selected, CultureInfo.InvariantCulture); } }

        /// <summary>Sound bit rate from the quality choice.</summary>
        int AudioKbps { get { int q = int.Parse(quality.Selected, CultureInfo.InvariantCulture); return q <= 14 ? 320 : q <= 18 ? 256 : q <= 23 ? 192 : 128; } }

        bool Ranged { get { return project.HasRange && part.Selected == "range"; } }
        double OutSeconds { get { return Ranged ? Math.Min(project.Duration, project.MarkOut) - project.MarkIn : project.Duration; } }

        void Sync()
        {
            string k = kind.Selected, f = Format;
            bool video = k == "video", gif = k == "gif", sound = k == "sound";
            videoFormat.Visible = video; soundFormat.Visible = sound; gifNote.Visible = gif;
            resolution.Visible = !gif; gifSize.Visible = gif;
            fps.Visible = !gif; gifFps.Visible = gif;
            resolution.Enabled = fps.Enabled = video;
            quality.Enabled = video || (sound && f != "wav");
            resolution.Invalidate(); fps.Invalidate(); quality.Invalidate();

            bool tested;
            string hw = Encoders.Known(f, out tested);
            bool canCard = video && f != "webm";
            if (!canCard) { card.Enabled = false; card.Text = video ? "WebM is always encoded on the processor" : "Not used for this kind of export"; }
            else if (!tested) { card.Enabled = false; card.Text = "Checking the graphics card…"; }
            else if (hw == null) { card.Enabled = false; card.Text = "No graphics card encoder found – the processor does the work"; }
            else { card.Enabled = true; card.Text = "Use the " + Encoders.HardwareName(hw) + " graphics card: much faster, a slightly bigger file"; }
            bool useCard = canCard && tested && hw != null && card.Checked;
            card.Invalidate();

            var s = OutSize();
            double seconds = OutSeconds;
            int crf = int.Parse(quality.Selected, CultureInfo.InvariantCulture);
            double mb = Encoders.EstimateMb(f, useCard, s.Width, s.Height, OutFps, crf, AudioKbps, seconds);
            string size = "Estimated size about " + (mb < 1 ? "1 MB" : mb < 1024 ? Math.Round(mb) + " MB" : (mb / 1024).ToString("0.0") + " GB");
            if (sound)
            {
                info.Text = "Saves only the mixed sound of the timeline: " + Encoders.Describe(f) + (f == "wav" ? "" : ", " + AudioKbps + " kbps") + ". " + size + ".";
                return;
            }
            info.Text = s.Width + " × " + s.Height + ", " + OutFps.ToString("0.###", CultureInfo.InvariantCulture) + " fps, " + Encoders.Describe(f) + ". " + size + " (it varies with the content).";
            if (gif && seconds > 30) info.Text += " A GIF this long will be very large – a video is usually the better choice.";
            else if (f == "hevc") info.Text += " HEVC needs a recent phone, TV or PC to play.";
            else if (!gif && Math.Min(s.Width, s.Height) > Math.Min(project.Width, project.Height)) info.Text += " This is bigger than the project canvas, so pictures are scaled up.";
        }

        void Start()
        {
            if (job != null) return;
            if (finished) { Close(); return; }
            string path = pathBox.Text.Trim();
            string f = Format;
            try
            {
                if (string.IsNullOrEmpty(path)) throw new IOException("Choose where to save the file.");
                path = Path.GetFullPath(path);
                string want = Encoders.Extension(f);
                if (!string.Equals(Path.GetExtension(path), want, StringComparison.OrdinalIgnoreCase)) path += want;
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
            bool tested;
            var settings = new ExportSettings
            {
                OutPath = path, Width = size.Width, Height = size.Height,
                Fps = OutFps,
                Crf = int.Parse(quality.Selected, CultureInfo.InvariantCulture),
                Preset = "medium", Format = f, AudioOnly = Encoders.IsAudio(f), AudioKbps = AudioKbps,
                Hardware = card.Enabled && card.Checked && Encoders.Known(f, out tested) != null,
                From = Ranged ? project.MarkIn : 0, To = Ranged ? Math.Min(project.Duration, project.MarkOut) : 0,
            };
            lastKind = kind.Selected; lastVideo = videoFormat.Selected; lastSound = soundFormat.Selected; lastQuality = quality.Selected;
            if (card.Enabled) lastCard = card.Checked;
            WriteChoices();
            foreach (Control c in Inputs()) c.Enabled = false;
            go.Enabled = false;
            progress.Visible = stage.Visible = true;
            progress.Value = 0;
            stage.Text = "Starting…";
            var j = new ExportJob(project, media, settings);
            job = j;
            j.Progress += (p, s) => Post(() => { progress.Value = p; stage.Text = s; });
            j.Completed += err => Post(() => Done(err, j));
            j.Start();
        }

        Control[] Inputs() { return new Control[] { pathBox, browse, presets, kind, videoFormat, soundFormat, resolution, gifSize, fps, gifFps, quality, part, card }; }

        void Post(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        void Done(Exception err, ExportJob j)
        {
            job = null;
            if (err == null)
            {
                finished = true;
                progress.Value = 1;
                string size = "";
                try { double mb = new FileInfo(pathBox.Text).Length / 1048576.0; size = " · " + (mb < 10 ? mb.ToString("0.0") : Math.Round(mb).ToString()) + " MB"; } catch { }
                stage.Text = "Saved " + Path.GetFileName(pathBox.Text) + size +
                    (j.UsedFallback ? "\nThe graphics card couldn't encode this, so the processor did." : j.UsedHardware != null ? " · encoded on the " + Encoders.HardwareName(j.UsedHardware) + " graphics card" : "");
                show.Visible = play.Visible = true;
                go.Text = "Close";
                go.Icon = Ico.Check;
                go.Enabled = true;
                cancel.Visible = false;
                return;
            }
            foreach (Control c in Inputs()) c.Enabled = true;
            go.Enabled = true;
            Sync();
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
