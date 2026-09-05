using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPrivateDiagnostic() => throw new InvalidOperationException("PRIVATE Bearer C:\\Users\\PRIVATE\\auth.json");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonDiagnosticTask() => _ = Task.FromException(new InvalidOperationException("PRIVATE"));

    private static int DiagnosticChild(string mode, string directory)
    {
        DiagnosticLog.Current = new DiagnosticLog(directory);
        CodexTray.Program.InstallExceptionHandlers();
        using var context = new ApplicationContext();
        using var timer = new Timer { Interval = 50 };
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
