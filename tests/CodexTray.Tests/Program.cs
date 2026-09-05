using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CodexTray;

internal static partial class Program
{
    private static int passed;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "app-server") return FakeServer(args);
        if (args.FirstOrDefault() == "--diagnostic-child") return DiagnosticChild(args[1], args[2]);
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            if (args.FirstOrDefault() == "--update-smoke")
            {
                using var updates = new ReleaseUpdates();
                var result = updates.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(result);
                Check(result.Contains("latest") || result.Contains("available"));
                return 0;
            }
            if (args.FirstOrDefault() == "--notification-smoke")
            {
                using var icon = new NotifyIcon { Icon = SystemIcons.Information, Visible = true };
                bool shown = false;
                icon.BalloonTipShown += (_, __) => shown = true;
                icon.ShowBalloonTip(5000, "Codex Tray · test", "Test notification. Your alert preferences are unchanged.", ToolTipIcon.Info);
                var watch = Stopwatch.StartNew();
                while (!shown && watch.Elapsed < TimeSpan.FromSeconds(6)) { Application.DoEvents(); Thread.Sleep(20); }
                Console.WriteLine("Windows reported the test notification shown: " + shown);
                Check(shown);
                return 0;
            }
            if (args.FirstOrDefault() == "--live")
            {
                var executable = args.Length > 1 ? args[1] : WindowsIntegration.FindCodex(null);
                if (executable == null) throw new Exception("Codex executable not found.");
                var value = new CodexClient().ReadAsync(executable, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"Live read passed. 5-hour window present: {value.FiveHour != null}; weekly window present: {value.Weekly != null}; reset count supplied: {value.AvailableResets.HasValue}; expiry supplied: {value.NextExpiry.HasValue}.");
                foreach (var pool in value.Pools)
                    Console.WriteLine($"Usage pool: {pool.Name}; 5h available: {pool.FiveHour != null}; weekly available: {pool.Weekly != null}.");
                return 0;
            }
            if (args.FirstOrDefault() == "--inspect")
            {
                InspectProtocol().GetAwaiter().GetResult();
                Console.WriteLine("Registered Codex desktop app found: " + WindowsIntegration.OpenStoreDesktop(false));
                return 0;
            }
            if (args.FirstOrDefault() == "--ui-smoke")
            {
                Application.EnableVisualStyles();
                SmokeDpi();
                return 0;
            }
            if (args.FirstOrDefault() == "--settings-smoke" || args.FirstOrDefault() == "--settings-update-smoke")
            {
                Application.EnableVisualStyles();
                Run("Settings is above its modal owner and closes through Cancel", () => SettingsModalRegression(args[0] == "--settings-update-smoke"));
                return 0;
            }
            Run("Selects Codex multi-bucket view ahead of legacy", () =>
            {
                var s = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":99,\"windowDurationMins\":300}},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":22,\"windowDurationMins\":300}},\"other\":{}}}");
                Equal(78d, s.FiveHour!.Remaining);
            });
            Run("Identifies reversed windows by duration", () =>
            {
                var s = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":40,\"windowDurationMins\":10080},\"secondary\":{\"usedPercent\":7,\"windowDurationMins\":300}}}");
                Equal(93d, s.FiveHour!.Remaining); Equal(60d, s.Weekly!.Remaining);
            });
            Run("Usage pools never combine windows and history stays separate", () =>
            {
                var s = Parse("{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":40,\"windowDurationMins\":10080}},\"codex_extra\":{\"limitName\":\"Extra Codex quota\",\"primary\":{\"usedPercent\":7,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":20,\"windowDurationMins\":10080}}}}");
                Equal("codex", s.PoolId); Check(s.FiveHour == null); Equal(60d, s.Weekly!.Remaining);
                var history = new UsageHistory(); history.Add(s);
                s.SelectPool("codex_extra"); Equal(93d, s.FiveHour!.Remaining); Equal(80d, s.Weekly!.Remaining);
                history.Add(s); Equal(1, history.Points.Count);
                s.SelectPool("removed"); Equal("codex", s.PoolId); Check(s.FiveHour == null);
            });
            Run("Never invents a missing window or missing percent", () =>
            {
                var s = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":4},\"secondary\":{\"windowDurationMins\":300}}}");
                Check(s.FiveHour == null && s.Weekly == null);
            });
            Run("Does not substitute another metered product", () => Throws<UsageException>(() => Parse("{\"rateLimitsByLimitId\":{\"other\":{}},\"rateLimits\":{}}")));
            Run("Clamps overage and rejects textual percentages", () =>
            {
                var s = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":120,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":\"50\",\"windowDurationMins\":10080}}}");
                Equal(0d, s.FiveHour!.Remaining); Check(s.Weekly == null);
            });
            Run("Reset credits unknown is different from zero", () =>
            {
                Check(Parse("{\"rateLimits\":{}}").AvailableResets == null);
                Equal(0L, Parse("{\"rateLimits\":{},\"rateLimitResetCredits\":{\"availableCount\":0,\"credits\":[]}}").AvailableResets!.Value);
            });
            Run("Authoritative count survives capped reset details", () =>
            {
                var s = Parse("{\"rateLimits\":{},\"rateLimitResetCredits\":{\"availableCount\":9,\"credits\":[{\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":1800000000},{\"status\":\"redeemed\",\"resetType\":\"codexRateLimits\",\"expiresAt\":1700000000}]}}");
                Equal(9L, s.AvailableResets!.Value); Equal(1800000000L, s.NextExpiry!.Value.ToUnixTimeSeconds()); Check(!s.ResetDetailsComplete);
            });
            Run("Handles unlimited expiry and count-only responses", () =>
            {
                var s = Parse("{\"rateLimits\":{},\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":[{\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":null}]}}");
                Check(s.ResetDetailsComplete && s.NextExpiry == null);
                Check(!Parse("{\"rateLimits\":{},\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":null}}").ResetDetailsComplete);
            });
            Run("Banked resets preserve individual expiries in order", () =>
            {
                var s = Parse("{\"rateLimits\":{},\"rateLimitResetCredits\":{\"availableCount\":4,\"credits\":[{\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":1900000000},{\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":1800000000},{\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":null},{\"status\":\"available\",\"resetType\":\"codexRateLimits\"}]}}");
                Equal(4, s.ResetCredits.Count); Equal(1800000000L, s.ResetCredits[0].ExpiresAt!.Value.ToUnixTimeSeconds());
                Equal(1900000000L, s.ResetCredits[1].ExpiresAt!.Value.ToUnixTimeSeconds());
                Check(s.ResetCredits[2].ExpiryKnown && s.ResetCredits[2].ExpiresAt == null);
                Check(!s.ResetCredits[3].ExpiryKnown);
                Check(s.ResetCredits[3].Display(4, Now).Contains("unavailable"));
            });
            Run("Invalid timestamps are unavailable", () =>
                Check(Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":50,\"windowDurationMins\":300,\"resetsAt\":9999999999999}}}").FiveHour!.ResetsAt == null));
            Run("Banked expiry countdowns advance without changing the recorded expiry", () =>
            {
                var expiry = Now.AddDays(2).AddHours(3);
                var credit = new ResetCredit { ExpiresAt = expiry };
                var date = expiry.LocalDateTime.ToString("d MMM yyyy, HH:mm");
                Check(credit.Display(1, Now).Contains(date));
                Check(credit.Display(1, Now).EndsWith("(2d 3h)"));
                Check(credit.Display(1, Now.AddHours(4)).EndsWith("(1d 23h)"));
                Check(credit.Display(1, expiry.AddSeconds(-30)).EndsWith("(<1m)"));
                Check(credit.Display(1, expiry).Contains("Expiry passed"));
                Equal(expiry, credit.ExpiresAt!.Value);
                Check(new ResetCredit().Display(1, Now).Contains("No expiry"));
                Check(new ResetCredit { ExpiryKnown = false }.Display(1, Now).Contains("Expiry unavailable"));
            });
            Run("Passing a reset never manufactures fresh quota", () =>
            {
                var s = Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":80,\"windowDurationMins\":300,\"resetsAt\":1}}}");
                Check(s.FiveHour!.ResetPending(Now)); Equal(20d, s.FiveHour.Remaining); Check(s.IsStale(Now.AddMinutes(11)));
            });
            Run("Refresh throttles manual reads and backs off failures", () =>
            {
                var p = new RefreshPolicy(); p.Started(Now); p.Finished(Now, true, 0);
                Check(!p.CanRefresh(Now.AddSeconds(59), true)); Check(p.CanRefresh(Now.AddMinutes(1), true));
                Check(!p.CanRefresh(Now.AddMinutes(4), false)); Check(p.CanRefresh(Now.AddMinutes(5), false));
                p.Finished(Now, false, 0); Check(!p.CanRefresh(Now.AddMinutes(1), true));
                p.Finished(Now, false, 0); Equal(Now.AddMinutes(10), p.NextAttempt);
                p.Finished(Now, false, 0); Equal(Now.AddMinutes(20), p.NextAttempt);
                p.Finished(Now, false, 0); Equal(Now.AddMinutes(30), p.NextAttempt);
                p.Finished(Now, true, 15); Equal(Now.AddMinutes(5).AddSeconds(15), p.NextAttempt);
            });
            Run("History is bounded and clears when account changes", () =>
            {
                var h = new UsageHistory();
                h.Add(new UsageSnapshot { ReadAt = Now.AddDays(-31), AccountKey = "one" });
                h.Add(new UsageSnapshot { ReadAt = Now.AddDays(-2), AccountKey = "one" });
                h.Add(new UsageSnapshot { ReadAt = Now, AccountKey = "one" }); Equal(2, h.Points.Count);
                h.Add(new UsageSnapshot { ReadAt = Now.AddMinutes(5), AccountKey = "two" }); Equal(1, h.Points.Count);
                for (int i = 0; i < 43210; i++) h.Add(new UsageSnapshot { ReadAt = Now.AddSeconds(i), AccountKey = "two" });
                Equal(43201, h.Points.Count);
            });
            Run("Chart periods use rolling 24h, 7d and 30d boundaries", () =>
            {
                var h = new UsageHistory();
                foreach (var days in new[] { 31, 30, 8, 7, 2, 1, 0 }) h.Add(new UsageSnapshot { ReadAt = Now.AddDays(-days) });
                Equal(2, h.InRange(Now, 1).Count()); Equal(4, h.InRange(Now, 7).Count()); Equal(6, h.InRange(Now, 30).Count());
            });
            Run("Daily history survives restart and preserves every pool", () => WithHistoryDirectory(directory =>
            {
                var s = Parse(Fixture()); s.AccountKey = "account-one"; s.ReadAt = Now.AddDays(-6);
                s.Pools.Add(new UsagePool { Id = "codex_extra", Weekly = new QuotaWindow { Remaining = 9 } });
                new UsageHistory(new HistoryStore(directory)).AddAsync(s).GetAwaiter().GetResult();
                s.ReadAt = Now;
                var restarted = new UsageHistory(new HistoryStore(directory));
                restarted.AddAsync(s).GetAwaiter().GetResult();
                Equal(2, restarted.Points.Count); Equal(34d, restarted.Points[0].Weekly!.Value);
                restarted.SelectPool("codex_extra"); Equal(2, restarted.Points.Count); Equal(9d, restarted.Points[0].Weekly!.Value);
                restarted.SelectPool("codex"); Equal(2, restarted.Points.Count);
                var files = Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories);
                Equal(2, files.Length);
                Check(files.All(file => !File.ReadAllText(file).Contains("account-one")));
                s.AccountKey = "account-two";
                restarted.AddAsync(s).GetAwaiter().GetResult(); Equal(1, restarted.Points.Count);
            }));
            Run("Interrupted appends preserve valid history and accept new rows", () => WithHistoryDirectory(directory =>
            {
                var store = new HistoryStore(directory);
                store.Append("account", new[] { new HistoryRow { Point = new HistoryPoint { Time = Now.AddMinutes(-1), Weekly = 50 } } }, Now);
                var file = Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories).Single();
                File.AppendAllText(file, "{\"timestamp\":");
                store.Append("account", new[] { new HistoryRow { Point = new HistoryPoint { Time = Now, Weekly = 49 } } }, Now);
                var loaded = store.Load("account", Now);
                Equal(2, loaded.Rows.Count); Check(loaded.SkippedRows); Equal(49d, loaded.Rows[1].Point.Weekly!.Value);
            }));
            Run("Retention removes old daily files and leaves unrelated files alone", () => WithHistoryDirectory(directory =>
            {
                var store = new HistoryStore(directory);
                var old = Now.AddDays(-31);
                store.Append("account", new[] { new HistoryRow { Point = new HistoryPoint { Time = old } } }, old);
                var oldFile = Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories).Single();
                var notes = Path.Combine(Path.GetDirectoryName(oldFile)!, "notes.jsonl");
                File.WriteAllText(notes, "User notes");
                store.Append("account", new[] { new HistoryRow { Point = new HistoryPoint { Time = Now } } }, Now);
                Check(!File.Exists(oldFile)); Check(File.Exists(notes)); Equal(1, store.Load("account", Now).Rows.Count);
            }));
            Run("A disk write failure keeps live history and reports the failure", () => WithHistoryDirectory(directory =>
            {
                var blocked = Path.Combine(directory, "not-a-directory"); File.WriteAllText(blocked, "fixture");
                var h = new UsageHistory(new HistoryStore(blocked));
                var s = Parse(Fixture()); s.AccountKey = "account";
                h.AddAsync(s).GetAwaiter().GetResult();
                Equal(1, h.Points.Count); Check(h.Warning != null);
            }));
            Run("Real process transport handles interleaved RPC and reaps child", () =>
            {
                var before = TestProcesses();
                Environment.SetEnvironmentVariable("CODEX_TRAY_FAKE", "normal");
                var value = new CodexClient().ReadAsync(Application.ExecutablePath, CancellationToken.None).GetAwaiter().GetResult();
                Equal(64d, value.FiveHour!.Remaining); Equal(34d, value.Weekly!.Remaining);
                Check(SpinWait.SpinUntil(() => !TestProcesses().Except(before).Any(), 2000));
            });
            Run("Transport sends valid JSON under a UTF-8 Windows console", () =>
            {
                var original = Console.InputEncoding;
                try
                {
                    Console.InputEncoding = new System.Text.UTF8Encoding(true);
                    Environment.SetEnvironmentVariable("CODEX_TRAY_FAKE", "normal");
                    var value = new CodexClient().ReadAsync(Application.ExecutablePath, CancellationToken.None).GetAwaiter().GetResult();
                    Equal(64d, value.FiveHour!.Remaining); Equal(34d, value.Weekly!.Remaining);
                }
                finally { Console.InputEncoding = original; }
            });
            Run("Protocol errors never expose backend details", () =>
            {
                Environment.SetEnvironmentVariable("CODEX_TRAY_FAKE", "error");
                try { new CodexClient().ReadAsync(Application.ExecutablePath, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Expected error"); }
                catch (UsageException ex) { Check(!ex.Message.Contains("PRIVATE")); }
            });
            Run("Cancellation stops a silent backend promptly", () =>
            {
                var before = TestProcesses();
                Environment.SetEnvironmentVariable("CODEX_TRAY_FAKE", "hang");
                var watch = Stopwatch.StartNew();
                using var cancellation = new CancellationTokenSource(250);
                Throws<OperationCanceledException>(() => new CodexClient().ReadAsync(Application.ExecutablePath, cancellation.Token).GetAwaiter().GetResult());
                Check(watch.Elapsed < TimeSpan.FromSeconds(4));
                Check(SpinWait.SpinUntil(() => !TestProcesses().Except(before).Any(), 2000));
            });
            Environment.SetEnvironmentVariable("CODEX_TRAY_FAKE", null);
            FeatureChecks();
            DiagnosticChecks();
            Application.EnableVisualStyles();
            Run("Single icon is the default and unavailable limits are never selected", () =>
            {
                Equal("weekly", new Settings().IconVisibility);
                var onlyWeekly = new UsageSnapshot { Weekly = new QuotaWindow { Remaining = 58 } };
                var onlyFive = new UsageSnapshot { FiveHour = new QuotaWindow { Remaining = 72 } };
                foreach (var mode in new[] { "weekly", "5h", "both", "rotate" })
                {
                    var settings = new Settings { IconVisibility = mode };
                    var w = TraySelection.Select(onlyWeekly, settings, 15);
                    Equal(QuotaKind.Weekly, w.Primary); Check(w.Secondary == null && !w.Rotating);
                    var f = TraySelection.Select(onlyFive, settings, 15);
                    Equal(QuotaKind.FiveHour, f.Primary); Check(f.Secondary == null && !f.Rotating);
                    var unknown = TraySelection.Select(null, settings, 15);
                    Check(unknown.Secondary == null && !unknown.Rotating);
                }
            });
            Run("Two-window choices and rotation honor their interval", () =>
            {
                var snapshot = new UsageSnapshot { Weekly = new QuotaWindow(), FiveHour = new QuotaWindow() };
                var settings = new Settings { IconVisibility = "both" };
                var both = TraySelection.Select(snapshot, settings, 0);
                Equal(QuotaKind.Weekly, both.Primary); Equal(QuotaKind.FiveHour, both.Secondary!.Value);
                settings.IconVisibility = "5h"; Equal(QuotaKind.FiveHour, TraySelection.Select(snapshot, settings, 0).Primary);
                settings.IconVisibility = "rotate"; settings.RotationSeconds = 10;
                Equal(QuotaKind.Weekly, TraySelection.Select(snapshot, settings, 9.99).Primary);
                Equal(QuotaKind.FiveHour, TraySelection.Select(snapshot, settings, 10).Primary);
                Equal(QuotaKind.Weekly, TraySelection.Select(snapshot, settings, 20).Primary);
                settings.RotationSeconds = 30;
                Equal(QuotaKind.Weekly, TraySelection.Select(snapshot, settings, 20).Primary);
                Check(TraySelection.Select(snapshot, settings, 30).Rotating);
            });
            Run("All numeric glyphs fit on one line without clipping", () =>
            {
                for (int value = 0; value <= 100; value++)
                {
                    using var path = TrayIconRenderer.NumberPath(value.ToString());
                    var bounds = path.GetBounds();
                    Check(bounds.Left >= 1 && bounds.Right <= 31 && bounds.Top >= 1 && bounds.Bottom <= 31);
                    if (value == 58 || value == 100) Check(bounds.Width > bounds.Height);
                }
                RenderIconSheet();
            });
            Run("Popup sizing honors 150 percent and fits small work areas", () =>
            {
                Equal(new Size(660, 954), DpiLayout.Fit(new Size(440, 636), 144, new Size(3840, 2100)));
                Equal(new Size(880, 1272), DpiLayout.Fit(new Size(440, 636), 192, new Size(3840, 2100)));
                var small = DpiLayout.Fit(new Size(440, 636), 192, new Size(1280, 680));
                Check(small.Height <= 664 && small.Width <= 1264);
            });
            Run("Numbers and rings render at tray DPI sizes", () =>
            {
                foreach (int size in new[] { 16, 20, 24, 32, 48, 64 })
                foreach (var mode in new[] { "numbers", "rings" })
                foreach (var remaining in new double?[] { null, 0, 20, 50, 100 })
                {
                    using var icon = TrayIconRenderer.Create(remaining.HasValue ? new QuotaWindow { Remaining = remaining.Value } : null, mode, false, size);
                    Equal(size, icon.Width);
                }
            });
            Run("Popup renders live, empty and stale layouts at multiple sizes", RenderPreviews);
            ChartHoverChecks();
            Run("Repeated layout retains the font cached by native controls", () =>
            {
                using var control = new RadioButton();
                Theme.SetFont(control, 14);
                var font = control.Font;
                Theme.SetFont(control, 14);
                Check(ReferenceEquals(font, control.Font));
                using var bitmap = new Bitmap(100, 30);
                using var graphics = Graphics.FromImage(bitmap);
                Check(graphics.MeasureString("Settings", font).Width > 0);
                control.Font.Dispose();
            });
            Run("Popup and settings render with classic Windows controls", () =>
            {
                var original = Application.VisualStyleState;
                try { Application.VisualStyleState = System.Windows.Forms.VisualStyles.VisualStyleState.NoneEnabled; RenderPreviews(); }
                finally { Application.VisualStyleState = original; }
            });
            Console.WriteLine($"PASS: {passed} checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int FakeServer(string[] args)
    {
        var mode = Environment.GetEnvironmentVariable("CODEX_TRAY_FAKE");
        while (Console.ReadLine() is string line)
        {
            var request = Json.Parse(line);
            var id = Json.Number(request, "id");
            if (!id.HasValue) continue;
            if (mode == "hang") { Thread.Sleep(10000); continue; }
            var method = Json.String(request, "method");
            if (method == "initialize") Console.WriteLine("{\"id\":1,\"result\":{}}");
            else if (method == "account/read") Console.WriteLine("{\"id\":2,\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":\"test@example.invalid\",\"planType\":\"pro\"}}}");
            else if (method == "account/rateLimits/read")
            {
                Console.WriteLine("{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
                Console.WriteLine("{\"method\":\"unsupported/serverRequest\",\"id\":3,\"params\":{}}");
                Console.WriteLine(mode == "error" ? "{\"id\":3,\"error\":{\"code\":500,\"message\":\"PRIVATE BACKEND DETAIL\"}}" : "{\"id\":3,\"result\":" + Fixture() + "}");
            }
        }
        return 0;
    }
    private static string Fixture() => "{\"rateLimits\":{\"planType\":\"pro\",\"primary\":{\"usedPercent\":36,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":66,\"windowDurationMins\":10080}},\"rateLimitResetCredits\":{\"availableCount\":2,\"credits\":null}}";
    private static UsageSnapshot Parse(string json) => UsageParser.Parse(Json.Parse(json), Now);

    private static void RenderPreviews()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".artifacts");
        Directory.CreateDirectory(directory);
        var history = new UsageHistory();
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i <= 8640; i++) history.Points.Add(new HistoryPoint { Time = now.AddMinutes(-5 * (8640 - i)), FiveHour = Math.Max(0, 99 - i % 55 * 1.1), Weekly = 90 - i % 2016 * 56d / 2016 });
        history.Points[history.Points.Count - 1].FiveHour = 64;
        history.Points[history.Points.Count - 1].Weekly = 34;
        var s = Parse(Fixture()); s.ReadAt = now;
        s.FiveHour!.ResetsAt = now.AddHours(2).AddMinutes(18);
        s.Weekly!.ResetsAt = now.AddDays(3).AddHours(8);
        s.NextExpiry = now.AddDays(5); s.ResetDetailsComplete = true;
        s.AvailableResets = 3;
        foreach (var days in new[] { 5, 8, 11 }) s.ResetCredits.Add(new ResetCredit { ExpiresAt = now.AddDays(days) });
        using var form = new PopupForm(history) { KeepOpen = true };
        form.Location = new Point(-20000, -20000);
        form.Show();
        Application.DoEvents();
        form.UpdateUsage(s, "Connected to Codex", false, false, now.AddMinutes(5), true);
        foreach (var size in new[] { new Size(440, 636), new Size(660, 954), new Size(880, 1272) })
        {
            form.ClientSize = size;
            using var bitmap = new Bitmap(size.Width, size.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
            bitmap.Save(Path.Combine(directory, $"preview-{size.Width}.png"), ImageFormat.Png);
        }
        form.ClientSize = new Size(440, 636);
        form.UpdateUsage(null, "Codex was not found. Choose its executable from the tray menu.", false, true, now.AddMinutes(5), true);
        using (var bitmap = new Bitmap(440, 636)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, 440, 636)); bitmap.Save(Path.Combine(directory, "preview-empty.png")); }
        s.ReadAt = now.AddMinutes(-25);
        form.UpdateUsage(s, "Codex took too long to respond. Retrying automatically.", false, true, now.AddMinutes(10), false);
        using (var bitmap = new Bitmap(440, 636)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, 440, 636)); bitmap.Save(Path.Combine(directory, "preview-stale.png")); }
        s.Pools.Add(new UsagePool { Id = "codex_extra", Name = "Additional Codex quota", FiveHour = s.FiveHour, Weekly = s.Weekly });
        s.SelectPool("codex_extra"); s.ReadAt = now;
        form.UpdateUsage(s, "Connected to Codex", false, false, now.AddMinutes(5), true);
        using (var bitmap = new Bitmap(440, 636)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, 440, 636)); bitmap.Save(Path.Combine(directory, "preview-pools.png")); }
        s.FiveHour = null; s.Weekly!.Remaining = 58;
        form.ClientSize = new Size(660, 954);
        form.UpdateUsage(s, "Connected to Codex", false, false, now.AddMinutes(5), true);
        Check(!form.AccessibleDescription.Contains("5-hour"));
        using (var bitmap = new Bitmap(660, 954)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, 660, 954)); bitmap.Save(Path.Combine(directory, "preview-weekly-150.png")); }
        bool menuClicked = false, settingsClicked = false;
        form.MenuRequested += _ => menuClicked = true;
        form.SettingsRequested += (_, __) => settingsClicked = true;
        form.Controls.OfType<Button>().Single(b => b.AccessibleName == "Open menu").PerformClick();
        form.Controls.OfType<Button>().Single(b => b.AccessibleName == "Settings").PerformClick();
        Check(menuClicked && settingsClicked);
        int selectedDays = 0;
        form.ChartRangeSelected += days => selectedDays = days;
        form.Controls.OfType<Button>().Single(b => b.AccessibleName == "Past 30 days").PerformClick();
        Equal(30, form.ChartDays); Equal(30, selectedDays);
        using (var bitmap = new Bitmap(660, 954)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, 660, 954)); bitmap.Save(Path.Combine(directory, "preview-month-150.png")); }
        Equal(3, form.Controls.OfType<ListBox>().Single().Items.Count);
        form.Controls.OfType<Button>().Single(b => b.AccessibleName == "Past 7 days").PerformClick(); Equal(7, form.ChartDays);
        using var dialog = new SettingsForm(new Settings { IconVisibility = "rotate", RotationSeconds = 15 }, false);
        dialog.Location = new Point(-20000, -20000);
        dialog.Show();
        dialog.ClientSize = new Size(660, 954);
        Application.DoEvents();
        Equal("rotate", dialog.IconVisibility); Equal(15, dialog.RotationSeconds);
        using (var bitmap = new Bitmap(660, 954)) { dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, 660, 954)); bitmap.Save(Path.Combine(directory, "preview-settings-150.png")); }
        dialog.Controls.OfType<Button>().Single(b => b.Text == "Notifications").PerformClick();
        Check(!dialog.LowQuotaAlerts && !dialog.RestoredAlerts && !dialog.ExpiryAlerts);
        dialog.Controls.OfType<CheckBox>().Single(b => b.Text == "Low allowance warnings").Checked = true;
        var warning = dialog.Controls.OfType<NumericUpDown>().Single(c => c.AccessibleName == "Warning percentage remaining");
        var critical = dialog.Controls.OfType<NumericUpDown>().Single(c => c.AccessibleName == "Critical percentage remaining");
        Check(warning.Enabled && critical.Enabled);
        warning.Value = 8; Equal(7m, critical.Maximum); Equal(7, dialog.CriticalPercent);
        Check(dialog.LowQuotaAlerts);
        using (var bitmap = new Bitmap(660, 954)) { dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, 660, 954)); bitmap.Save(Path.Combine(directory, "preview-alerts-150.png")); }
        dialog.Controls.OfType<Button>().Single(b => b.Text == "About").PerformClick();
        bool openedReleases = false;
        dialog.ReleasesRequested += (_, __) => openedReleases = true;
        dialog.Controls.OfType<Button>().Single(b => b.Text == "Open Releases").PerformClick();
        Check(openedReleases);
        bool openedLogs = false;
        dialog.LogsRequested += (_, __) => openedLogs = true;
        dialog.Controls.OfType<Button>().Single(b => b.Text == "Open logs").PerformClick();
        Check(openedLogs);
        using (var bitmap = new Bitmap(660, 954)) { dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, 660, 954)); bitmap.Save(Path.Combine(directory, "preview-about-150.png")); }
    }

    private static void RenderIconSheet()
    {
        Directory.CreateDirectory(".artifacts");
        using var bitmap = new Bitmap(640, 320);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Theme.Background);
        var values = new[] { 5, 58, 88, 100 };
        for (int row = 0; row < 2; row++)
        for (int col = 0; col < values.Length; col++)
        {
            int size = row == 0 ? 16 : 24, x = 16 + col * 156, y = 15 + row * 154;
            Theme.Label(g, $"{values[col]} · {size}px", 13, Theme.Text, new RectangleF(x, y, 145, 25));
            using var icon = TrayIconRenderer.Create(new QuotaWindow { Remaining = values[col] }, "numbers", false, size);
            using var pixels = icon.ToBitmap();
            g.DrawImageUnscaled(pixels, x, y + 32);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(pixels, new Rectangle(x + 40, y + 30, size * 4, size * 4));
        }
        bitmap.Save(".artifacts/tray-numbers.png");
    }

    private static void SmokeDpi()
    {
        using var form = new PopupForm(new UsageHistory()) { KeepOpen = true };
        var snapshot = Parse(Fixture());
        snapshot.FiveHour = null; snapshot.Weekly!.Remaining = 58;
        form.UpdateUsage(snapshot, "DPI verification · sample data", false, false, DateTimeOffset.UtcNow.AddMinutes(5), true);
        foreach (var screen in Screen.AllScreens)
        {
            var anchor = new Point(screen.WorkingArea.Left + screen.WorkingArea.Width / 2, screen.WorkingArea.Bottom - 8);
            DpiLayout.Place(form, anchor, new Size(440, 636), true);
            form.Show(); Application.DoEvents();
            var dpi = DpiLayout.WindowDpi(form.Handle);
            Equal(DpiLayout.Fit(new Size(440, 636), dpi, screen.WorkingArea.Size), form.ClientSize);
            Check(screen.WorkingArea.Contains(form.Bounds));
            Console.WriteLine($"Monitor DPI {dpi}: popup {form.ClientSize.Width}x{form.ClientSize.Height}; WinForms DeviceDpi {form.DeviceDpi}; fits work area.");
            form.Hide();
        }
        Console.WriteLine($"Tray DPI {DpiLayout.TrayDpi}: icon {DpiLayout.TrayIconSize}px.");
    }

    private static void SettingsModalRegression(bool pendingUpdate = false)
    {
        using var popup = new PopupForm(new UsageHistory()) { KeepOpen = true };
        popup.ShowNearTray();
        Application.DoEvents();
        using var updates = new ReleaseUpdates(new SlowUpdateHandler());
        using var dialog = new SettingsForm(new Settings(), false, updates);
        bool requestStarted = false;
        bool visible = false, onTop = false, ownerDisabled = false, coversPopup = false;
        string observation = "Dialog timer did not run.";
        using var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, __) =>
        {
            if (pendingUpdate && !requestStarted)
            {
                dialog.ShowAbout();
                var button = dialog.Controls.OfType<Button>().Single(b => b.Text == "Check for updates");
                button.PerformClick();
                Check(!button.Enabled);
                requestStarted = true;
                return;
            }
            timer.Stop();
            visible = dialog.Visible;
            ownerDisabled = !IsWindowEnabled(popup.Handle);
            coversPopup = dialog.Bounds == popup.Bounds;
            var overlap = Rectangle.Intersect(popup.Bounds, dialog.Bounds);
            var point = new Point(overlap.Left + overlap.Width / 2, overlap.Top + overlap.Height / 2);
            onTop = overlap.Width > 0 && GetAncestor(WindowFromPoint(point), 2) == dialog.Handle;
            observation = $"Settings visible: {visible}; above popup: {onTop}; covers entire popup: {coversPopup}; modal owner disabled: {ownerDisabled}; DPI: {DpiLayout.WindowDpi(dialog.Handle)}.";
            dialog.Controls.OfType<Button>().Single(b => b.Text == "Cancel").PerformClick();
        };
        timer.Start();
        var result = dialog.ShowFor(popup);
        Console.WriteLine(observation);
        Equal(DialogResult.Cancel, result);
        Check(visible && onTop && ownerDisabled && coversPopup);
        Check(IsWindowEnabled(popup.Handle));
        if (pendingUpdate) Check(requestStarted);
    }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);

    private static async Task InspectProtocol()
    {
        var executable = WindowsIntegration.FindCodex(null) ?? throw new Exception("Codex not found");
        using var process = new Process { StartInfo = new ProcessStartInfo(executable, "app-server --listen stdio://") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.ErrorDataReceived += (_, __) => { };
        CodexClient.StartWithRawInput(process);
        using var job = new ProcessJob(process);
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(25000);
        await CodexClient.RequestAsync(process, 1, "initialize", new { clientInfo = new { name = "codex_usage_tray", version = "1.0.0" } }, timeout.Token);
        await CodexClient.SendAsync(process, new { method = "initialized" });
        var response = await CodexClient.RequestAsync(process, 2, "account/rateLimits/read", new { }, timeout.Token);
        var buckets = Json.Object(response, "rateLimitsByLimitId");
        if (buckets != null)
            foreach (var key in buckets.Keys)
            {
                var bucket = Json.Object(buckets, key)!;
                Console.WriteLine("Bucket: " + key);
                foreach (var name in new[] { "primary", "secondary" })
                {
                    var window = Json.Object(bucket, name);
                    Console.WriteLine(name + " duration in minutes: " + (window == null ? "unavailable" : Json.Number(window, "windowDurationMins")?.ToString() ?? "unspecified"));
                }
            }
        process.StandardInput.BaseStream.Close();
        process.WaitForExit(1000);
    }

    private static void Run(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void WithHistoryDirectory(Action<string> action)
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".artifacts", "history-tests"));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { action(path); }
        finally
        {
            if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid test cleanup path.");
            Directory.Delete(path, true);
        }
    }
    private static int[] TestProcesses()
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Application.ExecutablePath));
        try { return processes.Select(p => p.Id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
