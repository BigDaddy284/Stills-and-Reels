using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Accessibility;

namespace StillsAndReels
{
    /// <summary>
    /// Picks up pictures and files clicked in other apps. A low-level mouse hook only watches left clicks
    /// (it never blocks or changes them); the work happens on a separate STA thread so a slow app can't
    /// hold up the mouse.
    ///  - Pictures: on button-down the screen under the cursor is copied and the picture there is found
    ///    (see FindPicture: accessibility first, then what the screen shows). The part of it that is on screen is
    ///    kept. It is handed over on button-up, and only if the mouse didn't move (so drags
    ///    and text selection are left alone). Nothing is downloaded: what you get is what was on screen.
    ///  - Files: on button-up, the clicked item in an Open/Save dialog or another app's file list is turned
    ///    into a path (full path in the item's text, or the item's name plus the folder in the window's
    ///    address bar or title). Only files that exist on a local drive are reported.
    /// File Explorer windows are left to ExplorerWatcher, which reads their real selection. The desktop is read by
    /// both: ExplorerWatcher reads its selection, and clicks on its icons are matched to the Desktop folders here.
    /// </summary>
    internal sealed class PickupWatcher : IDisposable
    {
        public event Action<Bitmap, string> PictureClicked;     // raised on the worker thread; the handler owns the bitmap
        public event Action<string, FileKind> FileClicked;      // raised on the worker thread

        /// <summary>Pick up pictures from web pages and other apps (Pictures mode).</summary>
        public volatile bool WantPictures;
        /// <summary>Pick up clicked files from file dialogs and other apps.</summary>
        public volatile bool WantFiles;
        /// <summary>Which files count: videos (Videos / video editor) or pictures.</summary>
        public volatile bool WantVideos;

        const int MinPictureSize = 40;     // physical pixels; smaller graphics are icons and buttons
        const int MaxClickMove = 6;
        const int MaxClickMs = 900;

        readonly int ownPid = Process.GetCurrentProcess().Id;
        readonly object gate = new object();
        readonly Queue<Click> queue = new Queue<Click>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        volatile bool running;
        Thread hookThread, worker;
        uint hookThreadId;
        IntPtr hook;
        LowLevelMouseProc proc;   // kept referenced so the GC doesn't collect the callback

        // state of the current click (worker thread only)
        Point downPt;
        int downTime;
        Bitmap pending;
        string pendingName;
        long lastHash;

        struct Click { public bool Down; public Point Pt; public int Time; }

        public bool Active { get { return WantPictures || WantFiles; } }

