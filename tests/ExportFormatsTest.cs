// Exports a short project in every format the editor offers and checks what comes out.
//   tests/run.sh formats
// Compiled together with the app's sources. Needs the system ffmpeg (with libx265, libvpx-vp9 and libopus) and src720.mp4.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using StillsAndReels;

static class ExportFormatsTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static string Log(string file)
    {
        var p = new Process { StartInfo = new ProcessStartInfo("ffmpeg", "-hide_banner -i \"" + file + "\"") { UseShellExecute = false, RedirectStandardError = true } };
        p.Start(); string log = p.StandardError.ReadToEnd(); p.WaitForExit();
        return log;
    }

    static int Frames(string file)
    {
        var p = new Process { StartInfo = new ProcessStartInfo("ffmpeg", "-hide_banner -i \"" + file + "\" -map 0:v:0 -f null -") { UseShellExecute = false, RedirectStandardError = true } };
        p.Start(); string log = p.StandardError.ReadToEnd(); p.WaitForExit();
        var m = System.Text.RegularExpressions.Regex.Matches(log, @"frame=\s*(\d+)");
        return m.Count > 0 ? int.Parse(m[m.Count - 1].Groups[1].Value) : -1;
    }

    static Exception Export(Project p, Dictionary<int, MediaItem> media, ExportSettings s, out ExportJob job)
    {
        job = new ExportJob(p.Clone(), media, s);
        var done = new ManualResetEvent(false); Exception err = null;
        job.Completed += e => { err = e; done.Set(); };
        job.Start();
        if (!done.WaitOne(300000)) return new TimeoutException("the export didn't finish");
        return err;
    }

    static double Psnr(Bitmap a, Bitmap b)
    {
        double se = 0; long n = 0;
        for (int y = 0; y < a.Height; y += 3)
            for (int x = 0; x < a.Width; x += 3)
            {
                var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
                se += (p.R - q.R) * (p.R - q.R) + (p.G - q.G) * (p.G - q.G) + (p.B - q.B) * (p.B - q.B); n += 3;
            }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (se / n));
    }

    static int Main(string[] args)
    {
        string work = args[0], video = Path.Combine(work, "src720.mp4");
        ImageCache.Initialize();
        var info = MediaInfo.Parse(video, Log(video));
        var m = new MediaItem { Id = 1, Kind = MediaKind.Video, Status = MediaStatus.Ready, Name = "src720.mp4", OriginalPath = video, Info = info };
        string pcm = Path.Combine(work, "formats.pcm");
        Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -y -i \"" + video + "\" -map 0:a:0 -ac 2 -ar 48000 -f s16le \"" + pcm + "\"") { UseShellExecute = false }).WaitForExit();
        m.PcmPath = pcm;
        var media = new Dictionary<int, MediaItem> { { 1, m } };
        var p = new Project { Width = 640, Height = 360, Fps = 30 };
        p.Main.Clips.Add(new Clip { Id = p.NewId(), Kind = ClipKind.Video, MediaId = 1, Start = 0, Duration = 3 });

        ExportJob job;
        foreach (var f in new[] { "mp4", "hevc", "webm" })
        {
            string outPath = Path.Combine(work, "fmt-" + f + Encoders.Extension(f));
            var err = Export(p, media, new ExportSettings { OutPath = outPath, Width = 640, Height = 360, Fps = 30, Crf = 23, Format = f }, out job);
            string log = err == null ? Log(outPath) : "";
            var oi = MediaInfo.Parse(outPath, log);
            string codec = f == "mp4" ? "h264" : f == "hevc" ? "hevc" : "vp9";
            string sound = f == "webm" ? "opus" : "aac";
            Check(err == null && Math.Abs(oi.Duration - 3) < 0.15 && oi.Width == 640 && oi.Height == 360 && (oi.VideoCodec ?? "").Contains(codec) && (oi.AudioCodec ?? "").Contains(sound) && log.Contains("bt709"),
                f + " export: " + (err != null ? err.Message : oi.VideoCodec + " + " + oi.AudioCodec + ", " + oi.Duration.ToString("0.00") + " s, " + new FileInfo(outPath).Length / 1024 + " KB, tagged bt709: " + log.Contains("bt709")));
            if (f == "hevc" && err == null) Check(log.Contains("hvc1"), "HEVC is tagged hvc1 so Apple players open it");
        }

        // asking for the graphics card where there is none (or it fails) still gives a file
        string hwPath = Path.Combine(work, "fmt-hw.mp4");
        var hwErr = Export(p, media, new ExportSettings { OutPath = hwPath, Width = 640, Height = 360, Fps = 30, Crf = 23, Format = "mp4", Hardware = true }, out job);
        Check(hwErr == null && File.Exists(hwPath), "an export asked to use the graphics card finishes on the processor when there is none" + (hwErr != null ? ": " + hwErr.Message : ""));

        string gif = Path.Combine(work, "fmt.gif");
        var gErr = Export(p, media, new ExportSettings { OutPath = gif, Width = 320, Height = 180, Fps = 10, Format = "gif" }, out job);
        int gFrames = gErr == null ? Frames(gif) : -1;
        var gi = MediaInfo.Parse(gif, gErr == null ? Log(gif) : "");
        Check(gErr == null && gFrames == 30 && gi.Width == 320 && gi.Height == 180, "GIF export: " + (gErr != null ? gErr.Message : gFrames + " frames, " + gi.Width + "x" + gi.Height + ", " + new FileInfo(gif).Length / 1024 + " KB"));
        if (gErr == null)
        {
            // a GIF frame stands for a tenth of a second of the video: compare with the closest of the frames it covers
            double best = 0;
            using (var got = FrameGrabber.Grab(gif, 0, new Size(320, 180), ""))
                foreach (double t in new[] { 0, 0.03, 0.06 })
                    using (var want = FrameGrabber.Grab(video, t, new Size(320, 180), ""))
                        best = Math.Max(best, Psnr(got, want));
            Check(best > 26, "the GIF's colours are close to the video's (" + best.ToString("0.0") + " dB with 256 colours)");
        }

        foreach (var f in new[] { "mp3", "m4a", "wav" })
        {
            string outPath = Path.Combine(work, "fmt-sound" + Encoders.Extension(f));
            var err = Export(p, media, new ExportSettings { OutPath = outPath, Format = f, AudioOnly = true, AudioKbps = 192 }, out job);
            var oi = MediaInfo.Parse(outPath, err == null ? Log(outPath) : "");
            Check(err == null && Math.Abs(oi.Duration - 3) < 0.15 && oi.HasAudio && !oi.HasVideo, f + " export: " + (err != null ? err.Message : oi.AudioCodec + ", " + oi.Duration.ToString("0.00") + " s"));
        }

        // a marked range: the file is as long as the range and starts where the range starts
        string part = Path.Combine(work, "fmt-range.mp4");
        var rErr = Export(p, media, new ExportSettings { OutPath = part, Width = 640, Height = 360, Fps = 30, Crf = 18, Format = "mp4", From = 1.0, To = 2.5 }, out job);
        var ri = MediaInfo.Parse(part, rErr == null ? Log(part) : "");
        Check(rErr == null && Math.Abs(ri.Duration - 1.5) < 0.1 && Frames(part) == 45, "range export is as long as the range: " + (rErr != null ? rErr.Message : ri.Duration.ToString("0.00") + " s, " + Frames(part) + " frames"));
        if (rErr == null)
            using (var got = FrameGrabber.Grab(part, 0, new Size(320, 180), ""))
            using (var want = FrameGrabber.Grab(video, 0.99, new Size(320, 180), ""))
            using (var wrong = FrameGrabber.Grab(video, 0, new Size(320, 180), ""))
                Check(Psnr(got, want) > 28 && Psnr(got, wrong) < Psnr(got, want) - 5, "and starts at the range's first frame (" + Psnr(got, want).ToString("0.0") + " dB against it, " + Psnr(got, wrong).ToString("0.0") + " dB against the video's start)");
        string partWav = Path.Combine(work, "fmt-range.wav");
        var wErr = Export(p, media, new ExportSettings { OutPath = partWav, Format = "wav", From = 1.0, To = 2.5 }, out job);
        Check(wErr == null && Math.Abs(new FileInfo(partWav).Length - (44 + 1.5 * 48000 * 4)) < 16, "the range's sound alone is exactly the range long");

        // size estimates stay in a sensible relation to each other
        double h264 = Encoders.EstimateMb("mp4", false, 1920, 1080, 30, 18, 192, 60), hevc = Encoders.EstimateMb("hevc", false, 1920, 1080, 30, 18, 192, 60);
        Check(hevc < h264 && h264 > 20 && h264 < 400 && Math.Abs(Encoders.EstimateMb("wav", false, 0, 0, 0, 0, 0, 60) - 10.99) < 0.05, "size estimates: H.264 " + h264.ToString("0") + " MB, HEVC " + hevc.ToString("0") + " MB for a minute of 1080p");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}
