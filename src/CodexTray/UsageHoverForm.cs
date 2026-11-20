using CodexTray.Core;

namespace CodexTray;

internal sealed class UsageHoverForm : Form
{
    private readonly Button _profile = new();
    private readonly Label _session = DetailLabel();
    private readonly Label _pace = DetailLabel();
    private readonly UsageQuotaBar _quotaBar = new();
    private readonly Label _usage = DetailLabel();
    private readonly Label _spend = DetailLabel(Color.FromArgb(179, 218, 251));
    private readonly Label _spendStatus = DetailLabel(Color.FromArgb(165, 182, 202));
    private readonly ListView _spendRows = new()
    {
        View = View.Details, HeaderStyle = ColumnHeaderStyle.None, BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(30, 41, 59), ForeColor = Color.FromArgb(226, 232, 240),
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Width = 320, Margin = Padding.Empty,
        FullRowSelect = true, MultiSelect = false, ShowItemToolTips = true,
        AccessibleName = "Month-to-date API costs by model and other charges"
    };
    private readonly ResetTimingCard _nextReset = new(
        Color.FromArgb(27, 45, 48), Color.FromArgb(73, 117, 97), Color.FromArgb(179, 235, 201), 78);
    private readonly ResetTimingCard _bankedResets = new(
        Color.FromArgb(42, 36, 59), Color.FromArgb(115, 87, 145), Color.FromArgb(218, 199, 241), 78);
    private readonly Label _updated = DetailLabel(Color.FromArgb(148, 163, 184));
    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 1000 };
    private CodexSnapshot? _snapshot;
    private Point? _anchor;

    public UsageHoverForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.FromArgb(30, 41, 59);
        ForeColor = Color.FromArgb(241, 245, 249);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        Padding = new Padding(1);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Codex Tray usage";
        TopMost = true;

        _profile.AutoSize = true;
        _profile.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _profile.Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _profile.FlatStyle = FlatStyle.Flat;
        _profile.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _profile.FlatAppearance.MouseOverBackColor = Color.FromArgb(51, 65, 85);
        _profile.BackColor = Color.FromArgb(41, 55, 74);
        _profile.ForeColor = Color.FromArgb(226, 232, 240);
        _profile.Padding = new Padding(7, 3, 7, 3);
        _profile.Margin = Padding.Empty;
        _profile.Anchor = AnchorStyles.Right;
        _profile.Cursor = Cursors.Hand;
        _profile.Text = "OpenAI ↗";
        _profile.Click += (_, _) => Brand.OpenUrl(Brand.CodexUsageUrl);

        var heading = DetailLabel();
        heading.Text = "Codex usage";
        heading.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Regular, GraphicsUnit.Point);
        heading.Anchor = AnchorStyles.Left;
        heading.Margin = new Padding(0, 0, 10, 0);
        var header = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            MinimumSize = new Size(320, 0),
            Margin = new Padding(0, 0, 0, 8)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(_profile, 1, 0);
        _usage.Margin = new Padding(0, 8, 0, 4);
        _spend.Font = new Font("Segoe UI Semibold", 10f);
        _spend.Margin = new Padding(0, 8, 0, 4);
        _spendStatus.MaximumSize = new Size(320, 0);
        _spendRows.Columns.Add("Model or charge");
        _spendRows.Columns.Add("USD", 85, HorizontalAlignment.Right);
        _spendRows.SizeChanged += (_, _) => SizeSpendColumns();
        _session.Text = "Waiting for Codex usage";
        _nextReset.Visible = _bankedResets.Visible = _quotaBar.Visible = false;

        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = BackColor,
            ColumnCount = 1,
            Padding = new Padding(13, 11, 18, 12)
        };
        panel.Controls.Add(header);
        panel.Controls.Add(_session);
        panel.Controls.Add(_pace);
        panel.Controls.Add(_quotaBar);
        panel.Controls.Add(_nextReset);
        panel.Controls.Add(_bankedResets);
        panel.Controls.Add(_spend);
        panel.Controls.Add(_spendRows);
        panel.Controls.Add(_spendStatus);
        panel.Controls.Add(_usage);
        panel.Controls.Add(_updated);
        Controls.Add(panel);
        _countdownTimer.Tick += (_, _) => UpdateResetCards();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            var parameters = base.CreateParams;
            parameters.ExStyle |= wsExToolWindow;
            return parameters;
        }
    }

    public bool ContainsCursor => Bounds.Contains(Cursor.Position);

    public void UpdateSpend(string heading, MonthlySpend? report, string status)
    {
        _spend.Text = heading;
        _spendStatus.Text = status;
        _spendRows.BeginUpdate();
        try
        {
            _spendRows.Items.Clear();
            foreach (var row in report?.Rows ?? [])
            {
                var amount = MonthlySpend.Dollars(row.Amount);
                _spendRows.Items.Add(new ListViewItem([row.Name, amount]) { ToolTipText = $"{row.Name}  {amount}" });
            }
        }
        finally { _spendRows.EndUpdate(); }
        _spendRows.Visible = _spendRows.Items.Count > 0;
        if (_spendRows.Items.Count > 0)
        {
            var rowHeight = _spendRows.GetItemRect(0).Height;
            _spendRows.Height = Math.Min(6, _spendRows.Items.Count) * rowHeight + LogicalToDeviceUnits(4);
            SizeSpendColumns();
        }
        if (Visible && _anchor is { } anchor)
        {
            ShowNear(anchor);
        }
    }

    private void SizeSpendColumns()
    {
        var costWidth = LogicalToDeviceUnits(88);
        _spendRows.Columns[0].Width = Math.Max(40, _spendRows.ClientSize.Width - costWidth - 2);
        _spendRows.Columns[1].Width = costWidth;
    }

    public void UpdateSnapshot(CodexSnapshot snapshot)
    {
        _snapshot = snapshot;
        _quotaBar.Visible = _nextReset.Visible = true;
        var plan = snapshot.PlanType?.Equals("prolite", StringComparison.OrdinalIgnoreCase) == true
            ? "Pro Lite"
            : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                snapshot.PlanType?.Replace('_', ' ') ?? string.Empty);
        _profile.Text = string.IsNullOrWhiteSpace(plan) ? "OpenAI ↗" : $"OpenAI · {plan} ↗";
        _session.Visible = snapshot.WeeklyWindow is not null && snapshot.SessionWindow is not null;
        _session.Text = DisplayFormatter.WindowLine("Short window", snapshot.SessionWindow, DateTimeOffset.Now);
        _usage.Text = DisplayFormatter.UsageLine(snapshot.Usage);
        UpdateResetCards();
        _updated.Text = $"Updated {snapshot.FetchedAt.LocalDateTime:t}";
    }

    private void UpdateResetCards()
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var window = snapshot.DisplayWindow;
        _pace.Text = DisplayFormatter.PaceLine(window, now);
        _quotaBar.UpdateWindow(window, now);
        _nextReset.UpdateDetails(
            snapshot.WeeklyWindow is not null ? "NEXT WEEKLY RESET" : "NEXT RESET",
            DisplayFormatter.ResetCountdown(window?.ResetsAt, now),
            window is not null
                ? $"{Math.Round(window.RemainingPercent):0}% left · {Math.Round(window.UsedPercent):0}% used"
                : "Usage not reported",
            DisplayFormatter.ResetDate(window?.ResetsAt));

        var count = snapshot.AvailableResetCredits;
        _bankedResets.Visible = count is > 0;
        if (count is > 0)
        {
            _bankedResets.UpdateDetails($"BANKED RESETS ({count})",
                DisplayFormatter.ResetCountdown(snapshot.EarliestResetCreditExpiresAt, now),
                "Next credit expires",
                DisplayFormatter.ResetDate(snapshot.EarliestResetCreditExpiresAt));
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            UpdateResetCards();
            _countdownTimer.Start();
        }
        else
        {
            _countdownTimer.Stop();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _countdownTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    public void ShowNear(Point anchor)
    {
        _anchor = anchor;
        if (!Visible)
        {
            Show();
        }

        var workArea = Screen.FromPoint(anchor).WorkingArea;
        var x = Math.Clamp(anchor.X - Width + 18, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        var y = anchor.Y - Height - 14;
        if (y < workArea.Top)
        {
            y = Math.Min(workArea.Bottom - Height, anchor.Y + 18);
        }
        Location = new Point(x, Math.Max(workArea.Top, y));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Color.FromArgb(71, 85, 105));
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private static Label DetailLabel(Color? color = null) => new()
    {
        AutoSize = true,
        ForeColor = color ?? Color.FromArgb(226, 232, 240),
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
        Margin = new Padding(0, 0, 0, 4),
        MaximumSize = new Size(460, 0)
    };
}