        public void Start()
        {
            if (running) return;
            running = true;
            worker = new Thread(WorkLoop) { IsBackground = true, Name = "Pick-up worker" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            hookThread = new Thread(HookLoop) { IsBackground = true, Name = "Mouse hook" };
            hookThread.Start();
        }

        public void Dispose()
        {
            if (!running) return;
            running = false;
            signal.Set();
            if (hookThreadId != 0) PostThreadMessage(hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        // ------------------------------------------------------------------ hook thread

        void HookLoop()
        {
            try { RunHook(); }
            catch (Exception ex) { PickupLog.Write("mouse hook failed: " + ex.GetType().Name + " " + ex.Message); }   // no hook (e.g. blocked by security software): pick-up from other apps just stays off
        }

        void RunHook()
        {
            UsePhysicalPixels();
            hookThreadId = GetCurrentThreadId();
            proc = HookCallback;
            hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) { PickupLog.Write("mouse hook could not be installed, error " + Marshal.GetLastWin32Error()); return; }
            PickupLog.Write("mouse hook installed");
            MSG msg;
            while (running && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            // Keep this as short as possible: Windows drops hooks that are slow. Just note the click.
            try
            {
                if (code >= 0 && Active)
                {
                    int m = wParam.ToInt32();
                    if (m == WM_LBUTTONDOWN || m == WM_LBUTTONUP)
                    {
                        var s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        lock (gate)
                        {
                            if (queue.Count > 8) queue.Clear();   // the worker is stuck on a busy app: drop old clicks
                            queue.Enqueue(new Click { Down = m == WM_LBUTTONDOWN, Pt = new Point(s.x, s.y), Time = Environment.TickCount });
                        }
                        signal.Set();
                    }
                }
            }
            catch { }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        // ------------------------------------------------------------------ worker

        void WorkLoop()
        {
            try { UsePhysicalPixels(); } catch { }
            while (running)
            {
                signal.WaitOne(1000);
                while (running)
                {
                    Click c;
                    lock (gate)
                    {
                        if (queue.Count == 0) break;
                        c = queue.Dequeue();
                    }
                    try
                    {
                        if (c.Down) OnDown(c); else OnUp(c);
                    }
                    catch (Exception ex) { PickupLog.Write("error: " + ex.GetType().Name + " " + ex.Message); DropPending(); }
                }
            }
            DropPending();
        }

        void DropPending()
        {
            if (pending != null) { pending.Dispose(); pending = null; }
        }

        void OnDown(Click c)
        {
            DropPending();
            downPt = c.Pt;
            downTime = c.Time;
            if (!WantPictures) return;
            if (Environment.TickCount - c.Time > MaxClickMs) { PickupLog.Write("picture: click skipped, worker was busy"); return; }
            IntPtr root;
            if (!IsForeignWindow(c.Pt, out root) || IsShellWindow(root)) return;
            string cls = NativeMethods.ClassName(root);

            // Copy what is on screen first, before the app reacts to the click (a link starts loading another page).
            Rectangle area = VisiblePart(MonitorAt(c.Pt), root);
            if (area.Width < MinPictureSize || area.Height < MinPictureSize) return;
            using (var shot = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);

                string name, how;
                Rectangle r = FindPicture(c.Pt, cls, area, shot, out name, out how);
                if (r.IsEmpty)
                {
                    PickupLog.Write("picture: nothing found in " + cls + " (" + ProcessName(root) + ") at " + c.Pt.X + "," + c.Pt.Y + ": " + how);
                    return;
                }
                r.Intersect(area);
                if (r.Width < MinPictureSize / 2 || r.Height < MinPictureSize / 2) return;
                var crop = new Rectangle(r.X - area.X, r.Y - area.Y, r.Width, r.Height);
                pending = shot.Clone(crop, PixelFormat.Format32bppArgb);
                pendingName = PictureName(name, root);
                PickupLog.Write("picture: " + how + " in " + cls + " (" + ProcessName(root) + "), " + r.Width + "x" + r.Height);
            }
        }

        void OnUp(Click c)
        {
            bool isClick = Math.Abs(c.Pt.X - downPt.X) <= MaxClickMove && Math.Abs(c.Pt.Y - downPt.Y) <= MaxClickMove
                           && unchecked(c.Time - downTime) <= MaxClickMs;
            if (pending != null)
            {
                var bmp = pending;
                pending = null;
                long hash = Hash(bmp);
                if (!isClick || !WantPictures || hash == lastHash)
                {
                    PickupLog.Write(!isClick ? "picture: dropped, the mouse moved (drag)" : hash == lastHash ? "picture: same as last time" : "picture: dropped, pick-up switched off");
                    bmp.Dispose();
                }
                else
                {
                    lastHash = hash;
                    var h = PictureClicked;
                    if (h != null) h(bmp, pendingName); else bmp.Dispose();
                }
                return;   // a picture is never also a file item
            }
            if (!isClick || !WantFiles) return;
            IntPtr root;
            if (!IsForeignWindow(c.Pt, out root)) return;
            bool desktop = IsDesktop(root);
            if (!desktop && IsShellWindow(root)) return;
            Thread.Sleep(80);   // let the app update its selection first
            bool videos = WantVideos;
            FileKind kind;
            // The desktop's own selection is also read by ExplorerWatcher; this is the backup for when that
            // isn't available (it reads the clicked icon's name against the Desktop folders, OneDrive's included).
            string path = desktop ? FindDesktopFile(c.Pt, videos, out kind) : FindFile(c.Pt, root, videos, out kind);
            PickupLog.Write("file: " + (desktop ? "desktop" : NativeMethods.ClassName(root) + " (" + ProcessName(root) + ")") + " -> " + (path ?? "no " + (videos ? "video" : "picture") + " file found"));
            if (path == null) return;
            var fh = FileClicked;
            if (fh != null) fh(path, kind);
        }

        // ------------------------------------------------------------------ which window

        bool IsForeignWindow(Point pt, out IntPtr root)
        {
            root = IntPtr.Zero;
            IntPtr h = WindowFromPoint(pt);
            if (h == IntPtr.Zero) return false;
            root = NativeMethods.GetAncestor(h, NativeMethods.GA_ROOT);
            if (root == IntPtr.Zero) root = h;
            uint pid;
            GetWindowThreadProcessId(root, out pid);
            return pid != 0 && pid != ownPid;
        }

        static bool IsDesktop(IntPtr root)
        {
            string c = NativeMethods.ClassName(root);
            return c == "Progman" || c == "WorkerW";
        }

        static string ProcessName(IntPtr hwnd)
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                using (var p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch { return "?"; }
        }

        /// <summary>File Explorer and the desktop (read properly by ExplorerWatcher), the taskbar, Start, task switcher.</summary>
        static bool IsShellWindow(IntPtr root)
        {
            switch (NativeMethods.ClassName(root))
            {
                case "CabinetWClass": case "ExploreWClass": case "Progman": case "WorkerW":
                case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd": case "NotifyIconOverflowWindow":
                case "TaskListThumbnailWnd": case "MultitaskingViewFrame": case "ForegroundStaging":
                case "XamlExplorerHostIslandWindow": case "Windows.UI.Core.CoreWindow": case "TopLevelWindowForOverflowXamlIsland":
                    return true;
            }
            return false;
        }

        /// <summary>The screen (monitor) the point is on, in real pixels.</summary>
        static Rectangle MonitorAt(Point pt)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            IntPtr mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                return Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
            return Rectangle.FromLTRB(pt.X - 1500, pt.Y - 1500, pt.X + 1500, pt.Y + 1500);
        }

        static Rectangle VisiblePart(Rectangle r, IntPtr root)
        {
            RECT wr;
            if (GetWindowRect(root, out wr)) r.Intersect(Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom));
            r.Intersect(new Rectangle(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                                      GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN)));
            return r;
        }

