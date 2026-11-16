using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace StillsAndReels
{
    /// <summary>The pixels of one part of a layer before and after an edit, so undo and redo only copy what changed.</summary>
    internal sealed class PicPatch : IDisposable
    {
        public PicSurface Target;      // the layer's pixels the patch belongs to
        public Rectangle Rect;         // in the target's own coordinates
        public PicSurface Before, After;
        public long Bytes { get { return (Before != null ? Before.Bytes : 0) + (After != null ? After.Bytes : 0); } }
        public void Dispose()
        {
            if (Before != null) { Before.Dispose(); Before = null; }
            if (After != null) { After.Dispose(); After = null; }
        }
    }

    /// <summary>One undoable step.</summary>
    internal sealed class PicStep
    {
        public string Name;
        public PicState Before, After;
        public PicPatch[] Patches;
        public Rectangle PatchArea;    // canvas area covered by the patches
        public DateTime When = DateTime.UtcNow;
        public string MergeKey;        // steps with the same key made close together become one (slider drags)
    }

    /// <summary>
    /// An edit to a layer's pixels that is still going on (a brush drag, a fill). Before any area is written to,
    /// <see cref="Touch"/> saves its old pixels in 256-pixel tiles, so the undo step holds only what was painted over.
    /// </summary>
    internal sealed class PixelEdit
    {
        const int Tile = 256;
        readonly EditSession session;
        internal readonly string Name;
        internal readonly PicState Before;
        readonly Dictionary<long, PicSurface> saved = new Dictionary<long, PicSurface>();
        Rectangle touched;             // in surface coordinates
        bool done;

        /// <summary>The layer's pixels and where they sit on the canvas.</summary>
        public readonly PicSurface Surface;
        public readonly int X, Y;
        public readonly int LayerId;

        internal PixelEdit(EditSession session, string name, PicState before, PicSurface surface, int x, int y, int layerId)
        {
            this.session = session; Name = name; Before = before; Surface = surface; X = x; Y = y; LayerId = layerId;
        }

        /// <summary>The canvas area of the layer's pixels.</summary>
        public Rectangle Area { get { return new Rectangle(X, Y, Surface.Width, Surface.Height); } }

        /// <summary>Call before writing to a canvas area: keeps its old pixels for undo. Returns the part that lies on the layer (canvas coordinates).</summary>
        public Rectangle Touch(Rectangle canvasRect)
        {
            var r = Rectangle.Intersect(canvasRect, Area);
            if (r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
            var s = new Rectangle(r.X - X, r.Y - Y, r.Width, r.Height);
            int cols = (Surface.Width + Tile - 1) / Tile;
            for (int ty = s.Top / Tile; ty <= (s.Bottom - 1) / Tile; ty++)
                for (int tx = s.Left / Tile; tx <= (s.Right - 1) / Tile; tx++)
                {
                    long key = (long)ty * cols + tx;
                    if (saved.ContainsKey(key)) continue;
                    var tr = Rectangle.Intersect(new Rectangle(tx * Tile, ty * Tile, Tile, Tile), Surface.Bounds);
                    saved[key] = Surface.Clone(tr);
                }
            touched = touched.IsEmpty ? s : Rectangle.Union(touched, s);
            return r;
        }

        /// <summary>Shows what has been painted so far (the edit stays open).</summary>
        public void Show(Rectangle canvasRect) { session.RefreshArea(canvasRect); }

        /// <summary>Finishes the edit as one undo step. Returns the canvas area that changed.</summary>
        public Rectangle Commit()
        {
            if (done) return Rectangle.Empty;
            done = true;
            var patches = new List<PicPatch>();
            int cols = (Surface.Width + Tile - 1) / Tile;
            foreach (var kv in saved)
            {
                int tx = (int)(kv.Key % cols), ty = (int)(kv.Key / cols);
                var tr = Rectangle.Intersect(new Rectangle(tx * Tile, ty * Tile, Tile, Tile), Surface.Bounds);
                patches.Add(new PicPatch { Target = Surface, Rect = tr, Before = kv.Value, After = Surface.Clone(tr) });
            }
            saved.Clear();
            var area = touched.IsEmpty ? Rectangle.Empty : new Rectangle(touched.X + X, touched.Y + Y, touched.Width, touched.Height);
            session.FinishPixelEdit(this, patches.ToArray(), area);
            return area;
        }

        /// <summary>Puts the old pixels back and drops the edit.</summary>
        public void Cancel()
        {
            if (done) return;
            done = true;
            int cols = (Surface.Width + Tile - 1) / Tile;
            foreach (var kv in saved)
            {
                int tx = (int)(kv.Key % cols), ty = (int)(kv.Key / cols);
                Surface.CopyFrom(kv.Value, kv.Value.Bounds, new Point(tx * Tile, ty * Tile));
                kv.Value.Dispose();
            }
            saved.Clear();
            var area = touched.IsEmpty ? Rectangle.Empty : new Rectangle(touched.X + X, touched.Y + Y, touched.Width, touched.Height);
            session.CancelPixelEdit(this, area);
        }
    }

    /// <summary>
    /// A picture being edited: its layers, the undo history and the finished (mixed) picture the canvas shows.
    /// The file it came from is never written to; saving always makes a new file.
    /// </summary>
    internal sealed class EditSession : IDisposable
    {
        public const int MaxSteps = 200;

        /// <summary>How much memory the undo history may hold before the oldest steps are dropped.</summary>
        public static long UndoBudget = DefaultBudget();

        /// <summary>The file this picture came from (or was last saved to as a layered picture); null for a new picture.</summary>
        public string SourcePath;
        readonly List<PicStep> undo = new List<PicStep>();
        readonly List<PicStep> redo = new List<PicStep>();
        readonly HashSet<PicSurface> owned = new HashSet<PicSurface>();
        readonly HashSet<PicSurface> pinned = new HashSet<PicSurface>();
        PicState saved;
        int activeId;
        PixelEdit openEdit;
        bool disposed;

        public PicState State { get; private set; }
        public readonly PicCompositor Compositor = new PicCompositor();

        /// <summary>Raised after every change to the picture or its history.</summary>
        public event EventHandler Changed;
        /// <summary>Raised with the canvas area that needs repainting.</summary>
        public event Action<Rectangle> AreaChanged;

        public EditSession(Image source, string sourcePath) : this(source, sourcePath, false) { }

        /// <summary>startDirty: a brand-new picture (e.g. pasted) that has never been saved.</summary>
        public EditSession(Image source, string sourcePath, bool startDirty) : this(PicSurface.FromImage(source), sourcePath, startDirty) { }

        /// <summary>Starts from pixels the session takes ownership of.</summary>
        public EditSession(PicSurface pixels, string sourcePath, bool startDirty)
        {
            SourcePath = sourcePath;
            var layer = PicLayer.Create("Background", pixels);
            State = new PicState(pixels.Size, new[] { layer });
            activeId = layer.Id;
            Own(State);
            saved = startDirty ? null : State;
            Compositor.Update(State, null);
        }

        /// <summary>Starts from a whole layered picture (a project file).</summary>
        public EditSession(PicState state, string sourcePath)
        {
            SourcePath = sourcePath;
            State = state;
            activeId = state.Layers[state.Layers.Length - 1].Id;
            Own(State);
            saved = State;
            Compositor.Update(State, null);
        }

        static long DefaultBudget()
        {
            if (IntPtr.Size < 8) return 384L << 20;
            long ram = PicNative.PhysicalMemory();
            if (ram <= 0) return 1024L << 20;
            return Math.Max(512L << 20, Math.Min(2048L << 20, ram / 4));
        }

        // ------------------------------------------------------------------ state

        public Size Size { get { return State.Size; } }
        public bool CanUndo { get { return undo.Count > 0; } }
        public bool CanRedo { get { return redo.Count > 0; } }
        public string UndoName { get { return undo.Count > 0 ? undo[undo.Count - 1].Name : null; } }
        public string RedoName { get { return redo.Count > 0 ? redo[redo.Count - 1].Name : null; } }
        public int UndoCount { get { return undo.Count; } }
        public bool IsDirty { get { return !ReferenceEquals(State, saved); } }
        public void MarkSaved() { saved = State; Raise(); }

        /// <summary>True when there is ink, a shape or text anywhere (things "Clear all" would remove).</summary>
        public bool HasInk { get { return State.HasObjects; } }

        public int ActiveIndex
        {
            get
            {
                int i = State.IndexOf(activeId);
                return i >= 0 ? i : State.Layers.Length - 1;
            }
        }

        public PicLayer ActiveLayer { get { return State.Layers[ActiveIndex]; } }

        PicSelection selection;

        /// <summary>The selected part of the picture (null: nothing selected, tools work everywhere). Not part of undo.</summary>
        public PicSelection Selection
        {
            get { return selection; }
            set
            {
                if (ReferenceEquals(selection, value)) return;
                selection = value;
                RaiseArea(State.Bounds);
                Raise();
            }
        }

        public void SetActive(int index)
        {
            index = Math.Max(0, Math.Min(State.Layers.Length - 1, index));
            if (State.Layers[index].Id == activeId) return;
            activeId = State.Layers[index].Id;
            Raise();
        }

        void Raise() { var h = Changed; if (h != null) h(this, EventArgs.Empty); }
        void RaiseArea(Rectangle r) { var h = AreaChanged; if (h != null) h(r); }

        void Own(PicState s) { foreach (var l in s.Layers) if (l.Pixels != null) owned.Add(l.Pixels); }

        /// <summary>Memory held only for undo and redo.</summary>
        public long HistoryBytes
        {
            get
            {
                var live = new HashSet<PicSurface>();
                foreach (var l in State.Layers) if (l.Pixels != null) live.Add(l.Pixels);
                var counted = new HashSet<PicSurface>();
                long total = 0;
                foreach (var list in new[] { undo, redo })
                    foreach (var st in list)
                    {
                        foreach (var s in new[] { st.Before, st.After })
                            foreach (var l in s.Layers)
                                if (l.Pixels != null && !live.Contains(l.Pixels) && counted.Add(l.Pixels)) total += l.Pixels.Bytes;
                        if (st.Patches != null) foreach (var p in st.Patches) total += p.Bytes;
                    }
                return total;
            }
        }

        /// <summary>Frees every surface that neither the picture nor its history can reach any more.</summary>
        void Sweep()
        {
            var keep = new HashSet<PicSurface>(pinned);
            foreach (var l in State.Layers) if (l.Pixels != null) keep.Add(l.Pixels);
            foreach (var list in new[] { undo, redo })
                foreach (var st in list)
                    foreach (var s in new[] { st.Before, st.After })
                        foreach (var l in s.Layers) if (l.Pixels != null) keep.Add(l.Pixels);
            var drop = new List<PicSurface>();
            foreach (var s in owned) if (!keep.Contains(s)) drop.Add(s);
            foreach (var s in drop) { owned.Remove(s); s.Dispose(); }
        }

        static void DropStep(PicStep st) { if (st.Patches != null) foreach (var p in st.Patches) p.Dispose(); }

        void Trim()
        {
            bool dropped = false;
            while (undo.Count > MaxSteps) { DropStep(undo[0]); undo.RemoveAt(0); dropped = true; }
            if (undo.Count > 1 && HistoryBytes > UndoBudget)
            {
                while (undo.Count > 1 && HistoryBytes > UndoBudget) { DropStep(undo[0]); undo.RemoveAt(0); dropped = true; }
            }
            if (dropped) Sweep();
        }

        /// <summary>Makes a new state the current one, as one undo step.</summary>
        void Commit(string name, PicState next, PicPatch[] patches = null, Rectangle patchArea = default(Rectangle), string mergeKey = null)
        {
            if (openEdit != null) throw new InvalidOperationException("A pixel edit is still open.");
            var before = State;
            if (ReferenceEquals(next, before)) next = new PicState(before.Size, before.Layers);
            Own(next);
            bool hadRedo = redo.Count > 0;
            foreach (var st in redo) DropStep(st);
            redo.Clear();

            PicStep last = undo.Count > 0 ? undo[undo.Count - 1] : null;
            if (mergeKey != null && last != null && last.MergeKey == mergeKey && patches == null && last.Patches == null &&
                (DateTime.UtcNow - last.When).TotalSeconds < 1.5 && !ReferenceEquals(last.After, saved))
            {
                last.After = next;
                last.When = DateTime.UtcNow;
            }
            else undo.Add(new PicStep { Name = name, Before = before, After = next, Patches = patches, PatchArea = patchArea, MergeKey = mergeKey });

            State = next;
            if (before.Size != next.Size) selection = null;
            if (State.IndexOf(activeId) < 0) activeId = State.Layers[Math.Min(State.Layers.Length - 1, Math.Max(0, before.IndexOf(activeId)))].Id;
            if (hadRedo) Sweep();
            Trim();
            var dirty = Union(PicState.Diff(before, next), patchArea);
            Compositor.Update(State, before.Size != next.Size ? (Rectangle?)null : dirty);
            RaiseArea(before.Size != next.Size ? Rectangle.Union(before.Bounds, next.Bounds) : dirty);
            Raise();
        }

        static Rectangle Union(Rectangle a, Rectangle b) { return a.IsEmpty ? b : b.IsEmpty ? a : Rectangle.Union(a, b); }

        public void Undo()
        {
            if (undo.Count == 0 || openEdit != null) return;
            var st = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            if (st.Patches != null) foreach (var p in st.Patches) p.Target.CopyFrom(p.Before, p.Before.Bounds, p.Rect.Location);
            redo.Add(st);
            Move(st.After, st.Before, st.PatchArea);
        }

        public void Redo()
        {
            if (redo.Count == 0 || openEdit != null) return;
            var st = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            if (st.Patches != null) foreach (var p in st.Patches) p.Target.CopyFrom(p.After, p.After.Bounds, p.Rect.Location);
            undo.Add(st);
            Move(st.Before, st.After, st.PatchArea);
        }

        void Move(PicState from, PicState to, Rectangle patchArea)
        {
            State = to;
            if (State.IndexOf(activeId) < 0) activeId = State.Layers[State.Layers.Length - 1].Id;
            bool resized = from.Size != to.Size;
            if (resized) selection = null;
            var dirty = Union(PicState.Diff(from, to), patchArea);
            Compositor.Update(State, resized ? (Rectangle?)null : dirty);
            RaiseArea(resized ? Rectangle.Union(from.Bounds, to.Bounds) : dirty);
            Raise();
        }

        /// <summary>Repaints part of the canvas from the layers as they are now (used while a pixel edit is open).</summary>
        internal void RefreshArea(Rectangle canvasRect)
        {
            canvasRect.Intersect(State.Bounds);
            if (canvasRect.Width <= 0 || canvasRect.Height <= 0) return;
            Compositor.Update(State, canvasRect);
            RaiseArea(canvasRect);
        }

        // ------------------------------------------------------------------ pixel edits

        /// <summary>
        /// Opens the active layer's pixels for painting. With <paramref name="wholeCanvas"/> the layer is first grown
        /// to cover the canvas, so a brush can paint anywhere on it.
        /// </summary>
        public PixelEdit BeginPixels(string name, bool wholeCanvas)
        {
            if (openEdit != null) throw new InvalidOperationException("A pixel edit is already open.");
            var before = State;
            int i = ActiveIndex;
            var layer = State.Layers[i];
            var px = layer.Pixels;
            int x = layer.X, y = layer.Y;
            bool covers = px != null && layer.PixelBounds.Contains(State.Bounds);
            if (px == null || (wholeCanvas && !covers))
            {
                var area = px == null ? State.Bounds : Rectangle.Union(layer.PixelBounds, State.Bounds);
                var grown = new PicSurface(area.Width, area.Height);
                if (px != null) grown.CopyFrom(px, px.Bounds, new Point(layer.X - area.X, layer.Y - area.Y));
                owned.Add(grown);
                px = grown; x = area.X; y = area.Y;
                State = State.WithLayer(i, layer.WithPixels(grown, x, y));
                Compositor.Update(State, layer.Bounds);
            }
            openEdit = new PixelEdit(this, name, before, px, x, y, layer.Id);
            return openEdit;
        }

        internal void FinishPixelEdit(PixelEdit e, PicPatch[] patches, Rectangle area)
        {
            if (!ReferenceEquals(openEdit, e)) return;
            openEdit = null;
            var next = State;
            State = e.Before;                       // Commit works from the state before the edit began
            if (patches.Length == 0 && ReferenceEquals(next, e.Before)) { Sweep(); return; }
            Commit(e.Name, ReferenceEquals(next, e.Before) ? new PicState(next.Size, next.Layers) : next, patches.Length > 0 ? patches : null, area);
        }

        internal void CancelPixelEdit(PixelEdit e, Rectangle area)
        {
            if (!ReferenceEquals(openEdit, e)) return;
            openEdit = null;
            var shown = State;
            State = e.Before;
            Sweep();
            Compositor.Update(State, Union(PicState.Diff(shown, State), area));
            RaiseArea(Union(PicState.Diff(shown, State), area));
            Raise();
        }

        public bool HasOpenEdit { get { return openEdit != null; } }

        /// <summary>Replaces the active layer's pixels with a new surface of the same size and place (adjustments, filters).</summary>
        public void ReplaceActivePixels(string name, PicSurface pixels)
        {
            int i = ActiveIndex;
            var l = State.Layers[i];
            Commit(name, State.WithLayer(i, l.WithPixels(pixels, l.X, l.Y)));
        }

        // ------------------------------------------------------------------ objects (ink, shapes, text)

        public void AddObject(PicObject o, string name = null)
        {
            int i = ActiveIndex;
            var l = State.Layers[i];
            var list = new List<PicObject>(l.Objects) { o };
            Commit(name ?? NameOf(o), State.WithLayer(i, l.WithObjects(list.ToArray())));
        }

        static string NameOf(PicObject o)
        {
            var s = o as Stroke;
            if (s != null) return s.Tool == EditTool.Highlighter ? "Highlighter" : s.Tool == EditTool.Pencil ? "Pencil" : "Pen";
            var sh = o as ShapeObject;
            if (sh != null) return sh.Kind == ShapeKind.RoundRect ? "Rounded rectangle" : sh.Kind.ToString() == "Rect" ? "Rectangle" : sh.Kind.ToString();
            if (o is TextObject) return "Text";
            if (o is StepObject) return "Step marker";
            return "Draw";
        }

        public void AddStroke(Stroke s) { AddObject(s); }

        /// <summary>The layer an object is on, or -1.</summary>
        public int LayerOf(PicObject o)
        {
            for (int i = 0; i < State.Layers.Length; i++)
                if (Array.IndexOf(State.Layers[i].Objects, o) >= 0) return i;
            return -1;
        }

        public void RemoveObjects(ICollection<PicObject> remove, string name = "Erase")
        {
            if (remove == null || remove.Count == 0) return;
            var layers = (PicLayer[])State.Layers.Clone();
            bool any = false;
            for (int i = 0; i < layers.Length; i++)
            {
                var l = layers[i];
                if (!l.HasObjects) continue;
                var list = new List<PicObject>();
                foreach (var o in l.Objects) if (!remove.Contains(o)) list.Add(o);
                if (list.Count == l.Objects.Length) continue;
                layers[i] = l.WithObjects(list.ToArray());
                any = true;
            }
            if (any) Commit(name, State.WithLayers(layers));
        }

        public void RemoveStrokes(ICollection<Stroke> remove)
        {
            var set = new HashSet<PicObject>();
            foreach (var s in remove) set.Add(s);
            RemoveObjects(set);
        }

        /// <summary>Swaps one object for an edited copy. Quick repeats with the same key (dragging) become one undo step.</summary>
        public bool ReplaceObject(PicObject old, PicObject now, string name, string mergeKey = null)
        {
            int i = LayerOf(old);
            if (i < 0 || now == null) return false;
            var l = State.Layers[i];
            var arr = (PicObject[])l.Objects.Clone();
            arr[Array.IndexOf(arr, old)] = now;
            Commit(name, State.WithLayer(i, l.WithObjects(arr)), null, default(Rectangle), mergeKey);
            return true;
        }

        /// <summary>Moves an object to the top or bottom of its layer's drawing order.</summary>
        public void ReorderObject(PicObject o, bool toFront)
        {
            int i = LayerOf(o);
            if (i < 0) return;
            var l = State.Layers[i];
            var list = new List<PicObject>(l.Objects);
            list.Remove(o);
            if (toFront) list.Add(o); else list.Insert(0, o);
            Commit(toFront ? "Bring to front" : "Send to back", State.WithLayer(i, l.WithObjects(list.ToArray())));
        }

        /// <summary>Removes every stroke, shape and text box on every layer.</summary>
        public void ClearInk()
        {
            if (!State.HasObjects) return;
            var layers = (PicLayer[])State.Layers.Clone();
            for (int i = 0; i < layers.Length; i++) if (layers[i].HasObjects) layers[i] = layers[i].WithObjects(null);
            Commit("Clear drawings", State.WithLayers(layers));
        }

        /// <summary>The topmost object under a point, looking from the top layer down. Hidden layers are skipped.</summary>
        public PicObject ObjectAt(PointF p, float radius)
        {
            for (int i = State.Layers.Length - 1; i >= 0; i--)
            {
                var l = State.Layers[i];
                if (!l.Visible) continue;
                for (int k = l.Objects.Length - 1; k >= 0; k--)
                    if (l.Objects[k].HitTest(p, radius)) return l.Objects[k];
            }
            return null;
        }

        public IEnumerable<PicObject> AllObjects()
        {
            foreach (var l in State.Layers) if (l.Visible) foreach (var o in l.Objects) yield return o;
        }

        // ------------------------------------------------------------------ whole-picture changes

        /// <summary>Carries every layer through a canvas mapping. Layers whose shapes can't follow are drawn into pixels first.</summary>
        PicLayer[] MapObjects(PicLayer[] layers, PicMap m)
        {
            var result = new PicLayer[layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                var l = layers[i];
                if (!l.HasObjects) { result[i] = l; continue; }
                var mapped = new PicObject[l.Objects.Length];
                bool ok = true;
                for (int k = 0; k < mapped.Length && ok; k++) { mapped[k] = l.Objects[k].Map(m); ok = mapped[k] != null; }
                result[i] = ok ? l.WithObjects(mapped) : null;
            }
            return result;
        }

        /// <summary>A layer with its shapes and text drawn into its pixels (they stop being editable).</summary>
        PicLayer Rasterized(PicLayer l)
        {
            if (!l.HasObjects) return l;
            var area = State.Bounds;
            if (l.Pixels != null) area = Rectangle.Union(area, l.PixelBounds);
            var s = new PicSurface(area.Width, area.Height);
            if (l.Pixels != null) s.CopyFrom(l.Pixels, l.Pixels.Bounds, new Point(l.X - area.X, l.Y - area.Y));
            using (var g = s.CreateGraphics())
            {
                g.TranslateTransform(-area.X, -area.Y);
                foreach (var o in l.Objects) o.Draw(g);
            }
            // keep the layer no bigger than it needs to be
            var used = s.ContentBounds();
            if (used.IsEmpty) { s.Dispose(); return l.WithPixels(null, 0, 0).WithObjects(null); }
            if (used != s.Bounds && (long)used.Width * used.Height < (long)s.Width * s.Height * 3 / 4)
            {
                var small = s.Clone(used);
                s.Dispose();
                return l.WithPixels(small, area.X + used.X, area.Y + used.Y).WithObjects(null);
            }
            return l.WithPixels(s, area.X, area.Y).WithObjects(null);
        }

        public void Crop(Rectangle r)
        {
            r.Intersect(State.Bounds);
            if (r.Width < 1 || r.Height < 1 || r.Size == Size) return;
            var m = PicMap.Translate(-r.X, -r.Y);
            var layers = new PicLayer[State.Layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                var l = State.Layers[i];
                var objs = new PicObject[l.Objects.Length];
                for (int k = 0; k < objs.Length; k++) objs[k] = l.Objects[k].Map(m);
                PicSurface px = l.Pixels;
                int x = l.X - r.X, y = l.Y - r.Y;
                if (px != null)
                {
                    var keep = Rectangle.Intersect(new Rectangle(x, y, px.Width, px.Height), new Rectangle(Point.Empty, r.Size));
                    if (keep.Width <= 0 || keep.Height <= 0) { px = null; x = y = 0; }
                    else if (keep.Size != px.Size)
                    {
                        px = px.Clone(new Rectangle(keep.X - x, keep.Y - y, keep.Width, keep.Height));
                        x = keep.X; y = keep.Y;
                    }
                }
                layers[i] = l.WithPixels(px, x, y).WithObjects(objs);
            }
            Commit("Crop", new PicState(r.Size, layers));
        }

        public void RotateFlip(RotateFlipType type)
        {
            float w = Size.Width, h = Size.Height;
            var m = PicMap.ForRotateFlip(type, w, h);
            bool turn = type == RotateFlipType.Rotate90FlipNone || type == RotateFlipType.Rotate270FlipNone;
            var mapped = MapObjects(State.Layers, m);
            var layers = new PicLayer[State.Layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                var l = mapped[i];
                if (l == null)
                {
                    // text can't be mirrored or turned as text: it becomes pixels
                    l = Rasterized(State.Layers[i]);
                }
                if (l.Pixels != null)
                {
                    var px = l.Pixels.RotateFlip(type);
                    var a = m.Apply(new PointF(l.X, l.Y));
                    var b = m.Apply(new PointF(l.X + l.Pixels.Width, l.Y + l.Pixels.Height));
                    int nx = (int)Math.Round(Math.Min(a.X, b.X)), ny = (int)Math.Round(Math.Min(a.Y, b.Y));
                    if (!ReferenceEquals(l.Pixels, State.Layers[i].Pixels)) l.Pixels.Dispose();   // the temporary drawn-in copy
                    l = l.WithPixels(px, nx, ny);
                }
                layers[i] = l;
            }
            string name = type == RotateFlipType.Rotate90FlipNone ? "Rotate right" : type == RotateFlipType.Rotate270FlipNone ? "Rotate left"
                : type == RotateFlipType.Rotate180FlipNone ? "Rotate 180°" : type == RotateFlipType.RotateNoneFlipX ? "Flip horizontal" : "Flip vertical";
            Commit(name, new PicState(turn ? new Size(Size.Height, Size.Width) : Size, layers));
        }

        /// <summary>Makes the canvas larger (never smaller). The new area is transparent; nothing else changes.</summary>
        public void ExpandCanvas(int w, int h)
        {
            w = Math.Max(w, Size.Width); h = Math.Max(h, Size.Height);
            if (w == Size.Width && h == Size.Height) return;
            Commit("Canvas size", new PicState(new Size(w, h), State.Layers));
        }

        /// <summary>Changes the canvas size, keeping the picture at an anchor (0 = left/top, 0.5 = middle, 1 = right/bottom).</summary>
        public void SetCanvasSize(int w, int h, float anchorX, float anchorY)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            if (w == Size.Width && h == Size.Height) return;
            int dx = (int)Math.Round((w - Size.Width) * anchorX), dy = (int)Math.Round((h - Size.Height) * anchorY);
            var m = PicMap.Translate(dx, dy);
            var layers = new PicLayer[State.Layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                var l = State.Layers[i];
                var objs = new PicObject[l.Objects.Length];
                for (int k = 0; k < objs.Length; k++) objs[k] = l.Objects[k].Map(m);
                layers[i] = l.WithPosition(l.X + dx, l.Y + dy).WithObjects(objs);
            }
            Commit("Canvas size", new PicState(new Size(w, h), layers));
        }

        /// <summary>Resizes the whole picture: every layer is resampled and every shape scaled.</summary>
        public void ResizeImage(int w, int h, bool smooth = true)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            if (w == Size.Width && h == Size.Height) return;
            float sx = (float)w / Size.Width, sy = (float)h / Size.Height;
            var m = PicMap.Scaling(sx, sy);
            var mapped = MapObjects(State.Layers, m);
            var layers = new PicLayer[State.Layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                var l = mapped[i] ?? Rasterized(State.Layers[i]);
                if (l.Pixels != null)
                {
                    int nx = (int)Math.Round(l.X * sx), ny = (int)Math.Round(l.Y * sy);
                    int nw = Math.Max(1, (int)Math.Round((l.X + l.Pixels.Width) * sx) - nx), nh = Math.Max(1, (int)Math.Round((l.Y + l.Pixels.Height) * sy) - ny);
                    var px = l.Pixels.Resize(nw, nh, smooth);
                    if (!ReferenceEquals(l.Pixels, State.Layers[i].Pixels)) l.Pixels.Dispose();
                    l = l.WithPixels(px, nx, ny);
                }
                layers[i] = l;
            }
            Commit("Resize", new PicState(new Size(w, h), layers));
        }

        /// <summary>
        /// Turns the whole picture by any angle (degrees, clockwise) about its middle. With <paramref name="cropToFit"/>
        /// the empty corners are cut away; otherwise the canvas grows to hold everything. Drawings become pixels.
        /// </summary>
        public void RotateFree(double degrees, bool cropToFit)
        {
            if (Math.Abs(degrees) < 0.005) return;
            Size outSize;
            if (cropToFit)
            {
                var inside = PicAdjust.LargestInside(Size.Width, Size.Height, degrees);
                // one pixel in from the exact fit on every side: the turned picture's outermost pixels are where
                // drawing rounds differently from one graphics library to the next, and a see-through corner must not get in
                outSize = new Size(Math.Max(1, (int)Math.Floor(inside.Width) - 2), Math.Max(1, (int)Math.Floor(inside.Height) - 2));
            }
            else
            {
                double a = Math.Abs(degrees * Math.PI / 180.0), sin = Math.Abs(Math.Sin(a)), cos = Math.Abs(Math.Cos(a));
                outSize = new Size((int)Math.Ceiling(Size.Width * cos + Size.Height * sin), (int)Math.Ceiling(Size.Width * sin + Size.Height * cos));
            }
            var layers = new PicLayer[State.Layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                var l = State.Layers[i];
                if (l.Pixels == null && !l.HasObjects) { layers[i] = l; continue; }
                using (var full = PicCompositor.RenderLayer(State, l))
                    layers[i] = l.WithPixels(PicAdjust.Rotated(full, degrees, outSize, cropToFit), 0, 0).WithObjects(null);
            }
            Commit(cropToFit ? "Straighten" : "Rotate", new PicState(outSize, layers));
        }

        /// <summary>Cuts plain borders (see-through or one flat colour) off the picture. False when there is nothing to trim.</summary>
        public bool TrimEdges()
        {
            Rectangle r;
            using (var flat = PicCompositor.Flatten(State)) r = PicAdjust.TrimBounds(flat);
            if (r == State.Bounds) return false;
            Crop(r);
            return true;
        }

        // ------------------------------------------------------------------ layers

        /// <summary>Adds a layer above the active one and makes it active. Takes ownership of the pixels (which may be null).</summary>
        public PicLayer AddLayer(string name, PicSurface pixels, int x, int y, string stepName = "New layer")
        {
            var layer = PicLayer.Create(name, pixels, x, y);
            var list = new List<PicLayer>(State.Layers);
            list.Insert(ActiveIndex + 1, layer);
            var next = State.WithLayers(list.ToArray());
            int id = layer.Id;
            Commit(stepName, next);
            activeId = id;
            Raise();
            return layer;
        }

        public string NextLayerName(string stem)
        {
            for (int n = State.Layers.Length; ; n++)
            {
                string name = stem + " " + n;
                bool used = false;
                foreach (var l in State.Layers) if (l.Name == name) used = true;
                if (!used) return name;
            }
        }

        public void DeleteLayer(int index)
        {
            if (State.Layers.Length <= 1 || index < 0 || index >= State.Layers.Length) return;
            bool wasActive = State.Layers[index].Id == activeId;
            var list = new List<PicLayer>(State.Layers);
            list.RemoveAt(index);
            int id = wasActive ? list[Math.Max(0, index - 1)].Id : activeId;
            Commit("Delete layer", State.WithLayers(list.ToArray()));
            activeId = id;
            Raise();
        }

        public void DuplicateLayer(int index)
        {
            var l = State.Layers[index];
            var copy = l.AsNew(l.Name + " copy", l.Pixels != null ? l.Pixels.Clone() : null);
            var list = new List<PicLayer>(State.Layers);
            list.Insert(index + 1, copy);
            Commit("Duplicate layer", State.WithLayers(list.ToArray()));
            activeId = copy.Id;
            Raise();
        }

        public void MoveLayer(int from, int to)
        {
            to = Math.Max(0, Math.Min(State.Layers.Length - 1, to));
            if (from == to || from < 0 || from >= State.Layers.Length) return;
            var list = new List<PicLayer>(State.Layers);
            var l = list[from];
            list.RemoveAt(from);
            list.Insert(to, l);
            Commit("Reorder layers", State.WithLayers(list.ToArray()));
        }

        public void SetLayerVisible(int index, bool visible)
        {
            var l = State.Layers[index];
            if (l.Visible == visible) return;
            Commit(visible ? "Show layer" : "Hide layer", State.WithLayer(index, l.WithVisible(visible)));
        }

        public void SetLayerOpacity(int index, float opacity)
        {
            var l = State.Layers[index];
            if (Math.Abs(l.Opacity - opacity) < 0.0005f) return;
            Commit("Layer opacity", State.WithLayer(index, l.WithOpacity(opacity)), null, default(Rectangle), "opacity" + l.Id);
        }

        public void SetLayerBlend(int index, PicBlend blend)
        {
            var l = State.Layers[index];
            if (l.Blend == blend) return;
            Commit("Blend mode", State.WithLayer(index, l.WithBlend(blend)));
        }

        public void RenameLayer(int index, string name)
        {
            var l = State.Layers[index];
            if (string.IsNullOrWhiteSpace(name) || l.Name == name) return;
            Commit("Rename layer", State.WithLayer(index, l.WithName(name.Trim())));
        }

        /// <summary>Moves a layer (pixels and shapes) by whole pixels. Drags with the same key become one undo step.</summary>
        public void MoveLayerBy(int index, int dx, int dy, string mergeKey = null)
        {
            if (dx == 0 && dy == 0) return;
            var l = State.Layers[index];
            var m = PicMap.Translate(dx, dy);
            var objs = new PicObject[l.Objects.Length];
            for (int k = 0; k < objs.Length; k++) objs[k] = l.Objects[k].Map(m);
            Commit("Move layer", State.WithLayer(index, l.WithPosition(l.X + dx, l.Y + dy).WithObjects(objs)), null, default(Rectangle), mergeKey);
        }

        /// <summary>Gives a layer new pixels at a new place (resizing a pasted picture). Takes ownership of the pixels.</summary>
        public void SetLayerPixels(int index, PicSurface pixels, int x, int y, string name, string mergeKey = null)
        {
            var l = State.Layers[index];
            Commit(name, State.WithLayer(index, l.WithPixels(pixels, x, y)), null, default(Rectangle), mergeKey);
        }

        /// <summary>Keeps a surface alive while a tool still needs it (the original of a layer being resized).</summary>
        public void Pin(PicSurface s) { if (s != null) pinned.Add(s); }
        public void Unpin(PicSurface s) { if (s != null && pinned.Remove(s)) Sweep(); }

        /// <summary>Draws a layer's shapes and text into its pixels.</summary>
        public void RasterizeLayer(int index)
        {
            var l = State.Layers[index];
            if (!l.HasObjects) return;
            Commit("Turn drawings into pixels", State.WithLayer(index, Rasterized(l)));
        }

        /// <summary>Mixes a layer into the one below it.</summary>
        public void MergeDown(int index)
        {
            if (index <= 0 || index >= State.Layers.Length) return;
            var upper = State.Layers[index];
            var lower = State.Layers[index - 1];
            // the pair mixed on its own, at the lower layer's settings
            var pair = new PicState(State.Size, new[] { lower.WithVisible(true).WithOpacity(1f).WithBlend(PicBlend.Normal), upper.WithVisible(true) });
            var area = State.Bounds;
            var px = PicCompositor.Flatten(pair);
            var merged = lower.WithPixels(px, area.X, area.Y).WithObjects(null);
            var list = new List<PicLayer>(State.Layers);
            list[index - 1] = merged;
            list.RemoveAt(index);
            Commit("Merge down", State.WithLayers(list.ToArray()));
            activeId = merged.Id;
            Raise();
        }

        /// <summary>Mixes every visible layer into one.</summary>
        public void FlattenLayers()
        {
            if (State.Layers.Length == 1 && !State.Layers[0].HasObjects && State.Layers[0].Opacity >= 1f && State.Layers[0].Blend == PicBlend.Normal) return;
            var px = PicCompositor.Flatten(State);
            var layer = PicLayer.Create("Background", px);
            Commit("Flatten", new PicState(State.Size, new[] { layer }));
            activeId = layer.Id;
            Raise();
        }

        /// <summary>Bakes a pasted picture into a new layer (kept for callers that place a picture in one go).</summary>
        public void PasteImage(Image img, RectangleF rect)
        {
            int w = Math.Max(1, (int)Math.Round(rect.Width)), h = Math.Max(1, (int)Math.Round(rect.Height));
            using (var full = PicSurface.FromImage(img))
            {
                var px = full.Width == w && full.Height == h ? full.Clone() : full.Resize(w, h);
                AddLayer(NextLayerName("Pasted"), px, (int)Math.Round(rect.X), (int)Math.Round(rect.Y), "Paste");
            }
        }

        // ------------------------------------------------------------------ output

        /// <summary>The finished picture (all visible layers mixed) as a new surface.</summary>
        public PicSurface FlattenSurface()
        {
            if (Compositor.ShowsStateAsIs && Compositor.Picture != null && ReferenceEquals(Compositor.State, State)) return Compositor.Picture.Clone();
            return PicCompositor.Flatten(State);
        }

        /// <summary>The finished picture as a normal 32-bit bitmap, for saving and copying.</summary>
        public Bitmap Flatten()
        {
            if (Compositor.ShowsStateAsIs && Compositor.Picture != null && ReferenceEquals(Compositor.State, State)) return Compositor.Picture.ToBitmap();
            using (var s = PicCompositor.Flatten(State)) return s.ToBitmap();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Compositor.Dispose();
            foreach (var list in new[] { undo, redo }) foreach (var st in list) DropStep(st);
            undo.Clear(); redo.Clear();
            foreach (var s in owned) s.Dispose();
            owned.Clear();
            foreach (var s in pinned) s.Dispose();
            pinned.Clear();
        }
    }

    internal static class PicNative
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        /// <summary>Installed memory in bytes, or 0 when it can't be read.</summary>
        public static long PhysicalMemory()
        {
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) return 0;
                var st = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                return GlobalMemoryStatusEx(ref st) ? (long)st.TotalPhys : 0;
            }
            catch { return 0; }
        }
    }

    internal static class ImageSaver
    {
        /// <summary>Saves a bitmap. The format follows the file extension.</summary>
        public static void Save(Bitmap bmp, string path) { SaveWith(bmp, path, null); }

        public static bool IsJpeg(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".jpe" || ext == ".jfif";
        }

        public static bool IsWebp(string path) { return string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase); }

        /// <summary>Saves a bitmap with a chosen quality (JPEG, WebP) and, for JPEG, optional photo details.</summary>
        public static void SaveWith(Bitmap bmp, string path, SaveOptions o)
        {
            if (o == null) o = new SaveOptions();
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (IsJpeg(path))
            {
                byte[] jpeg = PicCodecs.EncodeJpeg(bmp, o.Quality);
                if (o.DetailsFrom != null)
                {
                    try
                    {
                        var details = JpegDetails.Clean(JpegDetails.Read(File.ReadAllBytes(o.DetailsFrom)), bmp.Width, bmp.Height);
                        if (details != null) jpeg = JpegDetails.Insert(jpeg, details);
                    }
                    catch { }   // details are a nicety: the picture is saved either way
                }
                File.WriteAllBytes(path, jpeg);
            }
            else if (ext == ".webp") PicCodecs.SaveWebp(bmp, path, o.Quality, o.Lossless);
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
