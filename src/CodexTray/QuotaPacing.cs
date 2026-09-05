using System;

namespace CodexTray;

internal static class QuotaPacing
{
    public static string Describe(UsageSnapshot? snapshot, DateTimeOffset now, bool unavailable)
    {
        var quota = snapshot?.Weekly;
        if (quota == null) return "";
        if (unavailable || snapshot!.IsStale(now) || !quota.ResetsAt.HasValue || quota.ResetPending(now))
            return "Weekly daily budget unavailable";
        var days = (quota.ResetsAt.Value - now).TotalDays;
        if (days < 1) return $"Weekly reset within 24h · {Math.Floor(quota.Remaining):0}% remaining";
        var daily = Math.Floor(quota.Remaining / days * 10) / 10;
        return $"Until reset: ~{daily:0.0}% of weekly allowance/day";
    }
}