        // ------------------------------------------------------------------ pictures

        /// <summary>
        /// Finds the picture under the point, in this order:
        ///  1. an accessibility "graphic" (an &lt;img&gt;, a picture in an app): the hit element, its children or parents.
        ///     Browsers build their accessibility tree only once something asks for it, so for a browser the
        ///     question is asked again a few times while the tree appears.
        ///  2. the clicked element itself (a link, tile or button around a picture the page hides from
        ///     accessibility, as photo sites and OneDrive do) when what it shows on screen looks like a photo.
        ///  3. no usable accessibility at all: the picture-like block around the point, found from the screen copy.
        /// </summary>
        static Rectangle FindPicture(Point pt, string cls, Rectangle area, Bitmap shot, out string name, out string how)
        {
            name = null; how = "";
            bool browser = cls.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) || cls == "MozillaWindowClass"
                           || cls == "ApplicationFrameWindow" || cls.StartsWith("WebView", StringComparison.Ordinal);
            var boxes = new List<Rectangle>();
            int tries = browser ? 4 : 1;
            for (int attempt = 0; attempt < tries; attempt++)
            {
                if (attempt > 0) Thread.Sleep(120);
                boxes.Clear();
                Rectangle r = AccessiblePicture(pt, boxes, out name, out how);
                if (r.Width >= MinPictureSize && r.Height >= MinPictureSize) { how = "graphic" + (attempt > 0 ? " (try " + (attempt + 1) + ")" : ""); return r; }
                if (boxes.Count > 0 && !browser) break;
                if (boxes.Count > 1) break;   // the page's tree is there; the picture just isn't marked as one
            }
            // 2. elements around the point, smallest first, that look like a photo on screen
            boxes.Sort((a, b) => (a.Width * a.Height).CompareTo(b.Width * b.Height));
            long maxArea = (long)area.Width * area.Height * 6 / 10;
            foreach (var b in boxes)
            {
                if (b.Width < 64 || b.Height < 64 || (long)b.Width * b.Height > maxArea || !b.Contains(pt)) continue;
                var vis = Rectangle.Intersect(b, area);
                if (vis.Width < 64 || vis.Height < 64) continue;
                if (LooksLikePhoto(shot, new Rectangle(vis.X - area.X, vis.Y - area.Y, vis.Width, vis.Height)))
                {
                    how += "; element that looks like a photo";
                    name = null;
                    return b;
                }
            }
            // 3. from the pixels
            var local = PictureBlock(shot, new Point(pt.X - area.X, pt.Y - area.Y));
            if (!local.IsEmpty && (long)local.Width * local.Height <= (long)area.Width * area.Height * 8 / 10
                && LooksLikePhoto(shot, local))
            {
                how += "; picture found on screen";
                name = null;
                return new Rectangle(local.X + area.X, local.Y + area.Y, local.Width, local.Height);
            }
            how += "; " + boxes.Count + " elements, none like a picture";
            return Rectangle.Empty;
        }

