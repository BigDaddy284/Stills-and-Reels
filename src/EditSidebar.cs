using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>What the sidebar needs to know to show the right buttons. MainForm fills one in after every change.</summary>
    internal sealed class SidebarState
    {
        public bool Picture;
        public EditTool Tool = EditTool.Pen;
        public Color[] Palette;
        public Color Color = Color.Black;
        public int Size = 4, MaxSize = 30;
        public bool CanUndo, CanRedo, HasInk, HasSelection, Floating;
        public string UndoName, RedoName;
        public PicObject Selected;
        public PicState Doc;
        public int ActiveLayer;
    }

    /// <summary>
    /// Left-hand sidebar for picture Edit mode, in three tabs: Tools, Adjust and Layers. It only raises events;
    /// MainForm owns the edit session and applies them with the same code the menus use.
    /// </summary>
    internal sealed class EditSidebar : ThemedPanel
    {
        public event Action<EditTool> ToolSelected;
        public event Action<Color> ColorPicked;
        public event Action<Color> FillPicked;                 // Color.Empty: no fill
        public event Action<int> SizeChangedByUser;
        public event Action<RotateFlipType> TransformRequested;
        /// <summary>undo, redo, clear, copy, save, applycrop, cancelcrop, sel.*, obj.*, adjust.*, pic.*, layer.*, crop:&lt;w&gt;:&lt;h&gt; ...</summary>
        public event Action<string> CommandRequested;
        public event Action OptionsChanged;                    // a tool option (tolerance, eraser mode, ...) was changed here
        public event Action AdjustChanged;                     // an Adjust slider moved
        public event Action<int> LayerSelected;
        public event Action<int> LayerVisibilityToggled;
        public event Action<int> LayerOpacityChanged;          // percent, while dragging

        /// <summary>The tool options the sidebar edits (the canvas reads the same object).</summary>
        public ToolOptions Options = new ToolOptions();
        /// <summary>The Adjust sliders as they stand.</summary>
        public readonly PicAdjustments Adjust = new PicAdjustments();

        sealed class Item
        {
            public Control[] Cells;
            public int Tab;                 // -1: every tab
            public Func<bool> When;
            public int Height;              // 0: by kind of control
            public bool Square;             // a row of square icon buttons
        }

        readonly List<Item> items = new List<Item>();
        readonly Dictionary<EditTool, ThemedButton> tools = new Dictionary<EditTool, ThemedButton>();
        SidebarState st = new SidebarState();
        int tab;
        bool laying;

        readonly SegmentBar tabs;
        readonly HintLabel hint, toolHint;
        readonly SectionLabel colourLabel, sizeLabel, fillLabel, toleranceLabel, strengthLabel;
        readonly SwatchPicker swatches, fillSwatches;
        readonly ThemedSlider slider, tolerance, strength;
        readonly ThemedToggle fillToggle, erasePixels, contiguous, allLayers;
        readonly ThemedButton pixelate, blur, fontButton, bold, italic;
        readonly ThemedButton undo, redo, clearInk, copy, save, paste, pasteNew;
        readonly List<ThemedButton> needPicture = new List<ThemedButton>();
        readonly List<SliderRow> adjustRows = new List<SliderRow>();
        readonly ThemedButton adjustApply, adjustReset;
        readonly LayerList layerList;
        readonly SliderRow layerOpacity;
        readonly ThemedButton layerBlend, layerUp, layerDown, layerDelete, layerMerge, layerFlatten;

        static bool IsShape(EditTool t) { return t == EditTool.Line || t == EditTool.Arrow || t == EditTool.Rect || t == EditTool.RoundRect || t == EditTool.Ellipse; }
        static bool IsBoxShape(EditTool t) { return t == EditTool.Rect || t == EditTool.RoundRect || t == EditTool.Ellipse; }
        static bool IsSelectTool(EditTool t) { return t == EditTool.RectSelect || t == EditTool.EllipseSelect || t == EditTool.Lasso || t == EditTool.Wand; }
        static bool IsInk(EditTool t) { return t == EditTool.Pen || t == EditTool.Pencil || t == EditTool.Highlighter; }

        bool HasPic { get { return st.Picture; } }
        bool UsesColour { get { return st.Selected != null || IsInk(st.Tool) || IsShape(st.Tool) || st.Tool == EditTool.Text || st.Tool == EditTool.Step || st.Tool == EditTool.Fill; } }
        bool UsesSize
        {
            get
            {
                if (st.Selected != null) return st.Selected.HasWidth;
                return IsInk(st.Tool) || st.Tool == EditTool.Eraser || IsShape(st.Tool) || st.Tool == EditTool.Text || st.Tool == EditTool.Step;
            }
        }
        bool UsesFill { get { return st.Selected != null ? st.Selected.HasFill : IsBoxShape(st.Tool) || st.Tool == EditTool.Text; } }
        bool IsTextish { get { return st.Selected is TextObject || (st.Selected == null && st.Tool == EditTool.Text); } }

        public EditSidebar()
        {
            Surface = Surface.Panel;
            BorderRight = true;
            Width = Theme.S(272);
            AutoScroll = true;

            tabs = new SegmentBar(new[] { "Tools", "Adjust", "Layers" }, new[] { Ico.Pen, Ico.Sliders, Ico.Layers });
            tabs.SelectedChanged += (s, e) => { tab = tabs.Selected; Relayout(); if (tab == 2) layerList.Invalidate(); };
            Add(-1, null, tabs);
            hint = new HintLabel { Text = "Select a picture in File Explorer, use File > Open, or paste a picture or snip (Ctrl+V) to start editing." };
            Add(-1, () => !HasPic, hint);

            // ------------------------------------------------------------ Tools tab
            Square(0, Tool(EditTool.Select, Ico.Pointer, "Select and move: click a shape, text or pasted picture to move or resize it (V)"),
                      Tool(EditTool.RectSelect, Ico.RectSelect, "Box selection: drag over the part to work on (M)"),
                      Tool(EditTool.EllipseSelect, Ico.EllipseSelect, "Oval selection"),
                      Tool(EditTool.Lasso, Ico.Lasso, "Freehand selection: draw around the part to work on (L)"),
                      Tool(EditTool.Wand, Ico.Wand, "Magic wand: click to select an area of similar colour (W)"),
                      Tool(EditTool.Crop, Ico.Crop, "Crop (C)"));
            Square(0, Tool(EditTool.Pen, Ico.Pen, "Pen (P)"),
                      Tool(EditTool.Pencil, Ico.PencilTool, "Pencil"),
                      Tool(EditTool.Highlighter, Ico.Highlighter, "Highlighter (H)"),
                      Tool(EditTool.Eraser, Ico.Eraser, "Eraser (E)"),
                      Tool(EditTool.Fill, Ico.Bucket, "Fill: pours colour into an area (G)"),
                      Tool(EditTool.Picker, Ico.Picker, "Colour picker: click the picture to take a colour (I). Alt+click does the same with any drawing tool."));
            Square(0, Tool(EditTool.Line, Ico.LineTool, "Line. Hold Shift for straight angles."),
                      Tool(EditTool.Arrow, Ico.ArrowTool, "Arrow (A)"),
                      Tool(EditTool.Rect, Ico.RectTool, "Rectangle (R). Hold Shift for a square."),
                      Tool(EditTool.RoundRect, Ico.RoundRectTool, "Rounded rectangle"),
                      Tool(EditTool.Ellipse, Ico.EllipseTool, "Oval (O). Hold Shift for a circle."),
                      Tool(EditTool.Text, Ico.Text, "Text (T)"));
            Square(0, Tool(EditTool.Step, Ico.StepTool, "Numbered step: each click adds the next number"),
                      Tool(EditTool.Redact, Ico.Redact, "Hide: drag over something to pixelate or blur it for good"),
                      Tool(EditTool.Pan, Ico.Hand, "Hand: drag to move around the picture (or hold Space with any tool)"),
                      new Spacer(), new Spacer(), new Spacer());
            toolHint = new HintLabel();
            Add(0, () => HasPic, toolHint);

            // a pasted picture waiting to be placed
            var place = Button(Ico.Check, "Place", "Place the pasted picture here (Enter)", "paste.place"); place.Primary = true;
            Row(0, () => st.Floating, place, Button(Ico.Close, "Cancel", "Take the pasted picture out again (Esc)", "paste.cancel"));

            // crop
            Add(0, () => HasPic && st.Tool == EditTool.Crop, new SectionLabel("Shape"));
            Row(0, () => HasPic && st.Tool == EditTool.Crop, Aspect("Free", 0, 0), Aspect("Original", -1, -1), Aspect("1:1", 1, 1), Aspect("4:3", 4, 3));
            Row(0, () => HasPic && st.Tool == EditTool.Crop, Aspect("3:2", 3, 2), Aspect("16:9", 16, 9), Aspect("3:4", 3, 4), Aspect("9:16", 9, 16));
            var applyCrop = Button(Ico.Check, "Apply crop", "Apply crop (Enter)", "applycrop"); applyCrop.Primary = true;
            Row(0, () => st.Tool == EditTool.Crop && HasPic, applyCrop, Button(Ico.Close, "Cancel", "Cancel crop (Esc)", "cancelcrop"));

            // colour, fill, size
            colourLabel = new SectionLabel("Colour");
            Add(0, () => HasPic && UsesColour && !st.Floating, colourLabel);
            swatches = new SwatchPicker();
            swatches.ColorPicked += (s, e) => { var h = ColorPicked; if (h != null) h(e.Color); };
            Add(0, () => HasPic && UsesColour && !st.Floating, swatches);

            fillToggle = new ThemedToggle { Text = "Fill the shape" };
            fillToggle.CheckedChanged += (s, e) =>
            {
                if (syncing) return;
                var c = fillToggle.Checked ? (lastFill.IsEmpty ? Color.White : lastFill) : Color.Empty;
                var h = FillPicked; if (h != null) h(c);
            };
            Add(0, () => HasPic && UsesFill && !st.Floating, fillToggle);
            fillLabel = new SectionLabel("Fill colour");
            fillSwatches = new SwatchPicker();
            fillSwatches.ColorPicked += (s, e) => { lastFill = e.Color; var h = FillPicked; if (h != null) h(e.Color); };
            Add(0, () => HasPic && UsesFill && fillToggle.Checked && !st.Floating, fillLabel);
            Add(0, () => HasPic && UsesFill && fillToggle.Checked && !st.Floating, fillSwatches);

            sizeLabel = new SectionLabel("Size");
            Add(0, () => HasPic && UsesSize && !st.Floating, sizeLabel);
            slider = new ThemedSlider { Minimum = 1, Maximum = 30 };
            slider.ValueChanged += (s, e) =>
            {
                sizeLabel.Value = slider.Value + " px";
                sizeLabel.Invalidate();
                var h = SizeChangedByUser; if (h != null) h(slider.Value);
            };
            Add(0, () => HasPic && UsesSize && !st.Floating, slider);

            // text
            fontButton = Button(Ico.Text, "Segoe UI", "Choose the font", "font");
            fontButton.AlignLeft = true;
            bold = new ThemedButton { Icon = Ico.Bold, IconOnly = true, Tip = "Bold" };
            bold.Click += (s, e) => { Command(bold.Checked ? "font.bold:0" : "font.bold:1"); };
            italic = new ThemedButton { Icon = Ico.Italic, IconOnly = true, Tip = "Italic" };
            italic.Click += (s, e) => { Command(italic.Checked ? "font.italic:0" : "font.italic:1"); };
            Row(0, () => HasPic && IsTextish && !st.Floating, new[] { 6, 2, 2 }, fontButton, bold, italic);

            // a selected shape or text box
            Add(0, () => HasPic && st.Selected != null, new SectionLabel("Selected item"));
            Row(0, () => HasPic && st.Selected != null,
                IconButton(Ico.Duplicate, "Duplicate (Ctrl+D)", "obj.duplicate"), IconButton(Ico.ToFront, "Bring to front", "obj.front"),
                IconButton(Ico.ToBack, "Send to back", "obj.back"), IconButton(Ico.Trash, "Delete (Del)", "obj.delete"));

            // eraser
            erasePixels = new ThemedToggle { Text = "Rub out pixels" };
            erasePixels.CheckedChanged += (s, e) => { if (syncing) return; Options.ErasePixels = erasePixels.Checked; RaiseOptions(); Relayout(); };
            Add(0, () => HasPic && st.Tool == EditTool.Eraser, erasePixels);

            // wand and fill
            toleranceLabel = new SectionLabel("Tolerance");
            tolerance = new ThemedSlider { Minimum = 0, Maximum = 255 };
            tolerance.ValueChanged += (s, e) => { Options.Tolerance = tolerance.Value; toleranceLabel.Value = tolerance.Value.ToString(); toleranceLabel.Invalidate(); RaiseOptions(); };
            contiguous = new ThemedToggle { Text = "Touching areas only" };
            contiguous.CheckedChanged += (s, e) => { if (syncing) return; Options.Contiguous = contiguous.Checked; RaiseOptions(); };
            allLayers = new ThemedToggle { Text = "Look at all layers" };
            allLayers.CheckedChanged += (s, e) => { if (syncing) return; Options.SampleAllLayers = allLayers.Checked; RaiseOptions(); };
            Func<bool> wandish = () => HasPic && (st.Tool == EditTool.Wand || st.Tool == EditTool.Fill);
            Add(0, wandish, toleranceLabel);
            Add(0, wandish, tolerance);
            Add(0, wandish, contiguous);
            Add(0, () => wandish() && st.Doc != null && st.Doc.Layers.Length > 1, allLayers);

            // hide (redact)
            pixelate = new ThemedButton { Icon = Ico.Redact, Text = "Pixelate", Tip = "Turn the area into big blocks" };
            pixelate.Click += (s, e) => { Options.RedactPixelate = true; RaiseOptions(); SyncOptions(); };
            blur = new ThemedButton { Icon = Ico.Filter, Text = "Blur", Tip = "Blur the area" };
            blur.Click += (s, e) => { Options.RedactPixelate = false; RaiseOptions(); SyncOptions(); };
            Row(0, () => HasPic && st.Tool == EditTool.Redact, pixelate, blur);
            strengthLabel = new SectionLabel("Strength");
            strength = new ThemedSlider { Minimum = 3, Maximum = 60 };
            strength.ValueChanged += (s, e) => { Options.RedactStrength = strength.Value; strengthLabel.Value = strength.Value.ToString(); strengthLabel.Invalidate(); RaiseOptions(); };
            Add(0, () => HasPic && st.Tool == EditTool.Redact, strengthLabel);
            Add(0, () => HasPic && st.Tool == EditTool.Redact, strength);

            // selection
            Func<bool> selecting = () => HasPic && (IsSelectTool(st.Tool) || st.HasSelection);
            Add(0, selecting, new SectionLabel("Selection"));
            Row(0, selecting, Button(Ico.None, "All", "Select everything (Ctrl+A)", "sel.all"), SelButton(Ico.None, "None", "Remove the selection (Ctrl+Shift+A or Esc)", "sel.none"),
                              SelButton(Ico.None, "Invert", "Select everything else (Ctrl+Shift+I)", "sel.invert"));
            Row(0, () => HasPic && st.HasSelection, SelButton(Ico.None, "Crop to", "Crop the picture to the selection", "sel.crop"),
                                                   SelButton(Ico.None, "Soften", "Soften the edge of the selection", "sel.feather"),
                                                   SelButton(Ico.None, "Clear", "Clear the selected area on this layer (Del)", "sel.delete"));
            Row(0, () => HasPic && st.HasSelection, SelButton(Ico.Copy, "Copy", "Copy the selected area (Ctrl+C)", "sel.copy"), SelButton(Ico.Scissors, "Cut", "Cut the selected area (Ctrl+X)", "sel.cut"));

            // always there
            Add(0, null, new SectionLabel("History"));
            undo = Button(Ico.Undo, "Undo", "Undo (Ctrl+Z)", "undo");
            redo = Button(Ico.Redo, "Redo", "Redo (Ctrl+Y)", "redo");
            Row(0, null, undo, redo);
            clearInk = Button(Ico.Trash, "Clear all drawings", "Remove every stroke, shape and text box", "clear");
            Add(0, () => st.HasInk, clearInk);

            Add(0, null, new SectionLabel("Paste"));
            paste = Button(Ico.Paste, "Paste", "Paste a picture or snip as a new layer you can move and resize (Ctrl+V)", "paste");
            pasteNew = Button(Ico.Plus, "As new", "Paste as a new picture (Ctrl+Alt+V)", "pastenew");
            Row(0, null, paste, pasteNew);

            copy = Button(Ico.Copy, "Copy", "Copy the edited picture (Ctrl+C)", "copy");
            save = Button(Ico.Save, "Save as…", "Save the edited picture as a new file (Ctrl+S). Your original is never changed.", "save");
            save.Primary = true;
            needPicture.Add(copy); needPicture.Add(save);
            // (added at the very end so it sits at the bottom of every tab)

            // ------------------------------------------------------------ Adjust tab
            var auto = Button(Ico.Sparkle, "Auto fix", "Balance brightness and contrast automatically", "adjust.auto");
            needPicture.Add(auto);
            var looks = Button(Ico.Filter, "Filters", "One-click looks: black and white, sepia, vivid and more", "adjust.looks");
            needPicture.Add(looks);
            Row(1, null, auto, looks);
            Add(1, null, new SectionLabel("Light"));
            AdjustRow("Exposure", "Overall brightness, like a camera's exposure", v => Adjust.Exposure = v, () => Adjust.Exposure);
            AdjustRow("Brightness", "Lifts or lowers the midtones", v => Adjust.Brightness = v, () => Adjust.Brightness);
            AdjustRow("Contrast", "The difference between light and dark", v => Adjust.Contrast = v, () => Adjust.Contrast);
            AdjustRow("Highlights", "The brightest parts only", v => Adjust.Highlights = v, () => Adjust.Highlights);
            AdjustRow("Shadows", "The darkest parts only", v => Adjust.Shadows = v, () => Adjust.Shadows);
            Add(1, null, new SectionLabel("Colour"));
            AdjustRow("Saturation", "How strong all colours are", v => Adjust.Saturation = v, () => Adjust.Saturation);
            AdjustRow("Vibrance", "Boosts dull colours more than vivid ones", v => Adjust.Vibrance = v, () => Adjust.Vibrance);
            AdjustRow("Warmth", "Cooler (blue) to warmer (orange)", v => Adjust.Temperature = v, () => Adjust.Temperature);
            AdjustRow("Tint", "Green to magenta", v => Adjust.Tint = v, () => Adjust.Tint);
            Add(1, null, new SectionLabel("Detail"));
            AdjustRow("Sharpen", "Makes edges crisper", v => Adjust.Sharpen = v, () => Adjust.Sharpen, 0);
            AdjustRow("Blur", "Softens the picture", v => Adjust.Blur = v, () => Adjust.Blur, 0);
            AdjustRow("Vignette", "Darkens (or lightens) the corners", v => Adjust.Vignette = v, () => Adjust.Vignette);
            adjustApply = Button(Ico.Check, "Apply", "Keep these changes (Enter)", "adjust.apply"); adjustApply.Primary = true;
            adjustReset = Button(Ico.Close, "Reset", "Put every slider back to the middle (Esc)", "adjust.reset");
            Row(1, null, adjustApply, adjustReset);
            var levels = Button(Ico.Sliders, "Levels…", "Set the black point, white point and midtones", "adjust.levels");
            var curves = Button(Ico.Curves, "Curves…", "Shape the brightness curve by hand", "adjust.curves");
            needPicture.Add(levels); needPicture.Add(curves);
            Row(1, null, levels, curves);

            Add(1, null, new SectionLabel("Size and shape"));
            Row(1, null, Transform(Ico.RotateLeft, "Rotate left (Ctrl+Shift+R)", RotateFlipType.Rotate270FlipNone),
                         Transform(Ico.RotateRight, "Rotate right (Ctrl+R)", RotateFlipType.Rotate90FlipNone),
                         Transform(Ico.FlipH, "Flip horizontal", RotateFlipType.RotateNoneFlipX),
                         Transform(Ico.FlipV, "Flip vertical", RotateFlipType.RotateNoneFlipY));
            var resize = Button(Ico.Resize, "Resize…", "Change the size of the picture in pixels or percent (Ctrl+Alt+I)", "pic.resize");
            var canvasSize = Button(Ico.CanvasSize, "Canvas…", "Add or remove space around the picture (Ctrl+Alt+C)", "pic.canvas");
            var straighten = Button(Ico.Straighten, "Straighten…", "Turn the picture by a few degrees to level it", "pic.straighten");
            var trim = Button(Ico.Trim, "Trim edges", "Cut off plain borders around the picture", "pic.trim");
            needPicture.Add(resize); needPicture.Add(canvasSize); needPicture.Add(straighten); needPicture.Add(trim);
            Row(1, null, resize, canvasSize);
            Row(1, null, straighten, trim);

            // ------------------------------------------------------------ Layers tab
            layerList = new LayerList();
            layerList.LayerClicked += i => { var h = LayerSelected; if (h != null) h(i); };
            layerList.EyeClicked += i => { var h = LayerVisibilityToggled; if (h != null) h(i); };
            layerList.RenameRequested += i => Command("layer.rename");
            Add(2, null, layerList);
            layerOpacity = new SliderRow("Opacity", 0, 100, 100) { Tip = "How see-through the layer is" };
            layerOpacity.ValueChanged += (s, e) => { if (syncing) return; var h = LayerOpacityChanged; if (h != null) h(layerOpacity.Value); };
            Add(2, null, layerOpacity);
            layerBlend = Button(Ico.Filter, "Blend: Normal", "How this layer mixes with the layers below it", "layer.blend");
            layerBlend.AlignLeft = true;
            Add(2, null, layerBlend);
            layerUp = IconButton(Ico.ArrowUp, "Move the layer up", "layer.up");
            layerDown = IconButton(Ico.ArrowDown, "Move the layer down", "layer.down");
            layerDelete = IconButton(Ico.Trash, "Delete the layer", "layer.delete");
            Row(2, null, IconButton(Ico.Plus, "Add an empty layer", "layer.add"), IconButton(Ico.Duplicate, "Duplicate the layer", "layer.dup"), layerUp, layerDown, layerDelete);
            layerMerge = Button(Ico.MergeDown, "Merge down", "Mix this layer into the one below it", "layer.merge");
            layerFlatten = Button(Ico.Layers, "Flatten", "Mix all layers into one", "layer.flatten");
            Row(2, null, layerMerge, layerFlatten);
            var saveLayers = Button(Ico.Save, "Save with layers…", "Save a picture file that keeps every layer, shape and text box, so you can carry on editing later (.srpic)", "save.layers");
            needPicture.Add(saveLayers);
            Add(2, null, saveLayers);
            Add(2, null, new HintLabel { Text = "Each paste arrives as its own layer. Shapes and text stay editable on their layer. \"Save with layers\" keeps all of that for later; other kinds of picture file are saved flat." });

            Add(-1, null, new SectionLabel("Keep a copy"));
            Row(-1, null, copy, save);

            SyncOptions();
            built = true;
            Relayout();
        }

        Color lastFill = Color.Empty;
        bool syncing, built;

        // ------------------------------------------------------------------ building blocks

        sealed class Spacer : Control { public Spacer() { SetStyle(ControlStyles.Selectable, false); Enabled = false; } protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Current.Panel); } }

        void Add(int tabIndex, Func<bool> when, Control c)
        {
            if (c == null) return;
            items.Add(new Item { Cells = new[] { c }, Tab = tabIndex, When = when });
            Controls.Add(c);
        }

        void Row(int tabIndex, Func<bool> when, params Control[] cs) { Row(tabIndex, when, null, cs); }

        readonly Dictionary<Item, int[]> weights = new Dictionary<Item, int[]>();

        void Row(int tabIndex, Func<bool> when, int[] weight, params Control[] cs)
        {
            var it = new Item { Cells = cs, Tab = tabIndex, When = when };
            items.Add(it);
            if (weight != null) weights[it] = weight;
            foreach (var c in cs) Controls.Add(c);
        }

        void Square(int tabIndex, params Control[] cs)
        {
            items.Add(new Item { Cells = cs, Tab = tabIndex, Square = true });
            foreach (var c in cs) Controls.Add(c);
        }

        void Command(string cmd) { var h = CommandRequested; if (h != null) h(cmd); }
        void RaiseOptions() { var h = OptionsChanged; if (h != null) h(); }

        ThemedButton Tool(EditTool t, Ico icon, string tip)
        {
            var b = new ThemedButton { Icon = icon, IconOnly = true, Tip = tip, IconSize = 19f };
            b.Click += (s, e) => { var h = ToolSelected; if (h != null) h(t); };
            tools[t] = b;
            return b;
        }

        ThemedButton Button(Ico icon, string text, string tip, string cmd)
        {
            var b = new ThemedButton { Icon = icon, Text = text, Tip = tip };
            b.Click += (s, e) => Command(cmd);
            return b;
        }

        ThemedButton SelButton(Ico icon, string text, string tip, string cmd) { return Button(icon, text, tip, cmd); }

        ThemedButton IconButton(Ico icon, string tip, string cmd)
        {
            var b = new ThemedButton { Icon = icon, IconOnly = true, Tip = tip, IconSize = 18f };
            b.Click += (s, e) => Command(cmd);
            return b;
        }

        readonly List<ThemedButton> aspectButtons = new List<ThemedButton>();

        ThemedButton Aspect(string text, int w, int h)
        {
            var b = new ThemedButton { Text = text, Tip = w == 0 ? "Any shape" : w < 0 ? "The picture's own proportions" : "Keep the crop at " + w + " to " + h, Tag = w + ":" + h };
            b.Click += (s, e) => { foreach (var o in aspectButtons) o.Checked = ReferenceEquals(o, b); Command("crop:" + w + ":" + h); };
            b.Checked = w == 0;
            aspectButtons.Add(b);
            return b;
        }

        ThemedButton Transform(Ico icon, string tip, RotateFlipType t)
        {
            var b = new ThemedButton { Icon = icon, IconOnly = true, Tip = tip, IconSize = 20f };
            b.Click += (s, e) => { var h = TransformRequested; if (h != null) h(t); };
            needPicture.Add(b);
            return b;
        }

        void AdjustRow(string name, string tip, Action<int> set, Func<int> get, int min = -100)
        {
            var row = new SliderRow(name, min, 100, 0) { Tip = tip };
            row.ValueChanged += (s, e) =>
            {
                set(row.Value);
                if (syncing) return;
                SyncAdjustButtons();
                var h = AdjustChanged; if (h != null) h();
            };
            row.Tag = get;
            adjustRows.Add(row);
            Add(1, null, row);
        }

        void SyncAdjustButtons()
        {
            bool any = !Adjust.IsIdentity;
            adjustApply.Enabled = adjustReset.Enabled = any && HasPic;
        }

        /// <summary>Puts every Adjust slider back to the middle (without raising AdjustChanged).</summary>
        public void ResetAdjust()
        {
            syncing = true;
            foreach (var r in adjustRows) r.Value = 0;
            Adjust.CurveR = Adjust.CurveG = Adjust.CurveB = null;
            Adjust.Grayscale = Adjust.Invert = false; Adjust.Sepia = Adjust.Fade = 0;
            syncing = false;
            SyncAdjustButtons();
        }

        /// <summary>Shows a set of adjustments on the sliders (a look picked from Filters), without raising AdjustChanged.</summary>
        public void ShowAdjust(PicAdjustments a)
        {
            syncing = true;
            Adjust.Exposure = a.Exposure; Adjust.Brightness = a.Brightness; Adjust.Contrast = a.Contrast; Adjust.Highlights = a.Highlights; Adjust.Shadows = a.Shadows;
            Adjust.Saturation = a.Saturation; Adjust.Vibrance = a.Vibrance; Adjust.Temperature = a.Temperature; Adjust.Tint = a.Tint;
            Adjust.Sharpen = a.Sharpen; Adjust.Blur = a.Blur; Adjust.Vignette = a.Vignette; Adjust.Sepia = a.Sepia; Adjust.Fade = a.Fade;
            Adjust.Grayscale = a.Grayscale; Adjust.Invert = a.Invert; Adjust.CurveR = a.CurveR; Adjust.CurveG = a.CurveG; Adjust.CurveB = a.CurveB;
            foreach (var r in adjustRows) r.Value = ((Func<int>)r.Tag)();
            syncing = false;
            SyncAdjustButtons();
        }

        public int Tab { get { return tab; } set { tabs.Selected = Math.Max(0, Math.Min(2, value)); } }

        /// <summary>The button a pop-up menu should hang from.</summary>
        public Control BlendAnchor { get { return layerBlend; } }

        // ------------------------------------------------------------------ state from MainForm

        public void SetState(SidebarState state)
        {
            st = state ?? new SidebarState();
            syncing = true;
            if (st.Palette != null)
            {
                swatches.Colors = st.Palette;
                swatches.Selected = st.Selected != null ? Color.FromArgb(255, st.Selected.Color) : st.Color;
                fillSwatches.Colors = Palettes.Colors(Palettes.Ink);
            }
            slider.Maximum = st.MaxSize;
            slider.SetSilently(st.Size);
            sizeLabel.Text = IsTextish ? "Text size" : st.Tool == EditTool.Step && st.Selected == null ? "Marker size" : IsShape(st.Tool) || st.Selected is ShapeObject ? "Line thickness" : "Size";
            sizeLabel.Value = st.Size + " px";
            Color fill = st.Selected != null ? st.Selected.Fill : Options.Fill;
            fillToggle.Text = IsTextish ? "Background behind the text" : "Fill the shape";
            fillToggle.Checked = !fill.IsEmpty;
            if (!fill.IsEmpty) { lastFill = fill; fillSwatches.Selected = Color.FromArgb(255, fill); }
            var text = st.Selected as TextObject;
            fontButton.Text = text != null ? text.FontName : Options.FontName;
            bold.Checked = text != null ? text.Bold : Options.Bold;
            italic.Checked = text != null ? text.Italic : Options.Italic;
            undo.Enabled = st.Picture && st.CanUndo;
            redo.Enabled = st.Picture && st.CanRedo;
            undo.Tip = st.CanUndo && st.UndoName != null ? "Undo " + st.UndoName + " (Ctrl+Z)" : "Undo (Ctrl+Z)";
            redo.Tip = st.CanRedo && st.RedoName != null ? "Redo " + st.RedoName + " (Ctrl+Y)" : "Redo (Ctrl+Y)";
            clearInk.Enabled = st.Picture && st.HasInk;
            foreach (var b in needPicture) b.Enabled = st.Picture;
            foreach (var r in adjustRows) r.Enabled = st.Picture;
            SyncAdjustButtons();

            var doc = st.Doc;
            layerList.SetLayers(doc, st.ActiveLayer);
            bool layers = doc != null;
            PicLayer active = layers && st.ActiveLayer >= 0 && st.ActiveLayer < doc.Layers.Length ? doc.Layers[st.ActiveLayer] : null;
            layerOpacity.Enabled = layerBlend.Enabled = active != null;
            if (active != null)
            {
                layerOpacity.Value = (int)Math.Round(active.Opacity * 100);
                layerBlend.Text = "Blend: " + PicBlendOps.Name(active.Blend);
            }
            layerUp.Enabled = active != null && st.ActiveLayer < doc.Layers.Length - 1;
            layerDown.Enabled = layerMerge.Enabled = active != null && st.ActiveLayer > 0;
            layerDelete.Enabled = layers && doc.Layers.Length > 1;
            layerFlatten.Enabled = layers && (doc.Layers.Length > 1 || doc.HasObjects);
            syncing = false;
            SyncOptions();
            toolHint.Text = HintFor(st);
            Relayout();
        }

        void SyncOptions()
        {
            syncing = true;
            erasePixels.Checked = Options.ErasePixels;
            tolerance.SetSilently(Options.Tolerance);
            toleranceLabel.Value = Options.Tolerance.ToString();
            contiguous.Checked = Options.Contiguous;
            allLayers.Checked = Options.SampleAllLayers;
            pixelate.Checked = Options.RedactPixelate;
            blur.Checked = !Options.RedactPixelate;
            strength.SetSilently(Options.RedactStrength);
            strengthLabel.Value = Options.RedactStrength.ToString();
            syncing = false;
        }

        static string HintFor(SidebarState s)
        {
            if (s.Floating) return "Drag the pasted picture to move it, drag a corner to resize it. It stays on its own layer.";
            if (s.Selected != null && s.Tool != EditTool.Select) return "Drag the item to move it, drag a handle to resize it. Click elsewhere to draw another.";
            switch (s.Tool)
            {
                case EditTool.Select: return "Select: click a shape, text or pasted picture, then drag it or its handles. Double-click text to change it.";
                case EditTool.RectSelect: case EditTool.EllipseSelect: case EditTool.Lasso:
                    return "Drag to select part of the picture. Shift adds, Alt takes away. Tools and adjustments then work inside it only.";
                case EditTool.Wand: return "Click an area to select everything of a similar colour. Shift adds, Alt takes away.";
                case EditTool.Crop: return "Drag on the picture to choose the area to keep. Drag the handles to adjust.";
                case EditTool.Pen: return "Pen: smooth, solid strokes.";
                case EditTool.Pencil: return "Pencil: thin, slightly rough strokes.";
                case EditTool.Highlighter: return "Highlighter: see-through colour over text.";
                case EditTool.Eraser: return "Eraser: drag over strokes and shapes to remove them, or switch to pixels to rub out part of the picture.";
                case EditTool.Fill: return "Fill: click an area to pour the colour into it.";
                case EditTool.Picker: return "Click anywhere on the picture to take its colour.";
                case EditTool.Line: case EditTool.Arrow: return "Drag to draw. Hold Shift for level, upright and 45° lines.";
                case EditTool.Rect: case EditTool.RoundRect: case EditTool.Ellipse: return "Drag to draw. Hold Shift for equal sides.";
                case EditTool.Text: return "Click where the text should go and type. Click elsewhere when done; click the text again to change it.";
                case EditTool.Step: return "Click to drop numbered markers: 1, 2, 3 …";
                case EditTool.Redact: return "Drag over anything private to hide it. This changes the pixels, so it can't be reversed after saving.";
                case EditTool.Pan: return "Drag to move around the picture. The mouse wheel zooms.";
            }
            return "";
        }

        // ------------------------------------------------------------------ layout

        void Relayout()
        {
            if (laying || !built) return;
            laying = true;
            try
            {
                SuspendLayout();
                foreach (var kv in tools)
                {
                    kv.Value.Checked = st.Picture && kv.Key == st.Tool;
                    kv.Value.Enabled = st.Picture;
                }
                swatches.Enabled = slider.Enabled = st.Picture;

                int pad = Theme.S(14);
                int gap = Theme.S(6);
                int w = ClientSize.Width - pad * 2;
                int y = Theme.S(10) + AutoScrollPosition.Y;
                int bh = Theme.S(34);
                foreach (var it in items)
                {
                    bool show = (it.Tab < 0 || it.Tab == tab) && (it.When == null || it.When());
                    foreach (var c in it.Cells) if (c.Visible != show) c.Visible = show;
                    if (!show) continue;
                    if (it.Cells.Length > 1 || it.Square)
                    {
                        int n = it.Cells.Length;
                        int g2 = it.Square ? Theme.S(4) : gap;
                        int h = it.Square ? Math.Min(Theme.S(38), (w - g2 * (n - 1)) / n) : bh;
                        int[] wt; weights.TryGetValue(it, out wt);
                        int total = 0;
                        if (wt != null) foreach (int k in wt) total += k;
                        int x = pad;
                        for (int i = 0; i < n; i++)
                        {
                            int cw = wt != null ? (w - g2 * (n - 1)) * wt[i] / total : (w - g2 * (n - 1)) / n;
                            if (i == n - 1) cw = pad + w - x;
                            it.Cells[i].SetBounds(x, y, cw, h);
                            x += cw + g2;
                        }
                        y += h + g2;
                        continue;
                    }
                    var one = it.Cells[0];
                    int ch = one.Height;
                    if (one is SectionLabel) { y += Theme.S(2); ch = Theme.S(20); }
                    else if (one is HintLabel) { ch = ((HintLabel)one).MeasureHeight(w); if (string.IsNullOrEmpty(one.Text)) { one.Visible = false; continue; } }
                    else if (one is SwatchPicker) { one.Width = w; ch = ((SwatchPicker)one).PreferredHeight; }
                    else if (one is ThemedButton) ch = bh;
                    else if (one is LayerList) ch = ((LayerList)one).PreferredHeight;
                    else if (one is SegmentBar) ch = Theme.S(36);
                    else if (one is ThemedToggle) ch = Theme.S(30);
                    one.SetBounds(pad, y, w, ch);
                    y += ch + gap;
                }
                int totalH = y - AutoScrollPosition.Y + Theme.S(10);
                var min = new Size(0, totalH);
                if (AutoScrollMinSize != min) AutoScrollMinSize = min;
            }
            finally
            {
                ResumeLayout(false);
                laying = false;
            }
            Invalidate(true);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (items != null && !laying && e.AffectedControl == this) Relayout();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (items != null) Relayout();
        }
    }

    /// <summary>A row of joined buttons where one is always picked (the sidebar's tabs).</summary>
    internal sealed class SegmentBar : Control
    {
        readonly string[] labels;
        readonly Ico[] icons;
        int selected, hover = -1;
        public event EventHandler SelectedChanged;

        public SegmentBar(string[] labels, Ico[] icons)
        {
            this.labels = labels; this.icons = icons;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(36);
            Cursor = Cursors.Hand;
        }

        public int Selected
        {
            get { return selected; }
            set
            {
                if (value == selected || value < 0 || value >= labels.Length) return;
                selected = value; Invalidate();
                var h = SelectedChanged; if (h != null) h(this, EventArgs.Empty);
            }
        }

        int At(Point p) { return Math.Max(0, Math.Min(labels.Length - 1, p.X * labels.Length / Math.Max(1, Width))); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Selected = At(e.Location); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = At(e.Location); if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(Parent != null ? Parent.BackColor : p.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var all = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Theme.Rounded(all, Theme.F(9)))
            {
                using (var b = new SolidBrush(p.Control)) g.FillPath(b, path);
                using (var pen = new Pen(p.ControlBorder)) g.DrawPath(pen, path);
            }
            float cw = (Width - 1f) / labels.Length;
            var font = Theme.UiFont(9.5f, FontStyle.Bold);
            for (int i = 0; i < labels.Length; i++)
            {
                var r = new RectangleF(0.5f + i * cw + Theme.F(3), Theme.F(3.5f), cw - Theme.F(6), Height - Theme.F(7));
                bool on = i == selected;
                if (on)
                    using (var path = Theme.Rounded(r, Theme.F(7)))
                    using (var b = Theme.AccentGradient(r)) g.FillPath(b, path);
                else if (i == hover)
                    using (var path = Theme.Rounded(r, Theme.F(7)))
                    using (var b = new SolidBrush(p.Hover)) g.FillPath(b, path);
                Color c = on ? Color.White : p.Text;
                var sz = TextRenderer.MeasureText(labels[i], font);
                bool hasIcon = icons[i] != Ico.None;
                float ic = hasIcon ? Theme.F(15) : 0f, total = ic + (hasIcon ? Theme.F(5) : 0f) + sz.Width;
                float x = r.Left + (r.Width - total) / 2f;
                if (hasIcon) Icons.Draw(g, icons[i], new RectangleF(x, r.Top + (r.Height - ic) / 2f, ic, ic), c, 1.8f);
                TextRenderer.DrawText(g, labels[i], font, new Rectangle((int)(x + ic + (hasIcon ? Theme.F(4) : 0f)), (int)r.Top, sz.Width + 4, (int)r.Height), c,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
    }

    /// <summary>A named slider on one line: name, slider, value. Double-click puts it back to its starting value.</summary>
    internal sealed class SliderRow : Control
    {
        readonly ThemedSlider slider;
        readonly int start;
        public event EventHandler ValueChanged;

        public SliderRow(string name, int min, int max, int value)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = name;
            start = value;
            Height = Theme.S(26);
            slider = new ThemedSlider { Minimum = min, Maximum = max };
            slider.SetSilently(value);
            slider.ValueChanged += (s, e) => { Invalidate(); var h = ValueChanged; if (h != null) h(this, EventArgs.Empty); };
            slider.DoubleClick += (s, e) => Value = start;
            Controls.Add(slider);
        }

        /// <summary>Show the value divided by ten, with one decimal.</summary>
        public bool Tenths;

        public string Tip { set { Ui.Tip.SetToolTip(this, value); Ui.Tip.SetToolTip(slider, value + " (double-click to reset)"); } }

        public int Value
        {
            get { return slider.Value; }
            set { if (slider.Value != value) slider.Value = value; else Invalidate(); }
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); slider.Enabled = Enabled; Invalidate(); }
        protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); Value = start; }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int label = Theme.S(78), val = Theme.S(34);
            if (slider == null) return;
            slider.SetBounds(label, 0, Math.Max(20, Width - label - val), Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : p.Panel);
            var f = Theme.UiFont(9.5f);
            TextRenderer.DrawText(e.Graphics, Text, f, new Rectangle(0, 0, Theme.S(78), Height), Enabled ? p.Text : p.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            string v = Tenths ? (slider.Value / 10.0).ToString("0.0") : slider.Value.ToString();
            if (slider.Minimum < 0 && slider.Value > 0) v = "+" + v;
            TextRenderer.DrawText(e.Graphics, v, f, new Rectangle(Width - Theme.S(34), 0, Theme.S(34), Height), slider.Value != start ? p.Text : p.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>The list of layers, top layer first: eye, small preview, name.</summary>
    internal sealed class LayerList : Control
    {
        PicState doc;
        int active = -1, hover = -1;
        readonly Dictionary<int, Bitmap> thumbs = new Dictionary<int, Bitmap>();
        PicState thumbsFor;

        public event Action<int> LayerClicked, EyeClicked, RenameRequested;

        public LayerList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        int RowH { get { return Theme.S(42); } }
        public int PreferredHeight { get { return Math.Max(1, doc != null ? doc.Layers.Length : 1) * RowH + Theme.S(4); } }

        public void SetLayers(PicState state, int activeIndex)
        {
            bool changed = !ReferenceEquals(doc, state);
            doc = state; active = activeIndex;
            if (changed && Visible) Invalidate();
            else Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ClearThumbs();
            base.Dispose(disposing);
        }

        void ClearThumbs() { foreach (var b in thumbs.Values) b.Dispose(); thumbs.Clear(); }

        /// <summary>A small picture of one layer, made by sampling its pixels (fast however large the layer is).</summary>
        unsafe Bitmap Thumb(PicLayer l, int size)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var cs = doc.Size;
            float scale = Math.Min(size / (float)cs.Width, size / (float)cs.Height);
            float ox = (size - cs.Width * scale) / 2f, oy = (size - cs.Height * scale) / 2f;
            if (l.Pixels != null)
            {
                var d = bmp.LockBits(new Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    for (int y = 0; y < size; y++)
                    {
                        uint* row = (uint*)((byte*)d.Scan0 + y * d.Stride);
                        int sy = (int)((y + 0.5f - oy) / scale) - l.Y;
                        for (int x = 0; x < size; x++)
                        {
                            int sx = (int)((x + 0.5f - ox) / scale) - l.X;
                            row[x] = sx >= 0 && sy >= 0 && sx < l.Pixels.Width && sy < l.Pixels.Height && (x + 0.5f - ox) >= 0 && (y + 0.5f - oy) >= 0 ? l.Pixels.Row(sy)[sx] : 0u;
                        }
                    }
                }
                finally { bmp.UnlockBits(d); }
            }
            if (l.HasObjects && l.Objects.Length <= 400)
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TranslateTransform(ox, oy);
                    g.ScaleTransform(scale, scale);
                    foreach (var o in l.Objects) o.Draw(g);
                }
            return bmp;
        }

        int RowAt(Point p)
        {
            if (doc == null) return -1;
            int row = p.Y / RowH;
            int index = doc.Layers.Length - 1 - row;
            return index >= 0 && index < doc.Layers.Length ? index : -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = RowAt(e.Location);
            if (i < 0 || e.Button != MouseButtons.Left) return;
            if (e.X < Theme.S(32)) { var h = EyeClicked; if (h != null) h(i); }
            else { var h = LayerClicked; if (h != null) h(i); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = RowAt(e.Location);
            if (i >= 0 && e.X >= Theme.S(32)) { var h = RenameRequested; if (h != null) h(i); }
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = RowAt(e.Location); if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.Current;
            g.Clear(Parent != null ? Parent.BackColor : p.Panel);
            if (doc == null)
            {
                TextRenderer.DrawText(g, "No picture open", Theme.UiFont(9.5f), ClientRectangle, p.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }
            if (!ReferenceEquals(thumbsFor, doc)) { ClearThumbs(); thumbsFor = doc; }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int rh = RowH, ts = Theme.S(32);
            for (int row = 0; row < doc.Layers.Length; row++)
            {
                int i = doc.Layers.Length - 1 - row;
                var l = doc.Layers[i];
                var r = new RectangleF(0.5f, row * rh + 0.5f, Width - 1, rh - Theme.F(4));
                using (var path = Theme.Rounded(r, Theme.F(7)))
                {
                    if (i == active)
                    {
                        using (var b = new SolidBrush(p.Pressed)) g.FillPath(b, path);
                        using (var pen = new Pen(p.AccentA, 1.4f)) g.DrawPath(pen, path);
                    }
                    else if (i == hover) using (var b = new SolidBrush(p.Hover)) g.FillPath(b, path);
                }
                float icon = Theme.F(17);
                Icons.Draw(g, l.Visible ? Ico.Eye : Ico.EyeOff, new RectangleF(Theme.F(8), r.Top + (r.Height - icon) / 2f, icon, icon), l.Visible ? p.Text : p.Muted, 1.7f);
                var tr = new Rectangle(Theme.S(34), (int)(r.Top + (r.Height - ts) / 2f), ts, ts);
                Bitmap th;
                if (!thumbs.TryGetValue(l.Id, out th)) { try { th = Thumb(l, ts); } catch { th = null; } if (th != null) thumbs[l.Id] = th; }
                using (var b = new SolidBrush(p.ThumbBg)) g.FillRectangle(b, tr);
                if (th != null) g.DrawImageUnscaled(th, tr.Location);
                using (var pen = new Pen(p.ControlBorder)) g.DrawRectangle(pen, tr);
                string sub = (l.Opacity < 0.995f ? (int)Math.Round(l.Opacity * 100) + "%  " : "") + (l.Blend != PicBlend.Normal ? PicBlendOps.Name(l.Blend) : "");
                var nameRect = new Rectangle(tr.Right + Theme.S(8), (int)r.Top, Width - tr.Right - Theme.S(12), (int)r.Height);
                if (sub.Trim().Length > 0)
                {
                    TextRenderer.DrawText(g, l.Name, Theme.UiFont(9.5f), new Rectangle(nameRect.X, nameRect.Y + Theme.S(3), nameRect.Width, nameRect.Height / 2), l.Visible ? p.Text : p.Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, sub.Trim(), Theme.UiFont(8.5f), new Rectangle(nameRect.X, nameRect.Y + nameRect.Height / 2 - Theme.S(1), nameRect.Width, nameRect.Height / 2), p.Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
                else
                    TextRenderer.DrawText(g, l.Name, Theme.UiFont(9.5f), nameRect, l.Visible ? p.Text : p.Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }
}
