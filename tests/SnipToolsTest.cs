// Tests for Snip's capture modes (rectangle, window, full screen) and its annotation tools.
// Compiled together with the app's sources; needs a display (Xvfb):
//   tests/run.sh sniptools
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using StillsAndReels;

static class SnipToolsTest
{
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static void Mouse(Control c, string what, int x, int y, MouseButtons b = MouseButtons.Left)
    {
        c.GetType().GetMethod(what, BF).Invoke(c, new object[] { new MouseEventArgs(b, 1, x, y, 0) });
    }

    static void Drag(Control c, int x0, int y0, int x1, int y1)
    {
        Mouse(c, "OnMouseDown", x0, y0);
        for (int k = 1; k <= 10; k++) Mouse(c, "OnMouseMove", x0 + (x1 - x0) * k / 10, y0 + (y1 - y0) * k / 10);
        Mouse(c, "OnMouseUp", x1, y1);
    }

    static void Pump(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < until) { Application.DoEvents(); System.Threading.Thread.Sleep(15); } }

    /// <summary>A stand-in for a screenshot: grey, with two overlapping "windows" (red in front of blue).</summary>
    static Bitmap FakeScreen()
    {
        var b = new Bitmap(800, 600);
        using (var g = Graphics.FromImage(b))
        {
            g.Clear(Color.FromArgb(60, 60, 60));
            g.FillRectangle(Brushes.Blue, 100, 100, 400, 300);
            g.FillRectangle(Brushes.Red, 300, 200, 300, 250);
        }
        return b;
    }

    static void Selector()
    {
        var bounds = new Rectangle(0, 0, 800, 600);
        // window snip: the front-most window under the pointer wins
        using (var screen = FakeScreen())
        using (var sel = new SnipSelector(screen, SnipShape.Window, true, bounds))
        {
            sel.Windows.Add(new Rectangle(300, 200, 300, 250));   // front
            sel.Windows.Add(new Rectangle(100, 100, 400, 300));   // behind
            sel.Show(); Pump(150);
            Mouse(sel, "OnMouseMove", 350, 250);
            sel.Refresh();
            Mouse(sel, "OnMouseDown", 350, 250);
            Check(sel.Result != null && sel.ResultRect == new Rectangle(300, 200, 300, 250), "a window snip takes the window in front under the pointer");
            Check(sel.Result != null && sel.Result.GetPixel(5, 5).ToArgb() == Color.Red.ToArgb() && sel.Result.Width == 300 && sel.Result.Height == 250, "and it holds that window's pixels");
        }
        using (var screen = FakeScreen())
        using (var sel = new SnipSelector(screen, SnipShape.Window, true, bounds))
        {
            sel.Windows.Add(new Rectangle(300, 200, 300, 250));
            sel.Windows.Add(new Rectangle(100, 100, 400, 300));
            sel.Show(); Pump(100);
            Mouse(sel, "OnMouseDown", 150, 150);
            Check(sel.ResultRect == new Rectangle(100, 100, 400, 300), "pointing at the part of a window that shows takes that window");
        }
        using (var screen = FakeScreen())
        using (var sel = new SnipSelector(screen, SnipShape.Window, true, bounds))
        {
            sel.Windows.Add(new Rectangle(700, 500, 400, 400));   // hangs off the screen
            sel.Show(); Pump(100);
            Mouse(sel, "OnMouseDown", 750, 550);
            Check(sel.ResultRect == new Rectangle(700, 500, 100, 100), "a window partly off the screen is cut at the screen edge");
        }
        using (var screen = FakeScreen())
        using (var sel = new SnipSelector(screen, SnipShape.Window, true, bounds))
        {
            sel.Show(); Pump(100);
            Mouse(sel, "OnMouseDown", 20, 20);
            Check(sel.Result != null && sel.ResultRect.Width >= 3 && sel.ResultRect.Contains(20, 20), "pointing at the empty desktop takes the screen");
        }
        // rectangle snip still works, with the magnifier painted
        using (var screen = FakeScreen())
        using (var sel = new SnipSelector(screen, SnipShape.Rectangle, true, bounds))
        {
            sel.Show(); Pump(100);
            Mouse(sel, "OnMouseMove", 790, 590);    // magnifier flips away from the corner
            sel.Refresh();
            Mouse(sel, "OnMouseMove", 5, 5);
            sel.Refresh();
            Drag(sel, 110, 120, 310, 270);
            Check(sel.Result != null && sel.ResultRect == new Rectangle(110, 120, 200, 150), "a rectangle snip is exactly the dragged box");
        }
        // right click cancels
        using (var screen = FakeScreen())
        using (var sel = new SnipSelector(screen, SnipShape.Window, true, bounds))
        {
            sel.Show(); Pump(100);
            Mouse(sel, "OnMouseDown", 350, 250, MouseButtons.Right);
            Check(sel.Result == null && sel.DialogResult == DialogResult.Cancel, "right click cancels a window snip");
        }
        Check(ScreenWindows.List(IntPtr.Zero) != null, "listing windows never fails (empty off Windows)");
    }

    static void Annotate()
    {
        var snip = new SnipForm(null, null, null, null);
        snip.Show(); Pump(200);
        var t = typeof(SnipForm);
        t.GetMethod("SetSnip", BF).Invoke(snip, new object[] { FakeScreen() });
        Pump(200);
        var canvas = (ImageCanvas)t.GetField("canvas", BF).GetValue(snip);
        var session = (EditSession)t.GetField("session", BF).GetValue(snip);
        Action<EditTool> tool = et => t.GetMethod("SelectTool", BF).Invoke(snip, new object[] { et });
        Func<int> objects = () => session.State.Layers[0].Objects.Length;
        int w = canvas.Width, h = canvas.Height;
        Check(session != null && session.Size == new Size(800, 600) && !session.HasInk, "a new snip starts clean");

        tool(EditTool.Arrow); Drag(canvas, w / 4, h / 4, w / 2, h / 3);
        var arrow = objects() == 1 ? session.State.Layers[0].Objects[0] as ShapeObject : null;
        Check(arrow != null && arrow.Kind == ShapeKind.Arrow, "the arrow tool draws an arrow");

        tool(EditTool.Rect); Drag(canvas, w / 2, h / 2, w * 3 / 4, h * 3 / 4);
        var box = objects() == 2 ? session.State.Layers[0].Objects[1] as ShapeObject : null;
        Check(box != null && box.Kind == ShapeKind.Rect, "the box tool draws a rectangle");

        tool(EditTool.Step);
        Mouse(canvas, "OnMouseDown", w / 3, h * 2 / 3); Mouse(canvas, "OnMouseUp", w / 3, h * 2 / 3);
        Mouse(canvas, "OnMouseDown", w / 3 + 60, h * 2 / 3); Mouse(canvas, "OnMouseUp", w / 3 + 60, h * 2 / 3);
        var s1 = objects() == 4 ? session.State.Layers[0].Objects[2] as StepObject : null;
        var s2 = objects() == 4 ? session.State.Layers[0].Objects[3] as StepObject : null;
        Check(s1 != null && s2 != null && s1.Number == 1 && s2.Number == 2, "numbered steps count up");

        tool(EditTool.Text);
        Mouse(canvas, "OnMouseDown", w / 2, h / 5); Mouse(canvas, "OnMouseUp", w / 2, h / 5);
        Check(canvas.IsEditingText, "the text tool opens a box to type in");
        foreach (Control ch in canvas.Controls) if (ch is TextBox) ch.Text = "Look here";
        // copying while still typing must keep the words
        bool copied = (bool)t.GetMethod("CopySnip", BF).Invoke(snip, new object[] { true });
        var text = objects() == 5 ? session.State.Layers[0].Objects[4] as TextObject : null;
        Check(!canvas.IsEditingText && text != null && text.Text == "Look here", "copying finishes the text being typed (copied=" + copied + ")");

        using (var before = session.FlattenSurface())
        {
            tool(EditTool.Redact); Drag(canvas, w * 2 / 5, h * 2 / 5, w * 3 / 5, h * 3 / 5);
            using (var after = session.FlattenSurface())
            {
                bool changed = false;
                for (int y = 0; y < after.Height && !changed; y += 3)
                    for (int x = 0; x < after.Width; x += 3)
                        if (after.GetPixel(x, y) != before.GetPixel(x, y)) { changed = true; break; }
                Check(changed && session.UndoName != null, "the hide tool changes the pixels it is dragged over");
            }
        }
        int undoBefore = session.UndoCount;
        session.Undo();
        Check(session.UndoCount == undoBefore - 1, "each annotation is one undo step");

        tool(EditTool.Pen); Drag(canvas, w / 5, h * 4 / 5, w / 2, h * 4 / 5);
        Check(session.HasInk && session.IsDirty && snip.Text.EndsWith("*"), "pen lines still work and mark the snip as changed");

        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sniptools-test" + PicProject.Extension);
        PicProject.Save(session.State, file);
        var back = PicProject.Load(file);
        Check(back.Layers[0].Objects.Length == session.State.Layers[0].Objects.Length, "a snip saved with layers keeps its arrows and text editable");
        foreach (var l in back.Layers) if (l.Pixels != null) l.Pixels.Dispose();
        try { System.IO.File.Delete(file); } catch { }

        foreach (var name in new[] { "bWin", "bFull", "bArrow", "bBox", "bText", "bStep", "bHide", "bMove" })
            Check(t.GetField(name, BF).GetValue(snip) != null, "the Snip bar has the " + name.Substring(1) + " button");
        session.MarkSaved();
        snip.Close();
        Pump(100);
    }

    [STAThread]
    static int Main()
    {
        ImageCache.Initialize();
        Application.ThreadException += (s, e) => { Console.WriteLine("FAIL exception: " + e.Exception); fails++; };
        try { Selector(); Annotate(); }
        catch (Exception ex) { Console.WriteLine("FAIL exception: " + (ex.InnerException ?? ex)); fails++; }
        ImageCache.Shutdown();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails;
    }
}
