using System; using System.Reflection; using System.Windows.Forms;
class T {
  [STAThread] static int Main(string[] a) {
    var asm = Assembly.LoadFrom("/home/claude/PicturePreview/build/StillsAndReels.exe");
    var mf = asm.GetType("StillsAndReels.MainForm");
    var cache = asm.GetType("StillsAndReels.ImageCache"); cache.GetMethod("Initialize").Invoke(null,null);
    int errors = 0;
    Application.ThreadException += (s,e)=>{ errors++; Console.WriteLine("ERROR: "+e.Exception); };
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    var f = (Form)Activator.CreateInstance(mf, BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance, null, new object[]{null}, null);
    var t = new Timer{Interval=3000};
    int step=0;
    t.Tick += (s,e)=>{
      step++;
      if (step==1){ foreach (var m in a) mf.GetMethod(m, BindingFlags.NonPublic|BindingFlags.Instance).Invoke(f,null);
        Console.WriteLine("open forms: "+Application.OpenForms.Count); }
      else if (step==2){ try { f.Close(); } catch(Exception ex){ errors++; Console.WriteLine("ERROR: "+ex); }
        // since 2.5, closing the main window with snip windows open hides it and leaves them running
        Console.WriteLine("main closed: hidden="+(!f.Visible && !f.IsDisposed)+" forms left="+Application.OpenForms.Count); }
      else if (step==3){ // closing the snip windows themselves ends the app (Windows shutdown closes them all at once)
        var open = new System.Collections.Generic.List<Form>(); foreach (Form o in Application.OpenForms) if (!ReferenceEquals(o, f)) open.Add(o);
        foreach (var o in open) try { o.Close(); } catch(Exception ex){ errors++; Console.WriteLine("ERROR: "+ex); } }
      else { t.Stop(); Console.WriteLine("closed="+f.IsDisposed+" forms left="+Application.OpenForms.Count+" errors="+errors); if (!f.IsDisposed) errors++; Application.ExitThread(); }
    };
    t.Start();
    Application.Run(f.IsDisposed ? null : new ApplicationContext(f));
    if (step<4) Console.WriteLine("closed="+f.IsDisposed+" errors="+errors);
    return errors;
  }
}
