using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;
using CodexTray;

internal static partial class Program
{
    private static void HistoryViewChecks()
    {
        Run("History export uses the selected pool and range with invariant recorded values", () =>
        {
            var history = new UsageHistory();
            history.SelectPool("codex_extra");
            history.Points.Add(new HistoryPoint { Time = Now.AddHours(-1), Weekly = 99 });
            history.SelectPool("codex");
            history.Points.Add(new HistoryPoint { Time = Now.AddDays(-2), FiveHour = 99, Weekly = 60 });
            history.Points.Add(new HistoryPoint { Time = Now.AddMinutes(-30), FiveHour = null, Weekly = 12.5 });
            history.Points.Add(new HistoryPoint { Time = Now, FiveHour = 0, Weekly = null });
            history.Points.Add(new HistoryPoint { Time = Now.AddSeconds(1), Weekly = 100 });
            var view = new HistoryReadings(history, "codex", Now);
            var rows = view.InRange(1);
            Equal(2, rows.Count);
            Equal(Now, rows[0].Time);
            var culture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
                using var writer = new StringWriter();
                view.WriteCsv(writer, 1);
                var lines = writer.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                Equal("recorded_at_utc,pool_id,five_hour_remaining_percent,weekly_remaining_percent", lines[0]);
                Equal("2026-09-05T11:30:00Z,codex,,12.5", lines[1]);
                Equal("2026-09-05T12:00:00Z,codex,0,", lines[2]);
                Equal(3, lines.Length);
            }
            finally { Thread.CurrentThread.CurrentCulture = culture; }
            history.Clear();
            Equal(2, view.InRange(1).Count);
        });
        Run("Readings table exposes missing values and preserves range when returning", () =>
        {
            var history = new UsageHistory();
            history.Points.Add(new HistoryPoint { Time = Now.AddDays(-2), FiveHour = 100, Weekly = 40 });
            history.Points.Add(new HistoryPoint { Time = Now.AddMinutes(-30), Weekly = 12.5 });
            history.Points.Add(new HistoryPoint { Time = Now, FiveHour = 0, Weekly = 10 });
            using var form = new HistoryForm(new HistoryReadings(history, "codex", Now), "Codex", 1);
            form.Location = new Point(-20000, -20000);
            form.Show();
            var table = form.Controls.OfType<DataGridView>().Single();
            Equal(2, table.RowCount);
            Equal("0%", table.Rows[0].Cells[1].Value);
            Equal("—", table.Rows[1].Cells[1].Value);
            Check(table.AccessibleName.Contains("remaining"));
            form.Controls.OfType<Button>().Single(c => c.Text == "7d").PerformClick();
            Equal(7, form.ChartDays);
            Equal(3, table.RowCount);
            Check(form.Controls.OfType<Label>().Any(c => c.Text.Contains("Gaps over 15 minutes are unobserved; usage may be missing.")));
            form.Close();
        });
        Run("CSV saves replace a complete export and preserve an existing file on failure", () => WithHistoryDirectory(directory =>
        {
            var history = new UsageHistory();
            history.Points.Add(new HistoryPoint { Time = Now, FiveHour = 50.25 });
            var view = new HistoryReadings(history, "codex_extra,\"test\"", Now);
            var path = Path.Combine(directory, "readings.csv");
            File.WriteAllText(path, "original");
            using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Throws<IOException>(() => view.SaveCsv(path, 1));
                Equal("original", File.ReadAllText(path));
            }
            view.SaveCsv(path, 1);
            Check(File.ReadAllText(path).Contains("2026-09-05T12:00:00Z,\"codex_extra,\"\"test\"\"\",50.25,"));
            Equal(1, Directory.GetFiles(directory).Length);
        }));
        Run("Readings stay legible at each DPI with a full retained history", () =>
        {
            var history = new UsageHistory();
            for (var i = 0; i <= 43200; i++) history.Points.Add(new HistoryPoint { Time = Now.AddMinutes(-i), FiveHour = 70.5, Weekly = i % 40 == 0 ? (double?)null : 52.5 });
            var watch = Stopwatch.StartNew();
            using var form = new HistoryForm(new HistoryReadings(history, "codex", Now), "Codex pool with a long descriptive name for a separate allowance", 30);
            form.Location = new Point(-20000, -20000); form.Show();
            var table = form.Controls.OfType<DataGridView>().Single();
            Equal(43201, table.RowCount);
            table.CurrentCell = table.Rows[12].Cells[0];
            Check(form.Controls.OfType<Label>().Any(c => c.Text.StartsWith("Recorded " + Now.AddMinutes(-12).ToLocalTime().ToString("d MMM yyyy, HH:mm:ss zzz"))));
            Directory.CreateDirectory(".artifacts");
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                form.ClientSize = new Size((int)(440 * scale), (int)(636 * scale));
                Check(table.Rows[12].Height >= 26 * scale);
                Equal(12, table.CurrentCell.RowIndex);
                using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                form.DrawToBitmap(bitmap, form.ClientRectangle);
                bitmap.Save(Path.Combine(".artifacts", "preview-readings-" + (int)(scale * 100) + ".png"));
            }
            Console.WriteLine("Synthetic 43,201-row view plus three DPI renders: " + watch.ElapsedMilliseconds + " ms.");
            form.Close();
        });
        Run("Readings opens from the popup and returns its range without requesting usage", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var history = new UsageHistory();
            history.Points.Add(new HistoryPoint { Time = now.AddDays(-2), Weekly = 45 });
            history.Points.Add(new HistoryPoint { Time = now.AddMinutes(-5), Weekly = 25 });
            using var popup = new PopupForm(history) { KeepOpen = true, Location = new Point(-20000, -20000) };
            popup.Show();
            popup.UpdateUsage(new UsageSnapshot { ReadAt = now, Weekly = new QuotaWindow { Remaining = 25 } }, "Sample data", false, false, now.AddMinutes(5), true);
            bool requested = false, inspected = false;
            int savedRange = 0;
            popup.RefreshRequested += (_, __) => requested = true;
            popup.ChartRangeSelected += days => savedRange = days;
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                var dialog = Application.OpenForms.OfType<HistoryForm>().Single();
                try
                {
                    Check(dialog.Visible && dialog.Owner == popup && !IsWindowEnabled(popup.Handle));
                    Equal(popup.Bounds, dialog.Bounds);
                    dialog.Controls.OfType<Button>().Single(c => c.Text == "7d").PerformClick();
                    Equal(2, dialog.Controls.OfType<DataGridView>().Single().RowCount);
                    inspected = true;
                }
                finally { dialog.Controls.OfType<Button>().Single(c => c.Text == "Back").PerformClick(); }
            };
            timer.Start();
            popup.Controls.OfType<Button>().Single(c => c.AccessibleName == "View recorded readings").PerformClick();
            Check(inspected && !requested && IsWindowEnabled(popup.Handle) && popup.KeepOpen);
            Equal(7, popup.ChartDays); Equal(7, savedRange);
            popup.Close();
        });
        Run("Empty history has a useful recovery path and disables export", () =>
        {
            using var form = new HistoryForm(new HistoryReadings(new UsageHistory(), "codex", Now), "Codex", 1);
            form.Location = new Point(-20000, -20000); form.Show();
            Check(!form.Controls.OfType<Button>().Single(c => c.Text == "Export CSV…").Enabled);
            Equal(0, form.Controls.OfType<DataGridView>().Single().RowCount);
            Check(form.Controls.OfType<Label>().Any(c => c.Text.Contains("successful usage check")));
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
            bitmap.Save(Path.Combine(".artifacts", "preview-readings-empty.png"));
            form.Close();
        });
    }
}
