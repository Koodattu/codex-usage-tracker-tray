using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexTray;

internal sealed class AlertObservation
{
    public double Remaining { get; set; }
    public long ReadAt { get; set; }
}

internal sealed class AlertState
{
    public Dictionary<string, long> Sent { get; set; } = new Dictionary<string, long>();
    public Dictionary<string, AlertObservation> Observed { get; set; } = new Dictionary<string, AlertObservation>();
}

internal sealed class QuotaNotifications
{
    private readonly string path;
    private AlertState state = new AlertState();
    private bool unreadable;
    public string? Warning { get; private set; }

    public QuotaNotifications(string path)
    {
        this.path = path;
        try
        {
            if (File.Exists(path))
                state = Json.Serializer().Deserialize<AlertState>(File.ReadAllText(path)) ?? throw new FormatException();
            if (state.Sent == null || state.Observed == null || state.Observed.Values.Any(v => v == null)) throw new FormatException();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException || ex is FormatException)
        {
            unreadable = true;
        }
    }

    public string? Evaluate(UsageSnapshot snapshot, Settings settings, DateTimeOffset now)
    {
        Warning = null;
        if (!settings.LowQuotaAlerts && !settings.RestoredAlerts && !settings.ExpiryAlerts) return null;
        if (unreadable) { Warning = "Saved alert state unavailable. Notifications paused."; return null; }
        if (snapshot.AccountKey == null || snapshot.IsStale(now) || snapshot.ReadAt > now) return null;
        // Work on a copy: failed saves must not consume an alert or advance its observation.
        var next = Json.Serializer().Deserialize<AlertState>(Json.Serializer().Serialize(state))!;
        var cutoff = now.AddDays(-30).ToUnixTimeSeconds();
        foreach (var key in next.Sent.Where(p => p.Value < cutoff).Select(p => p.Key).ToArray()) next.Sent.Remove(key);
        foreach (var key in next.Observed.Where(p => p.Value.ReadAt < cutoff).Select(p => p.Key).ToArray()) next.Observed.Remove(key);
        var messages = new List<string>();
        void Window(QuotaWindow? quota, string label)
        {
            if (quota == null || !quota.ResetsAt.HasValue || quota.ResetPending(now)) return;
            var scope = snapshot.AccountKey + "|" + snapshot.PoolId + "|" + label;
            var observationKey = Hash(scope);
            next.Observed.TryGetValue(observationKey, out var previous);
            if (previous != null && previous.ReadAt >= snapshot.ReadAt.ToUnixTimeSeconds()) return;
            var cycle = scope + "|" + quota.ResetsAt.Value.ToUnixTimeSeconds();
            bool Mark(string key)
            {
                key = Hash(key);
                if (next.Sent.ContainsKey(key)) return false;
                next.Sent[key] = now.ToUnixTimeSeconds(); return true;
            }
            if (settings.LowQuotaAlerts)
            {
                var alert = false;
                foreach (var threshold in new[] { settings.WarningPercent, settings.CriticalPercent }.Distinct())
                    if (quota.Remaining <= threshold) alert |= Mark(cycle + "|low|" + threshold);
                if (alert) messages.Add($"{label}: {Math.Floor(quota.Remaining):0}% remaining.");
            }
            if (settings.RestoredAlerts && previous?.Remaining <= 0 && quota.Remaining > 0 && Mark(cycle + "|restored"))
                messages.Add($"{label}: allowance available again ({Math.Floor(quota.Remaining):0}%).");
            next.Observed[observationKey] = new AlertObservation { Remaining = quota.Remaining, ReadAt = snapshot.ReadAt.ToUnixTimeSeconds() };
        }
        Window(snapshot.FiveHour, "5-hour");
        Window(snapshot.Weekly, "Weekly");
        if (settings.ExpiryAlerts && snapshot.AvailableResets > 0)
        {
            var expiring = 0;
            foreach (var group in snapshot.ResetCredits.Where(c => c.ExpiryKnown && c.ExpiresAt > now && c.ExpiresAt <= now.AddHours(24)).GroupBy(c => c.ExpiresAt!.Value.ToUnixTimeSeconds()))
            {
                var key = Hash(snapshot.AccountKey + "|expiry|" + group.Key);
                if (next.Sent.ContainsKey(key)) continue;
                next.Sent[key] = now.ToUnixTimeSeconds(); expiring += group.Count();
            }
            if (expiring > 0) messages.Add(expiring == 1 ? "A banked reset expires within 24 hours." : $"{expiring} banked resets expire within 24 hours.");
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Json.Serializer().Serialize(next));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            state = next;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Warning = "Alert state could not be saved. Notifications paused.";
            return null;
        }
        if (messages.Count == 0) return null;
        var pool = snapshot.Pools.FirstOrDefault(p => p.Id == snapshot.PoolId)?.Name ?? "Codex";
        return pool + "\n" + string.Join("\n", messages);
    }

    private static string Hash(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }
}
