using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CodexTray;

internal sealed class PopupForm : Form
{
    private static readonly RectangleF ChartBounds = new RectangleF(49, 269, 365, 91);
    internal HistoryPoint? HoveredChartPoint { get; private set; }
    private readonly Button refresh = MakeButton("Refresh", true);
    private readonly Button desktop = MakeButton("Open Codex", false);
    private readonly Button close = MakeButton("×", false);
    private readonly Button menuButton = MakeButton("⋯", false);
    private readonly Button settingsButton = MakeButton("⚙", false);
    private readonly Button dayRange = MakeButton("24h", false);
    private readonly Button weekRange = MakeButton("7d", false);
    private readonly Button monthRange = MakeButton("30d", false);
    private readonly ListBox resetList = new ListBox { BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Muted, IntegralHeight = false, AccessibleName = "Banked reset expiry times" };
    private readonly ToolTip hints = new ToolTip();
    private readonly Button poolSelector = MakeButton("Usage pool ▾", false);
    private readonly DpiMenu poolMenu = new DpiMenu();
    private readonly UsageHistory history;
    private UsageSnapshot? snapshot;
    private string status = "Connecting to Codex…";
    private DateTimeOffset nextAttempt;
    private bool busy;
    private bool failed;
    public event EventHandler? RefreshRequested;
    public event EventHandler? DesktopRequested;
    public event Action<string>? PoolSelected;
    public event Action<Control>? MenuRequested;
    public event EventHandler? SettingsRequested;
    public event Action<int>? ChartRangeSelected;
    public int ChartDays { get; private set; }
    public bool KeepOpen { get; set; }

    public PopupForm(UsageHistory history, int chartDays = 1)
    {
        this.history = history;
        ChartDays = chartDays;
        Text = "Codex usage";
        AccessibleName = "Codex usage remaining";
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        ClientSize = new Size(440, 636);
        Controls.AddRange(new Control[] { refresh, desktop, close, poolSelector, menuButton, settingsButton, dayRange, weekRange, monthRange, resetList });
        menuButton.AccessibleName = "Open menu";
        settingsButton.AccessibleName = "Settings";
        menuButton.TabIndex = 0; settingsButton.TabIndex = 1; close.TabIndex = 2;
        poolSelector.TabIndex = 3; dayRange.TabIndex = 4; weekRange.TabIndex = 5; monthRange.TabIndex = 6;
        resetList.TabIndex = 7; refresh.TabIndex = 8; desktop.TabIndex = 9;
        dayRange.AccessibleName = "Past 24 hours"; weekRange.AccessibleName = "Past 7 days"; monthRange.AccessibleName = "Past 30 days";
        dayRange.Click += (_, __) => SelectRange(1);
        weekRange.Click += (_, __) => SelectRange(7);
        monthRange.Click += (_, __) => SelectRange(30);
        UpdateRangeButtons();
        hints.SetToolTip(menuButton, "Open menu");
        hints.SetToolTip(settingsButton, "Settings");
        menuButton.Click += (_, __) => MenuRequested?.Invoke(menuButton);
        settingsButton.Click += (_, __) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        poolSelector.AccessibleName = "Select Codex usage pool";
        poolSelector.AutoEllipsis = true;
        poolSelector.Click += (_, __) =>
        {
            while (poolMenu.Items.Count > 0) { var item = poolMenu.Items[0]; poolMenu.Items.RemoveAt(0); item.Dispose(); }
            if (snapshot == null) return;
            foreach (var pool in snapshot.Pools)
                poolMenu.Items.Add(new ToolStripMenuItem(pool.Name, null, (_, ___) => PoolSelected?.Invoke(pool.Id)) { Checked = pool.Id == snapshot.PoolId });
            poolMenu.SetDpi(DpiLayout.WindowDpi(Handle));
            poolMenu.Show(poolSelector, new Point(0, poolSelector.Height));
        };
        refresh.Click += (_, __) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        desktop.Click += (_, __) => DesktopRequested?.Invoke(this, EventArgs.Empty);
        close.AccessibleName = "Close usage view";
        close.Click += (_, __) => Hide();
        Deactivate += (_, __) => { if (!KeepOpen) Hide(); };
        LayoutButtons();
    }

