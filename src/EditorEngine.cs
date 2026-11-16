using System;
using System.Collections.Generic;
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
    // =========================================================================== audio mixing

    /// <summary>One second-order filter (RBJ cookbook forms) with its own state for the left and right channel.</summary>
    internal sealed class Biquad
    {
        double b0, b1, b2, a1, a2;
        double l1, l2, r1, r2;

        Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            this.b0 = b0 / a0; this.b1 = b1 / a0; this.b2 = b2 / a0; this.a1 = a1 / a0; this.a2 = a2 / a0;
        }

        public static Biquad LowShelf(double hz, double db) { return Shelf(hz, db, false); }
        public static Biquad HighShelf(double hz, double db) { return Shelf(hz, db, true); }

        static Biquad Shelf(double hz, double db, bool high)
        {
            double A = Math.Pow(10, db / 40), w = 2 * Math.PI * hz / AudioMixer.Rate, cs = Math.Cos(w), sn = Math.Sin(w);
            double al = sn / 2 * Math.Sqrt(2), q = 2 * Math.Sqrt(A) * al;   // slope 1
            if (!high)
                return new Biquad(A * ((A + 1) - (A - 1) * cs + q), 2 * A * ((A - 1) - (A + 1) * cs), A * ((A + 1) - (A - 1) * cs - q),
                                  (A + 1) + (A - 1) * cs + q, -2 * ((A - 1) + (A + 1) * cs), (A + 1) + (A - 1) * cs - q);
            return new Biquad(A * ((A + 1) + (A - 1) * cs + q), -2 * A * ((A - 1) + (A + 1) * cs), A * ((A + 1) + (A - 1) * cs - q),
                              (A + 1) - (A - 1) * cs + q, 2 * ((A - 1) - (A + 1) * cs), (A + 1) - (A - 1) * cs - q);
        }

        public static Biquad Peak(double hz, double db, double q)
        {
            double A = Math.Pow(10, db / 40), w = 2 * Math.PI * hz / AudioMixer.Rate, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
            return new Biquad(1 + al * A, -2 * cs, 1 - al * A, 1 + al / A, -2 * cs, 1 - al / A);
        }

        public static Biquad HighPass(double hz)
        {
            double w = 2 * Math.PI * hz / AudioMixer.Rate, cs = Math.Cos(w), al = Math.Sin(w) / Math.Sqrt(2);
            return new Biquad((1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + al, -2 * cs, 1 - al);
        }

        public void Reset() { l1 = l2 = r1 = r2 = 0; }

        /// <summary>Filters interleaved stereo in place.</summary>
        public void Run(float[] buf, int frames)
        {
            double x1 = l1, x2 = l2, y1 = r1, y2 = r2;
            for (int i = 0; i < frames; i++)
            {
                double x = buf[i * 2], o = b0 * x + x1;
                x1 = b1 * x - a1 * o + x2; x2 = b2 * x - a2 * o;
                buf[i * 2] = (float)o;
                x = buf[i * 2 + 1]; o = b0 * x + y1;
                y1 = b1 * x - a1 * o + y2; y2 = b2 * x - a2 * o;
                buf[i * 2 + 1] = (float)o;
            }
            // denormals would make the filter crawl once the sound stops
            l1 = Math.Abs(x1) < 1e-20 ? 0 : x1; l2 = Math.Abs(x2) < 1e-20 ? 0 : x2;
            r1 = Math.Abs(y1) < 1e-20 ? 0 : y1; r2 = Math.Abs(y2) < 1e-20 ? 0 : y2;
        }
    }

    /// <summary>
    /// Mixes every sounding clip of a project from the cached PCM files (48 kHz stereo). Volume (and its keyframes),
    /// fades, bass, treble, voice boost and ducking are worked out here as the sound is mixed, so changing them is
    /// instant; noise reduction, pitch and even loudness come from files prepared by MediaLibrary.
    /// Call Mix with rising positions: filters carry their state from one call to the next, and start afresh after a jump.
    /// </summary>
    internal sealed class AudioMixer : IDisposable
    {
        public const int Rate = 48000;

        sealed class Src
        {
            public long Start, End, SrcOffset, FadeIn, FadeOut;
            public double Gain;
            public long[] KeyAt; public double[] KeyGain;   // volume keyframes (frames into the clip), or null
            public Biquad[] Filters;                          // bass, treble and the voice boost's tone, or null
            public bool Compress;                             // the voice boost's compressor
            public double CompEnv;
            public bool Duck; public double DuckDepth;
            public long Next = long.MinValue;                 // the position the next call continues from
            public FileStream Fs;
            public long FileFrames;
        }

        readonly List<Src> sources = new List<Src>();
        byte[] readBuf = new byte[0];
        float[] work = new float[0];
        float[] duckEnv = new float[0];
        bool anyDuck;
        double duckGain; int duckHold; long duckNext = long.MinValue;

        /// <summary>How many clips are silent because their prepared sound wasn't ready (only when not waiting).</summary>
        public int Missing;

        /// <summary>The loudest sample of each channel in the last Mix call (before the preview's own volume), 0..1 and beyond.</summary>
        public float PeakL, PeakR;

        /// <summary>
        /// Builds the mix. With wait, speed-changed or cleaned-up sound that isn't ready yet is made first (export),
        /// so call it off the UI thread; without, such clips stay silent and their sound is started in the background (playback).
        /// </summary>
        public AudioMixer(Project p, Func<int, MediaItem> media, Func<bool> cancelled = null, bool wait = true)
        {
            foreach (var tr in p.Tracks)
            {
                if (tr.Muted) continue;
                foreach (var c in tr.Clips)
                {
                    if (cancelled != null && cancelled()) return;
                    if ((c.Kind != ClipKind.Video && c.Kind != ClipKind.Audio) || !c.Sounds) continue;
                    var m = media(c.MediaId);
                    if (m == null || !m.IsUsable || !m.HasAudio) continue;
                    string chain = c.SoundChain();
                    string pcm = wait ? MediaLibrary.PcmFor(m, c.Speed, chain) : MediaLibrary.PcmIfReady(m, c.Speed, chain, true);
                    if (pcm == null) { Missing++; continue; }
                    try
                    {
                        var fs = new FileStream(pcm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                        var s = new Src
                        {
                            Start = (long)Math.Round(c.Start * Rate),
                            End = (long)Math.Round(c.End * Rate),
                            SrcOffset = (long)Math.Round(c.In / Math.Max(0.01, c.Speed) * Rate),
                            FadeIn = (long)Math.Round(c.AudioFadeIn * Rate),
                            FadeOut = (long)Math.Round(c.AudioFadeOut * Rate),
                            Gain = c.Volume,
                            Duck = c.Duck, DuckDepth = c.DuckDepth,
                            Fs = fs,
                            FileFrames = fs.Length / 4,
                        };
                        if (c.HasLevels)
                        {
                            s.KeyAt = new long[c.Levels.Count]; s.KeyGain = new double[c.Levels.Count];
                            for (int i = 0; i < c.Levels.Count; i++) { s.KeyAt[i] = (long)Math.Round(c.Levels[i].T * Rate); s.KeyGain[i] = Math.Max(0, c.Levels[i].V); }
                        }
                        var f = new List<Biquad>();
                        if (c.VoiceBoost) { f.Add(Biquad.HighPass(90)); f.Add(Biquad.Peak(250, -2.5, 1)); f.Add(Biquad.Peak(3200, 4.5, 0.9)); s.Compress = true; }
                        if (Math.Abs(c.Bass) > 0.5) f.Add(Biquad.LowShelf(120, Math.Max(-100, Math.Min(100, c.Bass)) * 0.12));
                        if (Math.Abs(c.Treble) > 0.5) f.Add(Biquad.HighShelf(4500, Math.Max(-100, Math.Min(100, c.Treble)) * 0.12));
                        if (f.Count > 0) s.Filters = f.ToArray();
                        if (s.Duck) anyDuck = true;
                        sources.Add(s);
                    }
                    catch { }
                }
            }
            // ducking needs something to duck under
            if (anyDuck && !sources.Exists(x => !x.Duck)) anyDuck = false;
        }

        public bool HasSound { get { return sources.Count > 0; } }

        static double KeyGainAt(Src s, long local)
        {
            var at = s.KeyAt;
            if (local <= at[0]) return s.KeyGain[0];
            int n = at.Length;
            if (local >= at[n - 1]) return s.KeyGain[n - 1];
            int i = 1;
            while (at[i] < local) i++;
            double u = (double)(local - at[i - 1]) / Math.Max(1, at[i] - at[i - 1]);
            return s.KeyGain[i - 1] + (s.KeyGain[i] - s.KeyGain[i - 1]) * u;
        }

        /// <summary>Reads, shapes and adds one source into buf. With env, the source is turned down by its ducking depth times env.</summary>
        void Add(Src s, long pos, int frames, float[] buf, float[] env)
        {
            long endPos = pos + frames;
            long a = Math.Max(pos, s.Start), b = Math.Min(endPos, s.End);
            if (b <= a) return;
            long srcFrame = s.SrcOffset + (a - s.Start);
            int count = (int)(b - a);
            if (srcFrame >= s.FileFrames) return;
            int avail = (int)Math.Min(count, s.FileFrames - srcFrame);
            if (readBuf.Length < avail * 4) readBuf = new byte[avail * 4];
            int got = 0;
            try
            {
                s.Fs.Position = srcFrame * 4;
                while (got < avail * 4)
                {
                    int n = s.Fs.Read(readBuf, got, avail * 4 - got);
                    if (n <= 0) break;
                    got += n;
                }
            }
            catch { return; }
            int gotFrames = got / 4;
            if (gotFrames == 0) return;
            if (work.Length < gotFrames * 2) work = new float[gotFrames * 2];
            var w = work;
            for (int i = 0; i < gotFrames; i++)
            {
                w[i * 2] = (short)(readBuf[i * 4] | (readBuf[i * 4 + 1] << 8)) / 32768f;
                w[i * 2 + 1] = (short)(readBuf[i * 4 + 2] | (readBuf[i * 4 + 3] << 8)) / 32768f;
            }
            bool fresh = s.Next != a;
            s.Next = a + gotFrames;
            if (s.Filters != null)
                foreach (var f in s.Filters) { if (fresh) f.Reset(); f.Run(w, gotFrames); }
            if (s.Compress)
            {
                // speech sits around a steady level: peaks above -20 dB are eased to a third, then the whole is lifted
                const double threshold = 0.1, attack = 1 - 0.99740, release = 1 - 0.99986, makeup = 1.7;   // 8 ms, 150 ms, +4.6 dB
                double e = fresh ? 0 : s.CompEnv;
                for (int i = 0; i < gotFrames; i++)
                {
                    double l = w[i * 2], r = w[i * 2 + 1], level = Math.Max(Math.Abs(l), Math.Abs(r));
                    e += (level - e) * (level > e ? attack : release);
                    double g = e > threshold ? Math.Pow(e / threshold, -2.0 / 3) : 1;
                    w[i * 2] = (float)(l * g * makeup); w[i * 2 + 1] = (float)(r * g * makeup);
                }
                s.CompEnv = e < 1e-20 ? 0 : e;
            }
            long clipLen = s.End - s.Start;
            int at = (int)(a - pos);
            for (int i = 0; i < gotFrames; i++)
            {
                long local = a - s.Start + i;
                double gain = s.KeyAt != null ? KeyGainAt(s, local) : s.Gain;
                if (s.FadeIn > 0 && local < s.FadeIn) gain *= (double)local / s.FadeIn;
                long fromEnd = clipLen - local;
                if (s.FadeOut > 0 && fromEnd < s.FadeOut) gain *= Math.Max(0, (double)fromEnd / s.FadeOut);
                if (env != null) gain *= 1 - s.DuckDepth * env[at + i];
                int o = (at + i) * 2;
                buf[o] += (float)(w[i * 2] * gain);
                buf[o + 1] += (float)(w[i * 2 + 1] * gain);
            }
        }

        /// <summary>Writes frames [pos, pos+frames) into buf (interleaved stereo, ±1.0).</summary>
        public void Mix(long pos, int frames, float[] buf)
        {
            Array.Clear(buf, 0, frames * 2);
            foreach (var s in sources) if (!s.Duck || !anyDuck) Add(s, pos, frames, buf, null);
            if (anyDuck)
            {
                // How far ducked clips go down, 0..1, from the loudness of everything else: down quickly when
                // something sounds, held through the short gaps between words, and back up slowly afterwards.
                if (duckEnv.Length < frames) duckEnv = new float[frames];
                const double threshold = 0.012, down = 1 - 0.99965, up = 1 - 0.99996;   // -38 dB; 60 ms; 500 ms
                const int hold = Rate * 35 / 100;
                bool fresh = duckNext != pos;
                double g = duckGain; int h = duckHold;
                for (int i = 0; i < frames; i++)
                {
                    bool loud = Math.Abs(buf[i * 2]) > threshold || Math.Abs(buf[i * 2 + 1]) > threshold;
                    if (fresh) { g = loud ? 1 : 0; h = 0; fresh = false; }
                    if (loud) h = hold; else if (h > 0) h--;
                    double target = h > 0 ? 1 : 0;
                    g += (target - g) * (target > g ? down : up);
                    duckEnv[i] = (float)g;
                }
                duckGain = g < 1e-9 ? 0 : g; duckHold = h; duckNext = pos + frames;
                foreach (var s in sources) if (s.Duck) Add(s, pos, frames, buf, duckEnv);
            }
            float pl = 0, pr = 0;
            for (int i = 0; i < frames; i++)
            {
                float l = Math.Abs(buf[i * 2]), r = Math.Abs(buf[i * 2 + 1]);
                if (l > pl) pl = l;
                if (r > pr) pr = r;
            }
            PeakL = pl; PeakR = pr;
        }

        public static void ToPcm16(float[] buf, int frames, float volume, byte[] dest)
        {
            for (int i = 0; i < frames * 2; i++)
            {
                float v = buf[i] * volume;
                // gentle limiter so overlapping clips don't crackle
                if (v > 1f || v < -1f) v = Math.Sign(v) * (1f - 1f / (1f + Math.Abs(v) * 2f) * 0.66f);
                int s = (int)(v * 32767f);
                if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
                dest[i * 2] = (byte)s;
                dest[i * 2 + 1] = (byte)(s >> 8);
            }
        }

        /// <summary>Writes the whole mix as a 16-bit WAV file.</summary>
        public void WriteWav(string path, double duration, Action<double> progress, Func<bool> cancelled, double from = 0)
        {
            long total = (long)Math.Ceiling(duration * Rate);
            long first = (long)Math.Round(from * Rate);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                long dataBytes = total * 4;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write((uint)(36 + dataBytes));
                w.Write(Encoding.ASCII.GetBytes("WAVE")); w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write((uint)dataBytes);
                const int chunk = Rate / 4;
                var mix = new float[chunk * 2];
                var bytes = new byte[chunk * 4];
                for (long pos = 0; pos < total; pos += chunk)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    int n = (int)Math.Min(chunk, total - pos);
                    Mix(first + pos, n, mix);
                    ToPcm16(mix, n, 1f, bytes);
                    w.Write(bytes, 0, n * 4);
                    if (progress != null) progress((double)pos / total);
                }
            }
        }

        public void Dispose()
        {
            foreach (var s in sources) try { s.Fs.Dispose(); } catch { }
            sources.Clear();
        }
    }

    // =========================================================================== playback

    /// <summary>Plays the timeline: pictures rendered by the compositor follow the sound clock.</summary>
    internal sealed class EditorPlayback : IDisposable
    {
        readonly Project project;
        readonly Dictionary<int, MediaItem> media;
        readonly double start, duration, fps;
        readonly Size size;
        readonly Func<float> volume;
        readonly Stopwatch clock = new Stopwatch();
        readonly ManualResetEvent firstFrame = new ManualResetEvent(false);
        volatile bool cancelled, ended;
        WaveOut wave;
        AudioMixer mixer;
        volatile bool audioClock;
        double lastPosition;

        public event Action<Frame> FrameReady;    // worker thread; the frame is only valid during the call
        public event Action Finished;              // worker thread
        public event Action<string> Problem;       // worker thread

        public EditorPlayback(Project snapshot, Dictionary<int, MediaItem> media, double start, Size size, Func<float> volume)
        {
            project = snapshot;
            this.media = media;
            this.start = Math.Max(0, start);
            duration = snapshot.Duration;
            fps = Math.Max(1, Math.Min(60, snapshot.Fps));
            this.size = size;
            this.volume = volume;
            lastPosition = this.start;
        }

        MediaItem Media(int id) { MediaItem m; return media.TryGetValue(id, out m) ? m : null; }

        public double Position
        {
            get
            {
                if (ended) return duration;
                if (!firstFrame.WaitOne(0)) return start;
                double t;
                var w = wave;
                if (audioClock && w != null) t = start + w.PlayedFrames / (double)AudioMixer.Rate;
                else t = start + clock.Elapsed.TotalSeconds;
                if (t < lastPosition) t = lastPosition; // never step backwards
                lastPosition = t;
                return Math.Min(duration, t);
            }
        }

        /// <summary>Some clips are silent because their prepared sound wasn't ready when playback started.</summary>
        public volatile bool SoundMissing;

        struct Level { public long Until; public float L, R; }
        readonly Queue<Level> levels = new Queue<Level>();   // sound is handed to the device ahead of being heard

        /// <summary>The loudness of the mix at the moment being heard (per channel, 1 = full scale; the preview's own volume isn't counted), for the meter.</summary>
        public void Levels(out float left, out float right)
        {
            left = right = 0;
            var w = wave;
            if (!audioClock || w == null) return;
            long played;
            try { played = w.PlayedFrames; } catch { return; }
            lock (levels)
            {
                while (levels.Count > 1 && levels.Peek().Until <= played) levels.Dequeue();
                if (levels.Count == 0) return;
                var l = levels.Peek();
                left = l.L; right = l.R;
            }
        }

        public void Start()
        {
            new Thread(VideoLoop) { IsBackground = true, Name = "editor video" }.Start();
            new Thread(AudioLoop) { IsBackground = true, Name = "editor audio" }.Start();
        }

        void VideoLoop()
        {
            var comp = new Compositor(project, Media);
            try
            {
                using (var frames = new StreamFrames(fps, false))
                {
                    var bmp = new Frame(size.Width, size.Height);
                    long lastK = -1;
                    // first frame before the clock starts, so the start isn't skipped while decoders spin up
                    long k0 = (long)Math.Floor(start * fps + 1e-6);
                    comp.Render(bmp, Math.Max(start, k0 / fps), frames);
                    if (cancelled) return;
                    Present(bmp);
                    lastK = k0;
                    clock.Start();
                    firstFrame.Set();
                    while (!cancelled)
                    {
                        double t = Position;
                        if (t >= duration - 1e-4)
                        {
                            ended = true;
                            var h = Finished;
                            if (h != null && !cancelled) h();
                            return;
                        }
                        long k = (long)Math.Floor(t * fps + 1e-6);
                        if (k == lastK) { Thread.Sleep(2); continue; }
                        lastK = k;
                        comp.Render(bmp, k / fps, frames);
                        if (cancelled) return;
                        Present(bmp);
                    }
                }
            }
            catch (Exception ex)
            {
                if (cancelled) return;
                var h = Problem;
                if (h != null) h(ex.Message);
            }
            finally
            {
                firstFrame.Set();
                comp.ReleaseWork();
            }
        }

        void Present(Frame b)
        {
            var h = FrameReady;
            if (h != null) h(b);
        }

        void AudioLoop()
        {
            try
            {
                mixer = new AudioMixer(project, Media, () => cancelled, false);
                SoundMissing = mixer.Missing > 0;
                if (cancelled || !mixer.HasSound) return;
                try { wave = new WaveOut(8, 2400); }
                catch { wave = null; return; } // no sound device: the stopwatch keeps time
                firstFrame.WaitOne();
                if (cancelled) return;
                long pos = (long)Math.Round(start * AudioMixer.Rate);
                long startFrame = pos;
                long end = (long)Math.Ceiling(duration * AudioMixer.Rate);
                var w = wave;
                var mix = new float[w.BufferBytes / 2];
                var bytes = new byte[w.BufferBytes];
                int framesPer = w.BufferBytes / WaveOut.BytesPerFrame;
                bool first = true;
                while (!cancelled && pos < end)
                {
                    int idx = w.FreeBuffer();
                    if (idx < 0) { Thread.Sleep(5); continue; }
                    int n = (int)Math.Min(framesPer, end - pos);
                    mixer.Mix(pos, n, mix);
                    lock (levels)
                    {
                        levels.Enqueue(new Level { Until = pos + n - startFrame, L = mixer.PeakL, R = mixer.PeakR });
                        while (levels.Count > 64) levels.Dequeue();
                    }
                    AudioMixer.ToPcm16(mix, n, volume(), bytes);
                    w.Write(idx, bytes, n * 4);
                    if (first) { first = false; audioClock = true; }
                    pos += n;
                }
                while (!cancelled && !w.AllDone) Thread.Sleep(10);
            }
            catch { }
            finally
            {
                var w = wave;
                audioClock = false;
                wave = null;
                if (w != null) try { w.Dispose(); } catch { }
                if (mixer != null) mixer.Dispose();
            }
        }

        public void Dispose()
        {
            cancelled = true;
            firstFrame.Set();
            var w = wave;
            if (w != null) try { w.Pause(); } catch { }
        }
    }

    // =========================================================================== paused preview

    /// <summary>Renders single preview frames on a worker thread; only the newest request matters.</summary>
    internal sealed class StillRenderer : IDisposable
    {
        sealed class Req { public Project P; public Dictionary<int, MediaItem> Media; public double T; public Size Size; public int Gen; public bool Live; }
        readonly Stopwatch busy = new Stopwatch();

        readonly object gate = new object();
        readonly StillFrames frames = new StillFrames();
        Req pending;
        volatile int latest;
        volatile bool disposed;
        public event Action<int, Bitmap> Rendered; // worker thread; the handler takes ownership of the bitmap

        public StillRenderer()
        {
            frames.IsCancelled = () => false;
            new Thread(Loop) { IsBackground = true, Name = "preview renderer" }.Start();
        }

        /// <summary>Asks for a frame; live = scrubbing (fast scaling, frames read ahead). Only the newest request is rendered.</summary>
        public int Request(Project p, Dictionary<int, MediaItem> media, double t, Size size, bool live = false)
        {
            lock (gate)
            {
                int gen = ++latest;
                pending = new Req { P = p, Media = media, T = t, Size = size, Gen = gen, Live = live };
                Monitor.PulseAll(gate);
                return gen;
            }
        }

        /// <summary>Stops an in-flight decode when the request it serves is already out of date.</summary>
        public void CancelStale() { frames.Cancel(); }

        /// <summary>
        /// Stops the current decode only if it has been running longer than ms. A grab normally takes well
        /// under that; killing every one as soon as the pointer moved on meant nothing ever finished while
        /// the playhead was being dragged.
        /// </summary>
        public void CancelIfSlow(int ms)
        {
            lock (gate) { if (!busy.IsRunning || busy.ElapsedMilliseconds < ms) return; }
            frames.Cancel();
        }

        void Loop()
        {
            while (!disposed)
            {
                Req r;
                lock (gate)
                {
                    while (pending == null && !disposed) Monitor.Wait(gate);
                    if (disposed) return;
                    r = pending;
                    pending = null;
                }
                int gen = r.Gen;
                frames.IsCancelled = () => latest != gen;
                frames.Live = r.Live;
                lock (gate) busy.Restart();
                Bitmap bmp = null;
                try
                {
                    var comp = new Compositor(r.P, id => { MediaItem m; return r.Media.TryGetValue(id, out m) ? m : null; }) { HighQuality = !r.Live };
                    var canvas = new Frame(Math.Max(2, r.Size.Width), Math.Max(2, r.Size.Height));
                    comp.Render(canvas, r.T, frames);
                    comp.ReleaseWork();
                    if (latest == gen) bmp = canvas.ToBitmap();
                }
                catch
                {
                    if (bmp != null) { bmp.Dispose(); bmp = null; }
                }
                lock (gate) busy.Reset();
                if (bmp == null) continue;
                if (latest != gen) { bmp.Dispose(); continue; }
                var h = Rendered;
                if (h != null) h(gen, bmp); else bmp.Dispose();
            }
        }

        public void Dispose()
        {
            disposed = true;
            lock (gate) Monitor.PulseAll(gate);
            frames.Cancel();
        }
    }

    // =========================================================================== export

    internal sealed class ExportSettings
    {
        public string OutPath;
        public int Width, Height;
        public double Fps = 30;
        public int Crf = 18;
        public string Preset = "medium";
        public bool AudioOnly;
        public int AudioKbps = 192;
        /// <summary>mp4 (H.264), hevc, webm, gif, mp3, m4a or wav. See Encoders.</summary>
        public string Format = "mp4";
        /// <summary>Try the graphics card's encoder first (it falls back to the processor if that fails).</summary>
        public bool Hardware;
        /// <summary>The part of the timeline to export, in seconds. To at or before From means all of it.</summary>
        public double From, To;
    }

    /// <summary>
    /// Renders the project with the compositor and encodes it with FFmpeg: H.264, HEVC or VP9 video (optionally on
    /// the graphics card, with the processor as the fallback), an animated GIF, or the mixed sound alone.
    /// </summary>
    internal sealed class ExportJob
    {
        readonly Project project;
        readonly Dictionary<int, MediaItem> media;
        readonly ExportSettings settings;
        volatile bool cancelled;
        Process proc;
        public event Action<double, string> Progress;   // worker thread: 0..1, stage text
        public event Action<Exception> Completed;       // worker thread: null = success

        public ExportJob(Project snapshot, Dictionary<int, MediaItem> media, ExportSettings s)
        {
            project = snapshot;
            this.media = media;
            settings = s;
        }

        public bool Cancelled { get { return cancelled; } }

        public void Start() { new Thread(Run) { IsBackground = true, Name = "export" }.Start(); }

        public void Cancel()
        {
            cancelled = true;
            FFmpeg.KillQuietly(proc);
        }

        MediaItem Media(int id) { MediaItem m; return media.TryGetValue(id, out m) ? m : null; }

        void Report(double p, string stage) { var h = Progress; if (h != null) h(Math.Max(0, Math.Min(1, p)), stage); }

        void Run()
        {
            Exception error = null;
            string wav = Path.Combine(MediaLibrary.Folder, "export-" + Guid.NewGuid().ToString("N") + ".wav");
            var err = new StringBuilder();
            try
            {
                if (project.Duration <= 0.01) throw new InvalidOperationException("The timeline is empty.");
                string format = settings.AudioOnly && !Encoders.IsAudio(settings.Format) ? "mp3" : (settings.Format ?? "mp4");
                bool ranged = settings.To > settings.From + 0.01;
                from = ranged ? Math.Max(0, Math.Min(project.Duration, settings.From)) : 0;
                double duration = (ranged ? Math.Min(project.Duration, settings.To) : project.Duration) - from;
                if (duration <= 0.01) throw new InvalidOperationException("The marked range is empty.");
                bool audioOnly = Encoders.IsAudio(format);
                bool hasSound = false;
                if (format != "gif")
                {
                    Report(0, "Preparing sound…");
                    using (var mixer = new AudioMixer(project, Media, () => cancelled))
                    {
                        if (cancelled) throw new OperationCanceledException();
                        hasSound = mixer.HasSound;
                        if (hasSound || audioOnly)
                            mixer.WriteWav(wav, duration, p => Report(p * (audioOnly ? 0.5 : 0.08), "Mixing sound…"), () => cancelled, from);
                    }
                }
                if (audioOnly) ExportAudio(format, wav, err);
                else if (format == "gif") ExportGif(duration, err);
                else ExportVideo(format, wav, hasSound, duration, err);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                FFmpeg.KillQuietly(proc);
                ImageCache.Delete(wav);
            }
            if (cancelled) error = new OperationCanceledException();
            if (error != null)
            {
                try { if (File.Exists(settings.OutPath)) File.Delete(settings.OutPath); } catch { }
                string detail;
                lock (err) detail = FFmpeg.LastMeaningfulLine(err.ToString());
                if (!(error is OperationCanceledException) && !string.IsNullOrEmpty(detail) && error is IOException)
                    error = new IOException(detail, error);
            }
            var h = Completed;
            if (h != null) h(error);
        }

        Process StartEncoder(string args, StringBuilder err)
        {
            var psi = new ProcessStartInfo(FFmpeg.Locate(), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var p = Process.Start(psi);
            FFmpeg.AddToJob(p);
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) { if (err.Length < 8000) err.AppendLine(e.Data); } };
            p.BeginErrorReadLine();
            p.OutputDataReceived += (s, e) => { };
            p.BeginOutputReadLine();
            return p;
        }

        double from;   // where on the timeline the export starts

        void ExportAudio(string format, string wav, StringBuilder err)
        {
            if (format == "wav")
            {
                // the mix is already a WAV file
                Report(0.9, "Writing the file…");
                File.Copy(wav, settings.OutPath, true);
                Report(1, "Done");
                return;
            }
            string codec = format == "m4a" ? "-c:a aac -b:a " + settings.AudioKbps + "k -movflags +faststart" : "-c:a libmp3lame -b:a " + settings.AudioKbps + "k";
            string args = "-hide_banner -loglevel error -protocol_whitelist file -i " + FFmpeg.Quote(wav) + " " + codec + " -y " + FFmpeg.Quote(settings.OutPath);
            proc = StartEncoder(args, err);
            proc.StandardInput.Close();
            while (!proc.WaitForExit(200))
            {
                if (cancelled) throw new OperationCanceledException();
                Report(0.75, "Encoding the sound…");
            }
            if (proc.ExitCode != 0) throw new IOException("FFmpeg couldn't write the file.");
            Report(1, "Done");
        }

        void ExportVideo(string format, string wav, bool hasSound, double duration, StringBuilder err)
        {
            int w = settings.Width & ~1, h = settings.Height & ~1;
            double fps = settings.Fps;
            var inv = CultureInfo.InvariantCulture;
            string hardware = settings.Hardware ? Encoders.Detect(format) : null;
            while (true)
            {
                var sb = new StringBuilder("-hide_banner -loglevel error ");
                sb.AppendFormat(inv, "-f rawvideo -pix_fmt bgra -s {0}x{1} -framerate {2:0.###} -protocol_whitelist pipe -i pipe:0 ", w, h, fps);
                if (hasSound) sb.Append("-protocol_whitelist file -i ").Append(FFmpeg.Quote(wav)).Append(' ');
                sb.Append("-map 0:v:0 ");
                if (hasSound) sb.Append("-map 1:a:0 ");
                sb.Append(Encoders.VideoArgs(format, hardware, settings.Crf, settings.Preset));
                if (hasSound) sb.Append(Encoders.AudioArgs(format, settings.AudioKbps));
                if (format != "webm") sb.Append("-movflags +faststart ");
                sb.AppendFormat(inv, "-t {0:0.###} -y ", duration);
                sb.Append(FFmpeg.Quote(settings.OutPath));
                lock (err) err.Length = 0;
                proc = StartEncoder(sb.ToString(), err);
                try
                {
                    RenderInto(proc, w, h, fps, duration, 0.08, 0.9, "Rendering frame");
                    Report(0.99, "Finishing the file…");
                    while (!proc.WaitForExit(200)) if (cancelled) throw new OperationCanceledException();
                    if (proc.ExitCode != 0) throw new IOException("FFmpeg couldn't write the video.");
                    break;
                }
                catch (Exception ex)
                {
                    // The graphics card's encoder can refuse a size, run out of sessions or lose its driver part-way.
                    // The processor's encoder always works, so the export starts again with it.
                    if (hardware == null || cancelled || ex is OperationCanceledException) throw;
                    FFmpeg.KillQuietly(proc);
                    UsedFallback = true;
                    hardware = null;
                    Report(0.08, "The graphics card couldn't encode this – starting again on the processor…");
                }
            }
            UsedHardware = hardware;
            Report(1, "Done");
        }

        /// <summary>The hardware encoder the finished export used ("nvenc", "qsv", "amf"), or null for the processor.</summary>
        public string UsedHardware;
        /// <summary>The graphics card was tried and failed, and the processor finished the job.</summary>
        public bool UsedFallback;

        /// <summary>
        /// An animated GIF needs its own 256 colours: the frames are rendered once to choose them and once more to
        /// write the file with them. Two passes keep the memory flat however long the GIF is.
        /// </summary>
        void ExportGif(double duration, StringBuilder err)
        {
            int w = Math.Max(2, settings.Width), h = Math.Max(2, settings.Height);
            double fps = Math.Max(1, Math.Min(50, settings.Fps));
            var inv = CultureInfo.InvariantCulture;
            string palette = Path.Combine(MediaLibrary.Folder, "export-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                string input = string.Format(inv, "-hide_banner -loglevel error -f rawvideo -pix_fmt bgra -s {0}x{1} -framerate {2:0.###} -protocol_whitelist pipe -i pipe:0 ", w, h, fps);
                proc = StartEncoder(input + "-vf palettegen=stats_mode=full -frames:v 1 -update 1 -y " + FFmpeg.Quote(palette), err);
                RenderInto(proc, w, h, fps, duration, 0, 0.45, "Choosing colours, frame");
                while (!proc.WaitForExit(200)) if (cancelled) throw new OperationCanceledException();
                if (proc.ExitCode != 0 || !File.Exists(palette)) throw new IOException("FFmpeg couldn't work out the GIF's colours.");
                lock (err) err.Length = 0;
                proc = StartEncoder(input + "-protocol_whitelist file -i " + FFmpeg.Quote(palette) +
                    " -lavfi \"[0:v][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle\" -loop 0 -y " + FFmpeg.Quote(settings.OutPath), err);
                RenderInto(proc, w, h, fps, duration, 0.45, 0.54, "Writing frame");
                Report(0.99, "Finishing the file…");
                while (!proc.WaitForExit(200)) if (cancelled) throw new OperationCanceledException();
                if (proc.ExitCode != 0) throw new IOException("FFmpeg couldn't write the GIF.");
                Report(1, "Done");
            }
            finally { ImageCache.Delete(palette); }
        }

        /// <summary>Composes every frame of the exported part and pipes it to the encoder, then closes the pipe.</summary>
        void RenderInto(Process encoder, int w, int h, double fps, double duration, double progressFrom, double progressSpan, string label)
        {
            var stdin = encoder.StandardInput.BaseStream;
            long total = Math.Max(1, (long)Math.Ceiling(duration * fps - 1e-6));
            var comp = new Compositor(project, Media) { HighQuality = true };
            var sw = Stopwatch.StartNew();
            // Frames go to the encoder from a second thread with two buffers, so the next frame is composed while
            // the previous one is still being written into FFmpeg's pipe (the write blocks whenever the encoder is behind).
            var writer = new PipeWriter(stdin, w * h * 4, () => cancelled);
            try
            {
                using (var frames = new StreamFrames(fps, true))
                {
                    for (long i = 0; i < total; i++)
                    {
                        if (cancelled) throw new OperationCanceledException();
                        double t = from + i / fps;
                        // the picture is composed straight into the buffer that goes to the encoder
                        var buf = writer.TakeFree();
                        comp.Render(new Frame(w, h, buf), t, frames);
                        writer.Send(buf);
                        if (i % 5 == 0 || i == total - 1)
                        {
                            double done = (i + 1.0) / total;
                            string eta = "";
                            if (sw.Elapsed.TotalSeconds > 2 && done > 0.01)
                            {
                                double left = sw.Elapsed.TotalSeconds / done * (1 - done);
                                eta = " – about " + (left >= 90 ? Math.Ceiling(left / 60) + " min" : Math.Max(1, Math.Ceiling(left)) + " s") + " left";
                            }
                            Report(progressFrom + progressSpan * done, label + " " + (i + 1) + " of " + total + eta);
                        }
                    }
                }
            }
            finally { comp.ReleaseWork(); writer.Finish(); }
            stdin.Close();
        }

        /// <summary>Writes frame buffers to a stream on its own thread; two buffers alternate between composing and writing.</summary>
        sealed class PipeWriter
        {
            readonly Stream output;
            readonly Func<bool> cancelled;
            readonly Queue<byte[]> free = new Queue<byte[]>();
            readonly Queue<byte[]> full = new Queue<byte[]>();
            readonly object sync = new object();
            readonly Thread thread;
            bool done;
            Exception error;

            public PipeWriter(Stream output, int frameBytes, Func<bool> cancelled)
            {
                this.output = output;
                this.cancelled = cancelled;
                free.Enqueue(new byte[frameBytes]);
                free.Enqueue(new byte[frameBytes]);
                thread = new Thread(Run) { IsBackground = true, Name = "export writer" };
                thread.Start();
            }

            public byte[] TakeFree()
            {
                lock (sync)
                {
                    while (free.Count == 0 && error == null) Monitor.Wait(sync, 200);
                    if (error != null) throw Wrap(error);
                    return free.Dequeue();
                }
            }

            public void Send(byte[] buf)
            {
                lock (sync)
                {
                    if (error != null) throw Wrap(error);
                    full.Enqueue(buf);
                    Monitor.PulseAll(sync);
                }
            }

            Exception Wrap(Exception e)
            {
                if (cancelled()) return new OperationCanceledException();
                return new IOException("The encoder stopped unexpectedly.", e);
            }

            /// <summary>Waits until every frame handed over has been written; throws if the encoder failed.</summary>
            public void Finish()
            {
                lock (sync) { done = true; Monitor.PulseAll(sync); }
                thread.Join();
                if (error != null && !cancelled()) throw Wrap(error);
                if (cancelled()) throw new OperationCanceledException();
            }

            void Run()
            {
                try
                {
                    while (true)
                    {
                        byte[] buf;
                        lock (sync)
                        {
                            while (full.Count == 0 && !done) Monitor.Wait(sync);
                            if (full.Count == 0) return;
                            buf = full.Dequeue();
                        }
                        output.Write(buf, 0, buf.Length);
                        lock (sync) { free.Enqueue(buf); Monitor.PulseAll(sync); }
                    }
                }
                catch (Exception ex) { lock (sync) { error = ex; Monitor.PulseAll(sync); } }
            }
        }

    }
}
