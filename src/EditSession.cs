using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace StillsAndReels
{
    internal enum EditTool { Pen, Pencil, Highlighter, Eraser, Crop, Pan }

    /// <summary>An ink stroke in image pixel coordinates. Immutable once finished.</summary>
    internal sealed class Stroke
    {
        public readonly EditTool Tool;
        public readonly Color Color;
        public readonly float Width;
        public readonly PointF[] Points;

        public Stroke(EditTool tool, Color color, float width, PointF[] points)
        {
            Tool = tool; Color = color; Width = width; Points = points;
        }

        public Stroke Transform(Func<PointF, PointF> f)
        {
            var pts = new PointF[Points.Length];
            for (int i = 0; i < pts.Length; i++) pts[i] = f(Points[i]);
            return new Stroke(Tool, Color, Width, pts);
        }

        /// <summary>True if a circle at p with the given radius touches this stroke.</summary>
        public bool HitTest(PointF p, float radius)
        {
            float r = radius + Width / 2f;
            float r2 = r * r;
            if (Points.Length == 1) return Dist2(p, Points[0]) <= r2;
            for (int i = 1; i < Points.Length; i++)
                if (SegDist2(p, Points[i - 1], Points[i]) <= r2) return true;
            return false;
        }

        static float Dist2(PointF a, PointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }

        static float SegDist2(PointF p, PointF a, PointF b)
        {
            float vx = b.X - a.X, vy = b.Y - a.Y;
            float len2 = vx * vx + vy * vy;
            if (len2 <= 0.0001f) return Dist2(p, a);
            float t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2;
            t = Math.Max(0, Math.Min(1, t));
            return Dist2(p, new PointF(a.X + t * vx, a.Y + t * vy));
        }

        public static void Draw(Graphics g, EditTool tool, Color color, float width, IList<PointF> pts)
        {
            if (pts == null || pts.Count == 0) return;
            Color c = color;
            if (tool == EditTool.Highlighter) c = Color.FromArgb(110, color);
            else if (tool == EditTool.Pencil) c = Color.FromArgb(205, color);

            if (pts.Count == 1)
            {
                float r = width / 2f;
                using (var b = new SolidBrush(c))
                {
                    if (tool == EditTool.Highlighter)
                        g.FillRectangle(b, pts[0].X - r, pts[0].Y - r, width, width);
                    else
                        g.FillEllipse(b, pts[0].X - r, pts[0].Y - r, width, width);
                }
                return;
            }

            var arr = new PointF[pts.Count];
            pts.CopyTo(arr, 0);
            using (var pen = new Pen(c, width))
            {
                if (tool == EditTool.Highlighter)
                {
                    pen.StartCap = pen.EndCap = LineCap.Square;
                    pen.LineJoin = LineJoin.Round;
                }
                else
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                }
                // Drawing as one path keeps a semi-transparent stroke from darkening where it overlaps itself.
                using (var path = new GraphicsPath())
                {
                    path.AddLines(arr);
                    g.DrawPath(pen, path);
                }
            }
        }

        public void Draw(Graphics g) { Draw(g, Tool, Color, Width, Points); }
    }

    /// <summary>One undoable state of an edit: a base bitmap plus ink strokes on top.</summary>
    internal sealed class EditState
    {
        public readonly Bitmap Base;
        public readonly Stroke[] Strokes;
        public EditState(Bitmap b, Stroke[] s) { Base = b; Strokes = s; }
    }

    internal sealed class EditSession : IDisposable
    {
        const int MaxUndo = 60;

        public readonly string SourcePath;
        readonly List<EditState> undo = new List<EditState>();
        readonly List<EditState> redo = new List<EditState>();
        readonly HashSet<Bitmap> allBases = new HashSet<Bitmap>();
        EditState saved;

        public EditState Current { get; private set; }
        public event EventHandler Changed;

        public EditSession(Image source, string sourcePath) : this(source, sourcePath, false) { }

        /// <summary>startDirty: a brand-new picture (e.g. pasted) that has never been saved.</summary>
        public EditSession(Image source, string sourcePath, bool startDirty)
        {
            SourcePath = sourcePath;
            var b = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
            }
            allBases.Add(b);
            Current = new EditState(b, new Stroke[0]);
            saved = startDirty ? null : Current;
        }

        public Size Size { get { return Current.Base.Size; } }
        public bool CanUndo { get { return undo.Count > 0; } }
        public bool CanRedo { get { return redo.Count > 0; } }
        public bool IsDirty { get { return !ReferenceEquals(Current, saved); } }
        public void MarkSaved() { saved = Current; Raise(); }

        void Commit(EditState next)
        {
            undo.Add(Current);
            if (undo.Count > MaxUndo) undo.RemoveAt(0);
            redo.Clear();
            Current = next;
            allBases.Add(next.Base);
            Raise();
        }

        void Raise() { var h = Changed; if (h != null) h(this, EventArgs.Empty); }

        public void Undo()
        {
            if (undo.Count == 0) return;
            redo.Add(Current);
            Current = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            Raise();
        }

        public void Redo()
        {
            if (redo.Count == 0) return;
            undo.Add(Current);
            Current = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            Raise();
        }

        public void AddStroke(Stroke s)
        {
            var list = new List<Stroke>(Current.Strokes) { s };
            Commit(new EditState(Current.Base, list.ToArray()));
        }

        public void RemoveStrokes(ICollection<Stroke> remove)
        {
            if (remove.Count == 0) return;
            var list = new List<Stroke>();
            foreach (var s in Current.Strokes) if (!remove.Contains(s)) list.Add(s);
            Commit(new EditState(Current.Base, list.ToArray()));
        }

        public void ClearInk()
        {
            if (Current.Strokes.Length == 0) return;
            Commit(new EditState(Current.Base, new Stroke[0]));
        }

        public void Crop(Rectangle r)
        {
            r.Intersect(new Rectangle(Point.Empty, Size));
            if (r.Width < 1 || r.Height < 1 || r.Size == Size) return;
            var b = Current.Base.Clone(r, PixelFormat.Format32bppArgb);
            float dx = r.X, dy = r.Y;
            Commit(new EditState(b, MapStrokes(p => new PointF(p.X - dx, p.Y - dy))));
        }

        public void RotateFlip(RotateFlipType type)
        {
            float w = Size.Width, h = Size.Height;
            Func<PointF, PointF> f;
            switch (type)
            {
                case RotateFlipType.Rotate90FlipNone: f = p => new PointF(h - p.Y, p.X); break;
                case RotateFlipType.Rotate270FlipNone: f = p => new PointF(p.Y, w - p.X); break;
                case RotateFlipType.Rotate180FlipNone: f = p => new PointF(w - p.X, h - p.Y); break;
                case RotateFlipType.RotateNoneFlipX: f = p => new PointF(w - p.X, p.Y); break;
                case RotateFlipType.RotateNoneFlipY: f = p => new PointF(p.X, h - p.Y); break;
                default: return;
            }
            var b = (Bitmap)Current.Base.Clone();
            b.RotateFlip(type);
            Commit(new EditState(b, MapStrokes(f)));
        }

        /// <summary>Makes the canvas larger (never smaller). New area is transparent. Ink is baked in.</summary>
        public void ExpandCanvas(int w, int h)
        {
            w = Math.Max(w, Size.Width); h = Math.Max(h, Size.Height);
            if (w == Size.Width && h == Size.Height) return;
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                using (var flat = Flatten()) g.DrawImageUnscaled(flat, 0, 0);
            }
            Commit(new EditState(b, new Stroke[0]));
        }

        /// <summary>Bakes a pasted picture (and the existing ink) into the base image as one undo step.</summary>
        public void PasteImage(Image img, RectangleF rect)
        {
            var b = Flatten();
            using (var g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceOver;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(img, rect);
            }
            Commit(new EditState(b, new Stroke[0]));
        }

        Stroke[] MapStrokes(Func<PointF, PointF> f)
        {
            var arr = new Stroke[Current.Strokes.Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = Current.Strokes[i].Transform(f);
            return arr;
        }

        /// <summary>Renders the base image with all ink into a new bitmap.</summary>
        public Bitmap Flatten()
        {
            var b = new Bitmap(Size.Width, Size.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(Current.Base, new Rectangle(Point.Empty, Size));
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                foreach (var s in Current.Strokes) s.Draw(g);
            }
            return b;
        }

        public void Dispose()
        {
            foreach (var b in allBases) b.Dispose();
            allBases.Clear();
            undo.Clear(); redo.Clear();
        }
    }

    internal static class ImageSaver
    {
        /// <summary>Saves a bitmap. The format follows the file extension.</summary>
        public static void Save(Bitmap bmp, string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg" || ext == ".jpe" || ext == ".jfif")
            {
                using (var flat = OnWhite(bmp))
                {
                    var codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (var p = new EncoderParameters(1))
                    {
                        p.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
                        flat.Save(path, codec, p);
                    }
                }
            }
            else if (ext == ".bmp" || ext == ".dib")
            {
                using (var flat = OnWhite(bmp)) flat.Save(path, ImageFormat.Bmp);
            }
            else if (ext == ".gif") bmp.Save(path, ImageFormat.Gif);
            else if (ext == ".tif" || ext == ".tiff") bmp.Save(path, ImageFormat.Tiff);
            else bmp.Save(path, ImageFormat.Png);
        }

        static Bitmap OnWhite(Bitmap src)
        {
            var b = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.White);
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            }
            return b;
        }
    }
}