    public void UpdateUsage(UsageSnapshot? value, string message, bool refreshing, bool error, DateTimeOffset next, bool canRefresh)
    {
        snapshot = value;
        status = message;
        busy = refreshing;
        failed = error;
        nextAttempt = next;
        poolSelector.Text = (value?.Pools.FirstOrDefault(p => p.Id == value.PoolId)?.Name ?? "Usage pool") + " ▾";
        poolSelector.Visible = value != null && value.Pools.Count > 1;
        refresh.Text = busy ? "Refreshing…" : "Refresh";
        refresh.Enabled = canRefresh && !busy;
        AccessibleDescription = (value?.FiveHour == null ? "" : $"5-hour remaining: {Percent(value.FiveHour)}. ")
            + (value?.Weekly == null ? "" : $"Weekly remaining: {Percent(value.Weekly)}. " + history.WeeklyUsageSummary(DateTimeOffset.UtcNow) + ". ") + message + ". " + QuotaPacing.Describe(value, DateTimeOffset.UtcNow, error);
        UpdateResetList();
        if (HoveredChartPoint != null && !history.Points.Contains(HoveredChartPoint)) ClearChartHover();
        Invalidate();
    }

    private void SelectRange(int days)
    {
        if (ChartDays == days) return;
        ChartDays = days;
        ClearChartHover();
        UpdateRangeButtons();
        ChartRangeSelected?.Invoke(days);
        Invalidate();
    }

    private void UpdateRangeButtons()
    {
        foreach (var button in new[] { dayRange, weekRange, monthRange })
        {
            bool selected = (button == dayRange ? 1 : button == weekRange ? 7 : 30) == ChartDays;
            button.BackColor = selected ? Theme.Mint : Theme.Card;
            button.ForeColor = selected ? Theme.Background : Theme.Muted;
            button.FlatAppearance.MouseOverBackColor = selected ? Color.FromArgb(127, 232, 193) : Theme.Line;
        }
    }

    private void UpdateResetList()
    {
        var now = DateTimeOffset.UtcNow;
        var items = snapshot?.ResetCredits.Select((credit, index) => credit.Display(index + 1, now)).ToList() ?? new System.Collections.Generic.List<string>();
        if (snapshot?.AvailableResets == null) items.Add("Codex has not provided reset details.");
        else if (snapshot.AvailableResets == 0) items.Add("No earned resets available right now.");
        else if (items.Count < snapshot.AvailableResets)
        {
            var missing = snapshot.AvailableResets.Value - items.Count;
            items.Add(missing == 1 ? "Expiry unavailable for 1 more reset." : $"Expiry unavailable for {missing} more resets.");
        }
        if (items.SequenceEqual(resetList.Items.Cast<string>())) return;
        var top = resetList.TopIndex;
        var selected = resetList.SelectedIndex;
        resetList.BeginUpdate();
        resetList.Items.Clear();
        resetList.Items.AddRange(items.Cast<object>().ToArray());
        if (resetList.Items.Count > 0)
        {
            resetList.TopIndex = Math.Min(top, resetList.Items.Count - 1);
            resetList.SelectedIndex = Math.Min(selected, resetList.Items.Count - 1);
        }
        resetList.EndUpdate();
    }

    public void ShowNearTray()
    {
        DpiLayout.Place(this, Cursor.Position, new Size(440, 636), true);
        Show();
        Activate();
    }

