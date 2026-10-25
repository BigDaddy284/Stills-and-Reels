using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>
    /// Shows the current picture with zoom and pan, and handles the drawing tools in edit mode.
    /// </summary>
    internal sealed class ImageCanvas : Control
    {
        static readonly float[] ZoomSteps =
        {
            0.02f, 0.03f, 0.05f, 0.0625f, 0.0833f, 0.1f, 0.125f, 0.1667f, 0.25f, 0.3333f, 0.5f, 0.6667f, 0.75f,
            1f, 1.25f, 1.5f, 2f, 3f, 4f, 5f, 6f, 8f, 10f, 12f, 16f, 24f, 32f
        };
        const float MinZoom = 0.02f, MaxZoom = 32f;

        enum DragMode { None, Pan, Draw, Erase, CropNew, CropMove, CropHandle, FloatMove, FloatSize }

        // Pasted picture that floats above the image until it is placed.
        Bitmap floatImg;
        RectangleF floatRect;          // image coordinates
        RectangleF floatDragStart;
        PointF floatDragOrigin;
        int floatHandle;
        public bool HasFloating { get { return floatImg != null; } }

        /// <summary>Starts a floating paste, centred in the visible part of the picture. Takes ownership of the bitmap.</summary>
        public void BeginPaste(Bitmap bmp)
        {
            CommitPaste();
            if (edit == null) { bmp.Dispose(); return; }
            floatImg = bmp;
            var cs = edit.Size;
            var view = ToImage(new Point(ClientSize.Width / 2, ClientSize.Height / 2));
            float x = view.X - bmp.Width / 2f, y = view.Y - bmp.Height / 2f;
            x = Math.Max(0, Math.Min(cs.Width - bmp.Width, x));
            y = Math.Max(0, Math.Min(cs.Height - bmp.Height, y));
            floatRect = new RectangleF((float)Math.Round(x), (float)Math.Round(y), bmp.Width, bmp.Height);
            Invalidate();
        }

        /// <summary>Places the floating picture into the image (one undo step).</summary>
        public void CommitPaste()
        {
            if (floatImg == null) return;
            var img = floatImg; var r = floatRect;
            floatImg = null;
            drag = DragMode.None; Capture = false;
            if (edit != null) edit.PasteImage(img, r);
            img.Dispose();
            Invalidate();
        }

        public void CancelPaste()
        {
            if (floatImg == null) return;
            floatImg.Dispose(); floatImg = null;
            drag = DragMode.None; Capture = false;
            Invalidate();
        }

        RectangleF FloatScreenRect
        {
            get
            {
                var a = ToScreen(floatRect.Left, floatRect.Top);
                var b = ToScreen(floatRect.Right, floatRect.Bottom);
                return RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y);
            }
        }

        int HitFloatHandle(Point p)
        {
            var r = FloatScreenRect;
            var pts = HandlePoints(r);
            float tol = 9f * dpiScale;
            for (int i = 0; i < pts.Length; i++)
                if (Math.Abs(p.X - pts[i].X) <= tol && Math.Abs(p.Y - pts[i].Y) <= tol) return i;
            return -1;
        }

        void DrawFloating(Graphics g)
        {
            var r = FloatScreenRect;
            var st = g.Save();
            g.InterpolationMode = zoom >= 1.999f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(floatImg, r);
            g.Restore(st);
            using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 1f))
            using (var pen2 = new Pen(Color.FromArgb(230, 20, 20, 20), 1f) { DashStyle = DashStyle.Dash })
            {
                g.SmoothingMode = SmoothingMode.None;
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                g.DrawRectangle(pen2, r.X, r.Y, r.Width, r.Height);
            }
            float hs = 4f * dpiScale;
            foreach (var p in HandlePoints(r))
            {
                var hr = new RectangleF(p.X - hs, p.Y - hs, hs * 2, hs * 2);
                g.FillRectangle(Brushes.White, hr);
                g.DrawRectangle(Pens.Black, hr.X, hr.Y, hr.Width, hr.Height);
            }
        }

        Image viewImage;
        bool animated;
        EditSession edit;
        string message, subMessage;

        float zoom = 1f;
        bool fit = true;
        PointF offset;
        float dpiScale = 1f;

        // High-quality downscaled copy used when zoomed out.
        Bitmap scaled;
        Image scaledSource;
        float scaledZoom;
        readonly Timer hqTimer = new Timer { Interval = 160 };
        bool zoomingFast;

        TextureBrush checker;

        DragMode drag;
        Point dragStart;
        PointF dragStartOffset;
        bool spaceDown;
        List<PointF> livePoints;
        float liveWidth;
        HashSet<Stroke> erasing;
        Point mouse;
        bool mouseInside;

        // Crop rectangle in image pixels (left, top, right, bottom).
        float cl, ct, cr, cb;
        int cropHandle;
        PointF cropDragStartImg;
        RectangleF cropDragStartRect;

        public EditTool Tool = EditTool.Pen;

        // Empty-state text shown when there is nothing to display (and no message).
        public Ico EmptyIcon = Ico.Picture;
        public string EmptyTitle = "Click a picture anywhere";
        public string EmptyDetail = "Click a picture in File Explorer, on the desktop, on a web page or in another app, or copy one,\nand it appears here. Arrow keys work in File Explorer. Use File > Open to pick one directly.";

        bool pixelGrid;
        public bool ShowPixelGrid { get { return pixelGrid; } set { pixelGrid = value; Invalidate(); } }

        public void SetEmptyState(Ico icon, string title, string detail)
        {
            EmptyIcon = icon; EmptyTitle = title; EmptyDetail = detail;
            if (Content == null) Invalidate();
        }
        public Color ToolColor = Color.Black;
        public float ToolSize = 4f; // on-screen pixels

        public event EventHandler ZoomChanged;
        public event EventHandler CropApplyRequested;
        public event EventHandler CropCancelRequested;

        public ImageCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Current.Canvas;
            ForeColor = Theme.Current.Text;
            Theme.Changed += OnThemeChanged;
            hqTimer.Tick += (s, e) => { hqTimer.Stop(); zoomingFast = false; Invalidate(); };
        }

        public float Zoom { get { return zoom; } }
        public bool IsFit { get { return fit; } }
        public bool HasContent { get { return Content != null; } }

        Image Content { get { return edit != null ? (Image)edit.Current.Base : viewImage; } }

        Size ContentSize
        {
            get
            {
                var c = Content;
                return c == null ? Size.Empty : c.Size;
            }
        }

        void OnThemeChanged(object s, EventArgs e)
        {
            BackColor = Theme.Current.Canvas;
            ForeColor = Theme.Current.Text;
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (var g = CreateGraphics()) dpiScale = Math.Max(1f, g.DpiX / 96f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnThemeChanged;
                StopAnimation();
                hqTimer.Dispose();
                ResetScaled();
                if (checker != null) checker.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- content

        public void ShowImage(Image img, bool isAnimated)
        {
            StopAnimation();
            viewImage = img;
            animated = isAnimated;
            message = subMessage = null;
            ResetScaled();
            if (animated && edit == null) ImageAnimator.Animate(viewImage, OnFrameChanged);
            fit = true;
            Relayout();
            Invalidate();
        }

        public void ShowMessage(string title, string detail)
        {
            StopAnimation();
            viewImage = null;
            message = title;
            subMessage = detail;
            ResetScaled();
            Invalidate();
            RaiseZoom();
        }

        public EditSession Edit
        {
            get { return edit; }
            set
            {
                if (edit != null) edit.Changed -= OnEditChanged;
                if (floatImg != null) { floatImg.Dispose(); floatImg = null; }
                edit = value;
                drag = DragMode.None;
                livePoints = null;
                erasing = null;
                ResetScaled();
                if (edit != null)
                {
                    StopAnimation();
                    edit.Changed += OnEditChanged;
                    ResetCrop();
                }
                else if (animated && viewImage != null)
                {
                    ImageAnimator.Animate(viewImage, OnFrameChanged);
                }
                Relayout();
                UpdateCursor();
                Invalidate();
            }
        }

        Size lastEditSize;

        void OnEditChanged(object sender, EventArgs e)
        {
            if (edit == null) return;
            if (edit.Size != lastEditSize)
            {
                lastEditSize = edit.Size;
                fit = true;
                ResetCrop();
                Relayout();
            }
            else
            {
                ClampCrop();
            }
            Invalidate();
        }

        void StopAnimation()
        {
            if (animated && viewImage != null)
            {
                try { ImageAnimator.StopAnimate(viewImage, OnFrameChanged); } catch { }
            }
        }

        void OnFrameChanged(object sender, EventArgs e)
        {
            if (IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke((MethodInvoker)Invalidate); } catch { }
            }
        }

        // ---------------------------------------------------------------- zoom & layout

        public void ZoomIn() { ZoomAt(NextStep(zoom, +1), Center); }
        public void ZoomOut() { ZoomAt(NextStep(zoom, -1), Center); }
        public void ActualSize() { ZoomAt(1f, Center); }

        public void FitToWindow()
        {
            fit = true;
            Relayout();
            Invalidate();
        }

        Point Center { get { return new Point(ClientSize.Width / 2, ClientSize.Height / 2); } }

        static float NextStep(float z, int dir)
        {
            if (dir > 0)
            {
                foreach (var s in ZoomSteps) if (s > z * 1.001f) return s;
                return MaxZoom;
            }
            for (int i = ZoomSteps.Length - 1; i >= 0; i--) if (ZoomSteps[i] < z * 0.999f) return ZoomSteps[i];
            return MinZoom;
        }

        public void ZoomAt(float newZoom, Point anchor)
        {
            if (Content == null) return;
            newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, newZoom));
            float ix = (anchor.X - offset.X) / zoom, iy = (anchor.Y - offset.Y) / zoom;
            zoom = newZoom;
            fit = false;
            offset = new PointF(anchor.X - ix * zoom, anchor.Y - iy * zoom);
            ClampOffset();
            zoomingFast = true;
            hqTimer.Stop(); hqTimer.Start();
            Invalidate();
            RaiseZoom();
        }

        void Relayout()
        {
            var cs = ContentSize;
            if (cs.IsEmpty) { RaiseZoom(); return; }
            if (edit != null) lastEditSize = cs;
            if (fit && ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                float z = Math.Min((float)ClientSize.Width / cs.Width, (float)ClientSize.Height / cs.Height);
                zoom = Math.Max(MinZoom, Math.Min(1f, z));
            }
            ClampOffset();
            RaiseZoom();
        }

        void ClampOffset()
        {
            var cs = ContentSize;
            if (cs.IsEmpty) return;
            float w = cs.Width * zoom, h = cs.Height * zoom;
            float x = offset.X, y = offset.Y;
            if (w <= ClientSize.Width) x = (ClientSize.Width - w) / 2f;
            else x = Math.Min(0, Math.Max(ClientSize.Width - w, x));
            if (h <= ClientSize.Height) y = (ClientSize.Height - h) / 2f;
            else y = Math.Min(0, Math.Max(ClientSize.Height - h, y));
            offset = new PointF((float)Math.Round(x), (float)Math.Round(y));
        }

        bool CanPan
        {
            get
            {
                var cs = ContentSize;
                return !cs.IsEmpty && (cs.Width * zoom > ClientSize.Width + 0.5f || cs.Height * zoom > ClientSize.Height + 0.5f);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (fit) Relayout(); else ClampOffset();
            Invalidate();
        }

        void RaiseZoom()
        {
            var h = ZoomChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        PointF ToImage(Point p) { return new PointF((p.X - offset.X) / zoom, (p.Y - offset.Y) / zoom); }
        PointF ToScreen(float x, float y) { return new PointF(offset.X + x * zoom, offset.Y + y * zoom); }

        // ---------------------------------------------------------------- painting

        void ResetScaled()
        {
            if (scaled != null) { scaled.Dispose(); scaled = null; }
            scaledSource = null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Current.Canvas);
            var img = Content;
            if (img == null)
            {
                DrawMessage(g);
                return;
            }

            if (animated && edit == null)
            {
                try { ImageAnimator.UpdateFrames(viewImage); } catch { }
            }

            var cs = img.Size;
            var dest = new RectangleF(offset.X, offset.Y, cs.Width * zoom, cs.Height * zoom);

            if (Image.IsAlphaPixelFormat(img.PixelFormat) || edit != null)
            {
                var visible = RectangleF.Intersect(dest, ClientRectangle);
                if (visible.Width > 0 && visible.Height > 0) g.FillRectangle(Checker, visible);
            }

            DrawImageContent(g, img, dest);
            if (pixelGrid && zoom >= 5.99f) DrawPixelGrid(g, dest);

            if (edit != null)
            {
                var state = g.Save();
                g.SetClip(dest);
                g.TranslateTransform(offset.X, offset.Y);
                g.ScaleTransform(zoom, zoom);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                foreach (var s in edit.Current.Strokes)
                {
                    if (erasing != null && erasing.Contains(s)) continue;
                    s.Draw(g);
                }
                if (livePoints != null) Stroke.Draw(g, Tool, ToolColor, liveWidth, livePoints);
                g.Restore(state);

                if (floatImg != null) DrawFloating(g);
                else if (Tool == EditTool.Crop) DrawCropOverlay(g);
                else if (Tool != EditTool.Pan && mouseInside && drag != DragMode.Pan && !spaceDown) DrawBrushPreview(g);
            }
        }

        void DrawImageContent(Graphics g, Image img, RectangleF dest)
        {
            bool allowCache = !(animated && edit == null) && !zoomingFast;
            if (zoom < 0.999f && allowCache)
            {
                int sw = Math.Max(1, (int)Math.Round(img.Width * zoom));
                int sh = Math.Max(1, (int)Math.Round(img.Height * zoom));
                if ((long)sw * sh <= 16000000L)
                {
                    if (scaled == null || !ReferenceEquals(scaledSource, img) || Math.Abs(scaledZoom - zoom) > 0.0001f)
                    {
                        ResetScaled();
                        scaled = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
                        using (var sg = Graphics.FromImage(scaled))
                        using (var attr = new ImageAttributes())
                        {
                            attr.SetWrapMode(WrapMode.TileFlipXY);
                            sg.CompositingMode = CompositingMode.SourceCopy;
                            sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            sg.DrawImage(img, new Rectangle(0, 0, sw, sh), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attr);
                        }
                        scaledSource = img;
                        scaledZoom = zoom;
                    }
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(scaled, new Rectangle((int)dest.X, (int)dest.Y, scaled.Width, scaled.Height));
                    return;
                }
            }

            // Draw only the visible part of the source.
            var vis = RectangleF.Intersect(dest, ClientRectangle);
            if (vis.Width <= 0 || vis.Height <= 0) return;
            float sx0 = (vis.Left - offset.X) / zoom, sy0 = (vis.Top - offset.Y) / zoom;
            float sx1 = (vis.Right - offset.X) / zoom, sy1 = (vis.Bottom - offset.Y) / zoom;
            int ix0 = Math.Max(0, (int)Math.Floor(sx0) - 1), iy0 = Math.Max(0, (int)Math.Floor(sy0) - 1);
            int ix1 = Math.Min(img.Width, (int)Math.Ceiling(sx1) + 1), iy1 = Math.Min(img.Height, (int)Math.Ceiling(sy1) + 1);
            if (ix1 <= ix0 || iy1 <= iy0) return;
            var src = new Rectangle(ix0, iy0, ix1 - ix0, iy1 - iy0);
            var d = new RectangleF(offset.X + ix0 * zoom, offset.Y + iy0 * zoom, src.Width * zoom, src.Height * zoom);

            g.InterpolationMode = zoom >= 0.999f ? (zoom >= 1.999f || Math.Abs(zoom - 1f) < 0.001f
                    ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear)
                : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (var attr = new ImageAttributes())
            {
                attr.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(img, new[] { d.Location, new PointF(d.Right, d.Top), new PointF(d.Left, d.Bottom) },
                            src, GraphicsUnit.Pixel, attr);
            }
        }

        TextureBrush Checker
        {
            get
            {
                if (checker == null)
                {
                    int s = (int)(8 * dpiScale);
                    using (var b = new Bitmap(s * 2, s * 2))
                    {
                        using (var g = Graphics.FromImage(b))
                        {
                            g.Clear(Color.FromArgb(205, 205, 205));
                            using (var br = new SolidBrush(Color.FromArgb(240, 240, 240)))
                            {
                                g.FillRectangle(br, 0, 0, s, s);
                                g.FillRectangle(br, s, s, s, s);
                            }
                        }
                        checker = new TextureBrush(b);
                    }
                }
                return checker;
            }
        }

        void DrawMessage(Graphics g)
        {
            if (message != null) Theme.DrawEmptyState(g, ClientRectangle, EmptyIcon == Ico.Snip ? Ico.Snip : Ico.Picture, message, subMessage, true);
            else Theme.DrawEmptyState(g, ClientRectangle, EmptyIcon, EmptyTitle, EmptyDetail, false);
        }

        void DrawPixelGrid(Graphics g, RectangleF dest)
        {
            var vis = RectangleF.Intersect(dest, ClientRectangle);
            if (vis.Width <= 0 || vis.Height <= 0) return;
            int x0 = (int)Math.Floor((vis.Left - offset.X) / zoom), x1 = (int)Math.Ceiling((vis.Right - offset.X) / zoom);
            int y0 = (int)Math.Floor((vis.Top - offset.Y) / zoom), y1 = (int)Math.Ceiling((vis.Bottom - offset.Y) / zoom);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;
            using (var pen = new Pen(Color.FromArgb(70, 128, 128, 128), 1f))
            {
                for (int x = x0; x <= x1; x++)
                {
                    float sx = (float)Math.Round(offset.X + x * zoom);
                    g.DrawLine(pen, sx, vis.Top, sx, vis.Bottom);
                }
                for (int y = y0; y <= y1; y++)
                {
                    float sy = (float)Math.Round(offset.Y + y * zoom);
                    g.DrawLine(pen, vis.Left, sy, vis.Right, sy);
                }
            }
        }

        void DrawBrushPreview(Graphics g)
        {
            if (Tool == EditTool.Crop) return;
            float d = Tool == EditTool.Highlighter ? ToolSize * 1f : ToolSize;
            if (Tool == EditTool.Eraser) d = Math.Max(ToolSize, 6f * dpiScale);
            d = Math.Max(d, 3f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(mouse.X - d / 2f, mouse.Y - d / 2f, d, d);
            using (var dark = new Pen(Color.FromArgb(160, 0, 0, 0), 3f))
            using (var light = new Pen(Color.FromArgb(230, 255, 255, 255), 1f))
            {
                if (Tool == EditTool.Highlighter) { g.DrawRectangle(dark, r.X, r.Y, r.Width, r.Height); g.DrawRectangle(light, r.X, r.Y, r.Width, r.Height); }
                else { g.DrawEllipse(dark, r); g.DrawEllipse(light, r); }
            }
        }

        // ---------------------------------------------------------------- crop

        public void ResetCrop()
        {
            var cs = ContentSize;
            cl = 0; ct = 0; cr = cs.Width; cb = cs.Height;
            Invalidate();
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

        bool CropIsWholeImage
        {
            get { return CropRectangle == new Rectangle(Point.Empty, ContentSize); }
        }

        RectangleF CropScreenRect
        {
            get
            {
                var a = ToScreen(Math.Min(cl, cr), Math.Min(ct, cb));
                var b = ToScreen(Math.Max(cl, cr), Math.Max(ct, cb));
                return RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y);
            }
        }

        PointF[] HandlePoints(RectangleF r)
        {
            float mx = (r.Left + r.Right) / 2f, my = (r.Top + r.Bottom) / 2f;
            return new[]
            {
                new PointF(r.Left, r.Top), new PointF(mx, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, my),
                new PointF(r.Right, r.Bottom), new PointF(mx, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, my)
            };
        }

        int HitHandle(Point p)
        {
            var pts = HandlePoints(CropScreenRect);
            float tol = 9f * dpiScale;
            for (int i = 0; i < pts.Length; i++)
                if (Math.Abs(p.X - pts[i].X) <= tol && Math.Abs(p.Y - pts[i].Y) <= tol) return i;
            return -1;
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
            string label = cropRect.Width + " × " + cropRect.Height;
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

        // ---------------------------------------------------------------- input

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Enter || keyData == Keys.Escape || keyData == Keys.Space) return true;
            if (floatImg != null)
            {
                var k = keyData & Keys.KeyCode;
                if (k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || k == Keys.Delete) return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space && !spaceDown) { spaceDown = true; UpdateCursor(); Invalidate(); }
            if (floatImg != null)
            {
                if (e.KeyCode == Keys.Enter) { CommitPaste(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Delete) { CancelPaste(); e.Handled = true; return; }
                int step = e.Shift ? 10 : 1;
                float dx = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0;
                float dy = e.KeyCode == Keys.Up ? -step : e.KeyCode == Keys.Down ? step : 0;
                if (dx != 0 || dy != 0) { floatRect.Offset(dx, dy); Invalidate(); e.Handled = true; return; }
            }
            if (edit != null && Tool == EditTool.Crop)
            {
                if (e.KeyCode == Keys.Enter) { var h = CropApplyRequested; if (h != null) h(this, EventArgs.Empty); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape) { var h = CropCancelRequested; if (h != null) h(this, EventArgs.Empty); e.Handled = true; }
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) { spaceDown = false; UpdateCursor(); Invalidate(); }
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
            if (f != null && Form.ActiveForm == f) Focus();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouseInside = false;
            if (edit != null) Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (Content == null || drag != DragMode.None) return;
            dragStart = e.Location;

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
            if (floatImg != null)
            {
                int fh = HitFloatHandle(e.Location);
                if (fh >= 0)
                {
                    drag = DragMode.FloatSize; floatHandle = fh;
                    floatDragStart = floatRect; floatDragOrigin = ip;
                    Capture = true; return;
                }
                if (FloatScreenRect.Contains(e.Location))
                {
                    drag = DragMode.FloatMove;
                    floatDragStart = floatRect; floatDragOrigin = ip;
                    Capture = true; Cursor = Cursors.SizeAll; return;
                }
                CommitPaste(); // clicking outside places it
                return;
            }
            switch (Tool)
            {
                case EditTool.Pen:
                case EditTool.Pencil:
                case EditTool.Highlighter:
                    liveWidth = Math.Max(0.5f, ToolSize / zoom);
                    livePoints = new List<PointF> { ip };
                    drag = DragMode.Draw;
                    break;
                case EditTool.Eraser:
                    erasing = new HashSet<Stroke>();
                    drag = DragMode.Erase;
                    EraseAt(e.Location);
                    break;
                case EditTool.Crop:
                    cropDragStartImg = ip;
                    cropDragStartRect = RectangleF.FromLTRB(Math.Min(cl, cr), Math.Min(ct, cb), Math.Max(cl, cr), Math.Max(ct, cb));
                    cropHandle = HitHandle(e.Location);
                    if (cropHandle >= 0) drag = DragMode.CropHandle;
                    else if (CropScreenRect.Contains(e.Location) && !CropIsWholeImage) drag = DragMode.CropMove;
                    else
                    {
                        drag = DragMode.CropNew;
                        var cs = ContentSize;
                        cl = cr = Math.Max(0, Math.Min(cs.Width, ip.X));
                        ct = cb = Math.Max(0, Math.Min(cs.Height, ip.Y));
                    }
                    break;
            }
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            mouseInside = true;
            switch (drag)
            {
                case DragMode.Pan:
                    offset = new PointF(dragStartOffset.X + e.X - dragStart.X, dragStartOffset.Y + e.Y - dragStart.Y);
                    ClampOffset();
                    Invalidate();
                    return;
                case DragMode.Draw:
                    {
                        var ip = ToImage(e.Location);
                        var last = livePoints[livePoints.Count - 1];
                        float dx = (ip.X - last.X) * zoom, dy = (ip.Y - last.Y) * zoom;
                        if (dx * dx + dy * dy >= 1f) livePoints.Add(ip);
                        Invalidate();
                        return;
                    }
                case DragMode.Erase:
                    EraseAt(e.Location);
                    Invalidate();
                    return;
                case DragMode.FloatMove:
                    {
                        var ip = ToImage(e.Location);
                        floatRect = new RectangleF((float)Math.Round(floatDragStart.X + ip.X - floatDragOrigin.X),
                                                   (float)Math.Round(floatDragStart.Y + ip.Y - floatDragOrigin.Y),
                                                   floatDragStart.Width, floatDragStart.Height);
                        Invalidate();
                        return;
                    }
                case DragMode.FloatSize:
                    ResizeFloat(ToImage(e.Location));
                    Invalidate();
                    return;
                case DragMode.CropNew:
                case DragMode.CropMove:
                case DragMode.CropHandle:
                    DragCrop(ToImage(e.Location));
                    Invalidate();
                    return;
            }
            UpdateCursor();
            if (edit != null && Tool != EditTool.Crop) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var mode = drag;
            drag = DragMode.None;
            Capture = false;
            switch (mode)
            {
                case DragMode.Draw:
                    if (livePoints != null && livePoints.Count > 0 && edit != null)
                        edit.AddStroke(new Stroke(Tool, ToolColor, liveWidth, livePoints.ToArray()));
                    livePoints = null;
                    break;
                case DragMode.Erase:
                    if (erasing != null && erasing.Count > 0 && edit != null) edit.RemoveStrokes(erasing);
                    erasing = null;
                    break;
                case DragMode.CropNew:
                case DragMode.CropMove:
                case DragMode.CropHandle:
                    NormalizeCrop();
                    break;
            }
            UpdateCursor();
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (Content == null || e.Button != MouseButtons.Left) return;
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
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Content == null || drag == DragMode.Draw) return;
            float notches = e.Delta / 120f;
            ZoomAt(zoom * (float)Math.Pow(1.2, notches), e.Location);
        }

        void ResizeFloat(PointF ip)
        {
            var r = floatDragStart;
            float l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
            bool left = floatHandle == 0 || floatHandle == 6 || floatHandle == 7;
            bool right = floatHandle == 2 || floatHandle == 3 || floatHandle == 4;
            bool top = floatHandle == 0 || floatHandle == 1 || floatHandle == 2;
            bool bottom = floatHandle == 4 || floatHandle == 5 || floatHandle == 6;
            if (left) l = Math.Min(ip.X, rt - 2);
            if (right) rt = Math.Max(ip.X, l + 2);
            if (top) t = Math.Min(ip.Y, b - 2);
            if (bottom) b = Math.Max(ip.Y, t + 2);
            bool corner = (floatHandle & 1) == 0;
            if (corner)
            {
                // keep the proportions of the original
                float ar = r.Width / r.Height;
                float w = rt - l, h = b - t;
                if (w / h > ar) w = h * ar; else h = w / ar;
                if (left) l = rt - w; else rt = l + w;
                if (top) t = b - h; else b = t + h;
            }
            floatRect = RectangleF.FromLTRB(l, t, rt, b);
        }

        void EraseAt(Point screen)
        {
            if (edit == null || erasing == null) return;
            var ip = ToImage(screen);
            float radius = Math.Max(ToolSize, 6f * dpiScale) / 2f / zoom;
            foreach (var s in edit.Current.Strokes)
                if (!erasing.Contains(s) && s.HitTest(ip, radius)) erasing.Add(s);
        }

        void DragCrop(PointF ip)
        {
            var cs = ContentSize;
            float x = Math.Max(0, Math.Min(cs.Width, ip.X));
            float y = Math.Max(0, Math.Min(cs.Height, ip.Y));
            var r = cropDragStartRect;
            switch (drag)
            {
                case DragMode.CropNew:
                    cr = x; cb = y;
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
                        float l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
                        bool left = cropHandle == 0 || cropHandle == 6 || cropHandle == 7;
                        bool right = cropHandle == 2 || cropHandle == 3 || cropHandle == 4;
                        bool top = cropHandle == 0 || cropHandle == 1 || cropHandle == 2;
                        bool bottom = cropHandle == 4 || cropHandle == 5 || cropHandle == 6;
                        if (left) l = Math.Min(x, rt - 1);
                        if (right) rt = Math.Max(x, l + 1);
                        if (top) t = Math.Min(y, b - 1);
                        if (bottom) b = Math.Max(y, t + 1);
                        cl = l; cr = rt; ct = t; cb = b;
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

        void UpdateCursor()
        {
            if (drag == DragMode.Pan) { Cursor = Cursors.SizeAll; return; }
            if (Content == null) { Cursor = Cursors.Default; return; }
            if (floatImg != null && !spaceDown)
            {
                int h = HitFloatHandle(mouse);
                if (h == 0 || h == 4) Cursor = Cursors.SizeNWSE;
                else if (h == 2 || h == 6) Cursor = Cursors.SizeNESW;
                else if (h == 1 || h == 5) Cursor = Cursors.SizeNS;
                else if (h == 3 || h == 7) Cursor = Cursors.SizeWE;
                else if (FloatScreenRect.Contains(mouse)) Cursor = Cursors.SizeAll;
                else Cursor = Cursors.Default;
                return;
            }
            if (edit == null || spaceDown || Tool == EditTool.Pan) { Cursor = CanPan ? Cursors.Hand : Cursors.Default; return; }
            if (Tool == EditTool.Crop)
            {
                int h = HitHandle(mouse);
                if (h == 0 || h == 4) Cursor = Cursors.SizeNWSE;
                else if (h == 2 || h == 6) Cursor = Cursors.SizeNESW;
                else if (h == 1 || h == 5) Cursor = Cursors.SizeNS;
                else if (h == 3 || h == 7) Cursor = Cursors.SizeWE;
                else if (CropScreenRect.Contains(mouse) && !CropIsWholeImage) Cursor = Cursors.SizeAll;
                else Cursor = Cursors.Cross;
                return;
            }
            Cursor = Cursors.Cross;
        }

        public void RefreshTool()
        {
            UpdateCursor();
            Invalidate();
        }
    }
}
