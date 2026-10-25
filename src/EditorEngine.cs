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

    /// <summary>Mixes every sounding clip of a project from the cached PCM files (48 kHz stereo).</summary>
    internal sealed class AudioMixer : IDisposable
    {
        public const int Rate = 48000;

        sealed class Src
        {
            public long Start, End, SrcOffset, FadeIn, FadeOut;
            public double Gain;
            public FileStream Fs;
            public long FileFrames;
        }

        readonly List<Src> sources = new List<Src>();
        byte[] readBuf = new byte[0];

        /// <summary>Builds the mix. May decode tempo-changed sound first, so call it off the UI thread.</summary>
        public AudioMixer(Project p, Func<int, MediaItem> media, Func<bool> cancelled = null)
        {
            foreach (var tr in p.Tracks)
            {
                if (tr.Muted) continue;
                foreach (var c in tr.Clips)
                {
                    if (cancelled != null && cancelled()) return;
                    if ((c.Kind != ClipKind.Video && c.Kind != ClipKind.Audio) || c.Muted || c.Volume <= 0.0001) continue;
                    var m = media(c.MediaId);
                    if (m == null || !m.IsUsable || !m.HasAudio) continue;
                    string pcm = MediaLibrary.PcmFor(m, c.Speed);
                    if (pcm == null) continue;
                    try
                    {
                        var fs = new FileStream(pcm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                        sources.Add(new Src
                        {
                            Start = (long)Math.Round(c.Start * Rate),
                            End = (long)Math.Round(c.End * Rate),
                            SrcOffset = (long)Math.Round(c.In / Math.Max(0.01, c.Speed) * Rate),
                            FadeIn = (long)Math.Round(c.AudioFadeIn * Rate),
                            FadeOut = (long)Math.Round(c.AudioFadeOut * Rate),
                            Gain = c.Volume,
                            Fs = fs,
                            FileFrames = fs.Length / 4,
                        });
                    }
                    catch { }
                }
            }
        }

        public bool HasSound { get { return sources.Count > 0; } }

        /// <summary>Adds frames [pos, pos+frames) into buf (interleaved stereo, ±1.0).</summary>
        public void Mix(long pos, int frames, float[] buf)
        {
            Array.Clear(buf, 0, frames * 2);
            long endPos = pos + frames;
            foreach (var s in sources)
            {
                long a = Math.Max(pos, s.Start), b = Math.Min(endPos, s.End);
                if (b <= a) continue;
                long srcFrame = s.SrcOffset + (a - s.Start);
                int count = (int)(b - a);
                if (srcFrame >= s.FileFrames) continue;
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
                catch { continue; }
                int gotFrames = got / 4;
                long clipLen = s.End - s.Start;
                for (int i = 0; i < gotFrames; i++)
                {
                    long local = a - s.Start + i;
                    double gain = s.Gain;
                    if (s.FadeIn > 0 && local < s.FadeIn) gain *= (double)local / s.FadeIn;
                    long fromEnd = clipLen - local;
                    if (s.FadeOut > 0 && fromEnd < s.FadeOut) gain *= Math.Max(0, (double)fromEnd / s.FadeOut);
                    int o = (int)(a - pos + i) * 2;
                    int l = (short)(readBuf[i * 4] | (readBuf[i * 4 + 1] << 8));
                    int r = (short)(readBuf[i * 4 + 2] | (readBuf[i * 4 + 3] << 8));
                    buf[o] += (float)(l * gain / 32768.0);
                    buf[o + 1] += (float)(r * gain / 32768.0);
                }
            }
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
        public void WriteWav(string path, double duration, Action<double> progress, Func<bool> cancelled)
        {
            long total = (long)Math.Ceiling(duration * Rate);
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
                    Mix(pos, n, mix);
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

        public event Action<Bitmap> FrameReady;   // worker thread; the bitmap is only valid during the call
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
                using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    long lastK = -1;
                    // first frame before the clock starts, so the start isn't skipped while decoders spin up
                    long k0 = (long)Math.Floor(start * fps + 1e-6);
                    comp.Render(g, size.Width, size.Height, Math.Max(start, k0 / fps), frames);
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
                        comp.Render(g, size.Width, size.Height, k / fps, frames);
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

        void Present(Bitmap b)
        {
            var h = FrameReady;
            if (h != null) h(b);
        }

        void AudioLoop()
        {
            try
            {
                mixer = new AudioMixer(project, Media, () => cancelled);
                if (cancelled || !mixer.HasSound) return;
                try { wave = new WaveOut(8, 2400); }
                catch { wave = null; return; } // no sound device: the stopwatch keeps time
                firstFrame.WaitOne();
                if (cancelled) return;
                long pos = (long)Math.Round(start * AudioMixer.Rate);
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
        sealed class Req { public Project P; public Dictionary<int, MediaItem> Media; public double T; public Size Size; public int Gen; }

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

        public int Request(Project p, Dictionary<int, MediaItem> media, double t, Size size)
        {
            lock (gate)
            {
                int gen = ++latest;
                pending = new Req { P = p, Media = media, T = t, Size = size, Gen = gen };
                Monitor.PulseAll(gate);
                return gen;
            }
        }

        /// <summary>Stops an in-flight decode when the request it serves is already out of date.</summary>
        public void CancelStale() { frames.Cancel(); }

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
                Bitmap bmp = null;
                try
                {
                    var comp = new Compositor(r.P, id => { MediaItem m; return r.Media.TryGetValue(id, out m) ? m : null; }) { HighQuality = true };
                    bmp = new Bitmap(Math.Max(2, r.Size.Width), Math.Max(2, r.Size.Height), PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(bmp)) comp.Render(g, bmp.Width, bmp.Height, r.T, frames);
                    comp.ReleaseWork();
                }
                catch
                {
                    if (bmp != null) { bmp.Dispose(); bmp = null; }
                }
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
    }

    /// <summary>Renders the project with the compositor and encodes it with FFmpeg (H.264 + AAC in MP4, or MP3).</summary>
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
                double duration = project.Duration;
                if (duration <= 0.01) throw new InvalidOperationException("The timeline is empty.");
                Report(0, "Preparing sound…");
                bool hasSound;
                using (var mixer = new AudioMixer(project, Media, () => cancelled))
                {
                    if (cancelled) throw new OperationCanceledException();
                    hasSound = mixer.HasSound;
                    if (hasSound || settings.AudioOnly)
                        mixer.WriteWav(wav, duration, p => Report(p * (settings.AudioOnly ? 0.5 : 0.08), "Mixing sound…"), () => cancelled);
                }
                if (settings.AudioOnly) ExportAudio(wav, duration, err);
                else ExportVideo(wav, hasSound, duration, err);
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

        void ExportAudio(string wav, double duration, StringBuilder err)
        {
            string args = "-hide_banner -loglevel error -protocol_whitelist file -i " + FFmpeg.Quote(wav) +
                          " -c:a libmp3lame -b:a " + settings.AudioKbps + "k -y " + FFmpeg.Quote(settings.OutPath);
            proc = StartEncoder(args, err);
            proc.StandardInput.Close();
            while (!proc.WaitForExit(200))
            {
                if (cancelled) throw new OperationCanceledException();
                Report(0.75, "Encoding MP3…");
            }
            if (proc.ExitCode != 0) throw new IOException("FFmpeg couldn't write the file.");
            Report(1, "Done");
        }

        void ExportVideo(string wav, bool hasSound, double duration, StringBuilder err)
        {
            int w = settings.Width & ~1, h = settings.Height & ~1;
            double fps = settings.Fps;
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("-hide_banner -loglevel error ");
            sb.AppendFormat(inv, "-f rawvideo -pix_fmt bgra -s {0}x{1} -framerate {2:0.###} -protocol_whitelist pipe -i pipe:0 ", w, h, fps);
            if (hasSound) sb.Append("-protocol_whitelist file -i ").Append(FFmpeg.Quote(wav)).Append(' ');
            sb.Append("-map 0:v:0 ");
            if (hasSound) sb.Append("-map 1:a:0 ");
            sb.Append(VideoQuality.H264FromRgb(settings.Crf, settings.Preset));
            if (hasSound) sb.AppendFormat(inv, "-c:a aac -b:a {0}k ", settings.AudioKbps);
            sb.AppendFormat(inv, "-movflags +faststart -t {0:0.###} -y ", duration);
            sb.Append(FFmpeg.Quote(settings.OutPath));
            proc = StartEncoder(sb.ToString(), err);
            var stdin = proc.StandardInput.BaseStream;

            long total = Math.Max(1, (long)Math.Ceiling(duration * fps - 1e-6));
            var comp = new Compositor(project, Media) { HighQuality = true };
            var buf = new byte[w * h * 4];
            var sw = Stopwatch.StartNew();
            try
            {
                using (var frames = new StreamFrames(fps, true))
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    for (long i = 0; i < total; i++)
                    {
                        if (cancelled) throw new OperationCanceledException();
                        double t = i / fps;
                        comp.Render(g, w, h, t, frames);
                        g.Flush();
                        CopyOut(bmp, buf);
                        try { stdin.Write(buf, 0, buf.Length); }
                        catch (IOException) { if (cancelled) throw new OperationCanceledException(); throw new IOException("The encoder stopped unexpectedly."); }
                        if (i % 5 == 0 || i == total - 1)
                        {
                            double done = (i + 1.0) / total;
                            string eta = "";
                            if (sw.Elapsed.TotalSeconds > 2 && done > 0.01)
                            {
                                double left = sw.Elapsed.TotalSeconds / done * (1 - done);
                                eta = " – about " + (left >= 90 ? Math.Ceiling(left / 60) + " min" : Math.Max(1, Math.Ceiling(left)) + " s") + " left";
                            }
                            Report(0.08 + 0.9 * done, "Rendering frame " + (i + 1) + " of " + total + eta);
                        }
                    }
                }
            }
            finally { comp.ReleaseWork(); }
            stdin.Close();
            Report(0.99, "Finishing the file…");
            while (!proc.WaitForExit(200)) if (cancelled) throw new OperationCanceledException();
            if (proc.ExitCode != 0) throw new IOException("FFmpeg couldn't write the video.");
            Report(1, "Done");
        }

        static void CopyOut(Bitmap b, byte[] dest)
        {
            int w = b.Width, h = b.Height;
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                if (d.Stride == w * 4) Marshal.Copy(d.Scan0, dest, 0, w * h * 4);
                else for (int y = 0; y < h; y++) Marshal.Copy(d.Scan0 + y * d.Stride, dest, y * w * 4, w * 4);
            }
            finally { b.UnlockBits(d); }
        }
    }
}
