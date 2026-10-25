using System; using System.IO; using System.Reflection; using System.Drawing; using System.Collections.Generic;
// Pick-up logic that doesn't need Windows: path parsing, hidden-extension matching, picked-picture files.
class T {
  static BindingFlags SF = BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
  static int fails=0; static void Check(bool c,string n){ Console.WriteLine((c?"PASS ":"FAIL ")+n); if(!c) fails++; }
  static void Main(string[] a){
    var asm = Assembly.LoadFrom(a.Length>0?a[0]:"/home/claude/PicturePreview/build/StillsAndReels.exe");
    var cache=asm.GetType("StillsAndReels.ImageCache"); var pw=asm.GetType("StillsAndReels.PickupWatcher");
    cache.GetMethod("Initialize").Invoke(null,null);
    Func<string,object[],object> P=(m,xs)=>pw.GetMethod(m,SF).Invoke(null,xs);
    // file:// and Windows paths are recognised (drive check needs Windows, so only the shape is tested here)
    var uri=new Uri("file:///C:/Users/me/My%20Videos/clip.mp4");
    Check(uri.LocalPath.Replace('/','\\').EndsWith("My Videos\\clip.mp4"),"file uri decodes");
    Check(P("AbsolutePath",new object[]{"no path here"})==null,"plain name is not a path");
    // hidden extension: "holiday" in a folder holding holiday.mp4 and holiday.txt
    string dir=Path.Combine(Path.GetTempPath(),"pp-pick"); Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir,"holiday.txt"),"x"); File.WriteAllText(Path.Combine(dir,"holiday.mp4"),"x");
    File.WriteAllText(Path.Combine(dir,"cat.png"),"x");
    object[] args={dir,"holiday",true,null};
    string f=(string)pw.GetMethod("InFolder",SF).Invoke(null,args);
    Check(f!=null && f.EndsWith("holiday.mp4") && args[3].ToString()=="Video","hidden extension finds the video: "+f);
    args=new object[]{dir,"holiday",false,null};
    Check(pw.GetMethod("InFolder",SF).Invoke(null,args)==null,"pictures mode ignores the video");
    args=new object[]{dir,"cat.png",false,null};
    Check(((string)pw.GetMethod("InFolder",SF).Invoke(null,args)??"").EndsWith("cat.png"),"full name finds the picture");
    args=new object[]{dir,"missing.mp4",true,null};
    Check(pw.GetMethod("InFolder",SF).Invoke(null,args)==null,"missing file not reported");
    // names for picked pictures
    Check((string)P("Clean",new object[]{"a/b:c*?  d."})=="a b c d","alt text cleaned for a file name");
    // picked pictures saved as PNG in the cache and recognised
    var bmp=new Bitmap(50,40); using(var g=Graphics.FromImage(bmp)) g.Clear(Color.Orange);
    string p1=(string)cache.GetMethod("SavePicked").Invoke(null,new object[]{bmp,"Copied picture"});
    string p2=(string)cache.GetMethod("SavePicked").Invoke(null,new object[]{bmp,"Copied picture"});
    Check(File.Exists(p1)&&File.Exists(p2)&&p1!=p2,"picked pictures get unique files");
    Check((bool)cache.GetMethod("IsPicked").Invoke(null,new object[]{p1}),"picked picture recognised");
    Check(!(bool)cache.GetMethod("IsPicked").Invoke(null,new object[]{"/tmp/pp/landscape.jpg"}),"normal file not picked");
    for(int i=0;i<8;i++) cache.GetMethod("SavePicked").Invoke(null,new object[]{bmp,"x"});
    Check(Directory.GetFiles(Path.GetDirectoryName(p1)).Length<=5,"only the latest few picked pictures kept");
    long h1=(long)P("Hash",new object[]{bmp}), h2=(long)P("Hash",new object[]{new Bitmap(bmp)});
    long h3=(long)P("Hash",new object[]{new Bitmap(bmp.Width,bmp.Height)});
    Check(h1==h2 && h1!=h3,"same picture same hash, different picture different hash");
    // pictures found from the screen: a "web page" with text and two photos side by side
    var page=new Bitmap(900,600); var rnd=new Random(1);
    using(var g=Graphics.FromImage(page)){ g.Clear(Color.White);
      for(int i=0;i<12;i++) g.DrawString("Some text on the page that is not a picture at all "+i, new Font(FontFamily.GenericSansSerif,11), Brushes.Black, 20, 20+i*20); }
    Action<Rectangle> photo = rc => { for(int y=rc.Top;y<rc.Bottom;y++) for(int x=rc.Left;x<rc.Right;x++)
        page.SetPixel(x,y,Color.FromArgb((x*3+rnd.Next(60))%256,(y*2+rnd.Next(60))%256,(x+y+rnd.Next(80))%256)); };
    var ph1=new Rectangle(100,300,300,200); var ph2=new Rectangle(404,300,250,200); photo(ph1); photo(ph2);
    var LooksLikePhoto=pw.GetMethod("LooksLikePhoto",SF); var PictureBlock=pw.GetMethod("PictureBlock",SF);
    var blk=(Rectangle)PictureBlock.Invoke(null,new object[]{page,new Point(200,400)});
    Check(blk==ph1,"picture block found exactly: "+blk);
    Check((bool)LooksLikePhoto.Invoke(null,new object[]{page,blk}),"photo looks like a photo");
    var blk2=(Rectangle)PictureBlock.Invoke(null,new object[]{page,new Point(500,350)});
    Check(blk2==ph2,"neighbouring picture separated by its gap: "+blk2);
    var txt=(Rectangle)PictureBlock.Invoke(null,new object[]{page,new Point(60,30)});
    Check(txt.IsEmpty || !(bool)LooksLikePhoto.Invoke(null,new object[]{page,txt}),"text is not a picture: "+txt);
    Check(((Rectangle)PictureBlock.Invoke(null,new object[]{page,new Point(800,100)})).IsEmpty,"plain background gives nothing");
    Check(!(bool)LooksLikePhoto.Invoke(null,new object[]{page,new Rectangle(0,0,900,280)}),"text area with background is not a photo");
    var desk=(System.Collections.Generic.List<string>)pw.GetMethod("DesktopFolders",SF).Invoke(null,null);
    Check(desk!=null,"desktop folders listed: "+desk.Count);
    cache.GetMethod("Shutdown").Invoke(null,null);
    Console.WriteLine(fails==0?"ALL PASSED":fails+" FAILED");
  }
}
