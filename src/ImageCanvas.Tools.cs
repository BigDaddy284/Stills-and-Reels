using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>The editing tools of the picture canvas: what the mouse and keys do, and what is drawn over the picture.</summary>
    internal sealed partial class ImageCanvas
    {
        enum DragMode
        {
            None, Pan, Draw, Erase, ErasePixels, CropNew, CropMove, CropHandle, Shape, ObjMove, ObjHandle,
            LayerMove, LayerHandle, Marquee, Redact, Pick
        }

        public EditTool Tool = EditTool.Pen;
        public Color ToolColor = Color.Black;
        public float ToolSize = 4f;                 // on-screen pixels
        public readonly ToolOptions Options = new ToolOptions();

        public event EventHandler CropApplyRequested;
        public event EventHandler CropCancelRequested;
        /// <summary>The colour picker (or Alt+click) took a colour from the picture.</summary>
        public event Action<Color> ColorPicked;
        /// <summary>A different shape or text box is selected (or none).</summary>
        public event EventHandler ObjectSelectionChanged;
        /// <summary>A short tip for the status bar.</summary>
        public event Action<string> Hint;

        DragMode drag;
        Point dragStart;
        PointF dragStartOffset;
        bool spaceDown;
        Point mouse;
        bool mouseInside;
        int dragSerial;                              // gives every drag its own undo-merge key
        string dragKey;

        // ink being drawn
        List<PointF> livePoints;
        float liveWidth;

        // erasing
        HashSet<PicObject> erasing;
        PixelEdit pixelEdit;
        PointF lastErase;

        // a shape being dragged out, or a marquee / redact rectangle
        PointF shapeA, shapeB;
        List<PointF> lassoPoints;
        PicSelMode marqueeMode;

        // the selected shape or text box
        PicObject selected;
        PicObject objStart;
        PointF objDragOrigin;
        int objHandle;
        bool objMoved;

        // a layer being moved or resized (a fresh paste, or a layer picked with the Select tool)
        int floatLayerId;
        bool floatFromPaste;
        int pasteUndoCount;
        PicSurface floatOriginal;
        Rectangle floatStartRect;
        PointF floatDragOrigin;
        int floatHandle;
        RectangleF floatLiveRect;

        // crop rectangle in picture pixels (left, top, right, bottom)
        float cl, ct, cr, cb;
        int cropHandle;
        PointF cropDragStartImg;
        RectangleF cropDragStartRect;

        // text being typed
        TextBox textBox;
        TextObject editingText;
        PointF textAt;
        float textSize;

        void ResetToolState()
        {
            drag = DragMode.None;
            livePoints = null;
            erasing = null;
            pixelEdit = null;
            lassoPoints = null;
            selected = null;
            objStart = null;
            floatLayerId = 0;
            floatOriginal = null;
        }

        void Say(string text) { var h = Hint; if (h != null) h(text); }

        float HandleTolerance { get { return 9f * dpiScale; } }

        // ================================================================ painting over the picture

        void PaintTools(Graphics g, RectangleF dest)
        {
            // things drawn in picture coordinates: ink and shapes in progress
            if (livePoints != null || drag == DragMode.Shape)
            {
                var state = g.Save();
                g.SetClip(dest, CombineMode.Intersect);
                g.TranslateTransform(offset.X, offset.Y);
                g.ScaleTransform(zoom, zoom);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (livePoints != null) Stroke.Draw(g, Tool, ToolColor, liveWidth, livePoints);
                if (drag == DragMode.Shape) ShapeObject.Draw(g, ShapeKindOf(Tool), shapeA, shapeB, ToolColor, liveWidth, Options.Fill);
                g.Restore(state);
            }

            if (drag == DragMode.LayerHandle && floatOriginal != null)
            {
                var r = ToScreen(floatLiveRect);
                var st = g.Save();
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(floatOriginal.Bitmap, r);
                g.Restore(st);
            }

            DrawSelectionOutline(g);
            if (drag == DragMode.Marquee || drag == DragMode.Redact) DrawMarquee(g);

            if (floatLayerId != 0) DrawLayerFrame(g);
            else if (Tool == EditTool.Crop) DrawCropOverlay(g);
            else
            {
                if (selected != null && textBox == null) DrawObjectHandles(g);
                if (ShowsBrush(Tool) && mouseInside && drag != DragMode.Pan && !spaceDown) DrawBrushPreview(g);
            }
        }

        static bool ShowsBrush(EditTool t)
        {
            return t == EditTool.Pen || t == EditTool.Pencil || t == EditTool.Highlighter || t == EditTool.Eraser;
        }

        static bool IsShapeTool(EditTool t)
        {
            return t == EditTool.Line || t == EditTool.Arrow || t == EditTool.Rect || t == EditTool.RoundRect || t == EditTool.Ellipse;
        }

        static bool IsMarqueeTool(EditTool t) { return t == EditTool.RectSelect || t == EditTool.EllipseSelect || t == EditTool.Lasso; }

        static ShapeKind ShapeKindOf(EditTool t)
        {
            switch (t)
            {
                case EditTool.Arrow: return ShapeKind.Arrow;
                case EditTool.Rect: return ShapeKind.Rect;
                case EditTool.RoundRect: return ShapeKind.RoundRect;
                case EditTool.Ellipse: return ShapeKind.Ellipse;
                default: return ShapeKind.Line;
            }
        }

        float BrushDiameter
        {
            get
            {
                float d = ToolSize;
                if (Tool == EditTool.Eraser) d = Math.Max(ToolSize, 6f * dpiScale);
                return Math.Max(d, 3f);
            }
        }

        Rectangle BrushRect(Point at)
        {
            float d = BrushDiameter + 6;
            return Rectangle.Ceiling(new RectangleF(at.X - d / 2f - 1, at.Y - d / 2f - 1, d + 2, d + 2));
        }

        void DrawBrushPreview(Graphics g)
        {
            float d = BrushDiameter;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(mouse.X - d / 2f, mouse.Y - d / 2f, d, d);
            using (var dark = new Pen(Color.FromArgb(160, 0, 0, 0), 3f))
            using (var light = new Pen(Color.FromArgb(230, 255, 255, 255), 1f))
            {
                if (Tool == EditTool.Highlighter) { g.DrawRectangle(dark, r.X, r.Y, r.Width, r.Height); g.DrawRectangle(light, r.X, r.Y, r.Width, r.Height); }
                else { g.DrawEllipse(dark, r); g.DrawEllipse(light, r); }
            }
        }

        static void DrawFrame(Graphics g, RectangleF r)
        {
            g.SmoothingMode = SmoothingMode.None;
            using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 1f))
            using (var pen2 = new Pen(Color.FromArgb(230, 20, 20, 20), 1f) { DashStyle = DashStyle.Dash })
            {
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                g.DrawRectangle(pen2, r.X, r.Y, r.Width, r.Height);
            }
        }

        void DrawHandle(Graphics g, PointF p)
        {
            float hs = 4f * dpiScale;
            var hr = new RectangleF(p.X - hs, p.Y - hs, hs * 2, hs * 2);
            g.FillRectangle(Brushes.White, hr);
            g.DrawRectangle(Pens.Black, hr.X, hr.Y, hr.Width, hr.Height);
        }

        static PointF[] HandlePoints(RectangleF r)
        {
            float mx = (r.Left + r.Right) / 2f, my = (r.Top + r.Bottom) / 2f;
            return new[]
            {
                new PointF(r.Left, r.Top), new PointF(mx, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, my),
                new PointF(r.Right, r.Bottom), new PointF(mx, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, my)
            };
        }

        int HitHandles(PointF[] pts, Point p, bool cornersOnly = false)
        {
            float tol = HandleTolerance;
            for (int i = 0; i < pts.Length; i++)
            {
                if (cornersOnly && (i & 1) == 1) continue;
                if (Math.Abs(p.X - pts[i].X) <= tol && Math.Abs(p.Y - pts[i].Y) <= tol) return i;
            }
            return -1;
        }

        static Cursor HandleCursor(int h)
        {
            if (h == 0 || h == 4) return Cursors.SizeNWSE;
            if (h == 2 || h == 6) return Cursors.SizeNESW;
            if (h == 1 || h == 5) return Cursors.SizeNS;
            if (h == 3 || h == 7) return Cursors.SizeWE;
            return null;
        }

        /// <summary>A box after one of its eight handles is dragged to a point. Corners can keep the box's proportions.</summary>
        static RectangleF ResizeBox(RectangleF start, int handle, PointF to, bool keepAspect, float minSize)
        {
            float l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
            bool left = handle == 0 || handle == 6 || handle == 7;
            bool right = handle == 2 || handle == 3 || handle == 4;
            bool top = handle == 0 || handle == 1 || handle == 2;
            bool bottom = handle == 4 || handle == 5 || handle == 6;
            if (left) l = Math.Min(to.X, r - minSize);
            if (right) r = Math.Max(to.X, l + minSize);
            if (top) t = Math.Min(to.Y, b - minSize);
            if (bottom) b = Math.Max(to.Y, t + minSize);
            bool corner = (handle & 1) == 0;
            if (corner && keepAspect && start.Height > 0.001f && start.Width > 0.001f)
            {
                float ar = start.Width / start.Height;
                float w = r - l, h = b - t;
                if (w / h > ar) w = h * ar; else h = w / ar;
                if (left) l = r - w; else r = l + w;
                if (top) t = b - h; else b = t + h;
            }
            return RectangleF.FromLTRB(l, t, r, b);
        }

        // ================================================================ selected shapes and text

        public PicObject SelectedObject { get { return selected; } }

        void Select(PicObject o)
        {
            if (ReferenceEquals(selected, o)) return;
            var old = selected;
            selected = o;
            if (old != null) InvalidateCanvas(old.Bounds, 12 * dpiScale);
            if (o != null) InvalidateCanvas(o.Bounds, 12 * dpiScale);
            var h = ObjectSelectionChanged; if (h != null) h(this, EventArgs.Empty);
        }

        public void ClearObjectSelection() { Select(null); }

        /// <summary>After an undo or any other change: forget a selected object that no longer exists.</summary>
        void ValidateSelection()
        {
            if (selected != null && (edit == null || edit.LayerOf(selected) < 0)) Select(null);
            if (floatLayerId != 0 && (edit == null || edit.State.IndexOf(floatLayerId) < 0)) EndLayerTransform();
        }

        /// <summary>Swaps the selected object for an edited copy (a colour or size picked in the sidebar).</summary>
        public void ChangeSelected(Func<PicObject, PicObject> change, string name)
        {
            if (selected == null || edit == null) return;
            var now = change(selected);
            if (now == null || ReferenceEquals(now, selected)) return;
            var old = selected;
            selected = now;
            if (!edit.ReplaceObject(old, now, name, "restyle" + name)) selected = old;
            InvalidateCanvas(RectangleF.Union(old.Bounds, now.Bounds), 12 * dpiScale);
        }

        public void DeleteSelected()
        {
            if (selected == null || edit == null) return;
            var o = selected;
            Select(null);
            edit.RemoveObjects(new HashSet<PicObject> { o }, "Delete");
        }

        public void DuplicateSelected()
        {
            if (selected == null || edit == null) return;
            float d = 16f / zoom;
            var copy = selected.Offset(d, d);
            int layer = edit.LayerOf(selected);
            if (layer >= 0) edit.SetActive(layer);
            edit.AddObject(copy, "Duplicate");
            Select(copy);
        }

        public void ReorderSelected(bool toFront) { if (selected != null && edit != null) edit.ReorderObject(selected, toFront); }

        static bool CornerOnly(PicObject o) { return o is TextObject || o is StepObject; }

        PointF[] ObjectHandles(PicObject o)
        {
            var sh = o as ShapeObject;
            if (sh != null && sh.IsLine) return new[] { ToScreen(sh.A), ToScreen(sh.B) };
            return HandlePoints(ToScreen(o.Box));
        }

        int HitObjectHandle(PicObject o, Point p)
        {
            var sh = o as ShapeObject;
            if (sh != null && sh.IsLine) return HitHandles(ObjectHandles(o), p);
            return HitHandles(ObjectHandles(o), p, CornerOnly(o));
        }

        void DrawObjectHandles(Graphics g)
        {
            var sh = selected as ShapeObject;
            if (sh == null || !sh.IsLine) DrawFrame(g, RectangleF.Inflate(ToScreen(selected.Box), 1, 1));
            var pts = ObjectHandles(selected);
            bool corners = CornerOnly(selected);
            for (int i = 0; i < pts.Length; i++)
            {
                if (corners && (i & 1) == 1) continue;
                DrawHandle(g, pts[i]);
            }
        }

        // ================================================================ a layer being moved or resized

        /// <summary>True while a freshly pasted picture is still waiting to be placed.</summary>
        public bool HasFloating { get { return floatLayerId != 0 && floatFromPaste; } }

        public bool IsTransformingLayer { get { return floatLayerId != 0; } }

        /// <summary>Pastes a picture as a new layer, centred in the visible part of the canvas, ready to move and resize. Takes ownership of the bitmap.</summary>
        public void BeginPaste(Bitmap bmp)
        {
            if (edit == null) { bmp.Dispose(); return; }
            CommitPaste();
            EndTextEdit(true);
            PicSurface px;
            try { px = PicSurface.FromImage(bmp); }
            finally { bmp.Dispose(); }
            var cs = edit.Size;
            var view = ToImage(new Point(ClientSize.Width / 2, ClientSize.Height / 2));
            float x = view.X - px.Width / 2f, y = view.Y - px.Height / 2f;
            x = Math.Max(0, Math.Min(cs.Width - px.Width, x));
            y = Math.Max(0, Math.Min(cs.Height - px.Height, y));
            int undoBefore = edit.UndoCount;
            var layer = edit.AddLayer(edit.NextLayerName("Pasted"), px, (int)Math.Round(x), (int)Math.Round(y), "Paste");
            Select(null);
            BeginLayerTransform(layer.Id, true);
            pasteUndoCount = undoBefore;
        }

        void BeginLayerTransform(int layerId, bool fromPaste)
        {
            EndLayerTransform();
            int i = edit.State.IndexOf(layerId);
            if (i < 0) return;
            edit.SetActive(i);
            floatLayerId = layerId;
            floatFromPaste = fromPaste;
            pasteUndoCount = -1;
            floatOriginal = edit.State.Layers[i].Pixels;
            edit.Pin(floatOriginal);
            Invalidate();
            UpdateCursor();
        }

        void EndLayerTransform()
        {
            if (floatLayerId == 0) return;
            if (drag == DragMode.LayerHandle || drag == DragMode.LayerMove) { drag = DragMode.None; Capture = false; }
            if (edit != null)
            {
                edit.Compositor.HideLayer(0);
                edit.Unpin(floatOriginal);
            }
            floatOriginal = null;
            floatLayerId = 0;
            floatFromPaste = false;
            Invalidate();
            UpdateCursor();
        }

        /// <summary>Places the pasted picture where it is (it stays its own layer).</summary>
        public void CommitPaste() { EndLayerTransform(); }

        /// <summary>Takes a fresh paste back out again.</summary>
        public void CancelPaste()
        {
            if (floatLayerId == 0) return;
            int upTo = pasteUndoCount;
            bool fresh = floatFromPaste && upTo >= 0;
            EndLayerTransform();
            if (fresh && edit != null) while (edit.UndoCount > upTo && edit.CanUndo) edit.Undo();
        }

        PicLayer FloatLayer
        {
            get
            {
                if (floatLayerId == 0 || edit == null) return null;
                int i = edit.State.IndexOf(floatLayerId);
                return i < 0 ? null : edit.State.Layers[i];
            }
        }

        Rectangle FloatRect
        {
            get
            {
                var l = FloatLayer;
                if (l == null) return Rectangle.Empty;
                return l.Pixels != null ? l.PixelBounds : l.Bounds;
            }
        }

        bool FloatCanResize { get { var l = FloatLayer; return l != null && l.Pixels != null && !l.HasObjects; } }

        void DrawLayerFrame(Graphics g)
        {
            var r = ToScreen(drag == DragMode.LayerHandle ? floatLiveRect : (RectangleF)FloatRect);
            DrawFrame(g, r);
            if (FloatCanResize) foreach (var p in HandlePoints(r)) DrawHandle(g, p);
        }

        /// <summary>The topmost layer with something under a picture point, leaving out a layer that simply covers the whole canvas.</summary>
        int LayerAt(PointF ip)
        {
            int x = (int)Math.Floor(ip.X), y = (int)Math.Floor(ip.Y);
            for (int i = edit.State.Layers.Length - 1; i >= 0; i--)
            {
                var l = edit.State.Layers[i];
                if (!l.Visible || l.Pixels == null) continue;
                if (i == 0 && l.PixelBounds.Contains(edit.State.Bounds)) continue;     // the background stays put
                if (!l.PixelBounds.Contains(x, y)) continue;
                if (l.Pixels.GetPixel(x - l.X, y - l.Y) >> 24 > 8) return i;
            }
            return -1;
        }

        // ================================================================ crop

        public void ResetCrop()
        {
            var cs = ContentSize;
            cl = 0; ct = 0; cr = cs.Width; cb = cs.Height;
            if (Options.CropAspect > 0 && edit != null) FitCropToAspect();
            Invalidate();
        }

        /// <summary>Sets the proportions the crop keeps (width / height; 0 for free) and refits the crop box to them.</summary>
        public void SetCropAspect(float aspect)
        {
            Options.CropAspect = Math.Max(0f, aspect);
            if (edit == null) return;
            if (aspect > 0) FitCropToAspect();
            Invalidate();
        }

        void FitCropToAspect()
        {
            float ar = Options.CropAspect;
            if (ar <= 0) return;
            var cs = ContentSize;
            float cx = (cl + cr) / 2f, cy = (ct + cb) / 2f;
            float w = Math.Abs(cr - cl), h = Math.Abs(cb - ct);
            if (w < 2 || h < 2) { w = cs.Width; h = cs.Height; cx = w / 2f; cy = h / 2f; }
            if (w / h > ar) w = h * ar; else h = w / ar;
            w = Math.Min(w, cs.Width); h = Math.Min(h, cs.Height);
            if (w / h > ar) w = h * ar; else h = w / ar;
            float l = Math.Max(0, Math.Min(cs.Width - w, cx - w / 2f)), t = Math.Max(0, Math.Min(cs.Height - h, cy - h / 2f));
            cl = l; ct = t; cr = l + w; cb = t + h;
        }

        void ClampCrop()
        {
            var cs = ContentSize;
            cl = Math.Max(0, Math.Min(cs.Width - 1, cl));
            ct = Math.Max(0, Math.Min(cs.Height - 1, ct));
            cr = Math.Max(cl + 1, Math.Min(cs.Width, cr));
            cb = Math.Max(ct + 1, Math.Min(cs.Height, cb));
        }

        public Rectangle CropRectangle
        {
            get
            {
                int l = (int)Math.Round(Math.Min(cl, cr)), t = (int)Math.Round(Math.Min(ct, cb));
                int r = (int)Math.Round(Math.Max(cl, cr)), b = (int)Math.Round(Math.Max(ct, cb));
                return Rectangle.FromLTRB(l, t, Math.Max(l + 1, r), Math.Max(t + 1, b));
            }
        }

        /// <summary>Sets the crop box directly (typed sizes, crop to selection).</summary>
        public void SetCropRectangle(Rectangle r)
        {
            r.Intersect(new Rectangle(Point.Empty, ContentSize));
            if (r.Width < 1 || r.Height < 1) return;
            cl = r.Left; ct = r.Top; cr = r.Right; cb = r.Bottom;
            Invalidate();
        }

        bool CropIsWholeImage
        {
            get { return CropRectangle == new Rectangle(Point.Empty, ContentSize); }
        }

        RectangleF CropScreenRect
        {
            get { return ToScreen(RectangleF.FromLTRB(Math.Min(cl, cr), Math.Min(ct, cb), Math.Max(cl, cr), Math.Max(ct, cb))); }
        }

        void DrawCropOverlay(Graphics g)
        {
            var r = CropScreenRect;
            var cs = ContentSize;
            var full = new RectangleF(offset.X, offset.Y, cs.Width * zoom, cs.Height * zoom);
            using (var region = new Region(full))
            using (var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                region.Exclude(r);
                g.FillRegion(shade, region);
            }
            g.SmoothingMode = SmoothingMode.None;
            using (var thirds = new Pen(Color.FromArgb(110, 255, 255, 255), 1f))
            {
                for (int i = 1; i <= 2; i++)
                {
                    float x = r.Left + r.Width * i / 3f, y = r.Top + r.Height * i / 3f;
                    g.DrawLine(thirds, x, r.Top, x, r.Bottom);
                    g.DrawLine(thirds, r.Left, y, r.Right, y);
                }
            }
            using (var border = new Pen(Color.White, 1.5f * dpiScale))
                g.DrawRectangle(border, r.X, r.Y, r.Width, r.Height);

            float hs = 7f * dpiScale;
            using (var fill = new SolidBrush(Color.White))
            using (var edge = new Pen(Color.FromArgb(60, 60, 60), 1f))
            {
                foreach (var p in HandlePoints(r))
                {
                    var hr = new RectangleF(p.X - hs / 2f, p.Y - hs / 2f, hs, hs);
                    g.FillRectangle(fill, hr);
                    g.DrawRectangle(edge, hr.X, hr.Y, hr.Width, hr.Height);
                }
            }

            var cropRect = CropRectangle;
            DrawSizeLabel(g, cropRect.Width + " × " + cropRect.Height, r);
        }

        void DrawSizeLabel(Graphics g, string label, RectangleF r)
        {
            using (var f = new Font("Segoe UI", 9f))
            {
                var sz = TextRenderer.MeasureText(label, f);
                var lr = new Rectangle((int)r.Left, (int)r.Top - sz.Height - 6, sz.Width + 8, sz.Height + 2);
                if (lr.Top < 2) lr.Y = (int)r.Top + 6;
                if (lr.Left < 2) lr.X = (int)r.Left + 6;
                using (var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20))) g.FillRectangle(bg, lr);
                TextRenderer.DrawText(g, label, f, lr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        void DragCrop(PointF ip)
        {
            var cs = ContentSize;
            float x = Math.Max(0, Math.Min(cs.Width, ip.X));
            float y = Math.Max(0, Math.Min(cs.Height, ip.Y));
            var r = cropDragStartRect;
            float ar = Options.CropAspect;
            switch (drag)
            {
                case DragMode.CropNew:
                    cr = x; cb = y;
                    if (ar > 0)
                    {
                        float w = Math.Abs(cr - cl), h = Math.Abs(cb - ct);
                        if (w / Math.Max(0.001f, h) > ar) w = h * ar; else h = w / ar;
                        // stay inside the picture
                        float maxW = cr >= cl ? cs.Width - cl : cl, maxH = cb >= ct ? cs.Height - ct : ct;
                        if (w > maxW) { w = maxW; h = w / ar; }
                        if (h > maxH) { h = maxH; w = h * ar; }
                        cr = cl + (cr >= cl ? w : -w);
                        cb = ct + (cb >= ct ? h : -h);
                    }
                    break;
                case DragMode.CropMove:
                    {
                        float dx = ip.X - cropDragStartImg.X, dy = ip.Y - cropDragStartImg.Y;
                        dx = Math.Max(-r.Left, Math.Min(cs.Width - r.Right, dx));
                        dy = Math.Max(-r.Top, Math.Min(cs.Height - r.Bottom, dy));
                        cl = r.Left + dx; cr = r.Right + dx; ct = r.Top + dy; cb = r.Bottom + dy;
                        break;
                    }
                case DragMode.CropHandle:
                    {
                        var nb = ResizeBox(r, cropHandle, new PointF(x, y), false, 1f);
                        if (ar > 0)
                        {
                            // keep the proportions: the dragged side sets one dimension, the other follows around the box's centre line
                            bool horizontalEdge = cropHandle == 1 || cropHandle == 5, verticalEdge = cropHandle == 3 || cropHandle == 7;
                            float w = nb.Width, h = nb.Height;
                            if (horizontalEdge) w = h * ar;
                            else if (verticalEdge) h = w / ar;
                            else if (w / Math.Max(0.001f, h) > ar) w = h * ar; else h = w / ar;
                            bool left = cropHandle == 0 || cropHandle == 6 || cropHandle == 7, top = cropHandle == 0 || cropHandle == 1 || cropHandle == 2;
                            float l = horizontalEdge ? (r.Left + r.Right) / 2f - w / 2f : left ? r.Right - w : r.Left;
                            float t = verticalEdge ? (r.Top + r.Bottom) / 2f - h / 2f : top ? r.Bottom - h : r.Top;
                            nb = new RectangleF(l, t, w, h);
                            // shrink back inside the picture if it stuck out
                            float over = Math.Max(Math.Max(-nb.Left, nb.Right - cs.Width) / Math.Max(1f, nb.Width), Math.Max(-nb.Top, nb.Bottom - cs.Height) / Math.Max(1f, nb.Height));
                            if (over > 0)
                            {
                                float s = Math.Max(0.05f, 1f - over * (horizontalEdge || verticalEdge ? 2f : 1f));
                                float nw = nb.Width * s, nh = nb.Height * s;
                                l = horizontalEdge ? (r.Left + r.Right) / 2f - nw / 2f : left ? r.Right - nw : r.Left;
                                t = verticalEdge ? (r.Top + r.Bottom) / 2f - nh / 2f : top ? r.Bottom - nh : r.Top;
                                nb = new RectangleF(l, t, nw, nh);
                            }
                        }
                        cl = nb.Left; cr = nb.Right; ct = nb.Top; cb = nb.Bottom;
                        break;
                    }
            }
        }

        void NormalizeCrop()
        {
            float l = Math.Min(cl, cr), r = Math.Max(cl, cr), t = Math.Min(ct, cb), b = Math.Max(ct, cb);
            if (r - l < 2 || b - t < 2)
            {
                // A click without a drag: go back to the whole picture.
                ResetCrop();
                return;
            }
            cl = l; cr = r; ct = t; cb = b;
            ClampCrop();
        }

        // ================================================================ pixel selections (marquee, lasso, wand)

        void DrawSelectionOutline(Graphics g)
        {
            var sel = edit != null ? edit.Selection : null;
            if (sel == null) return;
            var clip = g.VisibleClipBounds;
            g.SmoothingMode = SmoothingMode.None;
            using (var white = new Pen(Color.White, 1f))
            using (var black = new Pen(Color.Black, 1f) { DashPattern = new[] { 4f, 4f } })
            {
                foreach (var seg in sel.Edges)
                {
                    float x0 = offset.X + seg.X0 * zoom, y0 = offset.Y + seg.Y0 * zoom, x1 = offset.X + seg.X1 * zoom, y1 = offset.Y + seg.Y1 * zoom;
                    if (Math.Max(x0, x1) < clip.Left - 2 || Math.Min(x0, x1) > clip.Right + 2 || Math.Max(y0, y1) < clip.Top - 2 || Math.Min(y0, y1) > clip.Bottom + 2) continue;
                    g.DrawLine(white, x0, y0, x1, y1);
                    g.DrawLine(black, x0, y0, x1, y1);
                }
            }
        }

        void DrawMarquee(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var white = new Pen(Color.White, 1f))
            using (var black = new Pen(Color.Black, 1f) { DashPattern = new[] { 4f, 4f } })
            {
                if (Tool == EditTool.Lasso && lassoPoints != null && lassoPoints.Count > 1)
                {
                    var pts = new PointF[lassoPoints.Count];
                    for (int i = 0; i < pts.Length; i++) pts[i] = ToScreen(lassoPoints[i]);
                    g.DrawLines(white, pts); g.DrawLines(black, pts);
                    return;
                }
                var r = ToScreen(MarqueeRect());
                if (Tool == EditTool.EllipseSelect) { g.DrawEllipse(white, r); g.DrawEllipse(black, r); }
                else
                {
                    g.SmoothingMode = SmoothingMode.None;
                    g.DrawRectangle(white, r.X, r.Y, r.Width, r.Height); g.DrawRectangle(black, r.X, r.Y, r.Width, r.Height);
                }
                var mr = Rectangle.Round(MarqueeRect());
                if (mr.Width > 0 && mr.Height > 0) DrawSizeLabel(g, mr.Width + " × " + mr.Height, r);
            }
        }

        RectangleF MarqueeRect()
        {
            var cs = ContentSize;
            float ax = Math.Max(0, Math.Min(cs.Width, shapeA.X)), ay = Math.Max(0, Math.Min(cs.Height, shapeA.Y));
            float bx = Math.Max(0, Math.Min(cs.Width, shapeB.X)), by = Math.Max(0, Math.Min(cs.Height, shapeB.Y));
            return RectangleF.FromLTRB((float)Math.Round(Math.Min(ax, bx)), (float)Math.Round(Math.Min(ay, by)), (float)Math.Round(Math.Max(ax, bx)), (float)Math.Round(Math.Max(ay, by)));
        }

        static PicSelMode ModeFromKeys()
        {
            var k = ModifierKeys;
            bool shift = (k & Keys.Shift) != 0, alt = (k & Keys.Alt) != 0;
            if (shift && alt) return PicSelMode.Intersect;
            if (shift) return PicSelMode.Add;
            if (alt) return PicSelMode.Subtract;
            return PicSelMode.Replace;
        }

        void FinishMarquee()
        {
            if (edit == null) return;
            PicSelection made = null;
            if (Tool == EditTool.Lasso)
            {
                if (lassoPoints != null && lassoPoints.Count >= 3) made = PicSelection.FromPolygon(lassoPoints.ToArray(), edit.Size);
            }
            else
            {
                var r = MarqueeRect();
                if (r.Width >= 1 && r.Height >= 1)
                    made = Tool == EditTool.EllipseSelect ? PicSelection.FromEllipse(r, edit.Size) : PicSelection.FromRect(Rectangle.Round(r), edit.Size);
            }
            lassoPoints = null;
            if (made == null) { if (marqueeMode == PicSelMode.Replace) edit.Selection = null; return; }
            edit.Selection = PicSelection.Combine(edit.Selection, made, marqueeMode, edit.Size);
        }

        void WandAt(PointF ip)
        {
            int x = (int)Math.Floor(ip.X), y = (int)Math.Floor(ip.Y);
            if (!edit.State.Bounds.Contains(x, y)) return;
            PicSurface src; int ox = 0, oy = 0; bool own = false;
            SourceFor(out src, out ox, out oy, out own);
            if (src == null) return;
            try
            {
                var made = PicSelection.FromWand(src, x - ox, y - oy, Options.Tolerance, Options.Contiguous, ox, oy, edit.Size);
                edit.Selection = PicSelection.Combine(edit.Selection, made, ModeFromKeys(), edit.Size);
            }
            finally { if (own) src.Dispose(); }
        }

        /// <summary>The pixels the wand, fill and picker look at: the finished picture, or the active layer alone.</summary>
        void SourceFor(out PicSurface src, out int ox, out int oy, out bool own)
        {
            own = false; ox = oy = 0;
            if (Options.SampleAllLayers || edit.State.Layers.Length == 1 && !edit.ActiveLayer.HasObjects) { src = edit.Compositor.Picture; return; }
            var l = edit.ActiveLayer;
            src = l.Pixels; ox = l.X; oy = l.Y;
        }

        // ================================================================ fill, redact, erase pixels

        void FillAt(PointF ip)
        {
            int x = (int)Math.Floor(ip.X), y = (int)Math.Floor(ip.Y);
            if (!edit.State.Bounds.Contains(x, y)) return;
            var sel = edit.Selection;
            if (sel != null && sel.At(x, y) < 128) { Say("Click inside the selection to fill it"); return; }
            PicSurface src; int ox, oy; bool own;
            SourceFor(out src, out ox, out oy, out own);
            PicSelection area;
            if (src == null || !new Rectangle(ox, oy, src.Width, src.Height).Contains(x, y))
                area = sel ?? PicSelection.FromRect(edit.State.Bounds, edit.Size);     // an empty layer: fill it all
            else area = PicSelection.FromWand(src, x - ox, y - oy, Options.Tolerance, Options.Contiguous, ox, oy, edit.Size);
            if (sel != null) area = PicSelection.Combine(sel, area, PicSelMode.Intersect, edit.Size);
            if (area == null) return;
            var pe = edit.BeginPixels("Fill", true);
            var r = pe.Touch(area.Bounds);
            if (r.IsEmpty) { pe.Cancel(); return; }
            PicPaint.FillMask(pe.Surface, pe.X, pe.Y, area, PicSurface.Premultiply(ToolColor));
            pe.Commit();
        }

        void FinishRedact()
        {
            var r = Rectangle.Round(MarqueeRect());
            if (r.Width < 2 || r.Height < 2 || edit == null) return;
            var l = edit.ActiveLayer;
            if (l.Pixels == null || !l.PixelBounds.IntersectsWith(r))
            {
                Say("There is nothing on this layer to hide there – pick the layer with the picture first");
                return;
            }
            var pe = edit.BeginPixels(Options.RedactPixelate ? "Pixelate" : "Blur", false);
            var t = pe.Touch(r);
            if (t.IsEmpty) { pe.Cancel(); return; }
            var local = new Rectangle(t.X - pe.X, t.Y - pe.Y, t.Width, t.Height);
            float scale = Math.Max(1f, 1f / zoom);
            if (Options.RedactPixelate) PicFilters.Pixelate(pe.Surface, local, Math.Max(2, (int)Math.Round(Options.RedactStrength * scale)));
            else PicFilters.GaussianBlur(pe.Surface, local, Math.Max(1f, Options.RedactStrength * scale * 0.8f));
            pe.Commit();
        }

        void ErasePixelsTo(PointF ip)
        {
            if (pixelEdit == null) return;
            float radius = Math.Max(0.5f, BrushDiameter / 2f / zoom);
            var box = Rectangle.FromLTRB((int)Math.Floor(Math.Min(lastErase.X, ip.X) - radius - 1), (int)Math.Floor(Math.Min(lastErase.Y, ip.Y) - radius - 1),
                                         (int)Math.Ceiling(Math.Max(lastErase.X, ip.X) + radius + 1), (int)Math.Ceiling(Math.Max(lastErase.Y, ip.Y) + radius + 1));
            var t = pixelEdit.Touch(box);
            if (!t.IsEmpty)
            {
                PicPaint.EraseSegment(pixelEdit.Surface, new PointF(lastErase.X - pixelEdit.X, lastErase.Y - pixelEdit.Y), new PointF(ip.X - pixelEdit.X, ip.Y - pixelEdit.Y),
                    radius, new Rectangle(t.X - pixelEdit.X, t.Y - pixelEdit.Y, t.Width, t.Height), edit.Selection, pixelEdit.X, pixelEdit.Y);
                pixelEdit.Show(t);
            }
            lastErase = ip;
        }

        void EraseAt(Point screen)
        {
            if (edit == null || erasing == null) return;
            var ip = ToImage(screen);
            float radius = BrushDiameter / 2f / zoom;
            var dirty = RectangleF.Empty;
            foreach (var o in edit.AllObjects())
                if (!erasing.Contains(o) && o.HitTest(ip, radius))
                {
                    erasing.Add(o);
                    dirty = dirty.IsEmpty ? o.Bounds : RectangleF.Union(dirty, o.Bounds);
                }
            if (!dirty.IsEmpty) edit.Compositor.SetHidden(erasing, Rectangle.Ceiling(RectangleF.Inflate(dirty, 2, 2)));
            if (!dirty.IsEmpty) InvalidateCanvas(dirty, 4);
        }

        // ================================================================ text

        public bool IsEditingText { get { return textBox != null; } }

        void BeginTextEdit(PointF at, TextObject existing)
        {
            EndTextEdit(true);
            if (edit == null) return;
            editingText = existing;
            if (existing != null)
            {
                textAt = existing.Location;
                textSize = existing.Size;
                edit.Compositor.SetHidden(new HashSet<PicObject> { existing }, existing.PixelBounds);
                InvalidateCanvas(existing.Bounds, 12 * dpiScale);
            }
            else
            {
                textAt = at;
                textSize = Math.Max(6f, ToolSize / zoom);
            }
            textBox = new TextBox
            {
                Multiline = true, AcceptsReturn = true, AcceptsTab = false, WordWrap = false, ScrollBars = ScrollBars.None,
                BorderStyle = BorderStyle.FixedSingle, Text = existing != null ? existing.Text.Replace("\r", "").Replace("\n", "\r\n") : ""
            };
            textBox.TextChanged += (s, e) => PlaceTextBox();
            textBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = e.SuppressKeyPress = true; EndTextEdit(false); Focus(); }
                else if (e.KeyCode == Keys.Enter && e.Control) { e.Handled = e.SuppressKeyPress = true; EndTextEdit(true); Focus(); }
            };
            textBox.LostFocus += (s, e) =>
            {
                // clicking anywhere else places the text (deferred: the box can't be removed while it is losing focus)
                var tb = textBox;
                if (tb != null && IsHandleCreated) try { BeginInvoke((MethodInvoker)(() => { if (ReferenceEquals(textBox, tb) && !tb.Focused) EndTextEdit(true); })); } catch { }
            };
            Controls.Add(textBox);
            RestyleTextBox();
            textBox.Focus();
            textBox.SelectAll();
            Say("Type your text. Click elsewhere or press Ctrl+Enter to place it, Esc to cancel.");
        }

        /// <summary>Applies the current font, colour and size to the text being typed.</summary>
        public void RestyleTextBox()
        {
            if (textBox == null) return;
            string name = editingText != null ? editingText.FontName : Options.FontName;
            bool bold = editingText != null ? editingText.Bold : Options.Bold, italic = editingText != null ? editingText.Italic : Options.Italic;
            Color c = editingText != null ? editingText.Color : ToolColor;
            float px = Math.Max(7f, textSize * zoom);
            try { textBox.Font = new Font(name, px, (bold ? FontStyle.Bold : 0) | (italic ? FontStyle.Italic : 0), GraphicsUnit.Pixel); }
            catch { textBox.Font = new Font(FontFamily.GenericSansSerif, px, FontStyle.Regular, GraphicsUnit.Pixel); }
            bool light = (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 150;
            textBox.ForeColor = Color.FromArgb(255, c);
            textBox.BackColor = light ? Color.FromArgb(40, 40, 46) : Color.White;
            PlaceTextBox();
        }

        void PlaceTextBox()
        {
            if (textBox == null) return;
            var p = ToScreen(textAt);
            var sz = TextRenderer.MeasureText(textBox.Text.Length == 0 ? "Mg" : textBox.Text + "M", textBox.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
            int w = Math.Max(sz.Width + 12, (int)(60 * dpiScale)), h = Math.Max(sz.Height + 8, textBox.Font.Height + 8);
            textBox.Bounds = new Rectangle((int)Math.Round(p.X) - 3, (int)Math.Round(p.Y) - 3, w, h);
        }

        /// <summary>Finishes typing: the text becomes a text box on the picture (or is dropped).</summary>
        public void EndTextEdit(bool keep)
        {
            if (textBox == null) return;
            var tb = textBox;
            textBox = null;
            string text = tb.Text.Replace("\r", "").TrimEnd('\n', ' ');
            Controls.Remove(tb);
            tb.Dispose();
            var existing = editingText;
            editingText = null;
            if (edit == null) return;
            if (existing != null) edit.Compositor.SetHidden(null, existing.PixelBounds);
            if (!keep) { if (existing != null) InvalidateCanvas(existing.Bounds, 12 * dpiScale); return; }
            if (existing != null)
            {
                if (text.Length == 0) { Select(null); edit.RemoveObjects(new HashSet<PicObject> { existing }, "Delete text"); return; }
                if (text == existing.Text) { InvalidateCanvas(existing.Bounds, 12 * dpiScale); Select(existing); return; }
                var now = existing.WithText(text);
                selected = null;
                edit.ReplaceObject(existing, now, "Edit text");
                Select(now);
                return;
            }
            if (text.Length == 0) return;
            var t = new TextObject(text, Options.FontName, textSize, Options.Bold, Options.Italic, textAt, ToolColor, Options.Fill);
            edit.AddObject(t);
            Select(t);
        }

        int NextStepNumber()
        {
            int max = 0;
            foreach (var l in edit.State.Layers)
                foreach (var o in l.Objects) { var s = o as StepObject; if (s != null && s.Number > max) max = s.Number; }
            return max + 1;
        }

        // ================================================================ input

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Enter || keyData == Keys.Escape || keyData == Keys.Space) return true;
            var k = keyData & Keys.KeyCode;
            if (edit != null && (floatLayerId != 0 || selected != null || edit.Selection != null) &&
                (k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || k == Keys.Delete || k == Keys.Back)) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space && !spaceDown) { spaceDown = true; UpdateCursor(); Invalidate(BrushRect(mouse)); }
            if (edit == null) return;
            int step = e.Shift ? 10 : 1;
            int dx = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0;
            int dy = e.KeyCode == Keys.Up ? -step : e.KeyCode == Keys.Down ? step : 0;
            if (floatLayerId != 0)
            {
                if (e.KeyCode == Keys.Enter) { CommitPaste(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Escape) { if (floatFromPaste) CancelPaste(); else EndLayerTransform(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Delete) { DeleteFloatLayer(); e.Handled = true; return; }
                if (dx != 0 || dy != 0)
                {
                    int i = edit.State.IndexOf(floatLayerId);
                    if (i >= 0) edit.MoveLayerBy(i, dx, dy, "nudge" + floatLayerId);
                    e.Handled = true; return;
                }
            }
            if (Tool == EditTool.Crop)
            {
                if (e.KeyCode == Keys.Enter) { var h = CropApplyRequested; if (h != null) h(this, EventArgs.Empty); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape) { var h = CropCancelRequested; if (h != null) h(this, EventArgs.Empty); e.Handled = true; }
                return;
            }
            if (selected != null)
            {
                if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) { DeleteSelected(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Escape) { Select(null); e.Handled = true; return; }
                if (e.KeyCode == Keys.Enter && selected is TextObject) { BeginTextEdit(PointF.Empty, (TextObject)selected); e.Handled = true; return; }
                if (dx != 0 || dy != 0)
                {
                    ChangeSelected(o => o.Offset(dx, dy), "Move");
                    e.Handled = true; return;
                }
            }
            if (edit.Selection != null)
            {
                if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) { ClearSelectionPixels(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Escape) { edit.Selection = null; e.Handled = true; return; }
            }
        }

        void DeleteFloatLayer()
        {
            if (floatLayerId == 0 || edit == null) return;
            if (floatFromPaste) { CancelPaste(); return; }
            int i = edit.State.IndexOf(floatLayerId);
            EndLayerTransform();
            if (i >= 0 && edit.State.Layers.Length > 1) edit.DeleteLayer(i);
        }

        /// <summary>Delete with a selection: clears the selected pixels of the active layer.</summary>
        public void ClearSelectionPixels()
        {
            var sel = edit != null ? edit.Selection : null;
            if (sel == null) return;
            var l = edit.ActiveLayer;
            if (l.Pixels == null || !l.PixelBounds.IntersectsWith(sel.Bounds)) return;
            var pe = edit.BeginPixels("Delete", false);
            var t = pe.Touch(sel.Bounds);
            if (t.IsEmpty) { pe.Cancel(); return; }
            PicPaint.ClearMask(pe.Surface, pe.X, pe.Y, sel);
            pe.Commit();
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) { spaceDown = false; UpdateCursor(); Invalidate(BrushRect(mouse)); }
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            spaceDown = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            mouseInside = true;
            var f = FindForm();
            if (f != null && Form.ActiveForm == f && textBox == null) Focus();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouseInside = false;
            if (edit != null) Invalidate(BrushRect(mouse));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (textBox != null) { EndTextEdit(true); Focus(); return; }
            Focus();
            if (!HasContent || drag != DragMode.None) return;
            dragStart = e.Location;
            mouse = e.Location;

            bool panRequest = e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && (edit == null || spaceDown || Tool == EditTool.Pan));
            if (panRequest)
            {
                if (CanPan)
                {
                    drag = DragMode.Pan;
                    dragStartOffset = offset;
                    Capture = true;
                    Cursor = Cursors.SizeAll;
                }
                return;
            }
            if (e.Button != MouseButtons.Left || edit == null) return;

            PointF ip = ToImage(e.Location);
            dragKey = "drag" + (++dragSerial);

            if (floatLayerId != 0)
            {
                var fr = ToScreen((RectangleF)FloatRect);
                int fh = FloatCanResize ? HitHandles(HandlePoints(fr), e.Location) : -1;
                if (fh >= 0)
                {
                    drag = DragMode.LayerHandle; floatHandle = fh;
                    floatStartRect = FloatRect; floatLiveRect = floatStartRect; floatDragOrigin = ip;
                    edit.Compositor.HideLayer(floatLayerId);
                    Capture = true; Invalidate(); return;
                }
                if (fr.Contains(e.Location))
                {
                    drag = DragMode.LayerMove;
                    floatStartRect = FloatRect; floatDragOrigin = ip;
                    Capture = true; Cursor = Cursors.SizeAll; return;
                }
                bool wasPaste = floatFromPaste;
                EndLayerTransform();           // clicking outside places it
                if (wasPaste) return;
            }

            // Alt+click with a drawing tool picks up a colour
            if ((ModifierKeys & Keys.Alt) != 0 && (ShowsBrush(Tool) || IsShapeTool(Tool) || Tool == EditTool.Fill || Tool == EditTool.Text))
            {
                PickColor(e.Location);
                return;
            }

            // a selected shape can be adjusted without leaving the tool that made it
            if (selected != null && (Tool == EditTool.Select || IsShapeTool(Tool) || Tool == EditTool.Text || Tool == EditTool.Step))
            {
                int h = HitObjectHandle(selected, e.Location);
                if (h >= 0) { StartObjectDrag(DragMode.ObjHandle, h, ip); return; }
                if (Tool != EditTool.Select && selected.HitTest(ip, 3f / zoom)) { StartObjectDrag(DragMode.ObjMove, -1, ip); return; }
            }

            switch (Tool)
            {
                case EditTool.Pen:
                case EditTool.Pencil:
                case EditTool.Highlighter:
                    Select(null);
                    liveWidth = Math.Max(0.5f, ToolSize / zoom);
                    livePoints = new List<PointF> { ip };
                    drag = DragMode.Draw;
                    break;
                case EditTool.Eraser:
                    Select(null);
                    if (Options.ErasePixels)
                    {
                        var l = edit.ActiveLayer;
                        if (l.Pixels == null) { Say("This layer has no pixels to rub out. Switch the eraser to \"Strokes and shapes\" to remove drawings."); return; }
                        pixelEdit = edit.BeginPixels("Eraser", false);
                        lastErase = ip;
                        drag = DragMode.ErasePixels;
                        ErasePixelsTo(ip);
                    }
                    else
                    {
                        erasing = new HashSet<PicObject>();
                        drag = DragMode.Erase;
                        EraseAt(e.Location);
                    }
                    break;
                case EditTool.Crop:
                    cropDragStartImg = ip;
                    cropDragStartRect = RectangleF.FromLTRB(Math.Min(cl, cr), Math.Min(ct, cb), Math.Max(cl, cr), Math.Max(ct, cb));
                    cropHandle = HitHandles(HandlePoints(CropScreenRect), e.Location);
                    if (cropHandle >= 0) drag = DragMode.CropHandle;
                    else if (CropScreenRect.Contains(e.Location) && !CropIsWholeImage) drag = DragMode.CropMove;
                    else
                    {
                        drag = DragMode.CropNew;
                        var cs = ContentSize;
                        cl = cr = Math.Max(0, Math.Min(cs.Width, ip.X));
                        ct = cb = Math.Max(0, Math.Min(cs.Height, ip.Y));
                    }
                    Invalidate();
                    break;
                case EditTool.Select:
                    {
                        var hit = edit.ObjectAt(ip, 4f / zoom);
                        if (hit != null)
                        {
                            Select(hit);
                            StartObjectDrag(DragMode.ObjMove, -1, ip);
                            return;
                        }
                        Select(null);
                        int li = LayerAt(ip);
                        if (li >= 0)
                        {
                            BeginLayerTransform(edit.State.Layers[li].Id, false);
                            drag = DragMode.LayerMove;
                            floatStartRect = FloatRect; floatDragOrigin = ip;
                            Capture = true; Cursor = Cursors.SizeAll;
                        }
                        return;
                    }
                case EditTool.Line:
                case EditTool.Arrow:
                case EditTool.Rect:
                case EditTool.RoundRect:
                case EditTool.Ellipse:
                    Select(null);
                    liveWidth = Math.Max(0.5f, ToolSize / zoom);
                    shapeA = shapeB = ip;
                    drag = DragMode.Shape;
                    break;
                case EditTool.RectSelect:
                case EditTool.EllipseSelect:
                case EditTool.Lasso:
                    Select(null);
                    marqueeMode = ModeFromKeys();
                    shapeA = shapeB = ip;
                    lassoPoints = Tool == EditTool.Lasso ? new List<PointF> { ip } : null;
                    drag = DragMode.Marquee;
                    break;
                case EditTool.Wand:
                    Select(null);
                    WandAt(ip);
                    return;
                case EditTool.Fill:
                    Select(null);
                    FillAt(ip);
                    return;
                case EditTool.Picker:
                    drag = DragMode.Pick;
                    PickColor(e.Location);
                    break;
                case EditTool.Redact:
                    Select(null);
                    shapeA = shapeB = ip;
                    drag = DragMode.Redact;
                    break;
                case EditTool.Text:
                    {
                        var hit = edit.ObjectAt(ip, 3f / zoom) as TextObject;
                        if (hit != null) { Select(hit); BeginTextEdit(PointF.Empty, hit); }
                        else { Select(null); BeginTextEdit(ip, null); }
                        return;
                    }
                case EditTool.Step:
                    {
                        float radius = Math.Max(9f, ToolSize * 2.2f) / zoom;
                        var s = new StepObject(ip, radius, NextStepNumber(), ToolColor);
                        edit.AddObject(s);
                        Select(s);
                        return;
                    }
                default:
                    return;
            }
            Capture = true;
        }

        void StartObjectDrag(DragMode mode, int handle, PointF ip)
        {
            drag = mode;
            objHandle = handle;
            objStart = selected;
            objDragOrigin = ip;
            objMoved = false;
            Capture = true;
            if (mode == DragMode.ObjMove) Cursor = Cursors.SizeAll;
        }

        void PickColor(Point screen)
        {
            var c = ColorAt(screen);
            if (c.A == 0) return;
            var h = ColorPicked;
            if (h != null) h(Color.FromArgb(255, c));
        }

        static PointF Constrain(PointF a, PointF b, bool line)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            if (line)
            {
                // snap to the nearest 45 degrees
                double ang = Math.Atan2(dy, dx), len = Math.Sqrt(dx * dx + dy * dy);
                ang = Math.Round(ang / (Math.PI / 4)) * (Math.PI / 4);
                return new PointF(a.X + (float)(Math.Cos(ang) * len), a.Y + (float)(Math.Sin(ang) * len));
            }
            float s = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new PointF(a.X + (dx < 0 ? -s : s), a.Y + (dy < 0 ? -s : s));
        }

        RectangleF LiveShapeBounds()
        {
            var r = RectangleF.FromLTRB(Math.Min(shapeA.X, shapeB.X), Math.Min(shapeA.Y, shapeB.Y), Math.Max(shapeA.X, shapeB.X), Math.Max(shapeA.Y, shapeB.Y));
            float pad = liveWidth * 5f + 12f;
            return RectangleF.Inflate(r, pad, pad);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var oldMouse = mouse;
            mouse = e.Location;
            mouseInside = true;
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            switch (drag)
            {
                case DragMode.Pan:
                    offset = new PointF(dragStartOffset.X + e.X - dragStart.X, dragStartOffset.Y + e.Y - dragStart.Y);
                    ClampOffset();
                    PlaceTextBox();
                    Invalidate();
                    return;
                case DragMode.Draw:
                    {
                        var ip = ToImage(e.Location);
                        var last = livePoints[livePoints.Count - 1];
                        float dx = (ip.X - last.X) * zoom, dy = (ip.Y - last.Y) * zoom;
                        if (dx * dx + dy * dy >= 1f)
                        {
                            // the joint before this point changes shape too, so repaint from two points back
                            var from = livePoints[Math.Max(0, livePoints.Count - 2)];
                            livePoints.Add(ip);
                            var r = RectangleF.FromLTRB(Math.Min(Math.Min(from.X, last.X), ip.X), Math.Min(Math.Min(from.Y, last.Y), ip.Y),
                                                        Math.Max(Math.Max(from.X, last.X), ip.X), Math.Max(Math.Max(from.Y, last.Y), ip.Y));
                            InvalidateCanvas(RectangleF.Inflate(r, liveWidth, liveWidth), 4);
                        }
                        Invalidate(BrushRect(oldMouse)); Invalidate(BrushRect(mouse));
                        return;
                    }
                case DragMode.Erase:
                    EraseAt(e.Location);
                    Invalidate(BrushRect(oldMouse)); Invalidate(BrushRect(mouse));
                    return;
                case DragMode.ErasePixels:
                    ErasePixelsTo(ToImage(e.Location));
                    Invalidate(BrushRect(oldMouse)); Invalidate(BrushRect(mouse));
                    return;
                case DragMode.Shape:
                    {
                        var before = LiveShapeBounds();
                        shapeB = ToImage(e.Location);
                        if (shift) shapeB = Constrain(shapeA, shapeB, Tool == EditTool.Line || Tool == EditTool.Arrow);
                        InvalidateCanvas(RectangleF.Union(before, LiveShapeBounds()), 4);
                        return;
                    }
                case DragMode.Marquee:
                case DragMode.Redact:
                    {
                        var ip = ToImage(e.Location);
                        if (lassoPoints != null)
                        {
                            var last = lassoPoints[lassoPoints.Count - 1];
                            if (Math.Abs(ip.X - last.X) + Math.Abs(ip.Y - last.Y) >= 1.5f / zoom) lassoPoints.Add(ip);
                        }
                        shapeB = ip;
                        if (shift && lassoPoints == null && marqueeMode == PicSelMode.Replace && drag == DragMode.Marquee) shapeB = Constrain(shapeA, shapeB, false);
                        Invalidate();
                        return;
                    }
                case DragMode.Pick:
                    PickColor(e.Location);
                    return;
                case DragMode.ObjMove:
                    {
                        var ip = ToImage(e.Location);
                        float dx = ip.X - objDragOrigin.X, dy = ip.Y - objDragOrigin.Y;
                        if (!objMoved && Math.Abs(dx * zoom) + Math.Abs(dy * zoom) < 3) return;
                        objMoved = true;
                        MoveSelectedTo(objStart.Offset((float)Math.Round(dx), (float)Math.Round(dy)), "Move");
                        return;
                    }
                case DragMode.ObjHandle:
                    {
                        var ip = ToImage(e.Location);
                        objMoved = true;
                        PicObject now;
                        var sh = objStart as ShapeObject;
                        if (sh != null && sh.IsLine)
                        {
                            var other = objHandle == 0 ? sh.B : sh.A;
                            if (shift) ip = Constrain(other, ip, true);
                            now = objHandle == 0 ? sh.WithEnds(ip, sh.B) : sh.WithEnds(sh.A, ip);
                        }
                        else
                        {
                            bool keep = CornerOnly(objStart) || shift;
                            now = objStart.WithBox(ResizeBox(objStart.Box, objHandle, ip, keep, 2f / zoom));
                        }
                        MoveSelectedTo(now, "Resize");
                        return;
                    }
                case DragMode.LayerMove:
                    {
                        var ip = ToImage(e.Location);
                        int i = edit.State.IndexOf(floatLayerId);
                        if (i < 0) return;
                        var now = FloatRect;
                        int tx = floatStartRect.X + (int)Math.Round(ip.X - floatDragOrigin.X), ty = floatStartRect.Y + (int)Math.Round(ip.Y - floatDragOrigin.Y);
                        if (tx != now.X || ty != now.Y)
                        {
                            edit.MoveLayerBy(i, tx - now.X, ty - now.Y, dragKey);
                            InvalidateCanvas(Rectangle.Union(now, FloatRect), 12 * dpiScale);
                        }
                        return;
                    }
                case DragMode.LayerHandle:
                    {
                        var before = floatLiveRect;
                        floatLiveRect = ResizeBox(floatStartRect, floatHandle, ToImage(e.Location), (floatHandle & 1) == 0 && !shift, 2f);
                        InvalidateCanvas(RectangleF.Union(before, floatLiveRect), 12 * dpiScale);
                        return;
                    }
                case DragMode.CropNew:
                case DragMode.CropMove:
                case DragMode.CropHandle:
                    DragCrop(ToImage(e.Location));
                    Invalidate();
                    return;
            }
            UpdateCursor();
            if (edit != null && ShowsBrush(Tool) && floatLayerId == 0) { Invalidate(BrushRect(oldMouse)); Invalidate(BrushRect(mouse)); }
        }

        void MoveSelectedTo(PicObject now, string name)
        {
            if (now == null || selected == null) return;
            var old = selected;
            selected = now;
            if (!edit.ReplaceObject(old, now, name, dragKey)) { selected = old; return; }
            InvalidateCanvas(RectangleF.Union(old.Bounds, now.Bounds), 12 * dpiScale);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var mode = drag;
            drag = DragMode.None;
            Capture = false;
            if (edit == null && mode != DragMode.Pan) { UpdateCursor(); return; }
            switch (mode)
            {
                case DragMode.Draw:
                    if (livePoints != null && livePoints.Count > 0)
                    {
                        var s = new Stroke(Tool, ToolColor, liveWidth, livePoints.ToArray());
                        livePoints = null;
                        edit.AddStroke(s);
                    }
                    livePoints = null;
                    break;
                case DragMode.Erase:
                    {
                        var gone = erasing;
                        erasing = null;
                        if (gone != null && gone.Count > 0)
                        {
                            edit.Compositor.SetHidden(null, Rectangle.Empty);
                            edit.RemoveObjects(gone);
                        }
                        break;
                    }
                case DragMode.ErasePixels:
                    if (pixelEdit != null) { var pe = pixelEdit; pixelEdit = null; pe.Commit(); }
                    break;
                case DragMode.Shape:
                    {
                        float dx = (shapeB.X - shapeA.X) * zoom, dy = (shapeB.Y - shapeA.Y) * zoom;
                        InvalidateCanvas(LiveShapeBounds(), 4);
                        if (dx * dx + dy * dy >= 9f)
                        {
                            var s = new ShapeObject(ShapeKindOf(Tool), shapeA, shapeB, ToolColor, liveWidth, Options.Fill);
                            edit.AddObject(s);
                            Select(s);
                        }
                        break;
                    }
                case DragMode.Marquee:
                    FinishMarquee();
                    Invalidate();
                    break;
                case DragMode.Redact:
                    FinishRedact();
                    Invalidate();
                    break;
                case DragMode.LayerHandle:
                    FinishLayerResize();
                    break;
                case DragMode.CropNew:
                case DragMode.CropMove:
                case DragMode.CropHandle:
                    NormalizeCrop();
                    Invalidate();
                    break;
            }
            objStart = null;
            UpdateCursor();
        }

        void FinishLayerResize()
        {
            int i = edit.State.IndexOf(floatLayerId);
            var target = Rectangle.Round(floatLiveRect);
            edit.Compositor.HideLayer(0);
            if (i < 0 || floatOriginal == null || target.Width < 1 || target.Height < 1) { Invalidate(); return; }
            var cur = FloatRect;
            if (target == cur) { Invalidate(); return; }
            Cursor = Cursors.WaitCursor;
            PicSurface px = target.Size == floatOriginal.Size ? floatOriginal.Clone() : floatOriginal.Resize(target.Width, target.Height);
            edit.SetLayerPixels(i, px, target.X, target.Y, "Resize layer");
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!HasContent || e.Button != MouseButtons.Left) return;
            if (edit == null)
            {
                if (fit && zoom < 0.999f) ZoomAt(1f, e.Location);
                else FitToWindow();
            }
            else if (Tool == EditTool.Crop && CropScreenRect.Contains(e.Location))
            {
                var h = CropApplyRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
            else if (Tool == EditTool.Select || Tool == EditTool.Text)
            {
                var t = edit.ObjectAt(ToImage(e.Location), 3f / zoom) as TextObject;
                if (t != null) { drag = DragMode.None; Capture = false; Select(t); BeginTextEdit(PointF.Empty, t); }
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!HasContent || drag == DragMode.Draw || drag == DragMode.ErasePixels) return;
            float notches = e.Delta / 120f;
            ZoomAt(zoom * (float)Math.Pow(1.2, notches), e.Location);
        }

        void UpdateCursor()
        {
            if (drag == DragMode.Pan) { Cursor = Cursors.SizeAll; return; }
            if (drag != DragMode.None) return;
            if (!HasContent) { Cursor = Cursors.Default; return; }
            if (edit != null && floatLayerId != 0 && !spaceDown)
            {
                var fr = ToScreen((RectangleF)FloatRect);
                var hc = FloatCanResize ? HandleCursor(HitHandles(HandlePoints(fr), mouse)) : null;
                Cursor = hc ?? (fr.Contains(mouse) ? Cursors.SizeAll : Cursors.Default);
                return;
            }
            if (edit == null || spaceDown || Tool == EditTool.Pan) { Cursor = CanPan ? Cursors.Hand : Cursors.Default; return; }
            if (Tool == EditTool.Crop)
            {
                var hc = HandleCursor(HitHandles(HandlePoints(CropScreenRect), mouse));
                if (hc != null) Cursor = hc;
                else if (CropScreenRect.Contains(mouse) && !CropIsWholeImage) Cursor = Cursors.SizeAll;
                else Cursor = Cursors.Cross;
                return;
            }
            if (selected != null && (Tool == EditTool.Select || IsShapeTool(Tool) || Tool == EditTool.Text || Tool == EditTool.Step))
            {
                int h = HitObjectHandle(selected, mouse);
                if (h >= 0)
                {
                    var sh = selected as ShapeObject;
                    Cursor = sh != null && sh.IsLine ? Cursors.Cross : HandleCursor(h) ?? Cursors.Cross;
                    return;
                }
                if (selected.HitTest(ToImage(mouse), 3f / zoom)) { Cursor = Cursors.SizeAll; return; }
            }
            if (Tool == EditTool.Select)
            {
                var ip = ToImage(mouse);
                Cursor = edit.ObjectAt(ip, 4f / zoom) != null || LayerAt(ip) >= 0 ? Cursors.SizeAll : Cursors.Default;
                return;
            }
            if (Tool == EditTool.Text) { Cursor = Cursors.IBeam; return; }
            Cursor = Cursors.Cross;
        }

        public void RefreshTool()
        {
            if (Tool != EditTool.Text) EndTextEdit(true); else RestyleTextBox();
            if (floatLayerId != 0 && !floatFromPaste && Tool != EditTool.Select) EndLayerTransform();
            if (selected != null && !(Tool == EditTool.Select || IsShapeTool(Tool) || Tool == EditTool.Text || Tool == EditTool.Step)) Select(null);
            UpdateCursor();
            Invalidate();
        }
    }
}
