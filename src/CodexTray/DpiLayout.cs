using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexTray;

internal static class DpiLayout
{
    public static uint WindowDpi(IntPtr window) => Math.Max(96u, GetDpiForWindow(window));
    public static uint TrayDpi => WindowDpi(FindWindow("Shell_TrayWnd", null));
    public static int TrayIconSize => Math.Max(16, GetSystemMetricsForDpi(49, TrayDpi));

    public static Size Fit(Size logical, uint dpi, Size available)
    {
        var scale = Math.Min(dpi / 96f, Math.Min((available.Width - 16) / (float)logical.Width, (available.Height - 16) / (float)logical.Height));
        return new Size((int)Math.Round(logical.Width * scale), (int)Math.Round(logical.Height * scale));
    }

    public static void Place(Form form, Point anchor, Size logical, bool above)
    {
        var area = Screen.FromPoint(anchor).WorkingArea;
        // Move the hidden native window first, then ask Windows for that monitor's DPI.
        form.Location = anchor;
        form.ClientSize = Fit(logical, WindowDpi(form.Handle), area.Size);
        form.Location = new Point(Math.Max(area.Left + 8, Math.Min(anchor.X - form.Width / 2, area.Right - form.Width - 8)),
            Math.Max(area.Top + 8, Math.Min(above ? anchor.Y - form.Height - 8 : anchor.Y - form.Height / 2, area.Bottom - form.Height - 8)));
    }

    public static bool HandleDpiChange(Form form, ref Message message, Size logical)
    {
        if (message.Msg != 0x02E0) return false; // WM_DPICHANGED
        var proposed = Marshal.PtrToStructure<NativeRectangle>(message.LParam);
        var area = Screen.FromPoint(new Point(proposed.Left, proposed.Top)).WorkingArea;
        form.ClientSize = Fit(logical, (uint)(message.WParam.ToInt64() & 0xffff), area.Size);
        form.Location = new Point(Math.Max(area.Left + 8, Math.Min(proposed.Left, area.Right - form.Width - 8)),
            Math.Max(area.Top + 8, Math.Min(proposed.Top, area.Bottom - form.Height - 8)));
        message.Result = IntPtr.Zero;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
}

internal sealed class DpiMenu : ContextMenuStrip
{
    private Font? scaledFont;
    public void SetDpi(uint dpi)
    {
        var previous = scaledFont;
        scaledFont = new Font("Segoe UI", 13 * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        Font = scaledFont;
        previous?.Dispose();
        ImageScalingSize = new Size((int)(16 * dpi / 96), (int)(16 * dpi / 96));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) scaledFont?.Dispose();
        base.Dispose(disposing);
    }
}
