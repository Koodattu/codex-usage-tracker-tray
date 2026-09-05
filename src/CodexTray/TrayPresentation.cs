using System;

namespace CodexTray;

internal enum QuotaKind { Weekly, FiveHour }

internal sealed class TraySelection
{
    public QuotaKind Primary { get; set; }
    public QuotaKind? Secondary { get; set; }
    public bool Rotating { get; set; }

    public static TraySelection Select(UsageSnapshot? snapshot, Settings settings, double elapsedSeconds)
    {
        bool five = snapshot?.FiveHour != null, weekly = snapshot?.Weekly != null;
        if (five && weekly)
        {
            if (settings.IconVisibility == "both") return new TraySelection { Primary = QuotaKind.Weekly, Secondary = QuotaKind.FiveHour };
            if (settings.IconVisibility == "rotate") return new TraySelection
            {
                Primary = (long)(elapsedSeconds / settings.RotationSeconds) % 2 == 0 ? QuotaKind.Weekly : QuotaKind.FiveHour,
                Rotating = true
            };
            return new TraySelection { Primary = settings.IconVisibility == "5h" ? QuotaKind.FiveHour : QuotaKind.Weekly };
        }
        // Keep one reachable icon during loading/errors and fall back to the available window.
        return new TraySelection { Primary = five ? QuotaKind.FiveHour : QuotaKind.Weekly };
    }

    public static QuotaWindow? Window(UsageSnapshot? snapshot, QuotaKind kind) => kind == QuotaKind.Weekly ? snapshot?.Weekly : snapshot?.FiveHour;
    public static string Label(QuotaKind kind) => kind == QuotaKind.Weekly ? "Weekly" : "5-hour";
}
