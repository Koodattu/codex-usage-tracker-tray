using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexTray;

internal sealed class QuotaWindow
{
    public int Minutes { get; set; }
    public double Remaining { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public bool ResetPending(DateTimeOffset now) => ResetsAt.HasValue && ResetsAt.Value <= now;
}

internal sealed class UsageSnapshot
{
    public DateTimeOffset ReadAt { get; set; }
    public QuotaWindow? FiveHour { get; set; }
    public QuotaWindow? Weekly { get; set; }
    public long? AvailableResets { get; set; }
    public DateTimeOffset? NextExpiry { get; set; }
    public bool ResetDetailsComplete { get; set; }
    public List<ResetCredit> ResetCredits { get; } = new List<ResetCredit>();
    public string Plan { get; set; } = "Codex";
    public string? AccountKey { get; set; }
    public string PoolId { get; private set; } = "codex";
    public List<UsagePool> Pools { get; } = new List<UsagePool>();
    public void SelectPool(string id)
    {
        var pool = Pools.FirstOrDefault(p => p.Id == id) ?? Pools.FirstOrDefault(p => p.Id == "codex") ?? Pools.FirstOrDefault();
        if (pool == null) return;
        PoolId = pool.Id;
        FiveHour = pool.FiveHour;
        Weekly = pool.Weekly;
        Plan = pool.Plan;
    }
    public bool IsStale(DateTimeOffset now) => now - ReadAt > TimeSpan.FromMinutes(10);
}

internal sealed class ResetCredit
{
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool ExpiryKnown { get; set; } = true;
    public string Display(int index, DateTimeOffset now)
    {
        var expiry = !ExpiryKnown ? "Expiry unavailable" : !ExpiresAt.HasValue ? "No expiry"
            : ExpiresAt.Value <= now ? "Expiry passed · awaiting refresh"
            : $"Expires {ExpiresAt.Value.LocalDateTime:d MMM yyyy, HH:mm} ({Theme.Countdown(ExpiresAt.Value, now)})";
        return $"Reset {index} · {expiry}";
    }
}

internal sealed class UsagePool
{
    public string Id { get; set; } = "codex";
    public string Name { get; set; } = "Codex";
    public string Plan { get; set; } = "Codex";
    public QuotaWindow? FiveHour { get; set; }
    public QuotaWindow? Weekly { get; set; }
    public override string ToString() => Name;
}

internal static class Json
{
    public static JavaScriptSerializer Serializer() => new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 64 };
    public static Dictionary<string, object?> Parse(string text) => Serializer().Deserialize<Dictionary<string, object?>>(text)
        ?? throw new FormatException("Empty response.");
    public static Dictionary<string, object?>? Object(Dictionary<string, object?> parent, string name) =>
        parent.TryGetValue(name, out var value) ? value as Dictionary<string, object?> : null;
    public static string? String(Dictionary<string, object?> parent, string name) =>
        parent.TryGetValue(name, out var value) ? value as string : null;
    public static double? Number(Dictionary<string, object?> parent, string name)
    {
        if (!parent.TryGetValue(name, out var value) || value == null || value is string || value is bool) return null;
        if (!(value is int || value is long || value is decimal || value is double || value is float)) return null;
        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return double.IsNaN(number) || double.IsInfinity(number) ? null : (double?)number;
    }
    public static DateTimeOffset? Timestamp(Dictionary<string, object?> parent, string name)
    {
        var value = Number(parent, name);
        if (!value.HasValue || value < -62135596800d || value > 253402300799d) return null;
        return DateTimeOffset.FromUnixTimeSeconds((long)value.Value);
    }
}

