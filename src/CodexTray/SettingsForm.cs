using System;
using System.Drawing;
using System.Windows.Forms;

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

    public string DisplayMode => rings.Checked ? "rings" : "numbers";
    public string IconVisibility => both.Checked ? "both" : rotate.Checked ? "rotate" : fiveHour.Checked ? "5h" : "weekly";
    public int RotationSeconds => (int)interval.Value;
    public bool StartWithWindows => startup.Checked;

    public SettingsForm(Settings settings, bool? startsWithWindows)
    {
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
    }

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
        Set(styleGroup, 24, 102, 392, 30, scale);
        Set(numbers, 0, 0, 185, 30, scale); Set(rings, 200, 0, 192, 30, scale);
        Set(iconsGroup, 24, 174, 392, 136, scale);
        Set(weekly, 0, 0, 392, 30, scale); Set(fiveHour, 0, 34, 392, 30, scale);
        Set(rotate, 0, 68, 392, 30, scale); Set(both, 0, 102, 392, 30, scale);
        Set(interval, 308, 323, 108, 28, scale);
        Set(startup, 24, 419, 392, 30, scale);
        Set(cancel, 24, 574, 186, 38, scale); Set(save, 222, 574, 194, 38, scale);
    }

    private static void Set(Control control, int x, int y, int width, int height, float scale)
    {
        // Panels are positioned in device pixels; their child bounds use the same scale once.
        if (!(control is Panel))
        {
            var oldFont = control.Font;
            control.Font = new Font("Segoe UI", 14 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            if (oldFont.Unit == GraphicsUnit.Pixel) oldFont.Dispose();
        }
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
        Theme.Label(g, "Tray style", 14, Theme.Text, new RectangleF(24, 78, 392, 24), FontStyle.Bold);
        Theme.Label(g, "Tray icons", 14, Theme.Text, new RectangleF(24, 150, 392, 24), FontStyle.Bold);
        Theme.Label(g, "Switch interval (seconds)", 13, Theme.Muted, new RectangleF(24, 327, 278, 25));
        Theme.Label(g, "Unavailable limits are hidden. A single icon uses the available limit. Hover it to see which limit it shows.", 12, Theme.Muted, new RectangleF(24, 366, 392, 48));
    }

    private static RadioButton Option(string text) => new RadioButton { Text = text, ForeColor = Theme.Text, BackColor = Theme.Background, AutoSize = false };
    private static Button Button(string text, bool primary)
    {
        var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Theme.Mint : Theme.Card, ForeColor = primary ? Theme.Background : Theme.Text };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var control in new Control[] { numbers, rings, weekly, fiveHour, rotate, both, interval, startup, save, cancel }) control.Font.Dispose();
        base.Dispose(disposing);
    }
}
