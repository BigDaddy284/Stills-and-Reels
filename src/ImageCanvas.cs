using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>Settings of the picture tools beyond colour and size. The sidebar edits them; the canvas reads them.</summary>
    internal sealed class ToolOptions
    {
        public Color Fill = Color.Empty;            // shape and text-box fill (Empty: none)
        public string FontName = "Segoe UI";
        public bool Bold, Italic;
        public int Tolerance = 32;                  // magic wand and fill bucket, 0..255
        public bool Contiguous = true;              // wand and fill stop at edges (off: everywhere in the picture)
        public bool SampleAllLayers = true;         // wand, fill and picker look at the finished picture, not just the active layer
        public bool ErasePixels;                    // eraser: rub out pixels instead of whole strokes and shapes
        public bool RedactPixelate = true;          // redact tool: pixelate (true) or blur
        public int RedactStrength = 12;
        public float CropAspect;                    // width / height to keep while cropping (0: free)
    }

    /// <summary>
    /// Shows the current picture with zoom and pan, and handles the editing tools.
    /// This file is the view (what is shown, zooming, painting); ImageCanvas.Tools.cs holds the tools.
    /// </summary>
    internal sealed partial class ImageCanvas : Control
    {
        static readonly float[] ZoomSteps =
        {
            0.02f, 0.03f, 0.05f, 0.0625f, 0.0833f, 0.1f, 0.125f, 0.1667f, 0.25f, 0.3333f, 0.5f, 0.6667f, 0.75f,
            1f, 1.25f, 1.5f, 2f, 3f, 4f, 5f, 6f, 8f, 10f, 12f, 16f, 24f, 32f
        };
        const float MinZoom = 0.02f, MaxZoom = 32f;

        // what is shown: a still picture (with its half-size copies), an animated GIF, or a picture being edited
        PicSurface viewSurface;
        PicMips viewMips;
        bool viewHasAlpha;
        Image viewImage;      // animated pictures only
        bool animated;
        EditSession edit;
        string message, subMessage;
        int viewTurns;        // quarter turns for looking at a picture sideways (viewer only; the file is not changed)

        float zoom = 1f;
        bool fit = true;
        PointF offset;
        float dpiScale = 1f;

        readonly Timer hqTimer = new Timer { Interval = 160 };
        bool zoomingFast;

        TextureBrush checker;

        // Empty-state text shown when there is nothing to display (and no message).
        public Ico EmptyIcon = Ico.Picture;
        public string EmptyTitle = "Click a picture anywhere";
        public string EmptyDetail = "Click a picture in File Explorer, on the desktop, on a web page or in another app, or copy one,\nand it appears here. Arrow keys work in File Explorer. Use File > Open to pick one directly.";

        bool pixelGrid;
        public bool ShowPixelGrid { get { return pixelGrid; } set { pixelGrid = value; Invalidate(); } }

        public void SetEmptyState(Ico icon, string title, string detail)
        {
            EmptyIcon = icon; EmptyTitle = title; EmptyDetail = detail;
            if (!HasContent) Invalidate();
        }

        public event EventHandler ZoomChanged;

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
        public bool HasContent { get { return edit != null || viewSurface != null || viewImage != null; } }

        Size ContentSize
        {
            get
            {
                if (edit != null) return edit.Size;
                if (viewSurface != null) return (viewTurns & 1) == 1 ? new Size(viewSurface.Height, viewSurface.Width) : viewSurface.Size;
                if (viewImage != null) return viewImage.Size;
                return Size.Empty;
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
                EndTextEdit(false);
                if (checker != null) checker.Dispose();
                if (drawBuf != null) { drawBuf.Dispose(); drawBuf = null; }
                if (turned != null) { turnedMips.Dispose(); turned.Dispose(); turned = null; }
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- content

        /// <summary>Shows a still picture. The surface and its half-size copies stay owned by the caller.</summary>
        public void ShowSurface(PicSurface surface, PicMips mips, bool hasAlpha)
        {
            StopAnimation();
            viewImage = null; animated = false;
            viewSurface = surface; viewMips = mips; viewHasAlpha = hasAlpha;
            ResetTurn();
            message = subMessage = null;
            fit = true;
            Relayout();
            Invalidate();
        }

        /// <summary>Shows an animated picture (or, with null, clears the view).</summary>
        public void ShowImage(Image img, bool isAnimated)
        {
            StopAnimation();
            viewSurface = null; viewMips = null;
            ResetTurn();
            viewImage = img;
            animated = isAnimated && img != null;
            message = subMessage = null;
            if (animated && edit == null) ImageAnimator.Animate(viewImage, OnFrameChanged);
            fit = true;
            Relayout();
            Invalidate();
        }

        public void ShowMessage(string title, string detail)
        {
            StopAnimation();
            viewImage = null; viewSurface = null; viewMips = null;
            ResetTurn();
            message = title;
            subMessage = detail;
            Invalidate();
            RaiseZoom();
        }

        // viewing a picture turned on its side: a turned copy is made on demand and dropped with the picture
        PicSurface turned;
        readonly PicMips turnedMips = new PicMips();

        void ResetTurn()
        {
            viewTurns = 0;
            if (turned != null) { turnedMips.Dispose(); turned.Dispose(); turned = null; }
        }

        /// <summary>Turns the view a quarter turn (viewer only; nothing is saved).</summary>
        public void TurnView(bool clockwise)
        {
            if (viewSurface == null || edit != null) return;
            viewTurns = (viewTurns + (clockwise ? 1 : 3)) & 3;
            if (turned != null) { turnedMips.Dispose(); turned.Dispose(); turned = null; }
            if (viewTurns != 0)
            {
                turned = viewSurface.RotateFlip(viewTurns == 1 ? RotateFlipType.Rotate90FlipNone : viewTurns == 2 ? RotateFlipType.Rotate180FlipNone : RotateFlipType.Rotate270FlipNone);
                turnedMips.Build(turned);
            }
            fit = true;
            Relayout();
            Invalidate();
        }

        public int ViewTurns { get { return viewTurns; } }

        public EditSession Edit
        {
            get { return edit; }
            set
            {
                if (ReferenceEquals(edit, value)) return;
                EndTextEdit(false);
                EndLayerTransform();
                if (edit != null) { edit.Changed -= OnEditChanged; edit.AreaChanged -= OnEditArea; }
                edit = value;
                ResetToolState();
                if (edit != null)
                {
                    StopAnimation();
                    edit.Changed += OnEditChanged;
                    edit.AreaChanged += OnEditArea;
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
                Invalidate();
            }
            else ClampCrop();
            ValidateSelection();
        }

        void OnEditArea(Rectangle canvasRect) { InvalidateCanvas(canvasRect, 3); }

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
            if (!HasContent) return;
            newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, newZoom));
            float ix = (anchor.X - offset.X) / zoom, iy = (anchor.Y - offset.Y) / zoom;
            zoom = newZoom;
            fit = false;
            offset = new PointF(anchor.X - ix * zoom, anchor.Y - iy * zoom);
            ClampOffset();
            zoomingFast = true;
            hqTimer.Stop(); hqTimer.Start();
            PlaceTextBox();
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
            PlaceTextBox();
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
            if (fit) Relayout(); else { ClampOffset(); PlaceTextBox(); }
            Invalidate();
        }

        void RaiseZoom()
        {
            var h = ZoomChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        PointF ToImage(Point p) { return new PointF((p.X - offset.X) / zoom, (p.Y - offset.Y) / zoom); }
        PointF ToScreen(float x, float y) { return new PointF(offset.X + x * zoom, offset.Y + y * zoom); }
        PointF ToScreen(PointF p) { return ToScreen(p.X, p.Y); }

        RectangleF ToScreen(RectangleF r)
        {
            var a = ToScreen(r.Left, r.Top);
            var b = ToScreen(r.Right, r.Bottom);
            return RectangleF.FromLTRB(a.X, a.Y, b.X, b.Y);
        }

        /// <summary>Asks for a repaint of one part of the picture (canvas pixels), plus a margin in screen pixels.</summary>
        void InvalidateCanvas(RectangleF canvasRect, float margin)
        {
            if (canvasRect.Width <= 0 && canvasRect.Height <= 0) return;
            var s = ToScreen(canvasRect);
            var r = Rectangle.FromLTRB((int)Math.Floor(s.Left - margin), (int)Math.Floor(s.Top - margin), (int)Math.Ceiling(s.Right + margin), (int)Math.Ceiling(s.Bottom + margin));
            r.Intersect(ClientRectangle);
            if (r.Width > 0 && r.Height > 0) Invalidate(r);
        }

        // ---------------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Current.Canvas);
            if (!HasContent)
            {
                DrawMessage(g);
                return;
            }

            var cs = ContentSize;
            var dest = new RectangleF(offset.X, offset.Y, cs.Width * zoom, cs.Height * zoom);
            var clip = Rectangle.Intersect(e.ClipRectangle.IsEmpty ? ClientRectangle : e.ClipRectangle, ClientRectangle);

            if (animated && edit == null && viewImage != null)
            {
                try { ImageAnimator.UpdateFrames(viewImage); } catch { }
                var visibleA = RectangleF.Intersect(dest, ClientRectangle);
                if (Image.IsAlphaPixelFormat(viewImage.PixelFormat) && visibleA.Width > 0 && visibleA.Height > 0) g.FillRectangle(Checker, visibleA);
                DrawAnimated(g, viewImage, dest);
                return;
            }

            PicMips mips = edit != null ? edit.Compositor.Mips : turned != null ? turnedMips : viewMips;
            if (mips == null) return;

            if (edit != null || viewHasAlpha)
            {
                var visible = RectangleF.Intersect(dest, clip);
                if (visible.Width > 0 && visible.Height > 0) g.FillRectangle(Checker, visible);
            }

            DrawPicture(g, mips, cs, clip);
            if (pixelGrid && zoom >= 5.99f) DrawPixelGrid(g, dest);
            if (edit != null) PaintTools(g, dest);
        }

        /// <summary>Draws the visible part of the picture from the half-size copy nearest the size on screen.</summary>
        void DrawPicture(Graphics g, PicMips mips, Size full, Rectangle clip)
        {
            int level = mips.LevelFor(zoom);
            var src = mips.Level(level);
            float f = 1 << level;                 // one pixel of this level covers f picture pixels
            float z = zoom * f;                   // screen pixels per pixel of this level
            float maxX = full.Width / f, maxY = full.Height / f;

            // the part of the level under the area being repainted, with a pixel to spare
            float lx0 = (clip.Left - offset.X) / z, ly0 = (clip.Top - offset.Y) / z;
            float lx1 = (clip.Right - offset.X) / z, ly1 = (clip.Bottom - offset.Y) / z;
            float sx0 = Math.Max(0, (float)Math.Floor(lx0) - 1), sy0 = Math.Max(0, (float)Math.Floor(ly0) - 1);
            float sx1 = Math.Min(maxX, (float)Math.Ceiling(lx1) + 1), sy1 = Math.Min(maxY, (float)Math.Ceiling(ly1) + 1);
            if (sx1 <= sx0 || sy1 <= sy0) return;
            var srcRect = new RectangleF(sx0, sy0, sx1 - sx0, sy1 - sy0);
            var d = new RectangleF(offset.X + sx0 * z, offset.Y + sy0 * z, srcRect.Width * z, srcRect.Height * z);

            var state = g.Save();
            g.SetClip(clip);
            g.CompositingQuality = CompositingQuality.HighSpeed;
            if (z >= 1.999f || Math.Abs(z - 1f) < 0.001f) g.InterpolationMode = InterpolationMode.NearestNeighbor;
            else g.InterpolationMode = zoomingFast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            // Copy just that part out first: drawing then costs the same however large the whole picture is.
            int bw = (int)srcRect.Width, bh = (int)srcRect.Height;
            if (drawBuf == null || drawBuf.Width != bw || drawBuf.Height != bh)
            {
                if (drawBuf != null) drawBuf.Dispose();
                drawBuf = new PicSurface(bw, bh);
            }
            drawBuf.CopyFrom(src, new Rectangle((int)sx0, (int)sy0, bw, bh), Point.Empty);
            using (var attr = new ImageAttributes())
            {
                attr.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(drawBuf.Bitmap, new[] { d.Location, new PointF(d.Right, d.Top), new PointF(d.Left, d.Bottom) }, new RectangleF(0, 0, bw, bh), GraphicsUnit.Pixel, attr);
            }
            g.Restore(state);
        }

        PicSurface drawBuf;   // the part of the picture being painted

        void DrawAnimated(Graphics g, Image img, RectangleF dest)
        {
            var vis = RectangleF.Intersect(dest, ClientRectangle);
            if (vis.Width <= 0 || vis.Height <= 0) return;
            g.InterpolationMode = zoom >= 1.999f || Math.Abs(zoom - 1f) < 0.001f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(img, dest);
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

        /// <summary>The colour of the finished picture under a screen point (transparent outside it).</summary>
        public Color ColorAt(Point screen)
        {
            var ip = ToImage(screen);
            int x = (int)Math.Floor(ip.X), y = (int)Math.Floor(ip.Y);
            PicSurface s = edit != null ? edit.Compositor.Picture : turned ?? viewSurface;
            return s == null ? Color.Empty : s.GetColor(x, y);
        }

        /// <summary>The picture position under the mouse, for the status bar (null when the mouse is off the picture).</summary>
        public Point? MousePixel
        {
            get
            {
                if (!mouseInside || !HasContent) return null;
                var ip = ToImage(mouse);
                var cs = ContentSize;
                int x = (int)Math.Floor(ip.X), y = (int)Math.Floor(ip.Y);
                if (x < 0 || y < 0 || x >= cs.Width || y >= cs.Height) return null;
                return new Point(x, y);
            }
        }
    }
}
