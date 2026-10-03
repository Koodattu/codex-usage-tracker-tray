using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CodexTray;

internal static partial class Program
{
    // Interactive development surface: synthetic data, no account calls, registry or preference writes.
    private static void ProductPreview(string scenario)
    {
        Application.EnableVisualStyles();
        var now = DateTimeOffset.UtcNow;
        var history = new UsageHistory();
        for (var i = 0; i <= 8640; i++)
        {
            if (i > 8510 && i < 8570) continue;
            history.Points.Add(new HistoryPoint { Time = now.AddMinutes(-5 * (8640 - i)),
                FiveHour = 100 - i % 60 * 1.5, Weekly = i % 500 == 0 ? (double?)null : 94 - i % 2016 * 70d / 2016 });
        }
        var snapshot = new UsageSnapshot { ReadAt = now, Plan = "Pro", AvailableResets = 3,
            FiveHour = new QuotaWindow { Remaining = 64, ResetsAt = now.AddHours(2) },
            Weekly = new QuotaWindow { Remaining = 34, ResetsAt = now.AddDays(3) } };
        foreach (var days in new[] { 1, 5, 11 }) snapshot.ResetCredits.Add(new ResetCredit { ExpiresAt = now.AddDays(days) });
        var message = "Connected to Codex";
        var failed = false;
        if (scenario == "error") { failed = true; snapshot.ReadAt = now.AddDays(-1); message = "Codex took too long to respond. Retrying automatically."; }
        if (scenario == "reset") snapshot.FiveHour.ResetsAt = now.AddMinutes(-1);
        if (scenario == "empty") { history.Clear(); message = "Codex was not found. Choose its executable from the tray menu."; failed = true; }
        using var popup = new PopupForm(history) { KeepOpen = true, ShowInTaskbar = true };
        popup.Text = "Codex Tray · synthetic preview";
        popup.UpdateUsage(scenario == "empty" ? null : snapshot, message, false, failed, now.AddMinutes(5), !failed);
        // These actions belong to the real tray controller; the preview only inspects local data.
        foreach (var button in popup.Controls.OfType<Button>().Where(b => b.AccessibleName == "Open menu"
            || b.AccessibleName == "Settings" || b.Text == "Open Codex" || b.Text == "Refresh")) button.Enabled = false;
        popup.Shown += (_, __) => DpiLayout.Place(popup, new Point(600, 500), new Size(440, 636), false);
        using var lifetime = new System.Windows.Forms.Timer { Interval = 250 };
        lifetime.Tick += (_, __) => { if (!popup.Visible) popup.Close(); };
        popup.Shown += (_, __) => lifetime.Start();
        Application.Run(popup);
    }
}
