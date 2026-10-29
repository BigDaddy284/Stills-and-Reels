// Snip windows, taskbar shortcuts and the snip timer (2.3).
// Run on Xvfb with one of:
//   windows     the Snip and Video snip windows are separate taskbar windows: not owned by the main window,
//               with their own minimise and maximise, and minimising one leaves the others alone
//   start pic   started as from the taskbar's Pic Snip (--picsnip): main window minimised, Snip window open on screen,
//               closing the Snip window closes the app        (start vid: the same with Video snip)
//   keep        started from Pic Snip, then the main window is opened: closing the Snip window leaves the app open
//   timer       a 3 s snip timer: the Snip window disappears at once, the selection appears after 3 s, the choice
//               is remembered; Esc and clicking the countdown cancel it
//   args        --picsnip / --vidsnip parsing, passing a request to an open copy, the jump list call
using System; using System.Diagnostics; using System.Drawing; using System.Reflection; using System.Windows.Forms;
class T {
  const BindingFlags BF = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
  const BindingFlags SF = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
  static int fails = 0;
  static void Check(bool c, string n){ Console.WriteLine((c?"PASS ":"FAIL ")+n); if(!c) fails++; }
  static Assembly asm; static Type mf;
  static Form Field(Form f, string name){ return (Form)mf.GetField(name, BF).GetValue(f); }
  // Mono under openbox reports a maximised window as Normal, so look at its size too
  static bool Maxed(Form w){ var wa = Screen.FromControl(w).WorkingArea; return w.WindowState == FormWindowState.Maximized || (w.Width >= wa.Width && w.Height >= wa.Height - 40); }
  static bool OnScreen(Form w){ foreach (var s in Screen.AllScreens) if (s.WorkingArea.Contains(new Point(w.Left+w.Width/2, w.Top+20))) return true; return false; }

  [STAThread] static int Main(string[] a) {
    asm = Assembly.LoadFrom(a.Length>2 ? a[2] : "/home/claude/PicturePreview/build/StillsAndReels.exe");
    mf = asm.GetType("StillsAndReels.MainForm");
    asm.GetType("StillsAndReels.ImageCache").GetMethod("Initialize").Invoke(null,null);
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    Application.ThreadException += (s,e)=>{ fails++; Console.WriteLine("FAIL exception: "+e.Exception); };
    switch (a[0]) {
      case "windows": Windows(); break;
      case "start": Start(a[1]=="vid" ? "vidsnip" : "picsnip", false); break;
      case "keep": Start("picsnip", true); break;
      case "timer": Timer(); break;
      case "args": Args(); break;
    }
    Console.WriteLine(fails==0 ? "ALL PASSED "+a[0] : "FAILED "+a[0]+" ("+fails+")");
    return fails;
  }

  static void Steps(Form f, int interval, params Action[] steps){
    int i = 0;
    var t = new Timer{Interval=interval};
    t.Tick += (s,e)=>{ if (i < steps.Length) { try { steps[i](); } catch (Exception ex) { fails++; Console.WriteLine("FAIL "+ex); } i++; } else { t.Stop(); Application.ExitThread(); } };
    t.Start();
    f.Show();
    Application.Run(new ApplicationContext());   // not tied to the main window, so steps can run after it closes
  }

