using System.Globalization;

namespace CodexTray.Core;

public static class DisplayFormatter
{
    public static string ResetCountdown(DateTimeOffset? target, DateTimeOffset now)
    {
        if (target is null)
        {
            return "Not reported";
        }

        var remaining = target.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Refresh needed";
        }

        if (remaining.TotalMinutes < 1)
        {
            return "Less than 1m";
        }

        return remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d {remaining.Hours}h {remaining.Minutes}m"
            : remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
                : $"{remaining.Minutes}m";
    }

    public static string ResetDate(DateTimeOffset? target, TimeZoneInfo? timeZone = null)
    {
        if (target is null)
        {
            return "Date not reported";
        }

        var zone = timeZone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(target.Value, zone);
        var zoneLabel = zone.Id is "Eastern Standard Time" or "America/New_York"
            ? "ET"
            : $"UTC{local:zzz}";
        return $"{local.ToString("ddd, MMM d, yyyy · h:mm tt", CultureInfo.CurrentCulture)} {zoneLabel}";
    }

    public static string PaceLine(LimitWindow? window, DateTimeOffset now)
    {
        var ahead = UsageBandSelector.AheadOfPace(window, now);
        if (ahead is null)
        {
            return "Pace: unavailable (waiting for current reset timing)";
        }

        var status = ahead >= 15d ? "too fast" : ahead > 5d ? "running ahead" : "on track";
        var expectedUsed = window!.UsedPercent - ahead.Value;
        return $"Pace: {status} ({window.UsedPercent:0.#}% used; {expectedUsed:0.#}% budgeted so far)";
    }

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
