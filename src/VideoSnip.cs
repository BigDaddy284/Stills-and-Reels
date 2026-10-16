using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>
    /// Records part of the screen with FFmpeg (gdigrab), optionally with a microphone (dshow).
    /// Pausing closes the current segment; stopping joins the segments into one MP4 in the cache.
    /// </summary>
    internal sealed class ScreenRecorder : IDisposable
    {
        public Rectangle Region;
        public int Fps = 30;
        public bool ShowCursor = true;
        public string Microphone;     // dshow device name, or null

        readonly List<string> segments = new List<string>();
        readonly StringBuilder log = new StringBuilder();
        Process proc;
        readonly Stopwatch segClock = new Stopwatch();
        double before;
        public event Action<string> Failed; // any thread

        public bool IsRecording { get { return proc != null; } }
        public double Elapsed { get { return before + (proc != null ? segClock.Elapsed.TotalSeconds : 0); } }
        public bool HasFootage { get { return segments.Count > 0; } }

        public static List<string> Microphones()
        {
            var list = new List<string>();
            if (!FFmpeg.IsWindows) return list;
            try
            {
                var psi = new ProcessStartInfo(FFmpeg.Locate(), "-hide_banner -nostdin -list_devices true -f dshow -i dummy")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi))
                {
                    FFmpeg.AddToJob(p);
                    var err = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    foreach (Match m in Regex.Matches(err.Result, "\"([^\"]+)\"\\s*\\(audio\\)")) if (!list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
                }
            }
            catch { }
            return list;
        }

        public void Start()
        {
            if (proc != null) return;
            int w = Math.Max(16, Region.Width) & ~1, h = Math.Max(16, Region.Height) & ~1;
            string seg = Path.Combine(MediaLibrary.Folder, "rec-" + Guid.NewGuid().ToString("N") + ".mkv");
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("-hide_banner -loglevel error ");
            sb.AppendFormat(inv, "-thread_queue_size 1024 -f gdigrab -framerate {0} -draw_mouse {1} -offset_x {2} -offset_y {3} -video_size {4}x{5} -i desktop ",
                Fps, ShowCursor ? 1 : 0, Region.X, Region.Y, w, h);
            if (!string.IsNullOrEmpty(Microphone))
                sb.Append("-thread_queue_size 1024 -f dshow -i audio=\"").Append(Microphone.Replace("\"", "")).Append("\" ");
            sb.AppendFormat(inv, "-map 0:v ");
            if (!string.IsNullOrEmpty(Microphone)) sb.Append("-map 1:a -c:a aac -b:a 160k ");
            sb.AppendFormat(inv, "-c:v libx264 -preset ultrafast -crf 21 -pix_fmt yuv420p -g {0} -y ", Fps * 2);
            sb.Append(FFmpeg.Quote(seg));
            var psi = new ProcessStartInfo(FFmpeg.Locate(), sb.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var p = Process.Start(psi);
            FFmpeg.AddToJob(p);
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) { if (log.Length < 6000) log.AppendLine(e.Data); } };
            p.BeginErrorReadLine();
            p.OutputDataReceived += (s, e) => { };
            p.BeginOutputReadLine();
            p.EnableRaisingEvents = true;
            p.Exited += (s, e) =>
            {
                if (!ReferenceEquals(proc, p)) return; // stopped on purpose
                string msg;
                lock (log) msg = FFmpeg.LastMeaningfulLine(log.ToString());
                var hnd = Failed;
                if (hnd != null) hnd(string.IsNullOrEmpty(msg) ? "The recording stopped unexpectedly." : msg);
            };
            proc = p;
            segments.Add(seg);
            segClock.Restart();
        }

        /// <summary>Ends the current segment gracefully (FFmpeg finishes the file).</summary>
        public void StopSegment()
        {
            var p = proc;
            if (p == null) return;
            proc = null;
            segClock.Stop();
            before += segClock.Elapsed.TotalSeconds;
            try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
            try { if (!p.WaitForExit(8000)) FFmpeg.KillQuietly(p); } catch { }
            try { p.Dispose(); } catch { }
            var last = segments[segments.Count - 1];
            try { if (!File.Exists(last) || new FileInfo(last).Length < 1024) { segments.Remove(last); ImageCache.Delete(last); } } catch { }
        }

        /// <summary>Joins the segments into one MP4 (no re-encoding). Returns its path in the cache.</summary>
        public string Finish()
        {
            StopSegment();
            if (segments.Count == 0) throw new InvalidOperationException("Nothing was recorded.");
            string list = Path.Combine(MediaLibrary.Folder, "rec-" + Guid.NewGuid().ToString("N") + ".txt");
            var sb = new StringBuilder();
            foreach (var s in segments) sb.Append("file '").Append(s.Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
            File.WriteAllText(list, sb.ToString(), new UTF8Encoding(false));
            string outPath = Path.Combine(MediaLibrary.Folder, "Recording " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + ".mp4");
            string args = "-hide_banner -nostdin -loglevel error -f concat -safe 0 -protocol_whitelist file -i " + FFmpeg.Quote(list) +
                          " -c copy -movflags +faststart -y " + FFmpeg.Quote(outPath);
            string err;
            using (var p = FFmpeg.Start(args))
            {
                var e = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(60000);
                err = e.Result;
            }
            ImageCache.Delete(list);
            if (!File.Exists(outPath)) throw new IOException(FFmpeg.LastMeaningfulLine(err) ?? "The recording couldn't be saved.");
            foreach (var s in segments) ImageCache.Delete(s);
            segments.Clear();
            before = 0;
            return outPath;
        }

        public void Discard()
        {
            var p = proc;
            proc = null;
            FFmpeg.KillQuietly(p);
            foreach (var s in segments) ImageCache.Delete(s);
            segments.Clear();
            before = 0;
        }

        public void Dispose() { Discard(); }
    }

    internal static class CaptureExclusion
    {
        [DllImport("user32.dll")]
        static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        /// <summary>Keeps a window out of screen captures and recordings (Windows 10 2004 and later).</summary>
        public static void Apply(Form f)
        {
            if (!FFmpeg.IsWindows || f == null || !f.IsHandleCreated) return;
            try { SetWindowDisplayAffinity(f.Handle, 0x11); } catch { }
        }
    }

    /// <summary>Thin outline around the recorded area (click-through, not recorded).</summary>
    internal sealed class RegionFrame : Form
    {
        public RegionFrame(Rectangle region)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            int t = Theme.S(3);
            Bounds = Rectangle.Inflate(region, t, t);
            BackColor = Color.FromArgb(255, 60, 60);
            using (var path = new GraphicsPath(FillMode.Alternate))
            {
                path.AddRectangle(new Rectangle(0, 0, Width, Height));
                path.AddRectangle(new Rectangle(t, t, Width - 2 * t, Height - 2 * t));
                Region = new Region(path);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80 | 0x08000000; // transparent to clicks, tool window, no activate
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); CaptureExclusion.Apply(this); }
    }

    /// <summary>Small always-on-top bar shown while recording: time, pause/resume, stop. Not recorded.</summary>
    internal sealed class RecordBar : Form
    {
        readonly ThemedButton pause, stop;
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 200 };
        readonly Func<double> elapsed;
        readonly Func<bool> paused;
        Point dragFrom;
        bool dragging;
        public event EventHandler PauseClicked, StopClicked;

        public RecordBar(Func<double> elapsed, Func<bool> paused)
        {
            this.elapsed = elapsed;
            this.paused = paused;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(Theme.S(250), Theme.S(52));
            BackColor = Color.FromArgb(14, 19, 37);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            pause = new ThemedButton { Icon = Ico.Pause, IconOnly = true, Tip = "Pause", Size = new Size(Theme.S(40), Theme.S(36)) };
            pause.Click += (s, e) => { var h = PauseClicked; if (h != null) h(this, EventArgs.Empty); };
            stop = new ThemedButton { Icon = Ico.Stop, Text = "Stop", Primary = true, Size = new Size(Theme.S(84), Theme.S(36)) };
            stop.Click += (s, e) => { var h = StopClicked; if (h != null) h(this, EventArgs.Empty); };
            pause.Location = new Point(Width - Theme.S(8) - stop.Width - Theme.S(6) - pause.Width, Theme.S(8));
            stop.Location = new Point(Width - Theme.S(8) - stop.Width, Theme.S(8));
            Controls.Add(pause);
            Controls.Add(stop);
            using (var path = Theme.Rounded(new RectangleF(0, 0, Width, Height), Theme.F(12))) Region = new Region(path);
            tick.Tick += (s, e) => { pause.Icon = this.paused() ? Ico.Record : Ico.Pause; pause.Tip = this.paused() ? "Resume" : "Pause"; Invalidate(); };
            tick.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); CaptureExclusion.Apply(this); }

        protected override void Dispose(bool disposing) { if (disposing) tick.Dispose(); base.Dispose(disposing); }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); dragging = true; dragFrom = e.Location; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) Location = new Point(Location.X + e.X - dragFrom.X, Location.Y + e.Y - dragFrom.Y); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.F(12)))
            using (var b = new LinearGradientBrush(ClientRectangle, Color.FromArgb(38, 104, 255), Color.FromArgb(176, 72, 255), 0f))
            using (var pen = new Pen(b, Theme.F(1.5f))) g.DrawPath(pen, path);
            bool p = paused();
            bool blink = DateTime.Now.Millisecond < 600;
            var dot = new RectangleF(Theme.F(16), Height / 2f - Theme.F(6), Theme.F(12), Theme.F(12));
            using (var b = new SolidBrush(p ? Color.FromArgb(255, 190, 80) : blink ? Color.FromArgb(255, 70, 70) : Color.FromArgb(120, 255, 70, 70))) g.FillEllipse(b, dot);
            string t = VideoControls.FormatTime(elapsed(), 0) + (p ? "  paused" : "");
            TextRenderer.DrawText(g, t, Theme.UiFont(10.5f, FontStyle.Bold), new Rectangle((int)dot.Right + Theme.S(8), 0, Theme.S(120), Height), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Video snip pop-out: choose an area, record, then preview and keep the recording.</summary>
    internal sealed class VideoSnipForm : Form
    {
        readonly Form host;
        readonly ThemedPanel bar = new ThemedPanel { Surface = Surface.Bar, BorderBottom = true, Dock = DockStyle.Top };
        readonly FlowLayoutPanel flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        readonly ThemedPanel content = new ThemedPanel { Surface = Surface.Canvas, Dock = DockStyle.Fill };
        readonly ThemedPanel actions = new ThemedPanel { Surface = Surface.Bar, BorderTop = true, Dock = DockStyle.Bottom, Height = 1, Visible = false };
        readonly VideoView view = new VideoView { Dock = DockStyle.Fill };
        readonly ThemedButton record, area, screenBtn, cursorBtn, micBtn, fpsBtn;
        readonly ThemedButton addBtn, saveBtn, discardBtn;
        readonly Func<string, bool, bool> addToEditor;  // (path, temporary) -> added
        bool regionMode = true;
        Rectangle? fixedScreen;          // full-screen choice
        bool cursor = true;
        int fps = 30;
        string mic;
        List<string> mics;
        ScreenRecorder rec;
        RecordBar recBar;
        RegionFrame frame;
        bool paused;
        string resultPath;      // the recording (in the cache)
        string savedPath;       // where the user saved it, if they did
        bool added;

        public VideoSnipForm(Form host, Func<string, bool, bool> addToEditor)
        {
            this.host = host;
            this.addToEditor = addToEditor;
            Text = "Video snip";
            ShowInTaskbar = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.UiFont(9.5f);
            KeyPreview = true;
            MinimumSize = new Size(Theme.S(620), Theme.S(420));
            try { Icon = host.Icon; } catch { }

            flow.Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(10), Theme.S(4));
            flow.BackColor = Theme.Current.Bar;
            bar.Controls.Add(flow);
            record = Btn(Ico.Record, "Record", "Start recording (Ctrl+R)", (s, e) => StartRecording(), 112);
            record.Primary = true;
            Sep();
            area = Btn(Ico.RectSelect, "Area", "Record a part of the screen you choose", (s, e) => { regionMode = true; fixedScreen = null; Sync(); }, 84);
            screenBtn = Btn(Ico.Screen, "Full screen", "Record a whole screen", (s, e) => ChooseScreen(), 118);
            Sep();
            cursorBtn = Btn(Ico.Pointer, null, "Show the mouse pointer in the recording", (s, e) => { cursor = !cursor; Sync(); }, 40);
            micBtn = Btn(Ico.Mic, "No sound", "Record a microphone", (s, e) => ChooseMic(), 150);
            fpsBtn = Btn(Ico.Speed, "30 fps", "Frames per second", (s, e) => { fps = fps == 30 ? 60 : fps == 60 ? 15 : 30; Sync(); }, 84);

            addBtn = new ThemedButton { Icon = Ico.Video, Text = "Add to video editor", Primary = true, Tip = "Put the recording in the editor's media and on the timeline" };
            addBtn.Click += (s, e) => AddToEditor();
            saveBtn = new ThemedButton { Icon = Ico.Save, Text = "Save as…", Tip = "Save the recording as an MP4 file" };
            saveBtn.Click += (s, e) => SaveAs();
            discardBtn = new ThemedButton { Icon = Ico.Trash, Text = "Discard", Tip = "Delete this recording" };
            discardBtn.Click += (s, e) => Discard(true);
            actions.Controls.Add(addBtn); actions.Controls.Add(saveBtn); actions.Controls.Add(discardBtn);
            actions.Resize += (s, e) => LayoutActions();

            view.Visible = false;
            content.Controls.Add(view);
            content.Paint += PaintIdle;
            content.Resize += (s, e) => content.Invalidate();
            Controls.Add(content);
            Controls.Add(actions);
            Controls.Add(bar);

            var wa = Screen.FromControl(host).WorkingArea;
            int w = Theme.S(760), h = Theme.S(520);
            int x = host.Right + Theme.S(8);
            if (x + w > wa.Right) x = Math.Max(wa.Left, wa.Right - w - Theme.S(16));
            int y = Math.Max(wa.Top, Math.Min(host.Top + Theme.S(60), wa.Bottom - h));
            Bounds = new Rectangle(x, y, w, h);
            Theme.Changed += OnTheme;
            Sync();
        }

        void OnTheme(object s, EventArgs e) { flow.BackColor = Theme.Current.Bar; Theme.ApplyTo(this); }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.ApplyTo(this); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnTheme;
                CloseRecordingUi();
                if (rec != null) rec.Dispose();
                view.Player.Unload();
                if (resultPath != null && !added) ImageCache.Delete(resultPath);
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
            flow.Controls.Add(new Divider { Width = Theme.S(13), Height = Theme.S(36), Margin = new Padding(0, Theme.S(2), 0, Theme.S(2)) });
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
            if (flow != null && bar != null && flow.Controls.Count > 0)
            {
                int bottom = 0;
                foreach (Control c in flow.Controls) if (c.Visible) bottom = Math.Max(bottom, c.Bottom + c.Margin.Bottom);
                int want = bottom + flow.Padding.Bottom + Theme.S(2);
                if (want > Theme.S(30) && bar.Height != want) bar.Height = want;
            }
            base.OnLayout(e);
        }

        void LayoutActions()
        {
            int pad = Theme.S(12), h = Theme.S(40), y = (actions.Height - h) / 2;
            addBtn.SetBounds(actions.Width - pad - Theme.S(200), y, Theme.S(200), h);
            saveBtn.SetBounds(addBtn.Left - Theme.S(8) - Theme.S(120), y, Theme.S(120), h);
            discardBtn.SetBounds(pad, y, Theme.S(120), h);
        }

        void Sync()
        {
            bool busy = rec != null && (rec.IsRecording || paused);
            area.Checked = regionMode;
            screenBtn.Checked = !regionMode;
            screenBtn.Text = !regionMode && fixedScreen.HasValue ? ScreenName(fixedScreen.Value) : "Full screen";
            cursorBtn.Checked = cursor;
            micBtn.Text = string.IsNullOrEmpty(mic) ? "No sound" : mic;
            micBtn.Checked = !string.IsNullOrEmpty(mic);
            fpsBtn.Text = fps + " fps";
            foreach (Control c in flow.Controls) c.Enabled = !busy;
            bool has = resultPath != null;
            actions.Visible = has;
            actions.Height = Theme.S(60);
            view.Visible = has;
            addBtn.Enabled = has && !added;
            addBtn.Text = added ? "Added to the editor" : "Add to video editor";
            Text = has ? "Video snip – " + Path.GetFileName(savedPath ?? resultPath) : "Video snip";
            content.Invalidate();
        }

        string ScreenName(Rectangle r)
        {
            var all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
                if (all[i].Bounds == r) return "Screen " + (i + 1) + (all[i].Primary ? " (main)" : "");
            return "All screens";
        }

        void ChooseScreen()
        {
            var menu = new ContextMenuStrip();
            var all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i];
                string name = "Screen " + (i + 1) + "  –  " + s.Bounds.Width + " × " + s.Bounds.Height + (s.Primary ? "  (main)" : "");
                var b = s.Bounds;
                menu.Items.Add(new ToolStripMenuItem(name, null, (o, e) => { regionMode = false; fixedScreen = b; Sync(); }) { Checked = !regionMode && fixedScreen == b });
            }
            if (all.Length > 1)
            {
                var vs = SystemInformation.VirtualScreen;
                menu.Items.Add(new ToolStripMenuItem("All screens", null, (o, e) => { regionMode = false; fixedScreen = vs; Sync(); }) { Checked = !regionMode && fixedScreen == vs });
            }
            menu.Closed += (o, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            Ui.ShowMenuBelow(menu, screenBtn);
        }

        void ChooseMic()
        {
            if (mics == null)
            {
                Cursor = Cursors.WaitCursor;
                try { mics = ScreenRecorder.Microphones(); } finally { Cursor = Cursors.Default; }
            }
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("No sound", null, (o, e) => { mic = null; Sync(); }) { Checked = mic == null });
            foreach (var m in mics)
            {
                string mm = m;
                menu.Items.Add(new ToolStripMenuItem(mm, null, (o, e) => { mic = mm; Sync(); }) { Checked = mic == mm });
            }
            if (mics.Count == 0)
                menu.Items.Add(new ToolStripMenuItem("No microphone found") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Computer sound isn't recorded (only a microphone, or \"Stereo Mix\" if Windows offers it)") { Enabled = false });
            menu.Closed += (o, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            Ui.ShowMenuBelow(menu, micBtn);
        }

        void PaintIdle(object sender, PaintEventArgs e)
        {
            if (view.Visible) return;
            string title, detail;
            if (rec != null && (rec.IsRecording || paused)) { title = paused ? "Recording paused" : "Recording…"; detail = "Use the bar on screen to pause or stop."; }
            else
            {
                title = "Record your screen";
                detail = (regionMode ? "Press Record, then drag over the area to record." : "Press Record to capture " + screenBtn.Text.ToLowerInvariant() + ".") +
                         "\nA small bar lets you pause and stop. It isn't recorded.\nRecordings stay in the private cache until you save them or add them to the video editor.";
            }
            Theme.DrawEmptyState(e.Graphics, content.ClientRectangle, Ico.Screen, title, detail, false);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.R)) { StartRecording(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        bool ConfirmDiscard()
        {
            if (resultPath == null || added || savedPath != null) return true;
            var r = MessageBox.Show(this, "Keep the recording?\n\nYes – save it as a file\nNo – delete it", "Video snip", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return SaveAs();
            return true;
        }

        void StartRecording()
        {
            if (rec != null && (rec.IsRecording || paused)) return;
            if (!ConfirmDiscard()) return;
            Discard(false);
            Rectangle region;
            if (regionMode)
            {
                var r = PickRegion();
                if (!r.HasValue) return;
                region = r.Value;
            }
            else region = fixedScreen ?? Screen.FromControl(host).Bounds;
            if (region.Width < 32 || region.Height < 32) { MessageBox.Show(this, "That area is too small to record.", "Video snip"); return; }
            try { FFmpeg.Locate(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Video snip", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            rec = new ScreenRecorder { Region = region, Fps = fps, ShowCursor = cursor, Microphone = mic };
            rec.Failed += msg => BeginInvoke((MethodInvoker)(() => OnRecorderFailed(msg)));
            Countdown(region, () =>
            {
                try { rec.Start(); }
                catch (Exception ex) { CloseRecordingUi(); Show(); MessageBox.Show(this, "Couldn't start recording:\n" + ex.Message, "Video snip", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                paused = false;
                Sync();
            });
        }

        Rectangle? PickRegion()
        {
            var vs = SystemInformation.VirtualScreen;
            double myOpacity = Opacity;
            Bitmap shot = null;
            try
            {
                // hide only the Video snip window; the main window stays visible so it can be recorded
                Opacity = 0;
                Application.DoEvents();
                Thread.Sleep(220);
                shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
                using (var sel = new SnipSelector(shot, SnipShape.Rectangle, true, vs) { Purpose = "Drag over the area to record" })
                {
                    if (sel.ShowDialog() != DialogResult.OK || sel.Result == null) return null;
                    sel.Result.Dispose();
                    var r = sel.ResultRect;
                    return new Rectangle(r.X + vs.X, r.Y + vs.Y, r.Width, r.Height);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't capture the screen: " + ex.Message, "Video snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            finally
            {
                if (shot != null) shot.Dispose();
                Opacity = myOpacity;
            }
        }

        void Countdown(Rectangle region, Action then)
        {
            Hide();
            var cd = new CountdownForm(region);
            cd.FormClosed += (s, e) =>
            {
                if (!cd.Completed) { Show(); return; }
                ShowRecordingUi(region);
                then();
            };
            cd.Show();
        }

        void ShowRecordingUi(Rectangle region)
        {
            var vs = SystemInformation.VirtualScreen;
            bool full = region.Width >= Screen.FromRectangle(region).Bounds.Width - 4;
            if (!full) { frame = new RegionFrame(region); frame.Show(); }
            recBar = new RecordBar(() => rec != null ? rec.Elapsed : 0, () => paused);
            recBar.PauseClicked += (s, e) => TogglePause();
            recBar.StopClicked += (s, e) => StopRecording();
            var scr = Screen.FromRectangle(region).WorkingArea;
            int x = Math.Max(scr.Left + 8, Math.Min(scr.Right - recBar.Width - 8, region.Left + (region.Width - recBar.Width) / 2));
            int y = region.Bottom + Theme.S(10);
            if (y + recBar.Height > scr.Bottom - 4) y = region.Top - recBar.Height - Theme.S(10);
            if (y < scr.Top + 4) y = scr.Top + Theme.S(12);
            recBar.Location = new Point(x, y);
            recBar.Show();
        }

        void CloseRecordingUi()
        {
            if (recBar != null) { recBar.Close(); recBar.Dispose(); recBar = null; }
            if (frame != null) { frame.Close(); frame.Dispose(); frame = null; }
        }

        void TogglePause()
        {
            if (rec == null) return;
            if (!paused) { rec.StopSegment(); paused = true; }
            else
            {
                try { rec.Start(); paused = false; }
                catch (Exception ex) { MessageBox.Show(recBar, "Couldn't resume: " + ex.Message, "Video snip"); }
            }
        }

        void StopRecording()
        {
            if (rec == null) return;
            CloseRecordingUi();
            Show();
            Activate();
            Cursor = Cursors.WaitCursor;
            string path = null;
            Exception error = null;
            try { path = rec.Finish(); }
            catch (Exception ex) { error = ex; }
            finally { Cursor = Cursors.Default; }
            rec.Dispose();
            rec = null;
            paused = false;
            if (error != null)
            {
                Sync();
                MessageBox.Show(this, "The recording couldn't be finished:\n" + error.Message, "Video snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            resultPath = path;
            savedPath = null;
            added = false;
            Sync();
            LoadPreview(path);
        }

        void OnRecorderFailed(string msg)
        {
            if (rec == null) return;
            CloseRecordingUi();
            bool had = rec.HasFootage;
            Show();
            rec.Discard();
            rec = null;
            paused = false;
            Sync();
            MessageBox.Show(this, "Recording stopped: " + msg, "Video snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void LoadPreview(string path)
        {
            view.Surface.SetState(Ico.Video, "Opening the recording…", null, false);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                MediaInfo info = null;
                try { info = FFmpeg.Probe(path); } catch { }
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (info == null) { view.Surface.SetState(Ico.Video, "Couldn't open the recording", null, true); return; }
                        view.Player.Load(info);
                        view.Player.Play();
                    }));
                }
                catch { }
            });
        }

        bool SaveAs()
        {
            if (resultPath == null) return false;
            using (var dlg = new SaveFileDialog { Title = "Save recording", Filter = "MP4 video (*.mp4)|*.mp4", DefaultExt = "mp4", AddExtension = true, OverwritePrompt = true })
            {
                try { dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); } catch { }
                dlg.FileName = Path.GetFileName(resultPath);
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                try
                {
                    File.Copy(resultPath, dlg.FileName, true);
                    savedPath = dlg.FileName;
                    Sync();
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't save the recording:\n" + ex.Message, "Video snip", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
        }

        void AddToEditor()
        {
            if (resultPath == null || added) return;
            view.Player.Pause();
            bool ok = savedPath != null ? addToEditor(savedPath, false) : addToEditor(resultPath, true);
            if (ok) { added = true; Sync(); }
        }

        void Discard(bool ask)
        {
            if (resultPath == null) return;
            if (ask && !added && savedPath == null)
            {
                var r = MessageBox.Show(this, "Delete this recording?", "Video snip", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;
            }
            view.Player.Unload();
            view.Surface.ClearFrame();
            if (!added) ImageCache.Delete(resultPath);
            resultPath = null;
            savedPath = null;
            added = false;
            Sync();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (rec != null && (rec.IsRecording || paused))
            {
                var r = MessageBox.Show(this, "Stop and discard the recording in progress?", "Video snip", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) { e.Cancel = true; return; }
                CloseRecordingUi();
                rec.Discard();
            }
            else if (e.CloseReason == CloseReason.UserClosing && !ConfirmDiscard()) { e.Cancel = true; return; }
            view.Player.Unload();
            base.OnFormClosing(e);
        }

        /// <summary>3-2-1 before recording starts (not recorded).</summary>
        sealed class CountdownForm : Form
        {
            int n = 3;
            readonly System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 800 };
            public bool Completed;

            public CountdownForm(Rectangle region)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                int s = Theme.S(140);
                Bounds = new Rectangle(region.Left + (region.Width - s) / 2, region.Top + (region.Height - s) / 2, s, s);
                BackColor = Color.FromArgb(14, 19, 37);
                using (var path = new GraphicsPath()) { path.AddEllipse(0, 0, s, s); Region = new Region(path); }
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                KeyPreview = true;
                t.Tick += (o, e) => { n--; if (n <= 0) { t.Stop(); Completed = true; Close(); } else Invalidate(); };
                t.Start();
            }

            protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); CaptureExclusion.Apply(this); }
            protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Escape) { t.Stop(); Close(); } }
            protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(BackColor);
                using (var b = new LinearGradientBrush(ClientRectangle, Color.FromArgb(38, 104, 255), Color.FromArgb(176, 72, 255), 30f))
                using (var pen = new Pen(b, Theme.F(5))) g.DrawEllipse(pen, Theme.F(4), Theme.F(4), Width - Theme.F(8), Height - Theme.F(8));
                TextRenderer.DrawText(g, n.ToString(), new Font("Segoe UI", 44f, FontStyle.Bold), ClientRectangle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
