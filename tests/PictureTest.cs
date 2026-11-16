// Tests for the picture engine: pixel surfaces, layers, shapes, undo, selections and filters.
// Compiled together with the app's sources so it can use their types directly:
//   tests/run.sh picture
// No display is needed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using StillsAndReels;

static class PictureTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    static bool Near(Color c, int r, int g, int b, int tol = 3)
    {
        return Math.Abs(c.R - r) <= tol && Math.Abs(c.G - g) <= tol && Math.Abs(c.B - b) <= tol;
    }

    static PicSurface Solid(int w, int h, Color c)
    {
        var s = new PicSurface(w, h);
        s.Fill(s.Bounds, PicSurface.Premultiply(c));
        return s;
    }

    /// <summary>A picture whose colour says where each pixel is: red = x, green = y (both mod 256), blue 40.</summary>
    static unsafe PicSurface Gradient(int w, int h)
    {
        var s = new PicSurface(w, h);
        for (int y = 0; y < h; y++)
        {
            uint* p = s.Row(y);
            for (int x = 0; x < w; x++) p[x] = 0xFF000000u | ((uint)(x & 255) << 16) | ((uint)(y & 255) << 8) | 40u;
        }
        return s;
    }

    static bool Same(PicSurface a, PicSurface b)
    {
        if (a.Size != b.Size) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    static void Surfaces()
    {
        using (var g = Gradient(300, 200))
        {
            Check(g.GetColor(17, 99) == Color.FromArgb(255, 17, 99, 40), "surface keeps exact pixel values");
            Check(g.IsOpaque(), "an opaque picture is seen as opaque");
            using (var r = g.RotateFlip(RotateFlipType.Rotate90FlipNone))
            {
                Check(r.Width == 200 && r.Height == 300, "rotate right swaps width and height");
                // (x, y) -> (h - 1 - y, x)
                Check(r.GetColor(200 - 1 - 99, 17) == g.GetColor(17, 99), "rotate right moves each pixel to the right place");
                using (var back = r.RotateFlip(RotateFlipType.Rotate270FlipNone)) Check(Same(back, g), "rotate right then left gives the picture back");
            }
            using (var f = g.RotateFlip(RotateFlipType.RotateNoneFlipX)) Check(f.GetColor(0, 5) == g.GetColor(299, 5), "flip horizontal mirrors left and right");
            using (var f = g.RotateFlip(RotateFlipType.RotateNoneFlipY)) Check(f.GetColor(7, 0) == g.GetColor(7, 199), "flip vertical mirrors top and bottom");
            using (var f = g.RotateFlip(RotateFlipType.Rotate180FlipNone)) Check(f.GetColor(0, 0) == g.GetColor(299, 199), "rotate 180 turns the picture over");
            using (var c = g.Clone(new Rectangle(10, 20, 30, 40))) Check(c.Width == 30 && c.Height == 40 && c.GetColor(0, 0) == g.GetColor(10, 20), "cutting out a part keeps its pixels");
            using (var half = g.Resize(150, 100))
            {
                Check(half.Width == 150 && half.Height == 100, "resize gives the size asked for");
                Check(Near(half.GetColor(75, 50), 150, 100, 40, 6), "resize keeps colours in place");
            }
            using (var bmp = g.ToBitmap())
            using (var again = PicSurface.FromImage(bmp))
                Check(bmp.PixelFormat == PixelFormat.Format32bppArgb && Same(again, g), "to a bitmap and back loses nothing");
        }
        using (var t = new PicSurface(64, 64))
        {
            Check(!t.IsOpaque() && t.ContentBounds().IsEmpty, "a new surface is fully see-through");
            t.Fill(new Rectangle(10, 12, 5, 7), PicSurface.Premultiply(Color.FromArgb(128, 255, 0, 0)));
            Check(t.ContentBounds() == new Rectangle(10, 12, 5, 7), "content bounds finds the painted part");
            var c = t.GetColor(11, 13);
            Check(c.A == 128 && c.R >= 253 && c.G == 0, "half see-through colour survives the round trip");
        }
        using (var big = Gradient(1000, 600))
        {
            var mips = new PicMips();
            mips.Build(big);
            Check(mips.Count >= 3 && mips.Level(1).Width == 500 && mips.Level(2).Width == 250, "half-size copies are built down to a small size");
            Check(mips.LevelFor(1f) == 0 && mips.LevelFor(0.5f) == 1 && mips.LevelFor(0.3f) == 1 && mips.LevelFor(0.25f) == 2, "the right copy is picked for each zoom");
            big.Fill(new Rectangle(400, 200, 100, 100), PicSurface.Premultiply(Color.White));
            mips.Update(new Rectangle(400, 200, 100, 100));
            Check(mips.Level(1).GetColor(225, 125) == Color.FromArgb(255, 255, 255, 255) && mips.Level(2).GetColor(112, 62).R == 255, "a change to the picture reaches the half-size copies");
            mips.Dispose();
        }
    }

    static void Layers()
    {
        var es = new EditSession(Solid(200, 100, Color.FromArgb(255, 0, 0, 200)), null, false);
        Check(!es.IsDirty && !es.CanUndo && es.State.Layers.Length == 1, "a new session is clean with one layer");
        Check(ReferenceEquals(es.Compositor.Picture, es.State.Layers[0].Pixels), "one plain layer is shown without an extra copy");

        var top = es.AddLayer("Top", Solid(50, 50, Color.FromArgb(255, 255, 0, 0)), 20, 10);
        Check(es.State.Layers.Length == 2 && es.ActiveLayer.Id == top.Id && es.IsDirty, "adding a layer makes it the active one");
        Check(Near(es.Compositor.Picture.GetColor(30, 20), 255, 0, 0) && Near(es.Compositor.Picture.GetColor(100, 50), 0, 0, 200), "the layer covers only its own area");

        es.SetLayerOpacity(1, 0.5f);
        Check(Near(es.Compositor.Picture.GetColor(30, 20), 128, 0, 100, 4), "a half see-through layer mixes with what is below");
        es.SetLayerOpacity(1, 1f);
        es.SetLayerBlend(1, PicBlend.Multiply);
        Check(Near(es.Compositor.Picture.GetColor(30, 20), 0, 0, 0), "multiply darkens (red over blue gives black)");
        es.SetLayerBlend(1, PicBlend.Screen);
        Check(Near(es.Compositor.Picture.GetColor(30, 20), 255, 0, 200), "screen lightens (red over blue gives magenta)");
        es.SetLayerBlend(1, PicBlend.Normal);
        es.SetLayerVisible(1, false);
        Check(Near(es.Compositor.Picture.GetColor(30, 20), 0, 0, 200), "a hidden layer is left out");
        es.SetLayerVisible(1, true);

        es.MoveLayerBy(1, 100, 0);
        Check(Near(es.Compositor.Picture.GetColor(130, 20), 255, 0, 0) && Near(es.Compositor.Picture.GetColor(30, 20), 0, 0, 200), "moving a layer moves its pixels");
        es.Undo();
        Check(Near(es.Compositor.Picture.GetColor(30, 20), 255, 0, 0), "undo puts the layer back");
        es.Redo();

        es.DuplicateLayer(1);
        Check(es.State.Layers.Length == 3 && !ReferenceEquals(es.State.Layers[1].Pixels, es.State.Layers[2].Pixels), "duplicating a layer copies its pixels");
        es.MoveLayer(2, 0);
        Check(Near(es.Compositor.Picture.GetColor(130, 20), 255, 0, 0), "reordering layers keeps the picture right");
        es.DeleteLayer(0);
        Check(es.State.Layers.Length == 2, "a layer can be deleted");
        es.Undo(); es.Undo(); es.Undo();
        Check(es.State.Layers.Length == 2, "undo walks back through layer changes");

        es.MergeDown(1);
        Check(es.State.Layers.Length == 1 && Near(es.State.Layers[0].Pixels.GetColor(130, 20), 255, 0, 0), "merge down mixes the layer into the one below");
        es.Undo();
        Check(es.State.Layers.Length == 2, "undo splits merged layers again");
        es.FlattenLayers();
        Check(es.State.Layers.Length == 1 && es.State.Layers[0].Pixels.Width == 200, "flatten leaves one layer");
        using (var bmp = es.Flatten()) Check(bmp.Width == 200 && Near(bmp.GetPixel(130, 20), 255, 0, 0) && Near(bmp.GetPixel(5, 5), 0, 0, 200), "the saved picture matches what is shown");
        es.Dispose();
    }

    static void Objects()
    {
        var es = new EditSession(Solid(400, 300, Color.White), "x.png", false);
        var baseline = es.State.Layers[0].Pixels;
        es.AddStroke(new Stroke(EditTool.Pen, Color.Red, 10f, new[] { new PointF(50, 50), new PointF(350, 50) }));
        Check(es.HasInk && es.IsDirty && es.UndoCount == 1, "a pen stroke is one undo step");
        Check(Near(es.Compositor.Picture.GetColor(200, 50), 255, 0, 0), "the stroke shows on the picture");
        Check(baseline.GetColor(200, 50) == Color.FromArgb(255, 255, 255, 255), "the stroke stays separate from the pixels");
        Check(es.HistoryBytes == 0, "drawing costs no undo memory");

        var rect = new ShapeObject(ShapeKind.Rect, new PointF(100, 100), new PointF(200, 180), Color.Blue, 4f, Color.FromArgb(255, 0, 255, 0));
        es.AddObject(rect);
        Check(Near(es.Compositor.Picture.GetColor(150, 140), 0, 255, 0) && Near(es.Compositor.Picture.GetColor(100, 140), 0, 0, 255, 40), "a filled rectangle has its fill and its outline");
        Check(ReferenceEquals(es.ObjectAt(new PointF(150, 140), 2), rect), "clicking a shape finds it");
        Check(es.ObjectAt(new PointF(300, 250), 2) == null, "clicking empty space finds nothing");

        var moved = rect.Offset(100, 0);
        Check(es.ReplaceObject(rect, moved, "Move", "k"), "a shape can be moved");
        Check(Near(es.Compositor.Picture.GetColor(250, 140), 0, 255, 0) && Near(es.Compositor.Picture.GetColor(150, 140), 255, 255, 255), "the moved shape is redrawn in its new place only");
        var moved2 = moved.Offset(10, 0);
        es.ReplaceObject(moved, moved2, "Move", "k");
        Check(es.UndoCount == 3, "one drag is one undo step however many moves it has");

        var arrow = new ShapeObject(ShapeKind.Arrow, new PointF(20, 250), new PointF(120, 250), Color.Black, 6f, Color.Empty);
        es.AddObject(arrow);
        Check(Near(es.Compositor.Picture.GetColor(60, 250), 0, 0, 0) && arrow.HitTest(new PointF(60, 251), 2) && !arrow.HitTest(new PointF(60, 280), 2), "an arrow is drawn and can be hit along its line");
        var step = new StepObject(new PointF(350, 250), 16, 3, Color.OrangeRed);
        es.AddObject(step);
        Check(es.Compositor.Picture.GetColor(350, 236).R > 200 && step.Number == 3, "a numbered step marker is drawn");
        var text = new TextObject("Hello", "Arial", 24, true, false, new PointF(20, 10), Color.Black, Color.Yellow);
        es.AddObject(text);
        Check(text.Box.Width > 30 && text.Box.Height > 15 && Near(es.Compositor.Picture.GetColor(22, 12), 255, 255, 0, 30), "a text box measures itself and draws its background");

        // crop keeps shapes editable and in place
        es.Crop(new Rectangle(50, 0, 300, 300));
        Check(es.Size == new Size(300, 300) && es.HasInk, "crop keeps shapes as shapes");
        Check(Near(es.Compositor.Picture.GetColor(210, 140), 0, 255, 0), "shapes follow the crop");
        es.RotateFlip(RotateFlipType.Rotate90FlipNone);
        Check(es.Size == new Size(300, 300) && es.State.Layers[0].Objects.Length == 0, "rotating a layer with text turns its drawings into pixels");
        // the green rectangle was at (210, 140) -> rotate right: (h - 1 - y, x) = (159, 210)
        Check(Near(es.Compositor.Picture.GetColor(159, 210), 0, 255, 0), "everything turns together");
        es.Undo();
        Check(es.State.Layers[0].Objects.Length == 5, "undo brings the editable shapes back");
        es.Undo();
        Check(es.Size == new Size(400, 300), "undo of crop restores the size");

        var gone = new HashSet<PicObject> { text };
        es.RemoveObjects(gone);
        Check(es.State.Layers[0].Objects.Length == 4 && Near(es.Compositor.Picture.GetColor(22, 12), 255, 255, 255), "the eraser removes a whole shape");
        es.ResizeImage(200, 150);
        Check(es.Size == new Size(200, 150) && es.State.Layers[0].Objects.Length == 4 && Near(es.Compositor.Picture.GetColor(130, 70), 0, 255, 0, 12), "resizing the picture scales the shapes with it");
        es.Undo();
        es.ClearInk();
        Check(!es.HasInk && Near(es.Compositor.Picture.GetColor(200, 50), 255, 255, 255), "clear removes every drawing");
        while (es.CanUndo) es.Undo();
        Check(!es.IsDirty && !es.HasInk && Same(es.Compositor.Picture, baseline), "undoing everything gives the original back, marked unchanged");
        while (es.CanRedo) es.Redo();
        Check(!es.HasInk && es.IsDirty, "redo replays every step");
        es.Dispose();
    }

    static void PixelEdits()
    {
        var es = new EditSession(Gradient(1000, 800), null, false);
        using (var original = es.State.Layers[0].Pixels.Clone())
        {
            var pe = es.BeginPixels("Paint", false);
            var r = pe.Touch(new Rectangle(300, 300, 40, 40));
            pe.Surface.Fill(new Rectangle(r.X - pe.X, r.Y - pe.Y, r.Width, r.Height), PicSurface.Premultiply(Color.White));
            pe.Commit();
            Check(es.UndoCount == 1 && es.Compositor.Picture.GetColor(310, 310) == Color.FromArgb(255, 255, 255, 255), "painting pixels is one undo step");
            Check(es.HistoryBytes <= 4 * 256 * 256 * 4 * 2, "undo keeps only the tiles that changed (" + es.HistoryBytes / 1024 + " KB for a 3 MB picture)");
            es.Undo();
            Check(Same(es.Compositor.Picture, original), "undo restores the painted pixels exactly");
            es.Redo();
            Check(es.Compositor.Picture.GetColor(339, 339) == Color.FromArgb(255, 255, 255, 255) && es.Compositor.Picture.GetColor(340, 340) == original.GetColor(340, 340), "redo paints them again, and nothing more");

            pe = es.BeginPixels("Paint", false);
            r = pe.Touch(new Rectangle(0, 0, 100, 100));
            pe.Surface.Fill(new Rectangle(0, 0, 100, 100), 0);
            pe.Cancel();
            Check(es.UndoCount == 1 && es.Compositor.Picture.GetColor(5, 5) == original.GetColor(5, 5), "a cancelled edit leaves no trace");

            // filters through the same path
            pe = es.BeginPixels("Pixelate", false);
            r = pe.Touch(new Rectangle(500, 100, 64, 64));
            PicFilters.Pixelate(pe.Surface, r, 16);
            pe.Commit();
            var p = es.Compositor.Picture;
            Check(p.GetPixel(500, 100) == p.GetPixel(515, 115) && p.GetPixel(500, 100) != p.GetPixel(516, 100), "pixelate makes flat blocks");
            Check(p.GetPixel(499, 100) == original.GetPixel(499, 100) && p.GetPixel(564, 100) == original.GetPixel(564, 100), "pixelate stays inside its area");

            pe = es.BeginPixels("Blur", false);
            r = pe.Touch(new Rectangle(700, 500, 100, 100));
            // a hard edge inside the area
            pe.Surface.Fill(new Rectangle(700, 500, 50, 100), PicSurface.Premultiply(Color.Black));
            pe.Surface.Fill(new Rectangle(750, 500, 50, 100), PicSurface.Premultiply(Color.White));
            PicFilters.GaussianBlur(pe.Surface, r, 6f);
            pe.Commit();
            p = es.Compositor.Picture;
            int mid = p.GetColor(750, 550).R, left = p.GetColor(705, 550).R, right = p.GetColor(795, 550).R;
            Check(left < 10 && right > 245 && mid > 90 && mid < 170, "blur softens an edge (" + left + ", " + mid + ", " + right + ")");
            Check(p.GetPixel(699, 550) == original.GetPixel(699, 550) && p.GetPixel(800, 550) == original.GetPixel(800, 550), "blur stays inside its area");
            while (es.CanUndo) es.Undo();
            Check(Same(es.Compositor.Picture, original) && !es.IsDirty, "undoing every pixel edit gives the original back");
        }
        es.Dispose();

        // erasing pixels
        using (var s = Solid(100, 100, Color.Red))
        {
            PicPaint.EraseSegment(s, new PointF(20, 50), new PointF(80, 50), 10, s.Bounds, null, 0, 0);
            Check(s.GetPixel(50, 50) == 0 && s.GetPixel(50, 45) == 0 && s.GetColor(50, 30).A == 255 && s.GetPixel(12, 50) == 0 && s.GetColor(5, 50).A == 255, "the pixel eraser rubs out a round-ended line");
            int edge = s.GetColor(50, 40).A + s.GetColor(50, 39).A;
            Check(edge > 0 && edge < 510, "the erased edge is soft");
        }
    }

    static void UndoLimits()
    {
        long keep = EditSession.UndoBudget;
        try
        {
            EditSession.UndoBudget = 3L * 1000 * 1000;       // room for about two 1 MB steps besides the picture
            var es = new EditSession(Solid(500, 500, Color.Gray), null, false);
            for (int i = 0; i < 12; i++) es.RotateFlip(RotateFlipType.Rotate90FlipNone);
            Check(es.UndoCount < 12 && es.UndoCount >= 1, "old undo steps are dropped when they take too much memory (" + es.UndoCount + " kept)");
            Check(es.HistoryBytes <= EditSession.UndoBudget, "undo memory stays within its budget");
            int n = 0; while (es.CanUndo) { es.Undo(); n++; }
            Check(n >= 1 && es.Size == new Size(500, 500), "the steps that are kept still undo");
            es.Dispose();

            EditSession.UndoBudget = keep;
            es = new EditSession(Solid(50, 50, Color.Gray), null, false);
            for (int i = 0; i < EditSession.MaxSteps + 30; i++) es.AddStroke(new Stroke(EditTool.Pen, Color.Red, 2f, new[] { new PointF(i % 50, 1), new PointF(i % 50, 40) }));
            Check(es.UndoCount == EditSession.MaxSteps, "the number of undo steps is capped");
            es.Undo(); es.Undo();
            es.AddStroke(new Stroke(EditTool.Pen, Color.Red, 2f, new[] { new PointF(1, 1), new PointF(9, 9) }));
            Check(!es.CanRedo, "a new edit clears redo");
            es.MarkSaved();
            Check(!es.IsDirty, "saving marks the picture unchanged");
            es.Undo();
            Check(es.IsDirty, "undo after saving marks it changed again");
            es.Redo();
            Check(!es.IsDirty, "and redo back to the saved point marks it unchanged");
            es.Dispose();
        }
        finally { EditSession.UndoBudget = keep; }
    }

    static void Selections()
    {
        var size = new Size(200, 100);
        var r = PicSelection.FromRect(new Rectangle(10, 10, 50, 30), size);
        Check(r.Bounds == new Rectangle(10, 10, 50, 30) && r.At(10, 10) == 255 && r.At(60, 10) == 0 && r.IsRectangle, "a box selection covers exactly its box");
        Check(PicSelection.FromRect(new Rectangle(190, 90, 50, 50), size).Bounds == new Rectangle(190, 90, 10, 10), "a selection is cut off at the picture's edge");
        Check(PicSelection.FromRect(new Rectangle(300, 300, 5, 5), size) == null, "a selection outside the picture is nothing");

        var e = PicSelection.FromEllipse(new RectangleF(100, 20, 60, 60), size);
        Check(e.At(130, 50) == 255 && e.At(101, 21) == 0 && e.At(159, 79) == 0, "an oval selection leaves the corners out");
        long area = e.PixelCount;
        Check(Math.Abs(area - Math.PI * 30 * 30) < 60, "the oval has the right area (" + area + ")");

        var tri = PicSelection.FromPolygon(new[] { new PointF(0, 0), new PointF(40, 0), new PointF(0, 40) }, size);
        Check(tri.At(5, 5) == 255 && tri.At(35, 35) == 0, "a lasso selection follows its outline");

        var add = PicSelection.Combine(r, e, PicSelMode.Add, size);
        Check(add.At(20, 20) == 255 && add.At(130, 50) == 255 && add.At(80, 50) == 0 && add.Bounds == Rectangle.Union(r.Bounds, e.Bounds), "adding joins two selections");
        var r2 = PicSelection.FromRect(new Rectangle(30, 20, 100, 10), size);
        var sub = PicSelection.Combine(r, r2, PicSelMode.Subtract, size);
        Check(sub.At(20, 25) == 255 && sub.At(40, 25) == 0 && sub.At(40, 15) == 255, "subtracting cuts a piece out");
        var both = PicSelection.Combine(r, r2, PicSelMode.Intersect, size);
        Check(both.Bounds == new Rectangle(30, 20, 30, 10), "intersecting keeps only the overlap");
        Check(PicSelection.Combine(r, e, PicSelMode.Intersect, size) == null, "selections that don't touch have no overlap");
        var inv = PicSelection.Invert(r, size);
        Check(inv.At(20, 20) == 0 && inv.At(0, 0) == 255 && inv.At(199, 99) == 255, "invert selects everything else");
        Check(PicSelection.Invert(PicSelection.All(size), size) == null, "inverting everything selects nothing");
        var grown = r.Grow(5, size);
        Check(grown.Bounds == new Rectangle(5, 5, 60, 40), "grow widens a selection");
        Check(r.Grow(-5, size).Bounds == new Rectangle(15, 15, 40, 20), "shrink narrows it");
        var soft = r.Feather(4, size);
        Check(soft.At(35, 25) == 255 && soft.At(10, 25) > 60 && soft.At(10, 25) < 200 && soft.At(6, 25) > 0, "feather softens the edge");
        Check(r.Edges.Length == 4, "a box selection has four outline pieces");

        // the magic wand
        using (var s = Solid(200, 100, Color.White))
        {
            s.Fill(new Rectangle(50, 20, 40, 40), PicSurface.Premultiply(Color.Red));
            s.Fill(new Rectangle(150, 20, 20, 20), PicSurface.Premultiply(Color.FromArgb(255, 250, 5, 5)));
            var w = PicSelection.FromWand(s, 60, 30, 16, true, 0, 0, size);
            Check(w.Bounds == new Rectangle(50, 20, 40, 40) && w.IsRectangle, "the wand picks the touching area of one colour");
            var everywhere = PicSelection.FromWand(s, 60, 30, 16, false, 0, 0, size);
            Check(everywhere.At(160, 30) == 255 && everywhere.At(100, 30) == 0, "with 'touching only' off the wand picks that colour everywhere");
            var strict = PicSelection.FromWand(s, 60, 30, 2, false, 0, 0, size);
            Check(strict.At(160, 30) == 0, "a low tolerance leaves near-matches out");
            var white = PicSelection.FromWand(s, 0, 0, 10, true, 0, 0, size);
            Check(white.At(120, 80) == 255 && white.At(60, 30) == 0 && white.At(199, 99) == 255, "the wand flows around obstacles");

            PicPaint.FillMask(s, 0, 0, w, PicSurface.Premultiply(Color.Blue));
            Check(Near(s.GetColor(60, 30), 0, 0, 255) && Near(s.GetColor(49, 30), 255, 255, 255), "fill paints the selection only");
            PicPaint.ClearMask(s, 0, 0, e);
            Check(s.GetPixel(130, 50) == 0 && s.GetColor(101, 21).A == 255, "delete clears the selected pixels");
        }

        var es = new EditSession(Solid(200, 100, Color.White), null, false);
        es.Selection = r;
        es.Crop(new Rectangle(0, 0, 100, 100));
        Check(es.Selection == null, "a selection is dropped when the picture changes size");
        es.Dispose();
    }

    static void LoadAndSave(string dir)
    {
        ImageCache.Initialize();
        string png = Path.Combine(dir, "pt-alpha.png");
        using (var s = new PicSurface(120, 80))
        {
            s.Fill(new Rectangle(0, 0, 60, 80), PicSurface.Premultiply(Color.FromArgb(255, 10, 200, 30)));
            using (var bmp = s.ToBitmap()) ImageSaver.Save(bmp, png);
        }
        var li = LoadedImage.Load(png);
        Check(li.Surface != null && li.Mips != null && li.HasAlpha && li.Surface.Width == 120, "a picture with see-through parts loads with its transparency");
        Check(li.Surface.GetColor(10, 10) == Color.FromArgb(255, 10, 200, 30) && li.Surface.GetPixel(100, 10) == 0, "loaded pixels are exact");
        var es = new EditSession(li.Surface.Clone(), png, false);
        es.AddStroke(new Stroke(EditTool.Pen, Color.Red, 6f, new[] { new PointF(70, 40), new PointF(110, 40) }));
        string outPng = Path.Combine(dir, "pt-out.png"), outJpg = Path.Combine(dir, "pt-out.jpg");
        using (var flat = es.Flatten()) { ImageSaver.Save(flat, outPng); ImageSaver.Save(flat, outJpg); }
        using (var back = (Bitmap)Image.FromFile(outPng))
            Check(back.GetPixel(100, 10).A == 0 && Near(back.GetPixel(90, 40), 255, 0, 0) && back.GetPixel(10, 10) == Color.FromArgb(255, 10, 200, 30), "PNG keeps transparency and the drawing");
        using (var back = (Bitmap)Image.FromFile(outJpg))
            Check(Near(back.GetPixel(100, 10), 255, 255, 255, 6) && Near(back.GetPixel(10, 10), 10, 200, 30, 12), "JPEG puts white behind see-through parts");
        using (var orig = (Bitmap)Image.FromFile(png)) Check(orig.GetPixel(90, 40).A == 0, "the original file is untouched");
        es.Dispose();
        ImageCache.Delete(li.CachePath);
        li.Dispose();
        foreach (var f in new[] { png, outPng, outJpg }) try { File.Delete(f); } catch { }
    }

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.GetTempPath();
        try
        {
            Surfaces();
            Layers();
            Objects();
            PixelEdits();
            UndoLimits();
            Selections();
            LoadAndSave(dir);
        }
        catch (Exception ex) { Console.WriteLine("FAIL exception: " + ex); fails++; }
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails;
    }
}
