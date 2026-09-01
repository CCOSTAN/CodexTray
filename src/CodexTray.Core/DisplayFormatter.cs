using System.Globalization;

namespace CodexTray.Core;

public static class DisplayFormatter
{
    public static string WindowLine(string label, LimitWindow? window, DateTimeOffset now)
    {
        if (window is null)
        {
            return $"{label}: unavailable";
        }

        var remaining = Math.Round(window.RemainingPercent);
        var used = Math.Round(window.UsedPercent);
        var reset = window.ResetsAt is { } resetsAt
            ? $" · resets {Countdown(resetsAt, now)}"
            : string.Empty;
        return $"{label}: {remaining:0}% left ({used:0}% used){reset}";
    }

    public static string BuildTooltip(CodexSnapshot snapshot)
    {
        var reset = snapshot.EarliestResetAt is { } resetsAt
            ? $" · resets {Countdown(resetsAt, DateTimeOffset.Now)}"
            : string.Empty;
        var tooltip = snapshot.SessionWindow is { } sessionWindow
            ? $"Codex: {Math.Round(sessionWindow.RemainingPercent):0}% left"
              + (snapshot.WeeklyWindow is { } weeklyWindow
                  ? $", week {Math.Round(weeklyWindow.RemainingPercent):0}%"
                  : string.Empty)
            : snapshot.WeeklyWindow is { } weeklyOnly
                ? $"Codex: week {Math.Round(weeklyOnly.RemainingPercent):0}% left"
                : "Codex: unavailable";
        tooltip += reset;
        return tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    public static string CompactNumber(long value)
    {
        return value switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.#}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
            >= 1_000 => $"{value / 1_000d:0.#}K",
            _ => value.ToString(CultureInfo.InvariantCulture)
        };
    }

    public static string CreditsLine(
        int? availableResetCredits,
        DateTimeOffset? earliestExpiration = null,
        DateTimeOffset? now = null)
    {
        var line = availableResetCredits is { } credits
            ? $"Reset credits: {credits}"
            : "Reset credits: not reported";
        if (availableResetCredits is > 0 && earliestExpiration is { } expiresAt)
        {
            line += $" · expires {Countdown(expiresAt, now ?? DateTimeOffset.Now)}";
        }

        return line;
    }

    public static string UsageLine(UsageSummary? usage)
    {
        if (usage is null)
        {
            return "Token usage: not reported";
        }

        var parts = new List<string>();
        if (usage.TodayTokens is { } today)
        {
            parts.Add($"today {CompactNumber(today)}");
        }
        if (usage.LifetimeTokens is { } lifetime)
        {
            parts.Add($"lifetime {CompactNumber(lifetime)}");
        }
        return parts.Count == 0 ? "Token usage: not reported" : $"Tokens: {string.Join(" · ", parts)}";
    }

    private static string Countdown(DateTimeOffset target, DateTimeOffset now)
    {
        var remaining = target - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        if (remaining.TotalDays >= 2)
        {
            return $"in {(int)remaining.TotalDays}d {remaining.Hours}h";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        return $"in {Math.Max(1, remaining.Minutes)}m";
    }
}
