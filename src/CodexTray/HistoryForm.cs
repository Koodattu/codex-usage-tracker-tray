using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows.Forms;

namespace CodexTray;

internal sealed class HistoryForm : Form
{
    private readonly HistoryReadings readings;
    private readonly Label pool = Label(Theme.Muted);
    private readonly Label summary = Label(Theme.Text);
    private readonly Label detail = Label(Theme.Muted);
    private readonly Label note = Label(Theme.Muted);
    private readonly Label result = Label(Theme.Muted);
    private readonly Button day = Theme.Button("24h");
    private readonly Button week = Theme.Button("7d");
    private readonly Button month = Theme.Button("30d");
    private readonly Button export = Theme.Button("Export CSV…", true);
    private readonly Button back = Theme.Button("Back");
    private readonly DataGridView table = new DataGridView
    {
        ReadOnly = true, VirtualMode = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None,
        BackgroundColor = Theme.Card, GridColor = Theme.Line, EnableHeadersVisualStyles = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
        AccessibleName = "Recorded allowance remaining", StandardTab = true,
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText
    };
    private IReadOnlyList<HistoryPoint> rows = Array.Empty<HistoryPoint>();
    public int ChartDays { get; private set; }

    public HistoryForm(HistoryReadings readings, string poolName, int chartDays)
    {
        this.readings = readings;
        Text = "Recorded readings"; AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual; ShowInTaskbar = false;
        BackColor = Theme.Background; ForeColor = Theme.Text; DoubleBuffered = true;
        pool.Text = poolName; pool.AutoEllipsis = true; pool.AccessibleName = "Selected usage pool";
        result.Text = "Snapshot opened " + readings.CapturedAt.LocalDateTime.ToString("HH:mm") + ". Reopen for newer readings.";
        note.Text = "Remaining allowance, not token totals. — means not reported.\nGaps over 15 minutes are unobserved; usage may be missing.";
        table.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Card, ForeColor = Theme.Text,
            SelectionBackColor = Theme.Line, SelectionForeColor = Theme.Text };
        table.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Card, ForeColor = Theme.Muted,
            SelectionBackColor = Theme.Card, SelectionForeColor = Theme.Muted, WrapMode = DataGridViewTriState.False };
        table.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Recorded (local)", FillWeight = 52, SortMode = DataGridViewColumnSortMode.NotSortable });
        table.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "5-hour left", FillWeight = 24, SortMode = DataGridViewColumnSortMode.NotSortable });
        table.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Weekly left", FillWeight = 24, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (DataGridViewColumn column in table.Columns)
            if (column.Index > 0) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        table.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            var point = rows[e.RowIndex];
            e.Value = e.ColumnIndex == 0 ? point.Time.LocalDateTime.ToString("dd MMM yyyy HH:mm")
                : Percent(e.ColumnIndex == 1 ? point.FiveHour : point.Weekly);
        };
        table.CurrentCellChanged += (_, __) => UpdateDetail();
        table.RowHeightInfoNeeded += (_, e) => e.Height = Math.Max(2, (int)(26 * ClientSize.Width / 440f));
        day.Click += (_, __) => SelectRange(1);
        week.Click += (_, __) => SelectRange(7);
        month.Click += (_, __) => SelectRange(30);
        export.Click += (_, __) => Export();
        back.DialogResult = DialogResult.Cancel; CancelButton = back;
        day.AccessibleName = "Past 24 hours"; week.AccessibleName = "Past 7 days"; month.AccessibleName = "Past 30 days";
        day.TabIndex = 0; week.TabIndex = 1; month.TabIndex = 2; table.TabIndex = 3; export.TabIndex = 4; back.TabIndex = 5;
        Controls.AddRange(new Control[] { pool, summary, detail, note, result, day, week, month, table, export, back });
        ClientSize = new Size(440, 636);
        SelectRange(chartDays);
    }

    public DialogResult ShowFor(Form owner)
    {
        TopMost = owner.TopMost;
        var center = new Point(owner.Left + owner.Width / 2, owner.Top + owner.Height / 2);
        DpiLayout.Place(this, center, new Size(440, 636), false);
        if (owner.Visible) Bounds = owner.Bounds;
        return ShowDialog(owner);
    }

    private void SelectRange(int days)
    {
        ChartDays = days;
        var selectedTime = table.CurrentCell != null && table.CurrentCell.RowIndex < rows.Count ? rows[table.CurrentCell.RowIndex].Time : (DateTimeOffset?)null;
        rows = readings.InRange(days);
        table.RowCount = 0; table.RowCount = rows.Count;
        var selected = selectedTime.HasValue ? rows.ToList().FindIndex(p => p.Time == selectedTime.Value) : 0;
        if (rows.Count > 0) table.CurrentCell = table.Rows[Math.Max(0, selected)].Cells[0];
        table.Invalidate();
        foreach (var button in new[] { day, week, month })
        {
            var active = button == (days == 1 ? day : days == 7 ? week : month);
            button.BackColor = active ? Theme.Mint : Theme.Card;
            button.ForeColor = active ? Theme.Background : Theme.Text;
            button.AccessibleDescription = active ? "Selected range" : "";
            button.FlatAppearance.MouseOverBackColor = active ? Color.FromArgb(127, 232, 193) : Theme.Line;
        }
        summary.Text = rows.Count == 0 ? "0 recorded readings" : $"{rows.Count:N0} readings · newest first";
        export.Enabled = rows.Count > 0;
        table.Visible = rows.Count > 0;
        note.Visible = rows.Count > 0;
        UpdateDetail();
        LayoutControls();
        Invalidate();
    }

    private void UpdateDetail()
    {
        var index = table.CurrentCell?.RowIndex ?? -1;
        if (index < 0 || index >= rows.Count) { detail.Text = "History begins after a successful usage check.\nTry a longer range, or return to refresh."; return; }
        var point = rows[index];
        detail.Text = "Recorded " + point.Time.ToLocalTime().ToString("d MMM yyyy, HH:mm:ss zzz")
            + "\n5-hour: " + Percent(point.FiveHour) + " · Weekly: " + Percent(point.Weekly);
        if (index + 1 < rows.Count && point.Time - rows[index + 1].Time > TimeSpan.FromMinutes(15))
            detail.Text += " · gap before reading";
    }

    private void Export()
    {
        using var dialog = new SaveFileDialog { Title = "Export recorded allowance", Filter = "CSV file (*.csv)|*.csv", DefaultExt = "csv",
            FileName = "codex-readings-" + readings.CapturedAt.LocalDateTime.ToString("yyyy-MM-dd") + ".csv", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            readings.SaveCsv(dialog.FileName, ChartDays);
            result.ForeColor = Theme.Mint;
            result.Text = $"Exported {rows.Count:N0} readings. CSV times are UTC; blanks mean unavailable.";
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is ArgumentException)
        {
            DiagnosticLog.Current?.Write("history.export_failed", ex);
            result.ForeColor = Theme.Amber;
            result.Text = "Could not save the CSV. Choose another file or folder and try again.";
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutControls();
    }

    private void LayoutControls()
    {
        if (table == null) return;
        var scale = ClientSize.Width / 440f;
        void Place(Control c, int x, int y, int width, int height, int font = 12)
        {
            c.Bounds = new Rectangle((int)(x * scale), (int)(y * scale), (int)(width * scale), (int)(height * scale));
            Theme.SetFont(c, font * scale);
        }
        Place(pool, 24, 62, 392, 22, 13);
        Place(day, 24, 100, 68, 30, 13); Place(week, 100, 100, 68, 30, 13); Place(month, 176, 100, 68, 30, 13);
        Place(summary, 24, 143, 392, 22);
        Place(table, 24, 172, 392, 300);
        table.ColumnHeadersHeight = Math.Max(4, (int)(30 * scale));
        table.RowTemplate.Height = Math.Max(2, (int)(26 * scale));
        table.DefaultCellStyle.Padding = new Padding((int)(4 * scale), 0, (int)(4 * scale), 0);
        if (table.RowCount > 0) table.UpdateRowHeightInfo(0, true);
        Place(detail, 24, rows.Count == 0 ? 254 : 480, 392, 42);
        Place(note, 24, 524, 392, 38, 11);
        Place(result, 24, 566, 392, 34, 11);
        Place(export, 24, 602, 236, 30, 13); Place(back, 272, 602, 144, 30, 13);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.ScaleTransform(ClientSize.Width / 440f, ClientSize.Height / 636f);
        using var border = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(border, 0, 0, 439, 635);
        Theme.Label(e.Graphics, "Recorded readings", 25, Theme.Text, new RectangleF(24, 20, 392, 36), FontStyle.Bold);
        if (rows.Count == 0)
        {
            Theme.Label(e.Graphics, "No readings in this range", 19, Theme.Text, new RectangleF(24, 210, 392, 32), FontStyle.Bold);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (!DpiLayout.HandleDpiChange(this, ref m, new Size(440, 636))) base.WndProc(ref m);
    }

    private static Label Label(Color color) => new Label { ForeColor = color, BackColor = Theme.Background, UseMnemonic = false };
    private static string Percent(double? value) => value.HasValue ? value.Value.ToString("0.#") + "%" : "—";

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (Control control in Controls) control.Font.Dispose();
        base.Dispose(disposing);
    }
}
