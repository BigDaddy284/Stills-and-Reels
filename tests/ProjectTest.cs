// Tests for the video project model added in 3.0: keyframe motion curves, markers and the marked range,
// the recovery copy and the recent projects list.
//   tests/run.sh project
// Compiled together with the app's sources. No FFmpeg and no display are needed.
using System;
using System.Collections.Generic;
using System.IO;
using StillsAndReels;

static class ProjectTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static bool About(double a, double b, double tol = 1e-6) { return Math.Abs(a - b) <= tol; }

    static int Main(string[] args)
    {
        string work = args[0];
        ImageCache.Initialize();

        // ---- motion curves
        for (int kind = 1; kind <= 7; kind++)
        {
            var k = new Keyframe { Curve = kind };
            Check(About(k.Shape(1), 1) && (kind == 5 || About(k.Shape(0), 0)), Keyframe.CurveNames[kind] + " starts at the first keyframe and ends on the second");
        }
        Check(About(new Keyframe { Curve = 1 }.Shape(0.3), 0.3) && new Keyframe { Curve = 3 }.Shape(0.5) < 0.2 && new Keyframe { Curve = 4 }.Shape(0.5) > 0.8,
            "straight is even, slow start lags, slow finish leads");
        Check(new Keyframe { Curve = 5 }.Shape(0.99) == 0, "stay-then-jump holds until the keyframe");
        double over = 0; for (double u = 0; u <= 1; u += 0.01) over = Math.Max(over, new Keyframe { Curve = 6 }.Shape(u));
        Check(over > 1.05 && over < 1.2, "overshoot passes the target by a little (" + over.ToString("0.00") + ")");
        Check(new Keyframe { Ease = true }.CurveKind == 2 && new Keyframe { Ease = false }.CurveKind == 1, "a 2.6 keyframe keeps its smooth or straight motion");

        var c = new Clip { Start = 0, Duration = 4 };
        c.Keys.Add(new Keyframe { T = 0, X = 0, Scale = 1, Opacity = 1 });
        c.Keys.Add(new Keyframe { T = 2, X = 1, Scale = 0.02, Opacity = 1, Curve = 6 });
        double minScale = 9, maxOpacity = 0;
        for (double t = 0; t <= 2; t += 0.02) { var p = c.PlacementAt(t); minScale = Math.Min(minScale, p.Scale); maxOpacity = Math.Max(maxOpacity, p.Opacity); }
        Check(minScale >= 0.01 && maxOpacity <= 1, "an overshooting curve never turns a clip inside out or past full opacity");
        Check(About(c.PlacementAt(1).X, new Keyframe { Curve = 6 }.Shape(0.5)), "the curve of the keyframe being travelled to is the one used");

        // ---- markers and range are saved, and absent from a 2.6 project
        var proj = new Project { Width = 1280, Height = 720 };
        proj.Main.Clips.Add(new Clip { Id = proj.NewId(), Kind = ClipKind.Text, Text = "hello", Start = 0, Duration = 5 });
        proj.MarkIn = 1; proj.MarkOut = 3;
        proj.Markers.Add(new Marker { T = 2.5, Name = "chorus" });
        string path = Path.Combine(work, "project-test.srproj");
        proj.Save(path);
        var back = Project.Load(path);
        Check(back.HasRange && back.MarkIn == 1 && back.MarkOut == 3 && back.Markers.Count == 1 && back.Markers[0].Name == "chorus" && back.Markers[0].T == 2.5, "the marked range and markers are saved with the project");
        var copy = back.Clone(); copy.Markers[0].T = 9;
        Check(back.Markers[0].T == 2.5, "an undo snapshot has its own markers");
        string old = Path.Combine("tests", "fixtures", "project-2.6.srproj");
        if (File.Exists(old))
        {
            var o = Project.Load(old);
            Check(!o.HasRange && o.Markers != null && o.Markers.Count == 0, "a 2.6 project opens with no range and no markers");
        }

        // ---- recovery copy: project, where it was saved, and cache-only media
        string temp = Path.Combine(MediaLibrary.Folder, "Freeze frame 1.png");
        File.WriteAllBytes(temp, new byte[] { 1, 2, 3, 4, 5 });
        string kept = Path.Combine(work, "kept-original.png");
        File.WriteAllBytes(kept, new byte[] { 9 });
        var snap = proj.Clone();
        snap.Media.Add(new MediaRef { Id = 7, Path = temp, Name = "Freeze frame 1.png" });
        snap.Media.Add(new MediaRef { Id = 8, Path = kept, Name = "kept-original.png" });
        ProjectRecovery.Clear();
        ProjectRecovery.Write(snap, new HashSet<int> { 7 }, path);
        string own = Path.Combine(ProjectRecovery.Root, System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
        Check(File.Exists(Path.Combine(own, "project.srproj")) && Directory.GetFiles(Path.Combine(own, "media")).Length == 1, "the recovery copy holds the project and its cache-only media");
        Check(!ProjectRecovery.FindLeftBehind().Contains(own), "a running app's recovery copy is not offered to another copy of the app");
        File.Delete(temp);   // as the cache clean-up at the next start would
        // pretend the process is gone: move the folder to an id no process has
        string dead = Path.Combine(ProjectRecovery.Root, "999999991");
        if (Directory.Exists(dead)) Directory.Delete(dead, true);
        Directory.Move(own, dead);
        // the snapshot pointed into the folder under its old name
        string xml = File.ReadAllText(Path.Combine(dead, "project.srproj")).Replace(own, dead);
        File.WriteAllText(Path.Combine(dead, "project.srproj"), xml);
        var left = ProjectRecovery.FindLeftBehind();
        Check(left.Contains(dead), "a recovery copy whose app is gone is found");
        string savedAs; HashSet<string> temporary;
        var rec = ProjectRecovery.Read(dead, out savedAs, out temporary);
        var r7 = rec.FindMedia(7); var r8 = rec.FindMedia(8);
        Check(savedAs == path && rec.HasRange && rec.ClipCount == 1, "it reads back with where it was saved (" + Path.GetFileName(savedAs ?? "?") + ")");
        Check(temporary.Count == 1 && r7 != null && temporary.Contains(r7.Path) && File.Exists(r7.Path) && r7.Path.StartsWith(MediaLibrary.Folder) && Path.GetFileName(r7.Path) == "Freeze frame 1.png" && File.ReadAllBytes(r7.Path).Length == 5,
            "its cache-only media is back in the cache under its own name: " + (r7 != null ? Path.GetFileName(r7.Path) : "?"));
        Check(r8 != null && r8.Path == kept && !temporary.Contains(kept), "media with a real original still points at it");
        ProjectRecovery.Discard(dead);
        Check(!Directory.Exists(dead), "the recovery copy is removed once dealt with");

        // ---- recent projects
        var before = RecentProjects.List();
        string a = Path.Combine(work, "recent-a.srproj"), b = Path.Combine(work, "recent-b.srproj");
        File.WriteAllText(a, "x"); File.WriteAllText(b, "x");
        RecentProjects.Add(a); RecentProjects.Add(b); RecentProjects.Add(a);
        var list = RecentProjects.List();
        Check(list.Count >= 2 && list[0] == a && list[1] == b && list.FindAll(x => x == a).Count == 1, "recent projects are newest first with no repeats");
        File.Delete(b);
        Check(!RecentProjects.List().Contains(b), "a project whose file has gone drops off the list");
        // leave the list as it was found
        RecentProjects.Clear();
        for (int i = before.Count - 1; i >= 0; i--) RecentProjects.Add(before[i]);

        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}
