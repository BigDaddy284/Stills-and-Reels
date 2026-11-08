// Scrubbing and playback of the video editor, run against the built exe.
//   mono ScrubTest.exe <StillsAndReels.exe> <video> scrub   frames while dragging: StillFrames must hand out
//                                                            consecutive frames far faster than one FFmpeg run each
//   mono ScrubTest.exe <StillsAndReels.exe> <video> play    the playhead must move while the preview plays
//                                                            (run on Xvfb: DISPLAY=:99)
//   mono ScrubTest.exe <StillsAndReels.exe> <video> coarse  a fast drag over a long video (a second or more per
//                                                            mouse event): each frame must come from a keyframe-only
//                                                            grab, far quicker than decoding up to it (use a video of
//                                                            20 minutes or more, e.g. tests make one with testsrc2)
using System; using System.Collections; using System.Collections.Generic; using System.Diagnostics; using System.Drawing;
using System.Reflection; using System.Threading; using System.Windows.Forms;
class T {
  static Assembly asm;
  static readonly BindingFlags BF = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
  static Type Ty(string n) { return asm.GetType("StillsAndReels."+n); }
  static void Set(object o, string f, object v) { o.GetType().GetField(f,BF).SetValue(o,v); }
  static object Get(object o, string f) { return o.GetType().GetField(f,BF).GetValue(o); }
  static object Prop(object o, string p) { return o.GetType().GetProperty(p,BF).GetValue(o,null); }
  static object Call(object o, string m, params object[] a) { return o.GetType().GetMethod(m,BF).Invoke(o,a); }
  static int fails;
  static void Check(bool ok, string what) { Console.WriteLine((ok?"PASS ":"FAIL ")+what); if (!ok) fails++; }

  [STAThread] static int Main(string[] a) {
    asm = Assembly.LoadFrom(a[0]);
    Ty("ImageCache").GetMethod("Initialize").Invoke(null,null);
    if (a[2] == "scrub") Scrub(a[1]); else if (a[2] == "coarse") Coarse(a[1]); else Play(a[1]);
    Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }

  static object MediaFor(string video, out object info) {
    info = Ty("FFmpeg").GetMethod("Probe").Invoke(null, new object[]{video});
    var mi = Activator.CreateInstance(Ty("MediaItem"), true);
    Set(mi,"Id",1); Set(mi,"OriginalPath",video); Set(mi,"Name","src");
    Set(mi,"Kind",Enum.Parse(Ty("MediaKind"),"Video")); Set(mi,"Status",Enum.Parse(Ty("MediaStatus"),"Ready")); Set(mi,"Info",info);
    return mi;
  }

  static object ClipFor(object info) {
    var clip = Activator.CreateInstance(Ty("Clip"), true);
    Set(clip,"Id",50); Set(clip,"MediaId",1); Set(clip,"Kind",Enum.Parse(Ty("ClipKind"),"Video"));
    Set(clip,"Duration",(double)Get(info,"Duration"));
    return clip;
  }

