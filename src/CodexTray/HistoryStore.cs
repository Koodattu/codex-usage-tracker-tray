using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexTray;

internal sealed class HistoryRow
{
    public string Pool { get; set; } = "codex";
    public HistoryPoint Point { get; set; } = new HistoryPoint();
}

internal sealed class HistoryLoad
{
    public List<HistoryRow> Rows { get; } = new List<HistoryRow>();
    public bool SkippedRows { get; set; }
}

internal sealed class HistoryStore
{
    private readonly string directory;
    private DateTime lastPruned;
    public HistoryStore(string directory) { this.directory = Path.GetFullPath(directory); }

    private string AccountDirectory(string accountKey)
    {
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(accountKey))).Replace("-", "").ToLowerInvariant();
        return Path.Combine(directory, hash);
    }

    public HistoryLoad Load(string accountKey, DateTimeOffset now)
    {
        var result = new HistoryLoad();
        var accountDirectory = AccountDirectory(accountKey);
        if (!Directory.Exists(accountDirectory)) return result;
        foreach (var file in Directory.GetFiles(accountDirectory, "*.jsonl").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!TryFileDate(file, out var day) || day < now.UtcDateTime.Date.AddDays(-30) || day > now.UtcDateTime.Date) continue;
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var row = Json.Parse(line);
                    var time = Json.Timestamp(row, "timestamp");
                    var pool = Json.String(row, "pool");
                    var five = Json.Number(row, "fiveHourRemaining");
                    var weekly = Json.Number(row, "weeklyRemaining");
                    if (!time.HasValue || pool == null || pool.Length > 128 || (pool != "codex" && !pool.StartsWith("codex_", StringComparison.Ordinal))
                        || !ValidPercent(row, "fiveHourRemaining", five) || !ValidPercent(row, "weeklyRemaining", weekly))
                    { result.SkippedRows = true; continue; }
                    if (time < now.AddDays(-30) || time > now) continue;
                    result.Rows.Add(new HistoryRow { Pool = pool, Point = new HistoryPoint { Time = time.Value, FiveHour = five, Weekly = weekly } });
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException)
                {
                    // A truncated final append must not discard earlier observations.
                    result.SkippedRows = true;
                }
            }
        }
        result.Rows.Sort((a, b) => a.Point.Time.CompareTo(b.Point.Time));
        return result;
    }

    private static bool ValidPercent(Dictionary<string, object?> row, string key, double? value) =>
        row.TryGetValue(key, out var raw) && (raw == null || (value.HasValue && value >= 0 && value <= 100));

    public void Append(string accountKey, HistoryRow[] rows, DateTimeOffset now)
    {
        var accountDirectory = AccountDirectory(accountKey);
        Directory.CreateDirectory(accountDirectory);
        var file = Path.Combine(accountDirectory, now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        using (var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                var last = stream.ReadByte();
                if (last != '\n') stream.WriteByte((byte)'\n');
            }
            stream.Seek(0, SeekOrigin.End);
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
            {
                foreach (var row in rows)
                    writer.WriteLine(Json.Serializer().Serialize(new
                    {
                        timestamp = row.Point.Time.ToUnixTimeSeconds(), pool = row.Pool,
                        fiveHourRemaining = row.Point.FiveHour, weeklyRemaining = row.Point.Weekly
                    }));
                writer.Flush();
            }
            stream.Flush(true);
        }
        if (lastPruned != now.UtcDateTime.Date)
        {
            foreach (var account in Directory.GetDirectories(directory))
            {
                var name = Path.GetFileName(account);
                if (name.Length != 64 || name.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))) continue;
                foreach (var oldFile in Directory.GetFiles(account, "*.jsonl"))
                    if (TryFileDate(oldFile, out var day) && day < now.UtcDateTime.Date.AddDays(-30)) File.Delete(oldFile);
            }
            lastPruned = now.UtcDateTime.Date;
        }
    }

    private static bool TryFileDate(string path, out DateTime date) => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