internal static class UsageParser
{
    public static UsageSnapshot Parse(Dictionary<string, object?> result, DateTimeOffset now)
    {
        var buckets = Json.Object(result, "rateLimitsByLimitId");
        var snapshot = new UsageSnapshot { ReadAt = now };
        if (buckets != null && buckets.Count > 0)
        {
            foreach (var entry in buckets.OrderBy(e => e.Key == "codex" ? "" : e.Key, StringComparer.Ordinal))
            {
                if (entry.Key != "codex" && !entry.Key.StartsWith("codex_", StringComparison.Ordinal)) continue;
                if (!(entry.Value is Dictionary<string, object?> bucket)) continue;
                snapshot.Pools.Add(ParsePool(bucket, entry.Key, snapshot.Pools.Count + 1));
            }
        }
        else
        {
            var bucket = Json.Object(result, "rateLimits");
            if (bucket != null && (Json.String(bucket, "limitId") == null || Json.String(bucket, "limitId") == "codex"))
                snapshot.Pools.Add(ParsePool(bucket, "codex", 1));
        }
        if (snapshot.Pools.Count == 0) throw new UsageException("Codex usage is not available for this account.");
        snapshot.SelectPool("codex");
        var resets = Json.Object(result, "rateLimitResetCredits");
        if (resets != null)
        {
            var count = Json.Number(resets, "availableCount");
            if (count >= 0 && count < long.MaxValue && count == Math.Truncate(count.Value)) snapshot.AvailableResets = (long)count.Value;
            if (resets.TryGetValue("credits", out var rows) && rows is IEnumerable array && !(rows is string))
            {
                var credits = array.Cast<object>().OfType<Dictionary<string, object?>>()
                    .Where(c => Json.String(c, "status") == "available" && Json.String(c, "resetType") == "codexRateLimits").ToArray();
                snapshot.ResetDetailsComplete = snapshot.AvailableResets.HasValue && credits.LongLength >= snapshot.AvailableResets.Value;
                snapshot.NextExpiry = credits.Select(c => Json.Timestamp(c, "expiresAt")).Where(t => t.HasValue).OrderBy(t => t).FirstOrDefault();
                snapshot.ResetCredits.AddRange(credits.Select(c => new ResetCredit
                {
                    ExpiresAt = Json.Timestamp(c, "expiresAt"),
                    ExpiryKnown = c.TryGetValue("expiresAt", out var expiry) && (expiry == null || Json.Timestamp(c, "expiresAt").HasValue)
                }).OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue));
            }
        }
        return snapshot;
    }

    private static UsagePool ParsePool(Dictionary<string, object?> bucket, string id, int index)
    {
        var name = Json.String(bucket, "limitName");
        if (string.IsNullOrWhiteSpace(name)) name = id == "codex" ? "Codex" : "Codex limit " + index;
        else name = new string(name!.Where(c => !char.IsControl(c)).Take(80).ToArray());
        var pool = new UsagePool { Id = id, Name = name!, Plan = PlanName(Json.String(bucket, "planType")) };
        foreach (var windowName in new[] { "primary", "secondary" })
        {
            var window = Json.Object(bucket, windowName);
            if (window == null) continue;
            var minutes = Json.Number(window, "windowDurationMins");
            var used = Json.Number(window, "usedPercent");
            if (!used.HasValue || (minutes != 300 && minutes != 10080)) continue;
            var quota = new QuotaWindow
            {
                Minutes = (int)minutes.Value,
                Remaining = Math.Max(0, Math.Min(100, 100 - used.Value)),
                ResetsAt = Json.Timestamp(window, "resetsAt")
            };
            if (quota.Minutes == 300) pool.FiveHour = quota;
            else pool.Weekly = quota;
        }
        return pool;
    }

    private static string PlanName(string? plan)
    {
        switch (plan)
        {
            case "plus": return "Plus";
            case "pro": return "Pro";
            case "prolite": return "Pro Lite";
            case "free": return "Free";
            case "go": return "Go";
            case "team": return "Team";
            case "business": return "Business";
            case "edu": return "Edu";
            case "enterprise": return "Enterprise";
            default: return "Codex";
        }
    }
}

internal sealed class UsageException : Exception
{
    public bool SignInRequired { get; }
    public UsageException(string message, bool signInRequired = false) : base(message) { SignInRequired = signInRequired; }
}

internal sealed class HistoryPoint
{
    public DateTimeOffset Time { get; set; }
    public double? FiveHour { get; set; }
    public double? Weekly { get; set; }
}

internal sealed class UsageHistory
{
    public List<HistoryPoint> Points { get; private set; } = new List<HistoryPoint>();
    private readonly Dictionary<string, List<HistoryPoint>> pools = new Dictionary<string, List<HistoryPoint>>();
    private readonly HistoryStore? store;
    private string? accountKey;
    private string? loadWarning, writeWarning;
    public string? Warning => writeWarning ?? loadWarning;
    public UsageHistory(HistoryStore? store = null) { this.store = store; }

    public async Task AddAsync(UsageSnapshot snapshot)
    {
        if (store != null && snapshot.AccountKey != accountKey && snapshot.AccountKey != null)
        {
            Clear();
            try
            {
                var loaded = await Task.Run(() => store.Load(snapshot.AccountKey, snapshot.ReadAt));
                accountKey = snapshot.AccountKey;
                foreach (var row in loaded.Rows) AddPoint(row.Pool, row.Point, snapshot.ReadAt);
                loadWarning = loaded.SkippedRows ? "Some saved history could not be read." : null;
                if (loaded.SkippedRows) DiagnosticLog.Current?.Write("history.rows_skipped");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                accountKey = snapshot.AccountKey;
                loadWarning = "Saved history could not be opened.";
                DiagnosticLog.Current?.Write("history.load_failed", ex);
            }
        }
        Add(snapshot);
        if (store == null) return;
        if (snapshot.AccountKey == null) { writeWarning = "History is only available for this run."; return; }
        var rows = SnapshotRows(snapshot).ToArray();
        try
        {
            await Task.Run(() => store.Append(snapshot.AccountKey, rows, snapshot.ReadAt));
            writeWarning = null;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            writeWarning = "Usage updated. History could not be saved.";
            DiagnosticLog.Current?.Write("history.save_failed", ex);
        }
    }

