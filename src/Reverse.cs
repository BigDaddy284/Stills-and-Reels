using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace StillsAndReels
{
    /// <summary>
    /// Makes a backwards copy of part of a video or sound file, as a new file in the cache the editor then uses like
    /// any other media (it is copied next to the project when the project is saved).
    ///
    /// Playing a compressed video backwards frame by frame would mean decoding each group of frames again for every
    /// frame shown, so the reversed part is written out once instead. A video is turned round in short pieces, each
    /// small enough to hold in memory, which are then joined last-to-first; the sound is turned round from the
    /// prepared PCM. The original is only read.
    /// </summary>
    internal static class Reverser
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Frames per piece; 0 works it out from the frame size. Set by tests to exercise the joins.</summary>
        internal static int PieceFrames;

        /// <summary>
        /// Reverses [from, from + span) seconds of the item. Returns the new file. Blocks; throws on failure or when cancelled.
        /// </summary>
        public static string Make(MediaItem m, double from, double span, bool soundOnly, Action<double> progress, Func<bool> cancelled)
        {
            string folder = MediaLibrary.Folder;
            string stem = "Reversed " + Path.GetFileNameWithoutExtension(m.Name ?? "clip");
            foreach (char ch in Path.GetInvalidFileNameChars()) stem = stem.Replace(ch, '_');
            if (stem.Length > 80) stem = stem.Substring(0, 80);
            string ext = soundOnly ? ".wav" : ".mp4";
            string target = Path.Combine(folder, stem + ext);
            for (int i = 2; File.Exists(target); i++) target = Path.Combine(folder, stem + " (" + i + ")" + ext);

            string work = Path.Combine(folder, "rev-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string wav = null;
                string pcm;
                lock (m.Sync) pcm = m.PcmPath;
                if (m.HasAudio && pcm != null && File.Exists(pcm))
                {
                    wav = soundOnly ? target : Path.Combine(work, "sound.wav");
                    ReverseSound(pcm, from, span, wav);
                }
                if (soundOnly)
                {
                    if (wav == null) throw new InvalidOperationException("this clip has no sound to reverse");
                    return target;
                }

                double fps = m.Info != null && m.Info.Fps > 1 ? Math.Min(60, m.Info.Fps) : 30;
                var px = m.PixelSize;
                // The reverse filter holds every frame of a piece at once: keep a piece to about 400 MB of frames.
                long perFrame = Math.Max(1, (long)px.Width * px.Height * 3 / 2);
                int per = PieceFrames > 0 ? PieceFrames : (int)Math.Max(8, Math.Min(240, 400L * 1024 * 1024 / perFrame));
                long total = Math.Max(1, (long)Math.Round(span * fps));
                int pieces = (int)((total + per - 1) / per);
                var list = new StringBuilder();
                var names = new List<string>();
                string fpsText = fps.ToString("0.#####", Inv);
                for (int i = 0; i < pieces; i++)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    long first = (long)i * per;
                    int count = (int)Math.Min(per, total - first);
                    // A seek lands on the first frame at or after the time asked for, and the time is passed to the
                    // engine in milliseconds: ask a quarter of a frame early, so rounding can't skip the piece's first frame.
                    double at = Math.Max(0, from + first / fps - 0.25 / fps);
                    string piece = Path.Combine(work, "p" + i.ToString("00000") + ".mkv");
                    // -t on the input stops the decoder soon after the piece; trim then cuts to the exact frame count
                    string args = FFmpeg.InputArgs(m.SourcePath, at).Replace("-protocol_whitelist", "-t " + (count / fps + 1).ToString("0.###", Inv) + " -protocol_whitelist") +
                        "-map 0:v:0 -an -sn -dn -vf \"fps=" + fpsText + ",trim=end_frame=" + count + ",reverse,setpts=N/(" + fpsText + ")/TB,format=yuv420p\" " +
                        "-c:v libx264 -preset veryfast -crf 14 -g " + (int)Math.Round(fps) + " -r " + fpsText + " -y " + FFmpeg.Quote(piece);
                    Run(args, cancelled);
                    if (!File.Exists(piece) || new FileInfo(piece).Length == 0) throw new IOException("a part of the video couldn't be read");
                    names.Add(piece);
                    if (progress != null) progress((i + 1) / (double)pieces * 0.95);
                }
                for (int i = names.Count - 1; i >= 0; i--)
                    list.Append("file '").Append(names[i].Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
                string listFile = Path.Combine(work, "list.txt");
                File.WriteAllText(listFile, list.ToString(), new UTF8Encoding(false));
                string join = "-hide_banner -nostdin -loglevel error -f concat -safe 0 -i " + FFmpeg.Quote(listFile) +
                    (wav != null ? " -i " + FFmpeg.Quote(wav) + " -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 256k -shortest" : " -map 0:v:0 -c:v copy") +
                    " -movflags +faststart -y " + FFmpeg.Quote(target);
                Run(join, cancelled);
                if (!File.Exists(target) || new FileInfo(target).Length == 0) throw new IOException("the reversed video couldn't be written");
                if (progress != null) progress(1);
                return target;
            }
            catch
            {
                ImageCache.Delete(target);
                throw;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        static void Run(string args, Func<bool> cancelled)
        {
            Process p = null;
            var err = new StringBuilder();
            try
            {
                p = FFmpeg.Start(args);
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) { if (err.Length < 2000) err.AppendLine(e.Data); } };
                p.BeginErrorReadLine();
                p.StandardOutput.BaseStream.CopyTo(Stream.Null);
                while (!p.WaitForExit(150))
                    if (cancelled != null && cancelled()) { FFmpeg.KillQuietly(p); throw new OperationCanceledException(); }
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    string msg;
                    lock (err) msg = err.ToString().Trim();
                    throw new IOException(msg.Length > 0 ? msg.Split('\n')[0].Trim() : "the engine stopped with an error");
                }
            }
            finally { FFmpeg.KillQuietly(p); }
        }

        /// <summary>Writes [from, from + span) of a 48 kHz stereo 16-bit PCM file backwards as a WAV file.</summary>
        static void ReverseSound(string pcm, double from, double span, string wav)
        {
            using (var src = new FileStream(pcm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var fs = new FileStream(wav, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                long frames = src.Length / 4;
                long a = Math.Max(0, Math.Min(frames, (long)Math.Round(from * AudioMixer.Rate)));
                long n = (long)Math.Round(span * AudioMixer.Rate);
                long have = Math.Max(0, Math.Min(n, frames - a));
                long dataBytes = n * 4;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write((uint)(36 + dataBytes));
                w.Write(Encoding.ASCII.GetBytes("WAVE")); w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(AudioMixer.Rate); w.Write(AudioMixer.Rate * 4); w.Write((short)4); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write((uint)dataBytes);
                // the part past the end of the sound (a video whose sound stops early) comes first when reversed
                var silence = new byte[65536];
                for (long left = (n - have) * 4; left > 0; left -= silence.Length) w.Write(silence, 0, (int)Math.Min(silence.Length, left));
                const int block = 16384;
                var buf = new byte[block * 4];
                var rev = new byte[block * 4];
                for (long end = a + have; end > a;)
                {
                    int count = (int)Math.Min(block, end - a);
                    long start = end - count;
                    src.Position = start * 4;
                    int got = 0;
                    while (got < count * 4)
                    {
                        int r = src.Read(buf, got, count * 4 - got);
                        if (r <= 0) break;
                        got += r;
                    }
                    Array.Clear(buf, got, count * 4 - got);
                    for (int i = 0; i < count; i++) Buffer.BlockCopy(buf, (count - 1 - i) * 4, rev, i * 4, 4);
                    w.Write(rev, 0, count * 4);
                    end = start;
                }
            }
        }
    }
}
