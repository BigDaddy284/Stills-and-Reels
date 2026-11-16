// Pixel tests for the video editor's renderer (src/Raster.cs) and compositor (src/Compositor.cs).
// Compiled together with the app's sources so it can use their types directly:
//   mcs -sdk:4.8 -unsafe -main:ComposeTest <refs> -out:ComposeTest.exe src/*.cs tests/ComposeTest.cs
//   mono ComposeTest.exe          (tests/run.sh compose does both)
// No FFmpeg and no display are needed: clips here are pictures and text.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using StillsAndReels;
using Blend = StillsAndReels.Blend;

static class ComposeTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static Frame Solid(int w, int h, int b, int g, int r, int a = 255)
    {
        var f = new Frame(w, h);
        for (int i = 0; i < w * h; i++) { f.Data[i * 4] = (byte)b; f.Data[i * 4 + 1] = (byte)g; f.Data[i * 4 + 2] = (byte)r; f.Data[i * 4 + 3] = (byte)a; }
        f.Opaque = a == 255; f.Premultiplied = a == 255;
        return f;
    }

    /// <summary>A picture whose colour says where each pixel is: blue = x, green = y.</summary>
    static Frame Gradient(int w, int h)
    {
        var f = new Frame(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                f.Data[i] = (byte)(x * 255 / Math.Max(1, w - 1)); f.Data[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1)); f.Data[i + 2] = 90; f.Data[i + 3] = 255;
            }
        f.Opaque = f.Premultiplied = true;
        return f;
    }

    static int[] Px(Frame f, int x, int y) { int i = (y * f.Width + x) * 4; return new int[] { f.Data[i], f.Data[i + 1], f.Data[i + 2], f.Data[i + 3] }; }
    static bool Near(int[] p, int b, int g, int r, int tol = 2) { return Math.Abs(p[0] - b) <= tol && Math.Abs(p[1] - g) <= tol && Math.Abs(p[2] - r) <= tol; }
    static string S(int[] p) { return "(" + p[0] + "," + p[1] + "," + p[2] + ")"; }

    static double Psnr(Frame a, Frame b)
    {
        double se = 0; long n = 0;
        for (int i = 0; i < a.Width * a.Height; i++)
            for (int c = 0; c < 3; c++) { double d = a.Data[i * 4 + c] - b.Data[i * 4 + c]; se += d * d; n++; }
        if (se == 0) return 99;
        return 10 * Math.Log10(255.0 * 255.0 / (se / n));
    }

    static int Main(string[] args)
    {
        ImageCache.Initialize();
        if (args.Length == 2 && args[0] == "dump") { Dump(args[1]); return 0; }
        RasterTests();
        CompositorTests();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }

    static void RasterTests()
    {
        // 1. an opaque picture on whole pixels is copied exactly
        var canvas = Solid(64, 48, 10, 20, 30);
        var grad = Gradient(20, 10);
        Raster.Draw(canvas, new Layer { Src = grad, Tx = 5, Ty = 7 });
        bool same = true;
        for (int y = 0; y < 10; y++) for (int x = 0; x < 20; x++) { var a = Px(canvas, x + 5, y + 7); var b = Px(grad, x, y); if (a[0] != b[0] || a[1] != b[1] || a[2] != b[2]) same = false; }
        Check(same, "a picture on whole pixels is copied exactly");
        Check(Near(Px(canvas, 4, 7), 10, 20, 30, 0) && Near(Px(canvas, 25, 7), 10, 20, 30, 0) && Near(Px(canvas, 5, 17), 10, 20, 30, 0), "nothing outside the picture is touched");

        // 2. clipped at the canvas edges without writing outside
        canvas = Solid(16, 16, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = Solid(40, 40, 200, 100, 50), Tx = -10, Ty = -10 });
        Check(Near(Px(canvas, 0, 0), 200, 100, 50, 0) && Near(Px(canvas, 15, 15), 200, 100, 50, 0), "a picture larger than the canvas covers it");

        // 3. opacity mixes with what is underneath
        canvas = Solid(16, 16, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = Solid(16, 16, 200, 100, 50), Opacity = 0.5 });
        Check(Near(Px(canvas, 8, 8), 100, 50, 25), "50% opacity gives half the colour " + S(Px(canvas, 8, 8)));

        // 4. premultiplied transparency
        canvas = Solid(16, 16, 100, 100, 100);
        var half = Solid(16, 16, 100, 0, 0, 128);   // premultiplied: blue 200 at alpha 128
        half.Premultiplied = true;
        Raster.Draw(canvas, new Layer { Src = half });
        Check(Near(Px(canvas, 3, 3), 150, 50, 50), "a half-transparent picture mixes by its alpha " + S(Px(canvas, 3, 3)));

        // 5. scaling up keeps positions: blue runs left to right, green top to bottom
        canvas = Solid(200, 100, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = Gradient(50, 25), A = 4, D = 4 });
        var pl = Px(canvas, 2, 50); var pr = Px(canvas, 197, 50); var pt = Px(canvas, 100, 2); var pb = Px(canvas, 100, 97);
        Check(pl[0] < 8 && pr[0] > 247 && pt[1] < 8 && pb[1] > 247, "scaled picture keeps its left/right and top/bottom " + S(pl) + S(pr) + S(pt) + S(pb));
        Check(Math.Abs(Px(canvas, 100, 50)[0] - 127) < 6, "the middle of a scaled picture is the middle of the source");

        // 6. sharp scaling keeps flat colour flat and stays close to the fast scaler on smooth pictures
        var fast = Solid(160, 90, 0, 0, 0); var sharp = Solid(160, 90, 0, 0, 0);
        Raster.Draw(fast, new Layer { Src = Gradient(64, 36), A = 2.5, D = 2.5 });
        Raster.Draw(sharp, new Layer { Src = Gradient(64, 36), A = 2.5, D = 2.5, Sharp = true });
        Check(Psnr(fast, sharp) > 38, "sharp and fast scaling agree on a smooth picture (" + Psnr(fast, sharp).ToString("0.0") + " dB)");
        var flat = Solid(90, 50, 0, 0, 0);
        Raster.Draw(flat, new Layer { Src = Solid(300, 170, 77, 133, 201), A = 0.3, D = 0.3, Sharp = true });
        Check(Near(Px(flat, 45, 25), 77, 133, 201, 1) && Near(Px(flat, 1, 1), 77, 133, 201, 1), "shrinking a flat colour sharply keeps the colour " + S(Px(flat, 45, 25)));
        flat = Solid(90, 50, 0, 0, 0);
        Raster.Draw(flat, new Layer { Src = Solid(300, 170, 77, 133, 201), A = 0.3, D = 0.3 });
        Check(Near(Px(flat, 45, 25), 77, 133, 201, 1), "shrinking a flat colour quickly keeps the colour " + S(Px(flat, 45, 25)));

        // 7. a picture starting half-way into a pixel covers that pixel by half
        canvas = Solid(32, 8, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = Solid(8, 8, 200, 200, 200), Tx = 4.5, Ty = 0, A = 1, D = 1 });
        Check(Math.Abs(Px(canvas, 4, 4)[0] - 100) <= 3 && Px(canvas, 5, 4)[0] >= 198 && Px(canvas, 3, 4)[0] == 0, "a half-covered edge pixel gets half the colour (" + Px(canvas, 4, 4)[0] + ")");

        // 8. rotation by 90 degrees: the source's left edge becomes the top
        canvas = Solid(60, 60, 0, 0, 0);
        // x = -v + 50, y = u + 10 : u runs down, v runs to the left
        Raster.Draw(canvas, new Layer { Src = Gradient(40, 40), A = 0, B = 1, C = -1, D = 0, Tx = 50, Ty = 10 });
        var top = Px(canvas, 30, 11); var bottom = Px(canvas, 30, 48); var left = Px(canvas, 12, 30); var right = Px(canvas, 48, 30);
        Check(top[0] < 12 && bottom[0] > 243, "rotated a quarter turn: source left is at the top " + S(top) + S(bottom));
        Check(left[1] > 235 && right[1] < 20, "rotated a quarter turn: source top is at the right " + S(left) + S(right));
        Check(Px(canvas, 5, 30)[2] == 0 && Px(canvas, 55, 30)[2] == 0, "rotated picture stays inside its outline");

        // 9. blend modes on mid-grey
        Func<Blend, int, int, int> mix = (mode, back, front) =>
        {
            var c = Solid(4, 4, back, back, back);
            Raster.Draw(c, new Layer { Src = Solid(4, 4, front, front, front), Mode = mode });
            return Px(c, 1, 1)[0];
        };
        Check(Math.Abs(mix(Blend.Multiply, 128, 128) - 64) <= 1, "multiply 128 × 128 = " + mix(Blend.Multiply, 128, 128));
        Check(Math.Abs(mix(Blend.Screen, 128, 128) - 192) <= 1, "screen 128, 128 = " + mix(Blend.Screen, 128, 128));
        Check(mix(Blend.Add, 200, 100) == 255 && mix(Blend.Add, 20, 30) == 50, "add clamps at white");
        Check(mix(Blend.Darken, 40, 90) == 40 && mix(Blend.Lighten, 40, 90) == 90, "darken and lighten pick the right one");
        Check(mix(Blend.Difference, 40, 90) == 50, "difference");
        Check(mix(Blend.Overlay, 64, 128) >= 63 && mix(Blend.Overlay, 64, 128) <= 66 && mix(Blend.Overlay, 200, 128) > 198, "overlay keeps mid-grey neutral");
        Check(Math.Abs(mix(Blend.SoftLight, 100, 128) - 100) <= 3, "soft light with mid-grey barely changes the picture (" + mix(Blend.SoftLight, 100, 128) + ")");
        var cm = Solid(4, 4, 200, 200, 200);
        Raster.Draw(cm, new Layer { Src = Solid(4, 4, 0, 0, 0), Mode = Blend.Multiply, Opacity = 0.5 });
        Check(Math.Abs(Px(cm, 1, 1)[0] - 100) <= 2, "blend modes respect opacity (" + Px(cm, 1, 1)[0] + ")");

        // 10. clip rectangle and row alpha
        canvas = Solid(40, 20, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = Solid(40, 20, 255, 255, 255), HasClip = true, Clip = new Rectangle(10, 0, 10, 20) });
        Check(Px(canvas, 9, 5)[0] == 0 && Px(canvas, 10, 5)[0] == 255 && Px(canvas, 19, 5)[0] == 255 && Px(canvas, 20, 5)[0] == 0, "clip rectangle limits the drawing");
        canvas = Solid(100, 100, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = Solid(100, 100, 255, 255, 255), Alpha = Shapes.Circle(50, 50, 30) });
        Check(Px(canvas, 50, 50)[0] == 255 && Px(canvas, 50, 25)[0] == 255 && Px(canvas, 50, 15)[0] == 0 && Px(canvas, 5, 5)[0] == 0, "circle shape shows the inside only");

        // 11. masks in the clip's own space
        canvas = Solid(200, 100, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = Solid(200, 100, 255, 255, 255), Mask = new MaskShape { Kind = 2, W = 0.5, H = 0.5, Feather = 0.05 } });
        Check(Px(canvas, 100, 50)[0] == 255 && Px(canvas, 100, 30)[0] == 255 && Px(canvas, 100, 10)[0] == 0 && Px(canvas, 20, 50)[0] == 0, "ellipse mask shows the middle");
        // the size is a fraction of each side: half the width (100 px) by half the height (50 px)
        Check(Px(canvas, 100 + 44, 50)[0] == 255 && Px(canvas, 100 + 56, 50)[0] == 0 && Px(canvas, 100, 50 - 19)[0] == 255 && Px(canvas, 100, 50 - 31)[0] == 0, "ellipse mask is as wide and tall as asked");
        canvas = Solid(200, 100, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = Solid(200, 100, 255, 255, 255), Mask = new MaskShape { Kind = 1, W = 0.5, H = 0.5, Feather = 0.02, Invert = true } });
        Check(Px(canvas, 100, 50)[0] == 0 && Px(canvas, 5, 5)[0] == 255, "inverted rectangle mask shows the outside");
        canvas = Solid(200, 100, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = Solid(200, 100, 255, 255, 255), Mask = new MaskShape { Kind = 3, Feather = 0.02 } });
        Check(Px(canvas, 100, 20)[0] == 255 && Px(canvas, 100, 80)[0] == 0, "split mask shows one side");

        // 12. shrinking by a lot averages instead of skipping pixels (a fine checkerboard turns grey)
        var check = new Frame(256, 256);
        for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++) { byte v = (byte)(((x + y) & 1) * 255); int i = (y * 256 + x) * 4; check.Data[i] = check.Data[i + 1] = check.Data[i + 2] = v; check.Data[i + 3] = 255; }
        check.Opaque = check.Premultiplied = true;
        canvas = Solid(32, 32, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = check, A = 0.125, D = 0.125 });
        int g0 = Px(canvas, 10, 10)[0], g1 = Px(canvas, 11, 10)[0];
        Check(Math.Abs(g0 - 127) < 12 && Math.Abs(g1 - 127) < 12, "a fine pattern shrunk to an eighth turns even grey (" + g0 + ", " + g1 + ")");
        canvas = Solid(64, 64, 0, 0, 0);
        Raster.Draw(canvas, new Layer { Src = check, A = 0.2, D = 0.2 });
        g0 = Px(canvas, 10, 10)[0]; g1 = Px(canvas, 11, 10)[0];
        Check(Math.Abs(g0 - 127) < 12 && Math.Abs(g1 - 127) < 12, "and shrunk to a fifth (" + g0 + ", " + g1 + ")");
        canvas = Solid(64, 64, 0, 0, 0);
        double q = 0.2 * Math.Sqrt(0.5);
        Raster.Draw(canvas, new Layer { Src = check, A = q, B = q, C = -q, D = q, Tx = 32, Ty = 0 });
        g0 = Px(canvas, 32, 30)[0]; g1 = Px(canvas, 33, 30)[0];
        Check(Math.Abs(g0 - 127) < 16 && Math.Abs(g1 - 127) < 16, "and shrunk and turned (" + g0 + ", " + g1 + ")");
    }

    sealed class Pictures : IFrameSource
    {
        public Frame Get(Clip c, MediaItem m, double t, Size size) { return SourceFilters.ImageFrame(m, c, size); }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void Prepare(Clip c, MediaItem m, double t, Size size) { }
        public void Dispose() { }
    }

    static MediaItem Picture(int id, int w, int h, Func<int, int, Color> colour)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) bmp.SetPixel(x, y, colour(x, y));
        return new MediaItem { Id = id, Kind = MediaKind.Image, Status = MediaStatus.Ready, Picture = bmp, Name = "p" + id, Info = new MediaInfo { Width = w, Height = h, HasVideo = true, Fps = 30 } };
    }

    static Frame Render(Project p, Dictionary<int, MediaItem> media, double t, int w, int h, bool sharp = true)
    {
        var comp = new Compositor(p, id => { MediaItem m; return media.TryGetValue(id, out m) ? m : null; }) { HighQuality = sharp };
        var canvas = new Frame(w, h);
        using (var src = new Pictures()) comp.Render(canvas, t, src);
        return canvas;
    }

    /// <summary>The way 2.6 drew a clip, with GDI+, for comparison.</summary>
    static Frame OldRender(Project p, Clip c, MediaItem m, double t, int w, int h)
    {
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(p.Background));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                var bs = Compositor.BaseSize(c, m, w, h);
                using (var mx = Compositor.PlacementMatrix(c.PlacementAt(t), w, h, PointF.Empty, 1f))
                using (var ia = new ImageAttributes())
                {
                    g.MultiplyTransform(mx);
                    g.TranslateTransform(-bs.Width / 2, -bs.Height / 2);
                    g.ScaleTransform(bs.Width / m.Picture.Width, bs.Height / m.Picture.Height);
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(m.Picture, new Rectangle(0, 0, m.Picture.Width, m.Picture.Height), 0, 0, m.Picture.Width, m.Picture.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return Frame.FromBitmap(bmp, true);
        }
    }

    static void CompositorTests()
    {
        var media = new Dictionary<int, MediaItem>();
        // smooth, position-coded pictures (a sharp-edged one would punish tiny placement differences unfairly)
        media[1] = Picture(1, 320, 180, (x, y) => Color.FromArgb(255, 40 + x * 200 / 320, 40 + y * 200 / 180, 128));
        media[2] = Picture(2, 320, 180, (x, y) => Color.FromArgb(255, 20, 200, 60));
        media[3] = Picture(3, 100, 100, (x, y) => Color.FromArgb(255, 230, 40, 40));

        // a full-frame picture is the picture itself
        var p = new Project { Width = 320, Height = 180 };
        var c = new Clip { Id = 10, Kind = ClipKind.Image, MediaId = 1, Start = 0, Duration = 4 };
        p.Main.Clips.Add(c);
        var f = Render(p, media, 1, 320, 180);
        var want = Frame.FromBitmap(media[1].Picture, true);
        Check(Psnr(f, want) > 60, "a picture that fills the canvas is shown unchanged (" + Psnr(f, want).ToString("0.0") + " dB)");

        // placement matches the 2.6 renderer: moved, scaled and rotated
        c.X = 0.1; c.Y = -0.05; c.Scale = 0.6; c.Rotation = 25;
        f = Render(p, media, 1, 320, 180);
        var old = OldRender(p, c, media[1], 1, 320, 180);
        Check(Psnr(f, old) > 30, "a moved, scaled and rotated clip lands where 2.6 put it (" + Psnr(f, old).ToString("0.0") + " dB)");
        c.Rotation = 0; c.Scale = 0.5; c.X = 0.25; c.Y = 0.25;
        f = Render(p, media, 1, 320, 180);
        old = OldRender(p, c, media[1], 1, 320, 180);
        Check(Psnr(f, old) > 30, "a shrunk clip in the corner lands where 2.6 put it (" + Psnr(f, old).ToString("0.0") + " dB)");
        var corner = Px(f, 318, 178); var origin = Px(f, 2, 2);
        Check(corner[2] > 200 && origin[0] == 0 && origin[1] == 0 && origin[2] == 0, "the corner clip covers the corner and leaves the background black");

        // opacity and fades
        c.X = c.Y = 0; c.Scale = 1; c.Opacity = 0.5;
        f = Render(p, media, 1, 320, 180);
        var mid = Px(f, 160, 90);
        Check(Math.Abs(mid[2] - (40 + 100) / 2) <= 3, "50% opacity over black halves the picture (" + mid[2] + ")");
        c.Opacity = 1; c.FadeIn = 2;
        f = Render(p, media, 1, 320, 180);
        Check(Math.Abs(Px(f, 160, 90)[2] - (40 + 100) / 2) <= 3, "half-way through a fade-in the picture is half there");
        c.FadeIn = 0;

        // keyframes move the clip
        c.Keys.Add(new Keyframe { T = 0, X = -0.5, Scale = 0.2, Ease = false }); c.Keys.Add(new Keyframe { T = 4, X = 0.5, Scale = 0.2, Ease = false });
        f = Render(p, media, 0.02, 320, 180, false);
        var f2 = Render(p, media, 3.98, 320, 180, false);
        Check(Px(f, 10, 90)[1] > 0 && Px(f, 300, 90)[1] == 0 && Px(f2, 310, 90)[1] > 0 && Px(f2, 20, 90)[1] == 0, "keyframes carry a clip from left to right");
        c.Keys.Clear();

        // an overlay track draws on top, and a hidden track not at all
        var over = new Track { Id = 2, Kind = TrackKind.Video };
        var oc = new Clip { Id = 11, Kind = ClipKind.Image, MediaId = 3, Start = 0, Duration = 4, Scale = 0.5 };
        over.Clips.Add(oc); p.Tracks.Add(over);
        f = Render(p, media, 1, 320, 180);
        Check(Near(Px(f, 160, 90), 40, 40, 230, 3) && Px(f, 20, 20)[2] < 80, "an overlay draws over the main track " + S(Px(f, 160, 90)));
        oc.Blend = "screen";
        f = Render(p, media, 1, 320, 180);
        var scr = Px(f, 160, 90);
        Check(scr[2] >= 230 && scr[1] > 100 && scr[0] > 100, "a screen-blended overlay brightens instead of covering " + S(scr));
        oc.Blend = "";
        over.Hidden = true;
        f = Render(p, media, 1, 320, 180);
        Check(Px(f, 160, 90)[2] < 200, "a hidden track isn't drawn");
        p.Tracks.Remove(over);

        // transitions between two clips on the main track
        c.Duration = 2;
        var c2 = new Clip { Id = 12, Kind = ClipKind.Image, MediaId = 2, Start = 2, Duration = 2, Transition = "wiperight", TransitionDuration = 1 };
        p.Main.Clips.Add(c2);
        f = Render(p, media, 2.0, 320, 180);
        Check(Near(Px(f, 20, 90), 60, 200, 20, 3) && Px(f, 300, 90)[2] > 150, "wipe right at half-way: new clip on the left, old on the right " + S(Px(f, 20, 90)) + S(Px(f, 300, 90)));
        c2.Transition = "dissolve";
        f = Render(p, media, 2.0, 320, 180);
        var d = Px(f, 160, 90);
        Check(Math.Abs(d[1] - (140 + 200) / 2) <= 6, "dissolve at half-way mixes both clips (green " + d[1] + ")");
        c2.Transition = "circle";
        f = Render(p, media, 2.0, 320, 180);
        Check(Near(Px(f, 160, 90), 60, 200, 20, 3) && Px(f, 5, 5)[1] < 120, "circle at half-way: new clip in the middle, old in the corners");
        c2.Transition = "black";
        f = Render(p, media, 2.0, 320, 180);
        Check(Near(Px(f, 160, 90), 0, 0, 0, 3), "fade through black is black half-way " + S(Px(f, 160, 90)));
        c2.Transition = "slideleft";
        f = Render(p, media, 2.0, 320, 180);
        Check(Near(Px(f, 300, 90), 60, 200, 20, 3) && Px(f, 20, 90)[2] > 100, "slide left at half-way: new clip coming in from the right");
        foreach (var id in Looks.TransitionIds)
        {
            c2.Transition = id;
            bool ok = true;
            try { for (double t = 1.5; t <= 2.5; t += 0.1) Render(p, media, t, 320, 180, false); } catch (Exception ex) { ok = false; Console.WriteLine(ex); }
            if (!ok) Check(false, "transition " + id + " renders");
        }
        Check(true, "every transition renders across its whole length (" + Looks.TransitionIds.Length + ")");
        p.Main.Clips.Remove(c2);
        c.Duration = 4;

        // effects still apply: a mono filter removes the colour, chroma key removes the key colour
        c.Filter = "mono";
        f = Render(p, media, 1, 320, 180);
        var mono = Px(f, 200, 100);
        Check(Math.Abs(mono[0] - mono[1]) <= 2 && Math.Abs(mono[1] - mono[2]) <= 2, "the mono look makes grey " + S(mono));
        c.Filter = "";
        p.Main.Clips.Clear();
        var key = new Clip { Id = 13, Kind = ClipKind.Image, MediaId = 2, Start = 0, Duration = 4, ChromaKey = true, KeyColor = Color.FromArgb(255, 20, 200, 60).ToArgb() };
        p.Main.Clips.Add(key);
        p.Background = Color.FromArgb(255, 9, 9, 99).ToArgb();
        f = Render(p, media, 1, 320, 180);
        Check(Near(Px(f, 160, 90), 99, 9, 9, 2), "chroma key lets the background through " + S(Px(f, 160, 90)));
        p.Background = unchecked((int)0xFF000000);
        p.Main.Clips.Clear();

        // ---- 3.0 compositing: masks, frames, shadow, blurred background
        var pic = new Clip { Id = 20, Kind = ClipKind.Image, MediaId = 2, Start = 0, Duration = 4, Scale = 0.5 };
        p.Main.Clips.Add(pic);
        f = Render(p, media, 1, 320, 180);
        Check(Near(Px(f, 81, 46), 60, 200, 20, 3) && Near(Px(f, 160, 90), 60, 200, 20, 3), "a half-size clip has square corners by default " + S(Px(f, 81, 46)));
        pic.Corner = 1;
        f = Render(p, media, 1, 320, 180);
        Check(Near(Px(f, 81, 46), 0, 0, 0, 3) && Near(Px(f, 160, 90), 60, 200, 20, 3) && Near(Px(f, 160, 47), 60, 200, 20, 3), "rounded corners cut the corners off and nothing else " + S(Px(f, 81, 46)));
        pic.Corner = 0; pic.BorderWidth = 0.2; pic.BorderColor = Color.FromArgb(255, 250, 10, 10).ToArgb();
        f = Render(p, media, 1, 320, 180);
        Check(Near(Px(f, 83, 90), 10, 10, 250, 3) && Near(Px(f, 160, 48), 10, 10, 250, 3) && Near(Px(f, 160, 90), 60, 200, 20, 3) && Near(Px(f, 70, 90), 0, 0, 0, 3),
            "a border runs round the inside of the clip's edge " + S(Px(f, 83, 90)) + S(Px(f, 160, 90)));
        pic.BorderWidth = 0; pic.DropShadow = 1;
        p.Background = Color.White.ToArgb();
        f = Render(p, media, 1, 320, 180);
        var below = Px(f, 200, 139); var above = Px(f, 120, 40); var far = Px(f, 10, 10);
        Check(below[0] < 215 && above[0] > below[0] && far[0] == 255 && Near(Px(f, 160, 90), 60, 200, 20, 3), "a shadow falls below and to the right of the clip " + S(below) + S(above));
        pic.DropShadow = 0;
        p.Background = unchecked((int)0xFF000000);
        pic.MaskKind = 2; pic.MaskW = 0.5; pic.MaskH = 0.5; pic.MaskFeather = 0.05;
        f = Render(p, media, 1, 320, 180);
        Check(Near(Px(f, 160, 90), 60, 200, 20, 3) && Near(Px(f, 85, 50), 0, 0, 0, 3) && Near(Px(f, 160, 50), 0, 0, 0, 3), "an oval mask shows only the middle of the clip");
        pic.MaskInvert = true;
        f = Render(p, media, 1, 320, 180);
        Check(Near(Px(f, 160, 90), 0, 0, 0, 3) && Near(Px(f, 85, 50), 60, 200, 20, 3), "an inverted mask shows only the outside");
        pic.MaskKind = 0; pic.MaskInvert = false;
        // a tall picture on a wide canvas with a blurred background: no black bars left
        media[4] = Picture(4, 90, 160, (x, y) => Color.FromArgb(255, 200, 60 + y / 2, 40));
        pic.MediaId = 4; pic.Scale = 1;
        f = Render(p, media, 1, 320, 180);
        Check(Near(Px(f, 10, 90), 0, 0, 0, 2) && Px(f, 160, 90)[2] > 190, "a tall clip leaves black bars at the sides");
        pic.BackFill = 1;
        f = Render(p, media, 1, 320, 180);
        var bar = Px(f, 10, 90);
        Check(bar[2] > 80 && bar[2] < 180 && Px(f, 160, 90)[2] > 190 && Px(f, 310, 5)[2] > 60, "a blurred background fills the bars, dimmer than the clip " + S(bar));
        p.Main.Clips.Clear();

        // ---- a project saved by 2.6 opens and draws; what 3.0 adds is off in it
        var old26 = Project.Load("tests/fixtures/project-2.6.srproj");
        bool plain = true;
        foreach (var oc26 in old26.AllClips())
            if (oc26.Blend != "" || oc26.HasMask || oc26.BackFill != 0 || oc26.Corner != 0 || oc26.BorderWidth != 0 || oc26.DropShadow != 0) plain = false;
        Check(old26.ClipCount == 5 && old26.Width == 1280 && Math.Abs(old26.Fps - 25) < 0.01 && plain, "a 2.6 project loads with every new setting off");
        var first = old26.Main.Clips[0];
        Check(first.Keys.Count == 2 && Math.Abs(first.Speed - 1) < 1e-9 && first.Filter == "warm" && Math.Abs(first.Volume - 0.8) < 1e-9 && first.FlipH, "and keeps its own settings");
        bool drew = true;
        try { foreach (double t26 in new[] { 0.2, 1.5, 3.9, 4.1, 5.0, 6.9 }) Render(old26, media, t26, 320, 180); } catch (Exception ex) { drew = false; Console.WriteLine(ex); }
        Check(drew, "and every part of it draws");
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sr-compose-" + Guid.NewGuid().ToString("N") + ".srproj");
        first.Blend = "screen"; first.MaskKind = 2; first.MaskW = 0.4; first.Corner = 0.3; first.BackFill = 1;
        old26.Save(tmp);
        var again = Project.Load(tmp).Main.Clips[0];
        System.IO.File.Delete(tmp);
        Check(again.Blend == "screen" && again.MaskKind == 2 && Math.Abs(again.MaskW - 0.4) < 1e-9 && Math.Abs(again.Corner - 0.3) < 1e-9 && again.BackFill == 1 && again.Keys.Count == 2, "new settings survive saving and opening");

        // text: drawn, centred, and the same whether it stands still or is animated
        var text = VideoEditorView.TextPreset("title");
        text.Id = 14; text.Start = 0; text.Duration = 4; text.Text = "HELLO"; text.Shadow = false; text.Y = 0;
        p.Main.Clips.Add(text);
        f = Render(p, media, 1, 640, 360);
        var box = Lit(f);
        Check(box.Width > 80 && box.Height > 20, "text is drawn (" + box + ")");
        Check(Math.Abs((box.Left + box.Right) / 2 - 320) < 12 && Math.Abs((box.Top + box.Bottom) / 2 - 180) < 14, "text sits in the middle of the canvas");
        text.Keys.Add(new Keyframe { T = 0 }); text.Keys.Add(new Keyframe { T = 4 });
        f2 = Render(p, media, 1, 640, 360);
        var box2 = Lit(f2);
        Check(Math.Abs(box.Left - box2.Left) <= 2 && Math.Abs(box.Right - box2.Right) <= 2 && Math.Abs(box.Top - box2.Top) <= 2 && Math.Abs(box.Bottom - box2.Bottom) <= 2,
            "animated text is placed like still text (" + box + " / " + box2 + ")");
        Check(Psnr(f, f2) > 26, "animated text looks like still text (" + Psnr(f, f2).ToString("0.0") + " dB)");
        text.Keys.Clear();
        text.Keys.Add(new Keyframe { T = 0, Scale = 1 }); text.Keys.Add(new Keyframe { T = 4, Scale = 2, Ease = false });
        f2 = Render(p, media, 3.99, 640, 360);
        box2 = Lit(f2);
        Check(Math.Abs(box2.Width - box.Width * 2) < box.Width * 0.12, "text scaled by keyframes grows (" + box.Width + " -> " + box2.Width + ")");
        // drawing the same text again comes from the cache
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) Render(p, media, 1 + i * 0.01, 640, 360, false);
        Console.WriteLine("20 frames of animated text: " + sw.ElapsedMilliseconds + " ms");
    }

    // ---- pictures to look at: mono ComposeTest.exe dump <folder>

    static readonly List<KeyValuePair<string, Action<Project, Clip, Clip>>> scenes = new List<KeyValuePair<string, Action<Project, Clip, Clip>>>();
    static void Scene(string name, Action<Project, Clip, Clip> setup) { scenes.Add(new KeyValuePair<string, Action<Project, Clip, Clip>>(name, setup)); }

    /// <summary>Each scene starts with a full-canvas picture (a) and a smaller one over it (b), then changes them.</summary>
    static void Dump(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var media = new Dictionary<int, MediaItem>();
        media[1] = Picture(1, 640, 360, (x, y) => Color.FromArgb(255, 30 + x * 180 / 640, 60 + y * 150 / 360, 200 - x * 120 / 640));
        media[2] = Picture(2, 320, 180, (x, y) => ((x / 20 + y / 20) & 1) == 0 ? Color.FromArgb(255, 250, 200, 40) : Color.FromArgb(255, 230, 90, 30));
        media[3] = Picture(3, 180, 320, (x, y) => Color.FromArgb(255, 220 - y / 3, 80 + x / 2, 60 + y / 2));
        DumpScenes();
        int cols = 4, tw = 320, th = 180, pad = 6, label = 16;
        int rows = (scenes.Count + cols - 1) / cols;
        using (var sheet = new Bitmap(cols * (tw + pad) + pad, rows * (th + pad + label) + pad))
        using (var g = Graphics.FromImage(sheet))
        using (var font = new Font(FontFamily.GenericSansSerif, 8f))
        {
            g.Clear(Color.FromArgb(40, 40, 44));
            for (int i = 0; i < scenes.Count; i++)
            {
                var p = new Project { Width = 640, Height = 360 };
                var a = new Clip { Id = 1, Kind = ClipKind.Image, MediaId = 1, Start = 0, Duration = 4 };
                var b = new Clip { Id = 2, Kind = ClipKind.Image, MediaId = 2, Start = 0, Duration = 4, Scale = 0.5 };
                p.Main.Clips.Add(a);
                var tr = new Track { Id = 9, Kind = TrackKind.Video }; tr.Clips.Add(b); p.Tracks.Add(tr);
                scenes[i].Value(p, a, b);
                var f = Render(p, media, 2, tw, th);
                int x = pad + (i % cols) * (tw + pad), y = pad + (i / cols) * (th + pad + label);
                using (var bmp = f.ToBitmap()) g.DrawImageUnscaled(bmp, x, y);
                g.DrawString(scenes[i].Key, font, Brushes.White, x, y + th + 1);
            }
            sheet.Save(System.IO.Path.Combine(dir, "sheet.png"), ImageFormat.Png);
        }
        Console.WriteLine(scenes.Count + " scenes written to " + System.IO.Path.Combine(dir, "sheet.png"));
    }

    static void DumpScenes()
    {
        Scene("plain overlay", (p, a, b) => { });
        Scene("rounded + border + shadow", (p, a, b) => { b.Corner = 0.3; b.BorderWidth = 0.08; b.DropShadow = 0.8; });
        Scene("turned, shadow", (p, a, b) => { b.Rotation = 15; b.DropShadow = 1; b.Corner = 0.15; });
        Scene("oval mask, soft", (p, a, b) => { b.MaskKind = 2; b.MaskW = 0.8; b.MaskH = 0.8; b.MaskFeather = 0.4; });
        Scene("box mask, rounded, turned", (p, a, b) => { b.MaskKind = 1; b.MaskW = 0.7; b.MaskH = 0.5; b.MaskRound = 0.5; b.MaskRotation = 20; b.MaskFeather = 0.02; });
        Scene("split mask on the main clip", (p, a, b) => { a.MaskKind = 3; a.MaskRotation = 100; a.MaskFeather = 0.3; p.Background = Color.FromArgb(255, 20, 20, 30).ToArgb(); b.Scale = 0.01; });
        Scene("screen", (p, a, b) => { b.Blend = "screen"; b.Scale = 0.8; });
        Scene("multiply", (p, a, b) => { b.Blend = "multiply"; b.Scale = 0.8; });
        Scene("overlay", (p, a, b) => { b.Blend = "overlay"; b.Scale = 0.8; });
        Scene("difference", (p, a, b) => { b.Blend = "difference"; b.Scale = 0.8; });
        Scene("tall clip, black bars", (p, a, b) => { a.MediaId = 3; b.Scale = 0.01; });
        Scene("tall clip, blurred background", (p, a, b) => { a.MediaId = 3; a.BackFill = 1; b.Scale = 0.01; });
    }

    /// <summary>Bounding box of everything brighter than the black background.</summary>
    static Rectangle Lit(Frame f)
    {
        int x0 = f.Width, y0 = f.Height, x1 = -1, y1 = -1;
        for (int y = 0; y < f.Height; y++)
            for (int x = 0; x < f.Width; x++)
                if (f.Data[(y * f.Width + x) * 4 + 1] > 100) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        return x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
    }
}
