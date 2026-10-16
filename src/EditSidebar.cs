using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>
    /// Left-hand sidebar for picture Edit mode. It only raises events; MainForm owns the edit session
    /// and applies them with the same code the Edit menu uses.
    /// </summary>
    internal sealed class EditSidebar : ThemedPanel
    {
        public event Action<EditTool> ToolSelected;
        public event Action<Color> ColorPicked;
        public event Action<int> SizeChangedByUser;
        public event Action<RotateFlipType> TransformRequested;
        public event Action<string> CommandRequested; // undo, redo, clear, copy, save, applycrop, cancelcrop

        readonly Dictionary<EditTool, ThemedButton> tools = new Dictionary<EditTool, ThemedButton>();
        readonly List<Control> order = new List<Control>();
        readonly HashSet<Control> fullRow = new HashSet<Control>();
        readonly List<Control[]> rows = new List<Control[]>();

        readonly Control title;
        readonly HintLabel hint;
        readonly SectionLabel colourLabel, sizeLabel;
        readonly SwatchPicker swatches;
        readonly ThemedSlider slider;
        readonly ThemedButton applyCrop, cancelCrop, undo, redo, clearInk, copy, save, paste, pasteNew;
        readonly ThemedButton rotL, rotR, flipH, flipV;
        readonly HintLabel cropHint;
        bool hasPicture;
        EditTool tool = EditTool.Pen;

        public EditSidebar()
        {
            Surface = Surface.Panel;
            BorderRight = true;
            Width = Theme.S(260);
            AutoScroll = true;

            title = new TitleLabel("Edit picture");
            Add(title);
            hint = new HintLabel { Text = "Select a picture in File Explorer, use File > Open, or paste a picture or snip (Ctrl+V) to start editing." };
            Add(hint);

            Add(new SectionLabel("Tools"));
            AddPair(Tool(EditTool.Pen, Ico.Pen, "Pen", "Ballpoint pen"), Tool(EditTool.Pencil, Ico.PencilTool, "Pencil", "Pencil"));
            AddPair(Tool(EditTool.Highlighter, Ico.Highlighter, "Highlighter", "Highlighter"), Tool(EditTool.Eraser, Ico.Eraser, "Eraser", "Eraser – removes whole strokes you drag over"));
            AddPair(Tool(EditTool.Crop, Ico.Crop, "Crop", "Crop"), Tool(EditTool.Pan, Ico.Hand, "Move", "Move – drag to pan around the picture"));

            cropHint = new HintLabel { Text = "Drag on the picture to choose the area to keep. Drag the handles to adjust." };
            Add(cropHint);
            applyCrop = Button(Ico.Check, "Apply crop", "Apply crop (Enter)", "applycrop");
            applyCrop.Primary = true;
            cancelCrop = Button(Ico.Close, "Cancel", "Cancel crop (Esc)", "cancelcrop");
            AddPair(applyCrop, cancelCrop);

            colourLabel = new SectionLabel("Colour");
            Add(colourLabel);
            swatches = new SwatchPicker();
            swatches.ColorPicked += (s, e) => { var h = ColorPicked; if (h != null) h(e.Color); };
            Add(swatches);

            sizeLabel = new SectionLabel("Size");
            Add(sizeLabel);
            slider = new ThemedSlider { Minimum = 1, Maximum = 30 };
            slider.ValueChanged += (s, e) =>
            {
                sizeLabel.Value = slider.Value + " px";
                sizeLabel.Invalidate();
                var h = SizeChangedByUser; if (h != null) h(slider.Value);
            };
            Add(slider);

            Add(new SectionLabel("Rotate and flip"));
            rotL = Transform(Ico.RotateLeft, "Rotate left (Ctrl+Shift+R)", RotateFlipType.Rotate270FlipNone);
            rotR = Transform(Ico.RotateRight, "Rotate right (Ctrl+R)", RotateFlipType.Rotate90FlipNone);
            flipH = Transform(Ico.FlipH, "Flip horizontal", RotateFlipType.RotateNoneFlipX);
            flipV = Transform(Ico.FlipV, "Flip vertical", RotateFlipType.RotateNoneFlipY);
            AddRow(rotL, rotR, flipH, flipV);

            Add(new SectionLabel("History"));
            undo = Button(Ico.Undo, "Undo", "Undo (Ctrl+Z)", "undo");
            redo = Button(Ico.Redo, "Redo", "Redo (Ctrl+Y)", "redo");
            AddPair(undo, redo);
            clearInk = Button(Ico.Trash, "Clear all ink", "Remove every pen and highlighter stroke", "clear");
            Add(clearInk, true);

            Add(new SectionLabel("Paste"));
            paste = Button(Ico.Paste, "Paste", "Paste a picture or snip as a layer you can move and resize (Ctrl+V)", "paste");
            pasteNew = Button(Ico.Plus, "As new", "Paste as a new picture (Ctrl+Alt+V)", "pastenew");
            AddPair(paste, pasteNew);

            Add(new SectionLabel("Keep a copy"));
            copy = Button(Ico.Copy, "Copy", "Copy the edited picture (Ctrl+C)", "copy");
            save = Button(Ico.Save, "Save as…", "Save the edited picture as a new file (Ctrl+S). Your original is never changed.", "save");
            save.Primary = true;
            AddPair(copy, save);

            ApplyState();
        }

        sealed class TitleLabel : Control
        {
            public TitleLabel(string t)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Text = t;
                Height = Theme.S(30);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var p = Theme.Current;
                e.Graphics.Clear(Parent != null ? Parent.BackColor : p.Panel);
                TextRenderer.DrawText(e.Graphics, Text, Theme.DisplayFont(13f), ClientRectangle, p.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        void Add(Control c, bool full = true)
        {
            order.Add(c);
            if (full) fullRow.Add(c);
            Controls.Add(c);
        }

        void AddPair(Control a, Control b) { AddRow(a, b); }

        void AddRow(params Control[] cs)
        {
            rows.Add(cs);
            foreach (var c in cs) { Controls.Add(c); }
            order.Add(new RowMarker(rows.Count - 1));
        }

        sealed class RowMarker : Control { public readonly int Index; public RowMarker(int i) { Index = i; Visible = false; } }

        ThemedButton Tool(EditTool t, Ico icon, string text, string tip)
        {
            var b = new ThemedButton { Icon = icon, Text = text, Tip = tip };
            b.Click += (s, e) => { var h = ToolSelected; if (h != null) h(t); };
            tools[t] = b;
            return b;
        }

        ThemedButton Button(Ico icon, string text, string tip, string cmd)
        {
            var b = new ThemedButton { Icon = icon, Text = text, Tip = tip };
            b.Click += (s, e) => { var h = CommandRequested; if (h != null) h(cmd); };
            return b;
        }

        ThemedButton Transform(Ico icon, string tip, RotateFlipType t)
        {
            var b = new ThemedButton { Icon = icon, IconOnly = true, Tip = tip, IconSize = 20f };
            b.Click += (s, e) => { var h = TransformRequested; if (h != null) h(t); };
            return b;
        }

        // ------------------------------------------------------------------ state from MainForm

        public void SetState(bool picture, EditTool current, Color[] palette, Color color, int size, int maxSize, bool canUndo, bool canRedo, bool hasInk)
        {
            hasPicture = picture;
            tool = current;
            if (palette != null)
            {
                swatches.Colors = palette;
                swatches.Selected = color;
            }
            slider.Maximum = maxSize;
            slider.SetSilently(size);
            sizeLabel.Value = size + " px";
            undo.Enabled = picture && canUndo;
            redo.Enabled = picture && canRedo;
            clearInk.Enabled = picture && hasInk;
            ApplyState();
        }

        void ApplyState()
        {
            foreach (var kv in tools)
            {
                kv.Value.Checked = hasPicture && kv.Key == tool;
                kv.Value.Enabled = hasPicture;
            }
            bool drawing = tool == EditTool.Pen || tool == EditTool.Pencil || tool == EditTool.Highlighter;
            bool sized = drawing || tool == EditTool.Eraser;
            bool crop = tool == EditTool.Crop;
            hint.Visible = !hasPicture;
            colourLabel.Visible = swatches.Visible = drawing;
            sizeLabel.Visible = slider.Visible = sized;
            cropHint.Visible = crop && hasPicture;
            applyCrop.Visible = cancelCrop.Visible = crop;
            swatches.Enabled = slider.Enabled = hasPicture;
            applyCrop.Enabled = cancelCrop.Enabled = hasPicture;
            rotL.Enabled = rotR.Enabled = flipH.Enabled = flipV.Enabled = hasPicture;
            copy.Enabled = save.Enabled = hasPicture;
            PerformLayout();
            Invalidate(true);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (order == null || rows == null) return;
            int pad = Theme.S(16);
            int gap = Theme.S(6);
            int w = ClientSize.Width - pad * 2;
            int y = Theme.S(10) + AutoScrollPosition.Y;
            int bh = Theme.S(34);
            foreach (var c in order)
            {
                var rm = c as RowMarker;
                if (rm != null)
                {
                    var row = rows[rm.Index];
                    var vis = Array.FindAll(row, x => x.Visible);
                    if (vis.Length == 0) continue;
                    int cw = (w - gap * (vis.Length - 1)) / vis.Length;
                    for (int i = 0; i < vis.Length; i++)
                        vis[i].SetBounds(pad + i * (cw + gap), y, cw, bh);
                    y += bh + gap;
                    continue;
                }
                if (!c.Visible) continue;
                int h = c.Height;
                if (c is SectionLabel) { y += Theme.S(4); h = Theme.S(20); }
                else if (c is HintLabel) h = ((HintLabel)c).MeasureHeight(w);
                else if (c is SwatchPicker) { c.Width = w; h = ((SwatchPicker)c).PreferredHeight; }
                else if (c is ThemedButton) h = bh;
                c.SetBounds(pad, y, w, h);
                y += h + gap;
            }
            int total = y - AutoScrollPosition.Y + Theme.S(10);
            var min = new Size(0, total);
            if (AutoScrollMinSize != min) AutoScrollMinSize = min;
        }
    }
}
