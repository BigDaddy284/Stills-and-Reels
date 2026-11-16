// Times the video editor's compositor on its own (no FFmpeg): milliseconds per 1080p frame for common set-ups.
//   tests/run.sh bench
// Numbers depend on the machine; compare runs on the same one.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using StillsAndReels;

static class Bench
{
    sealed class Pictures : IFrameSource
    {
        // decoded pictures are kept, as the editor's own sources do, so the times are the compositor's
        readonly Dictionary<string, Frame> kept = new Dictionary<string, Frame>();
        public Frame Get(Clip c, MediaItem m, double t, Size size)
        {
            string key = m.Id + "|" + size.Width + "x" + size.Height;
            Frame f;
            if (!kept.TryGetValue(key, out f)) kept[key] = f = SourceFilters.ImageFrame(m, c, size);
            return f;
        }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void Prepare(Clip c, MediaItem m, double t, Size size) { }
        public void Dispose() { }
    }

    static MediaItem Picture(int id, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var px = new byte[w * h * 4];
        var rnd = new Random(id);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(x * 255 / w + rnd.Next(12)); px[i + 1] = (byte)(y * 255 / h); px[i + 2] = (byte)((x + y) & 255); px[i + 3] = 255;
            }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, d.Scan0, px.Length);
        bmp.UnlockBits(d);
        return new MediaItem { Id = id, Kind = MediaKind.Image, Status = MediaStatus.Ready, Picture = bmp, Name = "p" + id, Info = new MediaInfo { Width = w, Height = h, HasVideo = true, Fps = 30 } };
    }

    static Dictionary<int, MediaItem> media = new Dictionary<int, MediaItem>();

    static void Time(string what, bool sharp, Action<Project, Clip> setup)
    {
        var p = new Project { Width = 1920, Height = 1080 };
        var c = new Clip { Id = 10, Kind = ClipKind.Image, MediaId = 1, Start = 0, Duration = 10 };
        p.Main.Clips.Add(c);
        setup(p, c);
        var comp = new Compositor(p, id => { MediaItem m; return media.TryGetValue(id, out m) ? m : null; }) { HighQuality = sharp };
        var canvas = new Frame(1920, 1080);
        using (var src = new Pictures())
        {
            for (int i = 0; i < 3; i++) comp.Render(canvas, 1 + i * 0.033, src);
            var sw = Stopwatch.StartNew();
            int n = 0;
            while (sw.ElapsedMilliseconds < 1200 && n < 200) { comp.Render(canvas, 1 + n * 0.0333, src); n++; }
            double ms = sw.Elapsed.TotalMilliseconds / n;
            Console.WriteLine(string.Format("{0,-46} {1,7:0.00} ms  ({2:0} fps)", what + (sharp ? " [export]" : " [preview]"), ms, 1000 / ms));
        }
    }

    static int Main()
    {
        ImageCache.Initialize();
        media[1] = Picture(1, 1920, 1080);
        media[2] = Picture(2, 1280, 720);
        media[3] = Picture(3, 3840, 2160);
        Console.WriteLine(Environment.ProcessorCount + " cores");
        foreach (bool sharp in new[] { false, true })
        {
            Time("plain 1080p clip", sharp, (p, c) => { });
            Time("720p clip scaled up to 1080p", sharp, (p, c) => { c.MediaId = 2; });
            Time("4K clip on a 1080p canvas", sharp, (p, c) => { c.MediaId = 3; });
            Time("colour adjustments", sharp, (p, c) => { c.Brightness = 10; c.Contrast = 15; c.Saturation = 20; });
            Time("look + vignette + grain", sharp, (p, c) => { c.Filter = "cinematic"; c.Vignette = 50; c.Grain = 30; });
            Time("blur 35", sharp, (p, c) => { c.Blur = 35; });
            Time("sharpen 45", sharp, (p, c) => { c.Sharpen = 45; });
            Time("chroma key", sharp, (p, c) => { c.ChromaKey = true; });
            Time("rotated and scaled 80%", sharp, (p, c) => { c.Rotation = 12; c.Scale = 0.8; });
            Time("zooming with keyframes", sharp, (p, c) => { c.Keys.Add(new Keyframe { T = 0, Scale = 1 }); c.Keys.Add(new Keyframe { T = 10, Scale = 1.3 }); });
            Time("picture-in-picture + title", sharp, (p, c) =>
            {
                var tr = new Track { Id = 2, Kind = TrackKind.Video };
                tr.Clips.Add(new Clip { Id = 11, Kind = ClipKind.Image, MediaId = 2, Start = 0, Duration = 10, Scale = 0.35, X = 0.3, Y = 0.3 });
                p.Tracks.Add(tr);
                var tt = new Track { Id = 3, Kind = TrackKind.Video };
                var text = VideoEditorView.TextPreset("title"); text.Id = 12; text.Start = 0; text.Duration = 10;
                tt.Clips.Add(text); p.Tracks.Add(tt);
            });
            Time("dissolve between two clips", sharp, (p, c) =>
            {
                c.Duration = 1.5;
                p.Main.Clips.Add(new Clip { Id = 13, Kind = ClipKind.Image, MediaId = 2, Start = 1.5, Duration = 8, Transition = "dissolve", TransitionDuration = 3 });
            });
        }
        return 0;
    }
}
