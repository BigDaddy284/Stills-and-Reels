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
    /// Segments are captured losslessly (exact screen pixels); pausing closes the current segment, and stopping
    /// encodes all segments once into a high-quality MP4 in the cache.
    /// </summary>
    internal sealed class ScreenRecorder : IDisposable
    {
        public Rectangle Region;
        public int Fps = 30;
        public bool ShowCursor = true;
        public string Microphone;     // dshow device name, or null
        public bool PcSound;          // also record what the PC plays (WASAPI loopback, see PcSoundCapture)
        PcSoundCapture pc;

        readonly List<string> segments = new List<string>();
        readonly StringBuilder log = new StringBuilder();
        Process proc;
        Process finishProc;           // the final encode, so Discard can stop it
        volatile bool discarded;
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
            string video = DesktopDuplicationInput(Region, w, h, Fps, ShowCursor) ?? string.Format(inv,
                "-f gdigrab -framerate {0} -draw_mouse {1} -offset_x {2} -offset_y {3} -video_size {4}x{5} -i desktop ",
                Fps, ShowCursor ? 1 : 0, Region.X, Region.Y, w, h);
            string micInput = string.IsNullOrEmpty(Microphone) ? null : "-f dshow -i audio=\"" + Microphone.Replace("\"", "") + "\" ";
            string pcInput = null;
            if (PcSound)
            {
                pc = new PcSoundCapture();
                pc.Open();
                pcInput = pc.InputFormatArgs + "-i " + FFmpeg.Quote(pc.PipePath) + " ";
            }
            var sb = new StringBuilder(BuildArgs(video, micInput, pcInput, Fps, seg));
            var psi = new ProcessStartInfo(FFmpeg.Locate(), sb.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            Process p;
            try { p = Process.Start(psi); }
            catch { StopPc(); throw; }
            FFmpeg.AddToJob(p);
            var pcNow = pc;
            var diag = new StringBuilder();
            var wall = Stopwatch.StartNew();
            diag.AppendFormat(inv, "args: {0}\n", sb);
            p.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                if (pcNow != null) pcNow.OnFfmpegLine(e.Data);
                lock (diag)
                {
                    if (diag.Length < 200000) diag.AppendFormat(inv, "{0,8:0.000} {1}\n", wall.Elapsed.TotalSeconds, e.Data);
                    // progress lines aren't errors; keep them out of the message shown when a recording fails
                    if (!e.Data.StartsWith("frame=", StringComparison.Ordinal) && !e.Data.StartsWith("size=", StringComparison.Ordinal))
                        lock (log) { if (log.Length < 6000) log.AppendLine(e.Data); }
                }
            };
            p.BeginErrorReadLine();
            p.OutputDataReceived += (s, e) => { };
            diagLogs[seg] = diag;
            diagPc[seg] = pcNow;
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

        static readonly Dictionary<int, bool> ddaWorks = new Dictionary<int, bool>();

        /// <summary>
        /// Screen input through Desktop Duplication (FFmpeg's ddagrab): whole frames straight from the GPU, so
        /// no tearing and far less CPU than gdigrab, which copies the screen line by line while it is being drawn.
        /// Returns null (use gdigrab) when the area spans two screens or Desktop Duplication isn't available
        /// (remote desktop, some older drivers); that is checked once per screen with a short trial run.
        /// </summary>
        static string DesktopDuplicationInput(Rectangle region, int w, int h, int fps, bool cursor)
        {
            if (!FFmpeg.IsWindows) return null;
            var all = Screen.AllScreens;
            int idx = -1;
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i].Bounds;
                if (region.X >= b.Left && region.Y >= b.Top && region.X + w <= b.Right && region.Y + h <= b.Bottom) { idx = i; break; }
            }
            if (idx < 0) return null;
            var inv = CultureInfo.InvariantCulture;
            bool ok;
            lock (ddaWorks)
            {
                if (!ddaWorks.TryGetValue(idx, out ok))
                {
                    ok = false;
                    try
                    {
                        string trial = string.Format(inv, "-hide_banner -loglevel error -nostdin -f lavfi -i \"ddagrab=output_idx={0}:video_size=64x64:framerate=10,hwdownload,format=bgra\" -frames:v 2 -f null -", idx);
                        using (var t = FFmpeg.Start(trial))
                        {
                            t.BeginErrorReadLine();
                            t.StandardOutput.ReadToEnd();
                            if (t.WaitForExit(5000)) ok = t.ExitCode == 0; else FFmpeg.KillQuietly(t);
                        }
                    }
                    catch { ok = false; }
                    ddaWorks[idx] = ok;
                }
            }
            if (!ok) return null;
            var sb = all[idx].Bounds;
            return string.Format(inv, "-f lavfi -i \"ddagrab=output_idx={0}:offset_x={1}:offset_y={2}:video_size={3}x{4}:framerate={5}:draw_mouse={6},hwdownload,format=bgra\" ",
                idx, region.X - sb.Left, region.Y - sb.Top, w, h, fps, cursor ? 1 : 0);
        }

        /// <summary>
        /// FFmpeg arguments for one recording segment. videoInput/micInput/pcInput are the input options
        /// ending in "-i ..." (mic and PC sound may be null). Kept separate so tests can swap in other inputs.
        /// </summary>
        internal static string BuildArgs(string videoInput, string micInput, string pcInput, int fps, string seg, string delayInput = null)
        {
            // -loglevel info prints the "Input #0" line that times the picture's start (see StartGapMeter);
            // -stats every 0.5 s puts FFmpeg's progress into the recording log for diagnosis
            var sb = new StringBuilder("-hide_banner -loglevel info -stats -stats_period 0.5 ");
            bool mic = micInput != null, pcs = pcInput != null;
            // Input order matters for sync. FFmpeg times each input from its own first packet. The screen goes
            // first: its first frame is grabbed while it is opened, so the picture's clock is fixed before the
            // sound pipe connects, and a slow screen-capture start-up can't leave the sound ahead of the picture.
            // (The PC sound pipe is opened with a tiny probe, so FFmpeg doesn't sit reading seconds of it first.)
            // -analyzeduration 0: FFmpeg stops studying the screen input after its first frame, so the sound pipe
            // is opened about one frame after the picture's clock starts (otherwise it reads several frames first)
            sb.Append("-thread_queue_size 1024 -analyzeduration 0 -probesize 32 ").Append(videoInput);
            int v = 0, n = 1, micIdx = -1, pcIdx = -1;
            // tests only: an extra, unused input that is slow to open, to stand in for a slow screen-capture start
            if (delayInput != null) { sb.Append("-thread_queue_size 1024 -probesize 32 -analyzeduration 0 ").Append(delayInput); n++; }
            if (mic) { sb.Append("-thread_queue_size 1024 ").Append(micInput); micIdx = n++; }
            if (pcs) { sb.Append("-thread_queue_size 1024 -probesize 32 -analyzeduration 0 -fflags nobuffer ").Append(pcInput); pcIdx = n++; }
            const string Stereo = "aformat=sample_rates=48000:channel_layouts=stereo";
            var inv = CultureInfo.InvariantCulture;
            if (mic && pcs)
                sb.AppendFormat(inv, "-filter_complex \"[{0}:a]" + Stereo + "[m];[{1}:a]" + Stereo + "[p];[m][p]amix=inputs=2:duration=longest:normalize=0[a]\" -map {2}:v -map \"[a]\" -c:a pcm_s16le ", micIdx, pcIdx, v);
            else if (mic || pcs)
                sb.AppendFormat(inv, "-map {0}:v -map {1}:a -af " + Stereo + " -c:a pcm_s16le ", v, mic ? micIdx : pcIdx);
            else sb.AppendFormat(inv, "-map {0}:v ", v);
            sb.Append(VideoQuality.LosslessCapture);
            sb.AppendFormat(CultureInfo.InvariantCulture, "-g {0} -y ", fps * 2);
            sb.Append(FFmpeg.Quote(seg));
            return sb.ToString();
        }

        /// <summary>Ends the current segment gracefully (FFmpeg finishes the file).</summary>
        public void StopSegment()
        {
            var p = proc;
            if (p == null) return;
            proc = null;
            segClock.Stop();
            before += segClock.Elapsed.TotalSeconds;
            var last = segments[segments.Count - 1];
            var pcLog = pc;
            if (pcLog != null) lock (diagLogs) { StringBuilder d; if (diagLogs.TryGetValue(last, out d)) lock (d) d.AppendFormat(CultureInfo.InvariantCulture, "stop: pc sound connected {0:0.000} s ago\n", pcLog.SecondsSinceConnect); }
            StopPc(); // FFmpeg sees the end of the PC sound, then "q" ends the picture
            try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
            try { if (!p.WaitForExit(8000)) FFmpeg.KillQuietly(p); } catch { }
            try { p.Dispose(); } catch { }
            WriteDiagLog(last);
            try { if (!File.Exists(last) || new FileInfo(last).Length < 1024) { segments.Remove(last); ImageCache.Delete(last); } } catch { }
        }

        /// <summary>
        /// Joins the lossless segments and encodes them once into an MP4 (H.264 4:2:0, BT.709, AAC) in the cache.
        /// Runs FFmpeg for a while on long recordings, so call it off the UI thread; progress gets 0..1.
        /// Returns the file's path. Throws OperationCanceledException if Discard was called meanwhile.
        /// </summary>
        public string Finish(Action<double> progress = null)
        {
            StopSegment();
            if (segments.Count == 0) throw new InvalidOperationException("Nothing was recorded.");
            double total = Math.Max(0.1, before);
            var inv = CultureInfo.InvariantCulture;
            string list = Path.Combine(MediaLibrary.Folder, "rec-" + Guid.NewGuid().ToString("N") + ".txt");
            var sb = new StringBuilder();
            foreach (var s in segments) sb.Append("file '").Append(s.Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
            File.WriteAllText(list, sb.ToString(), new UTF8Encoding(false));
            string outPath = Path.Combine(MediaLibrary.Folder, "Recording " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", inv) + ".mp4");
            string args = "-hide_banner -nostdin -loglevel error -nostats -progress pipe:1 -f concat -safe 0 -protocol_whitelist file -i " + FFmpeg.Quote(list) + " " +
                          "-map 0:v:0 -map 0:a:0? " + VideoQuality.H264FromRgb(VideoQuality.RecordingCrf, VideoQuality.RecordingPreset) +
                          string.Format(inv, "-c:a aac -b:a {0}k -movflags +faststart -y ", VideoQuality.RecordingAudioKbps) + FFmpeg.Quote(outPath);
            var err = new StringBuilder();
            int exit = -1;
            try
            {
                using (var p = FFmpeg.Start(args))
                {
                    finishProc = p;
                    if (discarded) FFmpeg.KillQuietly(p);
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) { if (err.Length < 6000) err.AppendLine(e.Data); } };
                    p.BeginErrorReadLine();
                    string line;
                    var r = p.StandardOutput;
                    while ((line = r.ReadLine()) != null)
                    {
                        // -progress writes key=value lines; out_time_us is how far the encode has got
                        if (progress == null || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;
                        long us;
                        if (long.TryParse(line.Substring(12), NumberStyles.Integer, inv, out us) && us > 0) progress(Math.Min(1, us / 1e6 / total));
                    }
                    p.WaitForExit();
                    exit = p.ExitCode;
                }
            }
            catch (Exception) { if (!discarded) throw; }
            finally { finishProc = null; ImageCache.Delete(list); }
            if (discarded) { ImageCache.Delete(outPath); throw new OperationCanceledException(); }
            if (exit != 0 || !File.Exists(outPath))
            {
                ImageCache.Delete(outPath);
                string detail;
                lock (err) detail = FFmpeg.LastMeaningfulLine(err.ToString());
                throw new IOException(detail ?? "The recording couldn't be saved.");
            }
            foreach (var s in segments) ImageCache.Delete(s);
            segments.Clear();
            before = 0;
            return outPath;
        }

        /// <summary>Free space where recordings are written (lossless segments can be large for busy screens).</summary>
        public static long FreeBytes()
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(MediaLibrary.Folder))).AvailableFreeSpace; }
            catch { return long.MaxValue; }
        }

        // Diagnostic log of every recording segment, appended to %LOCALAPPDATA%\StillsAndReels\recording-log.txt
        // (the cache is wiped when the app closes, so it lives outside it). Trimmed when it passes 2 MB.
        readonly Dictionary<string, StringBuilder> diagLogs = new Dictionary<string, StringBuilder>();
        readonly Dictionary<string, PcSoundCapture> diagPc = new Dictionary<string, PcSoundCapture>();

        public static string LogPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StillsAndReels", "recording-log.txt"); }
        }

        void WriteDiagLog(string seg)
        {
            StringBuilder d; PcSoundCapture c;
            if (!diagLogs.TryGetValue(seg, out d)) return;
            diagLogs.Remove(seg);
            diagPc.TryGetValue(seg, out c); diagPc.Remove(seg);
            try
            {
                lock (d)
                {
                    if (c != null) d.AppendFormat(CultureInfo.InvariantCulture, "pc sound: connected {0:0.000} s before stop; picture lead {1}; silence inserted {2:0.000} s\n",
                        c.SecondsSinceConnect, double.IsNaN(c.Gap.Lead) ? "not measured" : c.Gap.Lead.ToString("0.000", CultureInfo.InvariantCulture) + " s", c.Gap.Inserted);
                    string path = LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    try { var fi = new FileInfo(path); if (fi.Exists && fi.Length > 2 << 20) fi.Delete(); } catch { }
                    File.AppendAllText(path, "=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + Path.GetFileName(seg) + "\n" + d + "\n");
                }
            }
            catch { }
        }

        void StopPc()
        {
            var c = pc;
            pc = null;
            if (c != null) c.Dispose();
        }

        public void Discard()
        {
            discarded = true;
            var p = proc;
            proc = null;
            FFmpeg.KillQuietly(p);
            StopPc();
            var f = finishProc;
            if (f != null) try { if (!f.HasExited) f.Kill(); } catch { }
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
        readonly ThemedButton record, area, screenBtn, cursorBtn, micBtn, pcBtn, fpsBtn;
        readonly ThemedButton addBtn, saveBtn, discardBtn;
        readonly Func<string, bool, bool> addToEditor;  // (path, temporary) -> added
        bool regionMode = true;
        Rectangle? fixedScreen;          // full-screen choice
        bool cursor = true;
        int fps = 30;
        string mic;
        bool pcSound = true;
        List<string> mics;

        void Remember()
        {
            if (settings == null) return;
            settings.Set("VideoSnip.PcSound", pcSound);
            settings.Set("VideoSnip.Cursor", cursor);
            settings.Set("VideoSnip.Fps", fps);
            settings.Set("VideoSnip.Mic", mic ?? "");
            try { settings.Save(); } catch { }
        }
        ScreenRecorder rec;
        RecordBar recBar;
        RegionFrame frame;
        bool paused;
        bool finishing;         // the recording is being encoded into its MP4
        readonly System.Windows.Forms.Timer diskTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        const long MinFreeToStart = 1L << 30, MinFreeToContinue = 600L << 20;
        double finishProgress;
        string resultPath;      // the recording (in the cache)
        string savedPath;       // where the user saved it, if they did
        bool added;

        readonly Settings settings;

        public VideoSnipForm(Form host, Settings settings, Func<string, bool, bool> addToEditor)
        {
            this.host = host;
            this.settings = settings;
            this.addToEditor = addToEditor;
            // the last choices are kept; PC sound starts on, since most recordings want the sound of what's playing
            if (settings != null)
            {
                pcSound = settings.GetBool("VideoSnip.PcSound", true);
                cursor = settings.GetBool("VideoSnip.Cursor", true);
                int f = settings.GetInt("VideoSnip.Fps", 30);
                fps = f == 15 || f == 60 ? f : 30;
                string m = settings.GetString("VideoSnip.Mic", "");
                mic = string.IsNullOrEmpty(m) ? null : m;
            }
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
            cursorBtn = Btn(Ico.Pointer, null, "Show the mouse pointer in the recording", (s, e) => { cursor = !cursor; Remember(); Sync(); }, 40);
            micBtn = Btn(Ico.Mic, "No mic", "Record a microphone", (s, e) => ChooseMic(), 150);
            pcBtn = Btn(Ico.Volume, "PC sound", "Also record what the PC plays: videos, music, games and calls", (s, e) => { pcSound = !pcSound; Remember(); Sync(); }, 116);
            fpsBtn = Btn(Ico.Speed, "30 fps", "Frames per second", (s, e) => { fps = fps == 30 ? 60 : fps == 60 ? 15 : 30; Remember(); Sync(); }, 84);

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
            int w = Theme.S(860), h = Theme.S(520);
            int x = host.Right + Theme.S(8);
            if (x + w > wa.Right) x = Math.Max(wa.Left, wa.Right - w - Theme.S(16));
            int y = Math.Max(wa.Top, Math.Min(host.Top + Theme.S(60), wa.Bottom - h));
            Bounds = new Rectangle(x, y, w, h);
            Theme.Changed += OnTheme;
            diskTimer.Tick += (s, e) => CheckDisk();
            diskTimer.Start();
            Sync();
        }

        /// <summary>Stops (and keeps) a recording before the disk fills up.</summary>
        void CheckDisk()
        {
            if (rec == null || !rec.IsRecording || finishing) return;
            if (ScreenRecorder.FreeBytes() >= MinFreeToContinue) return;
            StopRecording();
            MessageBox.Show(this, "Your disk is nearly full, so the recording was stopped. What was recorded so far is kept.", "Video snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void OnTheme(object s, EventArgs e) { flow.BackColor = Theme.Current.Bar; Theme.ApplyTo(this); }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.ApplyTo(this); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnTheme;
                diskTimer.Dispose();
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
            bool busy = finishing || rec != null && (rec.IsRecording || paused);
            area.Checked = regionMode;
            screenBtn.Checked = !regionMode;
            screenBtn.Text = !regionMode && fixedScreen.HasValue ? ScreenName(fixedScreen.Value) : "Full screen";
            cursorBtn.Checked = cursor;
            micBtn.Text = string.IsNullOrEmpty(mic) ? "No mic" : mic;
            pcBtn.Checked = pcSound;
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
            menu.Items.Add(new ToolStripMenuItem("No microphone", null, (o, e) => { mic = null; Remember(); Sync(); }) { Checked = mic == null });
            foreach (var m in mics)
            {
                string mm = m;
                menu.Items.Add(new ToolStripMenuItem(mm, null, (o, e) => { mic = mm; Remember(); Sync(); }) { Checked = mic == mm });
            }
            if (mics.Count == 0)
                menu.Items.Add(new ToolStripMenuItem("No microphone found") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("For the PC's own sound, use the PC sound button") { Enabled = false });
            menu.Closed += (o, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            Ui.ShowMenuBelow(menu, micBtn);
        }

        void PaintIdle(object sender, PaintEventArgs e)
        {
            if (view.Visible) return;
            string title, detail;
            if (finishing) { title = "Finishing the recording… " + (int)Math.Round(finishProgress * 100) + "%"; detail = "Encoding it in high quality. Long recordings take a little while."; }
            else if (rec != null && (rec.IsRecording || paused)) { title = paused ? "Recording paused" : "Recording…"; detail = "Use the bar on screen to pause or stop."; }
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
            if (finishing || rec != null && (rec.IsRecording || paused)) return;
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
            if (ScreenRecorder.FreeBytes() < MinFreeToStart)
            {
                MessageBox.Show(this, "There's less than 1 GB free on the disk that holds the app's cache, so recording can't start. Free up some space and try again.", "Video snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            rec = new ScreenRecorder { Region = region, Fps = fps, ShowCursor = cursor, Microphone = mic, PcSound = pcSound };
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
            if (rec == null || finishing) return;
            CloseRecordingUi();
            Show();
            Activate();
            var r = rec;
            finishing = true;
            finishProgress = 0;
            paused = false;
            Sync();
            // the lossless segments are encoded once into the final MP4; that can take a while, so off the UI thread
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string path = null;
                Exception error = null;
                try { path = r.Finish(p => Post(() => { finishProgress = p; content.Invalidate(); })); }
                catch (Exception ex) { error = ex; }
                Post(() => FinishDone(r, path, error));
            });
        }

        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        void FinishDone(ScreenRecorder r, string path, Exception error)
        {
            if (!ReferenceEquals(rec, r)) { if (path != null) ImageCache.Delete(path); return; } // discarded meanwhile
            finishing = false;
            rec.Dispose();
            rec = null;
            if (error is OperationCanceledException) { Sync(); return; }
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
            if (finishing)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    var r = MessageBox.Show(this, "The recording is still being finished. Stop and discard it?", "Video snip", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (r != DialogResult.OK) { e.Cancel = true; return; }
                }
                finishing = false;
                var fr = rec;
                rec = null;
                if (fr != null) fr.Discard();
            }
            else if (rec != null && (rec.IsRecording || paused))
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
