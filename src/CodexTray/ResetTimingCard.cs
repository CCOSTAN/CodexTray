using System.Drawing.Drawing2D;

namespace CodexTray;

internal sealed class ResetTimingCard : Control
{
    private readonly Color _borderColor;
    private readonly Font _titleFont = new("Segoe UI Semibold", 9f);
    private readonly Font _countdownFont = new("Segoe UI Semibold", 10f);
    private readonly Font _detailFont = new("Segoe UI", 9f);
    private string _title = string.Empty;
    private string _countdown = string.Empty;
    private string _subtitle = string.Empty;
    private string _date = string.Empty;

    public ResetTimingCard(Color background, Color border, Color accent, int height)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = background;
        ForeColor = accent;
        _borderColor = border;
        MinimumSize = new Size(320, height);
        Size = MinimumSize;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Margin = new Padding(0, 5, 0, 2);
        AccessibleRole = AccessibleRole.Grouping;
    }

    public void UpdateDetails(string title, string countdown, string subtitle, string date)
    {
        if ((_title, _countdown, _subtitle, _date) == (title, countdown, subtitle, date))
        {
            return;
        }

        (_title, _countdown, _subtitle, _date) = (title, countdown, subtitle, date);
        AccessibleName = string.Join(". ", new[] { title, countdown, subtitle, date }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = ScalePixels(1);
        var diameter = ScalePixels(14);
        var bounds = new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2);
        using var outline = new GraphicsPath();
        outline.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        outline.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        outline.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        outline.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        outline.CloseFigure();

        // Paint the corners with the surrounding surface, then fill the rounded card.
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(_borderColor, ScalePixels(1));
        e.Graphics.FillPath(fill, outline);
        e.Graphics.DrawPath(border, outline);

        var padding = ScalePixels(10);
        var countdownWidth = TextRenderer.MeasureText(e.Graphics, _countdown, _countdownFont).Width;
        var headerHeight = ScalePixels(24);
        var countdownBounds = new Rectangle(Width - padding - countdownWidth, ScalePixels(3), countdownWidth, headerHeight);
        var titleBounds = new Rectangle(padding, ScalePixels(3),
            Math.Max(0, countdownBounds.Left - padding - ScalePixels(10)), headerHeight);
        DrawText(e.Graphics, _title, _titleFont, titleBounds, ForeColor);
        DrawText(e.Graphics, _countdown, _countdownFont, countdownBounds, ForeColor, TextFormatFlags.Right);

        var detailColor = Color.FromArgb(211, 218, 228);
        var dateY = ScalePixels(28);
        if (!string.IsNullOrWhiteSpace(_subtitle))
        {
            DrawText(e.Graphics, _subtitle, _detailFont,
                new Rectangle(padding, dateY, Width - padding * 2, ScalePixels(20)), detailColor);
            dateY += ScalePixels(20);
        }
        DrawText(e.Graphics, _date, _detailFont,
            new Rectangle(padding, dateY, Width - padding * 2, ScalePixels(22)), detailColor);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _countdownFont.Dispose();
            _detailFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private int ScalePixels(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    private static void DrawText(Graphics graphics, string text, Font font, Rectangle bounds,
        Color color, TextFormatFlags alignment = TextFormatFlags.Left) =>
        TextRenderer.DrawText(graphics, text, font, bounds, color,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
}
