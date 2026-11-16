// Screenshots of the video editor with a small project open, to look at the panels after changing them.
//   tests/run.sh shots          (writes PNGs to $SR_WORK/shots, default /tmp/sr-tests/shots)
// Compiled together with the app's sources. Needs a display (Xvfb), ImageMagick's "import", and the fixtures
// tests/run.sh makes (src720.mp4 and /tmp/pp/landscape.jpg). It only looks; it checks nothing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using StillsAndReels;

static class UiShot
{
    static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(15); }
    }

    static void Shot(string path)
    {
        Pump(500);
        var p = Process.Start(new ProcessStartInfo("import", "-window root \"" + path + "\"") { UseShellExecute = false });
        p.WaitForExit();
        Console.WriteLine("wrote " + path);
    }

    [STAThread]
    static int Main(string[] args)
    {
        string work = args[0], outDir = Path.Combine(work, "shots");
        Directory.CreateDirectory(outDir);
        string video = Path.Combine(work, "src720.mp4"), picture = "/tmp/pp/landscape.jpg";

        var p = new Project { Width = 1280, Height = 720 };
        p.Media.Add(new MediaRef { Id = 1, Path = video, Name = "src720.mp4" });
        p.Media.Add(new MediaRef { Id = 2, Path = picture, Name = "landscape.jpg" });
        var vid = new Clip { Id = p.NewId(), Kind = ClipKind.Video, MediaId = 1, Start = 0, Duration = 5 };
        var pic = new Clip { Id = p.NewId(), Kind = ClipKind.Image, MediaId = 2, Start = 5, Duration = 4, Transition = "clock", TransitionDuration = 1, Motion = "zoomin" };
        vid.Levels.Add(new LevelKey { T = 0.5, V = 1 }); vid.Levels.Add(new LevelKey { T = 2, V = 0.3 }); vid.Levels.Add(new LevelKey { T = 4, V = 1.6 });
        vid.Duck = true;
        p.Main.Clips.Add(vid); p.Main.Clips.Add(pic);
        var over = new Track { Id = p.NewId(), Kind = TrackKind.Video };
        var pip = new Clip { Id = p.NewId(), Kind = ClipKind.Image, MediaId = 2, Start = 1, Duration = 3.5, Scale = 0.35, X = 0.28, Y = -0.26, Corner = 0.25, BorderWidth = 0.06, DropShadow = 0.7, AnimIn = "pop", AnimOut = "fade" };
        over.Clips.Add(pip); p.Tracks.Add(over);
        var texts = new Track { Id = p.NewId(), Kind = TrackKind.Video };
        var title = VideoEditorView.TextPreset("gradient"); title.Id = p.NewId(); title.Start = 0.5; title.Duration = 4; title.Text = "Stills & Reels 3";
        texts.Clips.Add(title); p.Tracks.Add(texts);
        p.MarkIn = 1.5; p.MarkOut = 6;
        p.Markers.Add(new Marker { T = 3, Name = "beat" }); p.Markers.Add(new Marker { T = 7.5, Name = "" });
        pip.FadeIn = 0.6; pip.FadeOut = 0.4;
        pip.Keys.Add(new Keyframe { T = 0, X = 0.28, Y = -0.26, Scale = 0.35, Opacity = 1 }); pip.Keys.Add(new Keyframe { T = 3, X = 0.2, Y = -0.2, Scale = 0.4, Opacity = 1, Curve = 6 });
        string proj = Path.Combine(work, "ui-shot.srproj");
        p.Save(proj);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ImageCache.Initialize();
        var main = new MainForm(proj, null);
        main.StartPosition = FormStartPosition.Manual;
        main.Bounds = new Rectangle(0, 0, 1600, 1000);
        main.Show();
        Pump(6000);
        var editor = (VideoEditorView)typeof(MainForm).GetField("editor", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(main);
        var project = editor.Project;
        Func<int, Clip> find = id => project.FindClip(id);

        editor.Scrub(2.0); editor.EndScrub();
        Pump(1500);
        Shot(Path.Combine(outDir, "01-project.png"));

        var steps = new List<KeyValuePair<string, Action>>
        {
            new KeyValuePair<string, Action>("02-overlay-basic", () => { editor.Select(find(pip.Id), false); editor.ShowProps("basic"); }),
            new KeyValuePair<string, Action>("03-video-adjust", () => { editor.Select(find(vid.Id), false); editor.ShowProps("adjust"); }),
            new KeyValuePair<string, Action>("04-video-effects", () => { editor.Select(find(vid.Id), false); editor.ShowProps("effects"); }),
            new KeyValuePair<string, Action>("05-video-audio", () => { editor.Select(find(vid.Id), false); editor.ShowProps("audio"); }),
            new KeyValuePair<string, Action>("06-video-speed", () => { editor.Select(find(vid.Id), false); editor.ShowProps("speed"); }),
            new KeyValuePair<string, Action>("07-text", () => { editor.Select(find(title.Id), false); editor.ShowProps("text"); }),
        };
        foreach (var s in steps)
        {
            try { s.Value(); } catch (Exception ex) { Console.WriteLine(s.Key + ": " + ex); }
            Pump(900);
            Shot(Path.Combine(outDir, s.Key + ".png"));
        }
        // the left panel's pages
        var tabs = typeof(VideoEditorView).GetField("tabs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(editor);
        var selected = tabs.GetType().GetProperty("Selected");
        foreach (var page in new[] { "text", "transitions", "filters", "effects" })
        {
            try { selected.SetValue(tabs, page, null); } catch (Exception ex) { Console.WriteLine(page + ": " + ex); }
            Pump(900);
            Shot(Path.Combine(outDir, "1x-left-" + page + ".png"));
        }
        // the export window
        try
        {
            var dlg = new ExportForm(project.Clone(), new Dictionary<int, MediaItem>(), "ui-shot", new List<string>());
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(420, 120);
            dlg.Show(main);
            Pump(1500);
            Shot(Path.Combine(outDir, "20-export.png"));
            var kind = (Segmented)typeof(ExportForm).GetField("kind", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dlg);
            kind.Selected = "gif";
            typeof(ExportForm).GetMethod("Sync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(dlg, null);
            Pump(400);
            Shot(Path.Combine(outDir, "21-export-gif.png"));
            dlg.Close();
        }
        catch (Exception ex) { Console.WriteLine("export: " + ex); }
        // decoders and the engine's child processes would keep an ordinary exit waiting
        Console.Out.Flush();
        Process.GetCurrentProcess().Kill();
        return 0;
    }
}
