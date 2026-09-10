using CodexTray.Core;

namespace CodexTray;

internal sealed class UsageHoverForm : Form
{
    private readonly LinkLabel _profile = new();
    private readonly Label _session = DetailLabel();
    private readonly Label _weekly = DetailLabel();
    private readonly Label _pace = DetailLabel();
    private readonly Label _credits = DetailLabel();
    private readonly Label _usage = DetailLabel();
    private readonly Label _updated = DetailLabel(Color.FromArgb(148, 163, 184));

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
        _profile.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Regular, GraphicsUnit.Point);
        _profile.LinkColor = Color.FromArgb(56, 189, 248);
        _profile.ActiveLinkColor = Color.FromArgb(125, 211, 252);
        _profile.VisitedLinkColor = _profile.LinkColor;
        _profile.Margin = new Padding(0, 0, 0, 7);
        _profile.Text = "Profile & usage ↗";
        _profile.LinkClicked += (_, _) => Brand.OpenUrl(Brand.CodexUsageUrl);

        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = BackColor,
            ColumnCount = 1,
            Padding = new Padding(13, 11, 18, 12)
        };
        panel.Controls.Add(_profile);
        panel.Controls.Add(_session);
        panel.Controls.Add(_weekly);
        panel.Controls.Add(_pace);
        panel.Controls.Add(_credits);
        panel.Controls.Add(_usage);
        panel.Controls.Add(_updated);
        Controls.Add(panel);
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

    public void UpdateSnapshot(CodexSnapshot snapshot)
    {
        _profile.Text = string.IsNullOrWhiteSpace(snapshot.PlanType)
            ? "Profile & usage ↗"
            : $"Profile & usage · {snapshot.PlanType} ↗";
        _session.Visible = snapshot.SessionWindow is not null;
        _session.Text = DisplayFormatter.WindowLine("Short window", snapshot.SessionWindow, DateTimeOffset.Now);
        _weekly.Text = DisplayFormatter.WindowLine("Weekly", snapshot.WeeklyWindow, DateTimeOffset.Now);
        _pace.Text = DisplayFormatter.PaceLine(snapshot.DisplayWindow, DateTimeOffset.Now);
        _credits.Text = DisplayFormatter.CreditsLine(
            snapshot.AvailableResetCredits,
            snapshot.EarliestResetCreditExpiresAt,
            DateTimeOffset.Now);
        _usage.Text = DisplayFormatter.UsageLine(snapshot.Usage);
        _updated.Text = $"Updated {snapshot.FetchedAt.LocalDateTime:t}";
    }

    public void ShowNear(Point anchor)
    {
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
