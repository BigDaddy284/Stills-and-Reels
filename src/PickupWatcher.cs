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
    ///  - Pictures: on button-down the accessibility element under the cursor is checked. If it is a picture
    ///    (an &lt;img&gt; on a web page, a picture in another desktop app), the part of it that is on screen is
    ///    copied from the screen. It is handed over on button-up, and only if the mouse didn't move (so drags
    ///    and text selection are left alone). Nothing is downloaded: what you get is what was on screen.
    ///  - Files: on button-up, the clicked item in an Open/Save dialog or another app's file list is turned
    ///    into a path (full path in the item's text, or the item's name plus the folder in the window's
    ///    address bar or title). Only files that exist on a local drive are reported.
    /// File Explorer windows and the desktop are left to ExplorerWatcher, which reads their real selection.
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
            catch { }   // no hook (e.g. blocked by security software): pick-up from other apps just stays off
        }

        void RunHook()
        {
            UsePhysicalPixels();
            hookThreadId = GetCurrentThreadId();
            proc = HookCallback;
            hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) return;
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
                    catch { DropPending(); }
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
            if (Environment.TickCount - c.Time > MaxClickMs) return;   // too stale: the page may have changed
            IntPtr root;
            if (!IsForeignWindow(c.Pt, out root) || IsShellWindow(root)) return;
            string name;
            Rectangle r = FindPicture(c.Pt, out name);
            if (r.Width < MinPictureSize || r.Height < MinPictureSize) return;
            r = VisiblePart(r, root);
            if (r.Width < MinPictureSize / 2 || r.Height < MinPictureSize / 2) return;
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size, CopyPixelOperation.SourceCopy);
            pending = bmp;
            pendingName = PictureName(name, root);
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
                if (!isClick || !WantPictures || hash == lastHash) { bmp.Dispose(); }
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
            if (!IsForeignWindow(c.Pt, out root) || IsShellWindow(root)) return;
            Thread.Sleep(80);   // let the app update its selection first
            bool videos = WantVideos;
            FileKind kind;
            string path = FindFile(c.Pt, root, videos, out kind);
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

        static Rectangle VisiblePart(Rectangle r, IntPtr root)
        {
            RECT wr;
            if (GetWindowRect(root, out wr)) r.Intersect(Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom));
            r.Intersect(new Rectangle(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                                      GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN)));
            return r;
        }

        // ------------------------------------------------------------------ pictures

        /// <summary>Finds a picture element at the point: the hit element, its parents, or (for a link around a picture) its children.</summary>
        static Rectangle FindPicture(Point pt, out string name)
        {
            name = null;
            IAccessible acc; object child;
            if (AccessibleObjectFromPoint(new POINT { X = pt.X, Y = pt.Y }, out acc, out child) != 0 || acc == null) return Rectangle.Empty;
            try
            {
                Rectangle r;
                if (IsPictureAt(acc, child, pt, out r, out name)) return r;
                if (IsSelf(child))
                {
                    if (PictureChild(acc, pt, out r, out name)) return r;
                }
                else if (IsPictureAt(acc, 0, pt, out r, out name)) return r;
                IAccessible p = acc;
                for (int up = 0; up < 2; up++)
                {
                    object parent = null;
                    try { parent = p.accParent; } catch { }
                    p = parent as IAccessible;
                    if (p == null) break;
                    if (IsPictureAt(p, 0, pt, out r, out name)) return r;
                }
            }
            catch { }
            return Rectangle.Empty;
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

        static bool PictureChild(IAccessible acc, Point pt, out Rectangle r, out string name)
        {
            r = Rectangle.Empty; name = null;
            int count;
            try { count = acc.accChildCount; } catch { return false; }
            if (count <= 0 || count > 30) return false;
            var kids = new object[count];
            int got;
            if (AccessibleChildren(acc, 0, count, kids, out got) != 0) return false;
            for (int i = 0; i < got; i++)
            {
                var k = kids[i] as IAccessible;
                bool hit = k != null ? IsPictureAt(k, 0, pt, out r, out name) : kids[i] is int && IsPictureAt(acc, kids[i], pt, out r, out name);
                if (hit) return true;
            }
            return false;
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
