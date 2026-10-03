using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CodexTray;

internal sealed class PopupForm : Form
{
    private RectangleF ChartBounds => snapshot?.Weekly != null ? new RectangleF(49, 300, 365, 84) : new RectangleF(49, 252, 365, 132);
    internal HistoryPoint? HoveredChartPoint { get; private set; }
    private readonly Button refresh = Theme.Button("Refresh", true);
    private readonly Button desktop = Theme.Button("Open Codex");
    private readonly Button close = Theme.Button("×");
    private readonly Button menuButton = Theme.Button("⋯");
    private readonly Button settingsButton = Theme.Button("⚙");
    private readonly Button dayRange = Theme.Button("24h");
    private readonly Button weekRange = Theme.Button("7d");
    private readonly Button monthRange = Theme.Button("30d");
    private readonly Button readings = Theme.Button("Readings…");
    private readonly ListBox resetList = new ListBox { BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Muted, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, AccessibleName = "Banked reset expiry times" };
    private readonly ToolTip hints = new ToolTip();
    private readonly Label connectionStatus = new Label { Text = "Connecting to Codex…", AutoEllipsis = true, BackColor = Theme.Background, ForeColor = Theme.Muted, AccessibleName = "Connection status" };
    private readonly Label freshness = new Label { BackColor = Theme.Background, ForeColor = Theme.Muted, AccessibleName = "Reading freshness", TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button poolSelector = Theme.Button("Usage pool ▾");
    private readonly DpiMenu poolMenu = new DpiMenu();
    private readonly UsageHistory history;
    private UsageSnapshot? snapshot;
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
        Controls.AddRange(new Control[] { refresh, desktop, close, poolSelector, menuButton, settingsButton, dayRange, weekRange, monthRange, resetList, connectionStatus, freshness, readings });
        menuButton.AccessibleName = "Open menu";
        settingsButton.AccessibleName = "Settings";
        menuButton.TabIndex = 0; settingsButton.TabIndex = 1; close.TabIndex = 2;
        poolSelector.TabIndex = 3; dayRange.TabIndex = 4; weekRange.TabIndex = 5; monthRange.TabIndex = 6;
        readings.TabIndex = 7; resetList.TabIndex = 8; refresh.TabIndex = 9; desktop.TabIndex = 10;
        readings.AccessibleName = "View recorded readings";
        readings.Click += (_, __) => ShowReadings();
        hints.SetToolTip(readings, "Inspect or export recorded allowance. No usage request is made.");
        dayRange.AccessibleName = "Past 24 hours"; weekRange.AccessibleName = "Past 7 days"; monthRange.AccessibleName = "Past 30 days";
        dayRange.Click += (_, __) => SelectRange(1);
        weekRange.Click += (_, __) => SelectRange(7);
        monthRange.Click += (_, __) => SelectRange(30);
        UpdateRangeButtons();
        hints.SetToolTip(menuButton, "Open menu");
        resetList.DrawItem += DrawResetRow;
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

    public void UpdateUsage(UsageSnapshot? value, string message, bool refreshing, bool error, DateTimeOffset next, bool canRefresh, DateTimeOffset? manualAllowedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        snapshot = value;
        var stale = value?.IsStale(now) == true;
        connectionStatus.Text = !error && !refreshing && stale ? "Readings are over 10 minutes old. Waiting for an update." : message;
        connectionStatus.ForeColor = error || stale ? Theme.Amber : Theme.Muted;
        hints.SetToolTip(connectionStatus, connectionStatus.Text);
        busy = refreshing;
        failed = error;
        poolSelector.Text = (value?.Pools.FirstOrDefault(p => p.Id == value.PoolId)?.Name ?? "Usage pool") + " ▾";
        poolSelector.Visible = value != null && value.Pools.Count > 1;
        var retry = manualAllowedAt ?? (error ? next : DateTimeOffset.MinValue);
        refresh.Text = busy ? "Refreshing…" : !canRefresh && retry > now ? (error ? "Retry in " : "Refresh in ") + Theme.Countdown(retry, now) : "Refresh";
        refresh.Enabled = canRefresh && !busy;
        var readTime = value?.ReadAt.LocalDateTime;
        var timing = readTime == null ? "" : (stale || error ? "Last read " : "Updated ")
            + readTime.Value.ToString(readTime.Value.Date == now.LocalDateTime.Date ? "HH:mm" : "d MMM yyyy, HH:mm");
        var nextCheck = !busy && next > now ? "Next check in " + Theme.Countdown(next, now) : "";
        freshness.Text = timing + (timing.Length > 0 && nextCheck.Length > 0 ? " · " : "") + nextCheck;
        AccessibleDescription = (value?.FiveHour == null ? "" : $"5-hour remaining: {Percent(value.FiveHour)}. ")
            + (value?.Weekly == null ? "" : $"Weekly remaining: {Percent(value.Weekly)}. " + history.WeeklyUsageSummary(now) + ". "
                + history.WeeklyUsageSummary(now, dailyAverage: true) + ". ") + connectionStatus.Text + ". " + freshness.Text
            + (value?.FiveHour?.ResetPending(now) == true || value?.Weekly?.ResetPending(now) == true ? ". Reset due; waiting for confirmed allowance." : "");
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

    private void ShowReadings()
    {
        var keepOpen = KeepOpen;
        KeepOpen = true;
        try
        {
            var pool = snapshot?.Pools.FirstOrDefault(p => p.Id == snapshot.PoolId);
            using var dialog = new HistoryForm(new HistoryReadings(history, snapshot?.PoolId ?? "codex", DateTimeOffset.UtcNow), pool?.Name ?? "Codex", ChartDays);
            dialog.ShowFor(this);
            SelectRange(dialog.ChartDays);
        }
        finally
        {
            KeepOpen = keepOpen;
            if (Visible) { Activate(); readings.Focus(); }
        }
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

    private void DrawResetRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= resetList.Items.Count) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Theme.Line : Theme.Card);
        e.Graphics.FillRectangle(background, e.Bounds);
        var credit = snapshot?.ResetCredits.ElementAtOrDefault(e.Index);
        var left = resetList.Items[e.Index].ToString();
        string right = "";
        var now = DateTimeOffset.UtcNow;
        if (credit != null)
        {
            left = !credit.ExpiryKnown ? "Expiry unavailable" : !credit.ExpiresAt.HasValue ? "No expiry" : credit.ExpiresAt.Value.LocalDateTime.ToString("d MMM yyyy, HH:mm");
            right = credit.ExpiryKnown && credit.ExpiresAt.HasValue ? credit.ExpiresAt <= now ? "Expired" : Theme.Countdown(credit.ExpiresAt.Value, now) : "—";
        }
        var scale = ClientSize.Width / 440f;
        var rightWidth = (int)(100 * scale);
        var leftBounds = new Rectangle(e.Bounds.Left, e.Bounds.Top, e.Bounds.Width - (credit == null ? 0 : rightWidth + (int)(12 * scale)), e.Bounds.Height);
        var rightBounds = new Rectangle(e.Bounds.Right - rightWidth, e.Bounds.Top, rightWidth, e.Bounds.Height);
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(e.Graphics, left, e.Font, leftBounds, selected ? Theme.Text : Theme.Muted, flags);
        TextRenderer.DrawText(e.Graphics, right, e.Font, rightBounds, credit?.ExpiresAt <= now ? Theme.Amber : Theme.Text, flags | TextFormatFlags.Right);
        e.DrawFocusRectangle();
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
        refresh.Bounds = Scale(new Rectangle(24, 582, 190, 30), scale);
        desktop.Bounds = Scale(new Rectangle(226, 582, 190, 30), scale);
        close.Bounds = Scale(new Rectangle(384, 56, 32, 28), scale);
        settingsButton.Bounds = Scale(new Rectangle(344, 56, 32, 28), scale);
        menuButton.Bounds = Scale(new Rectangle(304, 56, 32, 28), scale);
        poolSelector.Bounds = Scale(new Rectangle(174, 22, 242, 30), scale);
        dayRange.Bounds = Scale(new Rectangle(266, 214, 46, 26), scale);
        weekRange.Bounds = Scale(new Rectangle(316, 214, 46, 26), scale);
        monthRange.Bounds = Scale(new Rectangle(366, 214, 50, 26), scale);
        readings.Bounds = Scale(new Rectangle(166, 214, 92, 26), scale);
        resetList.Bounds = Scale(new Rectangle(40, 480, 360, 54), scale);
        Theme.SetFont(resetList, 12 * scale);
        resetList.ItemHeight = (int)(18 * scale);
        connectionStatus.Bounds = Scale(new Rectangle(39, 546, 377, 34), scale);
        Theme.SetFont(connectionStatus, 12 * scale);
        freshness.Bounds = Scale(new Rectangle(24, 615, 392, 17), scale);
        Theme.SetFont(freshness, 10 * scale);
        Theme.SetFont(poolSelector, 12 * scale);
        foreach (var button in new[] { refresh, desktop, close, menuButton, settingsButton, dayRange, weekRange, monthRange, readings })
            Theme.SetFont(button, (button == menuButton || button == settingsButton ? 20 : 13) * scale);
    }
    private static Rectangle Scale(Rectangle r, float s) => new Rectangle((int)(r.X * s), (int)(r.Y * s), (int)(r.Width * s), (int)(r.Height * s));
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
        var lastKnown = snapshot != null && (failed || snapshot.IsStale(now) || snapshot.FiveHour?.ResetPending(now) == true || snapshot.Weekly?.ResetPending(now) == true);
        Theme.Label(g, lastKnown ? "Last known allowance" : "Remaining allowance", 12, lastKnown ? Theme.Amber : Theme.Muted, new RectangleF(25, 62, 250, 23));
        if (!poolSelector.Visible)
        {
            Theme.RoundRect(g, Theme.Card, new RectangleF(300, 22, 116, 30), 8);
            Theme.Label(g, snapshot?.Plan ?? (failed ? "Unavailable" : "Connecting"), 12, Theme.Muted, new RectangleF(304, 29, 108, 19), FontStyle.Regular, StringAlignment.Center);
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
            var connecting = busy || (snapshot == null && !failed);
            Theme.RoundRect(g, Theme.Card, new RectangleF(24, 104, 392, 92), 12);
            Theme.Label(g, connecting ? "Checking usage…" : "Usage unavailable", 23, Theme.Muted, new RectangleF(40, 116, 360, 36), FontStyle.Bold);
            Theme.Label(g, connecting ? "Reading your signed-in Codex account." : snapshot != null ? "Codex did not report a supported usage limit." : "Open Codex to sign in, or use Menu → Setup.", 12, Theme.Muted, new RectangleF(40, 161, 360, 24));
        }
        Theme.Label(g, "History · % left", 14, Theme.Text, new RectangleF(24, 217, 138, 24), FontStyle.Bold);
        if (snapshot?.Weekly != null)
        {
            Theme.Label(g, "Weekly used · last 24h", 10, Theme.Muted, new RectangleF(24, 250, 190, 16));
            Theme.Label(g, "Avg. weekly used / day", 10, Theme.Muted, new RectangleF(226, 250, 190, 16), alignment: StringAlignment.Far);
            Theme.Label(g, history.WeeklyUsageSummary(now, true), 13, Theme.Text, new RectangleF(24, 268, 200, 22));
            Theme.Label(g, history.WeeklyUsageSummary(now, true, dailyAverage: true), 13, Theme.Text, new RectangleF(226, 268, 190, 22), alignment: StringAlignment.Far);
        }
        DrawChart(g, now);

