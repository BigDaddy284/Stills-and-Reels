using System; using System.Reflection; using System.Windows.Forms;
class T {
  [STAThread] static int Main(string[] a) {
    var asm = Assembly.LoadFrom("/home/claude/PicturePreview/build/StillsAndReels.exe");
    var mf = asm.GetType("StillsAndReels.MainForm");
    asm.GetType("StillsAndReels.ImageCache").GetMethod("Initialize").Invoke(null,null);
    var BF = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    var f = (Form)Activator.CreateInstance(mf, BF, null, new object[]{null}, null);
    int fails = 0; bool seen = false;
    var t = new Timer{Interval=1500}; int step=0;
    var watch = new Timer{Interval=100};
    watch.Tick += (s,e)=>{
      foreach (Form o in Application.OpenForms) if (o.GetType().Name=="SnipSelector") {
        watch.Stop(); seen = true;
        var snip = (Form)mf.GetField(a[0]=="video"?"videoSnip":"snip", BF).GetValue(f);
        Console.WriteLine("during selection: main opacity="+f.Opacity+" visible="+f.Visible+"; snip window opacity="+snip.Opacity);
        if (f.Opacity!=1 || !f.Visible) { fails++; Console.WriteLine("FAIL main window hidden"); }
        if (snip.Opacity!=0) { fails++; Console.WriteLine("FAIL snip window still showing"); }
        o.DialogResult = DialogResult.Cancel; o.Close(); return;
      }
    };
    t.Tick += (s,e)=>{
      step++;
      if (step==1){
        t.Stop();
        mf.GetMethod(a[0]=="video"?"OpenVideoSnip":"OpenSnip", BF).Invoke(f,null);
        var snip = (Form)mf.GetField(a[0]=="video"?"videoSnip":"snip", BF).GetValue(f);
        watch.Start();
        if (a[0]=="video") snip.GetType().GetMethod("PickRegion", BF).Invoke(snip,null);
        else snip.GetType().GetMethod("NewSnip").Invoke(snip,null);
        Console.WriteLine("after cancel: main opacity="+f.Opacity+"; snip window opacity="+snip.Opacity+" visible="+snip.Visible);
        if (snip.Opacity!=1 || !snip.Visible) { fails++; Console.WriteLine("FAIL snip window not restored"); }
        if (!seen) { fails++; Console.WriteLine("FAIL selector never appeared"); }
        f.Close(); Application.ExitThread();
      }
    };
    t.Start();
    Application.Run(new ApplicationContext(f));
    Console.WriteLine(fails==0?"PASS "+a[0]:"FAILED "+a[0]);
    return fails;
  }
}
