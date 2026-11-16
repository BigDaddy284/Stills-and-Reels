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

    static void Adjustments()
    {
        using (var src = Gradient(256, 64))
        using (var dst = new PicSurface(256, 64))
        {
            var a = new PicAdjustments();
            Check(a.IsIdentity, "untouched sliders change nothing");
            PicAdjust.Apply(src, dst, a);
            Check(Same(src, dst), "an untouched adjustment copies the picture exactly");

            a.Brightness = 40;
            PicAdjust.Apply(src, dst, a);
            Check(dst.GetColor(100, 20).R > src.GetColor(100, 20).R && dst.GetColor(100, 20).G > src.GetColor(100, 20).G, "brightness lightens");
            a = new PicAdjustments { Exposure = -50 };
            PicAdjust.Apply(src, dst, a);
            Check(dst.GetColor(200, 20).R < src.GetColor(200, 20).R, "lower exposure darkens");

            a = new PicAdjustments { Contrast = 60 };
            PicAdjust.Apply(src, dst, a);
            Check(dst.GetColor(30, 5).R < 30 && dst.GetColor(230, 5).R > 230, "contrast pushes darks down and lights up");

            a = new PicAdjustments { Grayscale = true };
            PicAdjust.Apply(src, dst, a);
            var gc = dst.GetColor(180, 40);
            Check(gc.R == gc.G && gc.G == gc.B && gc.A == 255, "black and white makes every pixel grey");

            a = new PicAdjustments { Saturation = -100 };
            PicAdjust.Apply(src, dst, a);
            gc = dst.GetColor(180, 40);
            Check(Math.Abs(gc.R - gc.G) <= 2 && Math.Abs(gc.G - gc.B) <= 2, "no saturation leaves no colour");

            a = new PicAdjustments { Invert = true };
            PicAdjust.Apply(src, dst, a);
            Check(dst.GetColor(17, 33) == Color.FromArgb(255, 255 - 17, 255 - 33, 255 - 40), "invert flips every channel");
            using (var twice = new PicSurface(256, 64))
            {
                PicAdjust.Apply(dst, twice, a);
                Check(Same(src, twice), "inverting twice gives the picture back");
            }

            a = new PicAdjustments { Temperature = 60 };
            PicAdjust.Apply(src, dst, a);
            Check(dst.GetColor(120, 30).R > src.GetColor(120, 30).R && dst.GetColor(120, 30).B < src.GetColor(120, 30).B, "warmer adds red and takes blue away");

            a = new PicAdjustments { Sepia = 100 };
            PicAdjust.Apply(src, dst, a);
            gc = dst.GetColor(200, 50);
            Check(gc.R > gc.G && gc.G > gc.B, "sepia is brown: more red than green than blue");

            // only inside a selection
            a = new PicAdjustments { Invert = true };
            var sel = PicSelection.FromRect(new Rectangle(10, 10, 20, 20), new Size(256, 64));
            PicAdjust.Apply(src, dst, a, sel);
            Check(dst.GetColor(15, 15) == Color.FromArgb(255, 255 - 15, 255 - 15, 255 - 40) && dst.GetPixel(100, 30) == src.GetPixel(100, 30) && dst.GetPixel(9, 15) == src.GetPixel(9, 15),
                "an adjustment stays inside the selection");

            a = new PicAdjustments { Blur = 50 };
            using (var edge = new PicSurface(100, 40))
            using (var soft = new PicSurface(100, 40))
            {
                edge.Fill(new Rectangle(0, 0, 50, 40), 0xFF000000u);
                edge.Fill(new Rectangle(50, 0, 50, 40), 0xFFFFFFFFu);
                PicAdjust.Apply(edge, soft, a);
                int mid = soft.GetColor(50, 20).R;
                Check(mid > 40 && mid < 215 && soft.GetColor(2, 20).R < 5 && soft.GetColor(97, 20).R > 250, "blur softens an edge and leaves flat areas alone");
            }

            // a hard-edged block, so looks that only touch detail (sharpen) have something to work on
            src.Fill(new Rectangle(100, 20, 40, 20), 0xFFFFFFFFu);
            foreach (var look in PicAdjustments.Looks)
            {
                PicAdjust.Apply(src, dst, look.Value);
                if (Same(src, dst)) Check(false, "the look '" + look.Key + "' changes the picture");
            }
            Check(PicAdjustments.Looks.Length >= 8, "there is a set of ready-made looks");
        }

        // levels and curves
        var lv = new PicLevels { InBlack = 50, InWhite = 200 };
        var curve = lv.ToCurve();
        Check(curve[50] == 0 && curve[200] == 255 && curve[20] == 0 && curve[240] == 255 && curve[125] > 120 && curve[125] < 136, "levels stretch the chosen range to full");
        lv = new PicLevels { Gamma = 2f };
        Check(lv.ToCurve()[128] > 160 && lv.ToCurve()[0] == 0 && lv.ToCurve()[255] == 255, "midtones above 1 brighten the middle only");
        Check(new PicLevels().IsIdentity && new PicLevels().ToCurve()[77] == 77, "untouched levels change nothing");
        var c2 = PicAdjust.CurveFromPoints(new[] { new PointF(0, 0), new PointF(128, 190), new PointF(255, 255) });
        bool rising = true;
        for (int i = 1; i < 256; i++) if (c2[i] < c2[i - 1]) rising = false;
        Check(c2[0] == 0 && c2[255] == 255 && Math.Abs(c2[128] - 190) <= 2 && rising, "a curve passes through its points and never turns back");
        var c3 = PicAdjust.CurveFromPoints(new[] { new PointF(0, 0), new PointF(255, 255) });
        Check(c3[60] == 60 && c3[200] == 200, "a straight curve changes nothing");

        // histogram and auto fix
        using (var flat = new PicSurface(100, 100))
        {
            flat.Fill(new Rectangle(0, 0, 50, 100), PicSurface.Premultiply(Color.FromArgb(255, 90, 90, 90)));
            flat.Fill(new Rectangle(50, 0, 50, 100), PicSurface.Premultiply(Color.FromArgb(255, 170, 170, 170)));
            var h = PicAdjust.Histogram(flat);
            Check(h.Count == 10000 && h.Luma[90] == 5000 && h.Luma[170] == 5000 && h.R[90] == 5000, "the histogram counts every pixel");
            var auto = PicAdjust.AutoLevels(h);
            Check(auto.InBlack <= 60 && auto.InBlack >= 40 && auto.InWhite >= 195 && auto.InWhite <= 215, "auto fix opens up a flat picture without overdoing it");
            var hs = PicAdjust.Histogram(flat, PicSelection.FromRect(new Rectangle(0, 0, 10, 10), new Size(100, 100)));
            Check(hs.Count == 100 && hs.Luma[90] == 100, "the histogram can look at just the selection");
        }
        using (var full = Gradient(256, 256))
        {
            var auto = PicAdjust.AutoLevels(PicAdjust.Histogram(full));
            Check(auto.InBlack < 12 && auto.InWhite > 200, "auto fix leaves a full-range picture nearly alone");
        }
        using (var cast = Solid(40, 40, Color.FromArgb(255, 200, 150, 100)))
        {
            byte[] r, g, b;
            PicAdjust.AutoWhiteBalance(PicAdjust.Histogram(cast), out r, out g, out b);
            Check(r[200] < 200 && b[100] > 100 && Math.Abs(r[200] - b[100]) < 30, "auto white balance pulls a colour cast towards grey");
        }
    }

    static void StraightenAndTrim()
    {
        var inside = PicAdjust.LargestInside(400, 300, 0);
        Check(inside.Width == 400 && inside.Height == 300, "no turn keeps the whole picture");
        inside = PicAdjust.LargestInside(400, 300, 90);
        Check(Math.Abs(inside.Width - 300) < 1 && Math.Abs(inside.Height - 300 * 300 / 400f) < 1.5f || inside.Width <= 300.5f && inside.Height <= 400.5f, "a quarter turn still fits inside");
        var i5 = PicAdjust.LargestInside(400, 300, 5);
        var i5n = PicAdjust.LargestInside(400, 300, -5);
        Check(i5.Width < 400 && i5.Height < 300 && i5.Width > 340 && i5.Height > 250 && Math.Abs(i5.Width - i5n.Width) < 0.01f, "a small turn loses only a little, the same both ways");

        var es = new EditSession(Solid(400, 300, Color.FromArgb(255, 20, 120, 220)), null, false);
        es.RotateFree(5, true);
        Check(es.Size.Width < 400 && es.Size.Height < 300 && es.CanUndo, "straighten with crop makes the picture a little smaller");
        using (var flat = es.FlattenSurface())
        {
            bool solid = true;
            foreach (var pt in new[] { new Point(0, 0), new Point(flat.Width - 1, 0), new Point(0, flat.Height - 1), new Point(flat.Width - 1, flat.Height - 1), new Point(flat.Width / 2, flat.Height / 2) })
            {
                var c = flat.GetColor(pt.X, pt.Y);
                if (c.A < 250 || !Near(c, 20, 120, 220, 6)) solid = false;
            }
            Check(solid, "after straighten with crop there are no empty corners");
        }
        es.Undo();
        Check(es.Size == new Size(400, 300), "undo gives the size back");
        es.RotateFree(30, false);
        Check(es.Size.Width > 400 && es.Size.Height > 300, "a free turn without crop grows the picture to fit");
        Check(es.Compositor.Picture.GetColor(1, 1).A == 0 && Near(es.Compositor.Picture.GetColor(es.Size.Width / 2, es.Size.Height / 2), 20, 120, 220, 6), "its corners are see-through and its middle is the picture");
        Check(es.TrimEdges() == false || es.Size.Width > 0, "trim copes with a turned picture");
        es.Dispose();

        // trim: see-through margins, then a flat-colour frame
        using (var s = new PicSurface(200, 150))
        {
            s.Fill(new Rectangle(40, 30, 100, 60), PicSurface.Premultiply(Color.Red));
            Check(PicAdjust.TrimBounds(s) == new Rectangle(40, 30, 100, 60), "trim finds the part that is not see-through");
        }
        using (var s = Solid(200, 150, Color.White))
        {
            s.Fill(new Rectangle(25, 35, 120, 70), PicSurface.Premultiply(Color.FromArgb(255, 10, 60, 200)));
            Check(PicAdjust.TrimBounds(s) == new Rectangle(25, 35, 120, 70), "trim cuts a plain white frame away");
        }
        using (var s = Solid(64, 64, Color.FromArgb(255, 40, 40, 40)))
            Check(PicAdjust.TrimBounds(s) == s.Bounds, "a picture of one colour is left alone");

        var et = new EditSession(Solid(200, 150, Color.White), null, false);
        et.AddObject(new ShapeObject(ShapeKind.Rect, new PointF(50, 40), new PointF(150, 110), Color.Black, 4f, Color.FromArgb(255, 200, 0, 0)));
        Check(et.TrimEdges() && et.Size.Width < 120 && et.Size.Width > 95 && et.Size.Height < 85 && et.Size.Height > 65, "trim edges crops to what was drawn");
        et.Undo();
        Check(et.Size == new Size(200, 150), "undo brings the trimmed edges back");
        et.Dispose();
    }

    static void Projects(string dir)
    {
        string file = Path.Combine(dir, "pt-layers" + PicProject.Extension);
        var es = new EditSession(Gradient(320, 200), null, false);
        es.AddStroke(new Stroke(EditTool.Pen, Color.Red, 6f, new[] { new PointF(20, 20), new PointF(120, 60), new PointF(200, 30) }));
        var top = es.AddLayer("Sticker", Solid(60, 40, Color.FromArgb(200, 0, 255, 0)), 100, 80);
        es.SetLayerOpacity(1, 0.6f);
        es.SetLayerBlend(1, PicBlend.Screen);
        es.AddObject(new ShapeObject(ShapeKind.Arrow, new PointF(10, 10), new PointF(50, 30), Color.Yellow, 3f, Color.Empty));
        es.AddObject(new TextObject("Hello\nworld", "Arial", 18f, true, false, new PointF(5, 5), Color.White, Color.FromArgb(160, 0, 0, 0)));
        es.AddObject(new StepObject(new PointF(30, 30), 12f, 3, Color.Blue));
        es.AddLayer("Empty", null, 0, 0);
        es.SetLayerVisible(2, false);

        PicProject.Save(es.State, file);
        Check(File.Exists(file) && PicProject.IsProject(file) && PicProject.IsProject("a.SRPIC") && !PicProject.IsProject("a.png"), "a layered picture saves to its own kind of file");
        Check(new FileInfo(file).Length < 320 * 200 * 4, "the file is packed smaller than its raw pixels");
        Check(!File.Exists(file + ".tmp"), "no half-written file is left behind");

        var back = PicProject.Load(file);
        Check(back.Size == new Size(320, 200) && back.Layers.Length == 3, "it opens with the same size and layers");
        var l0 = back.Layers[0]; var l1 = back.Layers[1]; var l2 = back.Layers[2];
        Check(l0.Name == "Background" && l1.Name == "Sticker" && l2.Name == "Empty", "layer names are kept");
        Check(Math.Abs(l1.Opacity - 0.6f) < 0.001f && l1.Blend == PicBlend.Screen && l1.X == 100 && l1.Y == 80 && !l2.Visible && l1.Visible, "opacity, mixing, place and hidden state are kept");
        Check(Same(l0.Pixels, es.State.Layers[0].Pixels) && Same(l1.Pixels, es.State.Layers[1].Pixels) && l2.Pixels == null, "every layer's pixels are exact");
        Check(l0.Objects.Length == 1 && l1.Objects.Length == 3 && l2.Objects.Length == 0, "drawings stay on their layers");
        var st = l0.Objects[0] as Stroke;
        Check(st != null && st.Tool == EditTool.Pen && st.Color.ToArgb() == Color.Red.ToArgb() && st.Points.Length == 3 && st.Points[1] == new PointF(120, 60) && st.Width == 6f, "a pen line is kept as a line");
        var sh = l1.Objects[0] as ShapeObject;
        Check(sh != null && sh.Kind == ShapeKind.Arrow && sh.B == new PointF(50, 30) && sh.Fill.IsEmpty && sh.Color.ToArgb() == Color.Yellow.ToArgb(), "an arrow is kept as an arrow");
        var tx = l1.Objects[1] as TextObject;
        Check(tx != null && tx.Text == "Hello\nworld" && tx.FontName == "Arial" && tx.Size == 18f && tx.Bold && !tx.Italic && tx.Fill.A == 160, "text is kept as text that can still be changed");
        var sp = l1.Objects[2] as StepObject;
        Check(sp != null && sp.Number == 3 && sp.Radius == 12f, "a numbered step is kept");

        var again = new EditSession(back, file);
        using (var a = es.FlattenSurface()) using (var b = again.FlattenSurface()) Check(Same(a, b), "the reopened picture looks exactly the same");
        Check(!again.IsDirty && !again.CanUndo, "a reopened picture starts clean");
        again.AddStroke(new Stroke(EditTool.Pen, Color.Blue, 4f, new[] { new PointF(1, 1), new PointF(9, 9) }));
        PicProject.Save(again.State, file);
        var third = PicProject.Load(file);
        Check(third.Layers[2].Objects.Length == 1, "saving over the same project file works");
        foreach (var l in third.Layers) if (l.Pixels != null) l.Pixels.Dispose();
        again.Dispose();
        es.Dispose();

        // broken files are refused, not half-loaded
        string bad = Path.Combine(dir, "pt-bad" + PicProject.Extension);
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        bool refused = false;
        try { PicProject.Load(bad); } catch (InvalidDataException) { refused = true; }
        Check(refused, "a file that is not a layered picture is refused");
        var bytes = File.ReadAllBytes(file);
        Array.Resize(ref bytes, bytes.Length / 2);
        File.WriteAllBytes(bad, bytes);
        refused = false;
        try { PicProject.Load(bad); } catch (Exception) { refused = true; }
        Check(refused, "a file that was cut short is refused");
        foreach (var f in new[] { file, bad }) try { File.Delete(f); } catch { }
    }

    /// <summary>A hand-made EXIF block: orientation 6, a camera name, a recorded size, and a preview picture in the second list.</summary>
    static byte[] MakeExif(out int thumbAt, out int thumbLen)
    {
        var t = new List<byte>();
        Action<int> u16 = v => { t.Add((byte)v); t.Add((byte)(v >> 8)); };
        Action<int> u32 = v => { t.Add((byte)v); t.Add((byte)(v >> 8)); t.Add((byte)(v >> 16)); t.Add((byte)(v >> 24)); };
        t.AddRange(new byte[] { (byte)'I', (byte)'I' }); u16(42); u32(8);
        // IFD0 at 8: Make, Orientation, ExifIFD pointer
        const int ifd0 = 8, n0 = 3, makeAt = ifd0 + 2 + n0 * 12 + 4;         // 50
        const int exifAt = makeAt + 8;                                       // 58
        const int ifd1 = exifAt + 2 + 2 * 12 + 4;                            // 88
        const int thumb = ifd1 + 2 + 2 * 12 + 4;                             // 118
        u16(n0);
        u16(0x010F); u16(2); u32(8); u32(makeAt);                            // Make: ASCII, 8 bytes
        u16(0x0112); u16(3); u32(1); u16(6); u16(0);                         // Orientation = 6
        u16(0x8769); u16(4); u32(1); u32(exifAt);                            // Exif sub-list
        u32(ifd1);
        t.AddRange(System.Text.Encoding.ASCII.GetBytes("TestCam\0"));
        u16(2);
        u16(0xA002); u16(4); u32(1); u32(4000);                              // recorded width
        u16(0xA003); u16(3); u32(1); u16(3000); u16(0);                      // recorded height
        u32(0);
        u16(2);
        u16(0x0201); u16(4); u32(1); u32(thumb);
        u16(0x0202); u16(4); u32(1); u32(12);
        u32(0);
        t.AddRange(System.Text.Encoding.ASCII.GetBytes("SECRETTHUMB!"));
        var seg = new List<byte> { 0xFF, 0xE1 };
        int len = 2 + 6 + t.Count;
        seg.Add((byte)(len >> 8)); seg.Add((byte)len);
        seg.AddRange(System.Text.Encoding.ASCII.GetBytes("Exif\0\0"));
        seg.AddRange(t);
        thumbAt = 10 + thumb; thumbLen = 12;
        return seg.ToArray();
    }

    static bool Contains(byte[] hay, string needle)
    {
        var n = System.Text.Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i + n.Length <= hay.Length; i++)
        {
            int k = 0;
            while (k < n.Length && hay[i + k] == n[k]) k++;
            if (k == n.Length) return true;
        }
        return false;
    }

    static void Formats(string dir)
    {
        ImageCache.Initialize();
        var made = new List<string>();
        Func<string, string> F = name => { string f = Path.Combine(dir, name); made.Add(f); return f; };

        // a picture with solid, see-through and half see-through parts
        PicSurface src = Gradient(160, 100);
        src.Fill(new Rectangle(100, 0, 60, 50), 0);
        src.Fill(new Rectangle(100, 50, 60, 50), PicSurface.Premultiply(Color.FromArgb(128, 255, 0, 0)));

        // the bitmap bridge used to talk to FFmpeg
        string bmpFile = F("pt-bridge.bmp");
        using (var b = src.ToBitmap()) PicCodecs.WriteBmp(b, bmpFile);
        using (var fs = File.OpenRead(bmpFile))
        using (var back = PicCodecs.ReadBmp(fs))
            Check(Same(src, back), "the bitmap bridge keeps every pixel, see-through ones too");

        // WebP, exact
        string webp = F("pt-exact.webp");
        using (var b = src.ToBitmap()) ImageSaver.SaveWith(b, webp, new SaveOptions { Lossless = true });
        Check(File.Exists(webp) && new FileInfo(webp).Length > 0, "a WebP picture is written");
        var li = LoadedImage.Load(webp);
        Check(li.Surface != null && li.Surface.Size == src.Size && li.HasAlpha, "a WebP picture opens, with its transparency");
        bool exact = true;
        foreach (var pt in new[] { new Point(5, 5), new Point(90, 80), new Point(130, 20), new Point(130, 75) })
        {
            Color a = src.GetColor(pt.X, pt.Y), c = li.Surface.GetColor(pt.X, pt.Y);
            if (Math.Abs(a.A - c.A) > 1 || (a.A > 0 && !Near(c, a.R, a.G, a.B, 2))) exact = false;
        }
        Check(exact, "exact WebP gives the same pixels back");
        ImageCache.Delete(li.CachePath); li.Dispose();

        // WebP, by quality
        string webLo = F("pt-lo.webp"), webHi = F("pt-hi.webp");
        using (var photo = new PicSurface(320, 240))
        {
            var rnd = new Random(7);
            unsafe
            {
                for (int y = 0; y < 240; y++)
                {
                    uint* p = photo.Row(y);
                    for (int x = 0; x < 320; x++)
                    {
                        int n = rnd.Next(40);
                        p[x] = 0xFF000000u | ((uint)Math.Min(255, x / 2 + n) << 16) | ((uint)Math.Min(255, y + n) << 8) | (uint)Math.Min(255, (x + y) / 3 + n);
                    }
                }
            }
            using (var b = photo.ToBitmap())
            {
                ImageSaver.SaveWith(b, webLo, new SaveOptions { Quality = 20 });
                ImageSaver.SaveWith(b, webHi, new SaveOptions { Quality = 95 });
                long lo = new FileInfo(webLo).Length, hi = new FileInfo(webHi).Length;
                Check(lo > 0 && hi > lo * 1.5, "WebP quality changes the file size (" + lo + " and " + hi + " bytes)");
                var back = LoadedImage.Load(webHi);
                Check(back.Surface.Size == photo.Size && !back.HasAlpha && Near(back.Surface.GetColor(200, 120), photo.GetColor(200, 120).R, photo.GetColor(200, 120).G, photo.GetColor(200, 120).B, 40), "a WebP photo opens looking like the photo");
                ImageCache.Delete(back.CachePath); back.Dispose();

                // JPEG quality
                string jLo = F("pt-lo.jpg"), jHi = F("pt-hi.jpg");
                ImageSaver.SaveWith(b, jLo, new SaveOptions { Quality = 25 });
                ImageSaver.SaveWith(b, jHi, new SaveOptions { Quality = 97 });
                long jl = new FileInfo(jLo).Length, jh = new FileInfo(jHi).Length;
                Check(jl > 0 && jh > jl * 1.5, "JPEG quality changes the file size (" + jl + " and " + jh + " bytes)");
                Check(PicCodecs.EncodeJpeg(b, 25).LongLength == jl, "the size guess in the save box matches what is written");
                Check(JpegDetails.Read(File.ReadAllBytes(jHi)) == null, "a saved JPEG carries no photo details unless asked");

                // photo details
                int thumbAt, thumbLen;
                byte[] exif = MakeExif(out thumbAt, out thumbLen);
                string orig = F("pt-details.jpg");
                File.WriteAllBytes(orig, JpegDetails.Insert(PicCodecs.EncodeJpeg(b, 80), exif));
                Check(JpegDetails.Has(orig) && Contains(File.ReadAllBytes(orig), "SECRETTHUMB!"), "the test photo has details and a preview inside");
                string kept = F("pt-kept.jpg");
                ImageSaver.SaveWith(b, kept, new SaveOptions { Quality = 90, DetailsFrom = orig });
                byte[] keptBytes = File.ReadAllBytes(kept);
                byte[] seg = JpegDetails.Read(keptBytes);
                Check(seg != null && Contains(seg, "TestCam"), "asked to, a saved JPEG keeps the camera details");
                Check(!Contains(keptBytes, "SECRETTHUMB!") && !Contains(keptBytes, "THUMB"), "the old preview picture inside the details is wiped");
                Check(seg != null && seg[10 + 8 + 2 + 12 + 8] == 1, "the copy is marked upright, because its pixels already are");
                Check(seg != null && BitConverter.ToInt32(seg, 10 + 58 + 2 + 8) == 320 && BitConverter.ToUInt16(seg, 10 + 58 + 2 + 12 + 8) == 240, "the recorded size is the copy's own");
                Check(seg != null && BitConverter.ToInt32(seg, 10 + 8 + 2 + 3 * 12) == 0, "the second list of details is unlinked");
                using (var img = Image.FromFile(kept)) Check(img.Width == 320 && img.Height == 240, "a JPEG with details still opens normally");
                var liKept = LoadedImage.Load(kept);
                Check(liKept.Surface.Width == 320 && liKept.Surface.Height == 240, "and it is not turned a second time");
                ImageCache.Delete(liKept.CachePath); liKept.Dispose();
                Check(Contains(File.ReadAllBytes(orig), "SECRETTHUMB!"), "the original photo is untouched");
            }
        }
        Check(JpegDetails.Clean(new byte[] { 0xFF, 0xE1, 0, 20, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }) == null, "details that can't be understood are left out, not guessed at");
        Check(JpegDetails.Read(new byte[] { 1, 2, 3 }) == null && JpegDetails.Read(null) == null, "a file that is not a JPEG has no details");
        {
            int ta, tl;
            var rows = JpegDetails.Describe(MakeExif(out ta, out tl));
            Check(rows.Count == 1 && rows[0].Key == "Camera" && rows[0].Value == "TestCam", "the details box reads the camera name");
            Check(JpegDetails.Describe(null).Count == 0 && JpegDetails.Describe(new byte[40]).Count == 0, "no details gives an empty list, not an error");
        }
        Check(MainForm.NaturalCompare("img2.png", "img10.jpg") < 0 && MainForm.NaturalCompare("IMG3.bmp", "img10.jpg") < 0 && MainForm.NaturalCompare("a.png", "B.png") < 0 &&
              MainForm.NaturalCompare("x07.png", "x7.png") != 0 && MainForm.NaturalCompare("same.png", "same.png") == 0 && MainForm.NaturalCompare("b", "a10") > 0,
              "pictures in a folder are stepped through in the order File Explorer shows them");

        // a format Windows can't read by itself (PPM), through FFmpeg
        string ppm = F("pt-small.ppm");
        using (var fs = File.Create(ppm))
        {
            var head = System.Text.Encoding.ASCII.GetBytes("P6\n3 2\n255\n");
            fs.Write(head, 0, head.Length);
            fs.Write(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30, 200, 100, 50, 255, 255, 255 }, 0, 18);
        }
        li = LoadedImage.Load(ppm);
        Check(li.Surface.Width == 3 && li.Surface.Height == 2 && li.Surface.GetColor(0, 0) == Color.FromArgb(255, 255, 0, 0) && li.Surface.GetColor(2, 0) == Color.FromArgb(255, 0, 0, 255) &&
              li.Surface.GetColor(0, 1) == Color.FromArgb(255, 10, 20, 30) && li.Surface.GetColor(1, 1) == Color.FromArgb(255, 200, 100, 50), "a PPM picture opens through FFmpeg, the right way up");
        ImageCache.Delete(li.CachePath); li.Dispose();

        string junk = F("pt-junk.webp");
        File.WriteAllBytes(junk, System.Text.Encoding.ASCII.GetBytes("this is not a picture at all"));
        string message = null;
        try { LoadedImage.Load(junk).Dispose(); } catch (Exception ex) { message = ex.Message; }
        Check(message != null && message.IndexOf("video", StringComparison.OrdinalIgnoreCase) < 0 && message.IndexOf('[') < 0 && message.IndexOf("stream", StringComparison.OrdinalIgnoreCase) < 0, "a broken picture gives a plain message (" + message + ")");
        Check(Directory.GetFiles(ImageCache.Folder, "*.webp").Length == 0, "nothing is left in the temporary folder after a failed open");

        // a layered picture opens flat in the viewer, with its layers kept for the editor
        string proj = F("pt-view" + PicProject.Extension);
        var es = new EditSession(src.Clone(), null, false);
        es.AddLayer("Dot", Solid(20, 20, Color.Blue), 10, 10);
        PicProject.Save(es.State, proj);
        li = LoadedImage.Load(proj);
        Check(li.IsProject && li.Surface != null && li.Surface.Size == src.Size && li.Surface.GetColor(15, 15) == Color.FromArgb(255, 0, 0, 255) && li.Surface.GetColor(50, 50) == src.GetColor(50, 50), "a layered picture is shown as one picture");
        var st = PicProject.Load(li.CachePath);
        Check(st.Layers.Length == 2, "and its layers are still there for the editor");
        foreach (var l in st.Layers) if (l.Pixels != null) l.Pixels.Dispose();
        ImageCache.Delete(li.CachePath); li.Dispose();
        es.Dispose();
        src.Dispose();

        Check(FileTypes.Classify("holiday.HEIC") == FileKind.Image && FileTypes.NeedsFfmpeg("a.avif") && !FileTypes.NeedsFfmpeg("a.png") && FileTypes.Classify("a.cr2") == FileKind.Incompatible, "the list of picture kinds is right");
        foreach (var f in made) try { File.Delete(f); } catch { }
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
            Adjustments();
            StraightenAndTrim();
            Projects(dir);
            Formats(dir);
        }
        catch (Exception ex) { Console.WriteLine("FAIL exception: " + ex); fails++; }
        Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
        return fails;
    }
}
