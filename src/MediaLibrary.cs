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
using System.Threading;

namespace StillsAndReels
{
    internal enum MediaKind { Video, Audio, Image }
    internal enum MediaStatus { Probing, Copying, Ready, Missing, Failed }

    /// <summary>One file in the video editor's media bin.</summary>
    internal sealed class MediaItem
    {
        public int Id;
        public string OriginalPath;
        public string CachePath;          // working copy (null until copied)
        public bool Temporary;            // original lives in the cache too (e.g. a screen recording not yet saved)
        public string Name;
        public MediaKind Kind;
        public MediaStatus Status = MediaStatus.Probing;
        public string Error;
        public MediaInfo Info;
        public double CopyProgress;       // 0..1
        public long Bytes;
        public Bitmap Thumb;              // tile picture
        public readonly List<KeyValuePair<double, Bitmap>> Strip = new List<KeyValuePair<double, Bitmap>>(); // timeline thumbnails
        public float[] Peaks;             // audio level, 100 per second
        public string PcmPath;            // 48 kHz stereo s16le of the whole file
        public bool PcmFailed;
        public readonly Dictionary<string, string> TempoPcm = new Dictionary<string, string>(); // speed -> pcm file
        public Bitmap Picture;            // decoded image (images only, at most 3840 px)
        public readonly object Sync = new object();
        public volatile bool Removed;     // set when taken out of the bin (background work stops)

        /// <summary>The file to read: the cached copy once it exists, otherwise the original (read-only).</summary>
        public string SourcePath
        {
            get
            {
                if (CachePath != null && Status != MediaStatus.Copying) return CachePath;
                return OriginalPath;
            }
        }

        public bool IsUsable { get { return Status == MediaStatus.Ready || Status == MediaStatus.Copying; } }
        public double Duration { get { return Kind == MediaKind.Image ? 0 : (Info != null ? Info.Duration : 0); } }
        public bool HasAudio { get { return Kind != MediaKind.Image && Info != null && Info.HasAudio; } }
        public bool HasVideo { get { return Kind == MediaKind.Video; } }

        public Size PixelSize
        {
            get
            {
                if (Kind == MediaKind.Image)
                {
                    var p = Picture;
                    if (p != null) return p.Size;
                    return Info != null ? new Size(Info.Width, Info.Height) : new Size(1920, 1080);
                }
                if (Info != null && Info.Width > 0) return new Size(Info.Width, Info.Height);
                return new Size(1920, 1080);
            }
        }

        public Bitmap StripAt(double t)
        {
            lock (Sync)
            {
                if (Strip.Count == 0) return Thumb;
                Bitmap best = Strip[0].Value;
                foreach (var kv in Strip) { if (kv.Key <= t + 0.01) best = kv.Value; else break; }
                return best;
            }
        }
    }

    /// <summary>
    /// The media bin. Every file is copied into the app's private cache folder in the background and the copy
    /// is what the editor reads. Copies are deleted when an item is removed and the whole cache is removed
    /// when the app closes. Originals are only ever read.
    /// </summary>
    internal sealed class MediaLibrary : IDisposable
    {
        readonly List<MediaItem> items = new List<MediaItem>();
        readonly SynchronizationContext ui;
        readonly object queueLock = new object();
        readonly Queue<Action> work = new Queue<Action>();
        readonly Queue<Action> copyWork = new Queue<Action>();
        readonly Thread worker, copier;
        volatile bool disposed;
        int nextId = 1;

        public event Action<MediaItem> ItemChanged;   // raised on the UI thread
        public event EventHandler ListChanged;        // raised on the UI thread

