using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexTray;

// A stable, selected-pool snapshot: background refresh cannot move rows while someone reads or exports.
internal sealed class HistoryReadings
{
    private readonly HistoryPoint[] points;
    public string PoolId { get; }
    public DateTimeOffset CapturedAt { get; }

    public HistoryReadings(UsageHistory history, string poolId, DateTimeOffset now)
    {
        PoolId = poolId;
        CapturedAt = now;
        points = history.InRange(now, 30).OrderByDescending(p => p.Time).Select(p =>
            new HistoryPoint { Time = p.Time, FiveHour = p.FiveHour, Weekly = p.Weekly }).ToArray();
    }

    public IReadOnlyList<HistoryPoint> InRange(int days) => points.Where(p => p.Time >= CapturedAt.AddDays(-days)).ToArray();

    public void WriteCsv(TextWriter writer, int days)
    {
        writer.Write("recorded_at_utc,pool_id,five_hour_remaining_percent,weekly_remaining_percent\r\n");
        foreach (var point in InRange(days).Reverse())
        {
            writer.Write(point.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.Write(","); writer.Write(CsvField(PoolId)); writer.Write(",");
            writer.Write(point.FiveHour?.ToString("R", CultureInfo.InvariantCulture)); writer.Write(",");
            writer.Write(point.Weekly?.ToString("R", CultureInfo.InvariantCulture)); writer.Write("\r\n");
        }
    }

    public void SaveCsv(string path, int days)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(true))) WriteCsv(writer, days);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string CsvField(string text) => text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
        ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
}
