using System;

namespace CodexTray;

internal static class QuotaPacing
{
    public static string Describe(UsageSnapshot? snapshot, DateTimeOffset now, bool unavailable, bool compact = false)
    {
        var quota = snapshot?.Weekly;
        if (quota == null) return "";
        if (unavailable || snapshot!.IsStale(now) || !quota.ResetsAt.HasValue || quota.ResetPending(now))
            return compact ? "Unavailable" : "Weekly daily budget unavailable";
        var days = (quota.ResetsAt.Value - now).TotalDays;
        if (days < 1) return compact ? "Reset within 24h" : $"Weekly reset within 24h · {Math.Floor(quota.Remaining):0}% remaining";
        var daily = Math.Floor(quota.Remaining / days * 10) / 10;
        return compact ? $"~{daily:0.0}%" : $"Until reset: ~{daily:0.0}% of weekly allowance/day";
    }
}