        public static readonly string[] AudioExtensions = { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff" };

        public MediaLibrary()
        {
            ui = SynchronizationContext.Current ?? new SynchronizationContext();
            worker = new Thread(() => Run(work)) { IsBackground = true, Name = "media analysis", Priority = ThreadPriority.BelowNormal };
            copier = new Thread(() => Run(copyWork)) { IsBackground = true, Name = "media copy", Priority = ThreadPriority.BelowNormal };
            worker.Start();
            copier.Start();
        }

        public static string Folder
        {
            get
            {
                string d = Path.Combine(ImageCache.Folder ?? Path.GetTempPath(), "media");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public IList<MediaItem> Items { get { return items.AsReadOnly(); } }

        public MediaItem Find(int id) { return items.Find(m => m.Id == id); }

        public MediaItem FindByPath(string path)
        {
            foreach (var m in items) if (SamePath(m.OriginalPath, path)) return m;
            return null;
        }

        public static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        public static bool IsAudioFile(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return Array.IndexOf(AudioExtensions, ext) >= 0;
        }

        public static bool CanImport(string path)
        {
            var k = FileTypes.Classify(path);
            return k == FileKind.Image || k == FileKind.Video || IsAudioFile(path);
        }

        public long CacheBytes
        {
            get
            {
                long n = 0;
                foreach (var m in items) if (m.CachePath != null) n += m.Bytes;
                return n;
            }
        }

        // ------------------------------------------------------------------ adding and removing

        /// <summary>Adds a file (or returns the existing item for it). The copy and analysis run in the background.</summary>
        public MediaItem Add(string path, int id = 0, bool temporary = false)
        {
            var existing = FindByPath(path);
            if (existing != null) return existing;
            var m = new MediaItem
            {
                Id = id > 0 ? id : nextId,
                OriginalPath = path,
                Name = Path.GetFileName(path),
                Temporary = temporary,
            };
            nextId = Math.Max(nextId, m.Id) + 1;
            if (FileTypes.Classify(path) == FileKind.Image) m.Kind = MediaKind.Image;
            else if (IsAudioFile(path)) m.Kind = MediaKind.Audio;
            else m.Kind = MediaKind.Video;
            items.Add(m);
            RaiseList();

            if (!File.Exists(path))
            {
                m.Status = MediaStatus.Missing;
                m.Error = "The file can't be found. Right-click it to locate it.";
                RaiseItem(m);
                return m;
            }
            Enqueue(work, () => Analyse(m));
            return m;
        }

        public void EnsureIdsAbove(int id) { nextId = Math.Max(nextId, id + 1); }

        public void Remove(MediaItem m)
        {
            if (!items.Remove(m)) return;
            m.Removed = true;
            RaiseList();
            Enqueue(work, () => DeleteFiles(m));
        }

        public void Clear()
        {
            var all = items.ToArray();
            foreach (var m in all) m.Removed = true;
            items.Clear();
            RaiseList();
            foreach (var m in all) { var mm = m; Enqueue(work, () => DeleteFiles(mm)); }
        }

        /// <summary>Points a missing item at a new file and re-analyses it.</summary>
        public void Relink(MediaItem m, string path)
        {
            DeleteFiles(m);
            m.Removed = false;
            m.OriginalPath = path;
            m.Name = Path.GetFileName(path);
            m.Status = MediaStatus.Probing;
            m.Error = null;
            m.CachePath = null;
            m.PcmPath = null;
            m.PcmFailed = false;
            RaiseItem(m);
            Enqueue(work, () => Analyse(m));
        }

        void DeleteFiles(MediaItem m)
        {
            lock (m.Sync)
            {
                if (m.CachePath != null && !SamePath(m.CachePath, m.OriginalPath)) ImageCache.Delete(m.CachePath);
                if (m.Temporary) ImageCache.Delete(m.OriginalPath);
                if (m.PcmPath != null) ImageCache.Delete(m.PcmPath);
                foreach (var p in m.TempoPcm.Values) ImageCache.Delete(p);
                m.TempoPcm.Clear();
                if (m.Thumb != null) { m.Thumb.Dispose(); m.Thumb = null; }
                foreach (var kv in m.Strip) kv.Value.Dispose();
                m.Strip.Clear();
                if (m.Picture != null) { m.Picture.Dispose(); m.Picture = null; }
                m.Peaks = null;
            }
        }

        // ------------------------------------------------------------------ background work

        void Enqueue(Queue<Action> q, Action a)
        {
            lock (queueLock) { q.Enqueue(a); Monitor.PulseAll(queueLock); }
        }

        void Run(Queue<Action> q)
        {
            while (!disposed)
            {
                Action a = null;
                lock (queueLock)
                {
                    while (q.Count == 0 && !disposed) Monitor.Wait(queueLock, 1000);
                    if (disposed) return;
                    a = q.Dequeue();
                }
                try { a(); } catch { }
            }
        }

        void RaiseItem(MediaItem m)
        {
            if (disposed) return;
            ui.Post(_ => { if (!disposed && items.Contains(m)) { var h = ItemChanged; if (h != null) h(m); } }, null);
        }

        void RaiseList()
        {
            var h = ListChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        void Analyse(MediaItem m)
        {
            if (m.Removed) return;
            try
            {
                if (m.Kind == MediaKind.Image)
                {
                    using (var li = LoadedImage.Load(m.OriginalPath))
                    {
                        var img = li.Image;
                        int w = img.Width, h = img.Height;
                        double k = Math.Min(1.0, 3840.0 / Math.Max(w, h));
                        var bmp = new Bitmap(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)), PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.DrawImage(img, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        }
                        lock (m.Sync)
                        {
                            m.Picture = bmp;
                            m.Thumb = Thumbnail(bmp, 160, 90);
                            m.Info = new MediaInfo { Path = m.OriginalPath, Width = w, Height = h, HasVideo = true, Fps = 30 };
                        }
                        // the image is decoded already, so its working copy is the loaded image itself
                        m.CachePath = li.CachePath;
                        try { m.Bytes = new FileInfo(li.CachePath).Length; } catch { }
                        li.CachePath = null; // keep the copy until the item is removed
                    }
                    m.Status = MediaStatus.Ready;
                    RaiseItem(m);
                    return;
                }

                m.Info = FFmpeg.Probe(m.OriginalPath);
                if (m.Kind == MediaKind.Video && !m.Info.HasVideo) m.Kind = MediaKind.Audio;
                if (m.Kind == MediaKind.Audio && !m.Info.HasAudio) throw new InvalidDataException("No sound was found in this file.");
                m.Status = MediaStatus.Copying;
                RaiseItem(m);
                Enqueue(copyWork, () => Copy(m));
                if (m.Kind == MediaKind.Video) MakeThumbnails(m);
                RaiseItem(m);
                if (m.Info.HasAudio) MakePcm(m);
                if (m.Removed) DeleteFiles(m);
                RaiseItem(m);
            }
            catch (Exception ex)
            {
                if (m.Removed) return;
                m.Status = MediaStatus.Failed;
                m.Error = ex is InvalidDataException ? "Incompatible file type. " + ex.Message : ex.Message;
                RaiseItem(m);
            }
        }

        void Copy(MediaItem m)
        {
            if (m.Removed || m.Status == MediaStatus.Failed) return;
            if (m.Temporary)
            {
                m.CachePath = m.OriginalPath;
                try { m.Bytes = new FileInfo(m.OriginalPath).Length; } catch { }
                m.CopyProgress = 1;
                m.Status = MediaStatus.Ready;
                RaiseItem(m);
                return;
            }
            string target = Path.Combine(Folder, Guid.NewGuid().ToString("N") + Path.GetExtension(m.OriginalPath));
            try
            {
                long total = new FileInfo(m.OriginalPath).Length;
                long done = 0;
                var buf = new byte[1 << 20];
                var lastRaise = DateTime.MinValue;
                using (var src = new FileStream(m.OriginalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16))
                {
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (disposed || m.Removed) throw new OperationCanceledException();
                        dst.Write(buf, 0, n);
                        done += n;
                        m.CopyProgress = total > 0 ? (double)done / total : 1;
                        if ((DateTime.Now - lastRaise).TotalMilliseconds > 250) { lastRaise = DateTime.Now; RaiseItem(m); }
                    }
                }
                try { File.SetAttributes(target, FileAttributes.Normal | FileAttributes.Temporary); } catch { }
                m.Bytes = done;
                m.CachePath = target;
                m.CopyProgress = 1;
                if (m.Status == MediaStatus.Copying) m.Status = MediaStatus.Ready;
                RaiseItem(m);
            }
            catch (Exception ex)
            {
                ImageCache.Delete(target);
                if (ex is OperationCanceledException || m.Removed) return;
                ImageCache.Delete(target);
                // the original stays usable (read-only) if the copy failed, e.g. the disk is full
                m.CachePath = null;
                m.CopyProgress = 0;
                if (m.Status == MediaStatus.Copying) m.Status = MediaStatus.Ready;
                m.Error = "Couldn't copy to the cache (" + ex.Message + ") – the original is read instead.";
                RaiseItem(m);
            }
        }

        static Bitmap Thumbnail(Image src, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(20, 24, 40));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                double s = Math.Max((double)w / src.Width, (double)h / src.Height);
                int dw = (int)Math.Ceiling(src.Width * s), dh = (int)Math.Ceiling(src.Height * s);
                g.DrawImage(src, new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh));
            }
            return b;
        }

