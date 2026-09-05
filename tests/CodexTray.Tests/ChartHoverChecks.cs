using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using CodexTray;

internal static partial class Program
{
    private static void ChartHoverChecks()
    {
        Run("Chart hover selects recorded samples at every range and display scale", () =>
        {
            foreach (var days in new[] { 1, 7, 30 })
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                var history = new UsageHistory();
                var first = new HistoryPoint { Time = Now.AddDays(-days * .75), Weekly = 80 };
                var second = new HistoryPoint { Time = Now.AddDays(-days * .25), Weekly = 56 };
                history.Points.Add(first); history.Points.Add(second);
                using var form = new PopupForm(history, days);
                form.ClientSize = new Size((int)(440 * scale), (int)(636 * scale));
                form.UpdateUsage(new UsageSnapshot { Weekly = new QuotaWindow() }, "Sample", false, false, Now, false);
                Equal(first, form.ChartPointAt(new Point((int)((49 + 365 * .25) * scale), (int)(340 * scale)), Now));
                Equal(second, form.ChartPointAt(new Point((int)((49 + 365 * .75) * scale), (int)(340 * scale)), Now));
                Check(form.ChartPointAt(new Point((int)(230 * scale), (int)(340 * scale)), Now) == null);
                Check(form.ChartPointAt(new Point((int)(140 * scale), (int)(260 * scale)), Now) == null);
                Check(form.ChartPointAt(new Point((int)(140 * scale), (int)(400 * scale)), Now) == null);
            }
        });
        Run("Chart hover ignores unavailable series, future readings, and old history", () =>
        {
            var history = new UsageHistory();
            using var form = new PopupForm(history);
            var snapshot = new UsageSnapshot { Weekly = new QuotaWindow() };
            form.UpdateUsage(snapshot, "Sample", false, false, Now, false);
            var location = new Point(230, 340);
            Check(form.ChartPointAt(location, Now) == null);
            history.Points.Add(new HistoryPoint { Time = Now.AddHours(-12), FiveHour = 60 });
            Check(form.ChartPointAt(location, Now) == null);
            snapshot.FiveHour = new QuotaWindow();
            Equal(history.Points[0], form.ChartPointAt(location, Now));
            history.Points.Clear();
            history.Points.Add(new HistoryPoint { Time = Now.AddSeconds(1), Weekly = 60 });
            history.Points.Add(new HistoryPoint { Time = Now.AddDays(-1).AddSeconds(-1), Weekly = 60 });
            Check(form.ChartPointAt(new Point(413, 340), Now) == null);
            Check(form.ChartPointAt(new Point(49, 340), Now) == null);
        });
        Run("Chart hover renders both series and clears on leave, range, hide, and pool changes", () =>
        {
            var history = new UsageHistory();
            var now = DateTimeOffset.UtcNow;
            var point = new HistoryPoint { Time = now.AddHours(-12), FiveHour = 72, Weekly = 56 };
            history.Points.Add(point);
            var snapshot = new UsageSnapshot { ReadAt = now, FiveHour = new QuotaWindow { Remaining = 72 }, Weekly = new QuotaWindow { Remaining = 56 } };
            using var form = new PopupForm(history) { Location = new Point(-20000, -20000), KeepOpen = true };
            form.Show(); Application.DoEvents();
            form.UpdateUsage(snapshot, "Sample data", false, false, now.AddMinutes(5), true);
            bool refreshed = false;
            form.RefreshRequested += (_, __) => refreshed = true;
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                form.ClientSize = new Size((int)(440 * scale), (int)(636 * scale));
                MoveChartMouse(form, (int)(231 * scale), (int)(340 * scale));
                Equal(point, form.HoveredChartPoint);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, form.ClientRectangle);
                bitmap.Save(Path.Combine(".artifacts", "preview-hover-" + (int)(scale * 100) + ".png"));
                form.UpdateUsage(snapshot, "Sample data", false, false, now.AddMinutes(5), true);
                Equal(point, form.HoveredChartPoint);
                typeof(PopupForm).GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { EventArgs.Empty });
                Check(form.HoveredChartPoint == null);
            }
            form.ClientSize = new Size(660, 954);
            snapshot.FiveHour = null;
            foreach (var hours in new[] { 23.8, .05 })
            {
                point.Time = now.AddHours(-hours);
                MoveChartMouse(form, (int)((49 + 365 * (1 - hours / 24)) * 1.5), 510);
                Equal(point, form.HoveredChartPoint);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, form.ClientRectangle);
                bitmap.Save(Path.Combine(".artifacts", hours > 12 ? "preview-hover-left.png" : "preview-hover-right.png"));
            }
            form.Controls.OfType<Button>().Single(b => b.AccessibleName == "Past 7 days").PerformClick();
            Check(form.HoveredChartPoint == null);
            MoveChartMouse(form, 619, 510); Equal(point, form.HoveredChartPoint);
            form.Hide(); Check(form.HoveredChartPoint == null);
            form.Show(); Application.DoEvents();
            MoveChartMouse(form, 619, 510); Equal(point, form.HoveredChartPoint);
            history.SelectPool("codex_extra");
            form.UpdateUsage(snapshot, "Sample data", false, false, now, true);
            Check(form.HoveredChartPoint == null);
            Check(!refreshed);
        });
    }

    private static void MoveChartMouse(PopupForm form, int x, int y) =>
        typeof(PopupForm).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, new object[] { new MouseEventArgs(MouseButtons.None, 0, x, y, 0) });
}
