using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CodexTray;

internal static partial class Program
{
    private static void StatusChecks()
    {
        Run("First-use recovery is readable without truncation at supported scales", () =>
        {
            const string message = "Codex was not found. Choose its executable from the tray menu.";
            using var popup = new PopupForm(new UsageHistory());
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                popup.ClientSize = new Size((int)(440 * scale), (int)(636 * scale));
                popup.UpdateUsage(null, message, false, true, Now.AddMinutes(5), false);
                var status = popup.Controls.OfType<Label>().Single(c => c.AccessibleName == "Connection status");
                var measured = TextRenderer.MeasureText(message, status.Font,
                    new Size(status.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak);
                Check(measured.Height <= status.ClientSize.Height);
            }
        });
        Run("Refresh status preserves the last reading and explains cooldown and pending reset", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = new UsageSnapshot { ReadAt = now.AddDays(-2), Weekly = new QuotaWindow { Remaining = 0, ResetsAt = now.AddMinutes(-1) } };
            using var popup = new PopupForm(new UsageHistory());
            popup.UpdateUsage(snapshot, "Codex took too long to respond. Retrying automatically.", false, true, now.AddMinutes(5), false);
            Check(popup.AccessibleDescription.Contains("Weekly remaining: 0%"));
            Check(popup.AccessibleDescription.Contains("Reset due; waiting for confirmed allowance"));
            Check(popup.Controls.OfType<Label>().Single(c => c.AccessibleName == "Reading freshness").Text.Contains(snapshot.ReadAt.LocalDateTime.ToString("d MMM yyyy")));
            var refreshButton = popup.Controls.OfType<Button>().Single(c => c.Text.StartsWith("Retry in "));
            Check(!refreshButton.Enabled);
            popup.UpdateUsage(snapshot, "Automatic refresh paused. Resume from the menu.", false, true, DateTimeOffset.MinValue, true);
            Check(refreshButton.Enabled);
            Check(!popup.Controls.OfType<Label>().Single(c => c.AccessibleName == "Reading freshness").Text.Contains("Next check"));
            snapshot.ReadAt = now; snapshot.Weekly.ResetsAt = now.AddDays(7); snapshot.Weekly.Remaining = 100;
            popup.UpdateUsage(snapshot, "Connected to Codex", false, false, now.AddMinutes(5), false, now.AddSeconds(45));
            Check(refreshButton.Text.StartsWith("Refresh in "));
            Check(!popup.AccessibleDescription.Contains("Reset due"));
            popup.UpdateUsage(snapshot, "Refreshing usage…", true, false, now.AddMinutes(5), false);
            Equal("Refreshing…", refreshButton.Text);
            Check(!refreshButton.Enabled);
            Equal(100d, snapshot.Weekly.Remaining);
        });
    }
}
