using System;
using System.Windows.Forms;

namespace StillsAndReels
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);

            // --picsnip / --vidsnip come from the taskbar jump list; if the app is already open it shows the snip window
            string snip = args.Length > 0 ? Shortcuts.SnipRequest(args[0]) : null;
            if (snip != null && Shortcuts.PassToRunningCopy(snip)) return;

            ImageCache.Initialize();
            try
            {
                var main = new MainForm(snip == null && args.Length > 0 ? args[0] : null, snip);
                Shortcuts.Listen(request => main.OpenSnipFromShortcut(request));
                Application.Run(main);
            }
            finally
            {
                Shortcuts.StopListening();
                ImageCache.Shutdown();
            }
        }

        static void ShowError(Exception ex)
        {
            try { Console.Error.WriteLine(ex); } catch { }
            try
            {
                MessageBox.Show("Something went wrong:\n\n" + (ex != null ? ex.Message : "Unknown error"),
                    "Stills & Reels", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