        /// <summary>Accessibility pass: returns a graphic's box, and collects the boxes of the elements around the point.</summary>
        static Rectangle AccessiblePicture(Point pt, List<Rectangle> boxes, out string name, out string how)
        {
            name = null; how = "";
            IAccessible acc; object child;
            int hr = AccessibleObjectFromPoint(new POINT { X = pt.X, Y = pt.Y }, out acc, out child);
            if (hr != 0 || acc == null) { how = "no accessibility (0x" + hr.ToString("X8") + ")"; return Rectangle.Empty; }
            try
            {
                Rectangle r;
                how = "hit " + RoleName(acc, child);
                if (IsPictureAt(acc, child, pt, out r, out name)) return r;
                AddBox(acc, child, boxes);
                if (IsSelf(child))
                {
                    if (PictureChild(acc, pt, 2, out r, out name)) return r;
                }
                else if (IsPictureAt(acc, 0, pt, out r, out name)) return r;
                IAccessible p = acc;
                for (int up = 0; up < 4; up++)
                {
                    object parent = null;
                    try { parent = p.accParent; } catch { }
                    p = parent as IAccessible;
                    if (p == null) break;
                    if (IsPictureAt(p, 0, pt, out r, out name)) return r;
                    AddBox(p, 0, boxes);
                }
            }
            catch { }
            return Rectangle.Empty;
        }

        static void AddBox(IAccessible acc, object child, List<Rectangle> boxes)
        {
            try
            {
                int l, t, w, h;
                acc.accLocation(out l, out t, out w, out h, child ?? 0);
                if (w > 0 && h > 0) boxes.Add(new Rectangle(l, t, w, h));
            }
            catch { }
        }

        static string RoleName(IAccessible acc, object child)
        {
            try { object r = acc.get_accRole(child ?? 0); return r is int ? "role 0x" + ((int)r).ToString("X") : "role ?"; }
            catch { return "role ?"; }
        }

        /// <summary>
        /// True when an area of the screen copy looks like a photo or picture rather than text or plain UI:
        /// many different colours and no single colour (a page background) covering most of it.
        /// </summary>
        internal static bool LooksLikePhoto(Bitmap bmp, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
            if (r.Width < 16 || r.Height < 16) return false;
            var counts = new Dictionary<int, int>();
            int n = 0;
            const int grid = 40;
            for (int gy = 0; gy < grid; gy++)
                for (int gx = 0; gx < grid; gx++)
                {
                    int x = r.X + (int)((gx + 0.5) * r.Width / grid), y = r.Y + (int)((gy + 0.5) * r.Height / grid);
                    int c = bmp.GetPixel(x, y).ToArgb();
                    int q = ((c >> 19) & 0x1F) << 10 | ((c >> 11) & 0x1F) << 5 | ((c >> 3) & 0x1F);   // 5 bits per channel
                    int v; counts.TryGetValue(q, out v); counts[q] = v + 1; n++;
                }
            int top = 0;
            foreach (var v in counts.Values) if (v > top) top = v;
            return counts.Count >= 120 && top * 100 / n < 35;
        }