        Theme.RoundRect(g, Theme.Card, new RectangleF(24, 424, 392, 118), 12);
        Theme.Label(g, "Banked resets", 14, Theme.Text, new RectangleF(40, 438, 190, 24), FontStyle.Bold);
        var count = snapshot?.AvailableResets;
        Theme.Label(g, count.HasValue ? $"{count.Value} available" : "Unavailable", 14, count.HasValue ? Theme.Mint : Theme.Muted, new RectangleF(225, 438, 175, 24), FontStyle.Bold, StringAlignment.Far);
        if (snapshot?.ResetCredits.Count > 0)
        {
            Theme.Label(g, "Expires", 10, Theme.Muted, new RectangleF(40, 463, 240, 16));
            var resetRight = 40 + resetList.ClientSize.Width / (ClientSize.Width / 440f);
            Theme.Label(g, "In", 10, Theme.Muted, new RectangleF(resetRight - 100, 463, 100, 16), alignment: StringAlignment.Far);
        }

        var stale = snapshot != null && snapshot.IsStale(now);
        using var dot = new SolidBrush(failed || stale ? Theme.Amber : busy ? Theme.Muted : Theme.Mint);
        g.FillEllipse(dot, 25, 551, 6, 6);
    }

    private void DrawQuota(Graphics g, QuotaWindow? quota, string title, float x, float width, DateTimeOffset now)
    {
        Theme.RoundRect(g, Theme.Card, new RectangleF(x, 104, width, 92), 12);
        Theme.Label(g, title, 10, Theme.Muted, new RectangleF(x + 16, 116, 80, 18), FontStyle.Bold);
        var outdated = quota?.ResetPending(now) == true || snapshot?.IsStale(now) == true || failed;
        var color = quota == null || outdated ? Theme.Muted : Theme.Quota(quota.Remaining);
        var percent = Percent(quota);
        Theme.Label(g, percent, percent.Length > 3 ? 28 : 32, color, new RectangleF(x + 13, 135, 94, 42), FontStyle.Bold);
        Theme.RoundRect(g, Theme.Line, new RectangleF(x + 16, 181, width - 32, 5), 2.5f);
        if (quota?.Remaining > 0)
        {
            using var progress = new SolidBrush(color);
            g.FillRectangle(progress, x + 16, 181, (float)((width - 32) * quota.Remaining / 100), 5);
        }
        var pending = quota?.ResetPending(now) == true;
        var reset = quota?.ResetsAt.HasValue == true ? pending ? "Pending" : Theme.Countdown(quota.ResetsAt.Value, now) : "Unavailable";
        var resetWidth = width > 190 ? 136 : 74;
        Theme.Label(g, pending ? "Reset due" : "Resets in", 10, Theme.Muted, new RectangleF(x + width - 16 - resetWidth, 116, resetWidth, 18), alignment: StringAlignment.Far);
        Theme.Label(g, reset, quota?.ResetsAt.HasValue == true ? 15 : 11, outdated ? Theme.Muted : Theme.Text,
            new RectangleF(x + width - 16 - resetWidth, 144, resetWidth, 25), FontStyle.Bold, StringAlignment.Far);
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
        Theme.Label(g, ChartDays == 1 ? "24h ago" : $"{ChartDays}d ago", 9, Theme.Muted, new RectangleF(left, 394, 80, 16));
        Theme.Label(g, "Now", 9, Theme.Muted, new RectangleF(left + width - 40, 394, 40, 16), alignment: StringAlignment.Far);
        if (snapshot?.FiveHour != null)
        {
            DrawSeries(g, now, p => p.FiveHour, Theme.Mint, top, height);
            Theme.Label(g, "● 5h", 10, Theme.Mint, new RectangleF(166, 394, 48, 18));
        }
        if (snapshot?.Weekly != null)
        {
            DrawSeries(g, now, p => p.Weekly, Theme.Violet, top, height);
            Theme.Label(g, "● Weekly", 10, Theme.Violet, new RectangleF(snapshot.FiveHour == null ? 196 : 226, 394, 80, 18));
        }
        if (HoveredChartPoint == null && history.InRange(now, ChartDays).Count(p => p.FiveHour.HasValue || p.Weekly.HasValue) < 2)
        {
            Theme.RoundRect(g, Theme.Background, new RectangleF(87, top + height / 2 - 19, 280, 39), 6);
            Theme.Label(g, "More history will appear as usage is recorded", 12, Theme.Muted, new RectangleF(89, top + height / 2 - 9, 276, 24), alignment: StringAlignment.Center);
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
        using var gapPen = new Pen(Theme.Muted, 2) { DashStyle = DashStyle.Dash };
        using var brush = new SolidBrush(color);
        using var path = new GraphicsPath();
        PointF? previous = null;
        DateTimeOffset previousTime = DateTimeOffset.MinValue;
        bool gap = false;
        foreach (var sample in history.InRange(now, ChartDays))
        {
            var value = select(sample);
            if (!value.HasValue) { gap = true; continue; }
            var point = new PointF(ChartX(sample, now), top + (float)(100 - value.Value) * height / 100);
            if (previous.HasValue && !gap && sample.Time - previousTime <= TimeSpan.FromMinutes(15))
            {
                // Colored steps represent observed values; gray lines only bridge missing history.
                var corner = new PointF(point.X, previous.Value.Y);
                path.AddLine(previous.Value, corner);
                path.AddLine(corner, point);
            }
            else
            {
                if (previous.HasValue) g.DrawLine(gapPen, previous.Value, point);
                path.StartFigure();
                g.FillEllipse(brush, point.X - 2, point.Y - 2, 4, 4);
            }
            previous = point;
            previousTime = sample.Time;
            gap = false;
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
            dayRange.Font.Dispose(); weekRange.Font.Dispose(); monthRange.Font.Dispose(); readings.Font.Dispose(); resetList.Font.Dispose();
            connectionStatus.Font.Dispose(); freshness.Font.Dispose();
        }
        base.Dispose(disposing);
    }
}
