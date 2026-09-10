namespace CodexTray.Core;

public enum UsageBand
{
    Unknown,
    Green,
    Amber,
    Red
}

public static class UsageBandSelector
{
    public static UsageBand Select(double? remainingPercent) => remainingPercent switch
    {
        null => UsageBand.Unknown,
        > 25d => UsageBand.Green,
        > 15d => UsageBand.Amber,
        _ => UsageBand.Red
    };

    public static double? AheadOfPace(LimitWindow? window, DateTimeOffset now)
    {
        if (window is not { WindowMinutes: > 0, ResetsAt: { } reset }
            || !double.IsFinite(window.UsedPercent))
        {
            return null;
        }

        var minutesLeft = (reset - now).TotalMinutes;
        if (minutesLeft <= 0 || minutesLeft > window.WindowMinutes)
        {
            return null;
        }

        var expectedUsed = 100d * (1d - minutesLeft / window.WindowMinutes);
        return Math.Clamp(window.UsedPercent, 0d, 100d) - expectedUsed;
    }

    public static UsageBand SelectWindow(LimitWindow? window, DateTimeOffset now)
    {
        if (window is null || !double.IsFinite(window.UsedPercent))
        {
            return UsageBand.Unknown;
        }

        // An expired sample cannot describe the new quota window.
        if (window.ResetsAt is { } reset && reset <= now)
        {
            return UsageBand.Unknown;
        }

        var remainingBand = Select(window.RemainingPercent);
        var ahead = AheadOfPace(window, now);
        if (remainingBand == UsageBand.Red || ahead >= 15d)
        {
            return UsageBand.Red;
        }
        if (remainingBand == UsageBand.Amber || ahead > 5d)
        {
            return UsageBand.Amber;
        }

        // Green promises a known pace; missing timing must not imply that.
        return ahead is null ? UsageBand.Unknown : UsageBand.Green;
    }
}
