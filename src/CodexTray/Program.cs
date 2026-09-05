using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

[assembly: InternalsVisibleTo("CodexTray.Tests")]

namespace CodexTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var instance = new Mutex(true, @"Local\CodexTray.1", out var first);
        if (!first) return;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, __) =>
        {
            MessageBox.Show("Codex Tray stopped unexpectedly. Restart the app to resume usage tracking.", "Codex Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
        };
        using var application = new TrayApplication(args.Contains("--background"));
        Application.Run(application);
        instance.ReleaseMutex();
    }
}