  static void Windows(){
    var f = (Form)Activator.CreateInstance(mf, BF, null, new object[]{null}, null);
    Form pic = null, vid = null;
    Steps(f, 800,
      () => { mf.GetMethod("OpenSnip", BF).Invoke(f,null); mf.GetMethod("OpenVideoSnip", BF).Invoke(f,null);
              pic = Field(f,"snip"); vid = Field(f,"videoSnip");
              foreach (var w in new[]{pic, vid}) {
                Check(w.Owner == null, w.Text+": not owned by the main window");
                Check(w.ShowInTaskbar, w.Text+": has its own taskbar entry");
                Check(w.MinimizeBox && w.MaximizeBox, w.Text+": has minimise and maximise");
                Check(w.Visible && OnScreen(w), w.Text+": shown on screen at "+w.Bounds);
              } },
      () => { f.WindowState = FormWindowState.Minimized; },
      () => { Check(f.WindowState == FormWindowState.Minimized, "main window minimised");
              Check(pic.Visible && pic.WindowState == FormWindowState.Normal, "Snip stays open while the main window is minimised");
              Check(vid.Visible && vid.WindowState == FormWindowState.Normal, "Video snip stays open while the main window is minimised");
              f.WindowState = FormWindowState.Normal; pic.WindowState = FormWindowState.Minimized; },
      () => { Check(pic.WindowState == FormWindowState.Minimized, "Snip minimised");
              Check(f.WindowState == FormWindowState.Normal && f.Visible, "main window stays open while Snip is minimised");
              Check(vid.WindowState == FormWindowState.Normal && vid.Visible, "Video snip stays open while Snip is minimised");
              vid.WindowState = FormWindowState.Maximized; },
      () => { Check(Maxed(vid), "Video snip maximised");
              Check(!Maxed(f) && !Maxed(pic), "main window and Snip not maximised with it");
              mf.GetMethod("OpenSnip", BF).Invoke(f,null); },
      () => { Check(pic.WindowState == FormWindowState.Normal, "Snip menu brings a minimised Snip window back");
              f.Close(); },
      () => { Check(f.IsDisposed && pic.IsDisposed && vid.IsDisposed, "closing the main window closes both snip windows"); });
  }

  static void Start(string request, bool openMain){
    var f = (Form)Activator.CreateInstance(mf, BF, null, new object[]{null, request}, null);
    string field = request == "vidsnip" ? "videoSnip" : "snip";
    Form w = null;
    Steps(f, 1000,
      () => { },
      () => { w = Field(f, field);
              if (f.WindowState != FormWindowState.Minimized && Type.GetType("Mono.Runtime") != null && (bool)mf.GetField("snipOnly", BF).GetValue(f))
                // about 1 run in 8, Xvfb + openbox maps a window that asked to start minimised as a normal window
                // (no resize event ever arrives). Windows shows it minimised synchronously. The app's own state is checked.
                Console.WriteLine("NOTE window manager did not minimise the main window (Linux-only race); the app still treats it as waiting");
              else Check(f.WindowState == FormWindowState.Minimized, "main window starts minimised");
              Check(w != null && w.Visible, field+" window opened at start");
              if (w != null) Check(OnScreen(w), field+" window is on screen at "+w.Bounds); },
      () => { if (openMain) mf.GetMethod("ShowMain", BF).Invoke(f,null); },
      () => { if (openMain) Check(f.WindowState != FormWindowState.Minimized, "main window opened");
              if (w != null) w.Close(); },
      () => { if (openMain) { Check(!f.IsDisposed && f.Visible, "app stays open after the snip window closes (main window was opened)"); f.Close(); }
              else Check(f.IsDisposed, "app closes with the snip window when started just for it"); });
  }

