// Runs ScreenRecorder.Finish on two lossless segments made with the app's own capture settings.
using System; using System.Collections.Generic; using System.Diagnostics; using System.Reflection;
class T { static int Main(string[] a) {
  var asm = Assembly.LoadFrom(a[0]); var BF = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
  Func<string,Type> Ty = n => asm.GetType("StillsAndReels."+n);
  Ty("ImageCache").GetMethod("Initialize").Invoke(null,null);
  string folder = (string)Ty("MediaLibrary").GetProperty("Folder", BF).GetValue(null);
  string cap = (string)Ty("VideoQuality").GetField("LosslessCapture", BF).GetValue(null);
  var segs = new List<string>();
  for (int i = 0; i < 2; i++) {
    string seg = System.IO.Path.Combine(folder, "rec-test" + i + ".mkv");
    var p = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -f lavfi -i testsrc2=size=1366x768:rate=30:duration=2,format=bgra -f lavfi -i sine=d=2 -map 0:v -map 1:a -c:a pcm_s16le " + cap + "-g 60 -y \"" + seg + "\"") { UseShellExecute = false });
    p.WaitForExit(); segs.Add(seg);
  }
  var rec = Activator.CreateInstance(Ty("ScreenRecorder"), true);
  var list = (List<string>)rec.GetType().GetField("segments", BF).GetValue(rec); list.AddRange(segs);
  rec.GetType().GetField("before", BF).SetValue(rec, 4.0);
  double last = 0; int calls = 0;
  string path = (string)rec.GetType().GetMethod("Finish").Invoke(rec, new object[]{ (Action<double>)(x => { last = x; calls++; }) });
  Console.WriteLine("finished: " + path + " progress calls=" + calls + " last=" + last.ToString("0.00") + " segments left=" + list.Count + " seg0 exists=" + System.IO.File.Exists(segs[0]));
  return 0;
}}
