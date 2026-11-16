// Runs the engine tests on a real Windows PC, with the bundled FFmpeg and the PC's own graphics card.
// What the Linux runs can't show: the graphics-card encoders, the sound filters and picture formats of the
// bundled FFmpeg 7.1, Windows' own GDI+, and how fast the renderer is on this processor.
//
// Built on Linux by tests/winkit.sh into one console exe (the app's sources, the tests, and the engine):
//   WinKit.exe            everything, writing winkit-log.txt next to the exe
//   WinKit.exe hardware   only the graphics-card encoder check
// Run it from the folder that holds the tests\fixtures folder.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using StillsAndReels;

static class WinKit
{
    sealed class Tee : TextWriter
    {
        readonly TextWriter a, b;
        public Tee(TextWriter a, TextWriter b) { this.a = a; this.b = b; }
        public override Encoding Encoding { get { return Encoding.UTF8; } }
        public override void Write(char c) { a.Write(c); b.Write(c); }
        public override void Write(string s) { a.Write(s); b.Write(s); }
        public override void WriteLine(string s) { a.WriteLine(s); b.WriteLine(s); b.Flush(); }
    }

    static string work;

    static string Run(string args, int timeoutMs = 300000)
    {
        var p = new Process { StartInfo = new ProcessStartInfo("ffmpeg", args) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true } };
        p.Start();
        string log = p.StandardError.ReadToEnd();
        p.WaitForExit(timeoutMs);
        return log;
    }

    static void Fixtures()
    {
        string a = Path.Combine(work, "src720.mp4"), b = Path.Combine(work, "src1080.mp4");
        if (!File.Exists(a))
            Run("-hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1280x720:rate=30 -f lavfi -i sine=frequency=660:sample_rate=48000 -t 6 " +
                "-c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p -g 30 -colorspace bt709 -color_primaries bt709 -color_trc bt709 -c:a aac \"" + a + "\"");
        if (!File.Exists(b))
            Run("-hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1920x1080:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 20 " +
                "-c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 -c:a aac \"" + b + "\"");
        Console.WriteLine("fixtures: " + (File.Exists(a) && File.Exists(b) ? "made" : "MISSING"));
    }

    static Exception Export(Project p, Dictionary<int, MediaItem> media, ExportSettings s, out ExportJob job, out double seconds)
    {
        var sw = Stopwatch.StartNew();
        job = new ExportJob(p.Clone(), media, s);
        var done = new ManualResetEvent(false); Exception err = null;
        job.Completed += e => { err = e; done.Set(); };
        job.Start();
        bool finished = done.WaitOne(600000);
        seconds = sw.Elapsed.TotalSeconds;
        return finished ? err : new TimeoutException("the export didn't finish");
    }

    /// <summary>Which graphics-card encoders work here, and how an export with each compares with the processor.</summary>
    static int Hardware()
    {
        Console.WriteLine("=== hardware");
        int fails = 0;
        foreach (var hw in new[] { "nvenc", "qsv", "amf" })
            foreach (var f in new[] { "mp4", "hevc" })
            {
                string log = Run("-hide_banner -nostdin -loglevel error -f lavfi -i color=c=black:s=1280x720:r=30 -frames:v 8 " + Encoders.VideoArgs(f, hw, 20, "medium") + "-f null -", 20000).Trim();
                Console.WriteLine("  probe " + hw + " " + f + ": " + (log.Length == 0 ? "works" : "no (" + log.Split('\n')[0].Trim() + ")"));
            }
        string h264 = Encoders.Detect("mp4"), hevc = Encoders.Detect("hevc");
        Console.WriteLine("detected for H.264: " + (h264 ?? "none") + ", for HEVC: " + (hevc ?? "none"));

        string video = Path.Combine(work, "src1080.mp4");
        var info = MediaInfo.Parse(video, Run("-hide_banner -i \"" + video + "\""));
        var m = new MediaItem { Id = 1, Kind = MediaKind.Video, Status = MediaStatus.Ready, Name = "src1080.mp4", OriginalPath = video, Info = info };
        string pcm = Path.Combine(work, "src1080.pcm");
        Run("-hide_banner -loglevel error -y -i \"" + video + "\" -map 0:a:0 -ac 2 -ar 48000 -f s16le \"" + pcm + "\"");
        m.PcmPath = pcm;
        var media = new Dictionary<int, MediaItem> { { 1, m } };
        var p = new Project { Width = 1920, Height = 1080, Fps = 30 };
        p.Main.Clips.Add(new Clip { Id = p.NewId(), Kind = ClipKind.Video, MediaId = 1, Start = 0, Duration = 20 });

        foreach (var f in new[] { "mp4", "hevc" })
            foreach (bool card in new[] { false, true })
            {
                if (card && (f == "mp4" ? h264 : hevc) == null) { Console.WriteLine(f + " on the graphics card: skipped, no encoder found"); continue; }
                string outPath = Path.Combine(work, "hw-" + f + (card ? "-card" : "-cpu") + ".mp4");
                ExportJob job; double secs;
                var err = Export(p, media, new ExportSettings { OutPath = outPath, Width = 1920, Height = 1080, Fps = 30, Crf = 18, Format = f, Hardware = card }, out job, out secs);
                bool ok = err == null && File.Exists(outPath) && new FileInfo(outPath).Length > 100000;
                string psnr = "";
                if (ok)
                {
                    var mt = System.Text.RegularExpressions.Regex.Match(Run("-hide_banner -i \"" + outPath + "\" -i \"" + video + "\" -lavfi psnr -f null -"), @"average:([0-9.]+)");
                    psnr = mt.Success ? ", " + mt.Groups[1].Value + " dB against the source" : "";
                }
                bool asAsked = !card || (job.UsedHardware != null && !job.UsedFallback);
                Console.WriteLine((ok && asAsked ? "PASS " : "FAIL ") + f + (card ? " on the graphics card" : " on the processor") + ": " +
                    (err != null ? err.Message : secs.ToString("0.0") + " s for 20 s of 1080p, " + new FileInfo(outPath).Length / 1048576.0 + " MB" + psnr +
                    ", encoder used: " + (job.UsedHardware ?? "software") + (job.UsedFallback ? " (the card failed and the processor finished it)" : "")));
                if (!(ok && asAsked)) fails++;
            }
        return fails;
    }

    static int RunTest(string type, params string[] args)
    {
        Console.WriteLine("=== " + type);
        try
        {
            var t = typeof(WinKit).Assembly.GetType(type);
            var main = t.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            object r = main.Invoke(null, main.GetParameters().Length == 0 ? null : new object[] { args });
            return r is int ? (int)r : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + type + " stopped: " + (ex.InnerException ?? ex));
            return 1;
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        string here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Directory.SetCurrentDirectory(here);
        using (var logFile = new StreamWriter(Path.Combine(here, "winkit-log.txt"), false, new UTF8Encoding(false)))
        {
            Console.SetOut(new Tee(Console.Out, logFile));
            work = Path.Combine(here, "work");
            Directory.CreateDirectory(work);
            Console.WriteLine("Stills & Reels 3.0 Windows checks, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            Console.WriteLine(Environment.OSVersion + ", " + Environment.ProcessorCount + " cores, .NET " + Environment.Version);
            // the tests call "ffmpeg" by name: put the app's own engine first on the path
            string engine = FFmpeg.Locate();
            if (FFmpeg.IsWindows) Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(engine) + ";" + Environment.GetEnvironmentVariable("PATH"));
            Console.WriteLine("engine: " + engine);
            {
                var v = new Process { StartInfo = new ProcessStartInfo("ffmpeg", "-version") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true } };
                v.Start(); Console.WriteLine(v.StandardOutput.ReadLine()); v.StandardOutput.ReadToEnd(); v.WaitForExit();
            }
            Fixtures();
            ImageCache.Initialize();
            var failed = new List<string>();
            bool only = args.Length > 0;
            Func<string, bool> wanted = name => !only || Array.IndexOf(args, name) >= 0;
            if (wanted("hardware") && Hardware() != 0) failed.Add("hardware");
            if (wanted("compose") && RunTest("ComposeTest") != 0) failed.Add("compose");
            if (wanted("project") && RunTest("ProjectTest", work) != 0) failed.Add("project");
            if (wanted("sound") && RunTest("SoundTest", work) != 0) failed.Add("sound");
            if (wanted("formats") && RunTest("ExportFormatsTest", work) != 0) failed.Add("formats");
            if (wanted("picture") && RunTest("PictureTest", work) != 0) failed.Add("picture");
            if (wanted("bench")) RunTest("Bench");
            Console.WriteLine(failed.Count == 0 ? "WINKIT: ALL PASSED" : "WINKIT FAILED: " + string.Join(" ", failed.ToArray()));
            Console.Out.Flush();
            return failed.Count;
        }
    }
}