  // Drags the playhead over 45 consecutive frames (1.5 s at 30 fps), live, then asks for the sharp frame.
  static void Scrub(string video) {
    object info; var mi = MediaFor(video, out info);
    var clip = ClipFor(info);
    double fps = (double)Get(info,"Fps");
    var frames = Activator.CreateInstance(Ty("StillFrames"), true);
    Set(frames,"Live",true);
    var size = new Size(640, 360);
    var get = frames.GetType().GetMethod("Get");
    var sw = Stopwatch.StartNew();
    var first = (Bitmap)get.Invoke(frames, new object[]{clip, mi, 0.5, size});
    double tFirst = sw.Elapsed.TotalMilliseconds;
    Check(first != null, "first frame while dragging arrives (" + (int)tFirst + " ms)");
    int got = 0; double worst = 0, total = 0;
    Bitmap prev = first;
    int changed = 0;
    for (int i = 1; i <= 45; i++) {
      sw.Restart();
      var b = (Bitmap)get.Invoke(frames, new object[]{clip, mi, 0.5 + i / fps, size});
      double ms = sw.Elapsed.TotalMilliseconds;
      if (b != null) { got++; if (!ReferenceEquals(b, prev)) changed++; prev = b; }
      total += ms; if (ms > worst) worst = ms;
    }
    Console.WriteLine("45 consecutive frames: " + got + " delivered, " + changed + " distinct, average " + (int)(total/45) + " ms, worst " + (int)worst + " ms");
    Check(got == 45, "every frame of the drag is delivered");
    Check(changed >= 40, "the frames differ (not one frame repeated)");
    Check(total / 45 < tFirst / 2, "a frame mid-drag costs far less than a cold grab (ahead-decoding works)");
    // a fine drag never falls back to keyframe stand-ins
    int standIns = 0;
    foreach (var k in ((System.Collections.IDictionary)Get(frames,"cache")).Keys) if (k.ToString().StartsWith("key|")) standIns++;
    Check(standIns == 0, "a frame-by-frame drag shows exact frames only (" + standIns + " keyframe stand-ins)");
    // dragging backwards over the same stretch comes from the cache
    sw.Restart();
    for (int i = 45; i >= 1; i--) get.Invoke(frames, new object[]{clip, mi, 0.5 + i / fps, size});
    Console.WriteLine("45 frames backwards over the same stretch: " + (int)sw.Elapsed.TotalMilliseconds + " ms in all");
    Check(sw.Elapsed.TotalMilliseconds < tFirst, "cached frames are instant");
    // the sharp frame after the drag
    Set(frames,"Live",false);
    sw.Restart();
    var sharp = (Bitmap)get.Invoke(frames, new object[]{clip, mi, 1.0, new Size(1280, 720)});
    Check(sharp != null && sharp.Width == 1280, "sharp frame at full size after the drag (" + (int)sw.Elapsed.TotalMilliseconds + " ms)");
    // past the end of the file still gives a picture
    double dur = (double)Get(info,"Duration");
    var last = (Bitmap)get.Invoke(frames, new object[]{clip, mi, dur + 0.5, size});
    Check(last != null, "a time past the end of the file still shows the last frame");
    Call(frames,"Dispose");
  }

  // Drags over a long video with a second or more between mouse events, as a zoomed-out timeline does.
  static void Coarse(string video) {
    object info; var mi = MediaFor(video, out info);
    var clip = ClipFor(info);
    double dur = (double)Get(info,"Duration");
    Check(dur >= 600, "the test video is long (" + (int)dur + " s)");
    var size = new Size(640, 360);
    var get = typeof(T).Assembly == null ? null : Ty("StillFrames").GetMethod("Get");
    // the old way, for comparison: one exact frame per position, decoded from the keyframe before it
    var plain = Activator.CreateInstance(Ty("StillFrames"), true);
    var sw = Stopwatch.StartNew();
    int n = 0;
    for (double t = dur * 0.8; t < dur * 0.8 + 12 && n < 12; t += 1.0, n++) get.Invoke(plain, new object[]{clip, mi, t, size});
    double exactMs = sw.Elapsed.TotalMilliseconds / n;
    Console.WriteLine("exact frames, 1 s apart, late in the file: " + (int)exactMs + " ms each");
    Call(plain,"Dispose");
    // live drag: the first position is a run (fine), from the second on the step is coarse
    var live = Activator.CreateInstance(Ty("StillFrames"), true);
    Set(live,"Live",true);
    get.Invoke(live, new object[]{clip, mi, dur * 0.8, size});
    get.Invoke(live, new object[]{clip, mi, dur * 0.8 + 1.3, size});
    sw.Restart();
    int got = 0, distinct = 0; Bitmap prev = null; double worst = 0;
    for (int i = 2; i < 42; i++) {
      var s1 = Stopwatch.StartNew();
      var b = (Bitmap)get.Invoke(live, new object[]{clip, mi, dur * 0.8 + i * 1.3, size});
      double ms = s1.Elapsed.TotalMilliseconds; if (ms > worst) worst = ms;
      if (b != null) { got++; if (!ReferenceEquals(b, prev)) distinct++; prev = b; }
    }
    double liveMs = sw.Elapsed.TotalMilliseconds / 40;
    Console.WriteLine("live drag, 1.3 s per step: " + got + "/40 frames, " + distinct + " distinct, " + (int)liveMs + " ms each, worst " + (int)worst + " ms");
    Check(got == 40, "every step of a coarse drag gets a frame");
    Check(distinct >= 24, "the frames change as the drag goes on: one per keyframe passed (" + distinct + " of about 26 with keyframes 2 s apart)");
    Check(liveMs < exactMs * 0.6, "a coarse-drag frame costs well under an exact frame");
    // dragging back over the same stretch is cached
    sw.Restart();
    for (int i = 41; i >= 2; i--) get.Invoke(live, new object[]{clip, mi, dur * 0.8 + i * 1.3, size});
    Console.WriteLine("back over the same 40 steps: " + (int)sw.Elapsed.TotalMilliseconds + " ms in all");
    Check(sw.Elapsed.TotalMilliseconds < exactMs * 2, "cached keyframes are instant");
    // the exact frame when the drag ends never reuses a keyframe stand-in
    Set(live,"Live",false);
    var exact = (Bitmap)get.Invoke(live, new object[]{clip, mi, dur * 0.8 + 10 * 1.3, size});
    var standIn = (Bitmap)get.Invoke(live, new object[]{clip, mi, dur * 0.8 + 10 * 1.3, size});
    Check(exact != null && ReferenceEquals(exact, standIn), "the exact frame is cached once the drag ends");
    bool differs = false;
    Set(live,"Live",true);
    // the stand-in for that moment was cached under its own key; find it by asking again with a coarse step
    get.Invoke(live, new object[]{clip, mi, dur * 0.8 + 30 * 1.3, size});
    var again = (Bitmap)get.Invoke(live, new object[]{clip, mi, dur * 0.8 + 10 * 1.3, size});
    differs = !ReferenceEquals(again, exact);
    Check(!differs, "once the exact frame exists, dragging over it shows the exact frame, not the keyframe");
    Call(live,"Dispose");
  }

