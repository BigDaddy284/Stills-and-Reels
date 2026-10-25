using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>
    /// The video editor workspace: media panel (left), preview (centre), properties (right) and the
    /// multitrack timeline (bottom). Owns the project, undo history, selection and playhead.
    /// </summary>
    internal sealed class VideoEditorView : Control
    {
        public const string ProjectFilter = "Stills & Reels video project (*.srproj;*.ppvproj)|*.srproj;*.ppvproj";
        public const string ProjectSaveFilter = "Stills & Reels video project (*.srproj)|*.srproj";
        public static bool IsProjectFile(string path)
        {
            string e = System.IO.Path.GetExtension(path ?? "");
            return string.Equals(e, ".srproj", StringComparison.OrdinalIgnoreCase) || string.Equals(e, ".ppvproj", StringComparison.OrdinalIgnoreCase);
        }

        Project project = new Project();
        public readonly MediaLibrary Library;
        readonly List<Project> undo = new List<Project>();
        readonly List<Project> redo = new List<Project>();
        string lastTag;
        DateTime lastTagTime;
        int version, savedVersion;
        string projectPath;

        readonly HashSet<int> selection = new HashSet<int>();
        double playhead;
        EditorPlayback playback;
        readonly StillRenderer still = new StillRenderer();
        readonly System.Windows.Forms.Timer playTimer = new System.Windows.Forms.Timer { Interval = 30 };
        readonly System.Windows.Forms.Timer renderTimer = new System.Windows.Forms.Timer { Interval = 20 };
        readonly System.Windows.Forms.Timer restartTimer = new System.Windows.Forms.Timer { Interval = 180 };
        int stillGen;
        bool snapping = true, mainMagnet = true, previewMuted, focusPreview;
        float previewVolume = 1f;
        readonly List<int> pendingAdds = new List<int>();
        readonly List<Clip> clipboard = new List<Clip>();
        uint clipboardSeq;   // Windows clipboard change counter when clips were copied

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetClipboardSequenceNumber();

        static uint ClipboardSeq() { try { return GetClipboardSequenceNumber(); } catch { return 0; } }

        // UI
        readonly ThemedPanel leftPanel, rightPanel, centre, timelineArea;
        readonly TabStrip tabs = new TabStrip { Dock = DockStyle.Top };
        readonly Dictionary<string, Control> pages = new Dictionary<string, Control>();
        readonly TileGrid mediaGrid = new TileGrid { Dock = DockStyle.Fill, ShowAddButton = true, EmptyText = "Pictures, videos and music you import appear here.\n\nVideos you click in File Explorer (in Videos mode or here) are added automatically and kept until you close Stills & Reels or remove them." };
        readonly TileGrid textGrid = new TileGrid { Dock = DockStyle.Fill, PictureAspect = 0.5f };
        readonly TileGrid transGrid = new TileGrid { Dock = DockStyle.Fill, PictureAspect = 0.56f };
        readonly TileGrid filterGrid = new TileGrid { Dock = DockStyle.Fill };
        readonly TileGrid effectGrid = new TileGrid { Dock = DockStyle.Fill, PictureAspect = 0.5f };
        readonly HintLabel mediaFooter = new HintLabel { Dock = DockStyle.Fill };
        readonly ThemedPanel mediaFooterHost = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Bottom, BorderTop = true };
        readonly StackPanel props = new StackPanel { Dock = DockStyle.Fill };
        public readonly TimelineControl Timeline;
        readonly PreviewSurface preview;
        readonly PreviewBar bar;
        readonly EditToolbar toolbar;
        readonly SplitterBar splitter = new SplitterBar { Dock = DockStyle.Bottom };
        int timelineHeight = -1;
        string propsPage = "basic";
        int propsFor = -1;
        readonly List<PropSlider> liveSliders = new List<PropSlider>();
        Bitmap filterSource;

        public event Action<string> Message;   // status line text
        public event EventHandler StateChanged; // title, menus

        public VideoEditorView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Current.Window;
            Library = new MediaLibrary();
            Library.ItemChanged += OnMediaChanged;
            Library.ListChanged += (s, e) => RefreshMedia();

            Timeline = new TimelineControl(this) { Dock = DockStyle.Fill };
            preview = new PreviewSurface(this) { Dock = DockStyle.Fill };
            bar = new PreviewBar(this) { Dock = DockStyle.Bottom };
            toolbar = new EditToolbar(this) { Dock = DockStyle.Top };
            toolbar.Zoom.ValueChanged += (s, e) => { if (!syncingZoom) Timeline.ZoomFraction = toolbar.Zoom.Value / 1000.0; };
            Timeline.ViewChanged += (s, e) => SyncZoom();

            centre = new ThemedPanel { Surface = Surface.Canvas, Dock = DockStyle.Fill };
            centre.Controls.Add(preview);
            centre.Controls.Add(bar);

            leftPanel = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Left, BorderRight = true, Width = Theme.S(290) };
            BuildLeft();
            rightPanel = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Right, BorderLeft = true, Width = Theme.S(300) };
            rightPanel.Controls.Add(props);

            timelineArea = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Bottom, Height = Theme.S(300) };
            timelineArea.Controls.Add(Timeline);
            timelineArea.Controls.Add(toolbar);
            splitter.GetHeight = () => timelineArea.Height;
            splitter.SetHeight = h => { timelineHeight = h; PerformLayout(); };

            Controls.Add(centre);
            Controls.Add(leftPanel);
            Controls.Add(rightPanel);
            Controls.Add(splitter);
            Controls.Add(timelineArea);

            still.Rendered += (gen, bmp) =>
            {
                if (IsDisposed || !IsHandleCreated) { bmp.Dispose(); return; }
                try { BeginInvoke((MethodInvoker)(() => { if (gen == stillGen && !IsPlaying) preview.SetFrame(bmp); else bmp.Dispose(); })); }
                catch { bmp.Dispose(); }
            };
            playTimer.Tick += (s, e) => OnPlayTick();
            renderTimer.Tick += (s, e) => { renderTimer.Stop(); RenderNow(); };
            restartTimer.Tick += (s, e) => { restartTimer.Stop(); if (IsPlaying) { double t = playback.Position; StopPlayback(); playhead = t; StartPlayback(); } };
            AllowDrop = true;
            Theme.Changed += OnTheme;
            Rebuild();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnTheme;
                StopPlayback();
                still.Dispose();
                Library.Dispose();
                playTimer.Dispose(); renderTimer.Dispose(); restartTimer.Dispose();
                if (filterSource != null) filterSource.Dispose();
            }
            base.Dispose(disposing);
        }

        void OnTheme(object s, EventArgs e)
        {
            BackColor = Theme.Current.Window;
            RebuildTiles();
            RebuildProps();
            Invalidate(true);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            if (leftPanel != null && rightPanel != null && timelineArea != null)
            {
                int side = Math.Max(Theme.S(230), Math.Min(Theme.S(320), (int)(Width * 0.21)));
                leftPanel.Width = side;
                rightPanel.Width = side;
                leftPanel.Visible = rightPanel.Visible = !focusPreview && Width > Theme.S(900);
                int h = timelineHeight < 0 ? (int)(Height * 0.4) : timelineHeight;
                h = Math.Max(Theme.S(170), Math.Min(Height - Theme.S(230), h));
                if (h < Theme.S(120)) h = Theme.S(120);
                if (timelineArea.Height != h) timelineArea.Height = h;
            }
            base.OnLayout(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RequestPreview();
        }

        protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Theme.Current.Window); }

        // ================================================================== public state

        public Project Project { get { return project; } }
        public double Playhead { get { return playhead; } }
        public bool IsPlaying { get { return playback != null; } }
        public bool Snapping { get { return snapping; } }
        public bool MainMagnet { get { return mainMagnet; } }
        public bool PreviewMuted { get { return previewMuted; } }
        public bool FocusPreview { get { return focusPreview; } }
        public bool IsDirty { get { return version != savedVersion && !project.IsEmpty || (version != savedVersion && projectPath != null); } }
        public bool CanUndo { get { return undo.Count > 0; } }
        public bool CanRedo { get { return redo.Count > 0; } }
        public bool HasSelection { get { return selection.Count > 0; } }
        public int SelectionCount { get { return selection.Count; } }
        public string ProjectName { get { return projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : "Untitled project"; } }
        public string ProjectPath { get { return projectPath; } }

        public string EmptyHint
        {
            get { return "Import pictures, videos or music on the left, or click a video in File Explorer.\nDouble-click an item (or drag it down) to put it on the timeline."; }
        }

        public string StatusText
        {
            get
            {
                var sb = new StringBuilder(ProjectName);
                if (IsDirty) sb.Append(" *");
                sb.Append("    ").Append(project.Width).Append(" × ").Append(project.Height);
                sb.Append("    ").Append(project.Fps.ToString("0.##", CultureInfo.InvariantCulture)).Append(" fps");
                sb.Append("    ").Append(Tc(project.Duration));
                int n = project.ClipCount;
                sb.Append("    ").Append(n).Append(n == 1 ? " clip" : " clips");
                return sb.ToString();
            }
        }

        public string AspectLabel
        {
            get
            {
                int w = project.Width, h = project.Height;
                int g = Gcd(w, h);
                int a = w / g, b = h / g;
                if (a == 64 && b == 27) return "21:9";
                if (a > 40 || b > 40) return ((double)w / h).ToString("0.##", CultureInfo.InvariantCulture) + ":1";
                return a + ":" + b;
            }
        }

        static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return Math.Max(1, a); }

        /// <summary>Timecode hh:mm:ss:ff (hours only when needed).</summary>
        public static string Tc(double t)
        {
            if (double.IsNaN(t) || t < 0) t = 0;
            int fps = 30;
            long total = (long)Math.Floor(t * fps + 1e-6);
            long f = total % fps, s = (total / fps) % 60, m = (total / fps / 60) % 60, h = total / fps / 3600;
            return (h > 0 ? h + ":" : "") + m.ToString("00") + ":" + s.ToString("00") + ":" + f.ToString("00");
        }

        void Say(string text) { var h = Message; if (h != null) h(text); }
        void RaiseState() { var h = StateChanged; if (h != null) h(this, EventArgs.Empty); }

        Dictionary<int, MediaItem> MediaSnapshot()
        {
            var d = new Dictionary<int, MediaItem>();
            foreach (var m in Library.Items) d[m.Id] = m;
            return d;
        }

        public string ClipTitle(Clip c)
        {
            if (c.Kind == ClipKind.Text) return string.IsNullOrWhiteSpace(c.Text) ? "Text" : c.Text.Split('\n')[0];
            var m = Library.Find(c.MediaId);
            if (m != null) return m.Name;
            var r = project.FindMedia(c.MediaId);
            return r != null ? r.Name : "Missing media";
        }

        // ================================================================== undo / change tracking

        /// <summary>Takes an undo snapshot before a change. Changes with the same tag in quick succession share one step.</summary>
        public void BeginChange(string tag)
        {
            var now = DateTime.Now;
            if (tag != null && tag == lastTag && (now - lastTagTime).TotalSeconds < 1.5) { lastTagTime = now; return; }
            undo.Add(project.Clone());
            if (undo.Count > 200) undo.RemoveAt(0);
            redo.Clear();
            lastTag = tag;
            lastTagTime = now;
        }

        public void EndChange() { Changed(); }

        /// <summary>Call after a finished change: tidies the project and refreshes everything.</summary>
        public void Changed()
        {
            Normalize();
            version++;
            RefreshAll();
            if (IsPlaying) { restartTimer.Stop(); restartTimer.Start(); }
        }

        /// <summary>Call during a drag: cheaper refresh.</summary>
        public void LiveChanged()
        {
            Normalize();
            version++;
            Timeline.Invalidate();
            SyncProps();
            bar.Invalidate();
            preview.Invalidate();
            RequestPreview();
        }

        void Normalize()
        {
            foreach (var t in project.Tracks) t.Sort();
            if (mainMagnet)
            {
                double x = 0;
                foreach (var c in project.Main.Clips) { c.Start = x; x = c.End; }
            }
            // drop empty extra tracks
            for (int i = project.Tracks.Count - 1; i >= 1; i--)
                if (project.Tracks[i].Clips.Count == 0) project.Tracks.RemoveAt(i);
            foreach (var c in project.AllClips())
            {
                if (c.Start < 0) c.Start = 0;
                if (c.Duration < 0.02) c.Duration = 0.02;
            }
            selection.RemoveWhere(id => project.FindClip(id) == null);
        }

        public void Undo()
        {
            if (undo.Count == 0) { Say("Nothing to undo"); return; }
            StopPlayback();
            redo.Add(project.Clone());
            project = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            lastTag = null;
            version++;
            selection.RemoveWhere(id => project.FindClip(id) == null);
            propsFor = -2;
            RefreshAll();
        }

        public void Redo()
        {
            if (redo.Count == 0) { Say("Nothing to redo"); return; }
            StopPlayback();
            undo.Add(project.Clone());
            project = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            lastTag = null;
            version++;
            selection.RemoveWhere(id => project.FindClip(id) == null);
            propsFor = -2;
            RefreshAll();
        }

        void RefreshAll()
        {
            Timeline.Invalidate();
            preview.Invalidate();
            bar.Sync();
            SyncToolbar();
            RebuildPropsIfNeeded();
            SyncProps();
            RefreshMediaUsed();
            RequestPreview();
            RaiseState();
        }

        void Rebuild()
        {
            RebuildTiles();
            propsFor = -2;
            RefreshAll();
        }

        // ================================================================== selection

        public bool IsSelected(Clip c) { return selection.Contains(c.Id); }

        public Clip SingleSelected
        {
            get
            {
                if (selection.Count != 1) return null;
                return project.FindClip(selection.First());
            }
        }

        public IEnumerable<Clip> SelectedClips()
        {
            foreach (var id in selection.ToArray())
            {
                var c = project.FindClip(id);
                if (c != null) yield return c;
            }
        }

        public void Select(Clip c, bool toggle)
        {
            if (toggle) { if (!selection.Remove(c.Id)) selection.Add(c.Id); }
            else { selection.Clear(); selection.Add(c.Id); }
            OnSelectionChanged();
        }

        public void ClearSelection()
        {
            if (selection.Count == 0) return;
            selection.Clear();
            OnSelectionChanged();
        }

        public void SelectAll()
        {
            selection.Clear();
            foreach (var c in project.AllClips()) selection.Add(c.Id);
            OnSelectionChanged();
        }

        void OnSelectionChanged()
        {
            Timeline.Invalidate();
            preview.Invalidate();
            SyncToolbar();
            RebuildPropsIfNeeded();
            RebuildTileStates();
            RaiseState();
        }

        // ================================================================== playhead, playback, preview

        public void Scrub(double t, bool keepPlaying = false)
        {
            bool was = IsPlaying;
            if (was) StopPlayback();
            playhead = Math.Max(0, Math.Min(Math.Max(project.Duration, 0), t));
            Timeline.Invalidate();
            bar.Invalidate();
            preview.Invalidate();
            SyncProps();
            still.CancelStale();
            RenderNow();
            if (was && keepPlaying) StartPlayback();
        }

        public void EndScrub() { }

        public void SeekTo(double t)
        {
            bool was = IsPlaying;
            Scrub(t);
            Timeline.EnsureVisible(playhead);
            if (was) StartPlayback();
        }

        public void StepFrames(int n)
        {
            if (IsPlaying) Pause();
            double fd = project.FrameDuration;
            SeekTo(project.Snap(playhead + n * fd));
        }

        void JumpEdit(int dir)
        {
            var pts = new SortedSet<double> { 0, project.Duration };
            foreach (var c in project.AllClips()) { pts.Add(Math.Round(c.Start, 4)); pts.Add(Math.Round(c.End, 4)); }
            double target = dir > 0 ? pts.Where(x => x > playhead + 1e-3).DefaultIfEmpty(project.Duration).Min()
                                    : pts.Where(x => x < playhead - 1e-3).DefaultIfEmpty(0).Max();
            SeekTo(target);
        }

        public void TogglePlay() { if (IsPlaying) Pause(); else Play(); }

        public void Play()
        {
            if (IsPlaying) return;
            if (project.IsEmpty) { Say("Add something to the timeline first"); return; }
            if (playhead >= project.Duration - 0.04) playhead = 0;
            string waiting;
            if (!Library.AudioReady(project, out waiting)) Say("The sound of " + waiting + " is still being prepared – it plays silently until it's ready");
            StartPlayback();
        }

        public void Pause()
        {
            if (!IsPlaying) return;
            double t = playback.Position;
            StopPlayback();
            playhead = Math.Min(project.Duration, t);
            Timeline.Invalidate();
            bar.Sync();
            SyncProps();
            preview.Invalidate();
            RequestPreview();
            RaiseState();
        }

        void StartPlayback()
        {
            var size = preview.RenderSize(Math.Max(640, Math.Min(1280, (int)preview.CanvasRect.Width)));
            var pb = new EditorPlayback(project.Clone(), MediaSnapshot(), playhead, size, () => previewMuted ? 0f : previewVolume);
            pb.FrameReady += bmp => preview.PresentCopy(bmp);
            pb.Finished += () => Post(() =>
            {
                if (!ReferenceEquals(playback, pb)) return;
                StopPlayback();
                playhead = project.Duration;
                Timeline.Invalidate();
                bar.Sync();
                RequestPreview();
                RaiseState();
            });
            pb.Problem += msg => Post(() => Say("Playback problem: " + msg));
            playback = pb;
            preview.Message = null;
            stillGen++;
            pb.Start();
            playTimer.Start();
            bar.Sync();
            preview.Invalidate();
            RaiseState();
        }

        void StopPlayback()
        {
            restartTimer.Stop();
            playTimer.Stop();
            var pb = playback;
            playback = null;
            if (pb != null) pb.Dispose();
            bar.Sync();
        }

        void Post(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        void OnPlayTick()
        {
            if (playback == null) return;
            playhead = playback.Position;
            Timeline.Follow(playhead);
            bar.Invalidate();
        }

        public void RequestPreview()
        {
            if (IsPlaying) return;
            renderTimer.Stop();
            renderTimer.Start();
        }

        void RenderNow()
        {
            if (IsPlaying || !IsHandleCreated) return;
            bool empty = project.IsEmpty;
            preview.Message = empty ? "Your video starts here" : null;
            if (empty)
            {
                preview.SetFrame(null);
                return;
            }
            var size = preview.RenderSize(1600);
            stillGen = still.Request(project.Clone(), MediaSnapshot(), Math.Min(playhead, Math.Max(0, project.Duration - project.FrameDuration * 0.5)), size);
        }

        public void TogglePreviewMute() { previewMuted = !previewMuted; bar.Sync(); Say(previewMuted ? "Preview muted" : "Preview sound on"); }

        public void ToggleFocusPreview()
        {
            focusPreview = !focusPreview;
            PerformLayout();
            bar.Sync();
            RequestPreview();
        }

        // ================================================================== media bin

        public void ImportDialog()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Import media",
                Multiselect = true,
                Filter = "Videos, pictures and music|*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.webm;*.wmv;*.flv;*.mpg;*.mpeg;*.ts;*.mts;*.m2ts;*.3gp;*.ogv;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma|" +
                         FileTypes.VideoFilter + "|Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.ico|Music and sound|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma|All files|*.*"
            })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                ImportFiles(dlg.FileNames, false);
            }
        }

        /// <summary>Adds files to the media bin (and optionally to the end of the main track when ready).</summary>
        public void ImportFiles(IEnumerable<string> files, bool addToTimeline)
        {
            int n = 0, skipped = 0;
            foreach (var f in files)
            {
                if (!File.Exists(f) || !MediaLibrary.CanImport(f)) { skipped++; continue; }
                var m = Library.Add(f);
                n++;
                if (addToTimeline) QueueAdd(m);
            }
            tabs.Selected = "media";
            if (skipped > 0) Say(skipped + " file" + (skipped == 1 ? "" : "s") + " skipped – only videos, pictures (JPG, PNG, BMP, GIF, TIFF, ICO) and music can be imported");
            else if (n > 0) Say("Importing " + n + " file" + (n == 1 ? "" : "s") + "…");
        }

        /// <summary>A video shown in the viewer: keep it in the bin.</summary>
        public MediaItem AddFromViewer(string path)
        {
            if (!File.Exists(path)) return null;
            return Library.Add(path);
        }

        /// <summary>Opening the editor with a video loaded in the viewer: it goes on the timeline if it isn't there yet.</summary>
        public void OpenFromViewer(string path)
        {
            var m = AddFromViewer(path);
            if (m == null) return;
            if (project.UsesMedia(m.Id) || pendingAdds.Contains(m.Id)) return;
            QueueAdd(m);
        }

        void QueueAdd(MediaItem m)
        {
            if (m.IsUsable && m.Info != null) AddMediaToTimeline(m, false);
            else if (!pendingAdds.Contains(m.Id)) pendingAdds.Add(m.Id);
        }

        void OnMediaChanged(MediaItem m)
        {
            if (pendingAdds.Contains(m.Id))
            {
                if (m.Status == MediaStatus.Failed || m.Status == MediaStatus.Missing)
                {
                    pendingAdds.Remove(m.Id);
                    Say("Couldn't add " + m.Name + ": " + (m.Error ?? "it can't be read"));
                }
                else if (m.IsUsable && m.Info != null)
                {
                    pendingAdds.Remove(m.Id);
                    AddMediaToTimeline(m, false);
                }
            }
            UpdateMediaTile(m);
            if (project.UsesMedia(m.Id))
            {
                Timeline.Invalidate();
                RequestPreview();
            }
            if (m.Status == MediaStatus.Failed) Say(m.Name + ": " + m.Error);
        }

        void RefreshMedia()
        {
            var tiles = new List<Tile>();
            foreach (var m in Library.Items) tiles.Add(MediaTile(m));
            mediaGrid.SetTiles(tiles);
            UpdateFooter();
        }

        void RefreshMediaUsed()
        {
            foreach (var t in mediaGrid.Tiles)
            {
                var m = t.Tag as MediaItem;
                if (m != null) t.Used = project.UsesMedia(m.Id);
            }
            mediaGrid.Invalidate();
        }

        void UpdateMediaTile(MediaItem m)
        {
            int i = mediaGrid.Tiles.FindIndex(t => ReferenceEquals(t.Tag, m));
            if (i < 0) { RefreshMedia(); return; }
            var nt = MediaTile(m);
            nt.Selected = mediaGrid.Tiles[i].Selected;
            mediaGrid.Tiles[i] = nt;
            mediaGrid.Invalidate();
            UpdateFooter();
        }

        void UpdateFooter()
        {
            long bytes = Library.CacheBytes;
            int n = Library.Items.Count;
            mediaFooter.Text = n == 0 ? "Copies are kept in a private cache and deleted when you close the app."
                : n + " item" + (n == 1 ? "" : "s") + " · cached copies " + FormatBytes(bytes) + " · deleted when you close Stills & Reels";
            mediaFooterHost.Height = mediaFooter.MeasureHeight(Math.Max(100, leftPanel.Width - Theme.S(28))) + Theme.S(16);
            mediaFooter.Invalidate();
        }

        static string FormatBytes(long b)
        {
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#") + " MB";
            return (b / 1073741824.0).ToString("0.##") + " GB";
        }

        Tile MediaTile(MediaItem m)
        {
            var t = new Tile { Key = "m" + m.Id, Tag = m, Title = m.Name, ImageLock = m.Sync };
            lock (m.Sync) t.Image = m.Thumb;
            if (m.Kind == MediaKind.Audio)
            {
                t.Paint = (g, r) => DrawAudioTile(g, r, m);
                t.Image = null;
            }
            switch (m.Status)
            {
                case MediaStatus.Probing: t.Sub = "Reading…"; t.Progress = 0; break;
                case MediaStatus.Copying: t.Sub = "Copying to cache " + (int)(m.CopyProgress * 100) + "%"; t.Progress = m.CopyProgress; break;
                case MediaStatus.Missing: t.Sub = "File not found – right-click to locate"; t.Warn = true; break;
                case MediaStatus.Failed: t.Sub = m.Error ?? "Can't be used"; t.Warn = true; break;
                default:
                    if (m.Kind == MediaKind.Image) t.Sub = "Picture · " + m.PixelSize.Width + " × " + m.PixelSize.Height;
                    else if (m.Kind == MediaKind.Audio) t.Sub = "Sound · " + VideoControls.FormatTime(m.Duration, m.Duration);
                    else t.Sub = m.PixelSize.Width + " × " + m.PixelSize.Height + (m.HasAudio ? " · sound" : " · no sound");
                    if (m.Error != null) t.Sub = m.Error;
                    break;
            }
            if (m.Kind != MediaKind.Image && m.Duration > 0) t.Badge = VideoControls.FormatTime(m.Duration, m.Duration);
            if (m.Kind == MediaKind.Image) t.Badge = "Picture";
            t.Used = project.UsesMedia(m.Id);
            if (t.Image == null && t.Paint == null) t.Icon = m.Kind == MediaKind.Image ? Ico.Picture : Ico.Video;
            return t;
        }

        static void DrawAudioTile(Graphics g, RectangleF r, MediaItem m)
        {
            using (var b = new LinearGradientBrush(r, Color.FromArgb(18, 92, 84), Color.FromArgb(28, 60, 110), 0f)) g.FillRectangle(b, r);
            var peaks = m.Peaks;
            if (peaks != null && peaks.Length > 0)
            {
                using (var pen = new Pen(Color.FromArgb(200, 140, 245, 210), 1f))
                {
                    int n = (int)r.Width;
                    for (int x = 0; x < n; x += 2)
                    {
                        int i0 = (int)((long)x * peaks.Length / n), i1 = (int)((long)(x + 2) * peaks.Length / n);
                        float v = 0;
                        for (int i = i0; i < i1 && i < peaks.Length; i++) v = Math.Max(v, peaks[i]);
                        float h = v * r.Height * 0.42f;
                        g.DrawLine(pen, r.Left + x, r.Top + r.Height / 2 - h, r.Left + x, r.Top + r.Height / 2 + h);
                    }
                }
            }
            else Icons.Draw(g, Ico.Music, new RectangleF(r.Left + r.Width / 2 - 14, r.Top + r.Height / 2 - 14, 28, 28), Color.FromArgb(200, 255, 255, 255), 1.8f);
        }

        void OnMediaTileMenu(Tile t, Point screen)
        {
            var m = t.Tag as MediaItem;
            if (m == null) return;
            var menu = new ContextMenuStrip();
            bool ok = m.IsUsable && m.Info != null;
            menu.Items.Add(new ToolStripMenuItem("Add to the main track at the playhead", null, (s, e) => AddMediaToTimeline(m, true)) { Enabled = ok && m.Kind != MediaKind.Audio });
            menu.Items.Add(new ToolStripMenuItem("Add as an overlay at the playhead", null, (s, e) => AddOverlay(m)) { Enabled = ok && m.Kind != MediaKind.Audio });
            menu.Items.Add(new ToolStripMenuItem("Add as sound at the playhead", null, (s, e) => AddAsAudio(m)) { Enabled = ok && m.HasAudio });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Show original in File Explorer", null, (s, e) =>
            {
                try { if (File.Exists(m.OriginalPath)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + m.OriginalPath + "\""); } catch { }
            }) { Enabled = File.Exists(m.OriginalPath) && !m.Temporary });
            if (m.Status == MediaStatus.Missing || m.Status == MediaStatus.Failed)
                menu.Items.Add(new ToolStripMenuItem("Locate the file…", null, (s, e) => Relink(m)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Remove from media (deletes the cached copy)", null, (s, e) => RemoveMedia(m)));
            Theme.StyleMenu(menu);
            menu.Closed += (s, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            menu.Show(screen);
        }

        void Relink(MediaItem m)
        {
            using (var dlg = new OpenFileDialog { Title = "Locate " + m.Name, Filter = "All files|*.*", FileName = m.Name })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                Library.Relink(m, dlg.FileName);
                var r = project.FindMedia(m.Id);
                if (r != null) { r.Path = dlg.FileName; r.Name = Path.GetFileName(dlg.FileName); }
                version++;
                RaiseState();
            }
        }

        public void RemoveMedia(MediaItem m)
        {
            int uses = project.AllClips().Count(c => c.MediaId == m.Id && c.Kind != ClipKind.Text);
            if (uses > 0)
            {
                var r = MessageBox.Show(FindForm(), m.Name + " is used by " + uses + " clip" + (uses == 1 ? "" : "s") + " on the timeline.\n\nRemove it and those clips?",
                    "Remove media", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;
                StopPlayback();
                BeginChange(null);
                foreach (var t in project.Tracks) t.Clips.RemoveAll(c => c.MediaId == m.Id && c.Kind != ClipKind.Text);
                project.Media.RemoveAll(x => x.Id == m.Id);
                Changed();
            }
            pendingAdds.Remove(m.Id);
            Library.Remove(m);
            Say("Removed " + m.Name + " and deleted its cached copy");
        }

        void EnsureRef(MediaItem m)
        {
            var r = project.FindMedia(m.Id);
            if (r == null) project.Media.Add(new MediaRef { Id = m.Id, Path = m.OriginalPath, Name = m.Name });
        }

        Clip NewClip(MediaItem m)
        {
            var c = new Clip { Id = project.NewId(), MediaId = m.Id };
            switch (m.Kind)
            {
                case MediaKind.Image: c.Kind = ClipKind.Image; c.Duration = 5; break;
                case MediaKind.Audio: c.Kind = ClipKind.Audio; c.Duration = Math.Max(0.1, m.Duration); break;
                default: c.Kind = ClipKind.Video; c.Duration = Math.Max(0.1, m.Duration); break;
            }
            return c;
        }

        /// <summary>Adds a bin item: sound to an audio track, pictures and videos to the main track (at the playhead, or at the end).</summary>
        public void AddMediaToTimeline(MediaItem m, bool atPlayhead)
        {
            if (!m.IsUsable || m.Info == null) { Say(m.Name + " isn't ready yet"); if (m.IsUsable) QueueAdd(m); return; }
            if (m.Kind == MediaKind.Audio) { AddAsAudio(m); return; }
            StopPlayback();
            BeginChange(null);
            EnsureRef(m);
            bool matched = MatchCanvasToFirstVideo(m);
            var c = NewClip(m);
            var main = project.Main;
            if (!atPlayhead || main.Clips.Count == 0)
            {
                c.Start = main.End;
                main.Clips.Add(c);
            }
            else InsertOnMain(c, playhead, true);
            Changed();
            Select(c, false);
            if (!atPlayhead) Timeline.EnsureVisible(c.Start);
            Say("Added " + m.Name + (matched ? " – canvas set to " + project.Width + " × " + project.Height + ", " + Math.Round(project.Fps, 2) + " fps to match it" : ""));
        }

        /// <summary>
        /// The first video put on an untouched project (default canvas, nothing on the video tracks) sets the
        /// canvas size and frame rate, so its pictures are never scaled or retimed on export. Call inside a change.
        /// </summary>
        bool MatchCanvasToFirstVideo(MediaItem m)
        {
            if (m.Kind != MediaKind.Video || m.Info == null || !m.Info.HasVideo) return false;
            var fresh = new Project();
            if (project.Width != fresh.Width || project.Height != fresh.Height || Math.Abs(project.Fps - fresh.Fps) > 0.001) return false;
            if (project.Tracks.Any(t => t.Kind == TrackKind.Video && t.Clips.Count > 0)) return false;
            var s = m.PixelSize;
            if (s.Width < 16 || s.Height < 16) return false;
            double k = Math.Min(1, 3840.0 / Math.Max(s.Width, s.Height));
            project.Width = Math.Max(16, (int)(s.Width * k) & ~1);
            project.Height = Math.Max(16, (int)(s.Height * k) & ~1);
            if (m.Info.Fps >= 1 && m.Info.Fps <= 120) project.Fps = Math.Round(m.Info.Fps, 3);
            return true;
        }

        /// <summary>Inserts into the main track at time t: after the clip under t (or exactly at a join).</summary>
        void InsertOnMain(Clip c, double t, bool afterClipUnder)
        {
            var main = project.Main;
            main.Sort();
            int idx = main.Clips.Count;
            for (int i = 0; i < main.Clips.Count; i++)
            {
                var o = main.Clips[i];
                if (t < o.Start + (afterClipUnder ? 1e-6 : o.Duration / 2)) { idx = i; break; }
                if (afterClipUnder && t < o.End - 1e-6) { idx = i + 1; break; }
            }
            if (!mainMagnet)
            {
                double start = idx < main.Clips.Count ? main.Clips[idx].Start : main.End;
                if (idx > 0) start = Math.Max(start, main.Clips[idx - 1].End);
                // push later clips to make room
                for (int i = idx; i < main.Clips.Count; i++) main.Clips[i].Start += c.Duration;
                c.Start = idx > 0 ? main.Clips[idx - 1].End : 0;
            }
            main.Clips.Insert(idx, c);
            if (mainMagnet)
            {
                double x = 0;
                foreach (var o in main.Clips) { o.Start = x; x = o.End; }
            }
        }

        Track FreeTrack(TrackKind kind, double start, double end, Track prefer = null)
        {
            if (prefer != null && prefer.Kind == kind && !prefer.Locked && !ReferenceEquals(prefer, project.Main) && prefer.IsFree(start, end)) return prefer;
            foreach (var t in project.Tracks)
                if (t.Kind == kind && !t.Locked && !ReferenceEquals(t, project.Main) && t.IsFree(start, end)) return t;
            var nt = new Track { Id = project.NewId(), Kind = kind };
            project.Tracks.Add(nt);
            return nt;
        }

        public void AddOverlay(MediaItem m)
        {
            if (!m.IsUsable || m.Info == null) return;
            StopPlayback();
            BeginChange(null);
            EnsureRef(m);
            var c = NewClip(m);
            c.Start = project.Snap(playhead);
            c.Scale = 0.5;
            var t = FreeTrack(TrackKind.Video, c.Start, c.End);
            t.Clips.Add(c);
            Changed();
            Select(c, false);
        }

        public void AddAsAudio(MediaItem m)
        {
            if (!m.IsUsable || m.Info == null || !m.HasAudio) return;
            StopPlayback();
            BeginChange(null);
            EnsureRef(m);
            var c = new Clip { Id = project.NewId(), Kind = ClipKind.Audio, MediaId = m.Id, Start = project.Snap(playhead), Duration = Math.Max(0.1, m.Duration) };
            var t = FreeTrack(TrackKind.Audio, c.Start, c.End);
            t.Clips.Add(c);
            Changed();
            Select(c, false);
            Say("Added the sound of " + m.Name);
        }

        public void DropMedia(int mediaId, Track target, bool newTrack, double t)
        {
            var m = Library.Find(mediaId);
            if (m == null) return;
            if (!m.IsUsable || m.Info == null) { Say(m.Name + " isn't ready yet – try again in a moment"); return; }
            StopPlayback();
            BeginChange(null);
            EnsureRef(m);
            if (MatchCanvasToFirstVideo(m)) Say("Canvas set to " + project.Width + " × " + project.Height + ", " + Math.Round(project.Fps, 2) + " fps to match " + m.Name);
            var c = NewClip(m);
            bool audio = c.Kind == ClipKind.Audio;
            TrackKind kind = audio ? TrackKind.Audio : TrackKind.Video;
            if (target != null && target.Kind != kind) { target = null; newTrack = false; }
            c.Start = Math.Max(0, t);
            if (newTrack || (target == null && audio))
            {
                var nt = new Track { Id = project.NewId(), Kind = kind };
                project.Tracks.Add(nt);
                nt.Clips.Add(c);
            }
            else if (target == null || ReferenceEquals(target, project.Main))
            {
                if (mainMagnet || project.Main.IsFree(c.Start, c.End)) { if (mainMagnet) InsertOnMain(c, t, false); else project.Main.Clips.Add(c); }
                else FreeTrack(kind, c.Start, c.End).Clips.Add(c);
            }
            else if (target.IsFree(c.Start, c.End)) target.Clips.Add(c);
            else FreeTrack(kind, c.Start, c.End).Clips.Add(c);
            Changed();
            Select(c, false);
        }

        // ================================================================== clip editing commands

        public void MoveClip(Clip c, Track target, bool newTrack, double start)
        {
            var from = project.TrackOf(c);
            if (from == null) return;
            if (!newTrack && ReferenceEquals(target, from) && Math.Abs(start - c.Start) < 1e-6) return;
            StopPlayback();
            BeginChange(null);
            from.Clips.Remove(c);
            var kind = c.Kind == ClipKind.Audio ? TrackKind.Audio : TrackKind.Video;
            if (newTrack || target == null)
            {
                var nt = new Track { Id = project.NewId(), Kind = kind };
                project.Tracks.Add(nt);
                c.Start = start;
                nt.Clips.Add(c);
            }
            else if (ReferenceEquals(target, project.Main) && mainMagnet)
            {
                InsertOnMain(c, start + c.Duration / 2 - 1e-4, false);
            }
            else if (target.IsFree(start, start + c.Duration))
            {
                c.Start = start;
                target.Clips.Add(c);
            }
            else
            {
                // overlapping: put it on a new track rather than covering another clip
                c.Start = start;
                var nt = new Track { Id = project.NewId(), Kind = kind };
                int at = project.Tracks.IndexOf(target);
                project.Tracks.Insert(Math.Max(1, at + 1), nt);
                nt.Clips.Add(c);
            }
            Changed();
        }

        IEnumerable<Clip> TargetsAtPlayhead()
        {
            var sel = SelectedClips().Where(c => playhead > c.Start + 1e-3 && playhead < c.End - 1e-3).ToList();
            if (sel.Count > 0) return sel;
            if (selection.Count > 0) return sel;
            var m = project.Main.ClipAt(playhead);
            return m != null && playhead > m.Start + 1e-3 ? new List<Clip> { m } : new List<Clip>();
        }

        public void SplitSelected()
        {
            var targets = TargetsAtPlayhead().Where(c => !project.TrackOf(c).Locked).ToList();
            if (targets.Count == 0) { Say("Put the playhead over a clip to split it"); return; }
            StopPlayback();
            BeginChange(null);
            double t = project.Snap(playhead);
            Clip last = null;
            foreach (var c in targets)
            {
                var b = SplitClip(c, t);
                if (b != null) last = b;
            }
            Changed();
            if (last != null) Select(last, false);
        }

        Clip SplitClip(Clip c, double t)
        {
            double fd = project.FrameDuration;
            if (t <= c.Start + fd * 0.5 || t >= c.End - fd * 0.5) return null;
            var track = project.TrackOf(c);
            var b = c.Clone();
            b.Id = project.NewId();
            double local = t - c.Start;
            b.Start = t;
            b.Duration = c.End - t;
            b.In = c.In + local * c.Speed;
            b.Transition = "";
            b.FadeIn = 0; b.AudioFadeIn = 0;
            c.FadeOut = 0; c.AudioFadeOut = 0;
            c.Duration = local;
            if (c.HasKeys)
            {
                var p = c.PlacementAt(t);
                var mid = new Keyframe { T = local, X = p.X, Y = p.Y, Scale = p.Scale, Rotation = p.Rotation, Opacity = p.Opacity };
                var aKeys = c.Keys.Where(k => k.T < local - 1e-6).Select(k => k.Clone()).ToList();
                aKeys.Add(mid.Clone());
                var bKeys = c.Keys.Where(k => k.T > local + 1e-6).Select(k => { var n = k.Clone(); n.T -= local; return n; }).ToList();
                var mb = mid.Clone(); mb.T = 0; bKeys.Insert(0, mb);
                c.Keys = aKeys; b.Keys = bKeys;
            }
            track.Clips.Add(b);
            return b;
        }

        public void DeleteSelected(bool ripple)
        {
            var list = SelectedClips().Where(c => !project.TrackOf(c).Locked).ToList();
            if (list.Count == 0) { Say("Select a clip to delete"); return; }
            StopPlayback();
            BeginChange(null);
            foreach (var c in list)
            {
                var tr = project.TrackOf(c);
                tr.Clips.Remove(c);
                if (ripple && !(mainMagnet && ReferenceEquals(tr, project.Main)))
                    foreach (var o in tr.Clips) if (o.Start >= c.End - 1e-6) o.Start -= c.Duration;
            }
            selection.Clear();
            Changed();
            OnSelectionChanged();
        }

        public void DuplicateSelected()
        {
            var list = SelectedClips().ToList();
            if (list.Count == 0) { Say("Select a clip to duplicate"); return; }
            StopPlayback();
            BeginChange(null);
            Clip last = null;
            foreach (var c in list)
            {
                var tr = project.TrackOf(c);
                var d = c.Clone();
                d.Id = project.NewId();
                if (ReferenceEquals(tr, project.Main) && mainMagnet)
                {
                    int i = tr.Clips.IndexOf(c);
                    tr.Clips.Insert(i + 1, d);
                    double x = 0;
                    foreach (var o in tr.Clips) { o.Start = x; x = o.End; }
                }
                else
                {
                    d.Start = c.End;
                    var target = tr.IsFree(d.Start, d.End) ? tr : FreeTrack(tr.Kind, d.Start, d.End);
                    target.Clips.Add(d);
                }
                last = d;
            }
            Changed();
            if (last != null) Select(last, false);
        }

        public void CopySelected(bool cut)
        {
            var list = SelectedClips().ToList();
            if (list.Count == 0) return;
            clipboard.Clear();
            double first = list.Min(c => c.Start);
            foreach (var c in list) { var k = c.Clone(); k.Start -= first; clipboard.Add(k); }
            // the Windows clipboard is left alone; its change counter tells whether something newer was copied since
            clipboardSeq = ClipboardSeq();
            if (cut) DeleteSelected(false);
            Say(list.Count + " clip" + (list.Count == 1 ? "" : "s") + (cut ? " cut" : " copied"));
        }

        /// <summary>Ctrl+V: pasted clips, a picture from the clipboard (as an overlay), or files.</summary>
        public void Paste()
        {
            if (clipboard.Count > 0 && clipboardSeq != 0 && ClipboardSeq() == clipboardSeq) { PasteClips(); return; }
            Bitmap pic = MainForm.ClipboardPicture();
            if (pic != null) { AddPicture(pic, "Pasted picture"); return; }
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                    ImportFiles(files, false);
                    return;
                }
            }
            catch { }
            if (clipboard.Count > 0) { PasteClips(); return; }
            Say("The clipboard has nothing to paste");
        }

        void PasteClips()
        {
            StopPlayback();
            BeginChange(null);
            Clip last = null;
            double at = project.Snap(playhead);
            foreach (var k in clipboard)
            {
                var c = k.Clone();
                c.Id = project.NewId();
                c.Start = at + k.Start;
                var kind = c.Kind == ClipKind.Audio ? TrackKind.Audio : TrackKind.Video;
                FreeTrack(kind, c.Start, c.End).Clips.Add(c);
                last = c;
            }
            Changed();
            if (last != null) Select(last, false);
        }

        /// <summary>Adds a picture (pasted, snipped or a freeze frame) to the bin and as an overlay at the playhead.</summary>
        public void AddPicture(Bitmap pic, string name)
        {
            string path = Path.Combine(MediaLibrary.Folder, name + " " + DateTime.Now.ToString("HHmmss") + ".png");
            try { pic.Save(path, ImageFormat.Png); }
            catch (Exception ex) { Say("Couldn't keep the picture: " + ex.Message); pic.Dispose(); return; }
            pic.Dispose();
            var m = Library.Add(path, 0, true);
            if (m.IsUsable && m.Info != null) AddOverlay(m);
            else { overlayWhenReady.Add(m.Id); QueueOverlay(m); }
            tabs.Selected = "media";
        }

        readonly List<int> overlayWhenReady = new List<int>();

        void QueueOverlay(MediaItem m)
        {
            // images are analysed in the background; poll briefly
            var timer = new System.Windows.Forms.Timer { Interval = 100 };
            int tries = 0;
            timer.Tick += (s, e) =>
            {
                tries++;
                if (m.Status == MediaStatus.Ready && m.Info != null) { timer.Stop(); timer.Dispose(); overlayWhenReady.Remove(m.Id); AddOverlay(m); }
                else if (tries > 100 || m.Status == MediaStatus.Failed) { timer.Stop(); timer.Dispose(); overlayWhenReady.Remove(m.Id); }
            };
            timer.Start();
        }

        public void FreezeFrame()
        {
            var c = SingleSelected;
            if (c == null || c.Kind != ClipKind.Video || playhead < c.Start || playhead >= c.End)
            {
                c = project.Main.ClipAt(playhead);
                if (c == null || c.Kind != ClipKind.Video) { Say("Put the playhead over a video clip to freeze a frame"); return; }
            }
            var m = Library.Find(c.MediaId);
            if (m == null || !m.IsUsable) return;
            StopPlayback();
            double src = c.SourceTime(playhead);
            Cursor = Cursors.WaitCursor;
            var clip = c;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Bitmap frame = null;
                try { frame = FrameGrabber.GrabFull(m, src); } catch { }
                Post(() =>
                {
                    Cursor = Cursors.Default;
                    if (frame == null) { Say("Couldn't read that frame"); return; }
                    string path = Path.Combine(MediaLibrary.Folder, "Freeze frame " + DateTime.Now.ToString("HHmmss") + ".png");
                    try { frame.Save(path, ImageFormat.Png); } catch (Exception ex) { Say("Couldn't keep the frame: " + ex.Message); frame.Dispose(); return; }
                    frame.Dispose();
                    var fm = Library.Add(path, 0, true);
                    WaitReady(fm, () => InsertFreeze(clip, fm));
                });
            });
        }

        void WaitReady(MediaItem m, Action then)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 60 };
            int tries = 0;
            timer.Tick += (s, e) =>
            {
                tries++;
                if (m.Status == MediaStatus.Ready && m.Info != null) { timer.Stop(); timer.Dispose(); then(); }
                else if (tries > 150 || m.Status == MediaStatus.Failed) { timer.Stop(); timer.Dispose(); Say("Couldn't prepare the picture"); }
            };
            timer.Start();
        }

        void InsertFreeze(Clip c, MediaItem fm)
        {
            if (project.FindClip(c.Id) == null) return;
            BeginChange(null);
            EnsureRef(fm);
            var track = project.TrackOf(c);
            double t = project.Snap(playhead);
            var second = SplitClip(c, t);
            var p = c.PlacementAt(Math.Max(c.Start, Math.Min(c.End - 1e-3, t)));
            var f = c.Clone();
            f.Id = project.NewId();
            f.Kind = ClipKind.Image;
            f.MediaId = fm.Id;
            f.In = 0; f.Speed = 1; f.Keys = new List<Keyframe>();
            f.X = p.X; f.Y = p.Y; f.Scale = p.Scale; f.Rotation = p.Rotation; f.Opacity = p.Opacity;
            f.FadeIn = f.FadeOut = 0; f.Transition = ""; f.Stabilize = false;
            f.Start = second != null ? t : c.End;
            f.Duration = 3;
            // make room on the clip's track
            foreach (var o in track.Clips) if (o.Start >= f.Start - 1e-6 && !ReferenceEquals(o, c)) o.Start += f.Duration;
            track.Clips.Add(f);
            Changed();
            Select(f, false);
            Say("Freeze frame added (3 seconds)");
        }

        public void DetachAudio()
        {
            var c = SingleSelected;
            if (c == null || c.Kind != ClipKind.Video) { Say("Select a video clip to detach its sound"); return; }
            var m = Library.Find(c.MediaId);
            if (m == null || !m.HasAudio) { Say("This video has no sound"); return; }
            if (c.Muted) { Say("This clip's sound is already off"); return; }
            StopPlayback();
            BeginChange(null);
            var a = new Clip
            {
                Id = project.NewId(), Kind = ClipKind.Audio, MediaId = c.MediaId, Start = c.Start, Duration = c.Duration, In = c.In, Speed = c.Speed,
                Volume = c.Volume, AudioFadeIn = c.AudioFadeIn, AudioFadeOut = c.AudioFadeOut,
            };
            FreeTrack(TrackKind.Audio, a.Start, a.End).Clips.Add(a);
            c.Muted = true;
            Changed();
            Select(a, false);
            Say("Sound detached to an audio track");
        }

        static readonly Dictionary<string, string> TextNames = new Dictionary<string, string>
        {
            { "plain", "Default text" }, { "title", "Title" }, { "subtitle", "Subtitle" }, { "lower", "Lower third" },
            { "caption", "Caption" }, { "outline", "Outline" }, { "neon", "Neon" }
        };

        public static Clip TextPreset(string style)
        {
            var c = new Clip { Kind = ClipKind.Text, Duration = 3, TextStyle = style == "plain" ? "plain" : style, Shadow = true };
            switch (style)
            {
                case "title": c.Text = "Title"; c.FontSize = 0.12; c.Bold = true; c.Y = -0.04; break;
                case "subtitle": c.Text = "Subtitle"; c.FontSize = 0.055; c.OutlineWidth = 0.06; c.Shadow = false; c.Y = 0.36; c.TextStyle = "plain"; break;
                case "lower":
                    c.Text = "Your name\nRole or place"; c.FontSize = 0.06; c.Align = 0; c.X = -0.27; c.Y = 0.3; c.Duration = 5;
                    c.BoxOpacity = 0.75; c.OutlineColor = unchecked((int)0xFF2668FF); c.Shadow = false; break;
                case "caption": c.Text = "Caption text"; c.FontSize = 0.05; c.BoxOpacity = 0.6; c.Shadow = false; c.Y = 0.38; c.TextStyle = "plain"; break;
                case "outline": c.Text = "OUTLINE"; c.FontSize = 0.11; c.Bold = true; c.OutlineWidth = 0.08; c.OutlineColor = unchecked((int)0xFF2668FF); c.Shadow = false; c.TextStyle = "plain"; break;
                case "neon": c.Text = "Neon"; c.FontSize = 0.11; c.Bold = true; c.OutlineColor = unchecked((int)0xFFFF3CC8); c.Shadow = false; break;
                default: c.Text = "Add text"; c.FontSize = 0.08; c.TextStyle = "plain"; break;
            }
            return c;
        }

        public void AddText(string style)
        {
            StopPlayback();
            BeginChange(null);
            var c = TextPreset(style);
            c.Id = project.NewId();
            c.Start = project.Snap(playhead);
            if (c.Start >= project.Duration - 0.05 && project.Duration > 0) c.Start = Math.Max(0, project.Snap(project.Duration - c.Duration));
            Track prefer = project.Tracks.LastOrDefault(t => t.Kind == TrackKind.Video && t.Clips.Any(x => x.Kind == ClipKind.Text));
            FreeTrack(TrackKind.Video, c.Start, c.End, prefer).Clips.Add(c);
            Changed();
            propsPage = "text";
            Select(c, false);
            Say("Text added – type in the box on the right");
            FocusTextBox();
        }

        void FocusTextBox()
        {
            foreach (Control ctl in props.Controls)
                if (ctl is TextBox) { ctl.Focus(); ((TextBox)ctl).SelectAll(); break; }
        }

        public void ApplyTransition(string id)
        {
            var list = SelectedClips().Where(c => c.IsVisual).ToList();
            if (list.Count == 0) { Say("Select the clip the transition should lead into"); return; }
            BeginChange(null);
            foreach (var c in list)
            {
                c.Transition = id ?? "";
                if (!string.IsNullOrEmpty(id)) c.TransitionDuration = Math.Min(Math.Max(0.2, c.TransitionDuration <= 0 ? 0.6 : c.TransitionDuration), Math.Max(0.2, c.Duration));
            }
            Changed();
            if (!string.IsNullOrEmpty(id) && list.Count == 1)
            {
                var c = list[0];
                double d = c.TransitionDuration;
                bool joined = project.TrackOf(c).Clips.Any(o => !ReferenceEquals(o, c) && Math.Abs(o.End - c.Start) < 0.02);
                Scrub(Math.Max(0, c.Start - (joined ? d / 2 + 0.1 : 0)));
                Play();
            }
            Say(string.IsNullOrEmpty(id) ? "Transition removed" : Looks.TransitionName(id) + " transition added");
        }

        public void ApplyFilter(string id)
        {
            var list = SelectedClips().Where(c => c.Kind == ClipKind.Video || c.Kind == ClipKind.Image).ToList();
            if (list.Count == 0) { Say("Select a video or picture clip first"); return; }
            BeginChange(null);
            foreach (var c in list) { c.Filter = id ?? ""; if (c.FilterAmount <= 0.01) c.FilterAmount = 1; }
            Changed();
            Say(string.IsNullOrEmpty(id) ? "Filter removed" : Looks.FilterName(id) + " filter applied");
        }

        public void ToggleEffect(string id)
        {
            var c = SingleSelected;
            if (c == null || !c.IsVisual) { Say("Select a clip first"); return; }
            if (c.Kind == ClipKind.Text && id != "fade") { Say("This effect works on videos and pictures"); return; }
            BeginChange(null);
            switch (id)
            {
                case "blur": c.Blur = c.Blur > 0.5 ? 0 : 35; break;
                case "sharpen": c.Sharpen = c.Sharpen > 0.5 ? 0 : 45; break;
                case "vignette": c.Vignette = c.Vignette > 0.5 ? 0 : 55; break;
                case "grain": c.Grain = c.Grain > 0.5 ? 0 : 35; break;
                case "chroma": c.ChromaKey = !c.ChromaKey; break;
                case "stabilize": c.Stabilize = !c.Stabilize && c.Kind == ClipKind.Video; break;
                case "fade":
                    bool on = c.FadeIn > 0.01 || c.FadeOut > 0.01;
                    c.FadeIn = c.FadeOut = on ? 0 : Math.Min(0.6, c.Duration / 3);
                    if (c.Kind != ClipKind.Text && c.Kind != ClipKind.Image) c.AudioFadeIn = c.AudioFadeOut = on ? 0 : Math.Min(0.6, c.Duration / 3);
                    break;
            }
            propsPage = "effects";
            if (id == "fade") propsPage = c.Kind == ClipKind.Text ? "basic" : "basic";
            propsFor = -2;
            Changed();
        }

        // ---------------------------------------------------------------- keyframes and transform

        public void ToggleKeyframe()
        {
            var c = SingleSelected;
            if (c == null || !c.IsVisual) { Say("Select a clip to animate"); return; }
            if (playhead < c.Start - 1e-6 || playhead > c.End + 1e-6) { Say("Move the playhead over the clip to add a keyframe"); return; }
            BeginChange(null);
            var k = c.KeyAt(playhead, project.FrameDuration * 0.5);
            if (k != null) { c.Keys.Remove(k); Say("Keyframe removed"); }
            else
            {
                var p = c.PlacementAt(playhead);
                c.Keys.Add(new Keyframe { T = Math.Max(0, Math.Min(c.Duration, project.Snap(playhead) - c.Start)), X = p.X, Y = p.Y, Scale = p.Scale, Rotation = p.Rotation, Opacity = p.Opacity });
                c.SortKeys();
                Say(c.Keys.Count == 1 ? "Keyframe added – move the playhead and change position, scale, rotation or opacity to animate" : "Keyframe added");
            }
            Changed();
        }

        void JumpKey(int dir)
        {
            var c = SingleSelected;
            if (c == null || !c.HasKeys) return;
            double lt = playhead - c.Start;
            var k = dir > 0 ? c.Keys.FirstOrDefault(x => x.T > lt + 1e-3) : c.Keys.LastOrDefault(x => x.T < lt - 1e-3);
            if (k != null) SeekTo(c.Start + k.T);
        }

        /// <summary>Sets position/scale/rotation/opacity at the playhead: on the keyframe there when the clip is animated.</summary>
        public void SetPlacement(Clip c, Placement p)
        {
            if (c.HasKeys)
            {
                double lt = Math.Max(0, Math.Min(c.Duration, project.Snap(playhead) - c.Start));
                var k = c.KeyAt(playhead, project.FrameDuration * 0.5);
                if (k == null) { k = new Keyframe { T = lt }; c.Keys.Add(k); c.SortKeys(); }
                k.X = p.X; k.Y = p.Y; k.Scale = p.Scale; k.Rotation = p.Rotation; k.Opacity = p.Opacity;
            }
            else { c.X = p.X; c.Y = p.Y; c.Scale = p.Scale; c.Rotation = p.Rotation; c.Opacity = p.Opacity; }
            LiveChanged();
        }

        public void SetSpeed(Clip c, double s)
        {
            s = Math.Max(0.1, Math.Min(10, s));
            double old = c.Speed;
            if (Math.Abs(old - s) < 1e-6) return;
            double srcSpan = c.Duration * old;
            c.Speed = s;
            c.Duration = Math.Max(project.FrameDuration, srcSpan / s);
            foreach (var k in c.Keys) k.T *= old / s;
            var tr = project.TrackOf(c);
            if (tr != null && !(mainMagnet && ReferenceEquals(tr, project.Main)))
            {
                double next = tr.Clips.Where(o => !ReferenceEquals(o, c) && o.Start >= c.Start + 1e-6).Select(o => o.Start).DefaultIfEmpty(double.MaxValue).Min();
                if (c.End > next) c.Duration = next - c.Start;
            }
            var m = Library.Find(c.MediaId);
            if (m != null && m.HasAudio) { var mm = m; double ss = s; ThreadPool.QueueUserWorkItem(_ => MediaLibrary.PcmFor(mm, ss)); }
        }

        public Clip ClipAtCanvas(PointF pt)
        {
            var comp = new Compositor(project, id => Library.Find(id));
            for (int i = project.Tracks.Count - 1; i >= 0; i--)
            {
                var tr = project.Tracks[i];
                if (tr.Kind != TrackKind.Video || tr.Hidden) continue;
                var c = tr.ClipAt(playhead);
                if (c == null) continue;
                var q = comp.Outline(c, playhead, project.Width, project.Height);
                if (q == null) continue;
                using (var path = new GraphicsPath()) { path.AddPolygon(q); if (path.IsVisible(pt)) return c; }
            }
            return null;
        }

        public void ToggleTrack(Track t, string what)
        {
            BeginChange(null);
            switch (what)
            {
                case "lock": t.Locked = !t.Locked; Say(t.Locked ? "Track locked" : "Track unlocked"); break;
                case "hide": t.Hidden = !t.Hidden; Say(t.Hidden ? "Track hidden" : "Track shown"); break;
                case "mute": t.Muted = !t.Muted; Say(t.Muted ? "Track muted" : "Track sound on"); break;
            }
            Changed();
        }

        public void ToggleSnapping() { snapping = !snapping; SyncToolbar(); Say(snapping ? "Snapping on" : "Snapping off"); }

        public void ToggleMainMagnet()
        {
            mainMagnet = !mainMagnet;
            SyncToolbar();
            if (mainMagnet) { BeginChange(null); Changed(); }
            Say(mainMagnet ? "Main track magnet on – clips stay joined" : "Main track magnet off – clips can have gaps");
        }

        // ================================================================== project settings

        public void SetCanvas(int w, int h)
        {
            if (w == project.Width && h == project.Height) return;
            StopPlayback();
            BeginChange(null);
            project.Width = w & ~1;
            project.Height = h & ~1;
            Changed();
            Say("Canvas " + project.Width + " × " + project.Height);
        }

        void SetCanvasShape(int a, int b)
        {
            int baseSide = Math.Min(project.Width, project.Height);
            if (baseSide < 360) baseSide = 1080;
            int w, h;
            if (a >= b) { h = baseSide; w = (int)Math.Round(h * (double)a / b); }
            else { w = baseSide; h = (int)Math.Round(w * (double)b / a); }
            SetCanvas(w, h);
        }

        void MatchFirstClip()
        {
            var c = project.Main.Clips.FirstOrDefault(x => x.Kind == ClipKind.Video || x.Kind == ClipKind.Image);
            var m = c != null ? Library.Find(c.MediaId) : null;
            if (m == null) { Say("Put a video or picture on the main track first"); return; }
            var s = m.PixelSize;
            double k = Math.Min(1, 3840.0 / Math.Max(s.Width, s.Height));
            SetCanvas((int)(s.Width * k), (int)(s.Height * k));
            if (m.Info != null && m.Info.Fps > 1 && m.Kind == MediaKind.Video) SetFps(Math.Round(m.Info.Fps, 3));
        }

        void SetResolution(int shortSide)
        {
            double r = (double)project.Width / project.Height;
            int w, h;
            if (r >= 1) { h = shortSide; w = (int)Math.Round(h * r); }
            else { w = shortSide; h = (int)Math.Round(w / r); }
            SetCanvas(w, h);
        }

        void SetFps(double f)
        {
            if (Math.Abs(f - project.Fps) < 0.001) return;
            BeginChange(null);
            project.Fps = f;
            Changed();
        }

        public void ShowAspectMenu(Control anchor)
        {
            var menu = new ContextMenuStrip();
            foreach (var r in new[] { new[] { 16, 9 }, new[] { 9, 16 }, new[] { 1, 1 }, new[] { 4, 3 }, new[] { 3, 4 }, new[] { 4, 5 }, new[] { 21, 9 } })
            {
                int a = r[0], b = r[1];
                string name = a + ":" + b + (a == 16 ? "  (YouTube, TV)" : a == 9 ? "  (TikTok, Reels, Shorts)" : a == 1 ? "  (square)" : a == 4 && b == 5 ? "  (Instagram portrait)" : a == 21 ? "  (cinema)" : "");
                menu.Items.Add(new ToolStripMenuItem(name, null, (s, e) => SetCanvasShape(a, b)) { Checked = AspectLabel == a + ":" + b });
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Match the first clip", null, (s, e) => MatchFirstClip()));
            Theme.StyleMenu(menu);
            menu.Closed += (s, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            menu.Show(anchor, new Point(0, -menu.GetPreferredSize(Size.Empty).Height - Theme.S(4)));
        }

        // ================================================================== saving & opening projects

        public bool ConfirmClose()
        {
            if (!IsDirty) return true;
            StopPlayback();
            var r = MessageBox.Show(FindForm(), "Save changes to the video project \"" + ProjectName + "\"?", "Video editor",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return SaveProject(false);
            return true;
        }

        public void NewProject()
        {
            if (!ConfirmClose()) return;
            StopPlayback();
            project = new Project();
            projectPath = null;
            undo.Clear(); redo.Clear();
            version = savedVersion = 0;
            selection.Clear();
            playhead = 0;
            propsFor = -2;
            Timeline.FitAll();
            RefreshAll();
            Say("New video project");
        }

        public bool SaveProject(bool saveAs)
        {
            StopPlayback();
            string path = projectPath;
            if (saveAs || path == null)
            {
                using (var dlg = new SaveFileDialog { Title = "Save video project", Filter = ProjectSaveFilter, DefaultExt = "srproj", AddExtension = true, OverwritePrompt = true })
                {
                    try { dlg.InitialDirectory = projectPath != null ? Path.GetDirectoryName(projectPath) : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); } catch { }
                    dlg.FileName = ProjectName == "Untitled project" ? "My video.srproj" : ProjectName + ".srproj";
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return false;
                    path = dlg.FileName;
                }
            }
            try
            {
                KeepTemporaryMedia(path);
                // the project lists exactly the media its clips use
                project.Media.Clear();
                foreach (var id in project.AllClips().Where(c => c.Kind != ClipKind.Text).Select(c => c.MediaId).Distinct())
                {
                    var m = Library.Find(id);
                    if (m != null) project.Media.Add(new MediaRef { Id = m.Id, Path = m.OriginalPath, Name = m.Name });
                }
                project.Save(path);
                projectPath = path;
                savedVersion = version;
                RaiseState();
                Say("Saved " + Path.GetFileName(path));
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "Couldn't save the project:\n" + ex.Message, "Video editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        /// <summary>Recordings, freeze frames and pasted pictures only live in the cache: copy them next to the project.</summary>
        void KeepTemporaryMedia(string projectFile)
        {
            var used = new HashSet<int>(project.AllClips().Where(c => c.Kind != ClipKind.Text).Select(c => c.MediaId));
            var temps = Library.Items.Where(m => m.Temporary && used.Contains(m.Id)).ToList();
            if (temps.Count == 0) return;
            string dir = Path.Combine(Path.GetDirectoryName(projectFile) ?? "", Path.GetFileNameWithoutExtension(projectFile) + " media");
            Directory.CreateDirectory(dir);
            foreach (var m in temps)
            {
                string target = Path.Combine(dir, m.Name);
                for (int i = 2; File.Exists(target); i++) target = Path.Combine(dir, Path.GetFileNameWithoutExtension(m.Name) + " (" + i + ")" + Path.GetExtension(m.Name));
                File.Copy(m.OriginalPath, target);
                if (m.CachePath == null || MediaLibrary.SamePath(m.CachePath, m.OriginalPath)) m.CachePath = m.OriginalPath;
                m.OriginalPath = target;
                m.Name = Path.GetFileName(target);
                m.Temporary = false;
            }
            RefreshMedia();
        }

        public void OpenProjectDialog()
        {
            using (var dlg = new OpenFileDialog { Title = "Open video project", Filter = ProjectFilter })
            {
                try { dlg.InitialDirectory = projectPath != null ? Path.GetDirectoryName(projectPath) : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); } catch { }
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                OpenProject(dlg.FileName);
            }
        }

        public void OpenProject(string path)
        {
            if (!ConfirmClose()) return;
            Project p;
            try { p = Project.Load(path); }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "Couldn't open the project:\n" + ex.Message, "Video editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            StopPlayback();
            // map the project's media onto the bin (reusing items already there)
            var map = new Dictionary<int, int>();
            int missing = 0;
            foreach (var r in p.Media)
            {
                var m = Library.FindByPath(r.Path);
                if (m == null)
                {
                    int id = Library.Find(r.Id) == null ? r.Id : 0;
                    m = Library.Add(r.Path, id);
                }
                if (m.Status == MediaStatus.Missing) missing++;
                map[r.Id] = m.Id;
                Library.EnsureIdsAbove(r.Id);
            }
            foreach (var c in p.AllClips())
            {
                int nid;
                if (c.Kind != ClipKind.Text && map.TryGetValue(c.MediaId, out nid)) c.MediaId = nid;
            }
            foreach (var r in p.Media) { int nid; if (map.TryGetValue(r.Id, out nid)) r.Id = nid; }
            int maxId = p.AllClips().Select(c => c.Id).DefaultIfEmpty(0).Max();
            foreach (var t in p.Tracks) maxId = Math.Max(maxId, t.Id);
            p.NextId = Math.Max(p.NextId, maxId + 1);
            project = p;
            projectPath = path;
            undo.Clear(); redo.Clear();
            version = savedVersion = 0;
            selection.Clear();
            playhead = 0;
            propsFor = -2;
            Normalize();
            Timeline.FitAll();
            RefreshAll();
            Say("Opened " + Path.GetFileName(path) + (missing > 0 ? " – " + missing + " file" + (missing == 1 ? " is" : "s are") + " missing (right-click them in Media to locate)" : ""));
        }

        // ================================================================== export, frames, captions

        public void Export()
        {
            StopPlayback();
            if (project.IsEmpty) { Say("Put something on the timeline before exporting"); return; }
            string waiting;
            if (!Library.AudioReady(project, out waiting))
            {
                MessageBox.Show(FindForm(), "The sound of " + waiting + " is still being prepared. Please try again in a moment.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (project.AllClips().Any(c => c.Kind != ClipKind.Text && (Library.Find(c.MediaId) == null || !Library.Find(c.MediaId).IsUsable)))
            {
                var r = MessageBox.Show(FindForm(), "Some clips use media that is missing or can't be read. They will be left out of the export.\n\nExport anyway?",
                    "Export", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.OK) return;
            }
            var sources = Library.Items.Select(m => m.OriginalPath).ToList();
            using (var dlg = new ExportForm(project.Clone(), MediaSnapshot(), ProjectName, sources)) dlg.ShowDialog(FindForm());
        }

        public void SaveFrame()
        {
            StopPlayback();
            if (project.IsEmpty) return;
            using (var dlg = new SaveFileDialog { Title = "Save this frame", Filter = "PNG picture (*.png)|*.png|JPEG picture (*.jpg)|*.jpg", AddExtension = true, DefaultExt = "png" })
            {
                try { dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); } catch { }
                dlg.FileName = ProjectName + " " + Tc(playhead).Replace(":", ".") + ".png";
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                Cursor = Cursors.WaitCursor;
                try
                {
                    using (var bmp = RenderFull(playhead)) ImageSaver.Save(bmp, dlg.FileName);
                    Say("Saved " + Path.GetFileName(dlg.FileName));
                }
                catch (Exception ex) { MessageBox.Show(FindForm(), "Couldn't save the frame:\n" + ex.Message, "Video editor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { Cursor = Cursors.Default; }
            }
        }

        Bitmap RenderFull(double t)
        {
            var comp = new Compositor(project.Clone(), id => Library.Find(id)) { HighQuality = true };
            var bmp = new Bitmap(project.Width, project.Height, PixelFormat.Format32bppArgb);
            using (var frames = new StillFrames())
            using (var g = Graphics.FromImage(bmp)) comp.Render(g, bmp.Width, bmp.Height, Math.Min(t, Math.Max(0, project.Duration - project.FrameDuration)), frames);
            comp.ReleaseWork();
            return bmp;
        }

        public void ImportCaptions()
        {
            using (var dlg = new OpenFileDialog { Title = "Import captions", Filter = "Subtitles (*.srt)|*.srt|All files|*.*" })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                List<KeyValuePair<double[], string>> cues;
                try { cues = ParseSrt(File.ReadAllText(dlg.FileName)); }
                catch (Exception ex) { MessageBox.Show(FindForm(), "Couldn't read the captions:\n" + ex.Message, "Captions", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (cues.Count == 0) { Say("No captions found in that file"); return; }
                StopPlayback();
                BeginChange(null);
                var track = new Track { Id = project.NewId(), Kind = TrackKind.Video };
                project.Tracks.Add(track);
                double lastEnd = 0;
                foreach (var cue in cues)
                {
                    var c = TextPreset("caption");
                    c.Id = project.NewId();
                    c.Start = Math.Max(lastEnd, cue.Key[0]);
                    c.Duration = Math.Max(0.2, cue.Key[1] - c.Start);
                    c.Text = cue.Value;
                    lastEnd = c.End;
                    track.Clips.Add(c);
                }
                Changed();
                Say(cues.Count + " captions imported");
            }
        }

        static List<KeyValuePair<double[], string>> ParseSrt(string s)
        {
            var list = new List<KeyValuePair<double[], string>>();
            var blocks = Regex.Split(s.Replace("\r\n", "\n").Replace('\r', '\n').Trim(), @"\n\s*\n");
            var rx = new Regex(@"(\d+):(\d+):(\d+)[,.](\d+)\s*-->\s*(\d+):(\d+):(\d+)[,.](\d+)");
            foreach (var b in blocks)
            {
                var lines = b.Split('\n').ToList();
                int i = lines.FindIndex(l => rx.IsMatch(l));
                if (i < 0) continue;
                var m = rx.Match(lines[i]);
                Func<int, double> T = k => int.Parse(m.Groups[k].Value) * 3600 + int.Parse(m.Groups[k + 1].Value) * 60 + int.Parse(m.Groups[k + 2].Value) + int.Parse(m.Groups[k + 3].Value.PadRight(3, '0').Substring(0, 3)) / 1000.0;
                string text = string.Join("\n", lines.Skip(i + 1)).Trim();
                text = Regex.Replace(text, "<[^>]+>", "");
                if (text.Length == 0) continue;
                list.Add(new KeyValuePair<double[], string>(new[] { T(1), T(5) }, text));
            }
            list.Sort((a, b) => a.Key[0].CompareTo(b.Key[0]));
            return list;
        }

        public void ExportCaptions()
        {
            var texts = project.AllClips().Where(c => c.Kind == ClipKind.Text && !string.IsNullOrWhiteSpace(c.Text)).OrderBy(c => c.Start).ToList();
            if (texts.Count == 0) { Say("There is no text on the timeline to export"); return; }
            using (var dlg = new SaveFileDialog { Title = "Export captions", Filter = "Subtitles (*.srt)|*.srt", DefaultExt = "srt", AddExtension = true, FileName = ProjectName + ".srt" })
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                var sb = new StringBuilder();
                int n = 1;
                Func<double, string> F = t =>
                {
                    long ms = (long)Math.Round(t * 1000);
                    return (ms / 3600000).ToString("00") + ":" + (ms / 60000 % 60).ToString("00") + ":" + (ms / 1000 % 60).ToString("00") + "," + (ms % 1000).ToString("000");
                };
                foreach (var c in texts) sb.Append(n++).Append('\n').Append(F(c.Start)).Append(" --> ").Append(F(c.End)).Append('\n').Append(c.Text.Trim()).Append("\n\n");
                try { File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false)); Say("Saved " + Path.GetFileName(dlg.FileName)); }
                catch (Exception ex) { MessageBox.Show(FindForm(), "Couldn't save the captions:\n" + ex.Message, "Captions", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        // ================================================================== menus

        public void ShowClipMenu(Clip c, Point screen)
        {
            var menu = new ContextMenuStrip();
            bool locked = project.TrackOf(c) != null && project.TrackOf(c).Locked;
            bool under = playhead > c.Start + 1e-3 && playhead < c.End - 1e-3;
            menu.Items.Add(new ToolStripMenuItem("Split at the playhead", null, (s, e) => SplitSelected()) { ShortcutKeyDisplayString = "Ctrl+B", Enabled = under && !locked });
            menu.Items.Add(new ToolStripMenuItem("Duplicate", null, (s, e) => DuplicateSelected()) { ShortcutKeyDisplayString = "Ctrl+D" });
            menu.Items.Add(new ToolStripMenuItem("Copy", null, (s, e) => CopySelected(false)) { ShortcutKeyDisplayString = "Ctrl+C" });
            menu.Items.Add(new ToolStripMenuItem("Cut", null, (s, e) => CopySelected(true)) { ShortcutKeyDisplayString = "Ctrl+X", Enabled = !locked });
            menu.Items.Add(new ToolStripSeparator());
            if (c.Kind == ClipKind.Video)
            {
                menu.Items.Add(new ToolStripMenuItem("Freeze frame", null, (s, e) => FreezeFrame()) { Enabled = under && !locked });
                var m = Library.Find(c.MediaId);
                menu.Items.Add(new ToolStripMenuItem("Detach audio", null, (s, e) => DetachAudio()) { Enabled = m != null && m.HasAudio && !c.Muted && !locked });
            }
            if (c.Kind != ClipKind.Audio && c.Kind != ClipKind.Text || c.Kind == ClipKind.Video)
            {
                var speed = new ToolStripMenuItem("Speed");
                foreach (var sp in new[] { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 })
                {
                    double v = sp;
                    speed.DropDownItems.Add(new ToolStripMenuItem(v.ToString("0.##", CultureInfo.InvariantCulture) + "×", null, (s, e) => { BeginChange(null); SetSpeed(c, v); Changed(); })
                    { Checked = Math.Abs(c.Speed - v) < 0.001 });
                }
                if (c.Kind == ClipKind.Video) menu.Items.Add(speed);
            }
            if (c.Kind == ClipKind.Audio)
            {
                var speed = new ToolStripMenuItem("Speed");
                foreach (var sp in new[] { 0.5, 0.75, 1, 1.25, 1.5, 2 })
                {
                    double v = sp;
                    speed.DropDownItems.Add(new ToolStripMenuItem(v.ToString("0.##", CultureInfo.InvariantCulture) + "×", null, (s, e) => { BeginChange(null); SetSpeed(c, v); Changed(); }) { Checked = Math.Abs(c.Speed - v) < 0.001 });
                }
                menu.Items.Add(speed);
            }
            if (c.Kind != ClipKind.Text)
                menu.Items.Add(new ToolStripMenuItem(c.Muted ? "Turn sound on" : "Mute clip", null, (s, e) => { BeginChange(null); c.Muted = !c.Muted; Changed(); }) { Enabled = c.Kind != ClipKind.Image });
            if (c.IsVisual)
                menu.Items.Add(new ToolStripMenuItem("Reset position and size", null, (s, e) => { BeginChange(null); c.X = c.Y = 0; c.Scale = 1; c.Rotation = 0; c.Opacity = 1; c.Keys.Clear(); Changed(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Delete", null, (s, e) => DeleteSelected(false)) { ShortcutKeyDisplayString = "Del", Enabled = !locked });
            menu.Items.Add(new ToolStripMenuItem("Delete and close the gap", null, (s, e) => DeleteSelected(true)) { ShortcutKeyDisplayString = "Shift+Del", Enabled = !locked });
            Theme.StyleMenu(menu);
            menu.Closed += (s, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            menu.Show(screen);
        }

        public void ShowTimelineMenu(Point screen, double t)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Paste here", null, (s, e) => { Scrub(Math.Max(0, t)); Paste(); }) { ShortcutKeyDisplayString = "Ctrl+V" });
            menu.Items.Add(new ToolStripMenuItem("Add text here", null, (s, e) => { Scrub(Math.Max(0, t)); AddText("plain"); }));
            menu.Items.Add(new ToolStripMenuItem("Import media…", null, (s, e) => ImportDialog()) { ShortcutKeyDisplayString = "Ctrl+I" });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Select all", null, (s, e) => SelectAll()) { ShortcutKeyDisplayString = "Ctrl+A" });
            menu.Items.Add(new ToolStripMenuItem("Fit the project in view", null, (s, e) => Timeline.FitAll()));
            Theme.StyleMenu(menu);
            menu.Closed += (s, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            menu.Show(screen);
        }

        // ================================================================== keyboard

        bool TypingInTextBox()
        {
            var f = FindForm();
            Control a = f != null ? f.ActiveControl : null;
            while (a is ContainerControl && ((ContainerControl)a).ActiveControl != null) a = ((ContainerControl)a).ActiveControl;
            return a is TextBoxBase || a is ComboBox;
        }

        /// <summary>Editor shortcuts. Returns true when the key was used.</summary>
        public bool HandleKey(Keys k)
        {
            if (TypingInTextBox()) return false;
            switch (k)
            {
                case Keys.Space: TogglePlay(); return true;
                case Keys.Left: StepFrames(-1); return true;
                case Keys.Right: StepFrames(1); return true;
                case Keys.OemPeriod: StepFrames(1); return true;
                case Keys.Oemcomma: StepFrames(-1); return true;
                case Keys.Shift | Keys.Left: SeekTo(playhead - 1); return true;
                case Keys.Shift | Keys.Right: SeekTo(playhead + 1); return true;
                case Keys.Up: JumpEdit(-1); return true;
                case Keys.Down: JumpEdit(1); return true;
                case Keys.Home: SeekTo(0); return true;
                case Keys.End: SeekTo(project.Duration); return true;
                case Keys.Delete:
                case Keys.Back: DeleteSelected(false); return true;
                case Keys.Shift | Keys.Delete: DeleteSelected(true); return true;
                case Keys.Control | Keys.B: SplitSelected(); return true;
                case Keys.Control | Keys.D: DuplicateSelected(); return true;
                case Keys.Control | Keys.C: CopySelected(false); return true;
                case Keys.Control | Keys.X: CopySelected(true); return true;
                case Keys.Control | Keys.V: Paste(); return true;
                case Keys.Control | Keys.A: SelectAll(); return true;
                case Keys.Control | Keys.K: ToggleKeyframe(); return true;
                case Keys.Control | Keys.Oemcomma: JumpKey(-1); return false;
                case Keys.Control | Keys.I: ImportDialog(); return true;
                case Keys.Escape: if (focusPreview) { ToggleFocusPreview(); return true; } ClearSelection(); return true;
                case Keys.T: AddText("plain"); return true;
                case Keys.M: TogglePreviewMute(); return true;
                case Keys.F: ToggleFocusPreview(); return true;
                case Keys.Oemplus: case Keys.Add: Timeline.ZoomBy(1.33); return true;
                case Keys.OemMinus: case Keys.Subtract: Timeline.ZoomBy(0.75); return true;
                case Keys.Control | Keys.D0: Timeline.FitAll(); return true;
            }
            return false;
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
            if (files != null) ImportFiles(files, false);
        }

        // ================================================================== left panel

        void BuildLeft()
        {
            tabs.Add("media", "Media", Ico.Folder);
            tabs.Add("text", "Text", Ico.Text);
            tabs.Add("transitions", "Transitions", Ico.Transition);
            tabs.Add("filters", "Filters", Ico.Filter);
            tabs.Add("effects", "Effects", Ico.Sparkle);
            tabs.TabChanged += id => ShowPage(id);

            // media page
            var media = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Fill };
            var head = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Top, Height = Theme.S(56) };
            var import = new ThemedButton { Icon = Ico.Import, Text = "Import", Primary = true, Tip = "Import videos, pictures or music (Ctrl+I). You can also drag files here." };
            import.Click += (s, e) => ImportDialog();
            var record = new ThemedButton { Icon = Ico.Record, Text = "Record", Tip = "Record your screen (video snip)" };
            record.Click += (s, e) => { var h = RecordRequested; if (h != null) h(this, EventArgs.Empty); };
            var paste = new ThemedButton { Icon = Ico.Paste, IconOnly = true, Tip = "Paste a picture from the clipboard as an overlay (Ctrl+V)" };
            paste.Click += (s, e) => Paste();
            head.Controls.Add(new ButtonRowLayout(import, record, paste) { Dock = DockStyle.Fill });
            mediaGrid.TileDoubleClicked += t => { var m = t.Tag as MediaItem; if (m != null) AddMediaToTimeline(m, false); };
            mediaGrid.TileAddClicked += t => { var m = t.Tag as MediaItem; if (m != null) AddMediaToTimeline(m, true); };
            mediaGrid.TileClicked += t => { foreach (var x in mediaGrid.Tiles) x.Selected = ReferenceEquals(x, t); mediaGrid.Invalidate(); };
            mediaGrid.TileRightClicked += OnMediaTileMenu;
            mediaGrid.TileDragStarted += t =>
            {
                var m = t.Tag as MediaItem;
                if (m == null) return;
                var data = new DataObject();
                data.SetData(TimelineControl.DragFormat, m.Id.ToString(CultureInfo.InvariantCulture));
                mediaGrid.DoDragDrop(data, DragDropEffects.Copy);
            };
            mediaGrid.AllowDrop = true;
            mediaGrid.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            mediaGrid.DragDrop += (s, e) => { var files = e.Data.GetData(DataFormats.FileDrop) as string[]; if (files != null) ImportFiles(files, false); };
            mediaFooterHost.Padding = new Padding(Theme.S(12), Theme.S(8), Theme.S(12), Theme.S(4));
            mediaFooterHost.Controls.Add(mediaFooter);
            mediaFooterHost.Height = Theme.S(48);
            media.Controls.Add(mediaGrid);
            media.Controls.Add(mediaFooterHost);
            media.Controls.Add(head);
            AddPage("media", media);

            // text page
            var text = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Fill };
            var textBottom = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Bottom, Height = Theme.S(96) };
            var srtIn = new ThemedButton { Icon = Ico.Captions, Text = "Import captions (.srt)", Tip = "Each caption becomes a text clip" };
            srtIn.Click += (s, e) => ImportCaptions();
            var srtOut = new ThemedButton { Icon = Ico.Export, Text = "Export captions (.srt)", Tip = "Saves every text clip as a subtitle file" };
            srtOut.Click += (s, e) => ExportCaptions();
            textBottom.Controls.Add(new ButtonRowLayout(true, srtIn, srtOut) { Dock = DockStyle.Fill });
            textGrid.TileClicked += t => AddText(t.Key);
            textGrid.TileDragStarted += t => AddText(t.Key);
            text.Controls.Add(textGrid);
            text.Controls.Add(textBottom);
            text.Controls.Add(Hint("Click a style to add it at the playhead. Then type your words in the panel on the right."));
            AddPage("text", text);

            var trans = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Fill };
            transGrid.TileClicked += t => ApplyTransition(t.Key == "none" ? "" : t.Key);
            trans.Controls.Add(transGrid);
            trans.Controls.Add(Hint("Select a clip, then pick a transition. It plays as that clip begins – from the clip before it, or from black."));
            AddPage("transitions", trans);

            var filt = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Fill };
            filterGrid.TileClicked += t => ApplyFilter(t.Key == "none" ? "" : t.Key);
            filt.Controls.Add(filterGrid);
            filt.Controls.Add(Hint("Select a video or picture clip, then pick a look. Adjust its strength under Adjust on the right."));
            AddPage("filters", filt);

            var fx = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Fill };
            effectGrid.TileClicked += t => ToggleEffect(t.Key);
            fx.Controls.Add(effectGrid);
            fx.Controls.Add(Hint("Select a clip, then click an effect to turn it on or off. Fine-tune it under Effects on the right."));
            AddPage("effects", fx);

            leftPanel.Controls.Add(tabs);
            ShowPage("media");
        }

        public event EventHandler RecordRequested;

        Control Hint(string text)
        {
            var p = new ThemedPanel { Surface = Surface.Panel, Dock = DockStyle.Top };
            var h = new HintLabel { Text = text, Location = new Point(Theme.S(12), Theme.S(8)) };
            p.Controls.Add(h);
            p.Resize += (s, e) => { h.Width = Math.Max(50, p.Width - Theme.S(24)); h.Height = h.MeasureHeight(h.Width); p.Height = h.Height + Theme.S(14); };
            p.Height = Theme.S(56);
            return p;
        }

        void AddPage(string id, Control page)
        {
            page.Visible = false;
            pages[id] = page;
            leftPanel.Controls.Add(page);
        }

        void ShowPage(string id)
        {
            foreach (var kv in pages) kv.Value.Visible = kv.Key == id;
            if (id == "filters") MakeFilterTiles();
            leftPanel.PerformLayout();
        }

        void RebuildTiles()
        {
            RefreshMedia();
            var tt = new List<Tile>();
            foreach (var kv in TextNames)
            {
                string style = kv.Key;
                var sample = TextPreset(style);
                tt.Add(new Tile { Key = style, Title = kv.Value, Paint = (g, r) => DrawTextSample(g, r, sample) });
            }
            textGrid.SetTiles(tt);

            var tr = new List<Tile> { new Tile { Key = "none", Title = "None", Icon = Ico.Close } };
            foreach (var id in Looks.TransitionIds)
            {
                string tid = id;
                tr.Add(new Tile { Key = id, Title = Looks.TransitionName(id), Paint = (g, r) => DrawTransitionSample(g, r, tid) });
            }
            transGrid.SetTiles(tr);

            var fx = new List<Tile>
            {
                new Tile { Key = "fade", Title = "Fade in & out", Icon = Ico.Sun },
                new Tile { Key = "blur", Title = "Blur", Icon = Ico.Filter },
                new Tile { Key = "sharpen", Title = "Sharpen", Icon = Ico.Diamond },
                new Tile { Key = "vignette", Title = "Vignette", Icon = Ico.Screen },
                new Tile { Key = "grain", Title = "Film grain", Icon = Ico.Grid },
                new Tile { Key = "chroma", Title = "Chroma key", Sub = "Remove a green or blue screen", Icon = Ico.Layers },
                new Tile { Key = "stabilize", Title = "Stabilise", Sub = "Steadies shaky video (playback and export)", Icon = Ico.Hand },
            };
            effectGrid.SetTiles(fx);
            MakeFilterTiles();
            RebuildTileStates();
        }

        void RebuildTileStates()
        {
            var c = SingleSelected;
            foreach (var t in transGrid.Tiles) t.Selected = c != null && (c.Transition ?? "") == (t.Key == "none" ? "" : t.Key);
            foreach (var t in filterGrid.Tiles) t.Selected = c != null && (c.Filter ?? "") == (t.Key == "none" ? "" : t.Key);
            foreach (var t in effectGrid.Tiles)
            {
                bool on = false;
                if (c != null)
                    switch (t.Key)
                    {
                        case "fade": on = c.FadeIn > 0.01 || c.FadeOut > 0.01; break;
                        case "blur": on = c.Blur > 0.5; break;
                        case "sharpen": on = c.Sharpen > 0.5; break;
                        case "vignette": on = c.Vignette > 0.5; break;
                        case "grain": on = c.Grain > 0.5; break;
                        case "chroma": on = c.ChromaKey; break;
                        case "stabilize": on = c.Stabilize; break;
                    }
                t.Selected = on;
            }
            transGrid.Invalidate(); filterGrid.Invalidate(); effectGrid.Invalidate();
        }

        void MakeFilterTiles()
        {
            Bitmap src = preview.CopyFrame();
            if (src != null)
            {
                if (filterSource != null) filterSource.Dispose();
                filterSource = new Bitmap(128, (int)Math.Max(8, 128.0 * src.Height / src.Width), PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(filterSource))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, 0, 0, filterSource.Width, filterSource.Height);
                }
                src.Dispose();
            }
            var old = filterGrid.Tiles.ToList();
            var list = new List<Tile> { new Tile { Key = "none", Title = "None", Image = filterSource != null ? new Bitmap(filterSource) : null, Icon = Ico.Close } };
            foreach (var id in Looks.FilterIds)
            {
                Bitmap img = null;
                if (filterSource != null)
                {
                    img = new Bitmap(filterSource);
                    var d = img.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                    Pixels.ApplyMatrix(d, Pixels.FilterMatrix(id));
                    img.UnlockBits(d);
                }
                list.Add(new Tile { Key = id, Title = Looks.FilterName(id), Image = img, Paint = img == null ? (Action<Graphics, RectangleF>)((g, r) => DrawFilterSwatch(g, r, id)) : null });
            }
            filterGrid.SetTiles(list);
            foreach (var t in old) if (t.Image != null) t.Image.Dispose();
            RebuildTileStates();
        }

        static void DrawFilterSwatch(Graphics g, RectangleF r, string id)
        {
            using (var bmp = new Bitmap(64, 36, PixelFormat.Format32bppArgb))
            {
                using (var gg = Graphics.FromImage(bmp))
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, 64, 36), Color.FromArgb(255, 230, 120, 60), Color.FromArgb(255, 40, 120, 220), 20f))
                {
                    gg.FillRectangle(b, 0, 0, 64, 36);
                    using (var b2 = new SolidBrush(Color.FromArgb(255, 60, 170, 80))) gg.FillEllipse(b2, 8, 14, 30, 30);
                }
                var d = bmp.LockBits(new Rectangle(0, 0, 64, 36), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                Pixels.ApplyMatrix(d, Pixels.FilterMatrix(id));
                bmp.UnlockBits(d);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(bmp, r);
            }
        }

        static void DrawTextSample(Graphics g, RectangleF r, Clip sample)
        {
            using (var b = new LinearGradientBrush(r, Color.FromArgb(30, 44, 90), Color.FromArgb(70, 40, 110), 30f)) g.FillRectangle(b, r);
            var c = sample.Clone();
            c.X = 0; c.Y = 0;
            float canvasH = r.Height * (c.TextStyle == "lower" ? 1.5f : 2.0f);
            if (c.FontSize < 0.07) canvasH *= 1.4f;
            RectangleF box; float em;
            using (TextLayout.Build(c, canvasH, out box, out em)) { }
            float k = Math.Min(1f, Math.Min(r.Width * 0.84f / Math.Max(1, box.Width + em * 0.8f), r.Height * 0.8f / Math.Max(1, box.Height + em * 0.5f)));
            using (var m = new Matrix())
            {
                m.Translate(r.Left + r.Width / 2, r.Top + r.Height / 2);
                m.Scale(k, k);
                TextLayout.Draw(g, c, canvasH, m, 1);
            }
        }

        static void DrawTransitionSample(Graphics g, RectangleF r, string id)
        {
            var a = Color.FromArgb(48, 104, 255); var b = Color.FromArgb(176, 72, 255);
            using (var ba = new SolidBrush(a)) g.FillRectangle(ba, r);
            float p = 0.5f;
            var st = g.Save();
            using (var bb = new SolidBrush(b))
            {
                switch (id)
                {
                    case "dissolve": using (var bh = new SolidBrush(Color.FromArgb(128, b))) g.FillRectangle(bh, r); break;
                    case "black": using (var bk = new LinearGradientBrush(r, a, Color.Black, 0f)) g.FillRectangle(bk, r.Left, r.Top, r.Width / 2, r.Height); using (var bk = new LinearGradientBrush(r, Color.Black, b, 0f)) g.FillRectangle(bk, r.Left + r.Width / 2, r.Top, r.Width / 2, r.Height); break;
                    case "white": using (var bk = new LinearGradientBrush(r, a, Color.White, 0f)) g.FillRectangle(bk, r.Left, r.Top, r.Width / 2, r.Height); using (var bk = new LinearGradientBrush(r, Color.White, b, 0f)) g.FillRectangle(bk, r.Left + r.Width / 2, r.Top, r.Width / 2, r.Height); break;
                    case "wipeleft": g.FillRectangle(bb, r.Left + r.Width * (1 - p), r.Top, r.Width * p, r.Height); break;
                    case "wiperight": g.FillRectangle(bb, r.Left, r.Top, r.Width * p, r.Height); break;
                    case "wipeup": g.FillRectangle(bb, r.Left, r.Top + r.Height * (1 - p), r.Width, r.Height * p); break;
                    case "wipedown": g.FillRectangle(bb, r.Left, r.Top, r.Width, r.Height * p); break;
                    case "slideleft": g.FillRectangle(bb, r.Left + r.Width * 0.45f, r.Top, r.Width, r.Height); using (var pen = new Pen(Color.White, 2)) g.DrawLine(pen, r.Left + r.Width * 0.45f, r.Top, r.Left + r.Width * 0.45f, r.Bottom); break;
                    case "slideright": g.FillRectangle(bb, r.Left, r.Top, r.Width * 0.55f, r.Height); using (var pen = new Pen(Color.White, 2)) g.DrawLine(pen, r.Left + r.Width * 0.55f, r.Top, r.Left + r.Width * 0.55f, r.Bottom); break;
                    case "slideup": g.FillRectangle(bb, r.Left, r.Top + r.Height * 0.45f, r.Width, r.Height); using (var pen = new Pen(Color.White, 2)) g.DrawLine(pen, r.Left, r.Top + r.Height * 0.45f, r.Right, r.Top + r.Height * 0.45f); break;
                    case "slidedown": g.FillRectangle(bb, r.Left, r.Top, r.Width, r.Height * 0.55f); using (var pen = new Pen(Color.White, 2)) g.DrawLine(pen, r.Left, r.Top + r.Height * 0.55f, r.Right, r.Top + r.Height * 0.55f); break;
                    case "circle": { float d = r.Height * 0.8f; g.SmoothingMode = SmoothingMode.AntiAlias; g.FillEllipse(bb, r.Left + (r.Width - d) / 2, r.Top + (r.Height - d) / 2, d, d); break; }
                    case "zoom": { var zr = RectangleF.Inflate(r, -r.Width * 0.22f, -r.Height * 0.22f); g.FillRectangle(bb, zr); using (var pen = new Pen(Color.White, 1.5f)) g.DrawRectangle(pen, zr.X, zr.Y, zr.Width, zr.Height); break; }
                }
            }
            g.Restore(st);
            Icons.Draw(g, Ico.Transition, new RectangleF(r.Right - 22, r.Bottom - 22, 18, 18), Color.FromArgb(200, 255, 255, 255), 1.6f);
        }

        // ================================================================== properties panel

        /// <summary>Shows a page of the properties panel (e.g. "basic" for crop) for the selected clip.</summary>
        public void ShowProps(string page)
        {
            if (focusPreview) ToggleFocusPreview();
            propsPage = page;
            propsFor = -2;
            RebuildProps();
            if (SingleSelected == null) Say("Select a clip on the timeline first");
        }

        void RebuildPropsIfNeeded()
        {
            int key = selection.Count == 1 ? selection.First() : selection.Count == 0 ? -1 : -3;
            if (key != propsFor) RebuildProps();
        }

        void SyncProps()
        {
            var c = SingleSelected;
            foreach (var s in liveSliders)
            {
                s.Sync();
                if (s.Keyable && c != null)
                {
                    s.KeyAnimated = c.HasKeys;
                    s.KeyActive = c.KeyAt(playhead, project.FrameDuration * 0.5) != null;
                    s.Invalidate();
                }
            }
        }

        void SyncToolbar()
        {
            var c = SingleSelected;
            bool any = selection.Count > 0;
            toolbar.Undo.Enabled = undo.Count > 0;
            toolbar.Redo.Enabled = redo.Count > 0;
            toolbar.Split.Enabled = !project.IsEmpty;
            toolbar.Delete.Enabled = any;
            toolbar.Duplicate.Enabled = any;
            toolbar.Freeze.Enabled = !project.IsEmpty;
            toolbar.Detach.Enabled = c != null && c.Kind == ClipKind.Video;
            toolbar.Keyframe.Enabled = c != null && c.IsVisual;
            toolbar.Snap.Checked = snapping;
            toolbar.Magnet.Checked = mainMagnet;
            SyncZoom();
        }

        bool syncingZoom;
        void SyncZoom()
        {
            syncingZoom = true;
            toolbar.Zoom.Value = (int)Math.Round(Timeline.ZoomFraction * 1000);
            syncingZoom = false;
        }

        void RebuildProps()
        {
            props.SuspendLayout();
            var old = props.Controls.Cast<Control>().ToList();
            props.Controls.Clear();
            foreach (var o in old) o.Dispose();
            liveSliders.Clear();
            int key = selection.Count == 1 ? selection.First() : selection.Count == 0 ? -1 : -3;
            propsFor = key;
            var c = SingleSelected;
            if (selection.Count > 1) BuildMultiProps();
            else if (c == null) BuildProjectProps();
            else BuildClipProps(c);
            props.ResumeLayout(true);
            props.PerformLayout();
            Theme.ApplyScrollbars(props);
        }

        void P(Control c) { props.Controls.Add(c); }
        void Section(string text) { P(new SectionLabel(text)); }
        void HintP(string text) { P(new HintLabel { Text = text }); }

        ThemedButton Btn(string text, Ico icon, string tip, Action a, bool primary = false)
        {
            var b = new ThemedButton { Text = text, Icon = icon, Tip = tip, Primary = primary, IconOnly = text == null };
            b.Click += (s, e) => a();
            return b;
        }

        void BuildMultiProps()
        {
            P(new PanelTitle(selection.Count + " clips selected", "Shift-click clips in the timeline to change the selection"));
            P(new ButtonRow(Btn("Delete", Ico.Trash, "Delete (Del)", () => DeleteSelected(false)), Btn("Duplicate", Ico.Duplicate, "Duplicate (Ctrl+D)", DuplicateSelected)));
            P(new ButtonRow(Btn("Copy", Ico.Copy, "Copy (Ctrl+C)", () => CopySelected(false)), Btn("Split", Ico.Scissors, "Split at the playhead (Ctrl+B)", SplitSelected)));
            HintP("Transitions and filters from the left panel apply to every selected clip.");
        }

        void BuildProjectProps()
        {
            P(new PanelTitle("Project", ProjectName + (IsDirty ? " – not saved" : "")));
            Section("Canvas");
            var shapes = new[] { "16:9", "9:16", "1:1", "4:3" };
            var shapes2 = new[] { "3:4", "4:5", "21:9" };
            Func<string, ThemedButton> shapeBtn = s =>
            {
                var parts = s.Split(':');
                int a = int.Parse(parts[0]), b = int.Parse(parts[1]);
                var btn = new ThemedButton { Text = s, Checked = AspectLabel == s, Tip = "Canvas shape " + s };
                btn.Click += (o, e) => SetCanvasShape(a, b);
                return btn;
            };
            P(new ButtonRow(shapes.Select(shapeBtn).Cast<Control>().ToArray()));
            var row2 = shapes2.Select(shapeBtn).Cast<Control>().ToList();
            row2.Add(Btn("Match", Ico.None, "Match the first clip on the main track", MatchFirstClip));
            P(new ButtonRow(row2.ToArray()));
            Section("Resolution");
            var res = new Segmented();
            int shortSide = Math.Min(project.Width, project.Height);
            res.Add("720", "720p").Add("1080", "1080p").Add("1440", "1440p").Add("2160", "4K");
            res.Selected = shortSide.ToString(CultureInfo.InvariantCulture);
            res.Picked += v => SetResolution(int.Parse(v));
            P(res);
            HintP(project.Width + " × " + project.Height + " pixels");
            Section("Frame rate");
            var fps = new Segmented();
            foreach (var f in new[] { "24", "25", "30", "50", "60" }) fps.Add(f, f);
            fps.Selected = Math.Round(project.Fps).ToString(CultureInfo.InvariantCulture);
            fps.Picked += v => SetFps(double.Parse(v, CultureInfo.InvariantCulture));
            P(fps);
            Section("Background");
            var bg = new SwatchPicker { Columns = 6 };
            bg.Colors = new[] { Color.Black, Color.White, Color.FromArgb(40, 40, 40), Color.FromArgb(20, 30, 70), Color.FromArgb(0, 120, 80), Color.FromArgb(120, 20, 60) };
            bg.Selected = Color.FromArgb(project.Background);
            bg.ColorPicked += (s, e) => { BeginChange(null); project.Background = e.Color.ToArgb(); Changed(); };
            bg.Height = bg.PreferredHeight;
            P(bg);
            Section("Timeline");
            var snap = new ThemedToggle { Text = "Snapping", Checked = snapping };
            snap.CheckedChanged += (s, e) => { if (snap.Checked != snapping) ToggleSnapping(); };
            P(snap);
            var mag = new ThemedToggle { Text = "Main track magnet (no gaps)", Checked = mainMagnet };
            mag.CheckedChanged += (s, e) => { if (mag.Checked != mainMagnet) ToggleMainMagnet(); };
            P(mag);
            Section("Tips");
            HintP("Click a clip in the timeline to edit it here.\nSpace plays · ← → step a frame · Ctrl+B splits · Del deletes · Ctrl+wheel zooms the timeline.");
            P(new ButtonRow(Btn("Export…", Ico.Export, "Export the video (Ctrl+M)", Export, true)));
        }

        PropSlider Slider(string label, double min, double max, double def, Func<double> get, Action<double> set, Func<double, string> fmt, string tag)
        {
            var s = new PropSlider(label, min, max, def) { Format = fmt ?? (v => v.ToString("0", CultureInfo.InvariantCulture)), Getter = get };
            s.SetSilently(get());
            s.Changing += v => { BeginChange(tag); set(v); LiveChanged(); };
            s.Committed += () => { Changed(); };
            liveSliders.Add(s);
            P(s);
            return s;
        }

        PropSlider ClipSlider(Clip c, string label, double min, double max, double def, Func<Clip, double> get, Action<Clip, double> set, Func<double, string> fmt = null)
        {
            int id = c.Id;
            return Slider(label, min, max, def, () => { var cc = project.FindClip(id); return cc != null ? get(cc) : def; },
                v => { var cc = project.FindClip(id); if (cc != null) set(cc, v); }, fmt, "p:" + label + id);
        }

        PropSlider PlacementSlider(Clip c, string label, double min, double max, double def, Func<Placement, double> get, Func<Placement, double, Placement> set, Func<double, string> fmt)
        {
            int id = c.Id;
            var s = new PropSlider(label, min, max, def) { Format = fmt, Keyable = true };
            s.Getter = () => { var cc = project.FindClip(id); return cc != null ? get(cc.PlacementAt(Math.Max(cc.Start, Math.Min(cc.End, playhead)))) : def; };
            s.SetSilently(s.Getter());
            s.Changing += v =>
            {
                var cc = project.FindClip(id);
                if (cc == null) return;
                BeginChange("p:" + label + id);
                var p = cc.PlacementAt(Math.Max(cc.Start, Math.Min(cc.End, playhead)));
                if (cc.HasKeys && (playhead < cc.Start || playhead > cc.End)) { Say("Move the playhead over the clip to change an animated value"); return; }
                SetPlacement(cc, set(p, v));
            };
            s.Committed += () => Changed();
            s.KeyClicked += ToggleKeyframe;
            s.KeyAnimated = c.HasKeys;
            s.KeyActive = c.KeyAt(playhead, project.FrameDuration * 0.5) != null;
            liveSliders.Add(s);
            P(s);
            return s;
        }

        static string Pct(double v) { return Math.Round(v * 100).ToString(CultureInfo.InvariantCulture) + "%"; }
        static string Sec(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture) + " s"; }
        static string Signed(double v) { return (v > 0.5 ? "+" : "") + Math.Round(v).ToString(CultureInfo.InvariantCulture); }

        void BuildClipProps(Clip c)
        {
            var m = c.Kind == ClipKind.Text ? null : Library.Find(c.MediaId);
            string kind = c.Kind == ClipKind.Text ? "Text" : c.Kind == ClipKind.Audio ? "Sound" : c.Kind == ClipKind.Image ? "Picture" : "Video";
            P(new PanelTitle(ClipTitle(c), kind + " · " + Tc(c.Start) + " – " + Tc(c.End) + " · " + c.Duration.ToString("0.0#", CultureInfo.InvariantCulture) + " s"));

            var pagesList = new List<KeyValuePair<string, string>>();
            if (c.Kind == ClipKind.Text) pagesList.Add(new KeyValuePair<string, string>("text", "Text"));
            if (c.IsVisual) pagesList.Add(new KeyValuePair<string, string>("basic", "Basic"));
            if (c.Kind == ClipKind.Video || c.Kind == ClipKind.Image) { pagesList.Add(new KeyValuePair<string, string>("adjust", "Adjust")); pagesList.Add(new KeyValuePair<string, string>("effects", "Effects")); }
            bool sound = c.Kind == ClipKind.Audio || (c.Kind == ClipKind.Video && m != null && m.HasAudio);
            if (sound) pagesList.Add(new KeyValuePair<string, string>("audio", "Audio"));
            if (c.Kind == ClipKind.Video || c.Kind == ClipKind.Audio) pagesList.Add(new KeyValuePair<string, string>("speed", "Speed"));
            if (!pagesList.Any(x => x.Key == propsPage)) propsPage = pagesList[0].Key;
            if (pagesList.Count > 1)
            {
                var seg = new Segmented { Height = Theme.S(32) };
                foreach (var kv in pagesList) seg.Add(kv.Key, kv.Value);
                seg.Selected = propsPage;
                seg.Picked += v => { propsPage = v; propsFor = -2; BeginInvoke((MethodInvoker)RebuildProps); };
                P(seg);
            }
            if (m != null && !m.IsUsable) HintP("⚠ " + (m.Error ?? "This clip's media can't be read."));

            switch (propsPage)
            {
                case "text": BuildTextProps(c); break;
                case "basic": BuildBasicProps(c); break;
                case "adjust": BuildAdjustProps(c); break;
                case "effects": BuildEffectProps(c); break;
                case "audio": BuildAudioProps(c, m); break;
                case "speed": BuildSpeedProps(c); break;
            }
        }

        void BuildBasicProps(Clip c)
        {
            Section("Position and size");
            PlacementSlider(c, "Position X", -1, 1, 0, p => p.X, (p, v) => { p.X = v; return p; }, v => Math.Round(v * project.Width).ToString(CultureInfo.InvariantCulture));
            PlacementSlider(c, "Position Y", -1, 1, 0, p => p.Y, (p, v) => { p.Y = v; return p; }, v => Math.Round(v * project.Height).ToString(CultureInfo.InvariantCulture));
            PlacementSlider(c, "Scale", 0.05, 4, 1, p => p.Scale, (p, v) => { p.Scale = v; return p; }, Pct);
            PlacementSlider(c, "Rotation", -180, 180, 0, p => p.Rotation, (p, v) => { p.Rotation = v; return p; }, v => Math.Round(v) + "°");
            PlacementSlider(c, "Opacity", 0, 1, 1, p => p.Opacity, (p, v) => { p.Opacity = v; return p; }, Pct);
            int id = c.Id;
            P(new ButtonRow(
                Btn(null, Ico.ChevronLeft, "Previous keyframe", () => JumpKey(-1)),
                Btn("Keyframe", Ico.Diamond, "Add or remove a keyframe at the playhead (Ctrl+K). With keyframes, changes animate between them.", ToggleKeyframe),
                Btn(null, Ico.ChevronRight, "Next keyframe", () => JumpKey(1))));
            if (c.HasKeys)
                P(new ButtonRow(Btn("Remove all keyframes", Ico.Trash, "Stop animating this clip", () => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); var p = cc.PlacementAt(playhead); cc.Keys.Clear(); cc.X = p.X; cc.Y = p.Y; cc.Scale = p.Scale; cc.Rotation = p.Rotation; cc.Opacity = p.Opacity; propsFor = -2; Changed(); })));
            if (c.Kind != ClipKind.Text)
            {
                var flipH = Btn("Mirror", Ico.FlipH, "Mirror left to right", () => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.FlipH = !cc.FlipH; propsFor = -2; Changed(); });
                flipH.Checked = c.FlipH;
                var flipV = Btn("Flip", Ico.FlipV, "Turn upside down", () => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.FlipV = !cc.FlipV; propsFor = -2; Changed(); });
                flipV.Checked = c.FlipV;
                var rot = Btn(null, Ico.RotateRight, "Rotate 90°", () => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); var p = cc.PlacementAt(playhead); p.Rotation = Math.Round(p.Rotation / 90) * 90 + 90; if (p.Rotation > 180) p.Rotation -= 360; SetPlacement(cc, p); Changed(); });
                var reset = Btn(null, Ico.Refresh, "Reset position, size and rotation", () => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.X = cc.Y = 0; cc.Scale = 1; cc.Rotation = 0; cc.Opacity = 1; cc.Keys.Clear(); propsFor = -2; Changed(); });
                P(new ButtonRow(flipH, flipV, rot, reset));

                Section("Crop");
                ClipSlider(c, "Left", 0, 0.45, 0, x => x.CropL, (x, v) => x.CropL = v, Pct);
                ClipSlider(c, "Right", 0, 0.45, 0, x => x.CropR, (x, v) => x.CropR = v, Pct);
                ClipSlider(c, "Top", 0, 0.45, 0, x => x.CropT, (x, v) => x.CropT = v, Pct);
                ClipSlider(c, "Bottom", 0, 0.45, 0, x => x.CropB, (x, v) => x.CropB = v, Pct);
            }
            Section("Animation");
            ClipSlider(c, "Fade in", 0, Math.Min(5, c.Duration), 0, x => x.FadeIn, (x, v) => x.FadeIn = Math.Min(v, x.Duration), Sec);
            ClipSlider(c, "Fade out", 0, Math.Min(5, c.Duration), 0, x => x.FadeOut, (x, v) => x.FadeOut = Math.Min(v, x.Duration), Sec);
            Section("Transition in");
            var tb = Btn(string.IsNullOrEmpty(c.Transition) ? "None – choose…" : Looks.TransitionName(c.Transition), Ico.Transition, "Choose a transition", () => tabs.Selected = "transitions");
            tb.AlignLeft = true;
            P(tb);
            if (!string.IsNullOrEmpty(c.Transition))
                ClipSlider(c, "Transition length", 0.1, Math.Max(0.2, Math.Min(4, c.Duration)), 0.6, x => x.TransitionDuration, (x, v) => x.TransitionDuration = v, Sec);
        }

        void BuildAdjustProps(Clip c)
        {
            int id = c.Id;
            Section("Filter");
            var fb = Btn(string.IsNullOrEmpty(c.Filter) ? "None – choose…" : Looks.FilterName(c.Filter), Ico.Filter, "Choose a filter", () => tabs.Selected = "filters");
            fb.AlignLeft = true;
            P(fb);
            if (!string.IsNullOrEmpty(c.Filter)) ClipSlider(c, "Filter strength", 0, 1, 1, x => x.FilterAmount, (x, v) => x.FilterAmount = v, Pct);
            Section("Light and colour");
            ClipSlider(c, "Brightness", -100, 100, 0, x => x.Brightness, (x, v) => x.Brightness = v, Signed);
            ClipSlider(c, "Contrast", -100, 100, 0, x => x.Contrast, (x, v) => x.Contrast = v, Signed);
            ClipSlider(c, "Saturation", -100, 100, 0, x => x.Saturation, (x, v) => x.Saturation = v, Signed);
            ClipSlider(c, "Exposure", -100, 100, 0, x => x.Exposure, (x, v) => x.Exposure = v, Signed);
            ClipSlider(c, "Temperature", -100, 100, 0, x => x.Temperature, (x, v) => x.Temperature = v, Signed);
            ClipSlider(c, "Tint", -100, 100, 0, x => x.Tint, (x, v) => x.Tint = v, Signed);
            ClipSlider(c, "Hue", -180, 180, 0, x => x.Hue, (x, v) => x.Hue = v, v => Math.Round(v) + "°");
            P(new ButtonRow(Btn("Reset adjustments", Ico.Refresh, "Back to the original colours", () =>
            {
                var cc = project.FindClip(id); if (cc == null) return;
                BeginChange(null);
                cc.Brightness = cc.Contrast = cc.Saturation = cc.Exposure = cc.Temperature = cc.Tint = cc.Hue = 0;
                cc.Filter = ""; cc.FilterAmount = 1;
                propsFor = -2; Changed();
            })));
        }

        void BuildEffectProps(Clip c)
        {
            int id = c.Id;
            Section("Effects");
            ClipSlider(c, "Blur", 0, 100, 0, x => x.Blur, (x, v) => x.Blur = v);
            ClipSlider(c, "Sharpen", 0, 100, 0, x => x.Sharpen, (x, v) => x.Sharpen = v);
            ClipSlider(c, "Vignette", 0, 100, 0, x => x.Vignette, (x, v) => x.Vignette = v);
            ClipSlider(c, "Film grain", 0, 100, 0, x => x.Grain, (x, v) => x.Grain = v);
            Section("Chroma key (green screen)");
            var key = new ThemedToggle { Text = "Remove a colour", Checked = c.ChromaKey };
            key.CheckedChanged += (s, e) => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.ChromaKey = key.Checked; propsFor = -2; Changed(); };
            P(key);
            if (c.ChromaKey)
            {
                var sw = new SwatchPicker { Columns = 6 };
                sw.Colors = new[] { Color.FromArgb(0, 255, 0), Color.FromArgb(0, 177, 64), Color.FromArgb(0, 71, 187), Color.FromArgb(0, 0, 255), Color.White, Color.Black };
                sw.Selected = Color.FromArgb(c.KeyColor);
                sw.ColorPicked += (s, e) => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.KeyColor = e.Color.ToArgb(); Changed(); };
                sw.Height = sw.PreferredHeight;
                P(sw);
                var pick = Btn("Pick from the preview", Ico.Pointer, "Click on the preview to choose the colour to remove", () => StartEyedropper(id));
                P(pick);
                ClipSlider(c, "Strength", 0, 100, 30, x => x.KeySimilarity, (x, v) => x.KeySimilarity = v);
                ClipSlider(c, "Edge softness", 0, 100, 10, x => x.KeySmoothness, (x, v) => x.KeySmoothness = v);
            }
            if (c.Kind == ClipKind.Video)
            {
                Section("Stabilise");
                var stab = new ThemedToggle { Text = "Steady shaky video", Checked = c.Stabilize };
                stab.CheckedChanged += (s, e) => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.Stabilize = stab.Checked; Changed(); };
                P(stab);
                HintP("Stabilising is applied while playing and in the exported video. Paused frames show the original.");
            }
        }

        void StartEyedropper(int clipId)
        {
            Say("Click the colour to remove in the preview");
            MouseEventHandler handler = null;
            handler = (s, e) =>
            {
                preview.MouseDown -= handler;
                preview.Cursor = Cursors.Default;
                using (var frame = preview.CopyFrame())
                {
                    if (frame == null) return;
                    var r = preview.CanvasRect;
                    int x = (int)((e.X - r.Left) / r.Width * frame.Width), y = (int)((e.Y - r.Top) / r.Height * frame.Height);
                    if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) return;
                    var col = frame.GetPixel(x, y);
                    var cc = project.FindClip(clipId);
                    if (cc == null) return;
                    BeginChange(null);
                    cc.KeyColor = Color.FromArgb(255, col).ToArgb();
                    propsFor = -2;
                    Changed();
                }
            };
            preview.Cursor = Cursors.Cross;
            preview.MouseDown += handler;
        }

        void BuildAudioProps(Clip c, MediaItem m)
        {
            int id = c.Id;
            Section("Sound");
            var mute = new ThemedToggle { Text = "Mute this clip", Checked = c.Muted };
            mute.CheckedChanged += (s, e) => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.Muted = mute.Checked; Changed(); };
            P(mute);
            ClipSlider(c, "Volume", 0, 2, 1, x => x.Volume, (x, v) => x.Volume = v, Pct);
            ClipSlider(c, "Fade in", 0, Math.Min(10, c.Duration), 0, x => x.AudioFadeIn, (x, v) => x.AudioFadeIn = Math.Min(v, x.Duration), Sec);
            ClipSlider(c, "Fade out", 0, Math.Min(10, c.Duration), 0, x => x.AudioFadeOut, (x, v) => x.AudioFadeOut = Math.Min(v, x.Duration), Sec);
            if (c.Kind == ClipKind.Video)
                P(new ButtonRow(Btn("Detach audio", Ico.Detach, "Move the sound to its own track", DetachAudio)));
            if (m != null && m.PcmFailed) HintP("The sound of this file couldn't be read.");
        }

        void BuildSpeedProps(Clip c)
        {
            int id = c.Id;
            Section("Speed");
            var s = new PropSlider("Speed", 0.25, 4, 1) { Format = v => v.ToString("0.00", CultureInfo.InvariantCulture) + "×" };
            s.Getter = () => { var cc = project.FindClip(id); return cc != null ? cc.Speed : 1; };
            s.SetSilently(c.Speed);
            s.Changing += v => { var cc = project.FindClip(id); if (cc == null) return; BeginChange("speed" + id); SetSpeed(cc, Math.Round(v * 20) / 20); LiveChanged(); };
            s.Committed += () => Changed();
            liveSliders.Add(s);
            P(s);
            Func<double, ThemedButton> preset = v =>
            {
                var b = Btn(v.ToString("0.##", CultureInfo.InvariantCulture) + "×", Ico.None, "Play at " + v + "× speed", () => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); SetSpeed(cc, v); Changed(); });
                b.Checked = Math.Abs(c.Speed - v) < 0.001;
                return b;
            };
            P(new ButtonRow(preset(0.5), preset(1), preset(1.5), preset(2)));
            HintP("Slower clips get longer and faster ones shorter. The sound keeps its pitch. Speeds below 0.5× or above 2× play the picture only if the sound gets too stretched.");
        }

        void BuildTextProps(Clip c)
        {
            int id = c.Id;
            Section("Words");
            var tb = new TextBox
            {
                Multiline = true, AcceptsReturn = true, Text = c.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"), Height = Theme.S(78),
                BorderStyle = BorderStyle.FixedSingle, Font = Theme.UiFont(10.5f), ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Current.Dark ? Color.FromArgb(24, 30, 56) : Color.White, ForeColor = Theme.Current.Text
            };
            tb.TextChanged += (s, e) => { var cc = project.FindClip(id); if (cc == null) return; BeginChange("text" + id); cc.Text = tb.Text.Replace("\r\n", "\n"); LiveChanged(); };
            tb.Leave += (s, e) => Changed();
            P(tb);
            Section("Style");
            var fontBtn = Btn(c.Font, Ico.None, "Font", () => { });
            fontBtn.AlignLeft = true;
            fontBtn.Font = new Font(TextLayout.Family(c.Font), 10f);
            fontBtn.Click += (s, e) => ShowFontMenu(fontBtn, id);
            P(fontBtn);
            ClipSlider(c, "Size", 0.02, 0.3, 0.08, x => x.FontSize, (x, v) => x.FontSize = v, v => Math.Round(v * project.Height) + " px");
            var bold = Btn("Bold", Ico.None, "Bold", () => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.Bold = !cc.Bold; propsFor = -2; Changed(); });
            bold.Checked = c.Bold; bold.Font = Theme.UiFont(10f, FontStyle.Bold);
            var italic = Btn("Italic", Ico.None, "Italic", () => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.Italic = !cc.Italic; propsFor = -2; Changed(); });
            italic.Checked = c.Italic; italic.Font = Theme.UiFont(10f, FontStyle.Italic);
            P(new ButtonRow(bold, italic));
            if (c.TextStyle != "lower")
            {
                var align = new Segmented();
                align.Add("0", "Left").Add("1", "Centre").Add("2", "Right");
                align.Selected = c.Align.ToString(CultureInfo.InvariantCulture);
                align.Picked += v => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.Align = int.Parse(v); Changed(); };
                P(align);
            }
            Section("Colour");
            P(ColorRow(c.TextColor, col => { var cc = project.FindClip(id); if (cc != null) cc.TextColor = col; }));
            Section(c.TextStyle == "lower" ? "Accent" : c.TextStyle == "neon" ? "Glow" : "Outline");
            if (c.TextStyle != "lower" && c.TextStyle != "neon") ClipSlider(c, "Outline width", 0, 0.2, 0, x => x.OutlineWidth, (x, v) => x.OutlineWidth = v, Pct);
            P(ColorRow(c.OutlineColor, col => { var cc = project.FindClip(id); if (cc != null) cc.OutlineColor = col; }));
            Section("Background");
            ClipSlider(c, "Box opacity", 0, 1, 0, x => x.BoxOpacity, (x, v) => x.BoxOpacity = v, Pct);
            P(ColorRow(c.BoxColor, col => { var cc = project.FindClip(id); if (cc != null) cc.BoxColor = col; }));
            var shadow = new ThemedToggle { Text = "Shadow", Checked = c.Shadow };
            shadow.CheckedChanged += (s, e) => { var cc = project.FindClip(id); if (cc == null) return; BeginChange(null); cc.Shadow = shadow.Checked; Changed(); };
            P(shadow);
        }

        SwatchPicker ColorRow(int current, Action<int> set)
        {
            var sw = new SwatchPicker { Columns = 7 };
            sw.Colors = new[]
            {
                Color.White, Color.Black, Color.FromArgb(255, 210, 40), Color.FromArgb(255, 90, 60), Color.FromArgb(255, 60, 200),
                Color.FromArgb(38, 104, 255), Color.FromArgb(176, 72, 255), Color.FromArgb(0, 200, 120), Color.FromArgb(0, 190, 230),
                Color.FromArgb(120, 120, 120), Color.FromArgb(30, 30, 60), Color.FromArgb(240, 140, 0), Color.FromArgb(200, 30, 50)
            };
            sw.Selected = Color.FromArgb(current);
            sw.ColorPicked += (s, e) => { BeginChange(null); set(e.Color.ToArgb()); Changed(); };
            sw.Height = sw.PreferredHeight;
            return sw;
        }

        void ShowFontMenu(Control anchor, int clipId)
        {
            var menu = new ContextMenuStrip();
            var families = new[] { "Segoe UI", "Segoe UI Black", "Segoe UI Semibold", "Arial", "Arial Black", "Bahnschrift", "Calibri", "Cambria", "Candara", "Comic Sans MS",
                "Consolas", "Constantia", "Corbel", "Georgia", "Impact", "Ink Free", "Lucida Handwriting", "Segoe Print", "Segoe Script", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana" };
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { using (var fc = new System.Drawing.Text.InstalledFontCollection()) foreach (var f in fc.Families) installed.Add(f.Name); } catch { }
            var cc0 = project.FindClip(clipId);
            foreach (var name in families)
            {
                if (installed.Count > 0 && !installed.Contains(name)) continue;
                string n = name;
                var mi = new ToolStripMenuItem(n, null, (s, e) => { var cc = project.FindClip(clipId); if (cc == null) return; BeginChange(null); cc.Font = n; propsFor = -2; Changed(); });
                try { mi.Font = new Font(n, 10f); } catch { }
                mi.Checked = cc0 != null && string.Equals(cc0.Font, n, StringComparison.OrdinalIgnoreCase);
                menu.Items.Add(mi);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Other font…", null, (s, e) =>
            {
                var cc = project.FindClip(clipId);
                if (cc == null) return;
                using (var dlg = new FontDialog { ShowEffects = false, FontMustExist = true })
                {
                    try { dlg.Font = new Font(cc.Font, 12f); } catch { }
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                    BeginChange(null);
                    cc.Font = dlg.Font.FontFamily.Name;
                    cc.Bold = dlg.Font.Bold; cc.Italic = dlg.Font.Italic;
                    propsFor = -2;
                    Changed();
                }
            }));
            Theme.StyleMenu(menu);
            menu.Closed += (s, e) => BeginInvoke((MethodInvoker)menu.Dispose);
            Ui.ShowMenuBelow(menu, anchor);
        }

        /// <summary>Simple horizontal layout for header buttons (equal widths, optional stacked rows).</summary>
        sealed class ButtonRowLayout : Control
        {
            readonly bool stacked;
            public ButtonRowLayout(params Control[] items) : this(false, items) { }
            public ButtonRowLayout(bool stacked, params Control[] items)
            {
                this.stacked = stacked;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                foreach (var c in items) Controls.Add(c);
            }

            protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Current.Panel); }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                int pad = Theme.S(12), gap = Theme.S(6), h = Theme.S(36);
                var list = Controls.Cast<Control>().ToList();
                if (list.Count == 0) return;
                if (stacked)
                {
                    int y = Theme.S(8);
                    foreach (var c in list) { c.SetBounds(pad, y, Width - pad * 2, h); y += h + gap; }
                    return;
                }
                // icon-only buttons stay square
                int fixedW = 0, flex = 0;
                foreach (var c in list) { var tb = c as ThemedButton; if (tb != null && tb.IconOnly) fixedW += h; else flex++; }
                int w = Math.Max(10, (Width - pad * 2 - gap * (list.Count - 1) - fixedW) / Math.Max(1, flex));
                int x = pad;
                foreach (var c in list)
                {
                    var tb = c as ThemedButton;
                    int cw = tb != null && tb.IconOnly ? h : w;
                    c.SetBounds(x, (Height - h) / 2, cw, h);
                    x += cw + gap;
                }
            }
        }
    }
}
