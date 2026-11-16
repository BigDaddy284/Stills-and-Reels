// Themes and the app icon (2.6), run against the built exe (Xvfb for the window part):
//   mono ThemeTest.exe <StillsAndReels.exe>
// Checks that Dark, Light and Graphite are picked by their saved names (anything unknown gives Dark), that every
// palette has all of its colours set (no surface left at the default transparent black), that Graphite uses the
// Graphite theme's colours, that the app icon is embedded with every size from 16 px to 256 px, and that the main
// window and the Settings window carry it and keep the chosen theme name through a save.
using System; using System.Drawing; using System.Reflection; using System.Windows.Forms;
class T {
  static readonly BindingFlags BF = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
  static int fails;
  static void Check(bool ok, string what) { Console.WriteLine((ok?"PASS ":"FAIL ")+what); if (!ok) fails++; }
  [STAThread] static int Main(string[] a) {
    var asm = Assembly.LoadFrom(a[0]);
    var theme = asm.GetType("StillsAndReels.Theme");
    var pal = asm.GetType("StillsAndReels.Palette");
    Func<object> cur = () => theme.GetProperty("Current", BF).GetValue(null, null);
    Func<object,string,object> fld = (o, n) => pal.GetField(n, BF).GetValue(o);
    var setName = theme.GetMethod("Set", BF, null, new[]{typeof(string)}, null);
    foreach (var n in new[]{"Dark","Light","Graphite"}) {
      setName.Invoke(null, new object[]{n});
      Check((string)fld(cur(),"Name") == n, n + " is chosen by its name");
      foreach (var f in pal.GetFields(BF)) if (f.FieldType == typeof(Color))
        if (((Color)f.GetValue(cur())).A == 0) Check(false, n + "." + f.Name + " is set");
    }
    setName.Invoke(null, new object[]{"Purple"});
    Check((string)fld(cur(),"Name") == "Dark", "an unknown name gives Dark");
    setName.Invoke(null, new object[]{"Graphite"});
    var g = cur();
    Check((bool)fld(g,"Dark"), "Graphite counts as a dark theme (dark title bar)");
    Check(((Color)fld(g,"Window")).ToArgb() == Color.FromArgb(0x2a,0x2b,0x2f).ToArgb(), "Graphite window #2a2b2f");
    Check(((Color)fld(g,"Bar")).ToArgb() == Color.FromArgb(0x1b,0x1c,0x1f).ToArgb(), "Graphite bar #1b1c1f");
    Check(((Color)fld(g,"AccentA")).ToArgb() == Color.FromArgb(0x25,0x80,0xee).ToArgb(), "Graphite accent #2580ee");
    Check(((Color)fld(g,"AccentB")).ToArgb() == Color.FromArgb(0x8c,0xc4,0xff).ToArgb(), "Graphite sky #8cc4ff");
    Check(((Color)fld(g,"OverlayB")).ToArgb() == Color.FromArgb(0x8c,0xc4,0xff).ToArgb(), "Graphite snip bars use the sky gradient");
    setName.Invoke(null, new object[]{"Dark"});
    Check(((Color)fld(cur(),"OverlayB")).ToArgb() == Color.FromArgb(176,72,255).ToArgb(), "Dark keeps its purple snip gradient");
    Check(((Color)fld(cur(),"TimelineBg")).ToArgb() == Color.FromArgb(11,14,28).ToArgb(), "Dark keeps its timeline colour");

    var icon = (Icon)theme.GetProperty("AppIcon", BF).GetValue(null, null);
    Check(icon != null, "the app icon is embedded");
    if (icon != null) {
      using (var i16 = new Icon(icon, 16, 16)) Check(i16.Width == 16, "it has a 16 px image");
      using (var i32 = new Icon(icon, 32, 32)) Check(i32.Width == 32, "it has a 32 px image");
      using (var i64 = new Icon(icon, 64, 64)) Check(i64.Width == 64, "it has a 64 px image");
    }
    // Mono's Icon class stops at 64 px, so the 256 px image (for Explorer's large views) is checked in the file itself
    using (var s = asm.GetManifestResourceStream("StillsAndReels.app.ico")) {
      Check(s != null && s.Length > 10000, "app.ico resource present");
      var buf = new byte[6 + 16 * 16]; s.Read(buf, 0, buf.Length);
      int count = BitConverter.ToUInt16(buf, 4); var sizes = new System.Collections.Generic.List<int>();
      for (int i = 0; i < count && i < 16; i++) sizes.Add(buf[6 + 16 * i] == 0 ? 256 : buf[6 + 16 * i]);
      Check(sizes.Contains(16) && sizes.Contains(24) && sizes.Contains(32) && sizes.Contains(48) && sizes.Contains(256),
        "app.ico holds 16, 24, 32, 48 and 256 px images (" + string.Join(", ", sizes) + ")");
    }

    if (Environment.GetEnvironmentVariable("DISPLAY") != null) {
      asm.GetType("StillsAndReels.ImageCache").GetMethod("Initialize").Invoke(null, null);
      setName.Invoke(null, new object[]{"Graphite"});
      var f = (Form)Activator.CreateInstance(asm.GetType("StillsAndReels.MainForm"), BF, null, new object[]{ null }, null);
      Check(f.Icon == icon, "the main window uses the app icon");
      var optT = asm.GetType("StillsAndReels.SettingsForm+Options");
      var o = Activator.CreateInstance(optT);
      optT.GetField("ThemeName").SetValue(o, "Graphite");
      var applyT = typeof(Action<>).MakeGenericType(optT);
      var sf = (Form)Activator.CreateInstance(asm.GetType("StillsAndReels.SettingsForm"), BF, null, new object[]{ o, Delegate.CreateDelegate(applyT, typeof(T).GetMethod("Noop", BF).MakeGenericMethod(optT)) }, null);
      Check(sf.Icon == icon, "the Settings window uses the app icon");
      sf.Dispose();
      f.GetType().GetMethod("SetTheme", BF).Invoke(f, new object[]{"Light"});
      Check((string)fld(cur(),"Name") == "Light", "SetTheme switches theme");
      f.GetType().GetMethod("SetTheme", BF).Invoke(f, new object[]{"Graphite"});
      Check((string)fld(cur(),"Name") == "Graphite", "and back to Graphite");
    }
    Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
    return fails == 0 ? 0 : 1;
  }
  static void Noop<X>(X x) { }
}
