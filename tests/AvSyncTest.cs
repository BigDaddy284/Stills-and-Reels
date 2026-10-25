// Recording test on Linux (Xvfb): records the screen with the app's own FFmpeg arguments (ScreenRecorder.BuildArgs,
// with x11grab standing in for gdigrab) and PC sound streamed by the app's own SoundPump through a FIFO (standing in
// for the Windows named pipe). At a set moment a window turns white and a tone starts at the same instant, like a
// video playing on screen. Checks: the picture never stalls, frames aren't dropped, and sound and picture stay in sync,
// in each segment and in the finished MP4 (two segments, as after a pause, joined by ScreenRecorder.Finish).
//
// Build: mcs -sdk:4.8 -unsafe -main:AvSyncTest -r:System.dll -r:System.Drawing.dll -r:System.Runtime.Serialization.dll
//          -r:System.Xml.dll -r:System.Windows.Forms.dll -r:Microsoft.CSharp.dll -r:System.Core.dll -r:Accessibility.dll
//          -out:AvSyncTest.exe src/*.cs tests/AvSyncTest.cs
// Run:   DISPLAY=:99 mono AvSyncTest.exe <work folder> <pc|pc+mic|none|pc+slow> [segments=2]
//        pc+slow: an extra input that opens 0.8 s late sits between the screen and the sound pipe, like a screen
//        capture that is slow to start on Windows; the recorder must measure that gap and correct the sound.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using StillsAndReels;

