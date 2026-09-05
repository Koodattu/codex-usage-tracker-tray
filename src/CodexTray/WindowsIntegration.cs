using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CodexTray;

internal static class WindowsIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static string? FindCodex(string? configured)
    {
        if (!string.IsNullOrEmpty(configured)) return File.Exists(configured) ? configured : null;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var managed = Path.Combine(local, "OpenAI", "Codex", "bin");
        if (Directory.Exists(managed))
        {
            try
            {
                var recent = Directory.GetDirectories(managed).Select(d => Path.Combine(d, "codex.exe"))
                    .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (recent != null) return recent;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* Fall back to a standalone CLI. */ }
        }
        var candidates = new[]
        {
            Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
            Path.Combine(local, "Programs", "Codex", "resources", "codex.exe"),
            Path.Combine(managed, "codex.exe")
        };
        foreach (var path in candidates) if (File.Exists(path)) return path;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var path = Path.Combine(directory.Trim('"'), "codex.exe");
                if (Path.IsPathRooted(path) && File.Exists(path)) return path;
            }
            catch (ArgumentException) { /* Ignore malformed PATH entries. */ }
        }
        // The npm launcher is a .cmd file; execute the native binary directly.
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai");
        foreach (var package in new[] { "codex", "codex-win32-x64", "codex-win32-arm64" })
        foreach (var architecture in new[] { "x86_64-pc-windows-msvc", "aarch64-pc-windows-msvc" })
        {
            var path = Path.Combine(npm, package, "vendor", architecture, "codex", "codex.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("CodexTray") is string;
    }

    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("CodexTray", "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --background");
        else key.DeleteValue("CodexTray", false);
    }

    public static void OpenDesktop(string? configured)
    {
        if (!string.IsNullOrEmpty(configured))
        {
            if (!File.Exists(configured)) throw new UsageException("The selected Codex desktop app was moved. Choose it again from the tray menu.");
            Open(configured!);
            return;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var path in new[]
        {
            Path.Combine(local, "Programs", "OpenAI", "Codex", "Codex.exe"),
            Path.Combine(local, "Programs", "Codex", "Codex.exe")
        })
        {
            if (!File.Exists(path)) continue;
            Open(path);
            return;
        }
        // Resolve Store activation through the user's registered applications.
        if (OpenStoreDesktop()) return;
        using var protocol = Registry.ClassesRoot.OpenSubKey(@"codex\shell\open\command");
        if (protocol != null) { Open("codex://"); return; }
        throw new UsageException("Codex desktop was not found. Use ‘Choose Codex desktop app…’ in the tray menu.");
    }

    public static void OpenStore()
    {
        try { Open("ms-windows-store://pdp/?ProductId=9NT1R1C2HH7J"); }
        catch (Win32Exception) { Open("https://apps.microsoft.com/detail/9nt1r1c2hh7j"); }
    }
    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    internal static bool OpenStoreDesktop(bool launch = true)
    {
        object? shell = null, folder = null, items = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return false;
            shell = Activator.CreateInstance(type);
            folder = type.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { "shell:AppsFolder" });
            if (folder == null) return false;
            items = folder.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, folder, null);
            if (!(items is System.Collections.IEnumerable collection)) return false;
            foreach (var item in collection)
            {
                try
                {
                    var path = item.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                    if (path == null || !path.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) || path.Contains("\"") || path.IndexOf('!') < 0) continue;
                    if (launch) Process.Start(new ProcessStartInfo("explorer.exe", "\"shell:AppsFolder\\" + path + "\"") { UseShellExecute = true });
                    return true;
                }
                finally { if (Marshal.IsComObject(item)) Marshal.ReleaseComObject(item); }
            }
            return false;
        }
        finally
        {
            foreach (var value in new[] { items, folder, shell })
                if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }
}

// A private job prevents orphaned app-server processes if the tracker exits mid-read.
internal sealed class ProcessJob : IDisposable
{
    private IntPtr handle;
    public ProcessJob(Process process)
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (!process.HasExited) process.Kill();
            throw new Win32Exception(error);
        }
        var information = new ExtendedLimitInformation();
        information.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref information, Marshal.SizeOf(information)) || !AssignProcessToJobObject(handle, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            if (!process.HasExited) process.Kill();
            throw new Win32Exception(error);
        }
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
