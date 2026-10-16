using System; using System.IO; using System.Reflection; using System.Drawing; using System.Collections.Generic;
class T {
  static object S(Type t, string m, params object[] a){ return t.GetMethod(m, BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Invoke(null,a); }
  static int fails=0; static void Check(bool c,string n){ Console.WriteLine((c?"PASS ":"FAIL ")+n); if(!c) fails++; }
  static void Main(){
    var asm = Assembly.LoadFrom("/home/claude/PicturePreview/build/StillsAndReels.exe");
    var cache=asm.GetType("StillsAndReels.ImageCache"); var li=asm.GetType("StillsAndReels.LoadedImage");
    var ft=asm.GetType("StillsAndReels.FileTypes"); var es=asm.GetType("StillsAndReels.EditSession");
    var st=asm.GetType("StillsAndReels.Stroke"); var tool=asm.GetType("StillsAndReels.EditTool"); var saver=asm.GetType("StillsAndReels.ImageSaver");
    S(cache,"Initialize");
    Check(S(ft,"Classify","a.JPG").ToString()=="Image","jpg is image");
    Check(S(ft,"Classify","a.heic").ToString()=="Incompatible","heic incompatible");
    Check(S(ft,"Classify","a.txt").ToString()=="Other","txt ignored");
    var img = S(li,"Load","/tmp/pp/rotated_exif.jpg");
    var image=(Image)li.GetField("Image").GetValue(img); string cp=(string)li.GetField("CachePath").GetValue(img);
    Check(image.Width==200 && image.Height==400, "EXIF orientation 6 rotated -> "+image.Width+"x"+image.Height);
    Check(File.Exists(cp) && cp.Contains("StillsAndReels"), "cache copy exists: "+cp);
    S(cache,"Delete",cp); Check(!File.Exists(cp),"cache copy deleted");
    var src = DateTime.Now; var before=File.GetLastWriteTimeUtc("/tmp/pp/landscape.jpg");
    var l2=S(li,"Load","/tmp/pp/landscape.jpg"); var im2=(Image)li.GetField("Image").GetValue(l2);
    dynamic sess = Activator.CreateInstance(es, BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic, null, new object[]{im2,"/tmp/pp/landscape.jpg"}, null);
    var pen = Enum.Parse(tool,"Pen");
    var stroke = Activator.CreateInstance(st, BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{pen, Color.Red, 20f, new PointF[]{new PointF(100,100), new PointF(400,100)}},null);
    es.GetMethod("AddStroke").Invoke(sess,new[]{stroke});
    Check((bool)es.GetProperty("IsDirty").GetValue(sess),"dirty after stroke");
    es.GetMethod("Crop").Invoke(sess,new object[]{new Rectangle(50,50,1000,500)});
    es.GetMethod("RotateFlip").Invoke(sess,new object[]{RotateFlipType.Rotate90FlipNone});
    var size=(Size)es.GetProperty("Size").GetValue(sess);
    Check(size.Width==500 && size.Height==1000,"crop+rotate size "+size);
    var flat=(Bitmap)es.GetMethod("Flatten").Invoke(sess,null);
    // stroke at image (100..400,100) -> after crop (50..350,50) -> rotate90: (H-y, x) = (500-50=450, 50..350)
    var px=flat.GetPixel(450,200); Check(px.R>200 && px.G<60,"ink follows crop+rotate, pixel="+px);
    foreach(var ext in new[]{".png",".jpg",".bmp",".gif",".tif"}){ string o="/tmp/pp/out"+ext; S(saver,"Save",flat,o); using(var r=Image.FromFile(o)) Check(r.Width==500&&r.Height==1000,"saved "+ext); }
    Check(File.GetLastWriteTimeUtc("/tmp/pp/landscape.jpg")==before,"original untouched");
    Console.WriteLine(fails==0?"ALL PASSED":fails+" FAILED");
  }
}
