using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CodexTray;

internal static partial class Program
{
    private static void FeatureChecks()
    {
        Run("Notifications default off and create no alert file", () => WithHistoryDirectory(directory =>
        {
            var settings = new Settings();
            Check(!settings.LowQuotaAlerts && !settings.RestoredAlerts && !settings.ExpiryAlerts);
            var path = Path.Combine(directory, "alerts.json");
            Check(new QuotaNotifications(path).Evaluate(AlertSample(5), settings, Now) == null);
            Check(!File.Exists(path));
        }));
        Run("Low warnings persist, escalate once, and restart at a new reset", () => WithHistoryDirectory(directory =>
        {
            var path = Path.Combine(directory, "alerts.json");
            var settings = new Settings { LowQuotaAlerts = true };
            var sample = AlertSample(19);
            Check(new QuotaNotifications(path).Evaluate(sample, settings, Now)!.Contains("19%"));
            sample.ReadAt = Now.AddMinutes(5);
            Check(new QuotaNotifications(path).Evaluate(sample, settings, sample.ReadAt) == null);
            sample.Weekly!.Remaining = 9; sample.ReadAt = Now.AddMinutes(10);
            Check(new QuotaNotifications(path).Evaluate(sample, settings, sample.ReadAt)!.Contains("9%"));
            sample.ReadAt = Now.AddMinutes(15);
            Check(new QuotaNotifications(path).Evaluate(sample, settings, sample.ReadAt) == null);
            sample.Weekly.ResetsAt = Now.AddDays(8); sample.ReadAt = Now.AddMinutes(20);
            Check(new QuotaNotifications(path).Evaluate(sample, settings, sample.ReadAt) != null);
            Check(!File.ReadAllText(path).Contains("account-one"));
        }));
        Run("Alert scopes separate accounts and pools and ignore unavailable data", () => WithHistoryDirectory(directory =>
        {
            var alerts = new QuotaNotifications(Path.Combine(directory, "alerts.json"));
            var settings = new Settings { LowQuotaAlerts = true };
            var sample = AlertSample(5);
            Check(alerts.Evaluate(sample, settings, Now) != null);
            sample.AccountKey = "account-two";
            Check(alerts.Evaluate(sample, settings, Now) != null);
            sample.Pools.Add(new UsagePool { Id = "codex_extra", Name = "Extra", Weekly = sample.Weekly }); sample.SelectPool("codex_extra");
            Check(alerts.Evaluate(sample, settings, Now) != null);
            sample = AlertSample(5); sample.AccountKey = null;
            Check(alerts.Evaluate(sample, settings, Now) == null);
            sample.AccountKey = "account-three"; sample.ReadAt = Now.AddMinutes(-11);
            Check(alerts.Evaluate(sample, settings, Now) == null);
            sample.ReadAt = Now; sample.Weekly!.ResetsAt = Now;
            Check(alerts.Evaluate(sample, settings, Now) == null);
            sample.Weekly.ResetsAt = null;
            Check(alerts.Evaluate(sample, settings, Now) == null);
        }));
        Run("Restoration requires a fresh positive reading after depletion", () => WithHistoryDirectory(directory =>
        {
            var path = Path.Combine(directory, "alerts.json");
            var settings = new Settings { RestoredAlerts = true };
            var sample = AlertSample(0);
            Check(new QuotaNotifications(path).Evaluate(sample, settings, Now) == null);
            sample.ReadAt = Now.AddMinutes(5); sample.Weekly!.ResetsAt = sample.ReadAt;
            Check(new QuotaNotifications(path).Evaluate(sample, settings, sample.ReadAt) == null);
            sample.ReadAt = Now.AddMinutes(10); sample.Weekly.ResetsAt = Now.AddDays(8); sample.Weekly.Remaining = 100;
            Check(new QuotaNotifications(path).Evaluate(sample, settings, sample.ReadAt)!.Contains("available again"));
            sample.ReadAt = Now.AddMinutes(15);
            Check(new QuotaNotifications(path).Evaluate(sample, settings, sample.ReadAt) == null);
        }));
        Run("Credit expiry alerts group matching times and survive restarts", () => WithHistoryDirectory(directory =>
        {
            var path = Path.Combine(directory, "alerts.json");
            var settings = new Settings { ExpiryAlerts = true };
            var sample = AlertSample(80); sample.AvailableResets = 5;
            sample.ResetCredits.Add(new ResetCredit { ExpiresAt = Now.AddHours(24) });
            sample.ResetCredits.Add(new ResetCredit { ExpiresAt = Now.AddHours(24) });
            sample.ResetCredits.Add(new ResetCredit { ExpiresAt = Now.AddHours(25) });
            sample.ResetCredits.Add(new ResetCredit { ExpiresAt = Now });
            sample.ResetCredits.Add(new ResetCredit());
            Check(new QuotaNotifications(path).Evaluate(sample, settings, Now)!.Contains("2 banked resets"));
            Check(new QuotaNotifications(path).Evaluate(sample, settings, Now) == null);
            sample.ReadAt = Now.AddHours(1);
            Check(new QuotaNotifications(path).Evaluate(sample, settings, sample.ReadAt)!.Contains("A banked reset"));
        }));
        Run("Unreadable or unwritable alert state suppresses notifications", () => WithHistoryDirectory(directory =>
        {
            var path = Path.Combine(directory, "alerts.json");
            File.WriteAllText(path, "{");
            var alerts = new QuotaNotifications(path);
            var settings = new Settings { LowQuotaAlerts = true };
            Check(alerts.Evaluate(AlertSample(5), settings, Now) == null); Check(alerts.Warning != null);
            alerts = new QuotaNotifications(Path.Combine(path, "blocked.json"));
            Check(alerts.Evaluate(AlertSample(5), settings, Now) == null); Check(alerts.Warning != null);
        }));
        Run("Pacing uses fractional days and never projects from stale data", () =>
        {
            var sample = AlertSample(54); sample.Weekly!.ResetsAt = Now.AddDays(3);
            Check(QuotaPacing.Describe(sample, Now, false).Contains(18d.ToString("0.0")));
            sample.Weekly.ResetsAt = Now.AddDays(1.5);
            Check(QuotaPacing.Describe(sample, Now, false).Contains(36d.ToString("0.0")));
            Check(QuotaPacing.Describe(sample, Now, true).Contains("unavailable"));
            Check(QuotaPacing.Describe(sample, Now.AddMinutes(11), false).Contains("unavailable"));
            sample.Weekly.ResetsAt = Now.AddHours(2);
            Check(QuotaPacing.Describe(sample, Now, false).Contains("within 24h"));
            sample.Weekly.ResetsAt = Now;
            Check(QuotaPacing.Describe(sample, Now, false).Contains("unavailable"));
            sample.Weekly = null; Equal("", QuotaPacing.Describe(sample, Now, false));
        });
        Run("Update checks compare versions numerically and reject preview metadata", () =>
        {
            var current = new Version(1, 3, 0);
            Check(ReleaseUpdates.Describe(ReleaseJson("v1.10.0"), current).Contains("available"));
            Check(ReleaseUpdates.Describe(ReleaseJson("v1.3.0"), current).Contains("latest release"));
            Check(ReleaseUpdates.Describe(ReleaseJson("v1.2.0"), current).Contains("newer"));
            Throws<FormatException>(() => ReleaseUpdates.Describe(ReleaseJson("v1.3.0-beta"), current));
            Throws<FormatException>(() => ReleaseUpdates.Describe("{\"tag_name\":\"v2.0.0\",\"draft\":true,\"prerelease\":false}", current));
        });
        Run("Update discovery is explicit, throttled, and only reads release metadata", () =>
        {
            var handler = new UpdateHandler(HttpStatusCode.OK, ReleaseJson("v99.0.0"));
            using var updates = new ReleaseUpdates(handler);
            Equal(0, handler.Requests);
            Check(updates.CheckAsync(CancellationToken.None).GetAwaiter().GetResult().Contains("available"));
            updates.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(1, handler.Requests); Check(handler.ValidRequest);
            using var limited = new ReleaseUpdates(new UpdateHandler(HttpStatusCode.Forbidden, "{}"));
            Check(limited.CheckAsync(CancellationToken.None).GetAwaiter().GetResult().Contains("limited"));
            using var malformed = new ReleaseUpdates(new UpdateHandler(HttpStatusCode.OK, "{"));
            Check(malformed.CheckAsync(CancellationToken.None).GetAwaiter().GetResult().Contains("Could not read"));
        });
        Run("Closing an update check cancels its pending request", () =>
        {
            using var updates = new ReleaseUpdates(new SlowUpdateHandler());
            using var cancellation = new CancellationTokenSource();
            var request = updates.CheckAsync(cancellation.Token);
            Check(!request.IsCompleted);
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => request.GetAwaiter().GetResult());
        });
    }

    private static UsageSnapshot AlertSample(double remaining) => new UsageSnapshot
    {
        AccountKey = "account-one", ReadAt = Now,
        Weekly = new QuotaWindow { Minutes = 10080, Remaining = remaining, ResetsAt = Now.AddDays(7) }
    };
    private static string ReleaseJson(string tag) => "{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":false}";

    private sealed class UpdateHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;
        private readonly string json;
        public int Requests { get; private set; }
        public bool ValidRequest { get; private set; }
        public UpdateHandler(HttpStatusCode status, string json) { this.status = status; this.json = json; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            ValidRequest = request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/Koodattu/codex-usage-tracker-tray/releases/latest"
                && request.Headers.Authorization == null && request.Content == null && request.Headers.UserAgent.ToString().StartsWith("CodexTray/");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }

    private sealed class SlowUpdateHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The cancelled request should never complete normally.");
        }
    }
}
