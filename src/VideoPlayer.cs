using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StillsAndReels
{
    /// <summary>Minimal waveOut (winmm) PCM output: 48 kHz, stereo, 16-bit.</summary>
    internal sealed class WaveOut : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WaveFormat
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MmTime { public uint wType; public uint u; public uint pad; }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int device, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr hwo, ref MmTime t, int size);

        public const int Rate = 48000, Channels = 2, BytesPerFrame = 4;
        const uint WHDR_DONE = 1;

        IntPtr hwo;
        readonly IntPtr[] headers, buffers;
        readonly int bufferBytes;
        readonly int hdrSize = Marshal.SizeOf(typeof(WaveHdr));
        readonly int flagsOffset = (int)Marshal.OffsetOf(typeof(WaveHdr), "dwFlags");
        readonly int lengthOffset = (int)Marshal.OffsetOf(typeof(WaveHdr), "dwBufferLength");
        readonly bool[] queued;
        long written; // frames handed to the device

        public WaveOut(int bufferCount, int bufferFrames)
        {
            var fmt = new WaveFormat
            {
                wFormatTag = 1, nChannels = Channels, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * BytesPerFrame,
                nBlockAlign = BytesPerFrame, wBitsPerSample = 16, cbSize = 0
            };
            int r = waveOutOpen(out hwo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) throw new IOException("No audio output device is available (code " + r + ").");
            bufferBytes = bufferFrames * BytesPerFrame;
            headers = new IntPtr[bufferCount];
            buffers = new IntPtr[bufferCount];
            queued = new bool[bufferCount];
            for (int i = 0; i < bufferCount; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(bufferBytes);
                headers[i] = Marshal.AllocHGlobal(hdrSize);
                var h = new WaveHdr { lpData = buffers[i], dwBufferLength = (uint)bufferBytes };
                Marshal.StructureToPtr(h, headers[i], false);
                waveOutPrepareHeader(hwo, headers[i], hdrSize);
            }
        }

        public int BufferBytes { get { return bufferBytes; } }
        public int Count { get { return headers.Length; } }

        /// <summary>Index of a buffer that is free to fill, or -1.</summary>
        public int FreeBuffer()
        {
            for (int i = 0; i < headers.Length; i++)
            {
                if (!queued[i]) return i;
                uint flags = (uint)Marshal.ReadInt32(headers[i], flagsOffset);
                if ((flags & WHDR_DONE) != 0) { queued[i] = false; return i; }
            }
            return -1;
        }

        public bool AllDone
        {
            get
            {
                for (int i = 0; i < headers.Length; i++)
                {
                    if (!queued[i]) continue;
                    uint flags = (uint)Marshal.ReadInt32(headers[i], flagsOffset);
                    if ((flags & WHDR_DONE) == 0) return false;
                }
                return true;
            }
        }

        public void Write(int index, byte[] data, int count)
        {
            Marshal.Copy(data, 0, buffers[index], count);
            Marshal.WriteInt32(headers[index], lengthOffset, count);
            uint flags = (uint)Marshal.ReadInt32(headers[index], flagsOffset);
            Marshal.WriteInt32(headers[index], flagsOffset, (int)(flags & ~WHDR_DONE));
            queued[index] = true;
            written += count / BytesPerFrame;
            waveOutWrite(hwo, headers[index], hdrSize);
        }

        /// <summary>Frames actually played so far.</summary>
        public long PlayedFrames
        {
            get
            {
                var t = new MmTime { wType = 2 }; // TIME_SAMPLES
                if (waveOutGetPosition(hwo, ref t, Marshal.SizeOf(typeof(MmTime))) != 0) return 0;
                if (t.wType == 4) return t.u / BytesPerFrame; // driver answered in bytes
                return t.u;
            }
        }

        public void Pause() { var h = hwo; if (h != IntPtr.Zero) waveOutPause(h); }
        public void Resume() { var h = hwo; if (h != IntPtr.Zero) waveOutRestart(h); }

        public void Dispose()
        {
            var h = hwo;
            if (h == IntPtr.Zero) return;
            hwo = IntPtr.Zero;
            waveOutReset(h);
            for (int i = 0; i < headers.Length; i++)
            {
                waveOutUnprepareHeader(h, headers[i], hdrSize);
                Marshal.FreeHGlobal(headers[i]);
                Marshal.FreeHGlobal(buffers[i]);
            }
            waveOutClose(h);
        }
    }

    /// <summary>Receives decoded frames (BGRA). Implemented by the video surface.</summary>
    internal interface IFrameSink
    {
        void Present(byte[] bgra, int width, int height);
    }

    /// <summary>
    /// One continuous run of playback from a start position at a given speed. Seeking or changing speed
    /// creates a new session; pausing pauses the current one.
    /// </summary>
    internal sealed class PlaybackSession : IDisposable
    {
        readonly MediaInfo info;
        readonly double start, speed;
        readonly int width, height;
        readonly double fps;
        readonly IFrameSink sink;
        readonly Func<float> volume;
        readonly Stopwatch clock = new Stopwatch();
        readonly ManualResetEvent running = new ManualResetEvent(true);
        readonly object sync = new object();
        double correction;  // seconds of media time added to the stopwatch clock to follow audio
        volatile bool cancelled, paused;
        volatile bool videoDone, audioDone, endRaised;
        Process vproc, aproc;
        WaveOut wave;
        volatile bool audioStarted;
        double audioBase; // media time at which audio output started
        double lastFrameTime;
        readonly StringBuilder errors = new StringBuilder();
        int framesShown;

        public event Action<PlaybackSession> Ended;
        public event Action<PlaybackSession, string> Failed;

        public PlaybackSession(MediaInfo info, double start, double speed, Size outSize, IFrameSink sink, Func<float> volume)
        {
            this.info = info; this.start = start; this.speed = speed; this.sink = sink; this.volume = volume;
            width = outSize.Width; height = outSize.Height;
            fps = Math.Min(60, Math.Max(1, info.Fps));
            lastFrameTime = start;
        }

        public double Speed { get { return speed; } }

        /// <summary>Current media time in seconds.</summary>
        public double Position
        {
            get
            {
                double t;
                lock (sync) t = start + clock.Elapsed.TotalSeconds * speed + correction;
                if (info.HasVideo && videoDone) t = Math.Min(t, Math.Max(lastFrameTime, start));
                return Math.Max(0, info.Duration > 0 ? Math.Min(info.Duration, t) : t);
            }
        }

        public void Start()
        {
            if (info.HasVideo)
            {
                string vf = string.Format(CultureInfo.InvariantCulture,
                    "{0}fps={1:0.###},{2}",
                    Math.Abs(speed - 1) > 0.001 ? string.Format(CultureInfo.InvariantCulture, "setpts=PTS/{0:0.####},", speed) : "",
                    fps, VideoQuality.ToBgra(width, height, false, info));
                string args = FFmpeg.InputArgs(info.Path, start) + "-map 0:v:0 -an -sn -dn -vf \"" + vf + "\" -f rawvideo -pix_fmt bgra pipe:1";
                vproc = FFmpeg.Start(args);
                Drain(vproc);
                new Thread(VideoLoop) { IsBackground = true, Name = "video" }.Start();
            }
            else videoDone = true;

            if (info.HasAudio && speed >= 0.25 && speed <= 4)
            {
                try
                {
                    wave = new WaveOut(8, 2400); // 8 × 50 ms
                    string af = Math.Abs(speed - 1) > 0.001 ? " -af \"" + Tempo(speed) + "\"" : "";
                    string args = FFmpeg.InputArgs(info.Path, start) + "-map 0:a:0 -vn -sn -dn" + af + " -f s16le -acodec pcm_s16le -ac 2 -ar 48000 pipe:1";
                    aproc = FFmpeg.Start(args);
                    Drain(aproc);
                    new Thread(AudioLoop) { IsBackground = true, Name = "audio" }.Start();
                }
                catch
                {
                    // no audio device (or not on Windows): play silently
                    if (wave != null) { wave.Dispose(); wave = null; }
                    audioDone = true;
                }
            }
            else audioDone = true;

            lock (sync) clock.Start();
        }

        static string Tempo(double s)
        {
            var sb = new StringBuilder();
            while (s < 0.5) { sb.Append("atempo=0.5,"); s /= 0.5; }
            while (s > 2.0) { sb.Append("atempo=2.0,"); s /= 2.0; }
            sb.Append("atempo=").Append(s.ToString("0.####", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        void Drain(Process p)
        {
            p.ErrorDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (errors) { if (errors.Length < 4000) errors.AppendLine(e.Data); }
            };
            p.BeginErrorReadLine();
        }

        public void Pause()
        {
            if (paused || cancelled) return;
            paused = true;
            lock (sync) clock.Stop();
            running.Reset();
            var w = wave;
            if (w != null) try { w.Pause(); } catch { }
        }

        public void Resume()
        {
            if (!paused || cancelled) return;
            paused = false;
            lock (sync) clock.Start();
            running.Set();
            var w = wave;
            if (w != null) try { w.Resume(); } catch { }
        }

        public bool IsPaused { get { return paused; } }

        static bool ReadFull(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        void VideoLoop()
        {
            int frameBytes = width * height * 4;
            var buf = new byte[frameBytes];
            long n = 0;
            try
            {
                var stream = vproc.StandardOutput.BaseStream;
                while (!cancelled)
                {
                    if (!ReadFull(stream, buf, frameBytes)) break;
                    double due = start + n * speed / fps;
                    n++;
                    // wait for the frame's moment
                    while (!cancelled)
                    {
                        running.WaitOne();
                        if (cancelled) break;
                        FollowAudio();
                        double now = start + clock.Elapsed.TotalSeconds * speed + correction;
                        double ahead = due - now;
                        if (ahead <= 0.004) break;
                        Thread.Sleep(Math.Max(1, Math.Min(30, (int)(ahead / speed * 1000) - 1)));
                    }
                    if (cancelled) break;
                    double lateBy = (start + clock.Elapsed.TotalSeconds * speed + correction) - due;
                    if (lateBy > 3.0 * speed / fps && framesShown > 0) continue; // drop late frames to catch up
                    lastFrameTime = due;
                    framesShown++;
                    sink.Present(buf, width, height);
                }
            }
            catch { }
            if (cancelled) return;
            if (framesShown == 0)
            {
                string err;
                lock (errors) err = errors.ToString();
                if (!string.IsNullOrWhiteSpace(err) || info.Duration > 0.5)
                {
                    var h = Failed;
                    if (h != null) h(this, FFmpeg.LastMeaningfulLine(err) ?? "The video couldn't be decoded.");
                    return;
                }
            }
            videoDone = true;
            // let the clock run to the end of the last frame's display time
            CheckEnd();
        }

        void FollowAudio()
        {
            var w = wave;
            if (w == null || !audioStarted || audioDone) return;
            double audioTime = audioBase + w.PlayedFrames / (double)WaveOut.Rate * speed;
            lock (sync)
            {
                double clk = start + clock.Elapsed.TotalSeconds * speed + correction;
                double drift = audioTime - clk;
                if (Math.Abs(drift) > 0.035 && Math.Abs(drift) < 5) correction += drift;
            }
        }

        void AudioLoop()
        {
            var w = wave;
            var buf = new byte[w.BufferBytes];
            var samples = new short[w.BufferBytes / 2];
            bool eof = false;
            try
            {
                var stream = aproc.StandardOutput.BaseStream;
                while (!cancelled)
                {
                    if (!eof)
                    {
                        int idx = w.FreeBuffer();
                        if (idx >= 0)
                        {
                            int got = 0;
                            while (got < buf.Length)
                            {
                                int r = stream.Read(buf, got, buf.Length - got);
                                if (r <= 0) { eof = true; break; }
                                got += r;
                            }
                            got -= got % WaveOut.BytesPerFrame;
                            if (got > 0)
                            {
                                float vol = volume();
                                if (Math.Abs(vol - 1f) > 0.001f)
                                {
                                    Buffer.BlockCopy(buf, 0, samples, 0, got);
                                    int count = got / 2;
                                    for (int i = 0; i < count; i++)
                                    {
                                        int v = (int)(samples[i] * vol);
                                        samples[i] = (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v);
                                    }
                                    Buffer.BlockCopy(samples, 0, buf, 0, got);
                                }
                                if (!audioStarted)
                                {
                                    audioBase = start;
                                    audioStarted = true;
                                }
                                w.Write(idx, buf, got);
                            }
                            continue;
                        }
                    }
                    else if (w.AllDone) break;
                    Thread.Sleep(paused ? 30 : 8);
                }
            }
            catch { }
            finally
            {
                // this thread owns the device: free it here so no other thread can touch freed buffers
                wave = null;
                try { w.Dispose(); } catch { }
            }
            if (cancelled) return;
            audioDone = true;
            CheckEnd();
        }

        void CheckEnd()
        {
            if (!videoDone || !audioDone || endRaised || cancelled) return;
            // video-only: wait until the last frame has had its display time
            if (info.HasVideo)
            {
                double endAt = lastFrameTime + speed / fps;
                while (!cancelled && Position < endAt - 0.001 && start + clock.Elapsed.TotalSeconds * speed + correction < endAt)
                {
                    running.WaitOne();
                    Thread.Sleep(10);
                }
            }
            if (cancelled || endRaised) return;
            endRaised = true;
            var h = Ended;
            if (h != null) h(this);
        }

        public void Dispose()
        {
            cancelled = true;
            running.Set();
            FFmpeg.KillQuietly(vproc);
            FFmpeg.KillQuietly(aproc);
            // silence immediately; the audio thread frees the device when it notices the cancel
            var w = wave;
            if (w != null) try { w.Pause(); } catch { }
        }
    }

    /// <summary>
    /// Video playback built on the bundled FFmpeg: decoded frames are streamed over a pipe and audio is
    /// played through waveOut, with the picture following the audio clock. Videos are read in place
    /// (read-only); nothing is copied or written.
    /// </summary>
    internal sealed class VideoPlayer : IDisposable
    {
        readonly IFrameSink sink;
        readonly SynchronizationContext ui;
        PlaybackSession session;
        double position;
        bool playing;
        double speed = 1;
        float volume = 1f;
        bool muted;
        int grabGeneration;
        Process grabProc;
        readonly object grabLock = new object();

        public event EventHandler StateChanged;
        public event EventHandler Ended;
        public event Action<string> Failed;

        public VideoPlayer(IFrameSink sink)
        {
            this.sink = sink;
            ui = SynchronizationContext.Current ?? new SynchronizationContext();
        }

        public MediaInfo Info { get; private set; }
        public Func<Size> OutputSize; // decided by the surface (fits the window)

        public bool IsPlaying { get { return playing; } }
        public double Speed { get { return speed; } }
        public float Volume { get { return volume; } set { volume = Math.Max(0, Math.Min(1, value)); Raise(); } }
        public bool Muted { get { return muted; } set { muted = value; Raise(); } }

        float EffectiveVolume() { return muted ? 0f : volume * volume; } // squared feels more natural

        public double Position
        {
            get
            {
                var s = session;
                return s != null ? s.Position : position;
            }
        }

        public double Duration { get { return Info != null ? Info.Duration : 0; } }

        void Raise() { var h = StateChanged; if (h != null) h(this, EventArgs.Empty); }

        public void Load(MediaInfo info)
        {
            StopSession();
            Info = info;
            position = 0;
            playing = false;
            Raise();
            if (info != null && info.HasVideo) GrabFrame(0);
        }

        public void Unload()
        {
            StopSession();
            KillGrab();
            Info = null;
            playing = false;
            position = 0;
            Raise();
        }

        public void Play()
        {
            if (Info == null) return;
            if (session != null && session.IsPaused && Math.Abs(session.Speed - speed) < 0.0001)
            {
                session.Resume();
                playing = true;
                Raise();
                return;
            }
            if (Info.Duration > 0 && position >= Info.Duration - 0.05) position = 0;
            StartSession(position);
            playing = true;
            Raise();
        }

        public void Pause()
        {
            if (!playing) return;
            playing = false;
            if (session != null)
            {
                position = session.Position;
                session.Pause();
            }
            Raise();
        }

        public void Toggle() { if (playing) Pause(); else Play(); }

        public void Seek(double t)
        {
            if (Info == null) return;
            if (Info.Duration > 0) t = Math.Max(0, Math.Min(Info.Duration - 0.01, t)); else t = Math.Max(0, t);
            position = t;
            if (playing) StartSession(t);
            else
            {
                StopSession();
                if (Info.HasVideo) GrabFrame(t);
            }
            Raise();
        }

        /// <summary>Preview while dragging the timeline: shows the frame without restarting playback.</summary>
        public void Scrub(double t)
        {
            if (Info == null) return;
            if (Info.Duration > 0) t = Math.Max(0, Math.Min(Info.Duration - 0.01, t));
            position = t;
            if (session != null) { StopSession(); }
            if (Info.HasVideo) GrabFrame(t);
            Raise();
        }

        public void SetSpeed(double s)
        {
            speed = s;
            if (playing) StartSession(Position);
            else if (session != null) { position = session.Position; StopSession(); }
            Raise();
        }

        /// <summary>Restart at the current spot (e.g. after the window was resized).</summary>
        public void Refresh()
        {
            if (Info == null) return;
            if (playing) StartSession(Position);
            else if (Info.HasVideo) GrabFrame(Position);
        }

        void StartSession(double at)
        {
            StopSession();
            KillGrab();
            var size = Info.HasVideo ? OutputSize() : new Size(2, 2);
            var s = new PlaybackSession(Info, at, speed, size, sink, EffectiveVolume);
            s.Ended += OnSessionEnded;
            s.Failed += OnSessionFailed;
            session = s;
            try { s.Start(); }
            catch (Exception ex)
            {
                session = null;
                s.Dispose();
                playing = false;
                RaiseFailed(ex.Message);
            }
        }

        void StopSession()
        {
            var s = session;
            session = null;
            if (s != null)
            {
                s.Ended -= OnSessionEnded;
                s.Failed -= OnSessionFailed;
                s.Dispose();
            }
        }

        void OnSessionEnded(PlaybackSession s)
        {
            ui.Post(_ =>
            {
                if (!ReferenceEquals(s, session)) return;
                position = Info != null && Info.Duration > 0 ? Info.Duration : s.Position;
                StopSession();
                playing = false;
                Raise();
                var h = Ended;
                if (h != null) h(this, EventArgs.Empty);
            }, null);
        }

        void OnSessionFailed(PlaybackSession s, string message)
        {
            ui.Post(_ =>
            {
                if (!ReferenceEquals(s, session)) return;
                StopSession();
                playing = false;
                Raise();
                RaiseFailed(message);
            }, null);
        }

        void RaiseFailed(string m)
        {
            var h = Failed;
            if (h != null) h(m);
        }

        void KillGrab()
        {
            lock (grabLock)
            {
                grabGeneration++;
                FFmpeg.KillQuietly(grabProc);
                grabProc = null;
            }
        }

        /// <summary>Decodes a single frame at time t and shows it (used when paused, seeking and scrubbing).</summary>
        void GrabFrame(double t)
        {
            var info = Info;
            if (info == null) return;
            var size = OutputSize();
            int gen;
            Process p;
            lock (grabLock)
            {
                gen = ++grabGeneration;
                FFmpeg.KillQuietly(grabProc);
                grabProc = null;
                string args = FFmpeg.InputArgs(info.Path, t) +
                    "-map 0:v:0 -an -sn -dn -frames:v 1 -vf \"" + VideoQuality.ToBgra(size.Width, size.Height, true, info) + "\" -f rawvideo -pix_fmt bgra pipe:1";
                try { p = FFmpeg.Start(args); }
                catch (Exception ex) { RaiseFailed(ex.Message); return; }
                grabProc = p;
            }
            var proc = p;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var buf = new byte[size.Width * size.Height * 4];
                bool ok = false;
                var err = new StringBuilder();
                try
                {
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                    proc.BeginErrorReadLine();
                    ok = ReadAll(proc.StandardOutput.BaseStream, buf);
                }
                catch { }
                lock (grabLock)
                {
                    if (gen != grabGeneration) return;
                    if (ReferenceEquals(grabProc, proc)) grabProc = null;
                }
                if (ok) sink.Present(buf, size.Width, size.Height);
                else
                {
                    try { proc.WaitForExit(2000); } catch { }
                    string e;
                    lock (err) e = err.ToString();
                    // a seek past the last frame produces nothing: that's fine, keep the current picture
                    if (!string.IsNullOrWhiteSpace(e) && t < 0.05)
                        ui.Post(__ => RaiseFailed(FFmpeg.LastMeaningfulLine(e)), null);
                }
                try { proc.Dispose(); } catch { }
            });
        }

        static bool ReadAll(Stream s, byte[] buf)
        {
            int off = 0;
            while (off < buf.Length)
            {
                int n = s.Read(buf, off, buf.Length - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        public void Dispose()
        {
            StopSession();
            KillGrab();
        }
    }
}
