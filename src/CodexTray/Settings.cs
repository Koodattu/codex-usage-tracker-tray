using System;
using System.IO;

namespace CodexTray;

internal sealed class Settings
{
    public string DisplayMode { get; set; } = "numbers";
    public string IconVisibility { get; set; } = "weekly";
    public int RotationSeconds { get; set; } = 10;
    public string UsagePool { get; set; } = "codex";
    public int ChartDays { get; set; } = 1;
    public bool LowQuotaAlerts { get; set; }
    public bool RestoredAlerts { get; set; }
    public bool ExpiryAlerts { get; set; }
    public int WarningPercent { get; set; } = 20;
    public int CriticalPercent { get; set; } = 10;
    public string? CodexPath { get; set; }
    public string? DesktopPath { get; set; }
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTray");

    public static Settings Load()
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new Settings();
        try
        {
            var result = Json.Serializer().Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
            if (result.DisplayMode != "numbers" && result.DisplayMode != "rings") result.DisplayMode = "numbers";
            if (result.IconVisibility != "both" && result.IconVisibility != "5h" && result.IconVisibility != "weekly" && result.IconVisibility != "rotate") result.IconVisibility = "weekly";
            if (result.RotationSeconds < 5 || result.RotationSeconds > 300) result.RotationSeconds = 10;
            if (result.ChartDays != 1 && result.ChartDays != 7 && result.ChartDays != 30) result.ChartDays = 1;
            if (result.WarningPercent < 2 || result.WarningPercent > 99) result.WarningPercent = 20;
            if (result.CriticalPercent < 1 || result.CriticalPercent >= result.WarningPercent) result.CriticalPercent = Math.Min(10, result.WarningPercent - 1);
            return result;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)
        {
            LoadFailed = true;
            return new Settings();
        }
    }

    public static bool LoadFailed { get; private set; }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "settings.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Json.Serializer().Serialize(this));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
}
