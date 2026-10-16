using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace StillsAndReels
{
    /// <summary>
    /// Watches every File Explorer window (already open or opened later) on a background STA thread.
    /// It follows the Explorer window the user is working in - the top-most one in z-order, so it keeps
    /// working after you click into this app - and, on Windows 11, the active tab inside it.
    /// Mouse clicks and arrow keys both change Explorer's selection, so both are picked up.
    /// </summary>
    internal sealed class ExplorerWatcher : IDisposable
    {
        static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");

        public event Action<string, FileKind> SelectionChanged;   // raised on the watcher thread
        public event Action<int> WindowCountChanged;               // raised on the watcher thread

        volatile bool running;
        volatile bool paused;
        volatile bool resetRequested;
        volatile bool wantVideos;
        Thread thread;
        string lastCandidate;
        int lastCount = -1;

        public bool Paused
        {
            get { return paused; }
            set { paused = value; if (!value) resetRequested = true; }
        }

        /// <summary>Videos mode follows video files; Pictures mode follows pictures (as before).</summary>
        public bool WantVideos
        {
            get { return wantVideos; }
            set { if (wantVideos != value) { wantVideos = value; resetRequested = true; } }
        }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "Explorer watcher" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Dispose()
        {
            running = false;
        }

        void Loop()
        {
            object shell = null, windows = null;
            int failures = 0;
            while (running)
            {
                try
                {
                    if (windows == null)
                    {
                        Type t = Type.GetTypeFromProgID("Shell.Application");
                        shell = Activator.CreateInstance(t);
                        windows = ((dynamic)shell).Windows();
                    }
                    Tick(windows);
                    failures = 0;
                }
                catch (Exception)
                {
                    // Explorer may be busy or may have restarted; reconnect after repeated failures.
                    if (++failures >= 10)
                    {
                        Release(windows); Release(shell);
                        windows = null; shell = null;
                        failures = 0;
                    }
                }
                Thread.Sleep(paused ? 400 : 150);
            }
            Release(windows); Release(shell);
        }

        sealed class Entry
        {
            public IntPtr Hwnd;
            public object Window;
        }

        void Tick(object windowsObj)
        {
            dynamic windows = windowsObj;
            var entries = new List<Entry>();
            var tops = new HashSet<IntPtr>();
            try
            {
                int n = windows.Count;
                for (int i = 0; i < n; i++)
                {
                    object w = null;
                    try { w = windows.Item(i); } catch { }
                    if (w == null) continue;
                    try
                    {
                        dynamic dw = w;
                        string full = dw.FullName;
                        if (full == null || !full.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            Release(w);
                            continue;
                        }
                        IntPtr h = new IntPtr(Convert.ToInt64(dw.HWND));
                        entries.Add(new Entry { Hwnd = h, Window = w });
                        tops.Add(h);
                    }
                    catch { Release(w); }
                }

                if (tops.Count != lastCount)
                {
                    lastCount = tops.Count;
                    var h = WindowCountChanged;
                    if (h != null) h(lastCount);
                }

                if (resetRequested) { resetRequested = false; lastCandidate = null; }
                if (paused || entries.Count == 0) return;

                IntPtr target = TopMostOf(tops);
                if (target == IntPtr.Zero) return;

                object chosen = null;
                var mine = entries.FindAll(e => e.Hwnd == target);
                if (mine.Count == 1)
                {
                    chosen = mine[0].Window;
                }
                else if (mine.Count > 1)
                {
                    // Windows 11 tabs: every tab is its own entry sharing one top-level window.
                    IntPtr activeTab = NativeMethods.FindWindowEx(target, IntPtr.Zero, "ShellTabWindowClass", null);
                    foreach (var e in mine)
                    {
                        if (activeTab != IntPtr.Zero && TabWindow(e.Window) == activeTab) { chosen = e.Window; break; }
                    }
                    if (chosen == null) chosen = mine[0].Window;
                }
                if (chosen == null) return;

                string path; FileKind kind;
                ReadSelection(chosen, wantVideos, out path, out kind);
                if (path == null)
                {
                    // Nothing selected or no image in the selection: keep showing the last image.
                    lastCandidate = null;
                    return;
                }
                string key = kind + "|" + path;
                if (!string.Equals(key, lastCandidate, StringComparison.OrdinalIgnoreCase))
                {
                    lastCandidate = key;
                    var h = SelectionChanged;
                    if (h != null) h(path, kind);
                }
            }
            finally
            {
                foreach (var e in entries) Release(e.Window);
            }
        }

        static IntPtr TopMostOf(HashSet<IntPtr> candidates)
        {
            if (candidates.Count == 0) return IntPtr.Zero;
            IntPtr fg = NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), NativeMethods.GA_ROOT);
            if (candidates.Contains(fg)) return fg;
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows((h, l) =>
            {
                if (candidates.Contains(h)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static IntPtr TabWindow(object window)
        {
            object browser = null;
            try
            {
                var sp = window as IComServiceProvider;
                if (sp == null) return IntPtr.Zero;
                Guid sid = SID_STopLevelBrowser, iid = IID_IShellBrowser;
                if (sp.QueryService(ref sid, ref iid, out browser) != 0 || browser == null) return IntPtr.Zero;
                var ole = browser as IOleWindow;
                IntPtr h;
                if (ole != null && ole.GetWindow(out h) == 0) return h;
            }
            catch { }
            finally { Release(browser); }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Picks the first showable image in the selection; if there is none, the first image in a
        /// format we can't show. Non-image files and folders are skipped.
        /// </summary>
        static void ReadSelection(object window, bool videos, out string path, out FileKind kind)
        {
            path = null; kind = FileKind.Other;
            object doc = null, items = null;
            string firstIncompatible = null;
            try
            {
                doc = ((dynamic)window).Document;
                if (doc == null) return;
                items = ((dynamic)doc).SelectedItems();
                if (items == null) return;
                dynamic sel = items;
                int count = sel.Count;
                int limit = Math.Min(count, 500);
                for (int i = 0; i < limit; i++)
                {
                    object item = null;
                    string p = null;
                    try
                    {
                        item = sel.Item(i);
                        if (item != null) p = ((dynamic)item).Path;
                    }
                    catch { }
                    finally { Release(item); }

                    FileKind k = FileTypes.Classify(p);
                    if (videos)
                    {
                        // Videos mode: only videos count; pictures and other files keep the current video
                        if (k != FileKind.Video || !IsFile(p)) continue;
                        path = p; kind = FileKind.Video;
                        return;
                    }
                    if (k == FileKind.Other || k == FileKind.Video) continue;
                    if (!IsFile(p)) continue;
                    if (k == FileKind.Image)
                    {
                        path = p; kind = FileKind.Image;
                        return;
                    }
                    if (firstIncompatible == null) firstIncompatible = p;
                }
                if (firstIncompatible != null)
                {
                    path = firstIncompatible; kind = FileKind.Incompatible;
                }
            }
            catch
            {
                // Special views (e.g. Home, Control Panel) have no file selection.
                path = null; kind = FileKind.Other;
            }
            finally
            {
                Release(items);
                Release(doc);
            }
        }

        static bool IsFile(string p)
        {
            try { return File.Exists(p); }
            catch { return false; }
        }

        static void Release(object o)
        {
            try
            {
                if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
            }
            catch { }
        }
    }
}