    public void Add(UsageSnapshot snapshot)
    {
        if (snapshot.AccountKey != accountKey) Clear();
        accountKey = snapshot.AccountKey;
        foreach (var row in SnapshotRows(snapshot)) AddPoint(row.Pool, row.Point, snapshot.ReadAt);
        SelectPool(snapshot.PoolId);
    }

    private static IEnumerable<HistoryRow> SnapshotRows(UsageSnapshot snapshot)
    {
        var reported = snapshot.Pools.Count > 0 ? snapshot.Pools : new List<UsagePool>
        {
            new UsagePool { Id = snapshot.PoolId, FiveHour = snapshot.FiveHour, Weekly = snapshot.Weekly }
        };
        return reported.Select(pool => new HistoryRow
        {
            Pool = pool.Id,
            Point = new HistoryPoint { Time = DateTimeOffset.FromUnixTimeSeconds(snapshot.ReadAt.ToUnixTimeSeconds()), FiveHour = pool.FiveHour?.Remaining, Weekly = pool.Weekly?.Remaining }
        });
    }

    private void AddPoint(string pool, HistoryPoint point, DateTimeOffset now)
    {
        if (!pools.TryGetValue(pool, out var points)) pools[pool] = points = new List<HistoryPoint>();
        int expired = 0;
        while (expired < points.Count && points[expired].Time < now.AddDays(-30)) expired++;
        if (expired > 0) points.RemoveRange(0, expired);
        if (points.Count > 0 && points[points.Count - 1].Time >= point.Time)
            points.RemoveAll(p => p.Time >= point.Time);
        points.Add(point);
        // Covers thirty days even at the one-minute manual-refresh limit.
        if (points.Count > 43201) points.RemoveRange(0, points.Count - 43201);
    }

    public void SelectPool(string pool)
    {
        if (!pools.TryGetValue(pool, out var points)) pools[pool] = points = new List<HistoryPoint>();
        Points = points;
    }

    public IEnumerable<HistoryPoint> InRange(DateTimeOffset now, int days) => Points.Where(p => p.Time >= now.AddDays(-days) && p.Time <= now);

    public string WeeklyUsageSummary(DateTimeOffset now)
    {
        const string label = "Weekly used (24h): ";
        HistoryPoint? previous = null, first = null, last = null;
        double used = 0;
        int pairs = 0;
        bool partial = false;
        foreach (var point in InRange(now, 1))
        {
            if (!point.Weekly.HasValue) { previous = null; partial = true; continue; }
            if (first == null) first = point;
            if (previous != null)
            {
                var decrease = previous.Weekly!.Value - point.Weekly.Value;
                // An increase can hide consumption around a reset; never subtract it from usage.
                used += Math.Max(0, decrease);
                partial |= decrease < 0 || point.Time - previous.Time > TimeSpan.FromMinutes(15);
                pairs++;
            }
            previous = last = point;
        }
        if (pairs == 0) return label + "collecting history";
        partial |= first!.Time > now.AddDays(-1).AddMinutes(15) || last!.Time < now.AddMinutes(-15);
        return label + $"~{used:0.#}%" + (partial ? " · partial history" : "");
    }

    public void Clear()
    {
        pools.Clear(); Points = new List<HistoryPoint>(); accountKey = null;
        loadWarning = null; writeWarning = null;
    }
}

internal sealed class RefreshPolicy
{
    public DateTimeOffset NextAttempt { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset ManualAllowedAt { get; private set; } = DateTimeOffset.MinValue;
    public int Failures { get; private set; }
    public bool CanRefresh(DateTimeOffset now, bool manual) => now >= (manual ? ManualAllowedAt : NextAttempt);
    public void Started(DateTimeOffset now) { ManualAllowedAt = now.AddMinutes(1); }
    public void Finished(DateTimeOffset now, bool success, double jitter)
    {
        Failures = success ? 0 : Math.Min(Failures + 1, 4);
        double minutes = success ? 5 : Math.Min(30, 5 * Math.Pow(2, Failures - 1));
        NextAttempt = now.AddMinutes(minutes).AddSeconds(Math.Max(0, Math.Min(15, jitter)));
        // Manual refresh cannot bypass failure backoff.
        if (!success) ManualAllowedAt = NextAttempt;
    }
}
