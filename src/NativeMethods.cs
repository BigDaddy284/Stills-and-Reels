using System;
using System.Runtime.InteropServices;
using System.Text;

namespace StillsAndReels
{
    internal static class NativeMethods
    {
        public const uint GA_ROOT = 2;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

        public static string ClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    // IServiceProvider (COM flavour) - used to reach the IShellBrowser of an Explorer tab.
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    // IShellBrowser derives from IOleWindow; GetWindow returns the tab's ShellTabWindowClass window.
    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleWindow
    {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);

        [PreserveSig]
        int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
    }

    // IShellWindows - only FindWindowSW is called (to reach the desktop's view); the others hold vtable slots.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(ref object pvarloc, ref object pvarlocRoot, int swClass, out int phwnd, int swfwOptions);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
        [PreserveSig] int InsertMenusSB();
        [PreserveSig] int SetMenuSB();
        [PreserveSig] int RemoveMenusSB();
        [PreserveSig] int SetStatusTextSB();
        [PreserveSig] int EnableModelessSB();
        [PreserveSig] int TranslateAcceleratorSB();
        [PreserveSig] int BrowseObject();
        [PreserveSig] int GetViewStateStream();
        [PreserveSig] int GetControlWindow();
        [PreserveSig] int SendControlMsg();
        [PreserveSig] int QueryActiveShellView(out IShellView ppshv);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
        [PreserveSig] int TranslateAccelerator();
        [PreserveSig] int EnableModeless();
        [PreserveSig] int UIActivate();
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow();
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo();
        [PreserveSig] int AddPropertySheetPages();
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem();
        [PreserveSig] int GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }
}