  // Plays the timeline in a real VideoEditorView and watches the playhead.
  static void Play(string video) {
    var form = new Form { Width = 1400, Height = 900 };
    var ed = (Control)Activator.CreateInstance(Ty("VideoEditorView"), true);
    ed.Dock = DockStyle.Fill;
    form.Controls.Add(ed);
    var lib = Get(ed,"Library");
    form.Shown += (s, e) => {
      var m = Call(lib,"Add",video,0,false);
      var poll = new System.Windows.Forms.Timer { Interval = 100 };
      int phase = 0; var ticks = new List<double>(); var sw = new Stopwatch(); int waited = 0;
      poll.Tick += (s2, e2) => {
        try {
          if (phase == 0) {
            if (!(bool)Prop(m,"IsUsable") || Get(m,"Info") == null) { if (++waited > 300) { Check(false,"media never became usable"); form.Close(); } return; }
            Call(ed,"AddMediaToTimeline",m,false);
            Call(ed,"Play");
            sw.Start(); phase = 1; return;
          }
          if (phase == 1) {
            ticks.Add((double)Prop(ed,"Playhead"));
            if (sw.Elapsed.TotalSeconds > 2.5) {
              int advances = 0; for (int i = 1; i < ticks.Count; i++) if (ticks[i] > ticks[i-1] + 1e-6) advances++;
              Console.WriteLine("playhead samples over 2.5 s: " + ticks.Count + ", moved forward " + advances + " times, now at " + ticks[ticks.Count-1].ToString("0.00") + " s");
              Check((bool)Prop(ed,"IsPlaying"), "still playing");
              Check(ticks[ticks.Count-1] > 1.5, "the playhead has moved with the picture");
              Check(advances >= ticks.Count / 2, "the playhead moves steadily, not in a jump at the end");
              Call(ed,"Pause");
              phase = 2; sw.Restart(); return;
            }
          }
          if (phase == 2 && sw.Elapsed.TotalSeconds > 0.5) {
            Check(!(bool)Prop(ed,"IsPlaying"), "paused");
            // scrubbing from the UI: the playhead follows the drag and the view stays responsive
            double t0 = (double)Prop(ed,"Playhead");
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < 30; i++) Call(ed,"Scrub",0.2 + i/30.0,false,true);
            Call(ed,"EndScrub");
            Console.WriteLine("30 live Scrub calls took " + (int)sw2.Elapsed.TotalMilliseconds + " ms on the UI thread");
            Check(sw2.Elapsed.TotalMilliseconds < 500, "scrubbing never blocks the UI thread");
            Check(Math.Abs((double)Prop(ed,"Playhead") - (0.2 + 29/30.0)) < 1e-6, "the playhead is where the drag ended");
            poll.Stop(); form.Close();
          }
        } catch (Exception ex) { Console.WriteLine("ERROR " + ex); fails++; poll.Stop(); form.Close(); }
      };
      poll.Start();
    };
    Application.Run(form);
  }
}
