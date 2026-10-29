using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace StillsAndReels
{
    internal enum Ico
    {
        None, Picture, Pencil, Video, Pointer, ZoomIn, ZoomOut, Crop, More, Snip, Pen, PencilTool, Highlighter,
        Eraser, RotateLeft, RotateRight, FlipH, FlipV, Undo, Redo, Copy, Save, Trash, Check, Close, Play, Pause,
        Volume, VolumeLow, Mute, Fullscreen, ExitFullscreen, Minus, Plus, Grid, Lasso, RectSelect, Screen,
        Settings, Hand, Speed, Folder, Sun, Moon, Music, Refresh,
        Scissors, Snowflake, Text, Magnet, Link, FitWidth, Import, Export, Diamond, Transition, Filter, Sparkle,
        Record, Stop, Mic, Eye, EyeOff, Lock, Unlock, Layers, Paste, Cursor, Audio, Captions, Duplicate, Wave, Detach, ChevronLeft, ChevronRight, Timer
    }

    /// <summary>Line icons drawn in code on a 24×24 grid (no image files needed).</summary>
    internal static class Icons
    {
        public static void Draw(Graphics g, Ico icon, RectangleF r, Color c, float stroke = 1.8f)
        {
            using (var pen = new Pen(c, stroke))
            using (var brush = new SolidBrush(c))
                Render(g, icon, r, pen, brush);
        }

        public static void DrawGradient(Graphics g, Ico icon, RectangleF r, Color a, Color b, float stroke = 1.8f)
        {
            using (var br = new LinearGradientBrush(new RectangleF(0, 0, 24, 24), a, b, 35f))
            using (var pen = new Pen(br, stroke))
                Render(g, icon, r, pen, br);
        }

        static void Render(Graphics g, Ico icon, RectangleF r, Pen pen, Brush fill)
        {
            if (icon == Ico.None || r.Width < 2) return;
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Math.Min(r.Width, r.Height) / 24f;
            g.TranslateTransform(r.X + (r.Width - 24 * s) / 2f, r.Y + (r.Height - 24 * s) / 2f);
            g.ScaleTransform(s, s);
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            try { Shape(g, icon, pen, fill); }
            finally { g.Restore(state); }
        }

        static void RR(Graphics g, Pen p, float x, float y, float w, float h, float rad)
        {
            using (var path = Theme.Rounded(new RectangleF(x, y, w, h), rad)) g.DrawPath(p, path);
        }

        static void Arrow(Graphics g, Pen p, PointF tip, float dx, float dy)
        {
            // small open arrow head pointing along (dx,dy)
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return;
            dx /= len; dy /= len;
            float a = 3.2f;
            var left = new PointF(tip.X - a * dx + a * 0.8f * dy, tip.Y - a * dy - a * 0.8f * dx);
            var right = new PointF(tip.X - a * dx - a * 0.8f * dy, tip.Y - a * dy + a * 0.8f * dx);
            g.DrawLines(p, new[] { left, tip, right });
        }

        static void Shape(Graphics g, Ico icon, Pen p, Brush f)
        {
            switch (icon)
            {
                case Ico.Picture:
                    RR(g, p, 3, 4, 18, 16, 3);
                    g.DrawLines(p, new[] { new PointF(3.8f, 18.5f), new PointF(9.5f, 12.5f), new PointF(13.5f, 16.5f), new PointF(16, 14), new PointF(20.2f, 18.2f) });
                    g.FillEllipse(f, 14.6f, 7.2f, 3.4f, 3.4f);
                    break;
                case Ico.Pencil:
                case Ico.PencilTool:
                    g.DrawPolygon(p, new[] { new PointF(15.5f, 4.5f), new PointF(19.5f, 8.5f), new PointF(8.5f, 19.5f), new PointF(4, 20), new PointF(4.5f, 15.5f) });
                    g.DrawLine(p, 13.5f, 6.5f, 17.5f, 10.5f);
                    if (icon == Ico.PencilTool) g.DrawLine(p, 5.2f, 16.8f, 7.2f, 18.8f);
                    break;
                case Ico.Video:
                    RR(g, p, 3, 5, 18, 14, 3);
                    g.DrawPolygon(p, new[] { new PointF(10, 9), new PointF(15.5f, 12), new PointF(10, 15) });
                    break;
                case Ico.Pointer:
                    g.DrawPolygon(p, new[] { new PointF(6, 3.5f), new PointF(18, 13.5f), new PointF(12.3f, 14.2f), new PointF(15.5f, 20.2f), new PointF(13, 21.3f), new PointF(9.8f, 15.4f), new PointF(6, 19) });
                    break;
                case Ico.ZoomIn:
                case Ico.ZoomOut:
                    g.DrawEllipse(p, 3.5f, 3.5f, 13, 13);
                    g.DrawLine(p, 14.6f, 14.6f, 20.5f, 20.5f);
                    g.DrawLine(p, 6.8f, 10, 13.2f, 10);
                    if (icon == Ico.ZoomIn) g.DrawLine(p, 10, 6.8f, 10, 13.2f);
                    break;
                case Ico.Crop:
                    g.DrawLines(p, new[] { new PointF(6.5f, 2.5f), new PointF(6.5f, 17.5f), new PointF(21.5f, 17.5f) });
                    g.DrawLines(p, new[] { new PointF(2.5f, 6.5f), new PointF(17.5f, 6.5f), new PointF(17.5f, 21.5f) });
                    break;
                case Ico.More:
                    g.FillEllipse(f, 3.6f, 10.6f, 2.8f, 2.8f);
                    g.FillEllipse(f, 10.6f, 10.6f, 2.8f, 2.8f);
                    g.FillEllipse(f, 17.6f, 10.6f, 2.8f, 2.8f);
                    break;
                case Ico.Snip:
                    g.DrawEllipse(p, 3.5f, 14, 6, 6);
                    g.DrawEllipse(p, 14.5f, 14, 6, 6);
                    g.DrawLine(p, 8.4f, 15.2f, 17.5f, 3.5f);
                    g.DrawLine(p, 15.6f, 15.2f, 6.5f, 3.5f);
                    break;
                case Ico.Pen:
                    g.DrawPolygon(p, new[] { new PointF(16, 3.5f), new PointF(20.5f, 8), new PointF(10, 18.5f), new PointF(5.5f, 14) });
                    g.DrawLines(p, new[] { new PointF(5.5f, 14), new PointF(3.5f, 20.5f), new PointF(10, 18.5f) });
                    g.DrawLine(p, 4.6f, 19.4f, 7, 17);
                    break;
                case Ico.Highlighter:
                    g.DrawPolygon(p, new[] { new PointF(14.5f, 3.5f), new PointF(20.5f, 9.5f), new PointF(12, 18), new PointF(6, 12) });
                    g.DrawLines(p, new[] { new PointF(6, 12), new PointF(4.5f, 17.5f), new PointF(6.5f, 19.5f), new PointF(12, 18) });
                    g.DrawLine(p, 3, 21, 9, 21);
                    break;
                case Ico.Eraser:
                    g.DrawPolygon(p, new[] { new PointF(13.5f, 4), new PointF(20.5f, 11), new PointF(11.5f, 20), new PointF(7.5f, 20), new PointF(3.5f, 16), new PointF(3.5f, 14) });
                    g.DrawLine(p, 8, 9.5f, 15, 16.5f);
                    g.DrawLine(p, 11.5f, 20, 20.5f, 20);
                    break;
                case Ico.RotateLeft:
                case Ico.RotateRight:
                    {
                        var st = g.Save();
                        if (icon == Ico.RotateRight) { g.TranslateTransform(24, 0); g.ScaleTransform(-1, 1); }
                        g.DrawArc(p, 4.5f, 4.5f, 15, 15, 200, 280);
                        Arrow(g, p, new PointF(5.1f, 9.4f), -1.2f, 2.2f);
                        g.Restore(st);
                        break;
                    }
                case Ico.FlipH:
                    g.DrawPolygon(p, new[] { new PointF(9.5f, 5), new PointF(9.5f, 19), new PointF(3, 19) });
                    g.FillPolygon(f, new[] { new PointF(14.5f, 5), new PointF(14.5f, 19), new PointF(21, 19) });
                    using (var dash = (Pen)p.Clone()) { dash.DashPattern = new[] { 1.2f, 1.6f }; dash.Width = 1.2f; g.DrawLine(dash, 12, 2.5f, 12, 21.5f); }
                    break;
                case Ico.FlipV:
                    g.DrawPolygon(p, new[] { new PointF(5, 9.5f), new PointF(19, 9.5f), new PointF(19, 3) });
                    g.FillPolygon(f, new[] { new PointF(5, 14.5f), new PointF(19, 14.5f), new PointF(19, 21) });
                    using (var dash = (Pen)p.Clone()) { dash.DashPattern = new[] { 1.2f, 1.6f }; dash.Width = 1.2f; g.DrawLine(dash, 2.5f, 12, 21.5f, 12); }
                    break;
                case Ico.Undo:
                case Ico.Redo:
                    {
                        var st = g.Save();
                        if (icon == Ico.Redo) { g.TranslateTransform(24, 0); g.ScaleTransform(-1, 1); }
                        using (var path = new GraphicsPath())
                        {
                            path.AddLine(4.5f, 9.5f, 14, 9.5f);
                            path.AddArc(9.5f, 9.5f, 10, 10, 270, 180);
                            path.AddLine(14.5f, 19.5f, 9, 19.5f);
                            g.DrawPath(p, path);
                        }
                        g.DrawLines(p, new[] { new PointF(8.5f, 5.5f), new PointF(4.5f, 9.5f), new PointF(8.5f, 13.5f) });
                        g.Restore(st);
                        break;
                    }
                case Ico.Copy:
                    RR(g, p, 8, 8, 12, 12, 2.5f);
                    g.DrawLines(p, new[] { new PointF(16, 5.5f), new PointF(16, 4.5f), new PointF(14.5f, 4) });
                    RR(g, p, 4, 4, 12, 12, 2.5f);
                    break;
                case Ico.Save:
                    g.DrawPolygon(p, new[] { new PointF(4, 4), new PointF(16.5f, 4), new PointF(20, 7.5f), new PointF(20, 20), new PointF(4, 20) });
                    g.DrawLines(p, new[] { new PointF(8, 4), new PointF(8, 8.5f), new PointF(15, 8.5f), new PointF(15, 4) });
                    RR(g, p, 7.5f, 13, 9, 7, 1);
                    break;
                case Ico.Trash:
                    g.DrawLine(p, 4, 6.5f, 20, 6.5f);
                    g.DrawLines(p, new[] { new PointF(9, 6.5f), new PointF(9.5f, 3.8f), new PointF(14.5f, 3.8f), new PointF(15, 6.5f) });
                    g.DrawLines(p, new[] { new PointF(6, 6.5f), new PointF(7, 20.2f), new PointF(17, 20.2f), new PointF(18, 6.5f) });
                    g.DrawLine(p, 10, 10.5f, 10, 16.5f);
                    g.DrawLine(p, 14, 10.5f, 14, 16.5f);
                    break;
                case Ico.Check:
                    g.DrawLines(p, new[] { new PointF(5, 12.5f), new PointF(10, 17.5f), new PointF(19.5f, 7) });
                    break;
                case Ico.Close:
                    g.DrawLine(p, 6, 6, 18, 18);
                    g.DrawLine(p, 18, 6, 6, 18);
                    break;
                case Ico.Play:
                    using (var path = new GraphicsPath())
                    {
                        path.AddPolygon(new[] { new PointF(7.5f, 4.5f), new PointF(19.5f, 12), new PointF(7.5f, 19.5f) });
                        g.FillPath(f, path);
                        g.DrawPath(p, path);
                    }
                    break;
                case Ico.Pause:
                    g.FillRectangle(f, 6.5f, 4.5f, 3.6f, 15);
                    g.FillRectangle(f, 13.9f, 4.5f, 3.6f, 15);
                    break;
                case Ico.Volume:
                case Ico.VolumeLow:
                case Ico.Mute:
                    g.DrawPolygon(p, new[] { new PointF(3.5f, 9), new PointF(7.5f, 9), new PointF(12.5f, 4.5f), new PointF(12.5f, 19.5f), new PointF(7.5f, 15), new PointF(3.5f, 15) });
                    if (icon == Ico.Mute)
                    {
                        g.DrawLine(p, 16, 9, 21, 15);
                        g.DrawLine(p, 21, 9, 16, 15);
                    }
                    else
                    {
                        g.DrawArc(p, 11, 8, 6, 8, -60, 120);
                        if (icon == Ico.Volume) g.DrawArc(p, 11, 4.5f, 10, 15, -60, 120);
                    }
                    break;
                case Ico.Fullscreen:
                    g.DrawLines(p, new[] { new PointF(4, 9), new PointF(4, 4), new PointF(9, 4) });
                    g.DrawLines(p, new[] { new PointF(15, 4), new PointF(20, 4), new PointF(20, 9) });
                    g.DrawLines(p, new[] { new PointF(20, 15), new PointF(20, 20), new PointF(15, 20) });
                    g.DrawLines(p, new[] { new PointF(9, 20), new PointF(4, 20), new PointF(4, 15) });
                    break;
                case Ico.ExitFullscreen:
                    g.DrawLines(p, new[] { new PointF(4, 9), new PointF(9, 9), new PointF(9, 4) });
                    g.DrawLines(p, new[] { new PointF(15, 4), new PointF(15, 9), new PointF(20, 9) });
                    g.DrawLines(p, new[] { new PointF(20, 15), new PointF(15, 15), new PointF(15, 20) });
                    g.DrawLines(p, new[] { new PointF(9, 20), new PointF(9, 15), new PointF(4, 15) });
                    break;
                case Ico.Minus:
                    g.DrawLine(p, 5, 12, 19, 12);
                    break;
                case Ico.Plus:
                    g.DrawLine(p, 5, 12, 19, 12);
                    g.DrawLine(p, 12, 5, 12, 19);
                    break;
                case Ico.Grid:
                    for (int y = 0; y < 3; y++)
                        for (int x = 0; x < 3; x++)
                            g.FillEllipse(f, 4.4f + x * 6.2f, 4.4f + y * 6.2f, 2.8f, 2.8f);
                    break;
                case Ico.Lasso:
                    using (var dash = (Pen)p.Clone())
                    {
                        dash.DashPattern = new[] { 2f, 1.6f };
                        g.DrawBezier(dash, new PointF(5, 13), new PointF(2, 4), new PointF(20, 2), new PointF(20, 10));
                        g.DrawBezier(dash, new PointF(20, 10), new PointF(20, 16), new PointF(12, 17), new PointF(9, 15));
                    }
                    g.DrawLines(p, new[] { new PointF(9, 15), new PointF(7, 18), new PointF(8, 21) });
                    break;
                case Ico.RectSelect:
                    using (var dash = (Pen)p.Clone())
                    {
                        dash.DashPattern = new[] { 2f, 1.6f };
                        g.DrawRectangle(dash, 4, 5, 16, 14);
                    }
                    break;
                case Ico.Screen:
                    RR(g, p, 3, 4, 18, 12.5f, 2);
                    g.DrawLine(p, 12, 16.5f, 12, 20);
                    g.DrawLine(p, 8, 20, 16, 20);
                    break;
                case Ico.Settings:
                    {
                        g.DrawEllipse(p, 9, 9, 6, 6);
                        var pts = new PointF[16];
                        for (int i = 0; i < 16; i++)
                        {
                            double a = Math.PI * 2 * i / 16 - Math.PI / 16;
                            float rr = (i % 2 == 0) ? 9f : 6.8f;
                            pts[i] = new PointF(12 + (float)Math.Cos(a) * rr, 12 + (float)Math.Sin(a) * rr);
                        }
                        g.DrawPolygon(p, pts);
                        break;
                    }
                case Ico.Hand:
                    g.DrawLines(p, new[] { new PointF(8, 13), new PointF(8, 5.5f), new PointF(10.5f, 5.5f), new PointF(10.5f, 11) });
                    g.DrawLines(p, new[] { new PointF(10.5f, 10), new PointF(10.5f, 4), new PointF(13, 4), new PointF(13, 11) });
                    g.DrawLines(p, new[] { new PointF(13, 10), new PointF(13, 5), new PointF(15.5f, 5), new PointF(15.5f, 11.5f) });
                    g.DrawLines(p, new[] { new PointF(15.5f, 10.5f), new PointF(15.5f, 7.5f), new PointF(18, 7.5f), new PointF(18, 14.5f), new PointF(16, 20), new PointF(9.5f, 20), new PointF(5, 14.5f), new PointF(6, 12.5f), new PointF(8, 13) });
                    break;
                case Ico.Speed:
                    g.DrawArc(p, 3.5f, 5, 17, 17, 180, 180);
                    g.DrawLine(p, 12, 13.5f, 16, 9.5f);
                    g.FillEllipse(f, 10.6f, 12.1f, 2.8f, 2.8f);
                    break;
                case Ico.Folder:
                    g.DrawPolygon(p, new[] { new PointF(3, 6), new PointF(9, 6), new PointF(11, 8), new PointF(21, 8), new PointF(21, 19), new PointF(3, 19) });
                    break;
                case Ico.Sun:
                    g.DrawEllipse(p, 8, 8, 8, 8);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = Math.PI * 2 * i / 8;
                        g.DrawLine(p, 12 + (float)Math.Cos(a) * 6.5f, 12 + (float)Math.Sin(a) * 6.5f, 12 + (float)Math.Cos(a) * 9f, 12 + (float)Math.Sin(a) * 9f);
                    }
                    break;
                case Ico.Moon:
                    using (var outer = new GraphicsPath())
                    using (var inner = new GraphicsPath())
                    using (var region = new Region())
                    {
                        outer.AddEllipse(3.5f, 3.5f, 17, 17);
                        inner.AddEllipse(9.5f, 1.5f, 14, 14);
                        region.MakeEmpty();
                        region.Union(outer);
                        region.Exclude(inner);
                        g.FillRegion(f, region);
                    }
                    break;
                case Ico.Music:
                    g.DrawLines(p, new[] { new PointF(9, 17), new PointF(9, 5), new PointF(19, 3.5f), new PointF(19, 15.5f) });
                    g.DrawEllipse(p, 4.5f, 15, 4.5f, 4.5f);
                    g.DrawEllipse(p, 14.5f, 13.5f, 4.5f, 4.5f);
                    break;
                case Ico.Scissors:
                    g.DrawEllipse(p, 3.5f, 14.5f, 5.5f, 5.5f);
                    g.DrawEllipse(p, 15, 14.5f, 5.5f, 5.5f);
                    g.DrawLine(p, 7.8f, 15.4f, 17.5f, 3.5f);
                    g.DrawLine(p, 16.2f, 15.4f, 6.5f, 3.5f);
                    break;
                case Ico.Snowflake:
                    for (int i = 0; i < 3; i++)
                    {
                        double a = Math.PI * i / 3 + Math.PI / 2;
                        float dx = (float)Math.Cos(a) * 9, dy = (float)Math.Sin(a) * 9;
                        g.DrawLine(p, 12 - dx, 12 - dy, 12 + dx, 12 + dy);
                        foreach (int sgn in new[] { -1, 1 })
                        {
                            float ex = 12 + sgn * dx * 0.7f, ey = 12 + sgn * dy * 0.7f;
                            float nx = (float)Math.Cos(a + Math.PI / 2) * 2.4f, ny = (float)Math.Sin(a + Math.PI / 2) * 2.4f;
                            float ox = sgn * dx * 0.22f, oy = sgn * dy * 0.22f;
                            g.DrawLines(p, new[] { new PointF(ex + nx - ox, ey + ny - oy), new PointF(ex, ey), new PointF(ex - nx - ox, ey - ny - oy) });
                        }
                    }
                    break;
                case Ico.Text:
                    g.DrawLines(p, new[] { new PointF(5, 7), new PointF(5, 4.5f), new PointF(19, 4.5f), new PointF(19, 7) });
                    g.DrawLine(p, 12, 4.5f, 12, 19.5f);
                    g.DrawLine(p, 9, 19.5f, 15, 19.5f);
                    break;
                case Ico.Magnet:
                    g.DrawArc(p, 5, 6, 14, 14, 0, 180);
                    g.DrawLine(p, 5, 13, 5, 5);
                    g.DrawLine(p, 19, 13, 19, 5);
                    g.DrawLine(p, 9.5f, 13, 9.5f, 5);
                    g.DrawLine(p, 14.5f, 13, 14.5f, 5);
                    g.DrawLine(p, 5, 8.5f, 9.5f, 8.5f);
                    g.DrawLine(p, 14.5f, 8.5f, 19, 8.5f);
                    break;
                case Ico.Link:
                    RR(g, p, 2.5f, 8.5f, 11, 7, 3.5f);
                    RR(g, p, 10.5f, 8.5f, 11, 7, 3.5f);
                    break;
                case Ico.FitWidth:
                    g.DrawLine(p, 3.5f, 5, 3.5f, 19);
                    g.DrawLine(p, 20.5f, 5, 20.5f, 19);
                    g.DrawLine(p, 7, 12, 17, 12);
                    Arrow(g, p, new PointF(7, 12), -1, 0);
                    Arrow(g, p, new PointF(17, 12), 1, 0);
                    break;
                case Ico.Import:
                    g.DrawLine(p, 12, 3.5f, 12, 14.5f);
                    Arrow(g, p, new PointF(12, 14.5f), 0, 1);
                    g.DrawLines(p, new[] { new PointF(4, 14), new PointF(4, 20), new PointF(20, 20), new PointF(20, 14) });
                    break;
                case Ico.Export:
                    g.DrawLine(p, 12, 15, 12, 3.5f);
                    Arrow(g, p, new PointF(12, 3.5f), 0, -1);
                    g.DrawLines(p, new[] { new PointF(4, 14), new PointF(4, 20), new PointF(20, 20), new PointF(20, 14) });
                    break;
                case Ico.Diamond:
                    g.DrawPolygon(p, new[] { new PointF(12, 4), new PointF(20, 12), new PointF(12, 20), new PointF(4, 12) });
                    break;
                case Ico.Transition:
                    RR(g, p, 3, 6, 11, 12, 2);
                    g.FillRectangle(f, 10.5f, 7, 3, 10);
                    RR(g, p, 10, 6, 11, 12, 2);
                    break;
                case Ico.Filter:
                    g.DrawEllipse(p, 4, 3.5f, 10, 10);
                    g.DrawEllipse(p, 10, 3.5f, 10, 10);
                    g.DrawEllipse(p, 7, 10.5f, 10, 10);
                    break;
                case Ico.Sparkle:
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(10, 3, 10.8f, 8.5f, 12.5f, 10.2f, 18, 11);
                        path.AddBezier(18, 11, 12.5f, 11.8f, 10.8f, 13.5f, 10, 19);
                        path.AddBezier(10, 19, 9.2f, 13.5f, 7.5f, 11.8f, 2, 11);
                        path.AddBezier(2, 11, 7.5f, 10.2f, 9.2f, 8.5f, 10, 3);
                        g.DrawPath(p, path);
                    }
                    g.DrawLine(p, 18.5f, 2.5f, 18.5f, 7.5f);
                    g.DrawLine(p, 16, 5, 21, 5);
                    break;
                case Ico.Record:
                    g.FillEllipse(f, 6, 6, 12, 12);
                    break;
                case Ico.Stop:
                    using (var path = Theme.Rounded(new RectangleF(6.5f, 6.5f, 11, 11), 2)) g.FillPath(f, path);
                    break;
                case Ico.Mic:
                    RR(g, p, 9, 3, 6, 11, 3);
                    g.DrawArc(p, 5.5f, 6, 13, 12, 0, 180);
                    g.DrawLine(p, 12, 18, 12, 21);
                    g.DrawLine(p, 8.5f, 21, 15.5f, 21);
                    break;
                case Ico.Eye:
                case Ico.EyeOff:
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(2.5f, 12, 6, 5.5f, 18, 5.5f, 21.5f, 12);
                        path.AddBezier(21.5f, 12, 18, 18.5f, 6, 18.5f, 2.5f, 12);
                        g.DrawPath(p, path);
                    }
                    g.DrawEllipse(p, 9, 9, 6, 6);
                    if (icon == Ico.EyeOff) g.DrawLine(p, 4, 4, 20, 20);
                    break;
                case Ico.Lock:
                case Ico.Unlock:
                    RR(g, p, 5, 11, 14, 10, 2);
                    if (icon == Ico.Lock) g.DrawArc(p, 8, 4, 8, 10, 180, 180);
                    else g.DrawArc(p, 8, 4, 8, 10, 180, 130);
                    g.DrawLine(p, 8, 9, 8, 11);
                    if (icon == Ico.Lock) g.DrawLine(p, 16, 9, 16, 11);
                    break;
                case Ico.Layers:
                    g.DrawPolygon(p, new[] { new PointF(12, 3.5f), new PointF(21, 8.5f), new PointF(12, 13.5f), new PointF(3, 8.5f) });
                    g.DrawLines(p, new[] { new PointF(3, 12.5f), new PointF(12, 17.5f), new PointF(21, 12.5f) });
                    g.DrawLines(p, new[] { new PointF(3, 16.5f), new PointF(12, 21.5f), new PointF(21, 16.5f) });
                    break;
                case Ico.Paste:
                    RR(g, p, 5, 5, 14, 16, 2);
                    RR(g, p, 9, 3, 6, 4, 1.2f);
                    g.DrawLine(p, 8.5f, 11.5f, 15.5f, 11.5f);
                    g.DrawLine(p, 8.5f, 15.5f, 13.5f, 15.5f);
                    break;
                case Ico.Cursor:
                    g.DrawLine(p, 12, 3, 12, 21);
                    g.FillPolygon(f, new[] { new PointF(8, 3), new PointF(16, 3), new PointF(12, 7.5f) });
                    break;
                case Ico.Audio:
                case Ico.Wave:
                    {
                        float[] hs = { 4, 9, 14, 8, 16, 10, 5 };
                        for (int i = 0; i < hs.Length; i++)
                        {
                            float x = 4 + i * 2.66f;
                            g.DrawLine(p, x, 12 - hs[i] / 2, x, 12 + hs[i] / 2);
                        }
                        break;
                    }
                case Ico.Captions:
                    RR(g, p, 3, 5, 18, 14, 3);
                    g.DrawLine(p, 6.5f, 11, 11, 11);
                    g.DrawLine(p, 13.5f, 11, 17.5f, 11);
                    g.DrawLine(p, 6.5f, 15, 9.5f, 15);
                    g.DrawLine(p, 12, 15, 17.5f, 15);
                    break;
                case Ico.Duplicate:
                    RR(g, p, 8, 8, 12, 12, 2.5f);
                    RR(g, p, 4, 4, 12, 12, 2.5f);
                    g.DrawLine(p, 14, 11, 14, 17);
                    g.DrawLine(p, 11, 14, 17, 14);
                    break;
                case Ico.Detach:
                    g.DrawLines(p, new[] { new PointF(8, 15), new PointF(8, 4.5f), new PointF(16, 3.5f), new PointF(16, 13.5f) });
                    g.DrawEllipse(p, 4, 13.5f, 4, 4);
                    g.DrawEllipse(p, 12, 12, 4, 4);
                    g.DrawLine(p, 4, 21, 20, 21);
                    break;
                case Ico.ChevronLeft:
                    g.DrawLines(p, new[] { new PointF(14.5f, 5.5f), new PointF(8, 12), new PointF(14.5f, 18.5f) });
                    break;
                case Ico.ChevronRight:
                    g.DrawLines(p, new[] { new PointF(9.5f, 5.5f), new PointF(16, 12), new PointF(9.5f, 18.5f) });
                    break;
                case Ico.Refresh:
                    g.DrawArc(p, 4.5f, 4.5f, 15, 15, 30, 300);
                    Arrow(g, p, new PointF(18.8f, 8.2f), 0.9f, -2.2f);
                    break;
                case Ico.Timer:   // stopwatch
                    g.DrawEllipse(p, 4.5f, 6, 15, 15);
                    g.DrawLine(p, 10, 2.5f, 14, 2.5f);
                    g.DrawLine(p, 12, 2.5f, 12, 6);
                    g.DrawLine(p, 12, 13.5f, 12, 9.5f);
                    g.DrawLine(p, 12, 13.5f, 14.8f, 15.5f);
                    g.DrawLine(p, 18.2f, 6.8f, 19.6f, 5.4f);
                    break;
            }
        }
    }
}
