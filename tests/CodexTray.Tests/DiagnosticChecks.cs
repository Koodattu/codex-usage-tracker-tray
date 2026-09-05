using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodexTray;

internal static partial class Program
{
    private static void DiagnosticChecks()
    {
        Run("Diagnostics retain frames and error codes without exception payloads", () => WithHistoryDirectory(directory =>
        {
            var log = new DiagnosticLog(directory);
            try { ThrowPrivateDiagnostic(); }
            catch (Exception ex) { Check(log.Write("test.failure", new AggregateException("private aggregate", ex))); }
            var text = File.ReadAllText(Path.Combine(directory, "current.log"));
            Check(text.Contains("event=test.failure") && text.Contains("version=") && text.Contains("pid=") && text.Contains("uptime_s="));
            Check(text.Contains("System.InvalidOperationException") && text.Contains("hresult=0x") && text.Contains("ThrowPrivateDiagnostic"));
            Check(!text.Contains("PRIVATE") && !text.Contains("private aggregate") && !text.Contains("C:\\") && !text.Contains("Bearer"));
        }));
        Run("Diagnostics rotate three bounded files and preserve restart continuity", () => WithHistoryDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "unrelated");
            var log = new DiagnosticLog(directory);
            for (int i = 0; i < 120; i++) Check(log.Write("test." + new string('x', 12000)));
            Check(new DiagnosticLog(directory).Write("test.restarted"));
            var files = Directory.GetFiles(directory, "*.log");
            Equal(3, files.Length);
            Check(files.All(path => new FileInfo(path).Length <= DiagnosticLog.MaximumFileBytes));
            Check(File.ReadAllText(Path.Combine(directory, "current.log")).Contains("test.restarted"));
            Equal("unrelated", File.ReadAllText(Path.Combine(directory, "keep.txt")));
        }));
        Run("Diagnostics serialize concurrent writes and tolerate unavailable storage", () => WithHistoryDirectory(directory =>
        {
            var log = new DiagnosticLog(directory);
            Parallel.For(0, 40, i => Check(log.Write("test.concurrent")));
            Equal(40, File.ReadAllLines(Path.Combine(directory, "current.log")).Length);
            using (var locked = new FileStream(Path.Combine(directory, "current.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!log.Write("test.locked"));
            Check(log.Write("test.recovered"));
            var blocker = Path.Combine(directory, "blocked"); File.WriteAllText(blocker, "file");
            Check(!new DiagnosticLog(blocker).Write("test.unwritable"));
        }));
        Run("Production exception hooks record UI and unobserved task failures", () => WithHistoryDirectory(directory =>
        {
            foreach (var mode in new[] { "ui", "task" })
            {
                var path = Path.Combine(directory, mode);
                using var child = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--diagnostic-child " + mode + " \"" + path + "\"")
                { UseShellExecute = false, CreateNoWindow = true });
                try
                {
                    Check(child.WaitForExit(10000)); Equal(0, child.ExitCode);
                    var text = File.ReadAllText(Path.Combine(path, "current.log"));
                    Check(text.Contains(mode == "ui" ? "event=app.fatal_ui" : "event=app.unobserved_task"));
                    Check(text.Contains("System.InvalidOperationException") && !text.Contains("PRIVATE"));
                }
                finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
            }
        }));
        Run("Repeated menu DPI updates keep item fonts usable", () =>
        {
            using var menu = new DpiMenu();
            menu.Items.Add("Show usage");
            using var bitmap = new Bitmap(300, 100);
            using var graphics = Graphics.FromImage(bitmap);
            foreach (var dpi in new uint[] { 144, 144, 96, 96, 192, 192, 144 })
            {
                menu.SetDpi(dpi);
                menu.PerformLayout();
                Check(menu.Items[0].Font.GetHeight(graphics) > 0);
                Check(menu.GetPreferredSize(Size.Empty).Width > 0);
            }
        });
        Run("Separate monitor records clean and forced exits including a zero exit code", () => WithHistoryDirectory(directory =>
        {
            foreach (var mode in new[] { "clean", "terminate", "abrupt-zero" })
            {
                var path = Path.Combine(directory, mode);
                var run = Task.Run(() => CrashMonitor.Run(Application.ExecutablePath, "--monitor-probe " + mode, path, 50));
                Check(run.Wait(10000));
                Equal(mode == "terminate" ? unchecked((int)0xC0000005) : 0, run.Result);
                var text = File.ReadAllText(Path.Combine(path, "current.log"));
                Check(text.Contains("monitor.attached") && text.Contains("monitor.health") && text.Contains("monitor.exited"));
                Check(text.Contains("shutdown_completed=" + (mode == "clean" ? "true" : "false")));
                Check(text.Contains(mode == "terminate" ? "exit_code=0xC0000005" : "exit_code=0x00000000"));
                Check(!File.Exists(Path.Combine(path, "session.pending")));
                Check(!text.Contains("PRIVATE") && !text.Contains("Bearer"));
            }
        }));
        Run("Monitor retains evidence of an incomplete session and missing UI pulses", () => WithHistoryDirectory(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "session.pending"), "PRIVATE");
            var run = Task.Run(() => CrashMonitor.Run(Application.ExecutablePath, "--monitor-probe stalled", directory, 50));
            Check(run.Wait(10000)); Equal(0, run.Result);
            var text = File.ReadAllText(Path.Combine(directory, "current.log"));
            Check(text.Contains("monitor.previous_session_incomplete") && text.Contains("missed_ui_pulses=2"));
            Check(!text.Contains("PRIVATE") && !File.Exists(Path.Combine(directory, "session.pending")));
        }));
        Run("Monitor tolerates unavailable log storage and records launch failures", () => WithHistoryDirectory(directory =>
        {
            var blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "file");
            var run = Task.Run(() => CrashMonitor.Run(Application.ExecutablePath, "--monitor-probe clean", blocked, 50));
            Check(run.Wait(10000)); Equal(0, run.Result);
            Equal(1, CrashMonitor.Run(Path.Combine(directory, "missing.exe"), "", directory));
            Check(File.ReadAllText(Path.Combine(directory, "current.log")).Contains("monitor.failed"));
        }));
    }

    private static int MonitorProbe(string mode, string session)
    {
        using var pulse = EventWaitHandle.OpenExisting(CrashMonitor.EventName(session, "pulse"));
        using var completed = EventWaitHandle.OpenExisting(CrashMonitor.EventName(session, "completed"));
        for (int i = 0; i < 30; i++)
        {
            if (mode != "stalled") pulse.Set();
            Thread.Sleep(10);
        }
        if (mode == "terminate")
        {
            using var process = Process.GetCurrentProcess();
            TerminateProcess(process.Handle, 0xC0000005);
            return 99;
        }
        if (mode == "abrupt-zero") Environment.Exit(0);
        completed.Set();
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPrivateDiagnostic() => throw new InvalidOperationException("PRIVATE Bearer C:\\Users\\PRIVATE\\auth.json");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonDiagnosticTask() => _ = Task.FromException(new InvalidOperationException("PRIVATE"));

    private static int DiagnosticChild(string mode, string directory)
    {
        DiagnosticLog.Current = new DiagnosticLog(directory);
        CodexTray.Program.InstallExceptionHandlers();
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        int ticks = 0;
        timer.Tick += (_, __) =>
        {
            if (mode == "ui") { timer.Stop(); ThrowPrivateDiagnostic(); }
            if (ticks++ == 0) AbandonDiagnosticTask();
            GC.Collect(); GC.WaitForPendingFinalizers();
            if (File.Exists(Path.Combine(directory, "current.log")) || ticks > 20) context.ExitThread();
        };
        timer.Start();
        Application.Run(context);
        if (mode == "ui") Check(Environment.ExitCode == 1);
        return 0;
    }
}
