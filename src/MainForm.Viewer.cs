using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace StillsAndReels
{
    // The picture viewer's browsing: next and previous picture in the folder (with the next one read ahead so the
    // step is instant), full screen, slideshow and the details box.
    internal sealed partial class MainForm
    {
        ToolStripMenuItem miNextPic, miPrevPic, miPicFullscreen, miSlideshow, miSlideSpeed, miDetails;

        string[] folderList;            // the pictures in the folder of the one on screen, in name order
        string folderOf;                // which folder that list is for
        DateTime folderReadAt;
        string folderPosition;          // "3 of 57", shown in the status bar
        string stepTarget;              // the picture last asked for with next / previous
        int stepDirection = 1;

        LoadedImage ahead;              // the neighbour, read ahead
        string aheadPath;
        long aheadLength;
        DateTime aheadWritten;
        int aheadToken;

        readonly System.Windows.Forms.Timer slideTimer = new System.Windows.Forms.Timer();
        bool slideshow;

        // ------------------------------------------------------------------ the folder

        /// <summary>Compares names the way File Explorer lists them: numbers inside a name count as numbers.</summary>
        internal static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                char x = a[i], y = b[j];
                if (char.IsDigit(x) && char.IsDigit(y))
                {
                    int i0 = i, j0 = j;
                    while (i < a.Length && a[i] == '0') i++;
                    while (j < b.Length && b[j] == '0') j++;
                    int i1 = i, j1 = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    int la = i - i1, lb = j - j1;
                    if (la != lb) return la < lb ? -1 : 1;
                    int c = string.CompareOrdinal(a, i1, b, j1, la);
                    if (c != 0) return c < 0 ? -1 : 1;
                    int za = i1 - i0, zb = j1 - j0;
                    if (za != zb) { /* 007 and 7: same number; keep looking, break the tie at the end */ }
                    continue;
                }
                char ux = char.ToUpperInvariant(x), uy = char.ToUpperInvariant(y);
                if (ux != uy) return ux < uy ? -1 : 1;
                i++; j++;
            }
            if (i < a.Length) return 1;
            if (j < b.Length) return -1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The pictures in a folder, in name order. Safe to call from any thread.</summary>
        static string[] ListPictures(string dir)
        {
            try
            {
                var found = new List<string>();
                foreach (var f in Directory.GetFiles(dir))
                    if (FileTypes.Classify(f) == FileKind.Image) found.Add(f);
                found.Sort((x, y) => NaturalCompare(Path.GetFileName(x), Path.GetFileName(y)));
                return found.ToArray();
            }
            catch { return new string[0]; }
        }

        static string FolderOf(string path)
        {
            try { return path == null || ImageCache.IsPicked(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path)); }
            catch { return null; }
        }

        string[] FolderList(string path)
        {
            string dir = FolderOf(path);
            if (dir == null) return new string[0];
            if (folderList == null || !SamePath(folderOf ?? "", dir) || (DateTime.UtcNow - folderReadAt).TotalSeconds > 3)
            {
                folderList = ListPictures(dir);
                folderOf = dir;
                folderReadAt = DateTime.UtcNow;
            }
            return folderList;
        }

        static int IndexIn(string[] list, string path)
        {
            for (int i = 0; i < list.Length; i++) if (SamePath(list[i], path)) return i;
            return -1;
        }

        /// <summary>Shows the next (+1) or previous (-1) picture in the folder; int.MinValue and int.MaxValue go to the first and last.</summary>
        void StepPicture(int delta)
        {
            if (mode != AppMode.Pictures) return;
            string from = (loading || pendingPath != null) && stepTarget != null ? stepTarget : shownPath;
            var list = FolderList(from);
            if (from == null || list.Length == 0 || (list.Length == 1 && IndexIn(list, from) == 0)) { SystemSounds(); return; }
            int i = IndexIn(list, from), n;
            if (delta == int.MinValue) n = 0;
            else if (delta == int.MaxValue) n = list.Length - 1;
            else if (i < 0) n = delta > 0 ? 0 : list.Length - 1;
            else n = ((i + delta) % list.Length + list.Length) % list.Length;
            if (n == i) return;
            stepDirection = delta < 0 ? -1 : 1;
            stepTarget = list[n];
            OpenPath(list[n], FileKind.Image);
        }

        /// <summary>Called when a picture has just been put on screen: works out "3 of 57" and reads the neighbour ahead.</summary>
        void AfterPictureShown()
        {
            if (!loading && pendingPath == null) stepTarget = null;
            string path = shownPath;
            string dir = FolderOf(path);
            folderPosition = null;
            if (dir == null || mode != AppMode.Pictures) { DropAhead(); return; }
            int token = ++aheadToken;
            int direction = stepDirection;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var list = ListPictures(dir);
                int at = IndexIn(list, path);
                string next = at >= 0 && list.Length > 1 ? list[((at + direction) % list.Length + list.Length) % list.Length] : null;
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)(() =>
                    {
                        if (token != aheadToken || !SamePath(shownPath ?? "", path)) return;
                        folderList = list; folderOf = dir; folderReadAt = DateTime.UtcNow;
                        folderPosition = at >= 0 && list.Length > 1 ? (at + 1) + " of " + list.Length : null;
                        UpdateStatus();
                    }));
                }
                catch { return; }

                // read the neighbour ahead, unless it is already there or far too big to keep around
                if (next == null) return;
                LoadedImage li = null;
                try
                {
                    var info = new FileInfo(next);
                    if (!info.Exists || info.Length > 96L * 1024 * 1024) return;
                    lock (aheadGate) { if (ahead != null && SamePath(aheadPath, next) && aheadLength == info.Length && aheadWritten == info.LastWriteTimeUtc) return; }
                    if (token != aheadToken) return;
                    li = LoadedImage.Load(next);
                    if (li.IsAnimated || li.Surface == null || (long)li.Surface.Width * li.Surface.Height > 64L * 1000 * 1000) { Discard(li); return; }
                    LoadedImage old = null;
                    lock (aheadGate)
                    {
                        if (token != aheadToken) { old = li; }
                        else
                        {
                            old = ahead;
                            ahead = li; aheadPath = next; aheadLength = info.Length; aheadWritten = info.LastWriteTimeUtc;
                        }
                    }
                    Discard(old);
                }
                catch { Discard(li); }
            });
        }

        readonly object aheadGate = new object();

        /// <summary>Hands over the picture read ahead, if it is the one asked for and its file hasn't changed since.</summary>
        LoadedImage TakeAhead(string path)
        {
            LoadedImage li = null;
            lock (aheadGate)
            {
                if (ahead == null) return null;
                if (!SamePath(aheadPath, path)) return null;
                li = ahead; ahead = null;
            }
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length == aheadLength && info.LastWriteTimeUtc == aheadWritten) return li;
            }
            catch { }
            Discard(li);
            return null;
        }

        /// <summary>Lets go of the picture read ahead (editing and video need the memory more).</summary>
        void DropAhead()
        {
            LoadedImage li;
            lock (aheadGate) { aheadToken++; li = ahead; ahead = null; }
            Discard(li);
        }

        string ViewerStatus()
        {
            return mode == AppMode.Pictures && folderPosition != null && edit == null ? "    " + folderPosition : "";
        }

        // ------------------------------------------------------------------ full screen and slideshow

        void TogglePictureFullscreen()
        {
            if (mode != AppMode.Pictures) return;
            if (fullscreen) StopSlideshow();
            ToggleFullscreen();
            canvas.FitToWindow();
        }

        void ToggleSlideshow()
        {
            if (slideshow) { StopSlideshow(); if (fullscreen) TogglePictureFullscreen(); return; }
            if (mode != AppMode.Pictures || shownPath == null || FolderList(shownPath).Length < 2) { SystemSounds(); return; }
            if (!fullscreen) TogglePictureFullscreen();
            slideshow = true;
            slideTimer.Interval = Math.Max(1, Math.Min(60, settings.GetInt("SlideshowSeconds", 4))) * 1000;
            slideTimer.Start();
            UpdateMenus();
        }

        void StopSlideshow()
        {
            if (!slideshow) return;
            slideshow = false;
            slideTimer.Stop();
            UpdateMenus();
        }

        void OnSlideTick()
        {
            if (!slideshow) { slideTimer.Stop(); return; }
            if (!fullscreen || mode != AppMode.Pictures || !Visible) { StopSlideshow(); return; }
            if (loading) return;        // a slow picture gets its full time once it is up
            StepPicture(1);
        }

        void SetSlideSeconds(int seconds)
        {
            settings.Set("SlideshowSeconds", seconds);
            if (slideshow) { slideTimer.Stop(); slideTimer.Interval = seconds * 1000; slideTimer.Start(); }
            UpdateMenus();
        }

        /// <summary>Keys for the viewer (not while editing).</summary>
        bool ViewerKey(Keys keyData)
        {
            if (mode != AppMode.Pictures || IsMenuOpen() || menu.ContainsFocus) return false;
            switch (keyData)
            {
                case Keys.Right: case Keys.PageDown: StepPicture(1); RestartSlide(); return true;
                case Keys.Left: case Keys.PageUp: StepPicture(-1); RestartSlide(); return true;
                case Keys.Home: StepPicture(int.MinValue); return true;
                case Keys.End: StepPicture(int.MaxValue); return true;
                case Keys.F11: TogglePictureFullscreen(); return true;
                case Keys.F5: ToggleSlideshow(); return true;
                case Keys.Control | Keys.I: ShowPictureDetails(); return true;
                case Keys.Space:
                    if (slideshow) { if (slideTimer.Enabled) slideTimer.Stop(); else slideTimer.Start(); return true; }
                    if (fullscreen) { StepPicture(1); return true; }
                    return false;
                case Keys.Escape:
                    if (slideshow) { StopSlideshow(); if (fullscreen) TogglePictureFullscreen(); return true; }
                    if (fullscreen) { TogglePictureFullscreen(); return true; }
                    return false;
            }
            return false;
        }

        void RestartSlide()
        {
            if (slideshow && slideTimer.Enabled) { slideTimer.Stop(); slideTimer.Start(); }
        }

        // ------------------------------------------------------------------ details

        void ShowPictureDetails()
        {
            if (mode != AppMode.Pictures && mode != AppMode.PictureEdit) return;
            if (current == null && edit == null) { SystemSounds(); return; }
            var rows = new List<KeyValuePair<string, string>>();
            string path = edit != null ? edit.SourcePath : (current != null ? current.SourcePath : null);
            bool picked = path != null && ImageCache.IsPicked(path);
            Size size = edit != null ? edit.Size : current.Image.Size;
            rows.Add(new KeyValuePair<string, string>("Name", PictureName ?? "New picture"));
            if (path != null && !picked) rows.Add(new KeyValuePair<string, string>("Folder", Path.GetDirectoryName(path)));
            rows.Add(new KeyValuePair<string, string>("Size", size.Width + " × " + size.Height + " pixels  (" + (size.Width * (double)size.Height / 1e6).ToString("0.0") + " megapixels)"));
            if (edit != null && edit.State.Layers.Length > 1) rows.Add(new KeyValuePair<string, string>("Layers", edit.State.Layers.Length.ToString()));
            byte[] head = null;
            if (path != null && current != null && SamePath(current.SourcePath ?? "", path))
            {
                rows.Add(new KeyValuePair<string, string>("File size", FormatSize(current.FileSize)));
                try
                {
                    if (!picked && File.Exists(path)) rows.Add(new KeyValuePair<string, string>("Changed", File.GetLastWriteTime(path).ToString("d MMMM yyyy, HH:mm")));
                    if (ImageSaver.IsJpeg(path) && current.CachePath != null)
                        using (var fs = new FileStream(current.CachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            head = new byte[(int)Math.Min(fs.Length, 256 * 1024)];
                            int got = 0, n;
                            while (got < head.Length && (n = fs.Read(head, got, head.Length - got)) > 0) got += n;
                        }
                }
                catch { }
            }
            if (current != null && current.HasAlpha && edit == null) rows.Add(new KeyValuePair<string, string>("See-through parts", "Yes"));
            var details = JpegDetails.Describe(JpegDetails.Read(head));
            PicHistogram hist = null;
            try
            {
                if (edit != null) hist = PicAdjust.Histogram(edit.Compositor.Picture);
                else if (current != null && current.Surface != null) hist = PicAdjust.Histogram(current.Mips != null && current.Mips.Count > 1 ? current.Mips.Level(1) : current.Surface);
            }
            catch { }
            using (var dlg = new DetailsDialog(rows, details, hist)) dlg.ShowDialog(this);
        }

        // ------------------------------------------------------------------ menus

        void AddViewerMenus(ToolStripMenuItem view)
        {
            miNextPic = Item("&Next picture in the folder", Keys.None, (s, e) => StepPicture(1), "→");
            miPrevPic = Item("P&revious picture in the folder", Keys.None, (s, e) => StepPicture(-1), "←");
            miPicFullscreen = Item("F&ull screen", Keys.None, (s, e) => TogglePictureFullscreen(), "F11");
            miSlideshow = Item("Sli&deshow", Keys.None, (s, e) => ToggleSlideshow(), "F5");
            miSlideSpeed = new ToolStripMenuItem("Slideshow s&peed");
            foreach (int sec in new[] { 2, 3, 4, 6, 10, 20 })
            {
                int seconds = sec;
                miSlideSpeed.DropDownItems.Add(new ToolStripMenuItem(seconds + " seconds each", null, (s, e) => SetSlideSeconds(seconds)) { Tag = seconds });
            }
            miDetails = Item("Picture d&etails…", Keys.None, (s, e) => ShowPictureDetails(), "Ctrl+I");
            int at = view.DropDownItems.IndexOf(miPlayback);
            if (at < 0) at = view.DropDownItems.Count;
            foreach (var it in new ToolStripItem[] { miPrevPic, miNextPic, miPicFullscreen, miSlideshow, miSlideSpeed, miDetails, new ToolStripSeparator() })
                view.DropDownItems.Insert(at++, it);
            slideTimer.Tick += (s, e) => OnSlideTick();
        }

        void UpdateViewerMenus()
        {
            bool viewing = mode == AppMode.Pictures;
            bool pictures = viewing || mode == AppMode.PictureEdit;
            if (!viewing && ahead != null) DropAhead();      // editing and video need the memory more than a picture read ahead
            bool has = current != null || edit != null;
            miNextPic.Visible = miPrevPic.Visible = miPicFullscreen.Visible = miSlideshow.Visible = miSlideSpeed.Visible = miDetails.Visible = pictures;
            miNextPic.Enabled = miPrevPic.Enabled = miSlideshow.Enabled = viewing && shownPath != null && !ImageCache.IsPicked(shownPath);
            miPicFullscreen.Enabled = viewing;
            miPicFullscreen.Checked = viewing && fullscreen;
            miSlideshow.Checked = slideshow;
            miSlideSpeed.Enabled = viewing;
            miDetails.Enabled = pictures && has;
            int now = settings.GetInt("SlideshowSeconds", 4);
            foreach (ToolStripMenuItem it in miSlideSpeed.DropDownItems) it.Checked = (int)it.Tag == now;
        }
    }
}