        void MakeThumbnails(MediaItem m)
        {
            var info = m.Info;
            double dur = Math.Max(0.1, info.Duration);
            int th = 72;
            int tw = Math.Max(16, Math.Min(256, (int)Math.Round(th * (double)Math.Max(1, info.Width) / Math.Max(1, info.Height)))) & ~1;
            double step = Math.Max(1.0, dur / 90.0);
            string fps = (1.0 / step).ToString("0.######", CultureInfo.InvariantCulture);
            bool keyOnly = dur > 90;
            string args = "-hide_banner -nostdin -loglevel error " + (keyOnly ? "-skip_frame nokey " : "") +
                          "-protocol_whitelist file -i " + FFmpeg.Quote(m.OriginalPath) +
                          " -map 0:v:0 -an -sn -dn -vf \"fps=" + fps + ",scale=" + tw + ":" + th + ":flags=bilinear,format=bgra\" -f rawvideo -pix_fmt bgra pipe:1";
            Process p = null;
            try
            {
                p = FFmpeg.Start(args);
                p.BeginErrorReadLine();
                var stream = p.StandardOutput.BaseStream;
                var buf = new byte[tw * th * 4];
                int i = 0;
                while (!disposed && !m.Removed && ReadFull(stream, buf))
                {
                    var bmp = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
                    var data = bmp.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    for (int y = 0; y < th; y++) Marshal.Copy(buf, y * tw * 4, data.Scan0 + y * data.Stride, tw * 4);
                    bmp.UnlockBits(data);
                    lock (m.Sync)
                    {
                        m.Strip.Add(new KeyValuePair<double, Bitmap>(i * step, bmp));
                        if (m.Thumb == null) m.Thumb = Thumbnail(bmp, 160, 90);
                    }
                    if (i == 0) RaiseItem(m);
                    i++;
                }
            }
            catch { }
            finally { FFmpeg.KillQuietly(p); }
            if (m.Thumb == null)
            {
                // fall back to a single frame from the start
                try
                {
                    var frame = FrameGrabber.Grab(m.OriginalPath, Math.Min(1.0, dur / 3), new Size(tw, th), "");
                    if (frame != null) lock (m.Sync) { m.Thumb = Thumbnail(frame, 160, 90); m.Strip.Add(new KeyValuePair<double, Bitmap>(0, frame)); }
                }
                catch { }
            }
        }

