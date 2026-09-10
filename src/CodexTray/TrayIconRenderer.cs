using System.Drawing.Drawing2D;
using System.Drawing.Text;
using CodexTray.Core;

namespace CodexTray;

internal static class TrayIconRenderer
{
    private const int CanvasSize = 32;
    private const float TextPadding = 1.5f;

    public static Icon Render(LimitWindow? window)
    {
        var remainingPercent = window?.RemainingPercent;
        using var bitmap = new Bitmap(CanvasSize, CanvasSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        var color = UsageBandSelector.SelectWindow(window, DateTimeOffset.Now) switch
        {
            UsageBand.Green => Color.FromArgb(34, 197, 94),
            UsageBand.Amber => Color.FromArgb(245, 158, 11),
            UsageBand.Red => Color.FromArgb(239, 68, 68),
            _ => Color.FromArgb(100, 116, 139)
        };
        var label = remainingPercent is { } value
            ? Math.Round(value).ToString("0")
            : "?";
        using var font = CreateFittedFont(graphics, label);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        var bounds = new RectangleF(TextPadding, 0, CanvasSize - (TextPadding * 2), CanvasSize);

        // Keep the bitmap transparent and use a subtle dark keyline so the colored
        // percentage remains legible on either a light or dark taskbar.
        using var keyline = new SolidBrush(Color.FromArgb(210, 15, 23, 42));
        foreach (var offset in new[] { -0.75f, 0.75f })
        {
            graphics.DrawString(label, font, keyline, Offset(bounds, offset, 0), format);
            graphics.DrawString(label, font, keyline, Offset(bounds, 0, offset), format);
        }

        using var foreground = new SolidBrush(color);
        graphics.DrawString(label, font, foreground, bounds, format);

        return IconFactory.FromBitmap(bitmap);
    }

    private static Font CreateFittedFont(Graphics graphics, string label)
    {
        const float maximumWidth = CanvasSize - (TextPadding * 2);
        const float maximumHeight = CanvasSize - 1;
        var size = label.Length switch
        {
            1 => 28f,
            2 => 23f,
            _ => 18f
        };

        while (size >= 10f)
        {
            var candidate = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = graphics.MeasureString(label, candidate);
            if (measured.Width <= maximumWidth && measured.Height <= maximumHeight)
            {
                return candidate;
            }

            candidate.Dispose();
            size -= 0.5f;
        }

        return new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private static RectangleF Offset(RectangleF bounds, float x, float y) =>
        new(bounds.X + x, bounds.Y + y, bounds.Width, bounds.Height);
}
