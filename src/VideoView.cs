using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>Shows decoded video frames, letterboxed, plus empty / loading / error states.</summary>
    internal sealed class VideoSurface : Control, IFrameSink
    {
        readonly object frameLock = new object();
        Bitmap frame;
        int pendingPaint;
        public string StateTitle, StateDetail;
        public Ico StateIcon = Ico.Video;
        public bool StateWarning;
        public bool ShowState = true;

        public VideoSurface()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        public void Present(byte[] bgra, int width, int height)
        {
            lock (frameLock)
            {
                if (frame == null || frame.Width != width || frame.Height != height)
                {
                    if (frame != null) frame.Dispose();
                    frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                }
                var data = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    if (data.Stride == width * 4) Marshal.Copy(bgra, 0, data.Scan0, width * height * 4);
                    else
                        for (int y = 0; y < height; y++)
                            Marshal.Copy(bgra, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
                }
                finally { frame.UnlockBits(data); }
            }
            if (System.Threading.Interlocked.Exchange(ref pendingPaint, 1) == 0 && IsHandleCreated && !IsDisposed)
            {
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        System.Threading.Interlocked.Exchange(ref pendingPaint, 0);
                        ShowState = false;
                        Invalidate();
                    }));
                }
                catch { System.Threading.Interlocked.Exchange(ref pendingPaint, 0); }
            }
        }

        public bool HasFrame { get { lock (frameLock) return frame != null; } }

        public void ClearFrame()
        {
            lock (frameLock)
            {
                if (frame != null) { frame.Dispose(); frame = null; }
            }
            Invalidate();
        }

        public void SetState(Ico icon, string title, string detail, bool warning)
        {
            StateIcon = icon; StateTitle = title; StateDetail = detail; StateWarning = warning;
            ShowState = true;
            Invalidate();
        }

        public Size VideoAspectFit(Size video)
        {
            var cs = ClientSize;
            if (video.Width <= 0 || video.Height <= 0 || cs.Width <= 0 || cs.Height <= 0) return new Size(2, 2);
            double s = Math.Min((double)cs.Width / video.Width, (double)cs.Height / video.Height);
            s = Math.Min(1.0, s);
            int w = Math.Max(2, (int)(video.Width * s)) & ~1;
            int h = Math.Max(2, (int)(video.Height * s)) & ~1;
            // keep the pipe light: at most ~2.1 megapixels per frame
            double area = (double)w * h;
            if (area > 2100000)
            {
                double k = Math.Sqrt(2100000 / area);
                w = Math.Max(2, (int)(w * k)) & ~1;
                h = Math.Max(2, (int)(h * k)) & ~1;
            }
            return new Size(w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            bool drawn = false;
            if (!ShowState)
            {
                g.Clear(p.VideoBg);
                lock (frameLock)
                {
                    if (frame != null)
                    {
                        var cs = ClientSize;
                        double s = Math.Min((double)cs.Width / frame.Width, (double)cs.Height / frame.Height);
                        int w = (int)(frame.Width * s), h = (int)(frame.Height * s);
                        var dest = new Rectangle((cs.Width - w) / 2, (cs.Height - h) / 2, w, h);
                        g.InterpolationMode = Math.Abs(s - 1) < 0.01 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(frame, dest);
                        g.CompositingMode = CompositingMode.SourceOver;
                        drawn = true;
                    }
                }
            }
            if (!drawn)
            {
                g.Clear(p.Canvas);
                Theme.DrawEmptyState(g, ClientRectangle, StateIcon,
                    StateTitle ?? "Click a video in File Explorer",
                    StateTitle == null ? "Select a video in any File Explorer window and it will appear here.\nPlays MP4, MOV, AVI, MKV, WebM, WMV and more. Use File > Open to pick one directly." : StateDetail,
                    StateWarning);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) lock (frameLock) { if (frame != null) frame.Dispose(); frame = null; }
            base.Dispose(disposing);
        }
    }

    /// <summary>Play/pause, timeline, time, speed, volume and fullscreen.</summary>
    internal sealed class VideoControls : Control
    {
        readonly VideoPlayer player;
        Rectangle rPlay, rTrack, rTime, rSpeed, rMute, rVolume, rFull;
        string hover;
        bool scrubbing, volDrag;
        double scrubTime = -1;
        public bool Fullscreen;
        public event EventHandler FullscreenRequested;
        readonly Timer tick = new Timer { Interval = 100 };

        public VideoControls(VideoPlayer player)
        {
            this.player = player;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(58);
            Dock = DockStyle.Bottom;
            player.StateChanged += (s, e) => Invalidate();
            tick.Tick += (s, e) => { if (player.IsPlaying) Invalidate(); };
            tick.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tick.Dispose();
            base.Dispose(disposing);
        }

        public static string FormatTime(double t, double total)
        {
            if (double.IsNaN(t) || t < 0) t = 0;
            int s = (int)Math.Floor(t + 0.0001);
            int h = s / 3600, m = (s / 60) % 60, sec = s % 60;
            if (total >= 3600) return h + ":" + m.ToString("00") + ":" + sec.ToString("00");
            return (h * 60 + m) + ":" + sec.ToString("00");
        }

        string SpeedText
        {
            get
            {
                double s = player.Speed;
                return (Math.Abs(s - Math.Round(s)) < 0.001 ? ((int)Math.Round(s)).ToString() : s.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)) + "×";
            }
        }

        void LayoutRects()
        {
            int h = Height, pad = Theme.S(14), b = Theme.S(40);
            int y = (h - b) / 2;
            rPlay = new Rectangle(pad, y, b, b);
            rFull = new Rectangle(Width - pad - b, y, b, b);
            rVolume = new Rectangle(rFull.Left - Theme.S(10) - Theme.S(96), y, Theme.S(96), b);
            rMute = new Rectangle(rVolume.Left - Theme.S(2) - b, y, b, b);
            rSpeed = new Rectangle(rMute.Left - Theme.S(8) - Theme.S(54), y, Theme.S(54), b);
            var tf = Theme.UiFont(10f);
            string sample = FormatTime(player.Duration, player.Duration) + " / " + FormatTime(player.Duration, player.Duration);
            int tw = TextRenderer.MeasureText(sample, tf).Width + Theme.S(8);
            rTime = new Rectangle(rPlay.Right + Theme.S(10), y, tw, b);
            rTrack = new Rectangle(rTime.Right + Theme.S(10), y, Math.Max(Theme.S(40), rSpeed.Left - Theme.S(14) - rTime.Right - Theme.S(10)), b);
        }

        string HitTest(Point p)
        {
            if (rPlay.Contains(p)) return "play";
            if (rTrack.Contains(p)) return "track";
            if (rSpeed.Contains(p)) return "speed";
            if (rMute.Contains(p)) return "mute";
            if (rVolume.Contains(p)) return "volume";
            if (rFull.Contains(p)) return "full";
            return null;
        }

        double TimeAt(int x)
        {
            float pad = Theme.F(8);
            double t = (x - rTrack.Left - pad) / Math.Max(1.0, rTrack.Width - 2 * pad);
            return Math.Max(0, Math.Min(1, t)) * player.Duration;
        }

        void SetVolumeAt(int x)
        {
            float pad = Theme.F(8);
            double v = (x - rVolume.Left - pad) / Math.Max(1.0, rVolume.Width - 2 * pad);
            player.Volume = (float)Math.Max(0, Math.Min(1, v));
            if (player.Muted && player.Volume > 0) player.Muted = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            LayoutRects();
            if (scrubbing)
            {
                scrubTime = TimeAt(e.X);
                player.Scrub(scrubTime);
                Invalidate();
                return;
            }
            if (volDrag) { SetVolumeAt(e.X); return; }
            string h = HitTest(e.Location);
            if (h != hover)
            {
                hover = h;
                Cursor = h != null && player.Info != null ? Cursors.Hand : Cursors.Default;
                string tip = null;
                switch (h)
                {
                    case "play": tip = player.IsPlaying ? "Pause (Space)" : "Play (Space)"; break;
                    case "track": tip = "Drag to move through the video (← → jump 5 s)"; break;
                    case "speed": tip = "Playback speed"; break;
                    case "mute": tip = player.Muted ? "Unmute (M)" : "Mute (M)"; break;
                    case "volume": tip = "Volume (↑ ↓)"; break;
                    case "full": tip = Fullscreen ? "Exit full screen (Esc)" : "Full screen (F)"; break;
                }
                Ui.Tip.SetToolTip(this, tip);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!scrubbing) { hover = null; Invalidate(); } }

        bool wasPlaying;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || player.Info == null) return;
            LayoutRects();
            string h = HitTest(e.Location);
            if (h == "track" && player.Duration > 0)
            {
                scrubbing = true;
                wasPlaying = player.IsPlaying;
                if (wasPlaying) player.Pause();
                Capture = true;
                scrubTime = TimeAt(e.X);
                player.Scrub(scrubTime);
            }
            else if (h == "volume") { volDrag = true; Capture = true; SetVolumeAt(e.X); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (scrubbing)
            {
                scrubbing = false;
                Capture = false;
                player.Seek(TimeAt(e.X));
                scrubTime = -1;
                if (wasPlaying) player.Play();
                Invalidate();
                return;
            }
            if (volDrag) { volDrag = false; Capture = false; return; }
            if (e.Button != MouseButtons.Left || player.Info == null) return;
            LayoutRects();
            switch (HitTest(e.Location))
            {
                case "play": player.Toggle(); break;
                case "mute": player.Muted = !player.Muted; break;
                case "speed": ShowSpeedMenu(); break;
                case "full": { var h = FullscreenRequested; if (h != null) h(this, EventArgs.Empty); break; }
            }
        }

        public static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2 };

        void ShowSpeedMenu()
        {
            var menu = new ContextMenuStrip();
            foreach (var s in Speeds)
            {
                double sp = s;
                var mi = new ToolStripMenuItem(Math.Abs(sp - 1) < 0.001 ? "Normal (1×)" : sp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×")
                { Checked = Math.Abs(player.Speed - sp) < 0.001 };
                mi.Click += (o, e) => player.SetSpeed(sp);
                menu.Items.Add(mi);
            }
            Theme.StyleMenu(menu);
            menu.Closed += (o, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            var pt = new Point(rSpeed.Left, rSpeed.Top);
            menu.Show(this, new Point(pt.X, pt.Y - menu.GetPreferredSize(Size.Empty).Height - Theme.S(4)));
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(p.Bar);
            using (var pen = new Pen(p.BarBorder)) g.DrawLine(pen, 0, 0, Width, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            LayoutRects();
            bool has = player.Info != null;
            Color fg = has ? p.Text : p.Muted;

            // play button: gradient circle
            var pr = RectangleF.Inflate(rPlay, -Theme.F(1), -Theme.F(1));
            if (has)
            {
                if (hover == "play") Theme.DrawGlow(g, pr, pr.Width / 2, p.AccentA, Theme.F(5), 90);
                using (var b = Theme.AccentGradient(pr)) g.FillEllipse(b, pr);
            }
            else using (var b = new SolidBrush(p.Control)) g.FillEllipse(b, pr);
            var ir = RectangleF.Inflate(pr, -Theme.F(11), -Theme.F(11));
            if (!player.IsPlaying) ir.Offset(Theme.F(1), 0);
            Icons.Draw(g, player.IsPlaying ? Ico.Pause : Ico.Play, ir, has ? Color.White : p.Muted, 1.6f);

            // time
            double dur = player.Duration;
            double pos = scrubTime >= 0 ? scrubTime : player.Position;
            string time = FormatTime(pos, dur) + " / " + FormatTime(dur, dur);
            TextRenderer.DrawText(g, time, Theme.UiFont(10f), rTime, has ? p.SubText : p.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // timeline
            float pad = Theme.F(8), th = Theme.F(hover == "track" || scrubbing ? 6 : 4);
            var track = new RectangleF(rTrack.Left + pad, rTrack.Top + rTrack.Height / 2f - th / 2, rTrack.Width - 2 * pad, th);
            using (var path = Theme.Rounded(track, th / 2))
            using (var b = new SolidBrush(p.Control)) g.FillPath(b, path);
            if (has && dur > 0)
            {
                float t = (float)Math.Max(0, Math.Min(1, pos / dur));
                float x = track.Left + track.Width * t;
                if (x - track.Left > 1)
                    using (var path = Theme.Rounded(new RectangleF(track.Left, track.Top, x - track.Left, th), th / 2))
                    using (var b = Theme.AccentGradient(track)) g.FillPath(b, path);
                float k = Theme.F(hover == "track" || scrubbing ? 8 : 6.5f);
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x - k, track.Top + th / 2 - k, k * 2, k * 2);
                using (var pen = new Pen(p.AccentA, Theme.F(2))) g.DrawEllipse(pen, x - k, track.Top + th / 2 - k, k * 2, k * 2);
            }

            // speed
            var sr = RectangleF.Inflate(rSpeed, 0, -Theme.F(5));
            using (var path = Theme.Rounded(sr, sr.Height / 2))
            {
                using (var b = new SolidBrush(hover == "speed" && has ? p.Hover : p.Control)) g.FillPath(b, path);
                if (Math.Abs(player.Speed - 1) > 0.001)
                    using (var b = Theme.AccentGradient(sr)) using (var pen = new Pen(b, Theme.F(1.3f))) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, SpeedText, Theme.UiFont(10f, FontStyle.Bold), Rectangle.Round(sr), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // mute + volume
            if (hover == "mute" && has)
                using (var path = Theme.Rounded(rMute, Theme.F(8))) using (var b = new SolidBrush(p.Hover)) g.FillPath(b, path);
            Ico vi = player.Muted || player.Volume <= 0.001f ? Ico.Mute : player.Volume < 0.5f ? Ico.VolumeLow : Ico.Volume;
            Icons.Draw(g, vi, RectangleF.Inflate(rMute, -Theme.F(10), -Theme.F(10)), fg, 1.7f);
            var vt = new RectangleF(rVolume.Left + pad, rVolume.Top + rVolume.Height / 2f - Theme.F(2), rVolume.Width - 2 * pad, Theme.F(4));
            using (var path = Theme.Rounded(vt, Theme.F(2))) using (var b = new SolidBrush(p.Control)) g.FillPath(b, path);
            float vol = player.Muted ? 0 : player.Volume;
            float vx = vt.Left + vt.Width * vol;
            if (vx - vt.Left > 1)
                using (var path = Theme.Rounded(new RectangleF(vt.Left, vt.Top, vx - vt.Left, vt.Height), Theme.F(2)))
                using (var b = has ? (Brush)Theme.AccentGradient(vt) : new SolidBrush(p.Muted)) g.FillPath(b, path);
            float vk = Theme.F(6);
            using (var b = new SolidBrush(has ? Color.White : p.Muted)) g.FillEllipse(b, vx - vk, vt.Top + vt.Height / 2 - vk, vk * 2, vk * 2);
            using (var pen = new Pen(has ? p.AccentA : p.ControlBorder, Theme.F(1.5f))) g.DrawEllipse(pen, vx - vk, vt.Top + vt.Height / 2 - vk, vk * 2, vk * 2);

            // fullscreen
            if (hover == "full" && has)
                using (var path = Theme.Rounded(rFull, Theme.F(8))) using (var b = new SolidBrush(p.Hover)) g.FillPath(b, path);
            Icons.Draw(g, Fullscreen ? Ico.ExitFullscreen : Ico.Fullscreen, RectangleF.Inflate(rFull, -Theme.F(10), -Theme.F(10)), fg, 1.7f);
        }
    }

    /// <summary>Videos mode: surface on top, controls at the bottom.</summary>
    internal sealed class VideoView : Control
    {
        public readonly VideoSurface Surface = new VideoSurface();
        public readonly VideoPlayer Player;
        public readonly VideoControls Controls2;
        readonly Timer resizeTimer = new Timer { Interval = 350 };
        Size lastOut;

        public VideoView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Player = new VideoPlayer(Surface);
            Player.OutputSize = () =>
            {
                var info = Player.Info;
                lastOut = info != null ? Surface.VideoAspectFit(new Size(info.Width, info.Height)) : new Size(2, 2);
                return lastOut;
            };
            Controls2 = new VideoControls(Player);
            Surface.Dock = DockStyle.Fill;
            Controls.Add(Surface);
            Controls.Add(Controls2);
            resizeTimer.Tick += (s, e) =>
            {
                resizeTimer.Stop();
                var info = Player.Info;
                if (info == null || !info.HasVideo) return;
                var want = Surface.VideoAspectFit(new Size(info.Width, info.Height));
                // re-decode at the new size only when it changed noticeably
                if (Math.Abs(want.Width - lastOut.Width) > lastOut.Width * 0.15 || Math.Abs(want.Height - lastOut.Height) > lastOut.Height * 0.15)
                    Player.Refresh();
            };
            Surface.Resize += (s, e) => { resizeTimer.Stop(); resizeTimer.Start(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { resizeTimer.Dispose(); Player.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