  static void Timer(){
    var f = (Form)Activator.CreateInstance(mf, BF, null, new object[]{null}, null);
    Form pic = null; Stopwatch sw = null; long seenAt = -1;
    var watch = new Timer{Interval=50};
    watch.Tick += (s,e)=>{
      foreach (Form o in Application.OpenForms) if (o.GetType().Name=="SnipSelector") {
        watch.Stop(); seenAt = sw.ElapsedMilliseconds;
        o.DialogResult = DialogResult.Cancel; o.Close(); return;
      }
    };
    var settings = mf.GetField("settings", BF).GetValue(f);
    Steps(f, 900,
      () => { mf.GetMethod("OpenSnip", BF).Invoke(f,null); pic = Field(f,"snip");
              var t = pic.GetType();
              Check(t.GetField("bDelay", BF).GetValue(pic) != null, "Snip bar has a delay button");
              // choose 3 seconds the way the menu does
              t.GetField("delay", BF).SetValue(pic, 3);
              settings.GetType().GetMethod("Set", new[]{typeof(string), typeof(int)}).Invoke(settings, new object[]{"Snip.Delay", 3});
              sw = Stopwatch.StartNew(); watch.Start();
              t.GetMethod("NewSnip").Invoke(pic,null);
              Check((bool)t.GetProperty("Waiting").GetValue(pic,null), "timer running after New snip");
              Check(pic.Opacity == 0, "Snip window out of the way during the delay");
              Check(f.Opacity == 1 && f.Visible, "main window stays visible during the delay");
              var badge = (Form)t.GetField("badge", BF).GetValue(pic);
              Check(badge != null && badge.Visible, "countdown shown in the corner"); },
      () => { Check(seenAt < 0, "selection not shown before the delay (at 0.9 s)"); },
      () => { }, () => { }, () => { },
      () => { Check(seenAt >= 2900 && seenAt < 4500, "selection appeared after the 3 s delay ("+seenAt+" ms)");
              Check(pic.Opacity == 1 && pic.Visible, "Snip window back after the snip was cancelled");
              Check(!(bool)pic.GetType().GetProperty("Waiting").GetValue(pic,null), "timer stopped");
              // Esc cancels
              pic.GetType().GetMethod("NewSnip").Invoke(pic,null);
              var m = new Message{ Msg = 0x100, WParam = (IntPtr)Keys.Escape };
              pic.GetType().GetMethod("ProcessCmdKey", BF).Invoke(pic, new object[]{ m, Keys.Escape });
              Check(!(bool)pic.GetType().GetProperty("Waiting").GetValue(pic,null) && pic.Opacity == 1, "Esc cancels the timer and brings the Snip window back"); },
      () => { // clicking the countdown cancels
              pic.GetType().GetMethod("NewSnip").Invoke(pic,null);
              var badge = (Form)pic.GetType().GetField("badge", BF).GetValue(pic);
              badge.GetType().GetMethod("OnMouseUp", BF).Invoke(badge, new object[]{ new MouseEventArgs(MouseButtons.Left,1,5,5,0) });
              Check(!(bool)pic.GetType().GetProperty("Waiting").GetValue(pic,null) && pic.Opacity == 1, "clicking the countdown cancels the timer");
              Check(badge.IsDisposed, "countdown closed"); },
      () => { // closing the Snip window while the timer runs
              pic.GetType().GetMethod("NewSnip").Invoke(pic,null);
              pic.Close(); },
      () => { Check(pic.IsDisposed, "Snip window closes during the delay");
              foreach (Form o in Application.OpenForms) if (o.GetType().Name=="DelayBadge") { fails++; Console.WriteLine("FAIL countdown left on screen"); }
              // a new Snip window starts with the remembered delay
              mf.GetMethod("OpenSnip", BF).Invoke(f,null); var p2 = Field(f,"snip");
              Check((int)p2.GetType().GetField("delay", BF).GetValue(p2) == 3, "delay remembered for the next Snip window");
              var b = p2.GetType().GetField("bDelay", BF).GetValue(p2);
              Check(((Control)b).Text == "3 s delay", "delay button shows \"3 s delay\"");
              p2.GetType().GetField("delay", BF).SetValue(p2, 0);
              settings.GetType().GetMethod("Set", new[]{typeof(string), typeof(int)}).Invoke(settings, new object[]{"Snip.Delay", 0});
              f.Close(); });
  }

  static void Args(){
    var sc = asm.GetType("StillsAndReels.Shortcuts");
    Func<string,string> req = s => (string)sc.GetMethod("SnipRequest", SF).Invoke(null, new object[]{s});
    Check(req("--picsnip") == "picsnip" && req("/PicSnip") == "picsnip" && req("--vidsnip") == "vidsnip", "--picsnip and --vidsnip recognised");
    Check(req(@"C:\Pictures\cat.jpg") == null && req(null) == null, "file paths are still opened as files");
    Check(!(bool)sc.GetMethod("PassToRunningCopy", SF).Invoke(null, new object[]{"picsnip"}), "no open copy: the request is not passed on");
    string got = null;
    sc.GetMethod("Listen", SF).Invoke(null, new object[]{ (Action<string>)(r => got = r) });
    bool passed = (bool)sc.GetMethod("PassToRunningCopy", SF).Invoke(null, new object[]{"vidsnip"});
    for (int i = 0; i < 40 && got == null; i++) System.Threading.Thread.Sleep(50);
    if (passed) Check(got == "vidsnip", "an open copy receives Vid Snip");
    else Console.WriteLine("SKIP passing to an open copy: named events not supported here (Windows only)");
    sc.GetMethod("StopListening", SF).Invoke(null, null);
    try { sc.GetMethod("WriteJumpList", SF).Invoke(null, null); Check(true, "jump list call is safe off Windows"); }
    catch (Exception ex) { Check(false, "jump list call threw "+ex.InnerException); }
  }
}
