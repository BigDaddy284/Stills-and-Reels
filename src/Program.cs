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

            ImageCache.Initialize();
            try
            {
                Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
            }
            finally
            {
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
