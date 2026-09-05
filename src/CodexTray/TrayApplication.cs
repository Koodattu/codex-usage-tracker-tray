using System;
using System.ComponentModel;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexTray;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly Settings settings = Settings.Load();
    private readonly UsageHistory history = new UsageHistory(new HistoryStore(Path.Combine(Settings.DirectoryPath, "history")));
    private readonly RefreshPolicy policy = new RefreshPolicy();
    private readonly CodexClient client = new CodexClient();
    private readonly QuotaNotifications notifications = new QuotaNotifications(Path.Combine(Settings.DirectoryPath, "alerts.json"));
    private readonly ReleaseUpdates updates = new ReleaseUpdates();
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15000 };
    private readonly NotifyIcon primaryIcon = new NotifyIcon();
    private readonly NotifyIcon secondaryIcon = new NotifyIcon();
    private readonly DpiMenu menu = new DpiMenu();
    private readonly System.Windows.Forms.Timer rotationTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    private readonly Stopwatch rotationClock = Stopwatch.StartNew();
    private readonly PopupForm popup;
    private readonly Random random = new Random();
    private UsageSnapshot? snapshot;
    private bool busy, failed, paused, exiting;
    private string message = "Connecting to Codex…";
    private string iconKey = "";

    public TrayApplication(bool background)
    {
        popup = new PopupForm(history, settings.ChartDays);
        // Establish the WinForms synchronization context before asynchronous reads.
        _ = popup.Handle;
        popup.RefreshRequested += async (_, __) => await RefreshAsync(true);
        popup.DesktopRequested += (_, __) => RunAction(() => WindowsIntegration.OpenDesktop(settings.DesktopPath));
        popup.PoolSelected += SelectPool;
        popup.ChartRangeSelected += days => SaveSetting(() => settings.ChartDays = days);
        popup.SettingsRequested += (_, __) => ShowSettings();
        popup.MenuRequested += control =>
        {
            popup.KeepOpen = true;
            menu.Show(control, new Point(0, control.Height));
        };
        menu.Closed += (_, e) =>
        {
            popup.KeepOpen = false;
            if (e.CloseReason == ToolStripDropDownCloseReason.AppClicked && !popup.ContainsFocus) popup.Hide();
        };
        foreach (var icon in new[] { primaryIcon, secondaryIcon })
        {
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };
        }
        menu.Opening += (_, __) =>
        {
            BuildMenu();
            menu.SetDpi(menu.SourceControl != null ? DpiLayout.WindowDpi(popup.Handle) : DpiLayout.TrayDpi);
        };
        rotationTimer.Tick += (_, __) => UpdateView();
        timer.Tick += async (_, __) => { UpdateView(); if (!paused) await RefreshAsync(false); };
        UpdateView();
        EventHandler? start = null;
        start = async (_, __) =>
        {
            Application.Idle -= start;
            if (Settings.LoadFailed) Notify("Saved preferences could not be read. Default display settings are in use.");
            try
            {
                if (background) await Task.Delay(TimeSpan.FromSeconds(20), lifetime.Token);
                if (exiting) return;
                timer.Start();
                await RefreshAsync(false);
            }
            catch (OperationCanceledException) when (exiting) { }
        };
        Application.Idle += start;
    }

    private async Task RefreshAsync(bool manual)
    {
        if (exiting || busy || !policy.CanRefresh(DateTimeOffset.UtcNow, manual)) return;
        DiagnosticLog.Current?.Write(manual ? "refresh.started_manual" : "refresh.started_automatic");
        busy = true;
        policy.Started(DateTimeOffset.UtcNow);
        message = "Refreshing usage…";
        UpdateView();
        var success = false;
        try
        {
            var executable = WindowsIntegration.FindCodex(settings.CodexPath);
            if (executable == null) throw new UsageException("Codex was not found. Choose its executable from the tray menu.");
            var value = await client.ReadAsync(executable, lifetime.Token);
            if (exiting) return;
            value.SelectPool(settings.UsagePool);
            snapshot = value;
            await history.AddAsync(value);
            if (exiting) return;
            DiagnosticLog.Current?.Write("refresh.history_complete");
            success = true;
            failed = false;
            var alert = notifications.Evaluate(value, settings, DateTimeOffset.UtcNow);
            if (alert != null) Notify(alert);
            message = history.Warning ?? notifications.Warning ?? (value.FiveHour == null && value.Weekly == null ? "Codex did not provide 5-hour or weekly limits." : "Connected to Codex");
        }
        catch (OperationCanceledException) when (exiting) { DiagnosticLog.Current?.Write("refresh.cancelled_shutdown"); return; }
        catch (UsageException ex)
        {
            DiagnosticLog.Current?.Write(ex.SignInRequired ? "refresh.sign_in_required" : "refresh.failed", ex);
            failed = true;
            message = ex.Message;
            if (ex.SignInRequired) { snapshot = null; history.Clear(); }
        }
        finally
        {
            busy = false;
            if (!exiting)
            {
                policy.Finished(DateTimeOffset.UtcNow, success, random.NextDouble() * 15);
                UpdateView();
                DiagnosticLog.Current?.Write(success ? "refresh.completed" : "refresh.backoff");
            }
        }
    }

    private void UpdateView()
    {
        if (exiting) return;
        var now = DateTimeOffset.UtcNow;
        var stale = failed || paused || snapshot?.IsStale(now) == true;
        var selection = TraySelection.Select(snapshot, settings, rotationClock.Elapsed.TotalSeconds);
        var primary = TraySelection.Window(snapshot, selection.Primary);
        var secondary = selection.Secondary.HasValue ? TraySelection.Window(snapshot, selection.Secondary.Value) : null;
        var primaryStale = stale || primary?.ResetPending(now) == true;
        var secondaryStale = stale || secondary?.ResetPending(now) == true;
        var size = DpiLayout.TrayIconSize;
        var key = $"{settings.DisplayMode}:{selection.Primary}:{selection.Secondary}:{primary?.Remaining}:{secondary?.Remaining}:{primaryStale}:{secondaryStale}:{size}";
        if (key != iconKey)
        {
            ReplaceIcon(primaryIcon, TrayIconRenderer.Create(primary, settings.DisplayMode, primaryStale, size));
            if (selection.Secondary.HasValue) ReplaceIcon(secondaryIcon, TrayIconRenderer.Create(secondary, settings.DisplayMode, secondaryStale, size));
            iconKey = key;
        }
        var poolName = snapshot?.Pools.Find(p => p.Id == snapshot.PoolId)?.Name ?? "Codex";
        primaryIcon.Text = Tooltip(TraySelection.Label(selection.Primary), primary, primaryStale, now, poolName);
        if (selection.Secondary.HasValue) secondaryIcon.Text = Tooltip(TraySelection.Label(selection.Secondary.Value), secondary, secondaryStale, now, poolName);
        primaryIcon.Visible = true;
        secondaryIcon.Visible = selection.Secondary.HasValue;
        rotationTimer.Enabled = selection.Rotating;
        popup.UpdateUsage(snapshot, paused ? "Automatic refresh paused" : message, busy, failed || paused, paused ? DateTimeOffset.MinValue : policy.NextAttempt, policy.CanRefresh(now, true));
    }

    private static string Tooltip(string label, QuotaWindow? quota, bool stale, DateTimeOffset now, string poolName)
    {
        var value = quota == null ? "unavailable" : $"{Math.Floor(quota.Remaining):0}% left";
        var reset = quota?.ResetsAt == null ? "" : quota.ResetPending(now) ? " · reset due" : " · resets in " + Theme.Countdown(quota.ResetsAt.Value, now);
        var text = $"{label}: {value}{reset}" + (stale ? " · last known" : "") + " · " + poolName;
        return text.Length > 63 ? text.Substring(0, 63) : text;
    }

    private static void ReplaceIcon(NotifyIcon target, Icon icon)
    {
        var old = target.Icon;
        target.Icon = icon;
        old?.Dispose();
    }

    private void TogglePopup()
    {
        DiagnosticLog.Current?.Write(popup.Visible ? "popup.hide" : "popup.show");
        if (popup.Visible) popup.Hide(); else popup.ShowNearTray();
    }

    private void BuildMenu()
    {
        DiagnosticLog.Current?.Write("menu.opening");
        while (menu.Items.Count > 0) { var item = menu.Items[0]; menu.Items.RemoveAt(0); item.Dispose(); }
        menu.Items.Add("Show usage", null, (_, __) => popup.ShowNearTray());
        var refresh = menu.Items.Add(busy ? "Refreshing…" : "Refresh now", null, async (_, __) => await RefreshAsync(true));
        refresh.Enabled = !busy && policy.CanRefresh(DateTimeOffset.UtcNow, true);
        menu.Items.Add(new ToolStripSeparator());
        if (snapshot != null && snapshot.Pools.Count > 1)
        {
            var pools = new ToolStripMenuItem("Usage pool");
            foreach (var pool in snapshot.Pools) AddChoice(pools, pool.Name, snapshot.PoolId == pool.Id, () => SelectPool(pool.Id));
            menu.Items.Add(pools);
        }
        var display = new ToolStripMenuItem("Tray display");
        AddChoice(display, "Numbers", settings.DisplayMode == "numbers", () => SaveSetting(() => settings.DisplayMode = "numbers"));
        AddChoice(display, "Rings", settings.DisplayMode == "rings", () => SaveSetting(() => settings.DisplayMode = "rings"));
        menu.Items.Add(display);
        var visible = new ToolStripMenuItem("Show in tray");
        AddChoice(visible, "One icon · weekly", settings.IconVisibility == "weekly", () => SaveSetting(() => settings.IconVisibility = "weekly"));
        AddChoice(visible, "One icon · 5-hour", settings.IconVisibility == "5h", () => SaveSetting(() => settings.IconVisibility = "5h"));
        AddChoice(visible, $"One icon · switch every {settings.RotationSeconds}s", settings.IconVisibility == "rotate", () => SaveSetting(() => settings.IconVisibility = "rotate"));
        AddChoice(visible, "Two icons · one for each limit", settings.IconVisibility == "both", () => SaveSetting(() => settings.IconVisibility = "both"));
        menu.Items.Add(visible);
        menu.Items.Add("Settings…", null, (_, __) => ShowSettings());
        menu.Items.Add("About / Check for updates…", null, (_, __) => ShowSettings(true));
        menu.Items.Add("Open logs", null, (_, __) => OpenLogs());
        menu.Items.Add(new ToolStripMenuItem("Pause automatic refresh", null, (_, __) => { paused = !paused; UpdateView(); }) { Checked = paused });
        try
        {
            var startup = WindowsIntegration.StartsWithWindows();
            menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, __) => RunAction(() => WindowsIntegration.SetStartWithWindows(!startup))) { Checked = startup });
        }
        catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException)
        {
            menu.Items.Add(new ToolStripMenuItem("Start with Windows (unavailable)") { Enabled = false });
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Codex desktop", null, (_, __) => RunAction(() => WindowsIntegration.OpenDesktop(settings.DesktopPath)));
        menu.Items.Add("ChatGPT in Microsoft Store", null, (_, __) => RunAction(WindowsIntegration.OpenStore));
        var setup = new ToolStripMenuItem("Setup");
        setup.DropDownItems.Add("Choose Codex CLI executable…", null, (_, __) => ChooseExecutable(false));
        setup.DropDownItems.Add("Choose Codex desktop app…", null, (_, __) => ChooseExecutable(true));
        setup.DropDownItems.Add("Use automatic detection", null, (_, __) => SaveSetting(() => { settings.CodexPath = null; settings.DesktopPath = null; }));
        menu.Items.Add(setup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, __) => ExitThread());
    }

    private static void AddChoice(ToolStripMenuItem parent, string label, bool selected, Action action) =>
        parent.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, __) => action()) { Checked = selected });

    private void ChooseExecutable(bool desktop)
    {
        using var dialog = new OpenFileDialog { Title = desktop ? "Choose the Codex desktop application" : "Choose the Codex command-line executable", Filter = "Windows application (*.exe)|*.exe", CheckFileExists = true };
        popup.KeepOpen = true;
        try
        {
            if (dialog.ShowDialog(popup) != DialogResult.OK) return;
            SaveSetting(() => { if (desktop) settings.DesktopPath = dialog.FileName; else settings.CodexPath = dialog.FileName; });
        }
        finally { popup.KeepOpen = false; }
    }

    private void SaveSetting(Action change)
    {
        change();
        rotationClock.Restart();
        RunAction(settings.Save);
        UpdateView();
    }
    private void SelectPool(string id)
    {
        if (snapshot?.PoolId == id) return;
        settings.UsagePool = id;
        rotationClock.Restart();
        snapshot?.SelectPool(id);
        history.SelectPool(snapshot?.PoolId ?? id);
        RunAction(settings.Save);
        UpdateView();
    }
    private void ShowSettings(bool about = false)
    {
        DiagnosticLog.Current?.Write("settings.opening");
        menu.Close();
        popup.KeepOpen = true;
        try
        {
            bool? startup = null;
            RunAction(() => startup = WindowsIntegration.StartsWithWindows());
            using var dialog = new SettingsForm(settings, startup, updates);
            dialog.ReleasesRequested += (_, __) => RunAction(() => Process.Start(new ProcessStartInfo(ReleaseUpdates.ReleasesUrl) { UseShellExecute = true }));
            dialog.LogsRequested += (_, __) => OpenLogs();
            if (about) dialog.ShowAbout();
            if (dialog.ShowFor(popup) != DialogResult.OK) return;
            SaveSetting(() =>
            {
                settings.DisplayMode = dialog.DisplayMode;
                settings.IconVisibility = dialog.IconVisibility;
                settings.RotationSeconds = dialog.RotationSeconds;
                settings.LowQuotaAlerts = dialog.LowQuotaAlerts;
                settings.RestoredAlerts = dialog.RestoredAlerts;
                settings.ExpiryAlerts = dialog.ExpiryAlerts;
                settings.WarningPercent = dialog.WarningPercent;
                settings.CriticalPercent = dialog.CriticalPercent;
            });
            if (startup.HasValue && startup.Value != dialog.StartWithWindows)
                RunAction(() => WindowsIntegration.SetStartWithWindows(dialog.StartWithWindows));
        }
        finally { DiagnosticLog.Current?.Write("settings.closed"); popup.KeepOpen = false; if (popup.Visible) popup.Activate(); }
    }
    private void OpenLogs() => RunAction(() =>
    {
        Directory.CreateDirectory(DiagnosticLog.DirectoryPath);
        Process.Start(new ProcessStartInfo(DiagnosticLog.DirectoryPath) { UseShellExecute = true });
    });
    private void RunAction(Action action)
    {
        try { action(); }
        catch (UsageException ex) { DiagnosticLog.Current?.Write("action.failed", ex); Notify(ex.Message); }
        catch (Exception ex) when (ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is ArgumentException || ex is System.Runtime.InteropServices.COMException || ex is System.Reflection.TargetInvocationException)
        {
            DiagnosticLog.Current?.Write("action.failed", ex);
            Notify("Windows could not complete that action. Check the selected app path or your permissions.");
        }
    }
    private void Notify(string text)
    {
        primaryIcon.ShowBalloonTip(5000, "Codex Tray", text, ToolTipIcon.Info);
    }

    protected override void ExitThreadCore()
    {
        DiagnosticLog.Current?.Write("app.stopping");
        exiting = true;
        lifetime.Cancel();
        timer.Stop();
        rotationTimer.Stop();
        primaryIcon.Visible = false;
        secondaryIcon.Visible = false;
        popup.Close();
        base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!exiting) { exiting = true; lifetime.Cancel(); }
            timer.Dispose();
            rotationTimer.Dispose();
            primaryIcon.Icon?.Dispose();
            secondaryIcon.Icon?.Dispose();
            primaryIcon.Dispose();
            secondaryIcon.Dispose();
            menu.Dispose();
            popup.Dispose();
            updates.Dispose();
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
