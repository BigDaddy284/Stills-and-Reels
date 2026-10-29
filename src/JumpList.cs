using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace StillsAndReels
{
    /// <summary>
    /// Shortcuts that start straight into a snip tool: the taskbar jump list ("Pic Snip", "Vid Snip" when the app
    /// is pinned and right-clicked) runs the exe with --picsnip or --vidsnip. If the app is already open the request
    /// is passed to it, so the snip window opens there instead of in a second copy of the app.
    /// </summary>
    internal static class Shortcuts
    {
        public const string PicSnipArg = "--picsnip", VidSnipArg = "--vidsnip";

        /// <summary>"picsnip", "vidsnip" or null for an argument from the command line.</summary>
        public static string SnipRequest(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return null;
            string a = arg.Trim().TrimStart('-', '/').ToLowerInvariant();
            return a == "picsnip" ? "picsnip" : a == "vidsnip" ? "vidsnip" : null;
        }

        static string EventName(string request)
        {
            // Local\ is per logon session, so another user's copy of the app is never reached
            return @"Local\StillsAndReels." + request;
        }

        /// <summary>Asks a copy of the app that is already open to show a snip window. True if one took the request.</summary>
        public static bool PassToRunningCopy(string request)
        {
            try
            {
                EventWaitHandle ev;
                if (!EventWaitHandle.TryOpenExisting(EventName(request), out ev)) return false;
                using (ev)
                {
                    // we were started by the user's click, so we may hand the right to come to the front to the open copy
                    if (FFmpeg.IsWindows) try { AllowSetForegroundWindow(-1); } catch { }
                    ev.Set();
                }
                return true;
            }
            catch { return false; }
        }

        static EventWaitHandle picEvent, vidEvent;
        static RegisteredWaitHandle picWait, vidWait;

        /// <summary>Listens for snip requests from shortcuts started while the app is open.</summary>
        public static void Listen(Action<string> onRequest)
        {
            try
            {
                picEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName("picsnip"));
                vidEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName("vidsnip"));
                picWait = ThreadPool.RegisterWaitForSingleObject(picEvent, (s, t) => onRequest("picsnip"), null, -1, false);
                vidWait = ThreadPool.RegisterWaitForSingleObject(vidEvent, (s, t) => onRequest("vidsnip"), null, -1, false);
            }
            catch { StopListening(); }   // named events are a Windows feature; without them a shortcut opens a new copy
        }

        public static void StopListening()
        {
            if (picWait != null) { picWait.Unregister(null); picWait = null; }
            if (vidWait != null) { vidWait.Unregister(null); vidWait = null; }
            if (picEvent != null) { picEvent.Dispose(); picEvent = null; }
            if (vidEvent != null) { vidEvent.Dispose(); vidEvent = null; }
        }

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        /// <summary>
        /// Writes the taskbar jump list: two tasks that run this exe with --picsnip and --vidsnip. Windows keeps the list
        /// for the exe's path, so it is rewritten on every start (the exe may have been moved). No files are written.
        /// </summary>
        public static void WriteJumpList()
        {
            if (!FFmpeg.IsWindows) return;
            ICustomDestinationList list = null;
            try
            {
                string exe = Application.ExecutablePath;
                list = (ICustomDestinationList)new CDestinationList();
                uint slots;
                Guid iidArray = typeof(IObjectArray).GUID;
                object removed;
                list.BeginList(out slots, ref iidArray, out removed);
                var tasks = (IObjectCollection)new CEnumerableObjectCollection();
                tasks.AddObject(Link(exe, PicSnipArg, "Pic Snip", "Snip a picture of part of your screen"));
                tasks.AddObject(Link(exe, VidSnipArg, "Vid Snip", "Record your screen"));
                list.AddUserTasks((IObjectArray)tasks);
                list.CommitList();
            }
            catch
            {
                try { if (list != null) list.AbortList(); } catch { }
            }
            finally
            {
                if (list != null) Marshal.ReleaseComObject(list);
            }
        }

        static IShellLinkW Link(string exe, string args, string title, string tip)
        {
            var link = (IShellLinkW)new CShellLink();
            link.SetPath(exe);
            link.SetArguments(args);
            link.SetIconLocation(exe, 0);
            link.SetDescription(tip);
            try { link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(exe)); } catch { }
            var store = (IPropertyStore)link;
            var key = new PropertyKey { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 2 };   // PKEY_Title
            var value = new PropVariant { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(title) };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally { Marshal.FreeCoTaskMem(value.p); }
            return link;
        }

        // ---- shell COM interfaces (declared here so no extra library is needed)

        [ComImport, Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6"), ClassInterface(ClassInterfaceType.None)]
        class CDestinationList { }

        [ComImport, Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a"), ClassInterface(ClassInterfaceType.None)]
        class CEnumerableObjectCollection { }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046"), ClassInterface(ClassInterfaceType.None)]
        class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
        interface ICustomDestinationList
        {
            void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
            void BeginList(out uint minSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
            void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray items);
            void AppendKnownCategory(int category);
            void AddUserTasks(IObjectArray tasks);
            void CommitList();
            void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
            void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
            void AbortList();
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9")]
        interface IObjectArray
        {
            void GetCount(out uint count);
            void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5632b1a4-e38a-400a-928a-d4cd63230295")]
        interface IObjectCollection
        {
            // IObjectArray
            void GetCount(out uint count);
            void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object obj);
            // IObjectCollection
            void AddObject([MarshalAs(UnmanagedType.Interface)] object obj);
            void AddFromArray(IObjectArray source);
            void RemoveObjectAt(uint index);
            void Clear();
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr findData, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int max, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
        }

        // only the VT_LPWSTR case is used; sized like the real PROPVARIANT (24 bytes on 64-bit)
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr p;
        }
    }
}
