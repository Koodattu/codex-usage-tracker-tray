using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

namespace CodexTray;

// Runs in a separate instance of the same EXE so abrupt UI-process exits remain observable.
internal static class CrashMonitor
{
    internal static string EventName(string session, string kind) => @"Local\CodexTray.Diagnostics." + session + "." + kind;

    internal static int Run(string executable, string arguments, string directory, int sampleMilliseconds = 30000)
    {
        var log = new DiagnosticLog(directory);
        var marker = Path.Combine(directory, "session.pending");
        var session = Guid.NewGuid().ToString("N");
        using var pulse = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(session, "pulse"));
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(session, "completed"));
        log.Write("monitor.started");
        UpdateMarker(marker, true, log);
        try
        {
            using var child = Process.Start(new ProcessStartInfo(executable, arguments + " " + session)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            if (child == null) throw new InvalidOperationException();
            log.Write("monitor.attached target_pid=" + child.Id.ToString(CultureInfo.InvariantCulture));
            // Retain the process handle: querying by PID after exit risks PID reuse and losing the exit code.
            _ = child.Handle;
            int missedPulses = 0;
            while (!child.WaitForExit(sampleMilliseconds))
            {
                missedPulses = pulse.WaitOne(0) ? 0 : missedPulses + 1;
                Sample(child, missedPulses, log);
            }
            int code = child.ExitCode;
            log.Write("monitor.exited target_pid=" + child.Id.ToString(CultureInfo.InvariantCulture)
                + " exit_code=0x" + code.ToString("X8", CultureInfo.InvariantCulture)
                + " shutdown_completed=" + (completed.WaitOne(0) ? "true" : "false"));
            UpdateMarker(marker, false, log);
            return code;
        }
        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
        {
            log.Write("monitor.failed", ex);
            return 1;
        }
    }

    private static void Sample(Process process, int missedPulses, DiagnosticLog log)
    {
        try
        {
            process.Refresh();
            log.Write(string.Format(CultureInfo.InvariantCulture,
                "monitor.health target_pid={0} missed_ui_pulses={1} private_bytes={2} handles={3} gdi_objects={4} user_objects={5}",
                process.Id, missedPulses, process.PrivateMemorySize64, process.HandleCount,
                GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1)));
        }
        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
        {
            log.Write("monitor.sample_unavailable", ex);
        }
    }

    private static void UpdateMarker(string path, bool starting, DiagnosticLog log)
    {
        try
        {
            if (starting)
            {
                if (File.Exists(path)) log.Write("monitor.previous_session_incomplete");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                stream.WriteByte(1);
                stream.Flush(true);
            }
            else File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
        {
            log.Write("monitor.marker_unavailable", ex);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