static class AvSyncTest
{
    const int Rate = 48000, Fps = 30;
    const double SegSeconds = 6;
    // like a video on screen: picture and sound start at 2.0 s, both stop at 3.2 s, both start again at 4.2 s
    static readonly double[] On = { 2.0, 4.2 }, Off = { 3.2, 99 };
    const int EventsPerSegment = 2;
    static bool Playing(double t) { for (int i = 0; i < On.Length; i++) if (t >= On[i] && t < Off[i]) return true; return false; }
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static Form screen;
    static int fails;

    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    [STAThread]
    static int Main(string[] a)
    {
        string dir = a[0], mode = a[1];
        slowStart = mode == "pc+slow";
        int segCount = a.Length > 2 ? int.Parse(a[2]) : 2;
        Directory.CreateDirectory(dir);
        ImageCache.Initialize();
        screen = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(0, 0, 640, 360), BackColor = Color.Black, TopMost = true };
        int result = 1;
        screen.Shown += (s, e) => new Thread(() =>
        {
            try { result = Run(dir, mode, segCount); }
            catch (Exception ex) { Console.WriteLine("FAIL crashed: " + ex); result = 1; }
            screen.BeginInvoke((MethodInvoker)screen.Close);
        }) { IsBackground = true }.Start();
        Application.Run(screen);
        return result;
    }

    static void SetScreen(Color c)
    {
        screen.Invoke((MethodInvoker)(() => { screen.BackColor = c; screen.Refresh(); }));
    }

    static int Run(string dir, string mode, int segCount)
    {
        Thread.Sleep(1000);
        var segs = new List<string>();
        for (int i = 0; i < segCount; i++)
        {
            string seg = Path.Combine(dir, "seg" + i + ".mkv");
            RecordSegment(seg, mode);
            segs.Add(seg);
            Analyse(seg, "segment " + (i + 1), mode != "none", EventsPerSegment);
        }
        if (mode != "none")
        {
            // join and encode exactly as Stop does
            var rec = new ScreenRecorder();
            var f = typeof(ScreenRecorder).GetField("segments", BindingFlags.NonPublic | BindingFlags.Instance);
            var list = (List<string>)f.GetValue(rec);
            foreach (var s in segs) { string copy = Path.Combine(MediaLibrary.Folder, Path.GetFileName(s)); File.Copy(s, copy, true); list.Add(copy); }
            typeof(ScreenRecorder).GetField("before", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(rec, SegSeconds * segCount);
            string mp4 = rec.Finish(null);
            File.Copy(mp4, Path.Combine(dir, "final.mp4"), true);
            Analyse(Path.Combine(dir, "final.mp4"), "finished MP4", true, segCount * EventsPerSegment);
        }
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }

    static void RecordSegment(string seg, string mode)
    {
        SetScreen(Color.Black);
        Thread.Sleep(300);
        string fifo = seg + ".fifo";
        if (File.Exists(fifo)) File.Delete(fifo);
        Process.Start("mkfifo", "\"" + fifo + "\"").WaitForExit();
        string video = "-f x11grab -framerate " + Fps + " -draw_mouse 0 -video_size 640x360 -i :99.0+0,0 ";
        string mic = mode == "pc+mic" ? "-re -f lavfi -i anoisesrc=a=0.002:r=48000 " : null;
        string pc = mode.StartsWith("pc") ? "-f f32le -ar 48000 -ac 2 -i \"" + fifo + "\" " : null;
        string slowFifo = null, delay = null;
        if (mode == "pc+slow")
        {
            slowFifo = seg + ".slow.fifo";
            if (File.Exists(slowFifo)) File.Delete(slowFifo);
            Process.Start("mkfifo", "\"" + slowFifo + "\"").WaitForExit();
            delay = "-f f32le -ar 48000 -ac 1 -i \"" + slowFifo + "\" ";
        }
        string args = ScreenRecorder.BuildArgs(video, mic, pc, Fps, seg, delay);
        var p = Process.Start(new ProcessStartInfo("ffmpeg", args) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true });
        bool stopping = false;
        var gapMeter = new StartGapMeter(); var errText = new StringBuilder();
        // as ScreenRecorder.Start / PcSoundCapture: the "Input #0" line and the pipe connect time give the picture's lead
        p.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            if (!e.Data.StartsWith("frame=", StringComparison.Ordinal) && !e.Data.StartsWith("size=", StringComparison.Ordinal)) lock (errText) errText.AppendLine(e.Data);
            gapMeter.OnFfmpegLine(e.Data);
        };
        p.BeginErrorReadLine();
        if (slowFifo != null)
        {
            // opens 0.8 s late, then feeds silence so FFmpeg's reader never blocks
            new Thread(() =>
            {
                Thread.Sleep(800);
                try
                {
                    using (var sf = new FileStream(slowFifo, FileMode.Open, FileAccess.Write))
                    {
                        var z = new byte[480 * 4]; var c = Stopwatch.StartNew(); long w = 0;
                        while (!stopping) { long due = (long)(c.Elapsed.TotalSeconds * Rate); while (w < due) { sf.Write(z, 0, z.Length); w += 480; } Thread.Sleep(5); }
                    }
                }
                catch (IOException) { }
            }) { IsBackground = true }.Start();
        }
        Thread pump = null;
        var started = Stopwatch.StartNew();
        if (pc != null)
        {
            pump = new Thread(() =>
            {
                using (var fs = new FileStream(fifo, FileMode.Open, FileAccess.Write)) // waits for FFmpeg, like WaitForConnection
                {
                    gapMeter.OnConnected();
                    var clock = Stopwatch.StartNew();
                    bool shown = false;
                    double start = -1; long produced = 0;
                    double phase = 0;
                    try
                    {
                        SoundPump.Run(fs, Rate, 8, (ref byte[] buf) =>
                        {
                            double e = clock.Elapsed.TotalSeconds;
                            bool on = Playing(e);
                            if (on != shown) { shown = on; SetScreen(on ? Color.White : Color.Black); start = e; produced = 0; }
                            if (!on) return 0; // nothing playing: no packets, like Windows
                            long due = (long)((e - start) * Rate) - produced;
                            if (due < 480) return 0;      // packets of at least 10 ms
                            int n = (int)Math.Min(due, 4800);
                            if (buf.Length < n * 8) buf = new byte[n * 8];
                            for (int i = 0; i < n; i++)
                            {
                                var v = BitConverter.GetBytes((float)(0.3 * Math.Sin(phase)));
                                phase += 2 * Math.PI * 440 / Rate;
                                Buffer.BlockCopy(v, 0, buf, i * 8, 4); Buffer.BlockCopy(v, 0, buf, i * 8 + 4, 4);
                            }
                            produced += n;
                            return n;
                        }, () => stopping, gapMeter.Take);
                    }
                    catch (IOException) { }
                }
            }) { IsBackground = true };
            pump.Start();
        }
        else
        {
            new Thread(() =>
            {
                var c = Stopwatch.StartNew(); bool shown = false;
                while (c.Elapsed.TotalSeconds < SegSeconds) { bool on = Playing(c.Elapsed.TotalSeconds); if (on != shown) { shown = on; SetScreen(on ? Color.White : Color.Black); } Thread.Sleep(2); }
            }) { IsBackground = true }.Start();
        }
        Thread.Sleep((int)(SegSeconds * 1000));
        // as StopSegment: end the PC sound first, then "q"
        stopping = true;
        if (pump != null) pump.Join(3000);
        try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
        p.WaitForExit(10000);
        string log; lock (errText) log = errText.ToString().Trim();
        if (log.Length > 0) Console.WriteLine("  ffmpeg said: " + log.Replace("\n", " | "));
        if (!double.IsNaN(gapMeter.Lead)) Console.WriteLine(string.Format(Inv, "  picture lead {0:+0.000;-0.000} s, silence inserted {1:0.000} s", gapMeter.Lead, gapMeter.Inserted));
        File.Delete(fifo);
        if (slowFifo != null) File.Delete(slowFifo);
    }

    static string Ffmpeg(string args, bool probe = false)
    {
        var p = Process.Start(new ProcessStartInfo(probe ? "ffprobe" : "ffmpeg", args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        var o = p.StandardOutput.ReadToEndAsync();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o.Result + e;
    }

    static bool slowStart;
    static void Analyse(string file, string name, bool sound, int events)
    {
        // frame times and brightness
        string outp = Ffmpeg("-v error -f lavfi -i \"movie='" + file.Replace("'", "\\'") + "',signalstats\" -show_entries frame=pts_time:frame_tags=lavfi.signalstats.YAVG -of csv=p=0", true);
        var frames = new List<KeyValuePair<double, double>>();
        foreach (var line in outp.Split('\n'))
        {
            var parts = line.Trim().Split(',');
            double t, y;
            if (parts.Length >= 2 && double.TryParse(parts[0], NumberStyles.Float, Inv, out t) && double.TryParse(parts[1], NumberStyles.Float, Inv, out y))
                frames.Add(new KeyValuePair<double, double>(t, y));
        }
        frames.Sort((x, z) => x.Key.CompareTo(z.Key));
        if (frames.Count < 2) { Check(false, name + ": has frames"); return; }
        int first = 0;
        if (slowStart && name.StartsWith("segment") && frames.Count > 2 && frames[1].Key - frames[0].Key > 2.5 / Fps)
        {
            // pc+slow: FFmpeg grabs one frame, then captures nothing while the slow input opens; the frozen start is
            // the simulation itself, not the recorder, so the picture checks begin after it (sync must still hold)
            Console.WriteLine(string.Format(Inv, "  {0}: frozen start of {1:0.000} s while the slow input opened (expected in this mode)", name, frames[1].Key - frames[0].Key));
            first = 1;
        }
        double span = frames[frames.Count - 1].Key - frames[first].Key, maxGap = 0;
        for (int i = first + 1; i < frames.Count; i++) maxGap = Math.Max(maxGap, frames[i].Key - frames[i - 1].Key);
        double expect = span * Fps + 1;
        Check(maxGap <= 2.5 / Fps, string.Format(Inv, "{0}: picture never stalls (longest gap {1:0.000} s)", name, maxGap));
        Check(frames.Count - first >= expect * 0.95, string.Format(Inv, "{0}: no dropped frames ({1} of {2:0})", name, frames.Count - first, expect));
        // black -> white changes
        var flashes = new List<double>();
        for (int i = 1; i < frames.Count; i++) if (frames[i - 1].Value < 128 && frames[i].Value >= 128) flashes.Add(frames[i].Key);
        Check(flashes.Count == events, name + ": picture shows each event (" + flashes.Count + " of " + events + ")");
        if (!sound) return;
        string sd = Ffmpeg("-hide_banner -i \"" + file + "\" -map 0:a -af silencedetect=n=-30dB:d=0.3 -f null -");
        var tones = Regex.Matches(sd, @"silence_end: ([0-9.]+)").Cast<Match>().Select(m => double.Parse(m.Groups[1].Value, Inv)).ToList();
        Check(tones.Count >= events, name + ": sound has each event (" + tones.Count + " of " + events + ")");
        for (int i = 0; i < Math.Min(events, Math.Min(flashes.Count, tones.Count)); i++)
        {
            double off = flashes[i] - tones[i];
            // Viewers notice sound more than 45 ms early or 125 ms late (ITU-R BT.1359). Kept tighter here:
            // up to 40 ms early (plus up to 20 ms for the test window to repaint) or 90 ms late.
            Check(off > -0.090 && off < 0.060, string.Format(Inv, "{0}: sound in sync with picture, event {1} (picture {2:+0.000;-0.000} s after sound)", name, i + 1, off));
        }
        // the sound must run to the end of the picture (it used to stop about half a second early)
        double lastV = LastPts(file, "v"), lastA = LastPts(file, "a");
        Check(lastA >= lastV - 0.10, string.Format(Inv, "{0}: sound lasts to the end of the picture (sound {1:0.000} s, picture {2:0.000} s)", name, lastA, lastV));
    }

    static double LastPts(string file, string stream)
    {
        string o = Ffmpeg("-v error -select_streams " + stream + " -show_entries packet=pts_time -of csv=p=0 \"" + file + "\"", true);
        double last = -1;
        foreach (var line in o.Split('\n'))
        {
            double t;
            if (double.TryParse(line.Trim().TrimEnd(','), NumberStyles.Float, Inv, out t)) last = Math.Max(last, t);
        }
        return last;
    }
}