    protected override void WndProc(ref Message m)
    {
        if (!DpiLayout.HandleDpiChange(this, ref m, new Size(440, 636))) base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ClearChartHover(); LayoutButtons(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = ChartPointAt(e.Location, DateTimeOffset.UtcNow);
        if (ReferenceEquals(point, HoveredChartPoint)) return;
        HoveredChartPoint = point;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); ClearChartHover(); }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) ClearChartHover();
    }

    private void ClearChartHover()
    {
        if (HoveredChartPoint == null) return;
        HoveredChartPoint = null;
        Invalidate();
    }

    internal HistoryPoint? ChartPointAt(Point location, DateTimeOffset now)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return null;
        var logical = new PointF(location.X * 440f / ClientSize.Width, location.Y * 636f / ClientSize.Height);
        if (!ChartBounds.Contains(logical)) return null;
        HistoryPoint? nearest = null;
        // Snap to a recorded sample near the pointer; do not invent values across history gaps.
        float distance = 12;
        foreach (var point in history.InRange(now, ChartDays))
        {
            if (!(snapshot?.FiveHour != null && point.FiveHour.HasValue) && !(snapshot?.Weekly != null && point.Weekly.HasValue)) continue;
            var candidate = Math.Abs(ChartX(point, now) - logical.X);
            if (candidate > distance) continue;
            distance = candidate;
            nearest = point;
        }
        return nearest;
    }

    private float ChartX(HistoryPoint point, DateTimeOffset now) =>
        ChartBounds.Left + (float)((point.Time - now.AddDays(-ChartDays)).TotalDays / ChartDays) * ChartBounds.Width;
    private void LayoutButtons()
    {
        if (refresh == null) return;
        var scale = ClientSize.Width / 440f;
        refresh.Bounds = Scale(new Rectangle(24, 574, 186, 38), scale);
        desktop.Bounds = Scale(new Rectangle(222, 574, 194, 38), scale);
        close.Bounds = Scale(new Rectangle(384, 18, 32, 30), scale);
        settingsButton.Bounds = Scale(new Rectangle(344, 18, 32, 30), scale);
        menuButton.Bounds = Scale(new Rectangle(304, 18, 32, 30), scale);
        poolSelector.Bounds = Scale(new Rectangle(226, 53, 190, 25), scale);
        dayRange.Bounds = Scale(new Rectangle(266, 230, 46, 25), scale);
        weekRange.Bounds = Scale(new Rectangle(316, 230, 46, 25), scale);
        monthRange.Bounds = Scale(new Rectangle(366, 230, 50, 25), scale);
        resetList.Bounds = Scale(new Rectangle(40, 432, 360, 69), scale);
        Theme.SetFont(resetList, 12 * scale);
        Theme.SetFont(poolSelector, 12 * scale);
        foreach (var button in new[] { refresh, desktop, close, menuButton, settingsButton, dayRange, weekRange, monthRange })
            Theme.SetFont(button, (button == menuButton || button == settingsButton ? 20 : 13) * scale);
    }
    private static Rectangle Scale(Rectangle r, float s) => new Rectangle((int)(r.X * s), (int)(r.Y * s), (int)(r.Width * s), (int)(r.Height * s));
    private static Button MakeButton(string text, bool primary)
    {
        var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Theme.Mint : Theme.Card, ForeColor = primary ? Theme.Background : Theme.Text, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 13, GraphicsUnit.Pixel) };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(127, 232, 193) : Theme.Line;
        return button;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.ScaleTransform(ClientSize.Width / 440f, ClientSize.Height / 636f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var now = DateTimeOffset.UtcNow;
        using var border = new Pen(Theme.Line);
        g.DrawRectangle(border, 0, 0, 439, 635);
        Theme.Label(g, "Codex", 27, Theme.Text, new RectangleF(24, 18, 160, 37), FontStyle.Bold);
        Theme.Label(g, "Remaining allowance", 13, Theme.Muted, new RectangleF(25, 56, 196, 23));
        if (!poolSelector.Visible)
        {
            Theme.RoundRect(g, Theme.Card, new RectangleF(300, 53, 116, 24), 8);
            Theme.Label(g, snapshot?.Plan ?? (failed ? "Unavailable" : "Connecting"), 11, Theme.Muted, new RectangleF(304, 57, 108, 19), FontStyle.Regular, StringAlignment.Center);
        }

        if (snapshot?.FiveHour != null && snapshot.Weekly != null)
        {
            DrawQuota(g, snapshot.FiveHour, "5-HOUR", 24, 190, now);
            DrawQuota(g, snapshot.Weekly, "WEEKLY", 226, 190, now);
        }
        else if (snapshot?.Weekly != null) DrawQuota(g, snapshot.Weekly, "WEEKLY", 24, 392, now);
        else if (snapshot?.FiveHour != null) DrawQuota(g, snapshot.FiveHour, "5-HOUR", 24, 392, now);
        else
        {
            Theme.RoundRect(g, Theme.Card, new RectangleF(24, 94, 392, 120), 12);
            Theme.Label(g, busy ? "Checking usage…" : "Usage unavailable", 23, Theme.Muted, new RectangleF(40, 125, 360, 42), FontStyle.Bold);
            Theme.Label(g, "Available limits will appear here.", 12, Theme.Muted, new RectangleF(40, 174, 360, 24));
        }
        Theme.Label(g, "Remaining over time", 14, Theme.Text, new RectangleF(24, 232, 230, 24), FontStyle.Bold);
        Theme.Label(g, QuotaPacing.Describe(snapshot, now, failed), 11, Theme.Muted, new RectangleF(24, 215, 392, 17));
        if (snapshot?.Weekly != null)
            Theme.Label(g, history.WeeklyUsageSummary(now), 10, Theme.Muted, new RectangleF(49, 253, 365, 15));
        DrawChart(g, now);

        Theme.RoundRect(g, Theme.Card, new RectangleF(24, 392, 392, 118), 12);
        Theme.Label(g, "Banked resets", 14, Theme.Text, new RectangleF(40, 404, 190, 24), FontStyle.Bold);
        var count = snapshot?.AvailableResets;
        Theme.Label(g, count.HasValue ? $"{count.Value} available" : "Unavailable", 14, count.HasValue ? Theme.Mint : Theme.Muted, new RectangleF(225, 404, 175, 24), FontStyle.Bold, StringAlignment.Far);

        var stale = snapshot != null && snapshot.IsStale(now);
        using var dot = new SolidBrush(failed || stale ? Theme.Amber : busy ? Theme.Muted : Theme.Mint);
        g.FillEllipse(dot, 25, 525, 6, 6);
        Theme.Label(g, status, 12, failed ? Theme.Amber : Theme.Muted, new RectangleF(39, 518, 377, 33));
        var timing = snapshot == null ? "" : $"Updated {snapshot.ReadAt.LocalDateTime:HH:mm}";
        if (stale) timing += " · Last known values";
        if (!busy && nextAttempt > now) timing += (timing.Length > 0 ? " · " : "") + "Next check in " + Theme.Countdown(nextAttempt, now);
        Theme.Label(g, timing, 11, Theme.Muted, new RectangleF(24, 551, 392, 20));
    }

    private void DrawQuota(Graphics g, QuotaWindow? quota, string title, float x, float width, DateTimeOffset now)
    {
        Theme.RoundRect(g, Theme.Card, new RectangleF(x, 94, width, 120), 12);
        Theme.Label(g, title, 10, Theme.Muted, new RectangleF(x + 16, 107, width - 32, 18), FontStyle.Bold);
        var outdated = quota?.ResetPending(now) == true || snapshot?.IsStale(now) == true || failed;
        var color = quota == null || outdated ? Theme.Muted : Theme.Quota(quota.Remaining);
        Theme.Label(g, Percent(quota), 34, color, new RectangleF(x + 13, 125, 164, 45), FontStyle.Bold);
        Theme.RoundRect(g, Theme.Line, new RectangleF(x + 16, 176, width - 32, 5), 2.5f);
        if (quota?.Remaining > 0)
        {
            using var progress = new SolidBrush(color);
            g.FillRectangle(progress, x + 16, 176, (float)((width - 32) * quota.Remaining / 100), 5);
        }
        var reset = quota == null ? "Not provided by Codex" : quota.ResetPending(now) ? "Reset due · awaiting refresh" : quota.ResetsAt.HasValue ? "Resets in " + Theme.Countdown(quota.ResetsAt.Value, now) : "Reset time unavailable";
        var countdown = quota?.ResetsAt.HasValue == true && !quota.ResetPending(now);
        Theme.Label(g, reset, countdown ? 15 : 11, countdown ? Theme.Text : Theme.Muted,
            new RectangleF(x + 16, 187, width - 32, 25), countdown ? FontStyle.Bold : FontStyle.Regular);
    }

    private void DrawChart(Graphics g, DateTimeOffset now)
    {
        float left = ChartBounds.Left, top = ChartBounds.Top, width = ChartBounds.Width, height = ChartBounds.Height;
        using var grid = new Pen(Theme.Line);
        foreach (var percent in new[] { 100, 50, 0 })
        {
            var y = top + (100 - percent) * height / 100;
            g.DrawLine(grid, left, y, left + width, y);
            Theme.Label(g, percent.ToString(), 9, Theme.Muted, new RectangleF(24, y - 7, 23, 17));
        }
        Theme.Label(g, ChartDays == 1 ? "24h ago" : $"{ChartDays}d ago", 9, Theme.Muted, new RectangleF(left, 365, 80, 16));
        Theme.Label(g, "Now", 9, Theme.Muted, new RectangleF(left + width - 40, 365, 40, 16), alignment: StringAlignment.Far);
        if (snapshot?.FiveHour != null)
        {
            DrawSeries(g, now, p => p.FiveHour, Theme.Mint, top, height);
            Theme.Label(g, "● 5h", 10, Theme.Mint, new RectangleF(166, 365, 48, 18));
        }
        if (snapshot?.Weekly != null)
        {
            DrawSeries(g, now, p => p.Weekly, Theme.Violet, top, height);
            Theme.Label(g, "● Weekly", 10, Theme.Violet, new RectangleF(snapshot.FiveHour == null ? 196 : 226, 365, 80, 18));
        }
        if (HoveredChartPoint == null && history.InRange(now, ChartDays).Count(p => p.FiveHour.HasValue || p.Weekly.HasValue) < 2)
        {
            Theme.RoundRect(g, Theme.Background, new RectangleF(87, 294, 280, 39), 6);
            Theme.Label(g, "More history will appear as usage is recorded", 12, Theme.Muted, new RectangleF(89, 304, 276, 24), alignment: StringAlignment.Center);
        }
        DrawChartHover(g, now);
    }

    private void DrawChartHover(Graphics g, DateTimeOffset now)
    {
        var point = HoveredChartPoint;
        if (point == null || point.Time < now.AddDays(-ChartDays) || point.Time > now) return;
        var five = snapshot?.FiveHour != null ? point.FiveHour : null;
        var weekly = snapshot?.Weekly != null ? point.Weekly : null;
        if (!five.HasValue && !weekly.HasValue) return;
        var x = ChartX(point, now);
        using var guide = new Pen(Theme.Muted, 1) { DashStyle = DashStyle.Dot };
        g.DrawLine(guide, x, ChartBounds.Top, x, ChartBounds.Bottom);
        void Marker(double? value, Color color)
        {
            if (!value.HasValue) return;
            var y = ChartBounds.Top + (float)(100 - value.Value) * ChartBounds.Height / 100;
            using var fill = new SolidBrush(color);
            using var outline = new Pen(Theme.Background, 1.5f);
            g.FillEllipse(fill, x - 4, y - 4, 8, 8);
            g.DrawEllipse(outline, x - 4, y - 4, 8, 8);
        }
        Marker(five, Theme.Mint);
        Marker(weekly, Theme.Violet);
        const float width = 178;
        var left = x + 12 + width <= ChartBounds.Right ? x + 12 : x - 12 - width;
        var top = ChartBounds.Top + 4;
        var height = five.HasValue && weekly.HasValue ? 72 : 53;
        Theme.RoundRect(g, Theme.Line, new RectangleF(left, top, width, height), 6);
        Theme.Label(g, "Recorded " + point.Time.LocalDateTime.ToString("dd MMM · HH:mm"), 11, Theme.Text, new RectangleF(left + 9, top + 7, width - 18, 17));
        var row = top + 27;
        void Value(double? value, string label, Color color)
        {
            if (!value.HasValue) return;
            Theme.Label(g, $"{label}: {value.Value:0.#}% remaining", 12, color, new RectangleF(left + 9, row, width - 18, 19));
            row += 19;
        }
        Value(five, "5h", Theme.Mint);
        Value(weekly, "Weekly", Theme.Violet);
    }

    private void DrawSeries(Graphics g, DateTimeOffset now, Func<HistoryPoint, double?> select, Color color, float top, float height)
    {
        using var pen = new Pen(color, 1.8f);
        using var brush = new SolidBrush(color);
        using var path = new GraphicsPath();
        PointF? previous = null;
        DateTimeOffset previousTime = DateTimeOffset.MinValue;
        foreach (var sample in history.InRange(now, ChartDays))
        {
            var value = select(sample);
            if (!value.HasValue) { previous = null; continue; }
            var point = new PointF(ChartX(sample, now), top + (float)(100 - value.Value) * height / 100);
            if (previous.HasValue && sample.Time - previousTime <= TimeSpan.FromMinutes(15))
            {
                // Steps represent observed values; gaps do not imply measured activity.
                var corner = new PointF(point.X, previous.Value.Y);
                path.AddLine(previous.Value, corner);
                path.AddLine(corner, point);
            }
            else { path.StartFigure(); g.FillEllipse(brush, point.X - 2, point.Y - 2, 4, 4); }
            previous = point;
            previousTime = sample.Time;
        }
        if (path.PointCount > 0) g.DrawPath(pen, path);
        if (previous.HasValue) g.FillEllipse(brush, previous.Value.X - 2.5f, previous.Value.Y - 2.5f, 5, 5);
    }

    private static string Percent(QuotaWindow? quota) => quota == null ? "—" : Math.Floor(quota.Remaining).ToString("0") + "%";
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Font.Dispose(); desktop.Font.Dispose(); close.Font.Dispose(); poolSelector.Font.Dispose();
            menuButton.Font.Dispose(); settingsButton.Font.Dispose(); hints.Dispose(); poolMenu.Dispose();
            dayRange.Font.Dispose(); weekRange.Font.Dispose(); monthRange.Font.Dispose(); resetList.Font.Dispose();
        }
        base.Dispose(disposing);
    }
}