        /// <summary>
        /// The block of busy (non-plain) pixels around a point: grows a box outwards until each side meets a plain
        /// line (the page background or the gap between pictures). Empty when the point is on a plain area.
        /// </summary>
        internal static Rectangle PictureBlock(Bitmap bmp, Point p)
        {
            const int seed = 20, limit = 4000;
            if (p.X < 0 || p.Y < 0 || p.X >= bmp.Width || p.Y >= bmp.Height) return Rectangle.Empty;
            int l = Math.Max(0, p.X - seed), t = Math.Max(0, p.Y - seed);
            int r = Math.Min(bmp.Width - 1, p.X + seed), b = Math.Min(bmp.Height - 1, p.Y + seed);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride / 4;
                var px = new int[stride * bmp.Height];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                Func<int, int, int, int, bool> plain = (x0, y0, x1, y1) =>
                {
                    // a line is plain when 97% of it matches its first pixel (within 12 per channel)
                    int first = px[y0 * stride + x0], total = 0, same = 0;
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int c = px[y * stride + x];
                            total++;
                            if (Math.Abs(((c >> 16) & 255) - ((first >> 16) & 255)) <= 12 && Math.Abs(((c >> 8) & 255) - ((first >> 8) & 255)) <= 12
                                && Math.Abs((c & 255) - (first & 255)) <= 12) same++;
                        }
                    return same * 100 >= total * 97;
                };
                if (plain(l, t, r, b)) return Rectangle.Empty;   // clicked on plain background
                for (int i = 0; i < limit; i++)
                {
                    bool grew = false;
                    if (l > 0 && !plain(l - 1, t, l - 1, b)) { l--; grew = true; }
                    if (r < bmp.Width - 1 && !plain(r + 1, t, r + 1, b)) { r++; grew = true; }
                    if (t > 0 && !plain(l, t - 1, r, t - 1)) { t--; grew = true; }
                    if (b < bmp.Height - 1 && !plain(l, b + 1, r, b + 1)) { b++; grew = true; }
                    if (!grew) break;
                }
                return Rectangle.FromLTRB(l, t, r + 1, b + 1);
            }
            finally { bmp.UnlockBits(data); }
        }

        static bool IsSelf(object child)
        {
            return child == null || (child is int && (int)child == 0);
        }

        static bool IsPictureAt(IAccessible acc, object child, Point pt, out Rectangle r, out string name)
        {
            r = Rectangle.Empty; name = null;
            try
            {
                object role = acc.get_accRole(child ?? 0);
                if (!(role is int) || (int)role != ROLE_SYSTEM_GRAPHIC) return false;
                int l, t, w, h;
                acc.accLocation(out l, out t, out w, out h, child ?? 0);
                r = new Rectangle(l, t, w, h);
                if (!r.Contains(pt)) return false;
                try { name = acc.get_accName(child ?? 0); } catch { }
                return true;
            }
            catch { return false; }
        }

        static bool PictureChild(IAccessible acc, Point pt, int depth, out Rectangle r, out string name)
        {
            r = Rectangle.Empty; name = null;
            int count;
            try { count = acc.accChildCount; } catch { return false; }
            if (count <= 0 || count > 40) return false;
            var kids = new object[count];
            int got;
            if (AccessibleChildren(acc, 0, count, kids, out got) != 0) return false;
            for (int i = 0; i < got; i++)
            {
                var k = kids[i] as IAccessible;
                bool hit = k != null ? IsPictureAt(k, 0, pt, out r, out name) : kids[i] is int && IsPictureAt(acc, kids[i], pt, out r, out name);
                if (hit) return true;
                if (k != null && depth > 1 && Contains(k, pt) && PictureChild(k, pt, depth - 1, out r, out name)) return true;
            }
            return false;
        }

        static bool Contains(IAccessible acc, Point pt)
        {
            try
            {
                int l, t, w, h;
                acc.accLocation(out l, out t, out w, out h, 0);
                return new Rectangle(l, t, w, h).Contains(pt);
            }
            catch { return false; }
        }

        static string PictureName(string alt, IntPtr root)
        {
            string n = Clean(alt);
            if (string.IsNullOrEmpty(n) || n.Length < 2) n = null;
            if (n == null)
            {
                string title = Clean(WindowText(root));
                n = string.IsNullOrEmpty(title) ? "Picture" : "Picture from " + title;
            }
            if (n.Length > 60) n = n.Substring(0, 60).TrimEnd();
            return n;
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder();
            foreach (char ch in s) sb.Append("\\/:*?\"<>|".IndexOf(ch) >= 0 || char.IsControl(ch) ? ' ' : ch);
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.');
        }

        /// <summary>Cheap fingerprint of a capture, so clicking the same picture twice doesn't reload it.</summary>
        static long Hash(Bitmap b)
        {
            long h = b.Width * 31L + b.Height;
            int sx = Math.Max(1, b.Width / 12), sy = Math.Max(1, b.Height / 12);
            for (int y = sy / 2; y < b.Height; y += sy)
                for (int x = sx / 2; x < b.Width; x += sx)
                    h = h * 1000003L ^ b.GetPixel(x, y).ToArgb();
            return h;
        }

        // ------------------------------------------------------------------ files

        static string FindFile(Point pt, IntPtr root, bool videos, out FileKind kind)
        {
            kind = FileKind.Other;
            var texts = new List<string>();
            bool fileItem = false;   // a row, cell or item of a list or tree, as file lists are
            IAccessible acc; object child;
            if (AccessibleObjectFromPoint(new POINT { X = pt.X, Y = pt.Y }, out acc, out child) != 0 || acc == null) return null;
            try
            {
                AddTexts(acc, child, texts);
                fileItem |= IsItemRole(acc, child);
                if (!IsSelf(child)) { AddTexts(acc, 0, texts); fileItem |= IsItemRole(acc, 0); }
                IAccessible p = acc;
                for (int up = 0; up < 3; up++)
                {
                    object parent = null;
                    try { parent = p.accParent; } catch { }
                    p = parent as IAccessible;
                    if (p == null) break;
                    int role = 0;
                    try { object r = p.get_accRole(0); if (r is int) role = (int)r; } catch { }
                    if (role == ROLE_SYSTEM_LIST || role == ROLE_SYSTEM_OUTLINE || role == ROLE_SYSTEM_WINDOW || role == ROLE_SYSTEM_CLIENT) break;
                    fileItem |= IsItemRole(role);
                    AddTexts(p, 0, texts);
                }
            }
            catch { }
            if (texts.Count == 0) return null;

            // 1. a full path in the item itself (search tools, recent-file lists)
            foreach (var t in texts)
            {
                string full = AbsolutePath(t);
                if (full != null && Accept(full, videos, out kind)) return full;
            }
            // 2. the item's name inside the folder the window is showing (only for items of a list, not any text)
            if (!fileItem) return null;
            List<string> folders = null;
            foreach (var t in texts)
            {
                string n = t.Trim();
                if (n.Length == 0 || n.Length > 255 || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                if (folders == null) folders = WindowFolders(root);
                foreach (var dir in folders)
                {
                    string found = InFolder(dir, n, videos, out kind);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>The desktop icon under the point, found by its name in the Desktop folders (yours, OneDrive's and Public).</summary>
        static string FindDesktopFile(Point pt, bool videos, out FileKind kind)
        {
            kind = FileKind.Other;
            var texts = new List<string>();
            IAccessible acc; object child;
            if (AccessibleObjectFromPoint(new POINT { X = pt.X, Y = pt.Y }, out acc, out child) != 0 || acc == null) return null;
            try
            {
                if (!IsItemRole(acc, child)) return null;   // clicked the wallpaper, not an icon
                AddTexts(acc, child, texts);
            }
            catch { return null; }
            foreach (var dir in DesktopFolders())
                foreach (var t in texts)
                {
                    string n = t.Trim();
                    if (n.Length == 0 || n.Length > 255 || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                    string found = InFolder(dir, n, videos, out kind);
                    if (found != null) return found;
                }
            return null;
        }

        internal static List<string> DesktopFolders()
        {
            var list = new List<string>();
            Action<string> add = d => { if (!string.IsNullOrEmpty(d) && SafeDirExists(d) && !list.Contains(d)) list.Add(d); };
            add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));   // follows OneDrive's Desktop backup
            add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            {
                string od = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(od)) add(Path.Combine(od, "Desktop"));
            }
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile)) add(Path.Combine(profile, "Desktop"));
            return list;
        }

        static bool IsItemRole(IAccessible acc, object child)
        {
            try { object r = acc.get_accRole(child); return r is int && IsItemRole((int)r); }
            catch { return false; }
        }

        static bool IsItemRole(int role)
        {
            return role == ROLE_SYSTEM_LISTITEM || role == ROLE_SYSTEM_OUTLINEITEM || role == ROLE_SYSTEM_CELL || role == ROLE_SYSTEM_ROW;
        }

        static void AddTexts(IAccessible acc, object child, List<string> texts)
        {
            try { string s = acc.get_accName(child); if (!string.IsNullOrWhiteSpace(s) && !texts.Contains(s)) texts.Add(s); } catch { }
            try { string s = acc.get_accValue(child); if (!string.IsNullOrWhiteSpace(s) && !texts.Contains(s)) texts.Add(s); } catch { }
        }

        /// <summary>A path like C:\folder\file.mp4 inside the text (also "file:///C:/..."), on a local drive.</summary>
        static string AbsolutePath(string t)
        {
            if (string.IsNullOrEmpty(t)) return null;
            string s = t.Trim().Trim('"');
            if (s.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                try { s = new Uri(s).LocalPath; } catch { return null; }
            }
            int i = s.IndexOf(":\\", StringComparison.Ordinal);
            if (i < 1) return null;
            s = s.Substring(i - 1);
            if (!char.IsLetter(s[0])) return null;
            if (s.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            return IsLocalDrive(s) ? s : null;
        }

        static bool IsLocalDrive(string path)
        {
            try
            {
                string drive = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(drive) || drive.StartsWith("\\\\")) return false;   // network shares can hang
                uint type = GetDriveType(drive);
                return type == DRIVE_FIXED || type == DRIVE_REMOVABLE || type == DRIVE_CDROM || type == DRIVE_RAMDISK;
            }
            catch { return false; }
        }

        static bool Accept(string path, bool videos, out FileKind kind)
        {
            kind = FileTypes.Classify(path);
            if (videos ? kind != FileKind.Video : kind != FileKind.Image && kind != FileKind.Incompatible) return false;
            try { return File.Exists(path); } catch { return false; }
        }

        /// <summary>The name as shown, or the same name with its extension hidden (Explorer's default).</summary>
        static string InFolder(string dir, string name, bool videos, out FileKind kind)
        {
            kind = FileKind.Other;
            string direct = Path.Combine(dir, name);
            if (Accept(direct, videos, out kind)) return direct;
            if (FileTypes.Classify(name) != FileKind.Other) return null;   // has a media extension but isn't there
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, name + ".*"))
                    if (string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase) && Accept(f, videos, out kind))
                        return f;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Folders the window is showing: the "Address: C:\..." breadcrumb that Open/Save dialogs (and
        /// apps that host the shell view) have, folder names Windows shows instead of paths, and a path in the title.
        /// </summary>
        static List<string> WindowFolders(IntPtr root)
        {
            var list = new List<string>();
            var candidates = new List<string>();
            NativeMethods.EnumChildWindows(root, (h, l) =>
            {
                if (NativeMethods.ClassName(h) == "ToolbarWindow32")
                {
                    string text = WindowText(h);
                    int colon = text.IndexOf(": ", StringComparison.Ordinal);   // "Address: ", "Adresse : " ...
                    if (colon > 0) candidates.Add(text.Substring(colon + 2).Trim());
                }
                return candidates.Count < 8;
            }, IntPtr.Zero);
            candidates.Add(WindowText(root));
            foreach (var c in candidates)
            {
                string dir = AbsolutePath(c);
                if (dir != null)
                {
                    // titles like "C:\Videos - App": take the longest prefix that is a real folder
                    while (dir.Length > 3 && !SafeDirExists(dir))
                    {
                        int cut = dir.LastIndexOfAny(new[] { ' ', '\\', '-', '|' });
                        if (cut < 3) break;
                        dir = dir.Substring(0, cut).TrimEnd(' ', '-', '|');
                    }
                    if (SafeDirExists(dir) && !list.Contains(dir)) list.Add(dir);
                }
                else
                {
                    string known = KnownFolder(c);
                    if (known != null && !list.Contains(known)) list.Add(known);
                }
            }
            return list;
        }

        static bool SafeDirExists(string d)
        {
            try { return Directory.Exists(d); } catch { return false; }
        }

        /// <summary>Breadcrumbs show "Downloads", "Videos" ... for these folders instead of a path.</summary>
        static string KnownFolder(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return null;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var map = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads"),
            };
            foreach (var p in map)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (string.Equals(Path.GetFileName(p), displayName.Trim(), StringComparison.OrdinalIgnoreCase) && SafeDirExists(p)) return p;
            }
            return null;
        }

        static string WindowText(IntPtr h)
        {
            var sb = new StringBuilder(1024);
            // WM_GETTEXT with a timeout, so a hung app can't stall the worker
            IntPtr result;
            if (SendMessageTimeout(h, WM_GETTEXT, new IntPtr(sb.Capacity), sb, SMTO_ABORTIFHUNG, 200, out result) == IntPtr.Zero) return "";
            return sb.ToString();
        }

        // ------------------------------------------------------------------ DPI

        /// <summary>Work in real screen pixels on these threads, so hook positions, element boxes and the screen copy all agree on mixed-DPI setups.</summary>
        static void UsePhysicalPixels()
        {
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); }   // per-monitor v2 (Windows 10 1703+)
            catch { }
        }

        // ------------------------------------------------------------------ native

        const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_QUIT = 0x0012, WM_GETTEXT = 0x000D;
        const int ROLE_SYSTEM_GRAPHIC = 0x28, ROLE_SYSTEM_LIST = 0x21, ROLE_SYSTEM_OUTLINE = 0x23, ROLE_SYSTEM_WINDOW = 0x09, ROLE_SYSTEM_CLIENT = 0x0A;
        const int ROLE_SYSTEM_LISTITEM = 0x22, ROLE_SYSTEM_OUTLINEITEM = 0x24, ROLE_SYSTEM_CELL = 0x1D, ROLE_SYSTEM_ROW = 0x1C;
        const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        const uint SMTO_ABORTIFHUNG = 2, DRIVE_REMOVABLE = 2, DRIVE_FIXED = 3, DRIVE_CDROM = 5, DRIVE_RAMDISK = 6;

        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")]
        static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(Point pt);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(Point pt, uint flags);
        [DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")]
        static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetDriveType(string root);
        [DllImport("oleacc.dll")]
        static extern int AccessibleObjectFromPoint(POINT pt, [MarshalAs(UnmanagedType.Interface)] out IAccessible acc, out object child);
        [DllImport("oleacc.dll")]
        static extern int AccessibleChildren(IAccessible container, int start, int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children, out int obtained);
    }

    /// <summary>
    /// A short local log of what pick-up saw (window types, what was found, why something was skipped), so a pick-up
    /// that doesn't work can be looked into: Help > Pick-up log. It never holds page text, addresses or key presses,
    /// only file paths that were picked up. It stays on this PC and is trimmed as it grows.
    /// </summary>
    internal static class PickupLog
    {
        static readonly object gate = new object();

        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StillsAndReels", "pickup-log.txt"); }
        }

        public static void Write(string line)
        {
            try
            {
                lock (gate)
                {
                    string path = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        string old = path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    /// <summary>Tells the main window when the clipboard changes (Copy image in a browser, Ctrl+C on a file).</summary>
    internal sealed class ClipboardWatcher : NativeWindow, IDisposable
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        public event EventHandler Changed;

        public ClipboardWatcher()
        {
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });   // message-only window
            AddClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                var h = Changed;
                if (h != null) h(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        /// <summary>True when this app put the data there (its own Copy), which must not be picked up again.</summary>
        public static bool FromThisApp()
        {
            IntPtr owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(owner, out pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(Handle);
                DestroyHandle();
            }
        }

        [DllImport("user32.dll")]
        static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }
}
