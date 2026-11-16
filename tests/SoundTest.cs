// Tests for the video editor's sound: the mixer (volume keyframes, fades, bass, treble, voice boost, ducking),
// the prepared sound files (noise reduction, pitch, even loudness, speed) and reversing a clip.
// Compiled together with the app's sources:
//   tests/run.sh sound
// Needs the system ffmpeg and the fixtures tests/run.sh makes (src720.mp4). No display is needed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using StillsAndReels;

static class SoundTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    const int Rate = AudioMixer.Rate;
    static string work;
    static readonly Dictionary<int, MediaItem> media = new Dictionary<int, MediaItem>();
    static MediaItem Media(int id) { MediaItem m; return media.TryGetValue(id, out m) ? m : null; }

    /// <summary>A sound-only media item whose prepared PCM is the given function of time (same on both channels).</summary>
    static MediaItem Tone(int id, double seconds, Func<double, double> f)
    {
        string path = Path.Combine(work, "tone" + id + ".pcm");
        int n = (int)(seconds * Rate);
        var b = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            int v = (int)Math.Round(Math.Max(-1, Math.Min(1, f(i / (double)Rate))) * 32767);
            b[i * 4] = b[i * 4 + 2] = (byte)v; b[i * 4 + 1] = b[i * 4 + 3] = (byte)(v >> 8);
        }
        File.WriteAllBytes(path, b);
        var m = new MediaItem
        {
            Id = id, Kind = MediaKind.Audio, Status = MediaStatus.Ready, Name = "tone" + id + ".wav", PcmPath = path,
            Info = new MediaInfo { HasAudio = true, Duration = seconds },
        };
        media[id] = m;
        return m;
    }

    static Func<double, double> Sine(double hz, double amp) { return t => amp * Math.Sin(2 * Math.PI * hz * t); }

    static Project OneClip(int mediaId, double duration, Action<Clip> setup = null)
    {
        var p = new Project();
        var tr = new Track { Id = p.NewId(), Kind = TrackKind.Audio };
        var c = new Clip { Id = p.NewId(), Kind = ClipKind.Audio, MediaId = mediaId, Start = 0, Duration = duration };
        if (setup != null) setup(c);
        tr.Clips.Add(c); p.Tracks.Add(tr);
        return p;
    }

    static Clip Add(Project p, int mediaId, double start, double duration, Action<Clip> setup = null)
    {
        var tr = new Track { Id = p.NewId(), Kind = TrackKind.Audio };
        var c = new Clip { Id = p.NewId(), Kind = ClipKind.Audio, MediaId = mediaId, Start = start, Duration = duration };
        if (setup != null) setup(c);
        tr.Clips.Add(c); p.Tracks.Add(tr);
        return c;
    }

    /// <summary>The whole mix of a project as samples (left channel), mixed in chunks the way playback does.</summary>
    static float[] Render(Project p, int chunk = 2400)
    {
        using (var mixer = new AudioMixer(p, Media))
        {
            int total = (int)Math.Round(p.Duration * Rate);
            var all = new float[total];
            var buf = new float[chunk * 2];
            for (int pos = 0; pos < total; pos += chunk)
            {
                int n = Math.Min(chunk, total - pos);
                mixer.Mix(pos, n, buf);
                for (int i = 0; i < n; i++) all[pos + i] = buf[i * 2];
            }
            return all;
        }
    }

    /// <summary>Loudest sample between two times.</summary>
    static double Peak(float[] s, double from, double to)
    {
        double m = 0;
        for (int i = (int)(from * Rate); i < (int)(to * Rate) && i < s.Length; i++) m = Math.Max(m, Math.Abs(s[i]));
        return m;
    }

    static bool About(double v, double want, double tol) { return Math.Abs(v - want) <= tol; }

    static void MixerTests()
    {
        Tone(1, 4, Sine(440, 0.5));
        Tone(2, 4, Sine(60, 0.25));
        Tone(3, 4, Sine(7919, 0.25));   // not a whole number of samples per wave, so the samples reach the wave's peak
        Tone(4, 4, t => 0.3 * Math.Sin(2 * Math.PI * 300 * t));

        var plain = Render(OneClip(1, 4));
        Check(About(Peak(plain, 1, 2), 0.5, 0.005), "a plain clip comes through at its own level: " + Peak(plain, 1, 2).ToString("0.000"));

        var loud = Render(OneClip(1, 4, c => c.Volume = 3));
        Check(About(Peak(loud, 1, 2), 1.5, 0.01), "volume above 200% is applied (300% gives " + Peak(loud, 1, 2).ToString("0.00") + ")");

        var faded = Render(OneClip(1, 4, c => { c.AudioFadeIn = 1; c.AudioFadeOut = 1; }));
        Check(Peak(faded, 0, 0.1) < 0.06 && About(Peak(faded, 0.45, 0.55), 0.26, 0.04) && About(Peak(faded, 1.5, 2.5), 0.5, 0.005) && Peak(faded, 3.9, 4) < 0.06,
            "fades rise and fall: start " + Peak(faded, 0, 0.1).ToString("0.00") + ", half-way " + Peak(faded, 0.45, 0.55).ToString("0.00") + ", end " + Peak(faded, 3.9, 4).ToString("0.00"));

        // volume keyframes: silent at 1 s, full at 2 s, held either side
        var keyed = Render(OneClip(1, 4, c => { c.Volume = 0.2; c.Levels.Add(new LevelKey { T = 1, V = 0 }); c.Levels.Add(new LevelKey { T = 2, V = 1 }); }));
        Check(Peak(keyed, 0, 0.9) < 0.001 && About(Peak(keyed, 1.49, 1.51), 0.25, 0.02) && About(Peak(keyed, 2.5, 3.5), 0.5, 0.005),
            "volume keyframes draw a line: before " + Peak(keyed, 0, 0.9).ToString("0.000") + ", middle " + Peak(keyed, 1.49, 1.51).ToString("0.00") + ", after " + Peak(keyed, 2.5, 3.5).ToString("0.00"));
        var k = new Clip { Duration = 4 }; k.Levels.Add(new LevelKey { T = 1, V = 0 }); k.Levels.Add(new LevelKey { T = 2, V = 1 });
        Check(About(k.VolumeAt(0), 0, 1e-9) && About(k.VolumeAt(1.25), 0.25, 1e-9) && About(k.VolumeAt(3), 1, 1e-9), "Clip.VolumeAt follows the keyframes");

        // tone: bass lifts 60 Hz and leaves 8 kHz alone; treble the other way round
        var bassLow = Render(OneClip(2, 4, c => c.Bass = 100));
        var bassHigh = Render(OneClip(3, 4, c => c.Bass = 100));
        Check(Peak(bassLow, 1, 3) > 0.25 * 3.2 && About(Peak(bassHigh, 1, 3), 0.25, 0.02),
            "bass +100 lifts 60 Hz by about 12 dB (" + (Peak(bassLow, 1, 3) / 0.25).ToString("0.00") + "×) and leaves 8 kHz (" + (Peak(bassHigh, 1, 3) / 0.25).ToString("0.00") + "×)");
        var bassCut = Render(OneClip(2, 4, c => c.Bass = -100));
        Check(Peak(bassCut, 1, 3) < 0.25 / 3.2, "bass -100 cuts 60 Hz by about 12 dB (" + (Peak(bassCut, 1, 3) / 0.25).ToString("0.00") + "×)");
        var trebHigh = Render(OneClip(3, 4, c => c.Treble = 100));
        var trebLow = Render(OneClip(2, 4, c => c.Treble = 100));
        Check(Peak(trebHigh, 1, 3) > 0.25 * 3.2 && About(Peak(trebLow, 1, 3), 0.25, 0.02),
            "treble +100 lifts 8 kHz (" + (Peak(trebHigh, 1, 3) / 0.25).ToString("0.00") + "×) and leaves 60 Hz (" + (Peak(trebLow, 1, 3) / 0.25).ToString("0.00") + "×)");

        // voice boost: rumble is cut, and a loud and a quiet voice come out closer together than they went in
        var rumble = Render(OneClip(2, 4, c => c.VoiceBoost = true));
        Check(Peak(rumble, 1, 3) < 0.25 * 0.75, "voice boost cuts rumble at 60 Hz (" + (Peak(rumble, 1, 3) / 0.25).ToString("0.00") + "×)");
        Tone(5, 4, Sine(1000, 0.6)); Tone(6, 4, Sine(1000, 0.06));
        double big = Peak(Render(OneClip(5, 4, c => c.VoiceBoost = true)), 2, 3), small = Peak(Render(OneClip(6, 4, c => c.VoiceBoost = true)), 2, 3);
        Check(big / small < 6.5 && big / small > 2 && small > 0.06, "voice boost narrows a 10× difference in level to " + (big / small).ToString("0.0") + "× and lifts the quiet one (" + small.ToString("0.000") + ")");

        // the same result whatever the size of the pieces the mix is asked for
        var p1 = OneClip(4, 4, c => { c.VoiceBoost = true; c.Bass = 40; c.Treble = -30; });
        var a = Render(p1, 2400); var b = Render(p1, 777);
        double diff = 0;
        for (int i = 0; i < a.Length; i++) diff = Math.Max(diff, Math.Abs(a[i] - b[i]));
        Check(diff < 1e-5, "filters carry on seamlessly from one piece of the mix to the next (largest difference " + diff.ToString("0.0000000") + ")");

        // ducking: music (440 Hz) under a voice (300 Hz) that speaks from 1 s to 2 s
        var duck = OneClip(1, 4, c => { c.Duck = true; c.DuckAmount = 0.8; });
        Add(duck, 4, 1, 1);
        var music = OneClip(1, 4);
        var ducked = Render(duck);
        var voiceOnly = Render(OneClipAt(4, 1, 1, 4));
        var onlyMusic = new float[ducked.Length];
        for (int i = 0; i < ducked.Length; i++) onlyMusic[i] = ducked[i] - voiceOnly[i];
        double before = Peak(onlyMusic, 0.2, 0.9), during = Peak(onlyMusic, 1.5, 1.9), after = Peak(onlyMusic, 3.6, 4);
        Check(About(before, 0.5, 0.01) && About(during, 0.1, 0.02) && About(after, 0.5, 0.03),
            "ducked music drops under the voice and returns: before " + before.ToString("0.00") + ", during " + during.ToString("0.00") + ", after " + after.ToString("0.00"));
        double held = Peak(onlyMusic, 2.05, 2.3);
        Check(held < 0.2, "ducking holds through a short gap after the voice (" + held.ToString("0.00") + " a quarter second later)");
        var alone = Render(OneClip(1, 4, c => c.Duck = true));
        Check(About(Peak(alone, 1, 3), 0.5, 0.005), "a ducked clip with nothing over it plays at its own level");
        Check(Render(music).Length == ducked.Length, "the mix is as long as the project");

        // muted and silent clips
        Check(Peak(Render(OneClip(1, 4, c => c.Muted = true)), 0, 4) == 0, "a muted clip is silent");

        // the meter's peaks
        using (var mixer = new AudioMixer(OneClip(1, 4, c => c.Volume = 0.5), Media))
        {
            var buf = new float[4800 * 2];
            mixer.Mix(48000, 4800, buf);
            Check(About(mixer.PeakL, 0.25, 0.005) && About(mixer.PeakR, 0.25, 0.005), "the mixer reports the level of what it just mixed (" + mixer.PeakL.ToString("0.000") + ")");
        }
    }

    static Project OneClipAt(int mediaId, double start, double duration, double total)
    {
        var p = new Project();
        Add(p, mediaId, start, duration);
        // a silent clip to make the project as long as the others
        Add(p, mediaId, 0, total, c => c.Muted = true);
        return p;
    }

    /// <summary>Samples (left channel) of a 48 kHz stereo 16-bit PCM file.</summary>
    static float[] ReadPcm(string path, int skipBytes = 0)
    {
        var b = File.ReadAllBytes(path);
        int n = (b.Length - skipBytes) / 4;
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = (short)(b[skipBytes + i * 4] | (b[skipBytes + i * 4 + 1] << 8)) / 32768f;
        return s;
    }

    /// <summary>How many times a second the wave crosses zero going up, between two times: its pitch.</summary>
    static double Frequency(float[] s, double from, double to)
    {
        int n = 0;
        for (int i = (int)(from * Rate) + 1; i < (int)(to * Rate) && i < s.Length; i++) if (s[i - 1] < 0 && s[i] >= 0) n++;
        return n / (to - from);
    }

    static double Rms(float[] s, double from, double to)
    {
        double sum = 0; int n = 0;
        for (int i = (int)(from * Rate); i < (int)(to * Rate) && i < s.Length; i++) { sum += s[i] * s[i]; n++; }
        return Math.Sqrt(sum / Math.Max(1, n));
    }

    static void PreparedSoundTests()
    {
        var m = Tone(10, 6, Sine(440, 0.3));

        var c = new Clip { Pitch = 12 };
        Check(c.SoundChain().StartsWith("asetrate=96000,aresample=48000,atempo=0.5"), "an octave up is asetrate 96000 with the tempo halved: " + c.SoundChain());
        Check(new Clip().SoundChain() == "", "a clip with no sound work has an empty chain");

        string up = MediaLibrary.PcmFor(m, 1, c.SoundChain());
        Check(up != null, "pitch-shifted sound is made");
        if (up != null)
        {
            var s = ReadPcm(up);
            Check(About(s.Length / (double)Rate, 6, 0.05), "pitch-shifted sound keeps its length (" + (s.Length / (double)Rate).ToString("0.00") + " s)");
            Check(About(Frequency(s, 1, 5), 880, 12), "an octave up doubles the pitch (" + Frequency(s, 1, 5).ToString("0") + " Hz)");
            Check(MediaLibrary.PcmFor(m, 1, c.SoundChain()) == up, "the same sound work is made once and reused");
            Check(MediaLibrary.PcmIfReady(m, 1, c.SoundChain(), false) == up, "and found without waiting once it exists");
        }

        string fast = MediaLibrary.PcmFor(m, 16, "");
        Check(fast != null && About(ReadPcm(fast).Length / (double)Rate, 6 / 16.0, 0.03), "16× speed gives a sixteenth of the length");
        if (fast != null) Check(About(Frequency(ReadPcm(fast), 0.05, 0.3), 440, 25), "and keeps the pitch (" + Frequency(ReadPcm(fast), 0.05, 0.3).ToString("0") + " Hz)");

        // noise reduction: a quiet hiss under a tone gets quieter where the tone stops
        var rnd = new Random(7);
        var noisy = Tone(11, 6, t => (t < 3 ? 0.3 * Math.Sin(2 * Math.PI * 440 * t) : 0) + (rnd.NextDouble() - 0.5) * 0.04);
        string clean = MediaLibrary.PcmFor(noisy, 1, new Clip { Denoise = 80 }.SoundChain());
        Check(clean != null, "noise-reduced sound is made: " + new Clip { Denoise = 80 }.SoundChain());
        if (clean != null)
        {
            double was = Rms(ReadPcm(noisy.PcmPath), 4, 5.5), now = Rms(ReadPcm(clean), 4, 5.5), tone = Rms(ReadPcm(clean), 1, 2);
            Check(now < was * 0.5 && tone > 0.15, "noise reduction lowers the hiss (" + was.ToString("0.0000") + " to " + now.ToString("0.0000") + ") and keeps the tone (" + tone.ToString("0.00") + ")");
        }

        // even loudness: a quiet recording comes up
        var quiet = Tone(12, 8, Sine(440, 0.02));
        string even = MediaLibrary.PcmFor(quiet, 1, new Clip { EvenLoudness = true }.SoundChain());
        Check(even != null, "evened-out sound is made");
        if (even != null)
        {
            double r = Rms(ReadPcm(even), 5, 7);
            Check(r > 0.05 && About(ReadPcm(even).Length / (double)Rate, 8, 0.1), "even loudness lifts a quiet recording (" + (0.02 / Math.Sqrt(2)).ToString("0.000") + " to " + r.ToString("0.000") + ")");
        }

        // not waiting: nothing is ready yet, the work is started, and it turns up
        var later = Tone(13, 3, Sine(500, 0.3));
        string chain = new Clip { Pitch = -5 }.SoundChain();
        Check(MediaLibrary.PcmIfReady(later, 1, chain, true) == null, "asking without waiting returns nothing while the sound is being made");
        string got = null;
        for (int i = 0; i < 100 && got == null; i++) { System.Threading.Thread.Sleep(100); got = MediaLibrary.PcmIfReady(later, 1, chain, false); }
        Check(got != null, "and the sound is there a moment later");
        using (var mixer = new AudioMixer(OneClip(13, 3, x => x.Pitch = 7), Media, null, false))
            Check(mixer.Missing == 1 && !mixer.HasSound, "playback doesn't wait for sound that isn't ready: the clip is counted as missing");
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

    static void ReverseTests(string video)
    {
        // sound: a rising ramp comes back falling
        var ramp = Tone(20, 2, t => t / 2 * 0.9);
        string wav = Reverser.Make(ramp, 0.5, 1.0, true, null, null);
        var s = ReadPcm(wav, 44);
        Check(About(s.Length / (double)Rate, 1.0, 0.001), "reversed sound is as long as the part asked for (" + (s.Length / (double)Rate).ToString("0.000") + " s)");
        Check(About(s[0], 0.75 * 0.9, 0.002) && About(s[s.Length - 1], 0.25 * 0.9, 0.002) && s[100] > s[200],
            "reversed sound runs backwards: starts at " + s[0].ToString("0.000") + ", ends at " + s[s.Length - 1].ToString("0.000"));
        File.Delete(wav);

        if (!File.Exists(video)) { Check(false, "fixture missing: " + video); return; }
        string log = "";
        var probe = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("ffmpeg", "-hide_banner -i \"" + video + "\"") { UseShellExecute = false, RedirectStandardError = true } };
        probe.Start(); log = probe.StandardError.ReadToEnd(); probe.WaitForExit();
        var info = MediaInfo.Parse(video, log);
        var m = new MediaItem { Id = 30, Kind = MediaKind.Video, Status = MediaStatus.Ready, Name = "src720.mp4", OriginalPath = video, Info = info };
        // its sound, as the library would have prepared it
        string pcm = Path.Combine(work, "src720.pcm");
        var ex = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -y -i \"" + video + "\" -map 0:a:0 -ac 2 -ar 48000 -f s16le \"" + pcm + "\"") { UseShellExecute = false });
        ex.WaitForExit();
        m.PcmPath = pcm;

        double last = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string rev = Reverser.Make(m, 1.0, 2.0, false, f => last = f, null);
        Check(File.Exists(rev) && last == 1, "a reversed video is written (" + new FileInfo(rev).Length / 1024 + " KB in " + sw.ElapsedMilliseconds + " ms)");
        probe = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("ffmpeg", "-hide_banner -i \"" + rev + "\"") { UseShellExecute = false, RedirectStandardError = true } };
        probe.Start(); log = probe.StandardError.ReadToEnd(); probe.WaitForExit();
        var ri = MediaInfo.Parse(rev, log);
        Check(About(ri.Duration, 2.0, 0.06) && ri.Width == info.Width && ri.Height == info.Height && ri.HasAudio && About(ri.Fps, 30, 0.01),
            "it is the length, size and frame rate of the part reversed, with sound: " + ri.Duration.ToString("0.00") + " s, " + ri.Width + "x" + ri.Height + ", " + ri.Fps.ToString("0.##") + " fps");
        var size = new Size(320, 180);
        double fd = 1 / 30.0;
        // a seek lands on the first frame at or after the time asked for, so ask a little before each frame
        Func<string, int, Bitmap> frame = (file, n) => FrameGrabber.Grab(file, Math.Max(0, n * fd - 0.012), size, "");
        using (var first = frame(rev, 0))
        using (var wantFirst = frame(video, 89))
        using (var end = frame(rev, 59))
        using (var wantEnd = frame(video, 30))
        using (var mid = frame(rev, 15))
        using (var wantMid = frame(video, 74))
        using (var wrong = frame(video, 73))
        {
            double a = Psnr(first, wantFirst), b = Psnr(end, wantEnd), c = Psnr(mid, wantMid), d = Psnr(mid, wrong);
            Check(a > 30 && b > 30 && c > 30, "its frames are the original's in the opposite order: first " + a.ToString("0.0") + " dB, middle " + c.ToString("0.0") + " dB, last " + b.ToString("0.0") + " dB");
            Check(d < c - 6, "and not some other frame (a wrong frame scores " + d.ToString("0.0") + " dB)");
        }
        File.Delete(rev);

        // in short pieces, as a long or 4K clip is done: every frame still there once, in order across the joins
        Reverser.PieceFrames = 7;
        string rev2 = Reverser.Make(m, 1.0, 2.0, false, null, null);
        Reverser.PieceFrames = 0;
        probe = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("ffmpeg", "-hide_banner -i \"" + rev2 + "\" -map 0:v:0 -f null -") { UseShellExecute = false, RedirectStandardError = true } };
        probe.Start(); log = probe.StandardError.ReadToEnd(); probe.WaitForExit();
        var fm = System.Text.RegularExpressions.Regex.Matches(log, @"frame=\s*(\d+)");
        int count = fm.Count > 0 ? int.Parse(fm[fm.Count - 1].Groups[1].Value) : -1;
        double worst = 99;
        foreach (int n in new[] { 0, 3, 4, 5, 6, 7, 8, 13, 14, 30, 52, 53, 54, 59 })
            using (var got = frame(rev2, n))
            using (var want = frame(video, 89 - n))
                worst = Math.Min(worst, got == null || want == null ? 0 : Psnr(got, want));
        Check(count == 60 && worst > 30, "reversed in 7-frame pieces: " + count + " frames, the worst match across the joins is " + worst.ToString("0.0") + " dB");
        File.Delete(rev2);
    }

    static void ProjectTests()
    {
        // the new sound settings survive saving and loading, and a clip without them reads as before
        var p = OneClip(1, 4, c => { c.Denoise = 40; c.VoiceBoost = true; c.Bass = 20; c.Treble = -10; c.Pitch = 3; c.EvenLoudness = true; c.Duck = true; c.DuckAmount = 0.5; c.Levels.Add(new LevelKey { T = 1, V = 0.5 }); });
        string path = Path.Combine(work, "sound-test.srproj");
        p.Save(path);
        var q = Project.Load(path);
        Clip r = null;
        foreach (var c in q.AllClips()) r = c;
        Check(r != null && r.Denoise == 40 && r.VoiceBoost && r.Bass == 20 && r.Treble == -10 && r.Pitch == 3 && r.EvenLoudness && r.Duck && r.DuckAmount == 0.5 && r.Levels.Count == 1 && r.Levels[0].V == 0.5,
            "sound settings and volume keyframes are saved with the project");
        var copy = r.Clone(); copy.Levels[0].V = 2;
        Check(r.Levels[0].V == 0.5, "a copied clip has its own volume keyframes");
        string old = Path.Combine("tests", "fixtures", "project-2.6.srproj");
        if (File.Exists(old))
        {
            var o = Project.Load(old);
            bool ok = true;
            foreach (var c in o.AllClips()) if (c.Levels == null || c.HasLevels || c.SoundChain() != "" || c.Duck || c.VoiceBoost || c.Bass != 0) ok = false;
            Check(ok, "a 2.6 project opens with no sound work on any clip");
        }
        var k = new Clip { Start = 10, Duration = 4, Speed = 1 };
        k.Keys.Add(new Keyframe { T = 2, Scale = 1 }); k.Levels.Add(new LevelKey { T = 2, V = 1 });
        k.ShiftKeys(-1); k.ScaleKeys(0.5);
        Check(k.Keys[0].T == 0.5 && k.Levels[0].T == 0.5, "picture and volume keyframes move together when a clip is trimmed or sped up");
    }

    static int Main(string[] args)
    {
        work = args.Length > 0 ? args[0] : Path.GetTempPath();
        ImageCache.Initialize();
        MixerTests();
        PreparedSoundTests();
        ReverseTests(Path.Combine(work, "src720.mp4"));
        ProjectTests();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}
