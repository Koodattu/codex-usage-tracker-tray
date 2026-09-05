using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: InternalsVisibleTo("CodexTray.Tests")]

namespace CodexTray;

internal static class Program
{
    private static bool fatalError;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length < 2 || args[0] != "--monitored")
        {
            using var monitorInstance = new Mutex(true, @"Local\CodexTray.Monitor.1", out var firstMonitor);
            if (!firstMonitor) return;
            Environment.ExitCode = CrashMonitor.Run(Application.ExecutablePath,
                args.Contains("--background") ? "--monitored --background" : "--monitored",
                Path.Combine(DiagnosticLog.DirectoryPath, "monitor"));
            return;
        }
        // Only the monitor supplies an existing session; arbitrary command-line values cannot create one.
        if (!Guid.TryParseExact(args[args.Length - 1], "N", out var session)) return;
        if (!EventWaitHandle.TryOpenExisting(CrashMonitor.EventName(session.ToString("N"), "pulse"), out var pulse)) return;
        using var uiPulse = pulse;
        if (!EventWaitHandle.TryOpenExisting(CrashMonitor.EventName(session.ToString("N"), "completed"), out var completed)) return;
        using var shutdownCompleted = completed;
        using var instance = new Mutex(true, @"Local\CodexTray.1", out var first);
        if (!first) return;
        DiagnosticLog.Current = new DiagnosticLog(DiagnosticLog.DirectoryPath);
        DiagnosticLog.Current.Write("app.started");
        InstallExceptionHandlers();
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var heartbeat = new System.Windows.Forms.Timer { Interval = 5000 };
            heartbeat.Tick += (_, __) => uiPulse.Set();
            heartbeat.Start();
            using var application = new TrayApplication(args.Contains("--background"));
            Application.Run(application);
        }
        catch (Exception ex)
        {
            fatalError = true;
            DiagnosticLog.Current.Write("app.fatal_main", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            DiagnosticLog.Current.Write(fatalError ? "app.stopped_after_error" : "app.stopped");
            shutdownCompleted.Set();
            instance.ReleaseMutex();
        }
    }

    internal static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            fatalError = true;
            DiagnosticLog.Current?.Write("app.fatal_ui", e.Exception);
            Environment.ExitCode = 1;
            Application.Exit();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DiagnosticLog.Current?.Write(e.IsTerminating ? "app.fatal_background" : "app.unhandled_background", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            DiagnosticLog.Current?.Write("app.unobserved_task", e.Exception);
    }
}