        static bool ReadFull(Stream s, byte[] buf)
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

        void MakePcm(MediaItem m)
        {
            string target = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".pcm");
            string args = "-hide_banner -nostdin -loglevel error -protocol_whitelist file -i " + FFmpeg.Quote(m.OriginalPath) +
                          " -map 0:a:0 -vn -sn -dn -ac 2 -ar 48000 -f s16le -acodec pcm_s16le -y " + FFmpeg.Quote(target);
            Process p = null;
            try
            {
                p = FFmpeg.Start(args);
                p.BeginErrorReadLine();
                p.StandardOutput.BaseStream.CopyTo(Stream.Null);
                while (!p.WaitForExit(200)) { if (disposed || m.Removed) { FFmpeg.KillQuietly(p); ImageCache.Delete(target); return; } }
                if (!File.Exists(target)) throw new IOException("no audio");
                lock (m.Sync) m.PcmPath = target;
                m.Peaks = ComputePeaks(target);
            }
            catch
            {
                ImageCache.Delete(target);
                m.PcmFailed = true;
            }
            finally { FFmpeg.KillQuietly(p); }
        }

        static float[] ComputePeaks(string pcm)
        {
            const int per = 480; // frames per peak (10 ms)
            var fi = new FileInfo(pcm);
            long frames = fi.Length / 4;
            var peaks = new float[Math.Max(1, (int)(frames / per) + 1)];
            var buf = new byte[per * 4 * 256];
            using (var fs = new FileStream(pcm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int idx = 0, n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    int count = n / 4;
                    for (int f0 = 0; f0 < count; f0 += per)
                    {
                        int max = 0;
                        int end = Math.Min(count, f0 + per);
                        for (int f = f0; f < end; f++)
                        {
                            int l = (short)(buf[f * 4] | (buf[f * 4 + 1] << 8));
                            int r = (short)(buf[f * 4 + 2] | (buf[f * 4 + 3] << 8));
                            int a = Math.Max(Math.Abs(l), Math.Abs(r));
                            if (a > max) max = a;
                        }
                        if (idx < peaks.Length) peaks[idx++] = max / 32768f;
                    }
                }
            }
            return peaks;
        }

        /// <summary>
        /// Returns a PCM file for the item played at the given speed (pitch kept), making it if needed.
        /// Blocks; call from a background thread. Returns null if the item has no usable sound.
        /// </summary>
        public static string PcmFor(MediaItem m, double speed)
        {
            string basePcm;
            lock (m.Sync) basePcm = m.PcmPath;
            if (basePcm == null || !File.Exists(basePcm)) return null;
            if (Math.Abs(speed - 1) < 0.0005) return basePcm;
            string key = speed.ToString("0.####", CultureInfo.InvariantCulture);
            lock (m.Sync)
            {
                string have;
                if (m.TempoPcm.TryGetValue(key, out have) && File.Exists(have)) return have;
            }
            string target = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".pcm");
            string args = "-hide_banner -nostdin -loglevel error -protocol_whitelist file -f s16le -ar 48000 -ac 2 -i " + FFmpeg.Quote(basePcm) +
                          " -af \"" + Tempo(speed) + "\" -f s16le -acodec pcm_s16le -ar 48000 -ac 2 -y " + FFmpeg.Quote(target);
            Process p = null;
            try
            {
                p = FFmpeg.Start(args);
                p.BeginErrorReadLine();
                p.StandardOutput.BaseStream.CopyTo(Stream.Null);
                p.WaitForExit();
                if (!File.Exists(target)) return null;
                lock (m.Sync) m.TempoPcm[key] = target;
                return target;
            }
            catch { ImageCache.Delete(target); return null; }
            finally { FFmpeg.KillQuietly(p); }
        }

        public static string Tempo(double s)
        {
            var sb = new StringBuilder();
            while (s < 0.5) { sb.Append("atempo=0.5,"); s /= 0.5; }
            while (s > 2.0) { sb.Append("atempo=2.0,"); s /= 2.0; }
            sb.Append("atempo=").Append(s.ToString("0.#####", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>True when every item used by the project has finished its sound analysis.</summary>
        public bool AudioReady(Project p, out string waitingFor)
        {
            waitingFor = null;
            foreach (var c in p.AllClips())
            {
                if (c.Kind == ClipKind.Text || c.Kind == ClipKind.Image) continue;
                var m = Find(c.MediaId);
                if (m == null || !m.IsUsable || !m.HasAudio || m.PcmFailed) continue;
                if (m.PcmPath == null) { waitingFor = m.Name; return false; }
            }
            return true;
        }

        public void Dispose()
        {
            disposed = true;
            lock (queueLock) Monitor.PulseAll(queueLock);
        }
    }

    /// <summary>Single-frame decoding (paused preview, freeze frames, fallbacks).</summary>
    internal static class FrameGrabber
    {
        /// <summary>Decodes one frame at source time t, scaled to size (after the given extra filters). Returns null at end of file.</summary>
        public static Bitmap Grab(string path, double t, Size size, string preFilters, Action<Process> started = null)
        {
            string args = FFmpeg.InputArgs(path, Math.Max(0, t)) +
                string.Format(CultureInfo.InvariantCulture,
                    "-map 0:v:0 -an -sn -dn -frames:v 1 -vf \"{0}scale={1}:{2}:flags=bicubic,format=bgra\" -f rawvideo -pix_fmt bgra pipe:1",
                    string.IsNullOrEmpty(preFilters) ? "" : preFilters + ",", size.Width, size.Height);
            Process p = FFmpeg.Start(args);
            try
            {
                if (started != null) started(p);
                p.BeginErrorReadLine();
                var buf = new byte[size.Width * size.Height * 4];
                var s = p.StandardOutput.BaseStream;
                int off = 0;
                while (off < buf.Length)
                {
                    int n = s.Read(buf, off, buf.Length - off);
                    if (n <= 0) return null;
                    off += n;
                }
                return Pixels.FromBgra(buf, size.Width, size.Height);
            }
            finally { FFmpeg.KillQuietly(p); }
        }

        /// <summary>Full-resolution frame (used for freeze frames).</summary>
        public static Bitmap GrabFull(MediaItem m, double t)
        {
            var sz = m.PixelSize;
            var b = Grab(m.SourcePath, t, new Size(Math.Max(2, sz.Width), Math.Max(2, sz.Height)), "");
            if (b == null && t > 0.2) b = Grab(m.SourcePath, Math.Max(0, t - 0.25), new Size(Math.Max(2, sz.Width), Math.Max(2, sz.Height)), "");
            return b;
        }
    }
}
