using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StillsAndReels
{
    /// <summary>What FFmpeg reports about a video file.</summary>
    internal sealed class MediaInfo
    {
        public string Path;
        public double Duration;
        public bool HasVideo, HasAudio;
        public int Width, Height;          // display size (after rotation and aspect ratio)
        public double Fps;
        public string VideoCodec, AudioCodec;
        public long FileSize;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static MediaInfo Parse(string path, string log)
        {
            var info = new MediaInfo { Path = path };
            var dm = Regex.Match(log, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (dm.Success)
                info.Duration = int.Parse(dm.Groups[1].Value, Inv) * 3600 + int.Parse(dm.Groups[2].Value, Inv) * 60 +
                                double.Parse(dm.Groups[3].Value, Inv);

            double rotation = 0;
            var rm = Regex.Match(log, @"rotation of (-?\d+(?:\.\d+)?) degrees");
            if (rm.Success) rotation = double.Parse(rm.Groups[1].Value, Inv);
            else
            {
                rm = Regex.Match(log, @"rotate\s*:\s*(-?\d+)");
                if (rm.Success) rotation = double.Parse(rm.Groups[1].Value, Inv);
            }

            foreach (var raw in log.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("Stream #", StringComparison.Ordinal)) continue;
                if (line.Contains("Video:") && !info.HasVideo && !line.Contains("(attached pic)"))
                {
                    info.HasVideo = true;
                    var cm = Regex.Match(line, @"Video:\s*([A-Za-z0-9_]+)");
                    if (cm.Success) info.VideoCodec = cm.Groups[1].Value;
                    var sm = Regex.Match(line, @"[ ,](\d{2,5})x(\d{2,5})[ ,\]]");
                    if (sm.Success)
                    {
                        info.Width = int.Parse(sm.Groups[1].Value, Inv);
                        info.Height = int.Parse(sm.Groups[2].Value, Inv);
                    }
                    var dar = Regex.Match(line, @"DAR (\d+):(\d+)");
                    if (dar.Success && info.Height > 0)
                    {
                        double a = double.Parse(dar.Groups[1].Value, Inv) / Math.Max(1, double.Parse(dar.Groups[2].Value, Inv));
                        int w2 = (int)Math.Round(info.Height * a);
                        if (w2 > 0 && Math.Abs(w2 - info.Width) > 2) info.Width = w2;
                    }
                    var fm = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*fps");
                    if (!fm.Success) fm = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*tbr");
                    if (fm.Success) info.Fps = double.Parse(fm.Groups[1].Value, Inv);
                }
                else if (line.Contains("Audio:") && !info.HasAudio)
                {
                    info.HasAudio = true;
                    var cm = Regex.Match(line, @"Audio:\s*([A-Za-z0-9_]+)");
                    if (cm.Success) info.AudioCodec = cm.Groups[1].Value;
                }
            }
            if (Math.Abs(Math.Abs(rotation) % 180 - 90) < 1)
            {
                int t = info.Width; info.Width = info.Height; info.Height = t;
            }
            if (info.Fps <= 0 || info.Fps > 240) info.Fps = 30;
            try { info.FileSize = new FileInfo(path).Length; } catch { }
            return info;
        }
    }

    /// <summary>
    /// Locates the bundled FFmpeg (unpacking it once into %LOCALAPPDATA%\StillsAndReels\Engine) and starts it.
    /// FFmpeg is only ever given local file paths and is limited to the "file" protocol, so it never
    /// touches the network.
    /// </summary>
    internal static class FFmpeg
    {
        const string ResourceName = "StillsAndReels.ffmpeg.exe.gz";
        static readonly object gate = new object();
        static string exePath;
        static string failure;

        public static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        /// <summary>True once FFmpeg is unpacked and ready (so callers can show "preparing" only the first time).</summary>
        public static bool IsReady { get { return exePath != null; } }

        public static string EngineFolder
        {
            get
            {
                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StillsAndReels", "Engine", BuildInfo.FfmpegVersion);
            }
        }

        /// <summary>Returns the path to ffmpeg, unpacking it on first use. Throws with a readable message on failure.</summary>
        public static string Locate()
        {
            lock (gate)
            {
                if (exePath != null) return exePath;
                if (failure != null) throw new InvalidOperationException(failure);
                try
                {
                    exePath = IsWindows ? Unpack() : "ffmpeg"; // non-Windows: development/testing only
                    return exePath;
                }
                catch (Exception ex)
                {
                    failure = "The video engine couldn't be prepared: " + ex.Message;
                    throw new InvalidOperationException(failure);
                }
            }
        }

        static string Unpack()
        {
            string dir = EngineFolder;
            string target = System.IO.Path.Combine(dir, "ffmpeg.exe");
            string marker = System.IO.Path.Combine(dir, "ffmpeg.sha256");
            try
            {
                if (File.Exists(target) && File.Exists(marker) &&
                    new FileInfo(target).Length == BuildInfo.FfmpegLength &&
                    File.ReadAllText(marker).Trim() == BuildInfo.FfmpegSha256)
                    return target;
            }
            catch { }

            Directory.CreateDirectory(dir);
            string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string hash;
            using (var res = typeof(FFmpeg).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (res == null) throw new FileNotFoundException("the video engine is missing from this copy of the app");
                using (var gz = new GZipStream(res, CompressionMode.Decompress))
                using (var sha = SHA256.Create())
                using (var outFile = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
                using (var cs = new CryptoStream(outFile, sha, CryptoStreamMode.Write))
                {
                    gz.CopyTo(cs, 1 << 20);
                    cs.FlushFinalBlock();
                    hash = Hex(sha.Hash);
                }
            }
            if (hash != BuildInfo.FfmpegSha256)
            {
                try { File.Delete(tmp); } catch { }
                throw new InvalidDataException("the video engine failed its integrity check");
            }
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            if (File.Exists(target))
            {
                // another copy of the app is using an older file; use our fresh copy under its own name
                string alt = System.IO.Path.Combine(dir, "ffmpeg-" + hash.Substring(0, 8) + ".exe");
                if (!File.Exists(alt)) File.Move(tmp, alt); else File.Delete(tmp);
                return alt;
            }
            File.Move(tmp, target);
            File.WriteAllText(marker, hash);
            return target;
        }

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static string Quote(string path) { return "\"" + path + "\""; }

        /// <summary>Common input options: local files only, no stdin, quiet.</summary>
        public static string InputArgs(string file, double seek)
        {
            var sb = new StringBuilder("-hide_banner -nostdin -loglevel error ");
            if (seek > 0.0005) sb.Append("-ss ").Append(seek.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
            sb.Append("-protocol_whitelist file -i ").Append(Quote(file)).Append(' ');
            return sb.ToString();
        }

        public static Process Start(string args)
        {
            var psi = new ProcessStartInfo(Locate(), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var p = Process.Start(psi);
            Job.Add(p);
            return p;
        }

        public static void AddToJob(Process p) { Job.Add(p); }

        /// <summary>Reads stream details (duration, size, frame rate, audio).</summary>
        public static MediaInfo Probe(string file)
        {
            string args = "-hide_banner -nostdin -protocol_whitelist file -i " + Quote(file);
            var psi = new ProcessStartInfo(Locate(), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using (var p = Process.Start(psi))
            {
                Job.Add(p);
                var errTask = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } throw new TimeoutException("Reading the video took too long."); }
                string log = errTask.Result;
                var info = MediaInfo.Parse(file, log);
                if (!info.HasVideo && !info.HasAudio)
                {
                    string reason = LastMeaningfulLine(log);
                    throw new InvalidDataException(string.IsNullOrEmpty(reason) ? "No playable video or audio was found." : reason);
                }
                return info;
            }
        }

        public static string LastMeaningfulLine(string log)
        {
            if (string.IsNullOrEmpty(log)) return null;
            var lines = log.Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.Length == 0 || l.StartsWith("At least one output file", StringComparison.Ordinal)) continue;
                // "C:\folder\clip.xyz: Invalid data found..." -> keep only the reason, not the path
                int colon = l.LastIndexOf(": ", StringComparison.Ordinal);
                if (colon > 0 && (l.IndexOf('\\') >= 0 || l.IndexOf('/') >= 0) && colon + 2 < l.Length)
                    l = l.Substring(colon + 2);
                if (l.StartsWith("Error opening input files: ", StringComparison.OrdinalIgnoreCase)) l = l.Substring(27);
                if (l.StartsWith("Error opening input: ", StringComparison.OrdinalIgnoreCase)) l = l.Substring(21);
                if (l.IndexOf("Invalid data found", StringComparison.OrdinalIgnoreCase) >= 0)
                    l = "This file isn't a video format Stills & Reels can read, or it is damaged.";
                return l.Length > 0 ? char.ToUpperInvariant(l[0]) + l.Substring(1) : l;
            }
            return null;
        }

        public static void KillQuietly(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
            try { p.Dispose(); } catch { }
        }

        /// <summary>Windows job object: FFmpeg processes end automatically if Stills & Reels closes or crashes.</summary>
        static class Job
        {
            static IntPtr handle = IntPtr.Zero;
            static bool tried;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            static extern IntPtr CreateJobObject(IntPtr attrs, string name);

            [DllImport("kernel32.dll")]
            static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

            [DllImport("kernel32.dll")]
            static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [StructLayout(LayoutKind.Sequential)]
            struct BasicLimit
            {
                public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass, SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct IoCounters { public ulong a, b, c, d, e, f; }

            [StructLayout(LayoutKind.Sequential)]
            struct ExtendedLimit
            {
                public BasicLimit Basic;
                public IoCounters Io;
                public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
            }

            public static void Add(Process p)
            {
                if (!IsWindows || p == null) return;
                try
                {
                    lock (gate)
                    {
                        if (!tried)
                        {
                            tried = true;
                            var h = CreateJobObject(IntPtr.Zero, null);
                            if (h != IntPtr.Zero)
                            {
                                var info = new ExtendedLimit();
                                info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                                int len = Marshal.SizeOf(typeof(ExtendedLimit));
                                IntPtr mem = Marshal.AllocHGlobal(len);
                                try
                                {
                                    Marshal.StructureToPtr(info, mem, false);
                                    if (SetInformationJobObject(h, 9, mem, (uint)len)) handle = h;
                                }
                                finally { Marshal.FreeHGlobal(mem); }
                            }
                        }
                    }
                    if (handle != IntPtr.Zero) AssignProcessToJobObject(handle, p.Handle);
                }
                catch { }
            }
        }
    }
}
