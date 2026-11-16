using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace StillsAndReels
{
    internal enum EditTool
    {
        // the first six keep their old order: settings and tests refer to them
        Pen, Pencil, Highlighter, Eraser, Crop, Pan,
        Select,                                   // pick, move and resize shapes, text and layers
        RectSelect, EllipseSelect, Lasso, Wand,   // pixel selections
        Line, Arrow, Rect, RoundRect, Ellipse,    // shapes
        Text, Step,                               // text boxes and numbered step markers
        Fill, Picker,                             // fill bucket, colour picker
        Redact                                    // blur or pixelate an area
    }

    internal enum ShapeKind { Line, Arrow, Rect, RoundRect, Ellipse }

    /// <summary>
    /// A straight-line mapping of the canvas (move, scale, quarter turn or mirror), used to carry shapes along when
    /// the picture is cropped, resized, rotated or flipped.  x' = A·x + C·y + Tx,  y' = B·x + D·y + Ty.
    /// </summary>
    internal struct PicMap
    {
        public float A, B, C, D, Tx, Ty;

        public PointF Apply(PointF p) { return new PointF(A * p.X + C * p.Y + Tx, B * p.X + D * p.Y + Ty); }

        /// <summary>How much lengths grow (1 for moves, turns and mirrors).</summary>
        public float Scale { get { return (float)Math.Sqrt(Math.Abs(A * D - B * C)); } }

        /// <summary>Only a move and an upright scale: text stays readable.</summary>
        public bool IsUpright { get { return B == 0 && C == 0 && A > 0 && D > 0; } }

        /// <summary>Rectangles stay rectangles (no slant): moves, scales, quarter turns and mirrors.</summary>
        public bool KeepsBoxes { get { return (B == 0 && C == 0) || (A == 0 && D == 0); } }

        public static PicMap Translate(float dx, float dy) { return new PicMap { A = 1, D = 1, Tx = dx, Ty = dy }; }
        public static PicMap Scaling(float sx, float sy) { return new PicMap { A = sx, D = sy }; }

        /// <summary>Maps one box onto another (resizing a shape by its handles).</summary>
        public static PicMap BoxToBox(RectangleF from, RectangleF to)
        {
            float sx = from.Width > 0.0001f ? to.Width / from.Width : 1f, sy = from.Height > 0.0001f ? to.Height / from.Height : 1f;
            return new PicMap { A = sx, D = sy, Tx = to.X - from.X * sx, Ty = to.Y - from.Y * sy };
        }

        /// <summary>The mapping of a rotate or flip of a w × h canvas.</summary>
        public static PicMap ForRotateFlip(RotateFlipType type, float w, float h)
        {
            switch (type)
            {
                case RotateFlipType.Rotate90FlipNone: return new PicMap { C = -1, Tx = h, B = 1 };            // (h - y, x)
                case RotateFlipType.Rotate270FlipNone: return new PicMap { C = 1, B = -1, Ty = w };           // (y, w - x)
                case RotateFlipType.Rotate180FlipNone: return new PicMap { A = -1, Tx = w, D = -1, Ty = h };  // (w - x, h - y)
                case RotateFlipType.RotateNoneFlipX: return new PicMap { A = -1, Tx = w, D = 1 };             // (w - x, y)
                case RotateFlipType.RotateNoneFlipY: return new PicMap { A = 1, D = -1, Ty = h };             // (x, h - y)
                default: throw new ArgumentException("Unsupported rotation.");
            }
        }
    }

    /// <summary>
    /// Something drawn on a layer that stays editable: an ink stroke, a shape, a text box or a step marker.
    /// Objects never change once made; an edit makes a new object, which keeps undo simple and cheap.
    /// All coordinates are canvas pixels.
    /// </summary>
    internal abstract class PicObject
    {
        /// <summary>Everything the object paints, with a little room for smooth edges.</summary>
        public abstract RectangleF Bounds { get; }

        /// <summary>The box the handles sit on (the geometry, without the line width).</summary>
        public abstract RectangleF Box { get; }

        public abstract void Draw(Graphics g);

        /// <summary>True if a circle at p with the given radius touches the object.</summary>
        public abstract bool HitTest(PointF p, float radius);

        /// <summary>The object carried through a canvas mapping, or null when it can't follow (it is then drawn into the layer).</summary>
        public abstract PicObject Map(PicMap m);

        public PicObject Offset(float dx, float dy) { return Map(PicMap.Translate(dx, dy)); }

        /// <summary>The object stretched so its box becomes the given one.</summary>
        public virtual PicObject WithBox(RectangleF box)
        {
            var from = Box;
            if (box.Width < 1f) box.Width = 1f;
            if (box.Height < 1f) box.Height = 1f;
            return Map(PicMap.BoxToBox(from, box)) ?? this;
        }

        public abstract Color Color { get; }
        public abstract float Width { get; }
        public virtual Color Fill { get { return Color.Empty; } }
        public virtual bool HasWidth { get { return true; } }
        public virtual bool HasFill { get { return false; } }

        public abstract PicObject WithColor(Color c);
        public virtual PicObject WithWidth(float w) { return this; }
        public virtual PicObject WithFill(Color c) { return this; }

        /// <summary>The canvas area to repaint, in whole pixels.</summary>
        public Rectangle PixelBounds
        {
            get
            {
                var b = Bounds;
                return Rectangle.FromLTRB((int)Math.Floor(b.Left) - 2, (int)Math.Floor(b.Top) - 2, (int)Math.Ceiling(b.Right) + 2, (int)Math.Ceiling(b.Bottom) + 2);
            }
        }

        protected static float Dist2(PointF a, PointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }

        protected static float SegDist2(PointF p, PointF a, PointF b)
        {
            float vx = b.X - a.X, vy = b.Y - a.Y;
            float len2 = vx * vx + vy * vy;
            if (len2 <= 0.0001f) return Dist2(p, a);
            float t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2;
            t = Math.Max(0, Math.Min(1, t));
            return Dist2(p, new PointF(a.X + t * vx, a.Y + t * vy));
        }

        protected static RectangleF Norm(PointF a, PointF b)
        {
            return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }
    }

    /// <summary>An ink stroke (pen, pencil or highlighter) in canvas pixel coordinates.</summary>
    internal sealed class Stroke : PicObject
    {
        public readonly EditTool Tool;
        readonly Color color;
        readonly float width;
        public readonly PointF[] Points;
        RectangleF bounds;
        bool haveBounds;

        public Stroke(EditTool tool, Color color, float width, PointF[] points)
        {
            Tool = tool; this.color = color; this.width = width; Points = points;
        }

        public override Color Color { get { return color; } }
        public override float Width { get { return width; } }
        public override PicObject WithColor(Color c) { return new Stroke(Tool, c, width, Points); }
        public override PicObject WithWidth(float w) { return new Stroke(Tool, color, Math.Max(0.5f, w), Points); }

        public Stroke Transform(Func<PointF, PointF> f)
        {
            var pts = new PointF[Points.Length];
            for (int i = 0; i < pts.Length; i++) pts[i] = f(Points[i]);
            return new Stroke(Tool, color, width, pts);
        }

        public override PicObject Map(PicMap m)
        {
            var pts = new PointF[Points.Length];
            for (int i = 0; i < pts.Length; i++) pts[i] = m.Apply(Points[i]);
            return new Stroke(Tool, color, Math.Max(0.5f, width * m.Scale), pts);
        }

        public override RectangleF Box
        {
            get
            {
                if (!haveBounds)
                {
                    float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
                    foreach (var p in Points)
                    {
                        if (p.X < l) l = p.X; if (p.X > r) r = p.X;
                        if (p.Y < t) t = p.Y; if (p.Y > b) b = p.Y;
                    }
                    bounds = Points.Length == 0 ? RectangleF.Empty : RectangleF.FromLTRB(l, t, r, b);
                    haveBounds = true;
                }
                return bounds;
            }
        }

        public override RectangleF Bounds { get { return RectangleF.Inflate(Box, width * 0.75f + 1, width * 0.75f + 1); } }

        public override bool HitTest(PointF p, float radius)
        {
            float r = radius + width / 2f;
            float r2 = r * r;
            if (Points.Length == 1) return Dist2(p, Points[0]) <= r2;
            for (int i = 1; i < Points.Length; i++)
                if (SegDist2(p, Points[i - 1], Points[i]) <= r2) return true;
            return false;
        }

        public static void Draw(Graphics g, EditTool tool, Color color, float width, IList<PointF> pts)
        {
            if (pts == null || pts.Count == 0) return;
            Color c = color;
            if (tool == EditTool.Highlighter) c = Color.FromArgb(110 * color.A / 255, color);
            else if (tool == EditTool.Pencil) c = Color.FromArgb(205 * color.A / 255, color);

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

        public override void Draw(Graphics g) { Draw(g, Tool, color, width, Points); }
    }

    /// <summary>A line, arrow, rectangle, rounded rectangle or ellipse between two corner points.</summary>
    internal sealed class ShapeObject : PicObject
    {
        public readonly ShapeKind Kind;
        public readonly PointF A, B;
        readonly Color color, fill;
        readonly float width;

        public ShapeObject(ShapeKind kind, PointF a, PointF b, Color color, float width, Color fill)
        {
            Kind = kind; A = a; B = b; this.color = color; this.width = Math.Max(0.5f, width); this.fill = fill;
        }

        public bool IsLine { get { return Kind == ShapeKind.Line || Kind == ShapeKind.Arrow; } }
        public override Color Color { get { return color; } }
        public override float Width { get { return width; } }
        public override Color Fill { get { return fill; } }
        public override bool HasFill { get { return !IsLine; } }
        public override PicObject WithColor(Color c) { return new ShapeObject(Kind, A, B, c, width, fill); }
        public override PicObject WithWidth(float w) { return new ShapeObject(Kind, A, B, color, w, fill); }
        public override PicObject WithFill(Color c) { return IsLine ? this : new ShapeObject(Kind, A, B, color, width, c); }
        public ShapeObject WithEnds(PointF a, PointF b) { return new ShapeObject(Kind, a, b, color, width, fill); }

        public override RectangleF Box { get { return Norm(A, B); } }

        float HeadLength { get { return Math.Max(9f, width * 4.2f); } }

        public override RectangleF Bounds
        {
            get
            {
                float pad = width * 0.75f + 1 + (Kind == ShapeKind.Arrow ? HeadLength * 0.6f : 0);
                return RectangleF.Inflate(Box, pad, pad);
            }
        }

        public override PicObject Map(PicMap m)
        {
            if (!IsLine && !m.KeepsBoxes) return null;
            return new ShapeObject(Kind, m.Apply(A), m.Apply(B), color, Math.Max(0.5f, width * m.Scale), fill);
        }

        public override PicObject WithBox(RectangleF box)
        {
            if (!IsLine) return new ShapeObject(Kind, box.Location, new PointF(box.Right, box.Bottom), color, width, fill);
            return base.WithBox(box);
        }

        public override bool HitTest(PointF p, float radius)
        {
            float r = radius + width / 2f;
            if (IsLine) return SegDist2(p, A, B) <= r * r;
            var box = Box;
            if (!RectangleF.Inflate(box, r, r).Contains(p)) return false;
            if (Kind == ShapeKind.Ellipse)
            {
                float rx = box.Width / 2f, ry = box.Height / 2f;
                if (rx < 0.5f || ry < 0.5f) return true;
                float nx = (p.X - (box.X + rx)) / (rx + r), ny = (p.Y - (box.Y + ry)) / (ry + r);
                if (nx * nx + ny * ny > 1f) return false;
                if (fill.A > 0) return true;
                float ix = rx - r, iy = ry - r;
                if (ix <= 0 || iy <= 0) return true;
                float mx = (p.X - (box.X + rx)) / ix, my = (p.Y - (box.Y + ry)) / iy;
                return mx * mx + my * my >= 1f;
            }
            if (fill.A > 0) return true;
            var inner = RectangleF.Inflate(box, -r, -r);
            return inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p);
        }

        public override void Draw(Graphics g) { Draw(g, Kind, A, B, color, width, fill); }

        public static void Draw(Graphics g, ShapeKind kind, PointF a, PointF b, Color color, float width, Color fill)
        {
            var box = Norm(a, b);
            using (var pen = new Pen(color, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                switch (kind)
                {
                    case ShapeKind.Line:
                        g.DrawLine(pen, a, b);
                        break;
                    case ShapeKind.Arrow:
                        DrawArrow(g, a, b, color, width);
                        break;
                    case ShapeKind.Ellipse:
                        if (fill.A > 0) using (var br = new SolidBrush(fill)) g.FillEllipse(br, box);
                        if (color.A > 0) g.DrawEllipse(pen, box);
                        break;
                    case ShapeKind.Rect:
                        pen.LineJoin = LineJoin.Miter;
                        if (fill.A > 0) using (var br = new SolidBrush(fill)) g.FillRectangle(br, box);
                        if (color.A > 0) g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                        break;
                    case ShapeKind.RoundRect:
                        float rad = Math.Min(Math.Min(box.Width, box.Height) / 2f, Math.Max(4f, Math.Min(box.Width, box.Height) * 0.18f));
                        using (var path = RoundedPath(box, rad))
                        {
                            if (fill.A > 0) using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                            if (color.A > 0) g.DrawPath(pen, path);
                        }
                        break;
                }
            }
        }

        static void DrawArrow(Graphics g, PointF a, PointF b, Color color, float width)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return;
            dx /= len; dy /= len;
            float head = Math.Min(Math.Max(9f, width * 4.2f), len);
            float half = head * 0.42f;
            // the shaft stops inside the head so its round end doesn't poke through the tip
            var neck = new PointF(b.X - dx * head * 0.8f, b.Y - dy * head * 0.8f);
            using (var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
                if (len > head * 0.8f) g.DrawLine(pen, a, neck);
            var back = new PointF(b.X - dx * head, b.Y - dy * head);
            using (var br = new SolidBrush(color))
                g.FillPolygon(br, new[] { b, new PointF(back.X - dy * half, back.Y + dx * half), new PointF(back.X + dy * half, back.Y - dx * half) });
        }

        public static GraphicsPath RoundedPath(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            if (d < 0.5f || r.Width < 1 || r.Height < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>A block of text. Size is the letter height in canvas pixels.</summary>
    internal sealed class TextObject : PicObject
    {
        public readonly string Text, FontName;
        public readonly PointF Location;     // top-left corner
        public readonly float Size;
        public readonly bool Bold, Italic;
        readonly Color color, back;          // back: a filled box behind the text (none if transparent)
        RectangleF box;
        bool measured;

        public TextObject(string text, string fontName, float size, bool bold, bool italic, PointF location, Color color, Color back)
        {
            Text = text ?? ""; FontName = string.IsNullOrEmpty(fontName) ? "Segoe UI" : fontName;
            Size = Math.Max(4f, size); Bold = bold; Italic = italic; Location = location; this.color = color; this.back = back;
        }

        public override Color Color { get { return color; } }
        public override float Width { get { return Size; } }
        public override Color Fill { get { return back; } }
        public override bool HasFill { get { return true; } }
        public override PicObject WithColor(Color c) { return new TextObject(Text, FontName, Size, Bold, Italic, Location, c, back); }
        public override PicObject WithWidth(float w) { return new TextObject(Text, FontName, w, Bold, Italic, Location, color, back); }
        public override PicObject WithFill(Color c) { return new TextObject(Text, FontName, Size, Bold, Italic, Location, color, c); }
        public TextObject WithText(string t) { return new TextObject(t, FontName, Size, Bold, Italic, Location, color, back); }
        public TextObject WithFont(string name, bool bold, bool italic) { return new TextObject(Text, name, Size, bold, italic, Location, color, back); }

        float Pad { get { return back.A > 0 ? Size * 0.28f : 0f; } }

        static FontFamily Family(string name)
        {
            try { return new FontFamily(name); }
            catch { return new FontFamily(GenericFontFamilies.SansSerif); }
        }

        GraphicsPath BuildPath()
        {
            var path = new GraphicsPath();
            using (var fam = Family(FontName))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                var style = (Bold ? FontStyle.Bold : 0) | (Italic ? FontStyle.Italic : 0);
                if (!fam.IsStyleAvailable(style)) style = fam.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : style;
                try { path.AddString(Text.Length == 0 ? " " : Text, fam, (int)style, Size, new PointF(Location.X + Pad, Location.Y + Pad), fmt); }
                catch { }
            }
            return path;
        }

        /// <summary>The size of the text block for a font, without drawing it.</summary>
        public static SizeF Measure(string text, string fontName, float size, bool bold, bool italic)
        {
            using (var fam = Family(fontName))
            {
                var style = (bold ? FontStyle.Bold : 0) | (italic ? FontStyle.Italic : 0);
                if (!fam.IsStyleAvailable(style)) style = FontStyle.Regular;
                float line = size * fam.GetLineSpacing(style) / Math.Max(1, fam.GetEmHeight(style));
                var lines = (text ?? "").Replace("\r", "").Split('\n');
                float w = 0;
                using (var bmp = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(bmp))
                using (var f = new Font(fam, size, style, GraphicsUnit.Pixel))
                using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                    foreach (var l in lines) w = Math.Max(w, g.MeasureString(l.Length == 0 ? " " : l, f, PointF.Empty, fmt).Width);
                }
                return new SizeF(w, line * lines.Length);
            }
        }

        public override RectangleF Box
        {
            get
            {
                if (!measured)
                {
                    var sz = Measure(Text, FontName, Size, Bold, Italic);
                    box = new RectangleF(Location.X, Location.Y, sz.Width + 2 * Pad, sz.Height + 2 * Pad);
                    measured = true;
                }
                return box;
            }
        }

        public override RectangleF Bounds { get { return RectangleF.Inflate(Box, Size * 0.3f + 2, Size * 0.2f + 2); } }

        public override bool HitTest(PointF p, float radius) { return RectangleF.Inflate(Box, radius, radius).Contains(p); }

        public override PicObject Map(PicMap m)
        {
            if (!m.IsUpright) return null;
            float s = Math.Min(m.A, m.D);
            return new TextObject(Text, FontName, Size * s, Bold, Italic, m.Apply(Location), color, back);
        }

        public override PicObject WithBox(RectangleF b)
        {
            // dragging a handle scales the letters; the block keeps its shape
            var from = Box;
            float s = Math.Max(b.Width / Math.Max(1f, from.Width), b.Height / Math.Max(1f, from.Height));
            if (b.Width < from.Width * 0.999f || b.Height < from.Height * 0.999f)
                s = Math.Min(b.Width / Math.Max(1f, from.Width), b.Height / Math.Max(1f, from.Height));
            float size = Math.Max(4f, Size * s);
            return new TextObject(Text, FontName, size, Bold, Italic, b.Location, color, back);
        }

        public override void Draw(Graphics g)
        {
            if (back.A > 0)
            {
                var b = Box;
                using (var br = new SolidBrush(back))
                using (var path = ShapeObject.RoundedPath(b, Math.Min(b.Height / 2f, Size * 0.22f)))
                    g.FillPath(br, path);
            }
            using (var path = BuildPath())
            using (var br = new SolidBrush(color))
                g.FillPath(br, path);
        }
    }

    /// <summary>A numbered circle for marking steps on a screenshot (1, 2, 3, ...).</summary>
    internal sealed class StepObject : PicObject
    {
        public readonly PointF Center;
        public readonly float Radius;
        public readonly int Number;
        readonly Color color;

        public StepObject(PointF center, float radius, int number, Color color)
        {
            Center = center; Radius = Math.Max(4f, radius); Number = number; this.color = color;
        }

        public override Color Color { get { return color; } }
        public override float Width { get { return Radius; } }
        public override PicObject WithColor(Color c) { return new StepObject(Center, Radius, Number, c); }
        public override PicObject WithWidth(float w) { return new StepObject(Center, w, Number, color); }
        public override RectangleF Box { get { return new RectangleF(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2); } }
        public override RectangleF Bounds { get { return RectangleF.Inflate(Box, Radius * 0.2f + 2, Radius * 0.2f + 2); } }
        public override bool HitTest(PointF p, float radius) { float r = Radius + radius; return Dist2(p, Center) <= r * r; }

        public override PicObject Map(PicMap m)
        {
            if (!m.KeepsBoxes) return null;
            return new StepObject(m.Apply(Center), Radius * m.Scale, Number, color);   // the number always stays upright
        }

        public override PicObject WithBox(RectangleF b)
        {
            float r = Math.Max(4f, Math.Min(b.Width, b.Height) / 2f);
            return new StepObject(new PointF(b.X + b.Width / 2f, b.Y + b.Height / 2f), r, Number, color);
        }

        public override void Draw(Graphics g)
        {
            var b = Box;
            bool light = (color.R * 299 + color.G * 587 + color.B * 114) / 1000 > 150;
            using (var br = new SolidBrush(color)) g.FillEllipse(br, b);
            using (var pen = new Pen(light ? Color.FromArgb(200, 0, 0, 0) : Color.FromArgb(235, 255, 255, 255), Math.Max(1f, Radius * 0.13f)))
                g.DrawEllipse(pen, RectangleF.Inflate(b, -pen.Width / 2f, -pen.Width / 2f));
            string s = Number.ToString();
            float size = Radius * (s.Length > 2 ? 0.8f : s.Length > 1 ? 1.0f : 1.25f);
            using (var path = new GraphicsPath())
            using (var fam = new FontFamily(GenericFontFamilies.SansSerif))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                try { path.AddString(s, fam, (int)FontStyle.Bold, size, PointF.Empty, fmt); } catch { return; }
                var pb = path.GetBounds();
                if (pb.Width <= 0) return;
                using (var mx = new Matrix())
                {
                    mx.Translate(Center.X - (pb.X + pb.Width / 2f), Center.Y - (pb.Y + pb.Height / 2f));
                    path.Transform(mx);
                }
                using (var br = new SolidBrush(light ? Color.Black : Color.White)) g.FillPath(br, path);
            }
        }
    }
}
