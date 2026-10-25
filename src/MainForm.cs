using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace StillsAndReels
{
    internal sealed class MainForm : Form
    {
        const string AppName = "Stills & Reels";

        readonly Settings settings = new Settings();
        readonly ExplorerWatcher watcher = new ExplorerWatcher();
        readonly PickupWatcher pickup = new PickupWatcher();
        ClipboardWatcher clipWatch;
        bool pickClicks = true, pickFiles = true, pickCopied = true;
        readonly System.Windows.Forms.Timer clipTimer = new System.Windows.Forms.Timer { Interval = 150 };
        readonly ImageCanvas canvas = new ImageCanvas();
        readonly VideoView videoView = new VideoView();
        readonly ThemedPanel content = new ThemedPanel { Surface = Surface.Canvas, Dock = DockStyle.Fill };
        readonly EditSidebar sidebar = new EditSidebar { Dock = DockStyle.Left, Visible = false };
        TopBar topBar;
        StatusBar statusBar;
        SnipForm snip;
        VideoSnipForm videoSnip;
        readonly VideoEditorView editor = new VideoEditorView { Dock = DockStyle.Fill, Visible = false };
        bool keepViewedVideos = true;
        string editName;           // name shown for a picture that has no file yet (pasted)

        AppMode mode = AppMode.Pictures;

        // pictures (unchanged behaviour)
        LoadedImage current;
        string shownPath;          // file currently represented on screen (image or incompatible message)
        EditSession edit;
        bool loading;
        string pendingPath;
        int generation;
        int explorerCount = -1;
        bool follow = true;
        readonly string startupFile;
        string transientStatus;
        readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer { Interval = 2500 };

        // videos
        string videoPath;
        MediaInfo videoInfo;
        int videoGeneration;
        bool videoProbing;
        bool autoplay;

        // fullscreen (videos)
        bool fullscreen;
        FormBorderStyle prevBorder;
        FormWindowState prevState;
        Rectangle prevBounds;
        bool prevStatusVisible;
        readonly System.Windows.Forms.Timer idleTimer = new System.Windows.Forms.Timer { Interval = 400 };
        DateTime lastMouseMove = DateTime.Now;
        Point lastMousePos;

        // menus
        MenuStrip menu;
        ToolStripMenuItem miSaveAs, miOpenDefault, miShowInExplorer, miCopyPath;
        ToolStripMenuItem miUndo, miRedo, miCopy, miPaste, miPasteNew, miEdit, miSnip, miRotL, miRotR, miFlipH, miFlipV, miCrop, miClearInk;
        ToolStripMenuItem miEditVideo, miVideoSnip, miSplit, miDelete, miSelectAll, miNewProject, miOpenProject, miSaveProject, miSaveProjectAs, miImport, miExport;
        ToolStripMenuItem miVideoEditor;
        ToolStripMenuItem miPictures, miVideos, miZoomIn, miZoomOut, miFit, miActual, miGrid, miFollow, miTopMost, miStatusBar, miDark, miLight;
        ToolStripMenuItem miPlayback, miPlay, miBack, miForward, miMute, miFullscreen, miSpeed;
        ThemedButton tbPointer, tbZoom, tbCrop, tbMore, tbExport;

        // edit tools
        EditTool tool = EditTool.Pen;
        EditTool lastDrawingTool = EditTool.Pen;
        readonly Dictionary<EditTool, Color> toolColors = new Dictionary<EditTool, Color>();
        readonly Dictionary<EditTool, int> toolSizes = new Dictionary<EditTool, int>();

        public MainForm(string fileToOpen)
        {
            startupFile = fileToOpen;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.UiFont(9.5f);
            Text = AppName;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(Theme.S(860), Theme.S(540));
            KeyPreview = true;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // settings that affect the look must be known before building
            Theme.Set(settings.GetString("Theme", "Dark") != "Light");

            BuildMenu();
            topBar = new TopBar(menu);
            BuildTopBarButtons();
            statusBar = new StatusBar();
            statusBar.ZoomOut.Click += (s, e) => { if (mode == AppMode.VideoEdit) editor.Timeline.ZoomBy(0.75); else canvas.ZoomOut(); };
            statusBar.ZoomIn.Click += (s, e) => { if (mode == AppMode.VideoEdit) editor.Timeline.ZoomBy(1.33); else canvas.ZoomIn(); };
            statusBar.Grid.Click += (s, e) => SetPixelGrid(!canvas.ShowPixelGrid);
            topBar.Modes.CellClicked += OnModeCell;
            topBar.Modes.SetVideoEditing(true, true);

            canvas.Dock = DockStyle.Fill;
            canvas.ZoomChanged += (s, e) => UpdateStatus();
            canvas.CropApplyRequested += (s, e) => ApplyCrop();
            canvas.CropCancelRequested += (s, e) => CancelCrop();
            videoView.Dock = DockStyle.Fill;
            videoView.Visible = false;
            content.Controls.Add(canvas);
            content.Controls.Add(videoView);
            content.Controls.Add(editor);
            editor.Message += t => Flash(t);
            editor.StateChanged += (s, e) => UpdateUi();
            editor.RecordRequested += (s, e) => OpenVideoSnip();

            sidebar.ToolSelected += t => SelectTool(t);
            sidebar.ColorPicked += c => SetToolColor(c);
            sidebar.SizeChangedByUser += v => { if (toolSizes.ContainsKey(tool)) { toolSizes[tool] = v; ApplyToolToCanvas(); } };
            sidebar.TransformRequested += t => Transform(t);
            sidebar.CommandRequested += OnSidebarCommand;

            Controls.Add(content);
            Controls.Add(sidebar);
            Controls.Add(statusBar);
            Controls.Add(topBar);
            MainMenuStrip = menu;

            var player = videoView.Player;
            player.StateChanged += (s, e) => { UpdateMenus(); };
            player.Failed += msg => ShowVideoError(msg);
            videoView.Controls2.FullscreenRequested += (s, e) => ToggleFullscreen();
            videoView.Surface.Click += (s, e) => { if (player.Info != null) player.Toggle(); };
            videoView.Surface.DoubleClick += (s, e) => { if (player.Info != null) ToggleFullscreen(); };
            videoView.Surface.MouseMove += (s, e) => OnVideoMouseMove(e.Location);
            idleTimer.Tick += (s, e) => AutoHideControls();
            statusTimer.Tick += (s, e) => { statusTimer.Stop(); transientStatus = null; UpdateStatus(); };

            LoadSettings();
            Theme.Changed += (s, e) => ApplyTheme();

            watcher.SelectionChanged += (path, kind) => Post(() => OnExplorerSelection(path, kind));
            watcher.WindowCountChanged += n => Post(() => { explorerCount = n; UpdateStatus(); });
            pickup.FileClicked += (path, kind) => Post(() => OnExplorerSelection(path, kind));
            pickup.PictureClicked += (bmp, name) =>
            {
                if (IsDisposed || !IsHandleCreated) { bmp.Dispose(); return; }
                try { BeginInvoke((Action)(() => OnPictureClicked(bmp, name))); } catch { bmp.Dispose(); }
            };
            clipTimer.Tick += (s, e) => { clipTimer.Stop(); ReadCopied(); };
        }

        void Post(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTheme();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            watcher.Paused = !follow;
            watcher.Start();
            UpdatePickup();
            pickup.Start();
            try
            {
                clipWatch = new ClipboardWatcher();
                clipWatch.Changed += (s, e2) => OnClipboardChanged();
            }
            catch { clipWatch = null; }
            if (!string.IsNullOrEmpty(startupFile) && File.Exists(startupFile)) OpenAny(startupFile);
            UpdateUi();
        }

        void ApplyTheme()
        {
            Theme.ApplyTo(this);
            BackColor = Theme.Current.Window;
            UpdateUi();
        }

        // ==================================================================== building the UI

        static ToolStripMenuItem Item(string text, Keys keys, EventHandler click, string keyText = null)
        {
            var mi = new ToolStripMenuItem(text, null, click);
            if (keys != Keys.None) mi.ShortcutKeys = keys;
            if (keyText != null) mi.ShortcutKeyDisplayString = keyText;
            return mi;
        }

        void BuildMenu()
        {
            menu = new MenuStrip();

            var file = new ToolStripMenuItem("&File");
            miSaveAs = Item("Save &As…", Keys.Control | Keys.Shift | Keys.S, (s, e) => SaveAs());
            miOpenDefault = Item("Open with &default app", Keys.None, (s, e) => OpenInDefaultApp());
            miShowInExplorer = Item("Show in File &Explorer", Keys.None, (s, e) => ShowInExplorer());
            miCopyPath = Item("Copy file &path", Keys.None, (s, e) => CopyPath());
            miNewProject = Item("&New video project", Keys.None, (s, e) => { EnterVideoEdit(); editor.NewProject(); });
            miOpenProject = Item("Open video pro&ject…", Keys.None, (s, e) => { EnterVideoEdit(); editor.OpenProjectDialog(); });
            miSaveProject = Item("Save video p&roject", Keys.None, (s, e) => editor.SaveProject(false), "Ctrl+S");
            miSaveProjectAs = Item("Save video project as…", Keys.None, (s, e) => editor.SaveProject(true));
            miImport = Item("&Import media…", Keys.None, (s, e) => { EnterVideoEdit(); editor.ImportDialog(); }, "Ctrl+I");
            miExport = Item("Ex&port video…", Keys.Control | Keys.M, (s, e) => { EnterVideoEdit(); editor.Export(); });
            file.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("&Open…", Keys.Control | Keys.O, (s, e) => OpenDialog()),
                miSaveAs,
                new ToolStripSeparator(),
                miNewProject, miOpenProject, miSaveProject, miSaveProjectAs, miImport, miExport,
                new ToolStripSeparator(),
                miOpenDefault, miShowInExplorer, miCopyPath,
                new ToolStripSeparator(),
                Item("Se&ttings…", Keys.Control | Keys.Oemcomma, (s, e) => ShowSettings(), "Ctrl+,"),
                new ToolStripSeparator(),
                Item("E&xit", Keys.Alt | Keys.F4, (s, e) => Close())
            });

            var editMenu = new ToolStripMenuItem("&Edit");
            miUndo = Item("&Undo", Keys.Control | Keys.Z, (s, e) => Undo());
            miRedo = Item("&Redo", Keys.Control | Keys.Y, (s, e) => Redo());
            miCopy = Item("&Copy image", Keys.Control | Keys.C, (s, e) => CopyImage());
            miPaste = Item("&Paste", Keys.Control | Keys.V, (s, e) => Paste());
            miPasteNew = Item("Paste as &new picture", Keys.Control | Keys.Alt | Keys.V, (s, e) => PasteAsNew());
            miEdit = Item("&Edit picture", Keys.Control | Keys.E, (s, e) => ToggleEdit());
            miSnip = Item("&Snip…", Keys.Control | Keys.Shift | Keys.N, (s, e) => OpenSnip());
            miEditVideo = Item("Edit &video", Keys.Control | Keys.Shift | Keys.E, (s, e) => ToggleVideoEdit());
            miVideoSnip = Item("Video sn&ip (record screen)…", Keys.Control | Keys.Alt | Keys.R, (s, e) => OpenVideoSnip());
            miSplit = Item("Spli&t clip", Keys.None, (s, e) => editor.SplitSelected(), "Ctrl+B");
            miDelete = Item("&Delete clip", Keys.None, (s, e) => editor.DeleteSelected(false), "Del");
            miSelectAll = Item("Select &all clips", Keys.None, (s, e) => editor.SelectAll(), "Ctrl+A");
            miCrop = Item("Cro&p", Keys.None, (s, e) => EnterPictureEdit(EditTool.Crop));
            miRotL = Item("Rotate &left", Keys.Control | Keys.Shift | Keys.R, (s, e) => Transform(RotateFlipType.Rotate270FlipNone));
            miRotR = Item("Rotate righ&t", Keys.Control | Keys.R, (s, e) => Transform(RotateFlipType.Rotate90FlipNone));
            miFlipH = Item("Flip &horizontal", Keys.None, (s, e) => Transform(RotateFlipType.RotateNoneFlipX));
            miFlipV = Item("Flip &vertical", Keys.None, (s, e) => Transform(RotateFlipType.RotateNoneFlipY));
            miClearInk = Item("Clear all &ink", Keys.None, (s, e) => { if (edit != null) edit.ClearInk(); });
            editMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                miUndo, miRedo, new ToolStripSeparator(), miCopy, miPaste, miPasteNew, new ToolStripSeparator(),
                miEdit, miSnip, miCrop, miRotL, miRotR, miFlipH, miFlipV, miClearInk, new ToolStripSeparator(),
                miEditVideo, miVideoSnip, miSplit, miDelete, miSelectAll
            });

            var view = new ToolStripMenuItem("&View");
            miPictures = Item("&Pictures", Keys.Alt | Keys.D1, (s, e) => SetMode(AppMode.Pictures), "Alt+1");
            miVideos = Item("V&ideos", Keys.Alt | Keys.D2, (s, e) => SetMode(AppMode.Videos), "Alt+2");
            miVideoEditor = Item("Video &editor", Keys.Alt | Keys.D3, (s, e) => EnterVideoEdit(), "Alt+3");
            miZoomIn = Item("Zoom &in", Keys.Control | Keys.Oemplus, (s, e) => canvas.ZoomIn(), "Ctrl++");
            miZoomOut = Item("Zoom &out", Keys.Control | Keys.OemMinus, (s, e) => canvas.ZoomOut(), "Ctrl+-");
            miFit = Item("&Fit to window", Keys.Control | Keys.D0, (s, e) => canvas.FitToWindow(), "Ctrl+0");
            miActual = Item("&Actual size (100%)", Keys.Control | Keys.D1, (s, e) => canvas.ActualSize(), "Ctrl+1");
            miGrid = Item("Pi&xel grid", Keys.None, (s, e) => SetPixelGrid(!canvas.ShowPixelGrid));

            miPlayback = new ToolStripMenuItem("Pla&yback");
            miPlay = Item("&Play / pause", Keys.None, (s, e) => videoView.Player.Toggle(), "Space");
            miBack = Item("&Back 5 seconds", Keys.None, (s, e) => SeekBy(-5), "←");
            miForward = Item("&Forward 5 seconds", Keys.None, (s, e) => SeekBy(5), "→");
            miMute = Item("&Mute", Keys.None, (s, e) => videoView.Player.Muted = !videoView.Player.Muted, "M");
            miFullscreen = Item("F&ull screen", Keys.F11, (s, e) => ToggleFullscreen(), "F");
            miSpeed = new ToolStripMenuItem("&Speed");
            foreach (var sp in VideoControls.Speeds)
            {
                double v = sp;
                var mi = new ToolStripMenuItem(Math.Abs(v - 1) < 0.001 ? "Normal (1×)" : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×") { Tag = v };
                mi.Click += (s, e) => videoView.Player.SetSpeed(v);
                miSpeed.DropDownItems.Add(mi);
            }
            miPlayback.DropDownItems.AddRange(new ToolStripItem[] { miPlay, miBack, miForward, miMute, miSpeed, new ToolStripSeparator(), miFullscreen });

            miFollow = Item("&Follow clicks and File Explorer selection", Keys.None, (s, e) => SetFollow(!follow));
            miTopMost = Item("Always on &top", Keys.None, (s, e) => { TopMost = !TopMost; UpdateUi(); });
            miStatusBar = Item("&Status bar", Keys.None, (s, e) => { statusBar.Visible = !statusBar.Visible; UpdateUi(); });
            var themeMenu = new ToolStripMenuItem("T&heme");
            miDark = Item("&Dark mode", Keys.None, (s, e) => SetDark(true));
            miLight = Item("&Light mode", Keys.None, (s, e) => SetDark(false));
            themeMenu.DropDownItems.AddRange(new ToolStripItem[] { miDark, miLight });
            view.DropDownItems.AddRange(new ToolStripItem[]
            {
                miPictures, miVideos, miVideoEditor, new ToolStripSeparator(),
                miZoomIn, miZoomOut, miFit, miActual, miGrid, new ToolStripSeparator(),
                miPlayback, new ToolStripSeparator(),
                miFollow, miTopMost, miStatusBar, themeMenu
            });

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Item("&Keyboard shortcuts", Keys.None, (s, e) => ShowShortcuts()));
            help.DropDownItems.Add(Item("&Pick-up log", Keys.None, (s, e) => OpenPickupLog()));
            help.DropDownItems.Add(Item("&About " + AppName.Replace("&", "&&"), Keys.None, (s, e) => ShowAbout()));

            menu.Items.AddRange(new ToolStripItem[] { file, editMenu, view, help });
            foreach (ToolStripMenuItem top in menu.Items)
            {
                top.Padding = new Padding(Theme.S(10), Theme.S(4), Theme.S(10), Theme.S(4));
                // Size the titles with the same font they're drawn in, so the last letter isn't clipped.
                top.Font = Theme.UiFont(10.5f);
                top.DropDownOpening += (s, e) => UpdateMenus();
            }
        }

        void BuildTopBarButtons()
        {
            tbPointer = topBar.AddRightButton(Ico.Pointer, "Move tool – drag to pan the picture while editing");
            tbPointer.Click += (s, e) => { if (mode == AppMode.PictureEdit && edit != null) SelectTool(EditTool.Pan); };
            tbZoom = topBar.AddRightButton(Ico.ZoomIn, "Zoom");
            tbZoom.Click += (s, e) => ShowZoomMenu();
            tbCrop = topBar.AddRightButton(Ico.Crop, "Crop the picture");
            tbCrop.Click += (s, e) => { if (mode == AppMode.VideoEdit) editor.ShowProps("basic"); else EnterPictureEdit(EditTool.Crop); };
            tbMore = topBar.AddRightButton(Ico.More, "More options");
            tbMore.Click += (s, e) => ShowMoreMenu();
            tbExport = topBar.AddRightButton(Ico.Export, "Export the video (Ctrl+M)");
            tbExport.IconOnly = false;
            tbExport.Compact = false;
            tbExport.Primary = true;
            tbExport.Text = "Export";
            tbExport.IconSize = 18f;
            tbExport.Size = new Size(Theme.S(108), Theme.S(38));
            tbExport.Visible = false;
            tbExport.Click += (s, e) => editor.Export();
        }

        void ShowZoomMenu()
        {
            var m = new ContextMenuStrip();
            if (mode == AppMode.VideoEdit)
            {
                m.Items.Add(new ToolStripMenuItem("Zoom in the timeline", null, (s, e) => editor.Timeline.ZoomBy(1.33)) { ShortcutKeyDisplayString = "+" });
                m.Items.Add(new ToolStripMenuItem("Zoom out the timeline", null, (s, e) => editor.Timeline.ZoomBy(0.75)) { ShortcutKeyDisplayString = "−" });
                m.Items.Add(new ToolStripMenuItem("Fit the project in view", null, (s, e) => editor.Timeline.FitAll()) { ShortcutKeyDisplayString = "Ctrl+0" });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(new ToolStripMenuItem("Bigger preview (hide side panels)", null, (s, e) => editor.ToggleFocusPreview()) { Checked = editor.FocusPreview, ShortcutKeyDisplayString = "F" });
                m.Closed += (s, e) => BeginInvoke((MethodInvoker)m.Dispose);
                Ui.ShowMenuBelow(m, tbZoom);
                return;
            }
            m.Items.Add(new ToolStripMenuItem("Zoom in", null, (s, e) => canvas.ZoomIn()) { ShortcutKeyDisplayString = "Ctrl++" });
            m.Items.Add(new ToolStripMenuItem("Zoom out", null, (s, e) => canvas.ZoomOut()) { ShortcutKeyDisplayString = "Ctrl+-" });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Fit to window", null, (s, e) => canvas.FitToWindow()) { ShortcutKeyDisplayString = "Ctrl+0", Checked = canvas.IsFit });
            foreach (var z in new[] { 0.5f, 1f, 2f, 4f, 8f })
            {
                float zz = z;
                m.Items.Add(new ToolStripMenuItem((int)(z * 100) + "%", null, (s, e) => canvas.ZoomAt(zz, new Point(canvas.Width / 2, canvas.Height / 2)))
                { Checked = !canvas.IsFit && Math.Abs(canvas.Zoom - z) < 0.001f });
            }
            m.Closed += (s, e) => BeginInvoke((MethodInvoker)m.Dispose);
            Ui.ShowMenuBelow(m, tbZoom);
        }

        void ShowMoreMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add(new ToolStripMenuItem("Settings…", null, (s, e) => ShowSettings()) { ShortcutKeyDisplayString = "Ctrl+," });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Dark mode", null, (s, e) => SetDark(true)) { Checked = Theme.Current.Dark });
            m.Items.Add(new ToolStripMenuItem("Light mode", null, (s, e) => SetDark(false)) { Checked = !Theme.Current.Dark });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Follow clicks and File Explorer selection", null, (s, e) => SetFollow(!follow)) { Checked = follow });
            m.Items.Add(new ToolStripMenuItem("Always on top", null, (s, e) => { TopMost = !TopMost; UpdateUi(); }) { Checked = TopMost });
            m.Items.Add(new ToolStripMenuItem("Status bar", null, (s, e) => { statusBar.Visible = !statusBar.Visible; UpdateUi(); }) { Checked = statusBar.Visible });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Keyboard shortcuts", null, (s, e) => ShowShortcuts()));
            m.Items.Add(new ToolStripMenuItem("About " + AppName.Replace("&", "&&"), null, (s, e) => ShowAbout()));
            m.Closed += (s, e) => BeginInvoke((MethodInvoker)m.Dispose);
            Ui.ShowMenuBelow(m, tbMore);
        }

        // ==================================================================== settings

        void LoadSettings()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            int w = settings.GetInt("Width", Math.Min(Theme.S(1180), wa.Width - 80));
            int h = settings.GetInt("Height", Math.Min(Theme.S(800), wa.Height - 80));
            int x = settings.GetInt("X", wa.Left + (wa.Width - w) / 2);
            int y = settings.GetInt("Y", wa.Top + (wa.Height - h) / 2);
            var bounds = new Rectangle(x, y, Math.Max(MinimumSize.Width, w), Math.Max(MinimumSize.Height, h));
            bool onScreen = false;
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(new Rectangle(bounds.X + 40, bounds.Y + 10, Math.Max(1, bounds.Width - 80), 40))) onScreen = true;
            if (!onScreen) bounds = new Rectangle(wa.Left + 40, wa.Top + 40, Math.Min(bounds.Width, wa.Width - 80), Math.Min(bounds.Height, wa.Height - 80));
            Bounds = bounds;
            if (settings.GetBool("Maximized", false)) WindowState = FormWindowState.Maximized;

            TopMost = settings.GetBool("AlwaysOnTop", false);
            follow = settings.GetBool("FollowExplorer", true);
            pickClicks = settings.GetBool("PickUpClicks", true);
            pickFiles = settings.GetBool("PickUpFiles", true);
            pickCopied = settings.GetBool("PickUpCopied", true);
            statusBar.Visible = settings.GetBool("StatusBar", true);
            canvas.ShowPixelGrid = settings.GetBool("PixelGrid", false);
            autoplay = settings.GetBool("AutoplayVideos", false);
            keepViewedVideos = settings.GetBool("KeepViewedVideos", true);
            videoView.Player.Volume = settings.GetInt("Volume", 80) / 100f;
            videoView.Player.Muted = settings.GetBool("Muted", false);

            toolColors[EditTool.Pen] = settings.GetColor("PenColor", Color.Black);
            toolColors[EditTool.Pencil] = settings.GetColor("PencilColor", Color.FromArgb(255, 0x7A, 0x75, 0x74));
            toolColors[EditTool.Highlighter] = settings.GetColor("HighlighterColor", Color.FromArgb(255, 255, 255, 0));
            toolSizes[EditTool.Pen] = settings.GetInt("PenSize", 4);
            toolSizes[EditTool.Pencil] = settings.GetInt("PencilSize", 3);
            toolSizes[EditTool.Highlighter] = settings.GetInt("HighlighterSize", 18);
            toolSizes[EditTool.Eraser] = settings.GetInt("EraserSize", 16);
        }

        void SaveSettings()
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (fullscreen) b = prevBounds;
            settings.Set("X", b.X); settings.Set("Y", b.Y); settings.Set("Width", b.Width); settings.Set("Height", b.Height);
            settings.Set("Maximized", fullscreen ? prevState == FormWindowState.Maximized : WindowState == FormWindowState.Maximized);
            settings.Set("AlwaysOnTop", TopMost);
            settings.Set("FollowExplorer", follow);
            settings.Set("PickUpClicks", pickClicks);
            settings.Set("PickUpFiles", pickFiles);
            settings.Set("PickUpCopied", pickCopied);
            settings.Set("StatusBar", fullscreen ? prevStatusVisible : statusBar.Visible);
            settings.Set("Theme", Theme.Current.Dark ? "Dark" : "Light");
            settings.Set("PixelGrid", canvas.ShowPixelGrid);
            settings.Set("AutoplayVideos", autoplay);
            settings.Set("KeepViewedVideos", keepViewedVideos);
            settings.Set("Volume", (int)Math.Round(videoView.Player.Volume * 100));
            settings.Set("Muted", videoView.Player.Muted);
            settings.Set("PenColor", toolColors[EditTool.Pen]);
            settings.Set("PencilColor", toolColors[EditTool.Pencil]);
            settings.Set("HighlighterColor", toolColors[EditTool.Highlighter]);
            settings.Set("PenSize", toolSizes[EditTool.Pen]);
            settings.Set("PencilSize", toolSizes[EditTool.Pencil]);
            settings.Set("HighlighterSize", toolSizes[EditTool.Highlighter]);
            settings.Set("EraserSize", toolSizes[EditTool.Eraser]);
            settings.Save();
        }

        void ShowSettings()
        {
            var o = new SettingsForm.Options
            {
                Dark = Theme.Current.Dark, Follow = follow, TopMost = TopMost, StatusBar = statusBar.Visible,
                PixelGrid = canvas.ShowPixelGrid, Autoplay = autoplay, KeepViewed = keepViewedVideos,
                PickClicks = pickClicks, PickFiles = pickFiles, PickCopied = pickCopied
            };
            using (var f = new SettingsForm(o, ApplyOptions)) f.ShowDialog(this);
        }

        void ApplyOptions(SettingsForm.Options o)
        {
            SetDark(o.Dark);
            if (o.Follow != follow) SetFollow(o.Follow);
            TopMost = o.TopMost;
            if (!fullscreen) statusBar.Visible = o.StatusBar;
            canvas.ShowPixelGrid = o.PixelGrid;
            autoplay = o.Autoplay;
            keepViewedVideos = o.KeepViewed;
            pickClicks = o.PickClicks;
            pickFiles = o.PickFiles;
            pickCopied = o.PickCopied;
            UpdateUi();
        }

        void SetDark(bool dark)
        {
            Theme.Set(dark);
            UpdateUi();
        }

        void SetPixelGrid(bool on)
        {
            canvas.ShowPixelGrid = on;
            if (on && canvas.HasContent && canvas.Zoom < 5.99f) Flash("Pixel grid on – it appears from 600% zoom");
            UpdateUi();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (edit != null && !CloseEditSession(true))
            {
                e.Cancel = true;
                return;
            }
            if (!editor.ConfirmClose())
            {
                e.Cancel = true;
                return;
            }
            // Closing a snip window clears its field (FormClosed handler), so keep a local reference.
            var vs = videoSnip;
            if (vs != null && !vs.IsDisposed)
            {
                vs.Close();
                if (!vs.IsDisposed && vs.Visible) { e.Cancel = true; return; }
            }
            var ps = snip;
            if (ps != null && !ps.IsDisposed)
            {
                ps.Close();
                if (!ps.IsDisposed && ps.Visible) { e.Cancel = true; return; }
            }
            SaveSettings();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            watcher.Dispose();
            pickup.Dispose();
            if (clipWatch != null) clipWatch.Dispose();
            videoView.Player.Dispose();
            editor.Dispose();
            ReleaseCurrent();
            base.OnFormClosed(e);
        }

        // ==================================================================== modes

        void OnModeCell(string id)
        {
            switch (id)
            {
                case "pictures": SetMode(AppMode.Pictures); break;
                case "picedit": EnterPictureEdit(null); break;
                case "picsnip": OpenSnip(); break;
                case "videos": SetMode(AppMode.Videos); break;
                case "videdit": EnterVideoEdit(); break;
                case "vidsnip": OpenVideoSnip(); break;
            }
        }

        /// <summary>Switches between Pictures, picture editing and Videos. Returns false if the user cancelled.</summary>
        bool SetMode(AppMode m)
        {
            if (m == AppMode.PictureEdit) { EnterPictureEdit(null); return mode == AppMode.PictureEdit; }
            if (m == AppMode.VideoEdit) { EnterVideoEdit(); return mode == AppMode.VideoEdit; }
            if (m == mode) return true;
            if (mode == AppMode.PictureEdit)
            {
                if (!CloseEditSession(true)) return false;
                sidebar.Visible = false;
                canvas.SetEmptyState(Ico.Picture, "Click a picture anywhere",
                    "Click a picture in File Explorer, on the desktop, on a web page or in another app, or copy one,\nand it appears here. Arrow keys work in File Explorer. Use File > Open to pick one directly.");
            }
            if (mode == AppMode.Videos)
            {
                videoView.Player.Pause();
                if (fullscreen) ToggleFullscreen();
            }
            if (mode == AppMode.VideoEdit) editor.Pause();
            mode = m;
            bool videos = m == AppMode.Videos;
            editor.Visible = false;
            videoView.Visible = videos;
            canvas.Visible = !videos;
            watcher.WantVideos = videos;
            watcher.Paused = !follow;
            topBar.Modes.Mode = m;
            if (videos) videoView.Surface.Focus(); else canvas.Focus();
            UpdateUi();
            return true;
        }

        /// <summary>Picture Edit: opens the sidebar and loads the picture already on screen into the editor.</summary>
        void EnterPictureEdit(EditTool? startTool)
        {
            if (mode != AppMode.PictureEdit)
            {
                if (mode == AppMode.Videos)
                {
                    videoView.Player.Pause();
                    if (fullscreen) ToggleFullscreen();
                }
                if (mode == AppMode.VideoEdit) editor.Pause();
                mode = AppMode.PictureEdit;
                editor.Visible = false;
                videoView.Visible = false;
                canvas.Visible = true;
                watcher.WantVideos = false;
                watcher.Paused = !follow;
                sidebar.Visible = true;
                topBar.Modes.Mode = mode;
                canvas.SetEmptyState(Ico.Pencil, "Pick a picture to edit",
                    "Click a picture in File Explorer, open one with File > Open, or paste one.\nIt opens here, ready to draw on, crop, rotate or flip.");
            }
            if (edit == null && current != null && current.Image != null) StartEditSession();
            if (edit != null)
            {
                if (startTool.HasValue) SelectTool(startTool.Value);
                else if (tool == EditTool.Crop) SelectTool(EditTool.Crop);
                else SelectTool(tool);
            }
            canvas.Focus();
            SyncSidebar();
            UpdateUi();
        }

        void ToggleEdit()
        {
            if (mode == AppMode.PictureEdit) SetMode(AppMode.Pictures);
            else EnterPictureEdit(null);
        }

        void ToggleVideoEdit()
        {
            if (mode == AppMode.VideoEdit) SetMode(AppMode.Videos);
            else EnterVideoEdit();
        }

        /// <summary>Video editor. A video loaded in the viewer goes straight onto the timeline (if it isn't there yet).</summary>
        void EnterVideoEdit()
        {
            if (mode == AppMode.VideoEdit) { editor.Focus(); return; }
            if (mode == AppMode.PictureEdit)
            {
                if (!CloseEditSession(true)) return;
                sidebar.Visible = false;
                canvas.SetEmptyState(Ico.Picture, "Click a picture anywhere",
                    "Click a picture in File Explorer, on the desktop, on a web page or in another app, or copy one,\nand it appears here. Arrow keys work in File Explorer. Use File > Open to pick one directly.");
            }
            bool fromViewer = mode == AppMode.Videos;
            if (mode == AppMode.Videos)
            {
                videoView.Player.Pause();
                if (fullscreen) ToggleFullscreen();
            }
            mode = AppMode.VideoEdit;
            canvas.Visible = false;
            videoView.Visible = false;
            sidebar.Visible = false;
            editor.Visible = true;
            editor.BringToFront();
            watcher.WantVideos = true;
            watcher.Paused = !follow;
            topBar.Modes.Mode = mode;
            if (videoPath != null && videoInfo != null && (fromViewer || editor.Project.IsEmpty)) editor.OpenFromViewer(videoPath);
            editor.Focus();
            UpdateUi();
        }

        void OpenVideoSnip()
        {
            if (mode != AppMode.VideoEdit) EnterVideoEdit();
            if (videoSnip == null || videoSnip.IsDisposed)
            {
                videoSnip = new VideoSnipForm(this, settings, (path, temporary) =>
                {
                    if (mode != AppMode.VideoEdit) EnterVideoEdit();
                    var m = editor.Library.Add(path, 0, temporary);
                    editor.OpenFromViewer(path);
                    Flash("Recording added to the video editor");
                    return m != null;
                });
                videoSnip.FormClosed += (s, e) => { videoSnip = null; topBar.Modes.VideoSnipOpen = false; UpdateUi(); };
                videoSnip.Show(this);
                topBar.Modes.VideoSnipOpen = true;
            }
            else
            {
                if (videoSnip.WindowState == FormWindowState.Minimized) videoSnip.WindowState = FormWindowState.Normal;
                videoSnip.Activate();
            }
        }

        void StartEditSession()
        {
            edit = new EditSession(current.Image, current.SourcePath);
            edit.Changed += (s, e) => { SyncSidebar(); UpdateUi(); };
            canvas.Edit = edit;
        }

        /// <summary>Ends the edit session. Asks to save unsaved edits when <paramref name="ask"/> is set.</summary>
        bool CloseEditSession(bool ask)
        {
            if (edit == null) return true;
            canvas.CommitPaste();
            if (ask && edit.IsDirty)
            {
                var r = MessageBox.Show(this, "Save your edits as a new picture first?\n\nYour original file is never changed.",
                    AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return false;
                if (r == DialogResult.Yes && !SaveAs()) return false;
            }
            canvas.Edit = null;
            edit.Dispose();
            edit = null;
            SyncSidebar();
            return true;
        }

        void OpenSnip()
        {
            if (mode != AppMode.PictureEdit) EnterPictureEdit(null);
            if (snip == null || snip.IsDisposed)
            {
                snip = new SnipForm(this, () => CurrentPictureOrNull(), bmp => PasteBitmap(bmp, "Snip"));
                snip.FormClosed += (s, e) => { topBar.Modes.SnipOpen = false; snip = null; };
                snip.Show(this);
            }
            else
            {
                if (snip.WindowState == FormWindowState.Minimized) snip.WindowState = FormWindowState.Normal;
                snip.Activate();
            }
            topBar.Modes.SnipOpen = true;
        }

        Bitmap CurrentPictureOrNull()
        {
            try { return CurrentPicture(); } catch { return null; }
        }

        void OnSidebarCommand(string cmd)
        {
            switch (cmd)
            {
                case "undo": Undo(); break;
                case "redo": Redo(); break;
                case "clear": if (edit != null) edit.ClearInk(); break;
                case "copy": CopyImage(); break;
                case "paste": Paste(); break;
                case "pastenew": PasteAsNew(); break;
                case "save": SaveAs(); break;
                case "applycrop": ApplyCrop(); break;
                case "cancelcrop": CancelCrop(); break;
            }
        }

        // ==================================================================== loading pictures

        void OnExplorerSelection(string path, FileKind kind)
        {
            if (!follow) return;
            if (kind == FileKind.Video)
            {
                if (mode == AppMode.Videos) OpenVideo(path);
                else if (mode == AppMode.VideoEdit)
                {
                    bool had = editor.Library.FindByPath(path) != null;
                    var m = editor.AddFromViewer(path);
                    if (m != null && !had) Flash("Added " + m.Name + " to Media");
                }
                return;
            }
            if (mode == AppMode.Videos || mode == AppMode.VideoEdit) return;
            if (edit != null && edit.IsDirty) return; // never throw away unsaved edits
            OpenPath(path, kind);
        }

        /// <summary>Opens a picture or a video, switching mode when needed (File > Open, drag and drop, command line).</summary>
        void OpenAny(string path)
        {
            if (VideoEditorView.IsProjectFile(path))
            {
                EnterVideoEdit();
                if (mode == AppMode.VideoEdit) editor.OpenProject(path);
                return;
            }
            if (mode == AppMode.VideoEdit && MediaLibrary.CanImport(path))
            {
                editor.ImportFiles(new[] { path }, false);
                return;
            }
            if (FileTypes.IsVideo(path))
            {
                if (!SetMode(AppMode.Videos)) return;
                OpenVideo(path);
                return;
            }
            if (mode == AppMode.Videos && !SetMode(AppMode.Pictures)) return;
            if (edit != null && !CloseEditSession(true)) return;
            var kind = FileTypes.Classify(path);
            OpenPath(path, kind == FileKind.Other ? FileKind.Image : kind);
        }

        void OpenPath(string path, FileKind? knownKind = null)
        {
            FileKind kind = knownKind ?? FileTypes.Classify(path);
            if (kind == FileKind.Incompatible)
            {
                ShowIncompatible(path);
                return;
            }
            if (current != null && SamePath(current.SourcePath, path) && SamePath(shownPath, path)) return;
            generation++;
            pendingPath = path;
            if (!loading) StartLoad();
        }

        void StartLoad()
        {
            string p = pendingPath;
            pendingPath = null;
            if (p == null) return;
            loading = true;
            int gen = generation;
            Flash("Loading " + Path.GetFileName(p) + "…", true);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                LoadedImage li = null;
                Exception error = null;
                try { li = LoadedImage.Load(p); }
                catch (Exception ex) { error = ex; }
                if (IsDisposed || !IsHandleCreated)
                {
                    Discard(li);
                    return;
                }
                try { BeginInvoke((Action)(() => OnLoaded(gen, p, li, error))); }
                catch { Discard(li); }
            });
        }

        static void Discard(LoadedImage li)
        {
            if (li == null) return;
            ImageCache.Delete(li.CachePath);
            li.Dispose();
        }

        void OnLoaded(int gen, string path, LoadedImage li, Exception error)
        {
            loading = false;
            transientStatus = null;
            bool editingDirty = edit != null && edit.IsDirty;
            if (gen != generation || editingDirty || IsDisposed)
            {
                Discard(li);
                if (pendingPath != null && !editingDirty) StartLoad();
                UpdateUi();
                return;
            }
            CloseEditSession(false); // clean session for the previous picture
            if (error != null)
            {
                ReleaseCurrent();
                shownPath = path;
                canvas.ShowMessage("Couldn't open this picture", Path.GetFileName(path) + "\n" + FriendlyError(error));
                UpdateUi();
                return;
            }
            var old = current;
            current = li;
            shownPath = path;
            canvas.ShowImage(li.Image, li.IsAnimated);
            Discard(old); // the previous cached copy is deleted as soon as the new picture is up
            if (mode == AppMode.PictureEdit)
            {
                StartEditSession();
                SelectTool(tool == EditTool.Crop ? lastDrawingTool : tool);
            }
            UpdateUi();
        }

        static string FriendlyError(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "Access to the file was denied.";
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException) return "The file no longer exists.";
            if (ex is IOException) return "The file is in use or couldn't be read.";
            if (ex is OutOfMemoryException || ex is ArgumentException) return "The file appears to be damaged or isn't really this type of picture.";
            return ex.Message;
        }

        void ShowIncompatible(string path)
        {
            generation++;
            pendingPath = null;
            CloseEditSession(false);
            ReleaseCurrent();
            shownPath = path;
            string ext = (Path.GetExtension(path) ?? "").TrimStart('.').ToUpperInvariant();
            canvas.ShowMessage("Incompatible File Type",
                Path.GetFileName(path) + "\n" + ext + " files can't be previewed.\nSupported: JPG, PNG, BMP, GIF, TIFF and ICO.");
            UpdateUi();
        }

        void ReleaseCurrent()
        {
            if (current == null) return;
            var old = current;
            current = null;
            canvas.ShowMessage(null, null);
            Discard(old);
        }

        static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        void OpenDialog()
        {
            using (var dlg = new OpenFileDialog { Filter = FileTypes.OpenFilter, Title = "Open picture or video" })
            {
                dlg.FilterIndex = mode == AppMode.Videos ? 3 : 1;
                if (mode == AppMode.VideoEdit) dlg.Filter = FileTypes.OpenFilter + "|Video projects (*.srproj;*.ppvproj)|*.srproj;*.ppvproj";
                string last = mode == AppMode.Videos || mode == AppMode.VideoEdit ? videoPath : shownPath;
                if (last != null) try { dlg.InitialDirectory = Path.GetDirectoryName(last); } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                OpenAny(dlg.FileName);
            }
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (mode == AppMode.VideoEdit && files != null) { editor.ImportFiles(files, false); return; }
            if (files != null && files.Length > 0 && File.Exists(files[0])) OpenAny(files[0]);
        }

        // ==================================================================== picking up from other apps

        /// <summary>
        /// What the click pick-up looks for right now. Pictures on web pages and in other apps are taken in Pictures
        /// and Edit picture (but, like File Explorer clicks, never over unsaved edits), and nothing is picked up while
        /// a screen recording window is open.
        /// </summary>
        void UpdatePickup()
        {
            bool recording = videoSnip != null && !videoSnip.IsDisposed;
            watcher.FollowDesktop = pickFiles;
            pickup.WantVideos = mode == AppMode.Videos || mode == AppMode.VideoEdit;
            pickup.WantFiles = follow && pickFiles && !recording;
            pickup.WantPictures = follow && pickClicks && !recording && (mode == AppMode.Pictures || mode == AppMode.PictureEdit);
        }

        void OnPictureClicked(Bitmap bmp, string name)
        {
            if (!follow || !pickClicks || (mode != AppMode.Pictures && mode != AppMode.PictureEdit)) { bmp.Dispose(); return; }
            if (edit != null && edit.IsDirty)
            {
                // like File Explorer clicks: never throw away unsaved edits
                PickupLog.Write("picture: not shown, the picture being edited has unsaved edits");
                bmp.Dispose();
                return;
            }
            ShowPickedPicture(bmp, name);
        }

        void ShowPickedPicture(Bitmap bmp, string name)
        {
            string path = null;
            try { path = ImageCache.SavePicked(bmp, name); }
            catch { }
            finally { bmp.Dispose(); }
            if (path != null) OpenPath(path, FileKind.Image);
        }

        /// <summary>Opens the pick-up log in Notepad, to see why a click wasn't picked up.</summary>
        void OpenPickupLog()
        {
            string path = PickupLog.FilePath;
            if (!File.Exists(path)) PickupLog.Write("log opened (nothing picked up or skipped yet)");
            try { Process.Start("notepad.exe", "\"" + path + "\""); }
            catch { MessageBox.Show(this, "The pick-up log is at:\n" + path, AppName); }
        }

        void OnClipboardChanged()
        {
            if (!follow || !pickCopied || mode == AppMode.PictureEdit) return;   // in Edit, a copy is for pasting
            if (ClipboardWatcher.FromThisApp()) return;
            clipTimer.Stop();
            clipTimer.Start();   // give the copying app a moment to put all its formats on the clipboard
        }

        /// <summary>A file copied anywhere (Ctrl+C) is followed like a click; a copied picture ("Copy image") is shown at full quality in Pictures mode.</summary>
        void ReadCopied()
        {
            if (!follow || !pickCopied || mode == AppMode.PictureEdit || IsDisposed) return;
            try
            {
                if (ClipboardWatcher.FromThisApp()) return;
                var data = Clipboard.GetDataObject();
                if (data == null) return;
                bool videos = mode == AppMode.Videos || mode == AppMode.VideoEdit;
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files == null) return;
                    for (int i = 0; i < files.Length && i < 100; i++)
                    {
                        var k = FileTypes.Classify(files[i]);
                        bool wanted = videos ? k == FileKind.Video : k == FileKind.Image || k == FileKind.Incompatible;
                        if (wanted && File.Exists(files[i])) { OnExplorerSelection(files[i], k); return; }
                    }
                    return;
                }
                if (mode != AppMode.Pictures) return;
                // Office copies cells and text as pictures too; those aren't pictures the user meant to look at.
                if (data.GetDataPresent(DataFormats.Rtf) || data.GetDataPresent(DataFormats.CommaSeparatedValue) || data.GetDataPresent("Object Descriptor")) return;
                var bmp = ClipboardPicture();
                if (bmp != null) ShowPickedPicture(bmp, "Copied picture");
            }
            catch { }
        }

        void SetFollow(bool on)
        {
            follow = on;
            watcher.Paused = !follow;
            UpdateUi();
        }

        // ==================================================================== videos

        void OpenVideo(string path)
        {
            if (SamePath(path, videoPath) && (videoInfo != null || videoProbing)) return;
            int gen = ++videoGeneration;
            videoPath = path;
            videoInfo = null;
            videoProbing = true;
            var player = videoView.Player;
            player.Unload();
            videoView.Surface.ClearFrame();
            string name = Path.GetFileName(path);
            if (FFmpeg.IsReady) videoView.Surface.SetState(Ico.Video, "Opening video…", name, false);
            else videoView.Surface.SetState(Ico.Video, "Preparing the video engine…", "First time only – this takes a few seconds.\n" + name, false);
            UpdateUi();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                MediaInfo info = null;
                Exception error = null;
                try { info = FFmpeg.Probe(path); }
                catch (Exception ex) { error = ex; }
                Post(() => OnVideoProbed(gen, path, info, error));
            });
        }

        void OnVideoProbed(int gen, string path, MediaInfo info, Exception error)
        {
            if (gen != videoGeneration) return;
            videoProbing = false;
            string name = Path.GetFileName(path);
            if (error != null)
            {
                bool incompatible = error is InvalidDataException;
                videoView.Surface.SetState(Ico.Video, incompatible ? "Incompatible File Type" : "Couldn't open this video",
                    name + "\n" + (error.Message ?? "Unknown error"), true);
                UpdateUi();
                return;
            }
            videoInfo = info;
            if (keepViewedVideos && info.HasVideo) editor.AddFromViewer(path);
            if (!info.HasVideo)
                videoView.Surface.SetState(Ico.Music, name, "Audio only – press Play to listen.", false);
            videoView.Player.Load(info);
            if (autoplay) videoView.Player.Play();
            UpdateUi();
        }

        void ShowVideoError(string message)
        {
            string name = videoPath != null ? Path.GetFileName(videoPath) : "";
            videoView.Surface.ClearFrame();
            videoView.Surface.SetState(Ico.Video, "Couldn't play this video", name + "\n" + (message ?? "The video couldn't be decoded."), true);
            UpdateUi();
        }

        void SeekBy(double seconds)
        {
            var p = videoView.Player;
            if (p.Info == null) return;
            p.Seek(p.Position + seconds);
        }

        void ToggleFullscreen()
        {
            if (mode != AppMode.Videos && !fullscreen) return;
            if (!fullscreen)
            {
                prevBorder = FormBorderStyle;
                prevState = WindowState;
                prevBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                prevStatusVisible = statusBar.Visible;
                var screen = Screen.FromControl(this).Bounds;
                SuspendLayout();
                topBar.Visible = false;
                statusBar.Visible = false;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = screen;
                ResumeLayout();
                fullscreen = true;
                idleTimer.Start();
            }
            else
            {
                SuspendLayout();
                FormBorderStyle = prevBorder;
                Bounds = prevBounds;
                WindowState = prevState;
                topBar.Visible = true;
                statusBar.Visible = prevStatusVisible;
                videoView.Controls2.Visible = true;
                ResumeLayout();
                fullscreen = false;
                idleTimer.Stop();
                Theme.ApplyTitleBar(this);
            }
            videoView.Controls2.Fullscreen = fullscreen;
            videoView.Controls2.Invalidate();
            UpdateMenus();
        }

        void OnVideoMouseMove(Point p)
        {
            if (p == lastMousePos) return;
            lastMousePos = p;
            lastMouseMove = DateTime.Now;
            if (fullscreen && !videoView.Controls2.Visible) videoView.Controls2.Visible = true;
        }

        void AutoHideControls()
        {
            if (!fullscreen) return;
            bool overControls = videoView.Controls2.Visible &&
                videoView.Controls2.RectangleToScreen(videoView.Controls2.ClientRectangle).Contains(Cursor.Position);
            bool idle = (DateTime.Now - lastMouseMove).TotalSeconds > 2.5;
            if (idle && videoView.Player.IsPlaying && !overControls && videoView.Controls2.Visible) videoView.Controls2.Visible = false;
        }

        // ==================================================================== editing

        void SelectTool(EditTool t)
        {
            if (edit == null) { tool = t; SyncSidebar(); return; }
            if (t == EditTool.Crop) canvas.CommitPaste();
            if (tool == EditTool.Crop && t != EditTool.Crop) canvas.ResetCrop();
            tool = t;
            if (t != EditTool.Crop && t != EditTool.Eraser && t != EditTool.Pan) lastDrawingTool = t;
            if (t == EditTool.Crop) canvas.ResetCrop();
            ApplyToolToCanvas();
            SyncSidebar();
            UpdateUi();
        }

        void SyncSidebar()
        {
            KeyValuePair<string, Color>[] pal = tool == EditTool.Highlighter ? Palettes.Highlighter : Palettes.Ink;
            Color c;
            if (!toolColors.TryGetValue(tool, out c)) c = Color.Black;
            int size;
            if (!toolSizes.TryGetValue(tool, out size)) size = 4;
            int max = (tool == EditTool.Highlighter || tool == EditTool.Eraser) ? 60 : 30;
            sidebar.SetState(edit != null, tool, Palettes.Colors(pal), c, size, max,
                edit != null && edit.CanUndo, edit != null && edit.CanRedo, edit != null && edit.Current.Strokes.Length > 0);
        }

        void ApplyToolToCanvas()
        {
            canvas.Tool = tool;
            Color c;
            canvas.ToolColor = toolColors.TryGetValue(tool, out c) ? c : Color.Black;
            int sz;
            canvas.ToolSize = toolSizes.TryGetValue(tool, out sz) ? sz * Theme.Scale : 4;
            canvas.RefreshTool();
        }

        void SetToolColor(Color c)
        {
            if (!toolColors.ContainsKey(tool)) return;
            toolColors[tool] = c;
            ApplyToolToCanvas();
            SyncSidebar();
        }

        void ApplyCrop()
        {
            if (edit == null || tool != EditTool.Crop) return;
            canvas.CommitPaste();
            edit.Crop(canvas.CropRectangle);
            SelectTool(lastDrawingTool);
        }

        void CancelCrop()
        {
            if (edit == null) return;
            canvas.ResetCrop();
            SelectTool(lastDrawingTool);
        }

        void Transform(RotateFlipType t)
        {
            if (current == null && edit == null) { SystemSounds(); return; }
            if (mode != AppMode.PictureEdit) EnterPictureEdit(null);
            if (edit == null) return;
            canvas.CommitPaste();
            edit.RotateFlip(t);
        }

        void Undo()
        {
            if (mode == AppMode.VideoEdit) { editor.Undo(); return; }
            if (canvas.HasFloating) { canvas.CancelPaste(); return; }
            if (edit != null) edit.Undo();
        }

        /// <summary>Reads a picture from the clipboard (keeps transparency when a PNG is offered).</summary>
        internal static Bitmap ClipboardPicture()
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return null;
                Bitmap result = null;
                if (data.GetDataPresent("PNG"))
                {
                    var ms = data.GetData("PNG") as MemoryStream;
                    if (ms != null)
                    {
                        ms.Position = 0;
                        using (var img = Image.FromStream(ms)) result = new Bitmap(img);
                    }
                }
                if (result == null && data.GetDataPresent(DataFormats.Bitmap))
                {
                    var img = data.GetData(DataFormats.Bitmap) as Image;
                    if (img != null) result = new Bitmap(img);
                }
                if (result == null)
                {
                    try { if (Clipboard.ContainsImage()) { var img = Clipboard.GetImage(); if (img != null) result = new Bitmap(img); } } catch { }
                }
                if (result == null && data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0 && FileTypes.Classify(files[0]) == FileKind.Image)
                    {
                        using (var fs = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var img = Image.FromStream(fs)) result = new Bitmap(img);
                    }
                }
                if (result == null) return null;
                var argb = new Bitmap(result.Width, result.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(argb)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImageUnscaled(result, 0, 0); }
                result.Dispose();
                return argb;
            }
            catch { return null; }
        }

        /// <summary>
        /// Pastes the clipboard picture into the picture editor as a layer you can move and resize (like paint.net).
        /// With no picture open it starts a new picture from the clipboard.
        /// </summary>
        void Paste()
        {
            if (mode == AppMode.VideoEdit) { editor.Paste(); return; }
            if (mode == AppMode.Videos) { SystemSounds(); return; }
            var bmp = ClipboardPicture();
            if (bmp == null)
            {
                Flash("The clipboard doesn't have a picture to paste – snip or copy one first");
                SystemSounds();
                return;
            }
            PasteBitmap(bmp, "Pasted picture");
        }

        /// <summary>Puts a picture into the editor: as a floating layer on the open picture, or as a new picture.</summary>
        void PasteBitmap(Bitmap bmp, string name)
        {
            if (bmp == null) return;
            if (mode == AppMode.VideoEdit) { editor.AddPicture(bmp, name); Activate(); return; }
            if (current == null && edit == null)
            {
                StartNewPicture(bmp, name);
                Activate();
                return;
            }
            if (mode != AppMode.PictureEdit) EnterPictureEdit(null);
            if (edit == null) { StartNewPicture(bmp, name); return; }
            canvas.CommitPaste();
            if (tool == EditTool.Crop) SelectTool(lastDrawingTool);
            if (bmp.Width > edit.Size.Width || bmp.Height > edit.Size.Height)
            {
                var r = MessageBox.Show(this,
                    "The pasted picture (" + bmp.Width + " × " + bmp.Height + ") is bigger than this picture (" + edit.Size.Width + " × " + edit.Size.Height + ").\n\nMake the canvas bigger to fit it?",
                    AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { bmp.Dispose(); return; }
                if (r == DialogResult.Yes) edit.ExpandCanvas(bmp.Width, bmp.Height);
            }
            canvas.BeginPaste(bmp);
            Activate();
            canvas.Focus();
            Flash("Drag to move, drag the handles to resize. Enter or click outside to place it, Esc to cancel.");
            UpdateUi();
        }

        /// <summary>Ctrl+Alt+V: the clipboard picture becomes a new picture to edit.</summary>
        void PasteAsNew()
        {
            if (mode == AppMode.VideoEdit || mode == AppMode.Videos) { Paste(); return; }
            var bmp = ClipboardPicture();
            if (bmp == null) { Flash("The clipboard doesn't have a picture to paste"); SystemSounds(); return; }
            if (edit != null && !CloseEditSession(true)) { bmp.Dispose(); return; }
            StartNewPicture(bmp, "Pasted picture");
        }

        void StartNewPicture(Bitmap bmp, string name)
        {
            if (mode != AppMode.PictureEdit) EnterPictureEdit(null);
            if (edit != null && !CloseEditSession(true)) { bmp.Dispose(); return; }
            generation++;
            pendingPath = null;
            ReleaseCurrent();
            shownPath = null;
            editName = name + " " + DateTime.Now.ToString("HHmmss");
            canvas.ShowImage(null, false);
            edit = new EditSession(bmp, null, true);
            bmp.Dispose();
            edit.Changed += (s, e) => { SyncSidebar(); UpdateUi(); };
            canvas.Edit = edit;
            canvas.FitToWindow();
            SelectTool(tool == EditTool.Crop || tool == EditTool.Pan ? lastDrawingTool : tool);
            canvas.Focus();
            Flash("New picture from the clipboard – draw on it, crop it, then Save as");
            UpdateUi();
        }

        void Redo()
        {
            if (mode == AppMode.VideoEdit) { editor.Redo(); return; }
            if (edit != null) edit.Redo();
        }

        Bitmap CurrentPicture()
        {
            if (edit != null) { canvas.CommitPaste(); return edit.Flatten(); }
            if (current != null && current.Image != null) return new Bitmap(current.Image);
            return null;
        }

        void CopyImage()
        {
            if (mode == AppMode.VideoEdit) { editor.CopySelected(false); return; }
            if (mode == AppMode.Videos) { SystemSounds(); return; }
            using (var bmp = CurrentPicture())
            {
                if (bmp == null) { SystemSounds(); return; }
                try
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.Bitmap, true, bmp);
                    var png = new MemoryStream();
                    bmp.Save(png, ImageFormat.Png);
                    data.SetData("PNG", false, png);
                    Clipboard.SetDataObject(data, true);
                    Flash("Copied to clipboard");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't copy the picture: " + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        /// <summary>Saves the picture (with any edits) as a new file. Never overwrites the source file.</summary>
        bool SaveAs()
        {
            if (mode == AppMode.Videos) { SystemSounds(); return false; }
            if (mode == AppMode.VideoEdit) return editor.SaveProject(false);
            string source = edit != null ? edit.SourcePath : (current != null ? current.SourcePath : null);
            if (source == null && edit == null) { SystemSounds(); return false; }
            if (source == null)
            {
                // a new picture (pasted or snipped) that has never been saved
                string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                source = Path.Combine(string.IsNullOrEmpty(pics) ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : pics, (editName ?? "Picture") + ".png");
            }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Save a copy";
                dlg.Filter = FileTypes.SaveFilter;
                dlg.AddExtension = true;
                dlg.OverwritePrompt = true;
                string dir = Path.GetDirectoryName(source);
                if (ImageCache.IsPicked(source))
                {
                    // picked up from a web page or another app: it only lives in the temporary folder
                    string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    dir = string.IsNullOrEmpty(pics) ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : pics;
                }
                string name = Path.GetFileNameWithoutExtension(source);
                string ext = (Path.GetExtension(source) ?? "").ToLowerInvariant();
                int filter;
                string outExt;
                switch (ext)
                {
                    case ".jpg": case ".jpeg": case ".jpe": case ".jfif": filter = 2; outExt = ".jpg"; break;
                    case ".bmp": case ".dib": filter = 3; outExt = ".bmp"; break;
                    case ".gif": filter = 4; outExt = ".gif"; break;
                    case ".tif": case ".tiff": filter = 5; outExt = ".tif"; break;
                    default: filter = 1; outExt = ".png"; break;
                }
                dlg.FilterIndex = filter;
                dlg.DefaultExt = outExt.TrimStart('.');
                try { dlg.InitialDirectory = dir; } catch { }
                string suggestion = edit != null && edit.SourcePath == null ? name : name + " (edited)";
                for (int i = 2; File.Exists(Path.Combine(dir ?? "", suggestion + outExt)) && i < 1000; i++) suggestion = name + " (edited " + i + ")";
                dlg.FileName = suggestion + outExt;

                while (true)
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                    if (edit != null && edit.SourcePath != null && SamePath(dlg.FileName, edit.SourcePath) || current != null && SamePath(dlg.FileName, current.SourcePath))
                    {
                        MessageBox.Show(this, "Stills & Reels never overwrites the original file.\nPlease choose a different name or folder.",
                            AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        dlg.FileName = suggestion + outExt;
                        continue;
                    }
                    break;
                }

                try
                {
                    using (var bmp = CurrentPicture()) ImageSaver.Save(bmp, dlg.FileName);
                    if (edit != null) edit.MarkSaved();
                    Flash("Saved " + Path.GetFileName(dlg.FileName));
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't save the picture:\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
        }

        // ==================================================================== file actions

        string CurrentFile
        {
            get
            {
                if (mode == AppMode.Videos) return videoPath;
                if (mode == AppMode.VideoEdit) return editor.ProjectPath;
                return edit != null ? edit.SourcePath : shownPath;
            }
        }

        void OpenInDefaultApp()
        {
            var f = CurrentFile;
            if (f == null || !File.Exists(f)) return;
            if (mode == AppMode.Videos) videoView.Player.Pause();
            try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void ShowInExplorer()
        {
            var f = CurrentFile;
            if (f == null || !File.Exists(f)) return;
            try { Process.Start("explorer.exe", "/select,\"" + f + "\""); } catch { }
        }

        void CopyPath()
        {
            var f = CurrentFile;
            if (f == null) return;
            try { Clipboard.SetText(f); Flash("Path copied"); } catch { }
        }

        void ShowAbout()
        {
            MessageBox.Show(this,
                AppName + " 2.2\n\n" +
                "Click a picture or video in any File Explorer window (or move with the arrow keys) and it appears here. " +
                "Files clicked on the desktop or in Open/Save dialogs, pictures clicked on web pages or in other apps, and pictures or files you copy are picked up too (see Settings). " +
                "Pictures from web pages are copied from the screen as shown; nothing is downloaded.\n\n" +
                "Pictures are read from a temporary copy that is deleted as soon as you move to another picture. " +
                "Videos you view are played in place and (if you allow it in Settings) copied into the video editor's Media, " +
                "where they stay until you remove them or close the app. Edits are only ever saved as new files – your originals are never changed.\n\n" +
                "Video playback, editing and export use FFmpeg " + BuildInfo.FfmpegVersion + " (GPL), built into this app. It runs only on this PC and never uses the network.\n\n" +
                "Temporary folder: " + ImageCache.Folder,
                "About " + AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ShowShortcuts()
        {
            MessageBox.Show(this,
                "Pictures\n" +
                "  Ctrl + / Ctrl −   Zoom in / out\n  Ctrl+0   Fit to window\n  Ctrl+1   Actual size\n" +
                "  Ctrl+E   Edit picture\n  Ctrl+Shift+N   Snip\n  Ctrl+V   Paste (as a movable layer)\n  Ctrl+Alt+V   Paste as a new picture\n" +
                "  Ctrl+R / Ctrl+Shift+R   Rotate\n  Ctrl+Z / Ctrl+Y   Undo / redo\n  Ctrl+C   Copy\n  Ctrl+S   Save a copy\n" +
                "  Space + drag   Move around while editing\n\n" +
                "Videos\n" +
                "  Space or K   Play / pause\n  ← / →   Back / forward 5 s\n  J / L   Back / forward 10 s\n" +
                "  ↑ / ↓   Volume\n  M   Mute\n  F or F11   Full screen\n  Esc   Leave full screen\n\n" +
                "Video editor\n" +
                "  Space   Play / pause\n  ← / →   One frame   (Shift: one second)\n  ↑ / ↓   Previous / next cut\n" +
                "  Ctrl+B   Split    Del   Delete    Shift+Del   Delete and close the gap\n" +
                "  Ctrl+C / X / V   Copy / cut / paste    Ctrl+D   Duplicate\n" +
                "  Ctrl+K   Keyframe    T   Add text    Ctrl+I   Import\n" +
                "  Ctrl+S   Save project    Ctrl+M   Export    Ctrl+wheel   Zoom the timeline\n\n" +
                "Modes\n  Alt+1   Pictures\n  Alt+2   Videos\n  Alt+3   Video editor\n  Ctrl+Shift+E   Edit video\n  Ctrl+Alt+R   Video snip\n  Ctrl+O   Open a file",
                "Keyboard shortcuts", MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        static void SystemSounds()
        {
            try { System.Media.SystemSounds.Beep.Play(); } catch { }
        }

        bool TextBoxFocused()
        {
            Control a = ActiveControl;
            while (a is ContainerControl && ((ContainerControl)a).ActiveControl != null) a = ((ContainerControl)a).ActiveControl;
            return a is TextBoxBase;
        }

        static bool IsTextKey(Keys k)
        {
            Keys code = k & Keys.KeyCode;
            bool ctrl = (k & Keys.Control) != 0;
            if (ctrl && (code == Keys.C || code == Keys.V || code == Keys.X || code == Keys.A || code == Keys.Z || code == Keys.Y)) return true;
            if ((k & (Keys.Control | Keys.Alt)) == 0) return true; // plain typing, arrows, Delete, Home/End, Space
            if (ctrl && (code == Keys.Left || code == Keys.Right || code == Keys.Back || code == Keys.Delete || code == Keys.Home || code == Keys.End)) return true;
            return false;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // let text boxes (e.g. the words of a text clip) handle their own editing keys
            if (TextBoxFocused() && IsTextKey(keyData)) return false;
            if (mode == AppMode.VideoEdit && !IsMenuOpen())
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.S: editor.SaveProject(false); return true;
                    case Keys.Control | Keys.Shift | Keys.S: editor.SaveProject(true); return true;
                    case Keys.Control | Keys.Z: editor.Undo(); return true;
                    case Keys.Control | Keys.Y:
                    case Keys.Control | Keys.Shift | Keys.Z: editor.Redo(); return true;
                    case Keys.Control | Keys.N: editor.NewProject(); return true;
                }
                if (!menu.ContainsFocus && editor.HandleKey(keyData)) return true;
            }
            switch (keyData)
            {
                case Keys.Control | Keys.S: SaveAs(); return true;
                case Keys.Control | Keys.Add: canvas.ZoomIn(); return true;
                case Keys.Control | Keys.Subtract: canvas.ZoomOut(); return true;
                case Keys.Control | Keys.NumPad0: canvas.FitToWindow(); return true;
                case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
                case Keys.Control | Keys.Shift | Keys.Oemplus: canvas.ZoomIn(); return true;
            }
            if (mode == AppMode.Videos && !menu.ContainsFocus && !IsMenuOpen())
            {
                var p = videoView.Player;
                switch (keyData)
                {
                    case Keys.Space:
                    case Keys.K: if (p.Info != null) p.Toggle(); return true;
                    case Keys.Left: SeekBy(-5); return true;
                    case Keys.Right: SeekBy(5); return true;
                    case Keys.J: SeekBy(-10); return true;
                    case Keys.L: SeekBy(10); return true;
                    case Keys.Up: p.Volume = Math.Min(1, p.Volume + 0.05f); p.Muted = false; return true;
                    case Keys.Down: p.Volume = Math.Max(0, p.Volume - 0.05f); return true;
                    case Keys.M: p.Muted = !p.Muted; return true;
                    case Keys.F: if (p.Info != null || fullscreen) ToggleFullscreen(); return true;
                    case Keys.Home: if (p.Info != null) p.Seek(0); return true;
                    case Keys.Escape: if (fullscreen) { ToggleFullscreen(); return true; } break;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        bool IsMenuOpen()
        {
            foreach (ToolStripItem it in menu.Items)
            {
                var dd = it as ToolStripDropDownItem;
                if (dd != null && dd.DropDown.Visible) return true;
            }
            return false;
        }

        // ==================================================================== status / enabled state

        void Flash(string text, bool sticky = false)
        {
            transientStatus = text;
            statusTimer.Stop();
            if (!sticky) statusTimer.Start();
            UpdateStatus();
        }

        void UpdateMenus()
        {
            bool videos = mode == AppMode.Videos;
            bool vedit = mode == AppMode.VideoEdit;
            bool pictures = !videos && !vedit;
            bool hasImage = current != null && pictures;
            bool hasFile = CurrentFile != null;
            var player = videoView.Player;
            bool hasVideo = player.Info != null;

            miSaveAs.Enabled = (hasImage || edit != null) && pictures;
            miOpenDefault.Enabled = miShowInExplorer.Enabled = miCopyPath.Enabled = hasFile && !vedit;
            miSaveProject.Enabled = miSaveProjectAs.Enabled = vedit;
            miUndo.Enabled = vedit ? editor.CanUndo : edit != null && edit.CanUndo;
            miRedo.Enabled = vedit ? editor.CanRedo : edit != null && edit.CanRedo;
            miUndo.Text = vedit ? "&Undo (video editor)" : "&Undo";
            miRedo.Text = vedit ? "&Redo (video editor)" : "&Redo";
            miCopy.Text = vedit ? "&Copy clips" : "&Copy image";
            miCopy.Enabled = vedit ? editor.HasSelection : hasImage || edit != null;
            miPaste.Enabled = !videos;
            miPasteNew.Enabled = pictures;
            miEdit.Enabled = true;
            miEdit.Checked = mode == AppMode.PictureEdit;
            miSnip.Enabled = true;
            miEditVideo.Checked = vedit;
            miVideoSnip.Checked = videoSnip != null && !videoSnip.IsDisposed;
            miSplit.Visible = miDelete.Visible = miSelectAll.Visible = vedit;
            miSplit.Enabled = vedit && !editor.Project.IsEmpty;
            miDelete.Enabled = vedit && editor.HasSelection;
            miCrop.Enabled = miRotL.Enabled = miRotR.Enabled = miFlipH.Enabled = miFlipV.Enabled = pictures && (current != null || edit != null);
            miClearInk.Enabled = edit != null && edit.Current.Strokes.Length > 0;
            miPictures.Checked = mode == AppMode.Pictures || mode == AppMode.PictureEdit;
            miVideos.Checked = videos;
            miVideoEditor.Checked = vedit;
            miZoomIn.Enabled = miZoomOut.Enabled = miFit.Enabled = miActual.Enabled = canvas.HasContent && pictures;
            miGrid.Checked = canvas.ShowPixelGrid;
            miGrid.Enabled = pictures;
            miPlayback.Enabled = videos;
            miPlay.Enabled = miBack.Enabled = miForward.Enabled = hasVideo;
            miPlay.Text = player.IsPlaying ? "&Pause" : "&Play";
            miMute.Checked = player.Muted;
            miFullscreen.Checked = fullscreen;
            foreach (ToolStripMenuItem s in miSpeed.DropDownItems) s.Checked = Math.Abs((double)s.Tag - player.Speed) < 0.001;
            miFollow.Checked = follow;
            miTopMost.Checked = TopMost;
            miStatusBar.Checked = statusBar.Visible || (fullscreen && prevStatusVisible);
            miDark.Checked = Theme.Current.Dark;
            miLight.Checked = !Theme.Current.Dark;
        }

        string PictureName
        {
            get
            {
                if (edit != null && edit.SourcePath == null) return editName ?? "New picture";
                string f = CurrentFile;
                return f != null ? Path.GetFileName(f) : null;
            }
        }

        void UpdateUi()
        {
            if (IsDisposed || topBar == null) return;
            bool vedit = mode == AppMode.VideoEdit;
            string title = AppName;
            if (vedit) title = editor.ProjectName + (editor.IsDirty ? " *" : "") + " - Video editor - " + AppName;
            else
            {
                string fileName = mode == AppMode.Videos ? (videoPath != null ? Path.GetFileName(videoPath) : null) : PictureName;
                if (fileName != null)
                    title = fileName + (edit != null && edit.IsDirty ? " *" : "") + (mode == AppMode.PictureEdit && edit != null ? " (editing)" : "") + " - " + AppName;
            }
            Text = title;

            topBar.Modes.Mode = mode;
            topBar.Modes.SnipOpen = snip != null && !snip.IsDisposed;
            bool videos = mode == AppMode.Videos;
            bool pictures = !videos && !vedit;
            tbPointer.Enabled = mode == AppMode.PictureEdit && edit != null;
            tbPointer.Checked = tbPointer.Enabled && tool == EditTool.Pan;
            tbZoom.Enabled = (canvas.HasContent && pictures) || vedit;
            tbZoom.Tip = vedit ? "Timeline zoom and preview size" : "Zoom";
            tbCrop.Enabled = pictures ? current != null || edit != null : vedit && editor.SingleSelected != null && editor.SingleSelected.IsVisual;
            tbCrop.Tip = vedit ? "Crop the selected clip (Basic panel)" : "Crop the picture";
            tbCrop.Checked = mode == AppMode.PictureEdit && edit != null && tool == EditTool.Crop;
            if (tbExport.Visible != vedit) { tbExport.Visible = vedit; topBar.PerformLayout(); }
            statusBar.ZoomIn.Enabled = statusBar.ZoomOut.Enabled = (canvas.HasContent && pictures) || vedit;
            statusBar.Grid.Enabled = pictures;
            statusBar.Grid.Checked = canvas.ShowPixelGrid && pictures;
            UpdatePickup();
            UpdateMenus();
            UpdateStatus();
        }

        void UpdateStatus()
        {
            if (statusBar == null) return;
            string left;
            if (transientStatus != null) left = transientStatus;
            else if (mode == AppMode.VideoEdit) left = editor.StatusText;
            else if (mode == AppMode.Videos)
            {
                if (videoPath == null) left = "No video";
                else
                {
                    left = Path.GetFileName(videoPath);
                    var i = videoInfo;
                    if (i != null)
                    {
                        if (i.HasVideo && i.Width > 0) left += "    " + i.Width + " × " + i.Height + "    " + Math.Round(i.Fps, 2) + " fps";
                        else if (!i.HasVideo) left += "    Audio only";
                        if (i.Duration > 0) left += "    " + VideoControls.FormatTime(i.Duration, i.Duration);
                        if (i.FileSize > 0) left += "    " + FormatSize(i.FileSize);
                    }
                }
            }
            else
            {
                string f = PictureName;
                if (f == null) left = mode == AppMode.PictureEdit ? "No picture to edit – open one, or paste with Ctrl+V" : "No picture";
                else
                {
                    left = f;
                    if (edit != null) left += "    " + edit.Size.Width + " × " + edit.Size.Height;
                    else if (current != null) left += "    " + current.Image.Width + " × " + current.Image.Height;
                    if (current != null) left += "    " + FormatSize(current.FileSize);
                    if (canvas.HasContent) left += "    " + Math.Round(canvas.Zoom * 100) + "%";
                }
            }
            statusBar.LeftText = left;
            statusBar.Busy = transientStatus != null && loading;

            string right;
            bool anywhere = pickFiles || pickCopied || (pickClicks && mode == AppMode.Pictures);
            string windows = explorerCount + " File Explorer window" + (explorerCount == 1 ? "" : "s");
            if (mode == AppMode.PictureEdit && edit != null && edit.IsDirty) right = "Unsaved edits – following paused";
            else if (!follow) right = "Not following clicks or File Explorer";
            else if (explorerCount < 0) right = "Looking for File Explorer windows…";
            else if (mode == AppMode.VideoEdit) right = anywhere ? "Videos you click go to Media" : "Videos you click in File Explorer go to Media";
            else if (anywhere) right = (mode == AppMode.Videos ? "Following videos you click" : "Following pictures you click") + " · " + windows;
            else if (explorerCount == 0) right = "No File Explorer windows open";
            else right = "Watching " + windows + (mode == AppMode.Videos ? " for videos" : "");
            statusBar.RightText = right;
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " bytes";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.##") + " GB";
        }
    }
}
