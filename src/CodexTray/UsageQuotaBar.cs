using System.Drawing.Drawing2D;
using CodexTray.Core;

namespace CodexTray;

internal sealed class UsageQuotaBar : Control
{
    private double? _remainingPercent;
    private double? _budgetRemainingPercent;
    private UsageBand _band;

    public UsageQuotaBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        MinimumSize = new Size(320, 14);
        Size = MinimumSize;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Margin = new Padding(0, 0, 0, 3);
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    public void UpdateWindow(LimitWindow? window, DateTimeOffset now)
    {
        var remaining = window is not null && double.IsFinite(window.UsedPercent)
                        && !(window.ResetsAt is { } reset && reset <= now)
            ? window.RemainingPercent
            : (double?)null;
        var ahead = UsageBandSelector.AheadOfPace(window, now);
        var budget = remaining is { } value && ahead is { } offset
            ? Math.Clamp(value + offset, 0d, 100d)
            : (double?)null;
        var band = UsageBandSelector.SelectWindow(window, now);
        if ((_remainingPercent, _budgetRemainingPercent, _band) == (remaining, budget, band))
        {
            return;
        }

        (_remainingPercent, _budgetRemainingPercent, _band) = (remaining, budget, band);
        AccessibleName = remaining is { } quota
            ? $"Quota remaining: {quota:0.#}%. "
              + (budget is { } expected ? $"Budget remaining: {expected:0.#}%." : "Budget unavailable.")
            : "Quota remaining unavailable.";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var stroke = 6f * scale;
        var left = stroke / 2f;
        var right = Width - left;
        var center = Height / 2f;
        if (right <= left)
        {
            return;
        }

        using var track = new Pen(Color.FromArgb(51, 65, 85), stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        e.Graphics.DrawLine(track, left, center, right, center);

        var color = _band switch
        {
            UsageBand.Green => Color.FromArgb(34, 197, 94),
            UsageBand.Amber => Color.FromArgb(245, 158, 11),
            UsageBand.Red => Color.FromArgb(239, 68, 68),
            _ => Color.FromArgb(100, 116, 139)
        };
        if (_remainingPercent is > 0)
        {
            using var fill = new Pen(color, stroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            var end = left + (right - left) * (float)(_remainingPercent.Value / 100d);
            e.Graphics.DrawLine(fill, left, center, end, center);
        }

        if (_budgetRemainingPercent is { } budget)
        {
            var x = left + (right - left) * (float)(budget / 100d);
            using var marker = new Pen(Color.FromArgb(241, 245, 249), 1.5f * scale)
            {
                DashStyle = DashStyle.Dot,
                DashCap = DashCap.Round
            };
            e.Graphics.DrawLine(marker, x, scale, x, Height - scale);
        }
    }
}
