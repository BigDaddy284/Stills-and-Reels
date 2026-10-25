// End-to-end editor export: puts one video on the timeline and exports it with the app's own ExportJob.
// usage: mono ExportTest.exe <StillsAndReels.exe> <source video> <out.mp4> <width> <height> <fps> <crf>
using System; using System.Collections; using System.Reflection; using System.Threading;
class T {
  static int Main(string[] a) {
    var asm = Assembly.LoadFrom(a[0]);
    var BF = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    Func<string,Type> Ty = n => asm.GetType("StillsAndReels."+n);
    Ty("ImageCache").GetMethod("Initialize").Invoke(null,null);
    var info = Ty("FFmpeg").GetMethod("Probe").Invoke(null, new object[]{a[1]});
    var mi = Activator.CreateInstance(Ty("MediaItem"), true);
    Action<object,string,object> Set = (o,f,v) => o.GetType().GetField(f,BF).SetValue(o,v);
    Func<object,string,object> Get = (o,f) => o.GetType().GetField(f,BF).GetValue(o);
    Set(mi,"Id",1); Set(mi,"OriginalPath",a[1]); Set(mi,"Name","src");
    Set(mi,"Kind",Enum.Parse(Ty("MediaKind"),"Video")); Set(mi,"Status",Enum.Parse(Ty("MediaStatus"),"Ready")); Set(mi,"Info",info);
    var p = Activator.CreateInstance(Ty("Project"), true);
    Set(p,"Width",int.Parse(a[3])); Set(p,"Height",int.Parse(a[4])); Set(p,"Fps",double.Parse(a[5]));
    var clip = Activator.CreateInstance(Ty("Clip"), true);
    Set(clip,"Id",50); Set(clip,"MediaId",1); Set(clip,"Kind",Enum.Parse(Ty("ClipKind"),"Video"));
    Set(clip,"Duration",(double)Get(info,"Duration"));
    var tracks = (IList)Get(p,"Tracks"); var main = tracks[0];
    ((IList)Get(main,"Clips")).Add(clip);
    var dictT = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(int), Ty("MediaItem"));
    var dict = (IDictionary)Activator.CreateInstance(dictT); dict[1]=mi;
    var s = Activator.CreateInstance(Ty("ExportSettings"), true);
    Set(s,"OutPath",a[2]); Set(s,"Width",int.Parse(a[3])); Set(s,"Height",int.Parse(a[4])); Set(s,"Fps",double.Parse(a[5])); Set(s,"Crf",int.Parse(a[6]));
    var job = Activator.CreateInstance(Ty("ExportJob"), BF, null, new object[]{p, dict, s}, null);
    var done = new ManualResetEvent(false); Exception err = null;
    var ev = job.GetType().GetEvent("Completed");
    ev.AddEventHandler(job, (Action<Exception>)(e => { err = e; done.Set(); }));
    job.GetType().GetMethod("Start").Invoke(job,null);
    done.WaitOne(600000);
    Console.WriteLine(err == null ? "export OK" : "export FAILED: "+err);
    return err == null ? 0 : 1;
  }
}
