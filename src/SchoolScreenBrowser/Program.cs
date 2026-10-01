using System;
using System.Windows.Forms;

namespace SchoolScreenBrowser
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string pipeName = args.Length > 0 ? args[0] : string.Empty;
            if (string.IsNullOrWhiteSpace(pipeName)) return;
            Application.Run(new BrowserForm(pipeName));
        }
    }
}
