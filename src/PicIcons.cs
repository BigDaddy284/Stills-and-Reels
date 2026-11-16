using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace StillsAndReels
{
    /// <summary>Line icons of the picture tools, on the same 24×24 grid as Icons.cs.</summary>
    internal static class PicIcons
    {
        static void RR(Graphics g, Pen p, float x, float y, float w, float h, float rad)
        {
            using (var path = Theme.Rounded(new RectangleF(x, y, w, h), rad)) g.DrawPath(p, path);
        }

        static void Head(Graphics g, Pen p, PointF tip, float dx, float dy, float a = 3.2f)
        {
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return;
            dx /= len; dy /= len;
            var left = new PointF(tip.X - a * dx + a * 0.8f * dy, tip.Y - a * dy - a * 0.8f * dx);
            var right = new PointF(tip.X - a * dx - a * 0.8f * dy, tip.Y - a * dy + a * 0.8f * dx);
            g.DrawLines(p, new[] { left, tip, right });
        }

        static Pen Dashed(Pen p)
        {
            var d = (Pen)p.Clone();
            d.DashStyle = DashStyle.Custom;
            d.DashPattern = new[] { 1.6f, 1.9f };
            d.DashCap = DashCap.Round;
            return d;
        }

        public static void Shape(Graphics g, Ico icon, Pen p, Brush f)
        {
            switch (icon)
            {
                case Ico.EllipseSelect:
                    using (var d = Dashed(p)) g.DrawEllipse(d, 3.5f, 5.5f, 17, 13);
                    break;
                case Ico.Wand:
                    g.DrawLine(p, 4, 20, 14, 10);
                    g.DrawLine(p, 16.5f, 2.5f, 16.5f, 6);
                    g.DrawLine(p, 16.5f, 9, 16.5f, 12.5f);
                    g.DrawLine(p, 11.5f, 7.5f, 14.8f, 7.5f);
                    g.DrawLine(p, 18.2f, 7.5f, 21.5f, 7.5f);
                    g.DrawLine(p, 13.2f, 4.2f, 14.6f, 5.6f);
                    g.DrawLine(p, 19.8f, 4.2f, 18.4f, 5.6f);
                    g.DrawLine(p, 19.8f, 10.8f, 18.4f, 9.4f);
                    break;
                case Ico.Bucket:
                    g.DrawPolygon(p, new[] { new PointF(10.5f, 4), new PointF(18.5f, 12), new PointF(11.5f, 19), new PointF(3.5f, 11) });
                    g.DrawLine(p, 4.5f, 12, 17.5f, 12);
                    g.DrawLine(p, 10.5f, 4, 8, 1.8f);
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(20, 14.5f, 22.5f, 18, 22, 20.5f, 20, 20.5f);
                        path.AddBezier(20, 20.5f, 18, 20.5f, 17.5f, 18, 20, 14.5f);
                        g.FillPath(f, path);
                    }
                    break;
                case Ico.Picker:
                    g.DrawPolygon(p, new[] { new PointF(13.5f, 8), new PointF(16, 10.5f), new PointF(7.5f, 19), new PointF(4.2f, 19.8f), new PointF(5, 16.5f) });
                    g.DrawLine(p, 12, 6.5f, 17.5f, 12);
                    g.DrawLines(p, new[] { new PointF(14.8f, 9.2f), new PointF(18, 6), new PointF(18.8f, 3.8f), new PointF(20.2f, 5.2f), new PointF(18, 6) });
                    break;
                case Ico.LineTool:
                    g.DrawLine(p, 4.5f, 19.5f, 19.5f, 4.5f);
                    break;
                case Ico.ArrowTool:
                    g.DrawLine(p, 4.5f, 19.5f, 19, 5);
                    Head(g, p, new PointF(19.5f, 4.5f), 1, -1, 5f);
                    break;
                case Ico.RectTool:
                    g.DrawRectangle(p, 3.5f, 5.5f, 17, 13);
                    break;
                case Ico.RoundRectTool:
                    RR(g, p, 3.5f, 5.5f, 17, 13, 4.5f);
                    break;
                case Ico.EllipseTool:
                    g.DrawEllipse(p, 3.5f, 5.5f, 17, 13);
                    break;
                case Ico.StepTool:
                    g.DrawEllipse(p, 3.5f, 3.5f, 17, 17);
                    g.DrawLines(p, new[] { new PointF(10, 9.5f), new PointF(12.5f, 7.5f), new PointF(12.5f, 16.5f) });
                    break;
                case Ico.Redact:
                    RR(g, p, 3.5f, 3.5f, 17, 17, 2.5f);
                    g.FillRectangle(f, 6.5f, 6.5f, 3.6f, 3.6f);
                    g.FillRectangle(f, 13.9f, 6.5f, 3.6f, 3.6f);
                    g.FillRectangle(f, 10.2f, 10.2f, 3.6f, 3.6f);
                    g.FillRectangle(f, 6.5f, 13.9f, 3.6f, 3.6f);
                    g.FillRectangle(f, 13.9f, 13.9f, 3.6f, 3.6f);
                    break;
                case Ico.Sliders:
                    g.DrawLine(p, 4, 7, 20, 7);
                    g.DrawLine(p, 4, 12, 20, 12);
                    g.DrawLine(p, 4, 17, 20, 17);
                    g.FillEllipse(f, 13, 4.5f, 5, 5);
                    g.FillEllipse(f, 6, 9.5f, 5, 5);
                    g.FillEllipse(f, 11.5f, 14.5f, 5, 5);
                    break;
                case Ico.ArrowUp:
                    g.DrawLine(p, 12, 19.5f, 12, 5);
                    Head(g, p, new PointF(12, 4.5f), 0, -1, 5f);
                    break;
                case Ico.ArrowDown:
                    g.DrawLine(p, 12, 4.5f, 12, 19);
                    Head(g, p, new PointF(12, 19.5f), 0, 1, 5f);
                    break;
                case Ico.MergeDown:
                    g.DrawLine(p, 12, 3.5f, 12, 13);
                    Head(g, p, new PointF(12, 13.5f), 0, 1, 4.2f);
                    RR(g, p, 4, 17, 16, 4, 1.5f);
                    break;
                case Ico.Resize:
                    RR(g, p, 3.5f, 9.5f, 11, 11, 2);
                    g.DrawLines(p, new[] { new PointF(8, 3.5f), new PointF(20.5f, 3.5f), new PointF(20.5f, 16) });
                    g.DrawLine(p, 13, 11, 19.5f, 4.5f);
                    Head(g, p, new PointF(20, 4), 1, -1, 3.6f);
                    break;
                case Ico.CanvasSize:
                    using (var d = Dashed(p)) g.DrawRectangle(d, 3, 3, 18, 18);
                    RR(g, p, 7.5f, 7.5f, 9, 9, 1.5f);
                    break;
                case Ico.Straighten:
                    {
                        var st = g.Save();
                        g.TranslateTransform(12, 12);
                        g.RotateTransform(-14);
                        g.DrawRectangle(p, -7, -5, 14, 10);
                        g.Restore(st);
                        g.DrawLine(p, 2.5f, 20.5f, 21.5f, 20.5f);
                        break;
                    }
                case Ico.Trim:
                    g.DrawLine(p, 3, 6.5f, 21, 6.5f);
                    g.DrawLine(p, 3, 17.5f, 21, 17.5f);
                    g.DrawLine(p, 12, 2, 12, 5);
                    Head(g, p, new PointF(12, 5.2f), 0, 1, 2.6f);
                    g.DrawLine(p, 12, 22, 12, 19);
                    Head(g, p, new PointF(12, 18.8f), 0, -1, 2.6f);
                    g.FillRectangle(f, 7, 9.5f, 10, 5);
                    break;
                case Ico.Bold:
                    using (var path = new GraphicsPath())
                    {
                        path.AddLines(new[] { new PointF(7, 4.5f), new PointF(13, 4.5f) });
                        path.AddBezier(13, 4.5f, 17.5f, 4.5f, 17.5f, 11.5f, 13, 11.5f);
                        path.AddLine(13, 11.5f, 7, 11.5f);
                        path.StartFigure();
                        path.AddLine(7, 11.5f, 14, 11.5f);
                        path.AddBezier(14, 11.5f, 19, 11.5f, 19, 19.5f, 14, 19.5f);
                        path.AddLine(14, 19.5f, 7, 19.5f);
                        path.StartFigure();
                        path.AddLine(7, 4.5f, 7, 19.5f);
                        using (var thick = (Pen)p.Clone()) { thick.Width = p.Width * 1.45f; g.DrawPath(thick, path); }
                    }
                    break;
                case Ico.Italic:
                    g.DrawLine(p, 10.5f, 4.5f, 17, 4.5f);
                    g.DrawLine(p, 7, 19.5f, 13.5f, 19.5f);
                    g.DrawLine(p, 13.8f, 4.5f, 10.2f, 19.5f);
                    break;
                case Ico.ToFront:
                    RR(g, p, 9, 9, 11.5f, 11.5f, 2);
                    g.FillRectangle(f, 3.5f, 3.5f, 11, 11);
                    break;
                case Ico.ToBack:
                    g.FillRectangle(f, 9.5f, 9.5f, 11, 11);
                    RR(g, p, 3.5f, 3.5f, 11.5f, 11.5f, 2);
                    break;
                case Ico.Info:
                    g.DrawEllipse(p, 3.5f, 3.5f, 17, 17);
                    g.DrawLine(p, 12, 11, 12, 16.5f);
                    g.FillEllipse(f, 10.8f, 6.6f, 2.4f, 2.4f);
                    break;
                case Ico.Slideshow:
                    RR(g, p, 3, 4.5f, 18, 12.5f, 2);
                    g.FillPolygon(f, new[] { new PointF(10, 7.8f), new PointF(15, 10.8f), new PointF(10, 13.8f) });
                    g.DrawLine(p, 8, 20.5f, 16, 20.5f);
                    g.DrawLine(p, 12, 17, 12, 20.5f);
                    break;
                case Ico.Curves:
                    g.DrawLines(p, new[] { new PointF(3.5f, 3.5f), new PointF(3.5f, 20.5f), new PointF(20.5f, 20.5f) });
                    g.DrawBezier(p, 4.5f, 19.5f, 11, 19, 12, 6, 19.5f, 5);
                    break;
                case Ico.Window:
                    RR(g, p, 3, 4.5f, 18, 15, 2.5f);
                    g.DrawLine(p, 3, 9, 21, 9);
                    g.FillEllipse(f, 5.3f, 6, 1.7f, 1.7f);
                    g.FillEllipse(f, 8.1f, 6, 1.7f, 1.7f);
                    break;
            }
        }
    }
}
