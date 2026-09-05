using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexTray;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(18, 21, 25);
    public static readonly Color Card = Color.FromArgb(28, 32, 38);
    public static readonly Color Text = Color.FromArgb(238, 242, 245);
    public static readonly Color Muted = Color.FromArgb(148, 157, 169);
    public static readonly Color Line = Color.FromArgb(48, 55, 65);
    public static readonly Color Mint = Color.FromArgb(98, 220, 173);
    public static readonly Color Violet = Color.FromArgb(171, 160, 249);
    public static readonly Color Amber = Color.FromArgb(245, 188, 85);
    public static readonly Color Red = Color.FromArgb(246, 107, 115);
    public static Color Quota(double remaining) => remaining <= 20 ? Red : remaining <= 50 ? Amber : Mint;

    public static void Label(Graphics graphics, string text, float size, Color color, RectangleF bounds, FontStyle style = FontStyle.Regular, StringAlignment alignment = StringAlignment.Near)
    {
        using var font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    public static void RoundRect(Graphics graphics, Color color, RectangleF rectangle, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    public static string Countdown(DateTimeOffset time, DateTimeOffset now)
    {
        var span = time - now;
        if (span <= TimeSpan.Zero) return "now";
        if (span.TotalMinutes < 1) return "<1m";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalMinutes}m";
    }
}

internal static class TrayIconRenderer
{
    public static Icon Create(QuotaWindow? quota, string mode, bool stale, int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.ScaleTransform(size / 32f, size / 32f);
        var color = quota == null || stale ? Theme.Muted : Theme.Quota(quota.Remaining);
        if (mode == "rings")
        {
            using var track = new Pen(Color.FromArgb(105, 115, 128), 4);
            graphics.DrawEllipse(track, 4, 4, 24, 24);
            if (quota != null)
            {
                using var progress = new Pen(color, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (quota.Remaining >= 100) graphics.DrawEllipse(progress, 4, 4, 24, 24);
                else if (quota.Remaining > 0) graphics.DrawArc(progress, 4, 4, 24, 24, -90, (float)(quota.Remaining * 3.6));
            }
            else Theme.Label(graphics, "?", 18, color, new RectangleF(0, 4, 32, 24), FontStyle.Bold, StringAlignment.Center);
        }
        else
        {
            var text = quota == null ? "–" : Math.Floor(quota.Remaining).ToString("0");
            using var path = NumberPath(text);
            using var outline = new Pen(Color.FromArgb(210, 18, 21, 25), 1.4f) { LineJoin = LineJoin.Round };
            graphics.DrawPath(outline, path);
            using var brush = new SolidBrush(color);
            graphics.FillPath(brush, path);
        }
        if (stale)
        {
            using var dot = new SolidBrush(Theme.Amber);
            graphics.FillEllipse(dot, 24, 24, 7, 7);
        }
        var native = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(native);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(native); }
    }
    internal static GraphicsPath NumberPath(string text)
    {
        var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        path.AddString(text, family, (int)FontStyle.Bold, 32, PointF.Empty, format);
        var bounds = path.GetBounds();
        var scale = Math.Min(29 / bounds.Width, 27 / bounds.Height);
        using var transform = new Matrix(scale, 0, 0, scale,
            (32 - bounds.Width * scale) / 2 - bounds.X * scale,
            (32 - bounds.Height * scale) / 2 - bounds.Y * scale);
        path.Transform(transform);
        return path;
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
