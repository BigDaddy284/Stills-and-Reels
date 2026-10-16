using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>
    /// Multitrack timeline: ruler, track headers, clips with thumbnails and waveforms, playhead.
    /// Clips can be selected, moved (also between tracks), trimmed at either end, and snapped.
    /// All changes go through the editor so they can be undone.
    /// </summary>
    internal sealed class TimelineControl : Control
    {
        readonly VideoEditorView ed;

        double pps = 60;            // pixels per second
        double scrollT;             // seconds at the left edge of the clip area
        int scrollY;

        sealed class Row { public Track Track; public int Top, Height; public bool NewVideo, NewAudio; }
        readonly List<Row> rows = new List<Row>();

        enum Drag { None, Playhead, Move, TrimLeft, TrimRight, HScroll, Pending }
        Drag drag;
        Point downAt;
        Clip dragClip;
        double dragOffsetT;
        double origStart, origDuration, origIn;
        List<Keyframe> origKeys;
        Row ghostRow;
        double ghostStart;
        double snapLine = double.NaN;
        double scrollGrab;
        Point lastMouse;
        string dropMedia;          // "id" while a bin item is dragged over
        double dropT;
        Row dropRow;
        readonly Timer autoScroll = new Timer { Interval = 30 };

        public event EventHandler ViewChanged; // zoom or scroll changed (toolbar zoom slider follows)

        public TimelineControl(VideoEditorView editor)
        {
            ed = editor;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AllowDrop = true;
            autoScroll.Tick += (s, e) => AutoScrollTick();
            Font = Theme.UiFont(8.5f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) autoScroll.Dispose();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ geometry

        int HeaderW { get { return Theme.S(168); } }
        int RulerH { get { return Theme.S(30); } }
        int ScrollBarH { get { return Theme.S(12); } }
        int ClipLeft { get { return HeaderW; } }
        int ClipWidth { get { return Math.Max(10, Width - HeaderW); } }

        public double PixelsPerSecond { get { return pps; } }
        public double MinPps { get { return Math.Max(0.5, ClipWidth / (Math.Max(10, ed.Project.Duration) * 1.15)); } }
        public const double MaxPps = 800;

        double X2T(int x) { return scrollT + (x - ClipLeft) / pps; }
        float T2X(double t) { return (float)(ClipLeft + (t - scrollT) * pps); }

        int RowHeight(Track t)
        {
            if (t.Kind == TrackKind.Audio) return Theme.S(48);
            return ReferenceEquals(t, ed.Project.Main) ? Theme.S(72) : Theme.S(56);
        }

        void BuildRows()
        {
            rows.Clear();
            var p = ed.Project;
            int y = RulerH + Theme.S(8) - scrollY;
            int gap = Theme.S(5);
            var videos = p.Tracks.FindAll(t => t.Kind == TrackKind.Video);
            var audios = p.Tracks.FindAll(t => t.Kind == TrackKind.Audio);
            int newH = Theme.S(26);
            rows.Add(new Row { NewVideo = true, Top = y, Height = newH });
            y += newH + gap;
            for (int i = videos.Count - 1; i >= 0; i--)
            {
                var r = new Row { Track = videos[i], Top = y, Height = RowHeight(videos[i]) };
                rows.Add(r);
                y += r.Height + gap;
            }
            foreach (var a in audios)
            {
                var r = new Row { Track = a, Top = y, Height = RowHeight(a) };
                rows.Add(r);
                y += r.Height + gap;
            }
            rows.Add(new Row { NewAudio = true, Top = y, Height = newH });
            y += newH + gap;
            contentHeight = y + scrollY - RulerH;
        }

        int contentHeight;

        Row RowAt(int y)
        {
            foreach (var r in rows) if (y >= r.Top - Theme.S(3) && y < r.Top + r.Height + Theme.S(3)) return r;
            return null;
        }

        Row RowOf(Track t) { return rows.Find(r => ReferenceEquals(r.Track, t)); }

        RectangleF ClipRect(Clip c, Row r)
        {
            float x0 = T2X(c.Start), x1 = T2X(c.End);
            return new RectangleF(x0, r.Top, Math.Max(2, x1 - x0), r.Height);
        }

        // ------------------------------------------------------------------ public view control

        public void SetZoom(double newPps, double anchorT)
        {
            newPps = Math.Max(MinPps, Math.Min(MaxPps, newPps));
            double anchorX = (anchorT - scrollT) * pps;
            pps = newPps;
            scrollT = Math.Max(0, anchorT - anchorX / pps);
            ClampScroll();
            Invalidate();
            RaiseView();
        }

        public void ZoomBy(double factor) { SetZoom(pps * factor, ed.Playhead); }

        public void FitAll()
        {
            pps = Math.Max(0.5, Math.Min(MaxPps, ClipWidth / (Math.Max(5, ed.Project.Duration) * 1.08)));
            scrollT = 0;
            Invalidate();
            RaiseView();
        }

        /// <summary>Zoom position as 0..1 on a log scale (for the toolbar slider).</summary>
        public double ZoomFraction
        {
            get
            {
                double lo = Math.Log(MinPps), hi = Math.Log(MaxPps);
                return hi <= lo ? 0 : (Math.Log(pps) - lo) / (hi - lo);
            }
            set
            {
                double lo = Math.Log(MinPps), hi = Math.Log(MaxPps);
                SetZoom(Math.Exp(lo + (hi - lo) * Math.Max(0, Math.Min(1, value))), ed.Playhead);
            }
        }

        void RaiseView() { var h = ViewChanged; if (h != null) h(this, EventArgs.Empty); }

        void ClampScroll()
        {
            double maxT = Math.Max(0, ed.Project.Duration + 30 - ClipWidth / pps);
            scrollT = Math.Max(0, Math.Min(maxT, scrollT));
            BuildRows();
            int maxY = Math.Max(0, contentHeight - (Height - RulerH - ScrollBarH));
            scrollY = Math.Max(0, Math.Min(maxY, scrollY));
        }

        /// <summary>Keeps the playhead visible while playing.</summary>
        public void Follow(double t)
        {
            float x = T2X(t);
            if (x > ClipLeft + ClipWidth * 0.92 || x < ClipLeft)
            {
                scrollT = Math.Max(0, t - ClipWidth * 0.08 / pps);
                ClampScroll();
                RaiseView();
            }
            Invalidate();
        }

        public void EnsureVisible(double t)
        {
            float x = T2X(t);
            if (x < ClipLeft || x > ClipLeft + ClipWidth - Theme.S(20)) { scrollT = Math.Max(0, t - ClipWidth * 0.3 / pps); ClampScroll(); RaiseView(); }
            Invalidate();
        }

        // ------------------------------------------------------------------ snapping

        double Snap(double t, Clip ignore, bool includePlayhead = true)
        {
            snapLine = double.NaN;
            if (!ed.Snapping) return t;
            double tol = Theme.S(9) / pps;
            double best = t, bestD = tol;
            Action<double> consider = c => { double d = Math.Abs(c - t); if (d < bestD) { bestD = d; best = c; } };
            consider(0);
            if (includePlayhead) consider(ed.Playhead);
            foreach (var c in ed.Project.AllClips())
            {
                if (ReferenceEquals(c, ignore)) continue;
                consider(c.Start);
                consider(c.End);
            }
            if (Math.Abs(best - t) > 1e-9 || bestD < tol) snapLine = best;
            return best;
        }

        // ------------------------------------------------------------------ hit testing

        enum Edge { None, Left, Right, Body }

        Clip ClipAtPoint(Point p, out Edge edge, out Row row)
        {
            edge = Edge.None;
            row = RowAt(p.Y);
            if (row == null || row.Track == null || p.X < ClipLeft) return null;
            if (p.Y < row.Top || p.Y >= row.Top + row.Height) return null;
            Clip hit = null;
            foreach (var c in row.Track.Clips)
            {
                var r = ClipRect(c, row);
                if (p.X >= r.Left - 3 && p.X <= r.Right + 3) { hit = c; if (p.X >= r.Left && p.X <= r.Right) break; }
            }
            if (hit == null) return null;
            var cr = ClipRect(hit, row);
            float tol = Math.Min(Theme.F(7), cr.Width / 3);
            if (Math.Abs(p.X - cr.Left) <= tol) edge = Edge.Left;
            else if (Math.Abs(p.X - cr.Right) <= tol) edge = Edge.Right;
            else edge = Edge.Body;
            return hit;
        }

        /// <summary>Header buttons: 0 lock, 1 hide (video only), 2 mute. Audio rows have no hide button.</summary>
        Rectangle HeaderButton(Row r, int i)
        {
            int s = Theme.S(23);
            int slot = i;
            if (r.Track != null && r.Track.Kind == TrackKind.Audio && i == 0) slot = 1;
            int x = HeaderW - Theme.S(10) - (3 - slot) * (s + Theme.S(2));
            return new Rectangle(x, r.Top + (r.Height - s) / 2, s, s);
        }

        Rectangle ScrollTrack { get { return new Rectangle(ClipLeft, Height - ScrollBarH, ClipWidth, ScrollBarH); } }

        RectangleF ScrollThumb
        {
            get
            {
                var tr = ScrollTrack;
                double total = Math.Max(ed.Project.Duration + 30, ClipWidth / pps);
                double visible = ClipWidth / pps;
                float w = (float)Math.Max(Theme.S(30), tr.Width * visible / total);
                float x = (float)(tr.Left + (tr.Width - w) * (scrollT / Math.Max(1e-6, total - visible)));
                return new RectangleF(x, tr.Top + Theme.S(3), w, ScrollBarH - Theme.S(6));
            }
        }

        // ------------------------------------------------------------------ mouse

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            BuildRows();
            downAt = e.Location;
            lastMouse = e.Location;
            if (e.Y >= Height - ScrollBarH && e.X >= ClipLeft)
            {
                var th = ScrollThumb;
                if (th.Contains(e.Location)) { drag = Drag.HScroll; scrollGrab = e.X - th.Left; Capture = true; }
                else
                {
                    scrollT += (e.X < th.Left ? -1 : 1) * ClipWidth * 0.8 / pps;
                    ClampScroll(); Invalidate(); RaiseView();
                }
                return;
            }
            if (e.Y < RulerH && e.X >= ClipLeft)
            {
                if (e.Button != MouseButtons.Left) return;
                drag = Drag.Playhead;
                Capture = true;
                ed.Scrub(Math.Max(0, ed.Project.Snap(X2T(e.X))));
                return;
            }
            // track header buttons
            if (e.X < HeaderW)
            {
                var r = RowAt(e.Y);
                if (r != null && r.Track != null)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        if (i == 1 && r.Track.Kind == TrackKind.Audio) continue;
                        if (!HeaderButton(r, i).Contains(e.Location)) continue;
                        ed.ToggleTrack(r.Track, i == 0 ? "lock" : i == 1 ? (r.Track.Kind == TrackKind.Video ? "hide" : "none") : "mute");
                        return;
                    }
                }
                return;
            }
            Edge edge; Row row;
            var c = ClipAtPoint(e.Location, out edge, out row);
            if (c == null)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ed.ClearSelection();
                    drag = Drag.Playhead;
                    Capture = true;
                    ed.Scrub(Math.Max(0, ed.Project.Snap(X2T(e.X))));
                }
                else if (e.Button == MouseButtons.Right) ed.ShowTimelineMenu(PointToScreen(e.Location), X2T(e.X));
                return;
            }
            bool shift = (ModifierKeys & (Keys.Shift | Keys.Control)) != 0;
            if (e.Button == MouseButtons.Right)
            {
                if (!ed.IsSelected(c)) ed.Select(c, false);
                ed.ShowClipMenu(c, PointToScreen(e.Location));
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (shift) { ed.Select(c, true); return; }
            if (!ed.IsSelected(c) || ed.SelectionCount > 1) ed.Select(c, false);
            if (row.Track.Locked) return;
            dragClip = c;
            origStart = c.Start; origDuration = c.Duration; origIn = c.In;
            origKeys = new List<Keyframe>();
            foreach (var k in c.Keys) origKeys.Add(k.Clone());
            dragOffsetT = X2T(e.X) - c.Start;
            if (edge == Edge.Left || edge == Edge.Right)
            {
                drag = edge == Edge.Left ? Drag.TrimLeft : Drag.TrimRight;
                trimStarted = false;
                Capture = true;
            }
            else
            {
                drag = Drag.Pending;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            lastMouse = e.Location;
            switch (drag)
            {
                case Drag.None:
                    UpdateCursor(e.Location);
                    return;
                case Drag.HScroll:
                    {
                        var tr = ScrollTrack;
                        var th = ScrollThumb;
                        double total = Math.Max(ed.Project.Duration + 30, ClipWidth / pps);
                        double visible = ClipWidth / pps;
                        double f = (e.X - scrollGrab - tr.Left) / Math.Max(1.0, tr.Width - th.Width);
                        scrollT = Math.Max(0, f * (total - visible));
                        ClampScroll(); Invalidate(); RaiseView();
                        return;
                    }
                case Drag.Playhead:
                    {
                        double t = Math.Max(0, X2T(Math.Max(ClipLeft, e.X)));
                        if (ed.Snapping)
                        {
                            double s = Snap(t, null, false);
                            t = s;
                        }
                        ed.Scrub(ed.Project.Snap(t));
                        CheckAutoScroll();
                        return;
                    }
                case Drag.Pending:
                    if (Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) < Theme.S(5)) return;
                    drag = Drag.Move;
                    Cursor = Cursors.SizeAll;
                    goto case Drag.Move;
                case Drag.Move:
                    {
                        BuildRows();
                        double raw = Math.Max(0, X2T(e.X) - dragOffsetT);
                        double dur = dragClip.Duration;
                        double sStart = Snap(raw, dragClip); double lineA = snapLine;
                        double sEnd = Snap(raw + dur, dragClip); double lineB = snapLine;
                        double start;
                        if (!double.IsNaN(lineB) && (double.IsNaN(lineA) || Math.Abs(sEnd - raw - dur) < Math.Abs(sStart - raw))) { start = sEnd - dur; snapLine = lineB; }
                        else { start = sStart; snapLine = lineA; }
                        ghostStart = Math.Max(0, ed.Project.Snap(start));
                        ghostRow = TargetRow(e.Y, dragClip);
                        CheckAutoScroll();
                        Invalidate();
                        return;
                    }
                case Drag.TrimLeft:
                case Drag.TrimRight:
                    {
                        TrimTo(X2T(e.X));
                        CheckAutoScroll();
                        return;
                    }
            }
        }

        Row TargetRow(int y, Clip c)
        {
            var r = RowAt(y);
            bool audio = c.Kind == ClipKind.Audio;
            if (r == null)
            {
                // above everything → new video track; below everything → new audio track
                if (rows.Count > 0 && y < rows[0].Top) return audio ? rows[rows.Count - 1] : rows[0];
                return audio ? rows[rows.Count - 1] : RowOf(ed.Project.TrackOf(c));
            }
            if (r.NewVideo) return audio ? RowOf(ed.Project.TrackOf(c)) : r;
            if (r.NewAudio) return audio ? r : RowOf(ed.Project.TrackOf(c));
            if ((r.Track.Kind == TrackKind.Audio) != audio || r.Track.Locked) return RowOf(ed.Project.TrackOf(c));
            return r;
        }

        bool trimStarted;

        void TrimTo(double t)
        {
            var c = dragClip;
            if (!trimStarted)
            {
                if (Math.Abs(lastMouse.X - downAt.X) < Theme.S(3)) return;
                trimStarted = true;
                ed.BeginChange(null);
            }
            var track = ed.Project.TrackOf(c);
            if (track == null) return;
            bool magnetic = ed.MainMagnet && ReferenceEquals(track, ed.Project.Main);
            double minDur = Math.Max(ed.Project.FrameDuration, 0.1);
            bool hasSource = c.Kind == ClipKind.Video || c.Kind == ClipKind.Audio;
            var m = hasSource ? ed.Library.Find(c.MediaId) : null;
            double srcDur = m != null ? m.Duration : 0;
            double prevEnd = 0, nextStart = double.MaxValue;
            foreach (var o in track.Clips)
            {
                if (ReferenceEquals(o, c)) continue;
                if (o.End <= origStart + 1e-6) prevEnd = Math.Max(prevEnd, o.End);
                if (o.Start >= origStart + origDuration - 1e-6) nextStart = Math.Min(nextStart, o.Start);
            }
            if (drag == Drag.TrimLeft)
            {
                double end = origStart + origDuration;
                double s = Snap(t, c);
                // how far the start can move left: the previous clip (unless the main track closes gaps) and the start of the source
                double min = magnetic ? origStart - 3600 : prevEnd;
                if (hasSource) min = Math.Max(min, origStart - origIn / c.Speed);
                if (!magnetic) min = Math.Max(0, min);
                s = Math.Max(min, Math.Min(end - minDur, s));
                s = ed.Project.Snap(s);
                double delta = s - origStart;
                c.Start = origStart + delta;
                c.Duration = origDuration - delta;
                c.In = Math.Max(0, origIn + delta * c.Speed);
                c.Keys.Clear();
                foreach (var k in origKeys) { var nk = k.Clone(); nk.T -= delta; c.Keys.Add(nk); }
            }
            else
            {
                double e = Snap(t, c);
                double max = magnetic ? double.MaxValue : nextStart;
                if (hasSource && srcDur > 0) max = Math.Min(max, origStart + (srcDur - origIn) / c.Speed);
                e = Math.Max(origStart + minDur, Math.Min(max, e));
                e = ed.Project.Snap(e);
                c.Duration = e - origStart;
            }
            ed.LiveChanged();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var d = drag;
            drag = Drag.None;
            Capture = false;
            autoScroll.Stop();
            snapLine = double.NaN;
            if (d == Drag.Playhead) ed.EndScrub();
            else if (d == Drag.Move && dragClip != null && ghostRow != null)
            {
                Track target = ghostRow.Track;
                ed.MoveClip(dragClip, target, ghostRow.NewVideo || ghostRow.NewAudio, ghostStart);
            }
            else if ((d == Drag.TrimLeft || d == Drag.TrimRight) && trimStarted) ed.EndChange();
            dragClip = null;
            ghostRow = null;
            UpdateCursor(e.Location);
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            Edge edge; Row row;
            var c = ClipAtPoint(e.Location, out edge, out row);
            if (c != null) ed.Scrub(Math.Max(c.Start, Math.Min(c.End - 0.01, X2T(e.X))), true);
        }

        void UpdateCursor(Point p)
        {
            if (p.Y < RulerH || p.X < ClipLeft) { Cursor = p.Y < RulerH && p.X >= ClipLeft ? Cursors.Hand : Cursors.Default; return; }
            Edge edge; Row row;
            var c = ClipAtPoint(p, out edge, out row);
            if (c != null && row.Track.Locked) { Cursor = Cursors.No; return; }
            Cursor = c == null ? Cursors.Default : edge == Edge.Body ? Cursors.Hand : Cursors.SizeWE;
            string tip = null;
            if (c != null) tip = ed.ClipTitle(c) + "  ·  " + VideoEditorView.Tc(c.Start) + " – " + VideoEditorView.Tc(c.End) + (edge == Edge.Body ? "" : "\nDrag to trim");
            if (Ui.Tip.GetToolTip(this) != tip) Ui.Tip.SetToolTip(this, tip);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Ui.Tip.SetToolTip(this, null); }

        void CheckAutoScroll()
        {
            if (lastMouse.X > Width - Theme.S(30) || (lastMouse.X < ClipLeft + Theme.S(20) && scrollT > 0)) autoScroll.Start();
            else autoScroll.Stop();
        }

        void AutoScrollTick()
        {
            if (drag == Drag.None || drag == Drag.HScroll) { autoScroll.Stop(); return; }
            double step = 12 / pps;
            if (lastMouse.X > Width - Theme.S(30)) scrollT += step;
            else if (lastMouse.X < ClipLeft + Theme.S(20)) scrollT = Math.Max(0, scrollT - step);
            ClampScroll();
            RaiseView();
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, lastMouse.X, lastMouse.Y, 0));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var mods = ModifierKeys;
            if ((mods & Keys.Control) != 0)
            {
                double anchor = e.X >= ClipLeft ? X2T(e.X) : ed.Playhead;
                SetZoom(pps * (e.Delta > 0 ? 1.25 : 0.8), anchor);
                return;
            }
            if ((mods & Keys.Shift) != 0 || e.X < HeaderW)
            {
                scrollY -= Math.Sign(e.Delta) * Theme.S(40);
                ClampScroll();
                Invalidate();
                return;
            }
            scrollT -= Math.Sign(e.Delta) * ClipWidth * 0.12 / pps;
            ClampScroll();
            Invalidate();
            RaiseView();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (pps < MinPps) pps = MinPps;
            ClampScroll();
            Invalidate();
        }

        // ------------------------------------------------------------------ drag & drop from the media bin

        public const string DragFormat = "StillsAndReelsMedia";

        protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); OnDragOver(e); }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            if (e.Data.GetDataPresent(DragFormat))
            {
                e.Effect = DragDropEffects.Copy;
                BuildRows();
                var pt = PointToClient(new Point(e.X, e.Y));
                dropMedia = e.Data.GetData(DragFormat) as string;
                int id;
                int.TryParse(dropMedia, out id);
                var m = ed.Library.Find(id);
                bool audio = m != null && m.Kind == MediaKind.Audio;
                var r = RowAt(pt.Y);
                if (r == null) r = audio ? rows[rows.Count - 1] : rows[0];
                if (r.Track != null && (r.Track.Kind == TrackKind.Audio) != audio) r = audio ? rows[rows.Count - 1] : rows[0];
                if (r.NewAudio && !audio) r = rows[0];
                if (r.NewVideo && audio) r = rows[rows.Count - 1];
                dropRow = r;
                double t = Math.Max(0, X2T(pt.X));
                dropT = ed.Project.Snap(Snap(t, null));
                lastMouse = pt;
                Invalidate();
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); dropMedia = null; snapLine = double.NaN; Invalidate(); }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            if (e.Data.GetDataPresent(DragFormat) && dropRow != null)
            {
                int id;
                if (int.TryParse(e.Data.GetData(DragFormat) as string, out id))
                    ed.DropMedia(id, dropRow.Track, dropRow.NewAudio || dropRow.NewVideo, dropT);
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) ed.ImportFiles(files, true);
            }
            dropMedia = null;
            snapLine = double.NaN;
            Invalidate();
        }

        // ------------------------------------------------------------------ painting

        static string TickLabel(double t, double step)
        {
            int s = (int)Math.Floor(t + 1e-6);
            int m = s / 60, sec = s % 60;
            if (step < 1)
            {
                double frac = t - Math.Floor(t + 1e-6);
                if (frac > 0.001) return "+" + frac.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture).TrimStart('0');
            }
            return m.ToString("00") + ":" + sec.ToString("00");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            BuildRows();
            g.Clear(p.Dark ? Color.FromArgb(11, 14, 28) : Color.FromArgb(240, 243, 250));
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // track backgrounds
            var clipArea = new Rectangle(ClipLeft, RulerH, ClipWidth, Height - RulerH - ScrollBarH);
            var st0 = g.Save();
            g.SetClip(new Rectangle(0, RulerH, Width, Height - RulerH - ScrollBarH));
            foreach (var r in rows)
            {
                var rr = new Rectangle(ClipLeft, r.Top, ClipWidth, r.Height);
                if (r.Track == null)
                {
                    bool active = (drag == Drag.Move && ghostRow == r) || (dropMedia != null && dropRow == r);
                    if (active)
                        using (var path = Theme.Rounded(new RectangleF(rr.X + 4, rr.Y, rr.Width - 8, rr.Height), Theme.F(6)))
                        using (var pen = new Pen(p.AccentA, Theme.F(1.2f)) { DashStyle = DashStyle.Dash })
                        {
                            g.DrawPath(pen, path);
                            TextRenderer.DrawText(g, r.NewVideo ? "New track" : "New audio track", Font, Rectangle.Round(rr), p.SubText,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                        }
                    continue;
                }
                bool main = ReferenceEquals(r.Track, ed.Project.Main);
                using (var b = new SolidBrush(main ? (p.Dark ? Color.FromArgb(18, 23, 44) : Color.FromArgb(228, 233, 246)) : (p.Dark ? Color.FromArgb(15, 19, 36) : Color.FromArgb(234, 238, 248))))
                    g.FillRectangle(b, rr);
                if (main && r.Track.Clips.Count == 0)
                    TextRenderer.DrawText(g, "Drag videos and pictures here – or double-click them in the media panel", Theme.UiFont(9.5f), rr, p.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // grid lines from the ruler
            double step, major;
            TickSteps(out step, out major);

            // clips
            g.SetClip(clipArea);
            foreach (var r in rows)
            {
                if (r.Track == null) continue;
                foreach (var c in r.Track.Clips)
                {
                    var cr = ClipRect(c, r);
                    if (cr.Right < ClipLeft || cr.Left > Width) continue;
                    bool ghosting = drag == Drag.Move && ReferenceEquals(c, dragClip);
                    DrawClip(g, c, cr, r.Track, ghosting ? 0.35f : 1f);
                }
            }
            // transition badges on top of clip joins
            foreach (var r in rows)
            {
                if (r.Track == null) continue;
                foreach (var c in r.Track.Clips)
                {
                    if (string.IsNullOrEmpty(c.Transition)) continue;
                    float x = T2X(c.Start);
                    float s = Theme.F(20);
                    var br = new RectangleF(x - s / 2, r.Top + r.Height / 2f - s / 2, s, s);
                    using (var path = Theme.Rounded(br, Theme.F(5)))
                    {
                        using (var b = new SolidBrush(p.Dark ? Color.FromArgb(235, 26, 32, 60) : Color.FromArgb(240, 255, 255, 255))) g.FillPath(b, path);
                        using (var b = Theme.AccentGradient(br)) using (var pen = new Pen(b, Theme.F(1.3f))) g.DrawPath(pen, path);
                    }
                    Icons.Draw(g, Ico.Transition, RectangleF.Inflate(br, -Theme.F(3), -Theme.F(3)), p.Text, 1.6f);
                }
            }
            // ghost of a moving clip
            if (drag == Drag.Move && dragClip != null && ghostRow != null)
            {
                var gr = new RectangleF(T2X(ghostStart), ghostRow.Top, (float)(dragClip.Duration * pps), ghostRow.Height);
                using (var path = Theme.Rounded(gr, Theme.F(6)))
                {
                    using (var b = new SolidBrush(Color.FromArgb(70, p.AccentA))) g.FillPath(b, path);
                    using (var b = Theme.AccentGradient(gr)) using (var pen = new Pen(b, Theme.F(2))) g.DrawPath(pen, path);
                }
                TextRenderer.DrawText(g, VideoEditorView.Tc(ghostStart), Font, new Rectangle((int)gr.Left + 6, (int)gr.Top + 4, 200, 16), p.Text, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            }
            if (dropMedia != null && dropRow != null)
            {
                int id; int.TryParse(dropMedia, out id);
                var m = ed.Library.Find(id);
                double len = m == null ? 5 : m.Kind == MediaKind.Image ? 5 : Math.Max(0.5, m.Duration);
                var gr = new RectangleF(T2X(dropT), dropRow.Top, (float)(len * pps), dropRow.Height);
                using (var path = Theme.Rounded(gr, Theme.F(6)))
                {
                    using (var b = new SolidBrush(Color.FromArgb(60, p.AccentB))) g.FillPath(b, path);
                    using (var b = Theme.AccentGradient(gr)) using (var pen = new Pen(b, Theme.F(2)) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
                }
            }
            if (!double.IsNaN(snapLine))
            {
                float x = T2X(snapLine);
                using (var pen = new Pen(Color.FromArgb(230, 255, 200, 60), Theme.F(1.2f))) g.DrawLine(pen, x, RulerH, x, Height - ScrollBarH);
            }
            g.Restore(st0);

            // headers
            using (var b = new SolidBrush(p.Bar)) g.FillRectangle(b, 0, RulerH, HeaderW, Height - RulerH);
            using (var pen = new Pen(p.BarBorder)) g.DrawLine(pen, HeaderW - 1, RulerH, HeaderW - 1, Height);
            var st1 = g.Save();
            g.SetClip(new Rectangle(0, RulerH, HeaderW, Height - RulerH - ScrollBarH));
            int vNum = 0, aNum = 0;
            foreach (var r in rows) if (r.Track != null && r.Track.Kind == TrackKind.Video) vNum++;
            int vIdx = vNum;
            foreach (var r in rows)
            {
                if (r.Track == null) continue;
                bool video = r.Track.Kind == TrackKind.Video;
                bool main = ReferenceEquals(r.Track, ed.Project.Main);
                string name = video ? (main ? "Main" : "Track " + (vIdx)) : "Audio " + (++aNum);
                if (video) vIdx--;
                var ir = new RectangleF(Theme.F(10), r.Top + r.Height / 2f - Theme.F(9), Theme.F(18), Theme.F(18));
                Icons.Draw(g, video ? (main ? Ico.Video : Ico.Layers) : Ico.Music, ir, p.SubText, 1.6f);
                TextRenderer.DrawText(g, name, Theme.UiFont(8.5f), new Rectangle(Theme.S(32), r.Top, Math.Max(10, HeaderButton(r, 0).Left - Theme.S(34)), r.Height), p.SubText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                DrawHeaderButton(g, HeaderButton(r, 0), r.Track.Locked ? Ico.Lock : Ico.Unlock, r.Track.Locked);
                if (video) DrawHeaderButton(g, HeaderButton(r, 1), r.Track.Hidden ? Ico.EyeOff : Ico.Eye, r.Track.Hidden);
                DrawHeaderButton(g, HeaderButton(r, 2), r.Track.Muted ? Ico.Mute : Ico.Volume, r.Track.Muted);
            }
            g.Restore(st1);

            // ruler
            using (var b = new SolidBrush(p.Bar)) g.FillRectangle(b, 0, 0, Width, RulerH);
            using (var pen = new Pen(p.BarBorder)) g.DrawLine(pen, 0, RulerH - 1, Width, RulerH - 1);
            var st2 = g.Save();
            g.SetClip(new Rectangle(ClipLeft, 0, ClipWidth, RulerH));
            double t0 = Math.Floor(scrollT / step) * step;
            using (var pen = new Pen(p.Muted))
            using (var penMajor = new Pen(p.SubText))
            {
                for (double t = t0; T2X(t) < Width; t += step)
                {
                    float x = T2X(t);
                    bool isMajor = Math.Abs(t / major - Math.Round(t / major)) < 1e-6;
                    if (isMajor)
                    {
                        g.DrawLine(penMajor, x, RulerH - Theme.S(10), x, RulerH - 1);
                        TextRenderer.DrawText(g, TickLabel(t, major), Font, new Point((int)x + 3, Theme.S(3)), p.SubText, TextFormatFlags.NoPrefix);
                    }
                    else g.DrawLine(pen, x, RulerH - Theme.S(5), x, RulerH - 1);
                }
            }
            g.Restore(st2);
            TextRenderer.DrawText(g, VideoEditorView.Tc(ed.Playhead), Theme.UiFont(9f, FontStyle.Bold), new Rectangle(Theme.S(10), 0, HeaderW - Theme.S(12), RulerH), p.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // playhead
            float px = T2X(ed.Playhead);
            if (px >= ClipLeft - 1 && px <= Width)
            {
                var st3 = g.Save();
                g.SetClip(new Rectangle(ClipLeft - Theme.S(8), 0, ClipWidth + Theme.S(8), Height - ScrollBarH));
                using (var pen = new Pen(Color.White, Theme.F(1.5f))) g.DrawLine(pen, px, RulerH - Theme.S(4), px, Height - ScrollBarH);
                float hw = Theme.F(7);
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[] { new PointF(px - hw, Theme.F(4)), new PointF(px + hw, Theme.F(4)), new PointF(px + hw, RulerH - Theme.F(12)), new PointF(px, RulerH - Theme.F(4)), new PointF(px - hw, RulerH - Theme.F(12)) });
                    using (var b = Theme.AccentGradient(path.GetBounds())) g.FillPath(b, path);
                    using (var pen = new Pen(Color.White, Theme.F(1))) g.DrawPath(pen, path);
                }
                g.Restore(st3);
            }

            // horizontal scroll bar
            var str = ScrollTrack;
            using (var b = new SolidBrush(p.Bar)) g.FillRectangle(b, 0, str.Top, Width, str.Height);
            var th = ScrollThumb;
            using (var path = Theme.Rounded(th, th.Height / 2))
            using (var b = new SolidBrush(drag == Drag.HScroll ? p.SubText : p.ControlBorder)) g.FillPath(b, path);
        }

        void TickSteps(out double step, out double major)
        {
            double[] steps = { 1.0 / 30, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            step = steps[steps.Length - 1];
            foreach (var s in steps) if (s * pps >= Theme.S(9)) { step = s; break; }
            major = step;
            foreach (var s in steps) if (s >= step && s * pps >= Theme.S(70) && IsMultiple(s, step)) { major = s; break; }
            if (major * pps < Theme.S(70)) major = step * Math.Ceiling(Theme.S(70) / (step * pps));
        }

        static bool IsMultiple(double a, double b) { double k = a / b; return Math.Abs(k - Math.Round(k)) < 1e-6; }

        void DrawHeaderButton(Graphics g, Rectangle r, Ico icon, bool on)
        {
            var p = Theme.Current;
            if (on)
                using (var path = Theme.Rounded(r, Theme.F(5)))
                using (var b = new SolidBrush(Color.FromArgb(p.Dark ? 60 : 40, p.AccentA))) g.FillPath(b, path);
            Icons.Draw(g, icon, RectangleF.Inflate(r, -Theme.F(4.5f), -Theme.F(4.5f)), on ? p.Text : p.Muted, 1.6f);
        }

        void DrawClip(Graphics g, Clip c, RectangleF r, Track track, float alpha)
        {
            var p = Theme.Current;
            bool sel = ed.IsSelected(c);
            var m = c.Kind == ClipKind.Text ? null : ed.Library.Find(c.MediaId);
            bool offline = c.Kind != ClipKind.Text && (m == null || !m.IsUsable);
            Color a, b;
            switch (c.Kind)
            {
                case ClipKind.Text: a = Color.FromArgb(118, 64, 210); b = Color.FromArgb(168, 74, 226); break;
                case ClipKind.Audio: a = Color.FromArgb(18, 112, 96); b = Color.FromArgb(24, 140, 118); break;
                case ClipKind.Image: a = Color.FromArgb(32, 96, 150); b = Color.FromArgb(40, 120, 170); break;
                default: a = Color.FromArgb(34, 66, 150); b = Color.FromArgb(70, 52, 150); break;
            }
            if (offline) { a = Color.FromArgb(120, 36, 48); b = Color.FromArgb(150, 40, 60); }
            int al = (int)(255 * alpha);
            var inner = new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            using (var path = Theme.Rounded(inner, Theme.F(6)))
            {
                using (var br = Theme.Gradient(inner, Color.FromArgb(al, a), Color.FromArgb(al, b), 0f)) g.FillPath(br, path);
                var st = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                if (alpha > 0.5f && !offline)
                {
                    if (c.Kind == ClipKind.Video || c.Kind == ClipKind.Image) DrawThumbs(g, c, m, inner);
                    if ((c.Kind == ClipKind.Audio || (c.Kind == ClipKind.Video && m != null && m.HasAudio)) && m != null && m.Peaks != null)
                    {
                        var wr = c.Kind == ClipKind.Audio ? inner : new RectangleF(inner.X, inner.Bottom - inner.Height * 0.32f, inner.Width, inner.Height * 0.32f);
                        if (c.Kind == ClipKind.Video) using (var sh = new SolidBrush(Color.FromArgb(150, 10, 40, 40))) g.FillRectangle(sh, wr);
                        DrawWave(g, c, m, wr, c.Muted || track.Muted ? Color.FromArgb(120, 160, 170, 180) : Color.FromArgb(220, 120, 240, 200));
                    }
                    if (c.Kind == ClipKind.Text)
                    {
                        string t = string.IsNullOrWhiteSpace(c.Text) ? "Text" : c.Text.Replace("\r", " ").Replace("\n", " · ");
                        TextRenderer.DrawText(g, t, Theme.UiFont(9.5f, FontStyle.Bold), Rectangle.Round(new RectangleF(inner.X + 22, inner.Y, inner.Width - 26, inner.Height)), Color.White,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                        Icons.Draw(g, Ico.Text, new RectangleF(inner.X + 4, inner.Y + inner.Height / 2 - 8, 16, 16), Color.White, 1.8f);
                    }
                }
                if (offline)
                {
                    using (var hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(60, 255, 255, 255), Color.Transparent)) g.FillRectangle(hb, inner);
                    TextRenderer.DrawText(g, m == null ? "Media missing" : m.Status == MediaStatus.Probing ? "Loading…" : "Media offline", Font, Rectangle.Round(inner), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                // fades
                if (c.FadeIn > 0.01 || c.AudioFadeIn > 0.01)
                {
                    float w = (float)(Math.Max(c.FadeIn, c.AudioFadeIn) * pps);
                    using (var fb = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillPolygon(fb, new[] { new PointF(inner.X, inner.Y), new PointF(inner.X + w, inner.Y), new PointF(inner.X, inner.Bottom) });
                }
                if (c.FadeOut > 0.01 || c.AudioFadeOut > 0.01)
                {
                    float w = (float)(Math.Max(c.FadeOut, c.AudioFadeOut) * pps);
                    using (var fb = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillPolygon(fb, new[] { new PointF(inner.Right, inner.Y), new PointF(inner.Right - w, inner.Y), new PointF(inner.Right, inner.Bottom) });
                }
                g.Restore(st);

                // label band
                if (c.Kind != ClipKind.Text && inner.Width > 30)
                {
                    string label = ed.ClipTitle(c);
                    if (Math.Abs(c.Speed - 1) > 0.001) label = c.Speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×  " + label;
                    var lr = new Rectangle((int)inner.X + 5, (int)inner.Y + 2, (int)inner.Width - 10, Theme.S(16));
                    var ts = TextRenderer.MeasureText(label, Font);
                    var bg = new RectangleF(lr.X - 2, lr.Y, Math.Min(ts.Width + 4, lr.Width + 4), lr.Height);
                    using (var bgp = Theme.Rounded(bg, Theme.F(3)))
                    using (var bb = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillPath(bb, bgp);
                    TextRenderer.DrawText(g, label, Font, lr, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                }
                if (c.Muted && c.Kind != ClipKind.Text && c.Kind != ClipKind.Image && inner.Width > 40)
                    Icons.Draw(g, Ico.Mute, new RectangleF(inner.Right - Theme.F(19), inner.Y + Theme.F(2), Theme.F(16), Theme.F(16)), Color.White, 1.8f);

                // keyframes
                if (c.HasKeys)
                {
                    foreach (var k in c.Keys)
                    {
                        float x = (float)(r.X + k.T * pps);
                        float s = Theme.F(5);
                        var dy = inner.Bottom - Theme.F(8);
                        using (var kb = new SolidBrush(sel ? Color.FromArgb(255, 255, 210, 80) : Color.FromArgb(200, 255, 255, 255)))
                            g.FillPolygon(kb, new[] { new PointF(x, dy - s), new PointF(x + s, dy), new PointF(x, dy + s), new PointF(x - s, dy) });
                    }
                }

                if (sel)
                {
                    using (var bb = Theme.AccentGradient(inner))
                    using (var pen = new Pen(bb, Theme.F(2.4f))) g.DrawPath(pen, path);
                    using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), Theme.F(1))) using (var ip = Theme.Rounded(RectangleF.Inflate(inner, -Theme.F(2), -Theme.F(2)), Theme.F(5))) g.DrawPath(pen, ip);
                    // trim handles
                    using (var hb = new SolidBrush(Color.White))
                    {
                        float hh = Math.Min(inner.Height * 0.45f, Theme.F(22)), hw = Theme.F(3);
                        g.FillRectangle(hb, inner.X + Theme.F(2), inner.Y + (inner.Height - hh) / 2, hw, hh);
                        g.FillRectangle(hb, inner.Right - Theme.F(2) - hw, inner.Y + (inner.Height - hh) / 2, hw, hh);
                    }
                }
                else using (var pen = new Pen(Color.FromArgb(al / 3, 255, 255, 255))) g.DrawPath(pen, path);
            }
        }

        void DrawThumbs(Graphics g, Clip c, MediaItem m, RectangleF r)
        {
            if (m == null) return;
            var px = m.PixelSize;
            float th = r.Height;
            float tw = Math.Max(Theme.F(24), th * px.Width / Math.Max(1, px.Height));
            float start = Math.Max(r.Left, ClipLeft - tw);
            float first = r.Left + (float)Math.Floor((start - r.Left) / tw) * tw;
            var st = g.Save();
            g.InterpolationMode = InterpolationMode.Bilinear;
            for (float x = first; x < r.Right && x < Width; x += tw)
            {
                double t = c.Start + (x - r.Left + tw / 2) / pps;
                var bmp = m.Kind == MediaKind.Image ? m.Thumb : m.StripAt(c.SourceTime(t));
                if (bmp == null) continue;
                try
                {
                    lock (m.Sync)
                    {
                        float sw = bmp.Width, sh = bmp.Height;
                        float k = Math.Max(tw / sw, th / sh);
                        var src = new RectangleF((sw - tw / k) / 2, (sh - th / k) / 2, tw / k, th / k);
                        using (var ia = new System.Drawing.Imaging.ImageAttributes())
                        {
                            var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.72f };
                            ia.SetColorMatrix(cm);
                            g.DrawImage(bmp, new[] { new PointF(x, r.Top), new PointF(x + tw, r.Top), new PointF(x, r.Bottom) }, src, GraphicsUnit.Pixel, ia);
                        }
                    }
                }
                catch { }
            }
            g.Restore(st);
        }

        void DrawWave(Graphics g, Clip c, MediaItem m, RectangleF r, Color col)
        {
            var peaks = m.Peaks;
            if (peaks == null || peaks.Length == 0) return;
            float mid = r.Top + r.Height / 2;
            float half = r.Height * 0.46f;
            int x0 = (int)Math.Max(r.Left, ClipLeft), x1 = (int)Math.Min(r.Right, Width);
            float gain = (float)Math.Min(2, c.Volume);
            using (var pen = new Pen(col, 1f))
            {
                for (int x = x0; x < x1; x++)
                {
                    double ta = c.Start + (x - r.Left) / pps, tb = ta + 1 / pps;
                    int ia = (int)(c.SourceTime(ta) * 100), ib = (int)Math.Ceiling(c.SourceTime(tb) * 100);
                    float v = 0;
                    for (int i = Math.Max(0, ia); i <= ib && i < peaks.Length; i++) v = Math.Max(v, peaks[i]);
                    v = Math.Min(1, v * gain);
                    if (v < 0.01f) continue;
                    g.DrawLine(pen, x, mid - v * half, x, mid + v * half);
                }
            }
        }
    }
}
