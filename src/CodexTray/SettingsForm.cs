using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;

namespace CodexTray;

internal sealed class SettingsForm : Form
{
    private readonly Panel styleGroup = new Panel();
    private readonly Panel iconsGroup = new Panel();
    private readonly RadioButton numbers = Option("Numbers");
    private readonly RadioButton rings = Option("Rings");
    private readonly RadioButton weekly = Option("One icon · weekly");
    private readonly RadioButton fiveHour = Option("One icon · 5-hour");
    private readonly RadioButton rotate = Option("One icon · switch between limits");
    private readonly RadioButton both = Option("Two icons · one for each limit");
    private readonly NumericUpDown interval = new NumericUpDown { Minimum = 5, Maximum = 300, Increment = 5, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Switch interval in seconds" };
    private readonly CheckBox startup = new CheckBox { Text = "Start with Windows", BackColor = Theme.Background, ForeColor = Theme.Text, AutoSize = false };
    private readonly Button save = Button("Save", true);
    private readonly Button cancel = Button("Cancel", false);
    private readonly Button displayTab = Button("Display", false);
    private readonly Button alertsTab = Button("Notifications", false);
    private readonly Button aboutTab = Button("About", false);
    private readonly CheckBox lowAlerts = Check("Low allowance warnings");
    private readonly CheckBox restoredAlerts = Check("Notify when allowance is available again");
    private readonly CheckBox expiryAlerts = Check("Notify 24h before a banked reset expires");
    private readonly NumericUpDown warning = PercentInput(2, 99);
    private readonly NumericUpDown critical = PercentInput(1, 98);
    private readonly Button checkUpdates = Button("Check for updates", true);
    private readonly Button releases = Button("Open Releases", false);
    private readonly Label updateStatus = new Label { ForeColor = Theme.Muted, Text = "Updates are checked only when you ask.", AutoSize = false };
    private readonly CancellationTokenSource closing = new CancellationTokenSource();
    private readonly ReleaseUpdates? updates;
    private int tab;
    public event EventHandler? ReleasesRequested;
    public bool LowQuotaAlerts => lowAlerts.Checked;
    public bool RestoredAlerts => restoredAlerts.Checked;
    public bool ExpiryAlerts => expiryAlerts.Checked;
    public int WarningPercent => (int)warning.Value;
    public int CriticalPercent => (int)critical.Value;

    public string DisplayMode => rings.Checked ? "rings" : "numbers";
    public string IconVisibility => both.Checked ? "both" : rotate.Checked ? "rotate" : fiveHour.Checked ? "5h" : "weekly";
    public int RotationSeconds => (int)interval.Value;
    public bool StartWithWindows => startup.Checked;

    public SettingsForm(Settings settings, bool? startsWithWindows, ReleaseUpdates? updates = null)
    {
        this.updates = updates;
        Text = "Codex Tray settings";
        AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        styleGroup.Controls.AddRange(new Control[] { numbers, rings });
        iconsGroup.Controls.AddRange(new Control[] { weekly, fiveHour, rotate, both });
        Controls.AddRange(new Control[] { styleGroup, iconsGroup, interval, startup, save, cancel });
        Controls.AddRange(new Control[] { displayTab, alertsTab, aboutTab, lowAlerts, restoredAlerts, expiryAlerts, warning, critical, checkUpdates, releases, updateStatus });
        displayTab.Click += (_, __) => ShowTab(0);
        alertsTab.Click += (_, __) => ShowTab(1);
        aboutTab.Click += (_, __) => ShowTab(2);
        lowAlerts.Checked = settings.LowQuotaAlerts;
        restoredAlerts.Checked = settings.RestoredAlerts;
        expiryAlerts.Checked = settings.ExpiryAlerts;
        warning.Value = settings.WarningPercent;
        critical.Maximum = warning.Value - 1;
        critical.Value = settings.CriticalPercent;
        warning.AccessibleName = "Warning percentage remaining";
        critical.AccessibleName = "Critical percentage remaining";
        warning.ValueChanged += (_, __) => critical.Maximum = warning.Value - 1;
        lowAlerts.CheckedChanged += (_, __) => warning.Enabled = critical.Enabled = lowAlerts.Checked;
        warning.Enabled = critical.Enabled = lowAlerts.Checked;
        checkUpdates.Enabled = updates != null;
        checkUpdates.Click += async (_, __) =>
        {
            if (this.updates == null) return;
            checkUpdates.Enabled = false; updateStatus.Text = "Checking GitHub…";
            try
            {
                var result = await this.updates.CheckAsync(closing.Token);
                if (!closing.IsCancellationRequested) updateStatus.Text = result;
            }
            catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
            finally { if (!closing.IsCancellationRequested) checkUpdates.Enabled = true; }
        };
        releases.Click += (_, __) => ReleasesRequested?.Invoke(this, EventArgs.Empty);
        FormClosed += (_, __) => closing.Cancel();
        numbers.Checked = settings.DisplayMode != "rings";
        rings.Checked = settings.DisplayMode == "rings";
        weekly.Checked = settings.IconVisibility == "weekly";
        fiveHour.Checked = settings.IconVisibility == "5h";
        rotate.Checked = settings.IconVisibility == "rotate";
        both.Checked = settings.IconVisibility == "both";
        interval.Value = settings.RotationSeconds;
        interval.Enabled = rotate.Checked;
        rotate.CheckedChanged += (_, __) => interval.Enabled = rotate.Checked;
        startup.Checked = startsWithWindows == true;
        startup.Enabled = startsWithWindows.HasValue;
        save.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = save;
        CancelButton = cancel;
        ClientSize = new Size(440, 636);
        ShowTab(0);
    }

    private void ShowTab(int value)
    {
        tab = value;
        foreach (var control in new Control[] { styleGroup, iconsGroup, interval, startup }) control.Visible = tab == 0;
        foreach (var control in new Control[] { lowAlerts, restoredAlerts, expiryAlerts, warning, critical }) control.Visible = tab == 1;
        foreach (var control in new Control[] { checkUpdates, releases, updateStatus }) control.Visible = tab == 2;
        foreach (var button in new[] { displayTab, alertsTab, aboutTab })
        {
            bool selected = button == (tab == 0 ? displayTab : tab == 1 ? alertsTab : aboutTab);
            button.BackColor = selected ? Theme.Mint : Theme.Card;
            button.ForeColor = selected ? Theme.Background : Theme.Text;
        }
        Invalidate();
    }

    public void ShowAbout() => ShowTab(2);

    public DialogResult ShowFor(Form owner)
    {
        TopMost = owner.TopMost;
        var center = owner.Visible ? new Point(owner.Left + owner.Width / 2, owner.Top + owner.Height / 2) : Cursor.Position;
        DpiLayout.Place(this, center, new Size(440, 636), false);
        if (owner.Visible) Bounds = owner.Bounds;
        return ShowDialog(owner);
    }

    protected override void WndProc(ref Message m)
    {
        if (!DpiLayout.HandleDpiChange(this, ref m, new Size(440, 636))) base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (save == null) return;
        float scale = ClientSize.Width / 440f;
        Set(displayTab, 24, 70, 116, 32, scale); Set(alertsTab, 148, 70, 144, 32, scale); Set(aboutTab, 300, 70, 116, 32, scale);
        Set(styleGroup, 24, 138, 392, 30, scale);
        Set(numbers, 0, 0, 185, 30, scale); Set(rings, 200, 0, 192, 30, scale);
        Set(iconsGroup, 24, 210, 392, 136, scale);
        Set(weekly, 0, 0, 392, 30, scale); Set(fiveHour, 0, 34, 392, 30, scale);
        Set(rotate, 0, 68, 392, 30, scale); Set(both, 0, 102, 392, 30, scale);
        Set(interval, 308, 359, 108, 28, scale);
        Set(startup, 24, 455, 392, 30, scale);
        Set(lowAlerts, 24, 134, 392, 30, scale);
        Set(warning, 308, 180, 108, 28, scale); Set(critical, 308, 224, 108, 28, scale);
        Set(restoredAlerts, 24, 285, 392, 30, scale); Set(expiryAlerts, 24, 336, 392, 30, scale);
        Set(checkUpdates, 24, 286, 190, 38, scale); Set(releases, 226, 286, 190, 38, scale);
        Set(updateStatus, 24, 343, 392, 70, scale);
        Set(cancel, 24, 574, 186, 38, scale); Set(save, 222, 574, 194, 38, scale);
    }

    private static void Set(Control control, int x, int y, int width, int height, float scale)
    {
        // Panels are positioned in device pixels; their child bounds use the same scale once.
        if (!(control is Panel))
            Theme.SetFont(control, 14 * scale);
        control.Bounds = new Rectangle((int)(x * scale), (int)(y * scale), (int)(width * scale), (int)(height * scale));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.ScaleTransform(ClientSize.Width / 440f, ClientSize.Height / 636f);
        using var border = new Pen(Theme.Line);
        g.DrawRectangle(border, 0, 0, 439, 635);
        Theme.Label(g, "Settings", 25, Theme.Text, new RectangleF(24, 22, 392, 38), FontStyle.Bold);
        if (tab == 0)
        {
            Theme.Label(g, "Tray style", 14, Theme.Text, new RectangleF(24, 114, 392, 24), FontStyle.Bold);
            Theme.Label(g, "Tray icons", 14, Theme.Text, new RectangleF(24, 186, 392, 24), FontStyle.Bold);
            Theme.Label(g, "Switch interval (seconds)", 13, Theme.Muted, new RectangleF(24, 363, 278, 25));
            Theme.Label(g, "Unavailable limits are hidden. A single icon uses the available limit. Hover it to see which limit it shows.", 12, Theme.Muted, new RectangleF(24, 402, 392, 48));
        }
        else if (tab == 1)
        {
            Theme.Label(g, "Warn at (% remaining)", 14, Theme.Muted, new RectangleF(24, 184, 275, 25));
            Theme.Label(g, "Warn again at (% remaining)", 14, Theme.Muted, new RectangleF(24, 228, 275, 25));
            Theme.Label(g, "All notifications are off by default. Allowance alerts follow the selected usage pool and use fresh readings. Each warning is sent once per reset window, including across restarts.", 13, Theme.Muted, new RectangleF(24, 392, 392, 100));
            Theme.Label(g, "Windows notification settings and Do not disturb may hide alerts. Reset credits can only be viewed here.", 12, Theme.Muted, new RectangleF(24, 497, 392, 53));
        }
        else
        {
            Theme.Label(g, "Codex Tray " + ReleaseUpdates.CurrentVersion, 23, Theme.Text, new RectangleF(24, 136, 392, 36), FontStyle.Bold);
            Theme.Label(g, "An independent Windows companion for Codex.\nApache-2.0 license · No telemetry", 14, Theme.Muted, new RectangleF(24, 186, 392, 60));
            Theme.Label(g, "Checking contacts GitHub for the latest release. Downloads open in your browser; the app never replaces its own executable.", 13, Theme.Muted, new RectangleF(24, 429, 392, 84));
        }
    }

    private static RadioButton Option(string text) => new RadioButton { Text = text, ForeColor = Theme.Text, BackColor = Theme.Background, AutoSize = false };
    private static CheckBox Check(string text) => new CheckBox { Text = text, ForeColor = Theme.Text, BackColor = Theme.Background, AutoSize = false };
    private static NumericUpDown PercentInput(int minimum, int maximum) => new NumericUpDown { Minimum = minimum, Maximum = maximum, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
    private static Button Button(string text, bool primary)
    {
        var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Theme.Mint : Theme.Card, ForeColor = primary ? Theme.Background : Theme.Text };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing.Cancel();
            foreach (var control in new Control[] { numbers, rings, weekly, fiveHour, rotate, both, interval, startup, save, cancel, displayTab, alertsTab, aboutTab, lowAlerts, restoredAlerts, expiryAlerts, warning, critical, checkUpdates, releases, updateStatus }) control.Font.Dispose();
            closing.Dispose();
        }
        base.Dispose(disposing);
    }
}
