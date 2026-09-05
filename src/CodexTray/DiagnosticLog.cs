using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;

namespace CodexTray;

internal sealed class DiagnosticLog
{
    internal const int MaximumFileBytes = 256 * 1024;
    private readonly object gate = new object();
    private readonly string directory;
    private readonly Stopwatch uptime = Stopwatch.StartNew();
    private readonly string version = typeof(DiagnosticLog).Assembly.GetName().Version!.ToString();
    private readonly int processId;
    public static DiagnosticLog? Current { get; set; }
    public static string DirectoryPath => Path.Combine(Settings.DirectoryPath, "logs");

    public DiagnosticLog(string directory)
    {
        this.directory = directory;
        using var process = Process.GetCurrentProcess();
        processId = process.Id;
    }

    // Events must be app-defined labels, never response data or exception messages.
    public bool Write(string eventName, Exception? exception = null)
    {
        try
        {
            lock (gate)
            {
                var text = new StringBuilder();
                text.Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                    .Append(" version=").Append(version).Append(" pid=").Append(processId)
                    .Append(" uptime_s=").Append((long)uptime.Elapsed.TotalSeconds)
                    .Append(" event=").Append(eventName).AppendLine();
                int remaining = 8;
                if (exception != null) AppendException(text, exception, ref remaining);
                // Keep pathological exception chains and entries bounded as well as the files.
                if (text.Length > 16000) { text.Length = 16000; text.AppendLine(" [truncated]"); }
                var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "current.log");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumFileBytes)
                {
                    var previous = Path.Combine(directory, "previous.log");
                    var older = Path.Combine(directory, "older.log");
                    if (File.Exists(older)) File.Delete(older);
                    if (File.Exists(previous)) File.Move(previous, older);
                    File.Move(path, previous);
                }
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is ArgumentException || ex is NotSupportedException)
        {
            // Diagnostics are best-effort: a full or unavailable disk must not crash the tracker.
            return false;
        }
    }

    private static void AppendException(StringBuilder text, Exception exception, ref int remaining)
    {
        if (remaining-- <= 0) return;
        text.Append("  exception=").Append(exception.GetType().FullName)
            .Append(" hresult=0x").Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture)).AppendLine();
        if (exception is Win32Exception native) text.Append("    win32=").Append(native.NativeErrorCode).AppendLine();
        // Build frames from metadata: Exception.ToString()/StackTrace can expose messages and file paths.
        var frames = new StackTrace(exception, false).GetFrames();
        if (frames != null)
            for (int i = 0; i < Math.Min(frames.Length, 24); i++)
            {
                var method = frames[i].GetMethod();
                text.Append("    at ").Append(method?.DeclaringType?.FullName).Append('.').Append(method?.Name)
                    .Append(" il=").Append(frames[i].GetILOffset()).AppendLine();
            }
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (remaining <= 0) break;
                AppendException(text, inner, ref remaining);
            }
        }
        else if (exception.InnerException != null) AppendException(text, exception.InnerException, ref remaining);
    }
}
